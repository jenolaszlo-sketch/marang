#if NET10_0_OR_GREATER
using System.IO.Pipes;
using Gagamba.Execution;
using Gagamba.Runtime;

namespace Marang.Tests.LifecycleBroker;

/// <summary>
/// TEST-ONLY execution owner. Instantiate in the xUnit process and keep it
/// alive for the fixture. It owns the selected runtime, root probe run, and
/// every command requested by the probe. No process IDs or native handles are
/// inspected or returned.
/// </summary>
public sealed class LifecycleBrokerOwner : IAsyncDisposable
{
    private static readonly ExecutionRequirements Requirements = new(new[]
    {
        ExecutionRequirement.Require(ExecutionCapability.UnitTermination,
            CapabilityLevel.Partial, reason: "M4 lifecycle stop barriers"),
        ExecutionRequirement.Require(ExecutionCapability.SurvivesRootExit,
            CapabilityLevel.Partial, reason: "M4 descendants outlive the probe root"),
        ExecutionRequirement.Require(ExecutionCapability.RecursiveMembership,
            CapabilityLevel.Partial, reason: "M4 nested workload ownership"),
    });

    private readonly object _gate = new();
    private readonly ExecutionRuntime _runtime;
    private readonly CancellationTokenSource _serverStop = new();
    private readonly Dictionary<Guid, ActiveRun> _activeChildren = new();
    private readonly HashSet<Task> _handlers = new();
    private readonly Task _server;
    private Task<RunReceipt>? _rootTask;
    private CancellationTokenSource? _rootStop;
    private bool _stopping;

    private sealed record ActiveRun(CancellationTokenSource Stop, Task<RunReceipt> Task);

    public string PipeName { get; } = $"marang-lifecycle-{Guid.NewGuid():N}";
    public Task ServerCompletion => _server;

    public LifecycleBrokerOwner(ExecutionRuntimeOptions? runtimeOptions = null)
    {
        _runtime = ExecutionRuntime.Create(runtimeOptions);
        _server = AcceptLoopAsync(_serverStop.Token);
    }

