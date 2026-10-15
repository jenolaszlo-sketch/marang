#if NET10_0_OR_GREATER
using System.Text.Json;
using Gagamba.Execution;
using Gagamba.Runtime;

namespace Marang.Tests.LifecycleBroker;

/// <summary>Owns one probe root and its nested brokered commands through Gagamba.</summary>
internal sealed class OwnedProbeRun : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly LifecycleBrokerOwner _owner;

    public Task<RunReceipt> RootTask { get; }

    private OwnedProbeRun(LifecycleBrokerOwner owner, Task<RunReceipt> rootTask)
    {
        _owner = owner;
        RootTask = rootTask;
    }

    public static OwnedProbeRun Start(string probe, string root, string directory, string? barrier,
        bool resume, string phase = "multi-effect", int reconcileRun = 0)
    {
        var cgroup = Environment.GetEnvironmentVariable("GAGAMBA_LINUX_CGROUP");
        var owner = new LifecycleBrokerOwner(new ExecutionRuntimeOptions { LinuxCgroupParent = cgroup });
        try
        {
            var environment = new Dictionary<string, string>(LifecycleBrokerCommand.CaptureExecutionEnvironment(),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            {
                ["MARANG_LIFECYCLE_PIPE"] = owner.PipeName,
                ["MSBUILDDISABLENODEREUSE"] = "1",
                ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1"
            };
            if (!string.IsNullOrWhiteSpace(cgroup)) environment["GAGAMBA_LINUX_CGROUP"] = cgroup;

            var arguments = new List<string> { probe, "--phase", phase, "--dir", directory };
            if (barrier is not null) { arguments.Add("--barrier"); arguments.Add(barrier); }
            if (resume) { arguments.Add("--resume"); arguments.Add("true"); }
            if (reconcileRun > 0) { arguments.Add("--reconcile-run"); arguments.Add(reconcileRun.ToString()); }
            var start = ProcessStartSpec.Vector(FindDotnet(environment), arguments, root, environment);
            var options = new RunOptions(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(5),
                new OutputCaptureOptions(8 * 1024 * 1024, 8 * 1024 * 1024, 16 * 1024 * 1024, RetainBytes: true));
            return new OwnedProbeRun(owner, owner.StartRootAsync(start, options));
        }
        catch
        {
            owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public async Task<RunReceipt> WaitForNaturalExitAsync(TimeSpan timeout, string directory,
        CancellationToken cancellationToken = default)
    {
        RunReceipt receipt;
        try { receipt = await RootTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            var commands = await _owner.StopAllAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
            var root = await _owner.StopRootAsync(CancellationToken.None).ConfigureAwait(false);
            await WriteStopEvidenceAsync(directory, "timeout", null, commands, root).ConfigureAwait(false);
            throw new TimeoutException("Probe root exceeded " + timeout + ". " + Describe(root));
        }

        if (receipt.Cause != RunCause.NaturalExit || receipt.RootExitCode != 0
            || receipt.Cleanup != CleanupStatus.Confirmed
            || receipt.Output is not { Status: OutputCaptureStatus.Complete })
        {
            var commands = await _owner.StopAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await WriteStopEvidenceAsync(directory, "natural-run-failure", null, commands, receipt).ConfigureAwait(false);
            throw new InvalidOperationException("Probe root did not complete naturally with clean capture: " + Describe(receipt));
        }

        await _owner.StopAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    public async Task StopAtBarrierAsync(string directory, string barrier, int originalProcessId,
        bool requireActive, CancellationToken cancellationToken = default)
    {
        if (RootTask.IsCompleted)
            throw new InvalidOperationException("Probe root completed before barrier " + barrier + ". " + Describe(await RootTask));
        var commandReceipts = await _owner.StopAllAsync(requireActive, cancellationToken).ConfigureAwait(false);
        var rootReceipt = await _owner.StopRootAsync(cancellationToken).ConfigureAwait(false);
        if (rootReceipt.Cleanup != CleanupStatus.Confirmed || rootReceipt.Cause != RunCause.ExplicitStop
            || rootReceipt.Output is not { Status: OutputCaptureStatus.Complete })
            throw new InvalidOperationException("Probe root stop was not confirmed at " + barrier + ". " + Describe(rootReceipt));
        await WriteStopEvidenceAsync(directory, barrier, originalProcessId, commandReceipts, rootReceipt).ConfigureAwait(false);
    }

    public async Task StopAfterFailureAsync(string directory, string label)
    {
        var commandReceipts = await _owner.StopAllAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
        var rootReceipt = await _owner.StopRootAsync(CancellationToken.None).ConfigureAwait(false);
        await WriteStopEvidenceAsync(directory, label, null, commandReceipts, rootReceipt).ConfigureAwait(false);
    }

    private static async Task WriteStopEvidenceAsync(string directory, string barrier, int? originalProcessId,
        IReadOnlyList<LifecycleBrokerRunResult> commands, RunReceipt root)
    {
        var evidence = new
        {
            Barrier = barrier,
            OriginalWorkflowProcessId = originalProcessId,
            BrokerCommands = commands.Select(command => new
            {
                command.BrokerRunId,
                command.Accepted,
                command.Failure,
                command.Platform,
                command.Cause,
                command.Cleanup,
                command.ExitCode,
                command.CaptureStatus,
                command.StdoutBytesSeen,
                command.StderrBytesSeen,
                Stdout = command.StandardOutputText,
                Stderr = command.StandardErrorText,
                command.Reasons
            }).ToArray(),
            Root = new
            {
                root.CorrelationId,
                root.Platform,
                Cause = root.Cause.ToString(),
                root.RootExitCode,
                Cleanup = root.Cleanup.ToString(),
                root.StopRequestAccepted,
                CaptureStatus = root.Output?.Status.ToString(),
                StdoutBytesSeen = root.Output?.StdoutBytesSeen ?? 0,
                StderrBytesSeen = root.Output?.StderrBytesSeen ?? 0,
                Stdout = root.Output is null ? "" : System.Text.Encoding.UTF8.GetString(root.Output.StdoutRetained.Span),
                Stderr = root.Output is null ? "" : System.Text.Encoding.UTF8.GetString(root.Output.StderrRetained.Span),
                root.Reasons
            }
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "lifecycle-stop-" + barrier + ".json"),
            JsonSerializer.Serialize(evidence, JsonOptions)).ConfigureAwait(false);
    }

    public static string Describe(RunReceipt receipt) =>
        $"cause={receipt.Cause}; exit={receipt.RootExitCode?.ToString() ?? "null"}; cleanup={receipt.Cleanup}; " +
        $"capture={receipt.Output?.Status.ToString() ?? "none"}; stdout={Output(receipt.Output?.StdoutRetained)}; " +
        $"stderr={Output(receipt.Output?.StderrRetained)}; reasons={string.Join(" | ", receipt.Reasons)}";

    private static string Output(ReadOnlyMemory<byte>? bytes) => bytes is null
        ? "" : System.Text.Encoding.UTF8.GetString(bytes.Value.Span);

    private static string FindDotnet(IReadOnlyDictionary<string, string> environment)
    {
        if (!environment.TryGetValue("PATH", out var path) || string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("PATH is required to resolve the dotnet executable.");
        var names = OperatingSystem.IsWindows() ? new[] { "dotnet.exe", "dotnet" } : new[] { "dotnet" };
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var name in names)
        {
            var candidate = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException("Could not resolve dotnet from the captured PATH.");
    }

    public ValueTask DisposeAsync() => _owner.DisposeAsync();
}
#endif