    /// <summary>Run the top-level probe through the same runtime/provider
    /// boundary used for its nested command requests.</summary>
    public Task<RunReceipt> StartRootAsync(ProcessStartSpec root,
        RunOptions? options = null, CancellationToken callerCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_rootTask is not null) throw new InvalidOperationException("root was already started");
            _rootStop = new CancellationTokenSource();
            _rootTask = ExecutionOrchestrator.RunAsync(_runtime, Requirements, root,
                options ?? DefaultOptions(), callerCancellation, _rootStop.Token);
            return _rootTask;
        }
    }

    /// <summary>
    /// Stop every currently active nested command via its Gagamba explicit-stop
    /// token and await terminal receipts. Use requireActive=true at K12 so a
    /// missing active validation command fails the barrier.
    /// </summary>
    public async Task<IReadOnlyList<LifecycleBrokerRunResult>> StopAllAsync(
        bool requireActive = false, CancellationToken cancellationToken = default)
    {
        KeyValuePair<Guid, ActiveRun>[] snapshot;
        lock (_gate)
        {
            // One-way fence: this root fixture cannot admit new commands after
            // the stop barrier begins.
            _stopping = true;
            snapshot = _activeChildren.ToArray();
        }
        if (requireActive && snapshot.Length == 0)
            throw new InvalidOperationException("stop barrier had no active broker-owned command");

        foreach (var entry in snapshot) entry.Value.Stop.Cancel();
        var receipts = await Task.WhenAll(snapshot.Select(x => x.Value.Task))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        // RunAsync has reached terminal cleanup. Remove the completed handles
        // under the owner lock before permitting the caller to stop the root;
        // the outer RPC continuation may still be serializing its receipt.
        lock (_gate)
            foreach (var entry in snapshot)
                if (_activeChildren.TryGetValue(entry.Key, out var current)
                    && ReferenceEquals(current, entry.Value))
                    _activeChildren.Remove(entry.Key);
        var results = receipts.Select(receipt => ToResult(receipt)).ToArray();
        foreach (var result in results)
        {
            if (!result.Accepted || result.Cleanup != nameof(CleanupStatus.Confirmed))
                throw new InvalidOperationException(
                    $"broker command did not prove cleanup: {result.Cause}/{result.Cleanup}: {result.Failure}");
            if (requireActive && result.Cause != nameof(RunCause.ExplicitStop))
                throw new InvalidOperationException(
                    $"active-command barrier did not observe explicit stop: {result.Cause}");
        }
        return Array.AsReadOnly(results);
    }

    /// <summary>Explicitly stop the top-level probe only after nested commands
    /// have reached confirmed terminal receipts.</summary>
    public async Task<RunReceipt> StopRootAsync(CancellationToken cancellationToken = default)
    {
        Task<RunReceipt> root;
        lock (_gate)
        {
            root = _rootTask ?? throw new InvalidOperationException("root was not started");
            foreach (var finished in _activeChildren.Where(pair => pair.Value.Task.IsCompleted)
                .Select(pair => pair.Key).ToArray())
                _activeChildren.Remove(finished);
            if (_activeChildren.Count != 0)
                throw new InvalidOperationException("nested commands must reach terminal receipts before stopping root");
            _stopping = true;
            _rootStop!.Cancel();
        }
        var receipt = await root.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (receipt.Cleanup != CleanupStatus.Confirmed
            || receipt.Cause is not (RunCause.ExplicitStop or RunCause.NaturalExit or RunCause.NonzeroExit))
            throw new InvalidOperationException(
                $"root terminal state was not confirmed: {receipt.Cause}/{receipt.Cleanup}");
        return receipt;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                64 * 1024, 64 * 1024);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                Task handler = HandleConnectionAsync(pipe, cancellationToken);
                lock (_gate) _handlers.Add(handler);
                _ = handler.ContinueWith(completed =>
                {
                    lock (_gate) _handlers.Remove(completed);
                    _ = completed.Exception;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { await pipe.DisposeAsync().ConfigureAwait(false); break; }
            catch
            { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken serverToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            LifecycleBrokerRequest? request = null;
            try
            {
                request = await LifecycleBrokerProtocol.ReadAsync<LifecycleBrokerRequest>(pipe,
                    LifecycleBrokerProtocol.MaxRequestBytes, serverToken).ConfigureAwait(false);
                if (request.Operation != "Run" || request.Command is null)
                    throw new InvalidDataException("only Run RPCs with a command are accepted");
                var result = await RunCapturedAsync(request.Command, serverToken).ConfigureAwait(false);
                await LifecycleBrokerProtocol.WriteAsync(pipe,
                    new LifecycleBrokerResponse(request.RequestId, result, null),
                    LifecycleBrokerProtocol.MaxResponseBytes, serverToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (request is not null && pipe.IsConnected)
                {
                    try
                    {
                        await LifecycleBrokerProtocol.WriteAsync(pipe,
                            new LifecycleBrokerResponse(request.RequestId, null,
                                $"{ex.GetType().Name}: {ex.Message}"),
                            LifecycleBrokerProtocol.MaxResponseBytes, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { /* Connection loss is not evidence of child cleanup. */ }
                }
            }
        }
    }

    private async Task<LifecycleBrokerRunResult> RunCapturedAsync(
        LifecycleBrokerCommand command, CancellationToken serverToken)
    {
        string executable = Path.GetFileNameWithoutExtension(command.Executable);
        if (!string.Equals(executable, "dotnet", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(executable, "git", StringComparison.OrdinalIgnoreCase))
            return Refused("only dotnet and git invocations are allowed");
        if (command.DeadlineMilliseconds is < 1 or > 3_600_000
            || command.StdoutLimitBytes is < 0 or > 16 * 1024 * 1024
            || command.StderrLimitBytes is < 0 or > 16 * 1024 * 1024
            || command.TotalLimitBytes is < 0 or > 32 * 1024 * 1024
            || command.StopGraceMilliseconds is < 1 or > 60_000)
            return Refused("command budgets are outside the broker limits");

        var env = new Dictionary<string, string>(command.Environment, StringComparer.Ordinal);
        var process = ProcessStartSpec.Vector(command.Executable, command.Arguments,
            command.WorkingDirectory, env);
        CancellationTokenSource stop;
        Task<RunReceipt> runTask;
        Guid id;
        lock (_gate)
        {
            if (_stopping) return Refused("broker command admission is fenced after StopAll");
            stop = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            id = Guid.NewGuid();
            // Launch and registry insertion are atomic against StopAll's
            // fence/snapshot: RunAsync executes synchronously through launch.
            runTask = ExecutionOrchestrator.RunAsync(_runtime, Requirements, process,
                new RunOptions(TimeSpan.FromMilliseconds(command.DeadlineMilliseconds),
                    TimeSpan.FromMilliseconds(command.StopGraceMilliseconds),
                    new OutputCaptureOptions(command.StdoutLimitBytes, command.StderrLimitBytes,
                        command.TotalLimitBytes, RetainBytes: true)),
                explicitStop: stop.Token);
            _activeChildren[id] = new ActiveRun(stop, runTask);
        }
        try
        {
            var receipt = await runTask.ConfigureAwait(false);
            return ToResult(receipt, id);
        }
        finally
        {
            lock (_gate) _activeChildren.Remove(id);
            stop.Dispose();
        }
    }

    private static RunOptions DefaultOptions() => new(TimeSpan.FromMinutes(10),
        TimeSpan.FromSeconds(5), new OutputCaptureOptions(8 * 1024 * 1024,
            8 * 1024 * 1024, 16 * 1024 * 1024, RetainBytes: true));

    private static LifecycleBrokerRunResult ToResult(RunReceipt receipt, Guid? id = null)
    {
        var output = receipt.Output;
        bool terminalCause = receipt.Cause is RunCause.NaturalExit or RunCause.NonzeroExit
            or RunCause.ExplicitStop;
        bool accepted = terminalCause && receipt.Cleanup == CleanupStatus.Confirmed
            && output is { Status: OutputCaptureStatus.Complete };
        return new LifecycleBrokerRunResult(id ?? receipt.CorrelationId, accepted,
            accepted ? null : string.Join("; ", receipt.Reasons), receipt.Platform,
            receipt.Cause.ToString(), receipt.Cleanup.ToString(), receipt.RootExitCode,
            output?.Status.ToString(), output?.StdoutBytesSeen ?? 0, output?.StderrBytesSeen ?? 0,
            output?.StdoutRetained.ToArray() ?? Array.Empty<byte>(),
            output?.StderrRetained.ToArray() ?? Array.Empty<byte>(),
            receipt.Reasons.ToArray());
    }

    private static LifecycleBrokerRunResult Refused(string failure) => new(Guid.NewGuid(),
        false, failure, "unselected", "PrepareFailure", "Confirmed", null, null,
        0, 0, Array.Empty<byte>(), Array.Empty<byte>(), new[] { failure });

    public async ValueTask DisposeAsync()
    {
        ActiveRun[] children;
        Task<RunReceipt>? root;
        lock (_gate)
        {
            if (_stopping && _serverStop.IsCancellationRequested) return;
            _stopping = true;
            children = _activeChildren.Values.ToArray();
            root = _rootTask;
        }
        foreach (var child in children) child.Stop.Cancel();
        await Task.WhenAll(children.Select(x => x.Task)).ConfigureAwait(false);
        if (root is not null)
        {
            _rootStop!.Cancel();
            await root.ConfigureAwait(false);
        }
        _serverStop.Cancel();
        try { await _server.ConfigureAwait(false); } catch (OperationCanceledException) { }
        Task[] handlers;
        lock (_gate) handlers = _handlers.ToArray();
        await Task.WhenAll(handlers).ConfigureAwait(false);
        _rootStop?.Dispose();
        _serverStop.Dispose();
        await _runtime.DisposeAsync().ConfigureAwait(false);
    }
}
#endif
