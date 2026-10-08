using System.Text.Json;
using FluentAssertions;
using Marang.Http;
using Penghou.Qingniao;

namespace Marang.Tests;

/// <summary>
/// End-to-end cancel behavior over the HTTP adapter surface: a queued
/// delegation cancels immediately without provider work, a running one
/// cancels through the existing fenced provider path to a terminal
/// Cancelled state, and terminal, repeated, or foreign cancellations all fail
/// closed without duplicate provider calls.
/// </summary>
public sealed class MarangCancelHttpTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private const string Caller = "operator-1";

    [Fact]
    public async Task Queued_delegation_cancels_immediately_without_provider_work()
    {
        var provider = new CancellableProvider();
        var (runtime, catalog) = CreateRuntime(provider);
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope(Caller), CreateRequest("cancel-queued"), ct);

        var (status, body) = await SupervisionHttpEndpoints.PostCancelAsync(
            runtime, catalog, Caller, handle.DelegationId.Value, ct);

        status.Should().Be(200);
        Str(Json(body), "state").Should().Be("Cancelled");
        provider.StartCalls.Should().Be(0);
        provider.CancelCalls.Should().Be(0);
        (await runtime.GetAsync(handle.DelegationId, ct)).Progress.State
            .Should().Be(DelegationState.Cancelled);
    }

    [Fact]
    public async Task Running_delegation_cancels_to_terminal_through_the_provider_path()
    {
        var provider = new CancellableProvider();
        var (runtime, catalog) = CreateRuntime(provider);
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope(Caller), CreateRequest("cancel-running"), ct);
        await PumpToRunningAsync(runtime, handle.DelegationId, ct);

        var (status, _) = await SupervisionHttpEndpoints.PostCancelAsync(
            runtime, catalog, Caller, handle.DelegationId.Value, ct);

        status.Should().Be(200);
        provider.CancelCalls.Should().Be(1);

        var terminal = await PumpToTerminalAsync(runtime, handle.DelegationId, ct);
        terminal.Progress.State.Should().Be(DelegationState.Cancelled);
        provider.CancelCalls.Should().Be(1);
    }

    [Fact]
    public async Task Repeated_and_terminal_cancels_are_idempotent_successes()
    {
        var provider = new CancellableProvider();
        var (runtime, catalog) = CreateRuntime(provider);
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope(Caller), CreateRequest("cancel-repeat"), ct);

        var (firstStatus, firstBody) = await SupervisionHttpEndpoints.PostCancelAsync(
            runtime, catalog, Caller, handle.DelegationId.Value, ct);
        var (secondStatus, secondBody) = await SupervisionHttpEndpoints.PostCancelAsync(
            runtime, catalog, Caller, handle.DelegationId.Value, ct);

        firstStatus.Should().Be(200);
        secondStatus.Should().Be(200);
        Str(Json(secondBody), "state").Should().Be(Str(Json(firstBody), "state"));
        provider.CancelCalls.Should().Be(0);
        provider.StartCalls.Should().Be(0);
    }

    [Fact]
    public async Task Foreign_caller_and_unknown_delegation_are_denied_without_effect()
    {
        var provider = new CancellableProvider();
        var (runtime, catalog) = CreateRuntime(provider);
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope(Caller), CreateRequest("cancel-foreign"), ct);

        (await SupervisionHttpEndpoints.PostCancelAsync(
            runtime, catalog, "someone-else", handle.DelegationId.Value, ct)).StatusCode.Should().Be(404);
        (await SupervisionHttpEndpoints.PostCancelAsync(
            runtime, catalog, Caller, Guid.NewGuid(), ct)).StatusCode.Should().Be(404);

        provider.CancelCalls.Should().Be(0);
        (await runtime.GetAsync(handle.DelegationId, ct)).Progress.State
            .Should().Be(DelegationState.Queued);
    }

    private static JsonElement Json(object? body) =>
        JsonDocument.Parse(JsonSerializer.Serialize(body)).RootElement;

    private static string Str(JsonElement element, string name) =>
        element.GetProperty(name).GetString()!;

    private static async Task PumpToRunningAsync(
        DelegationRuntime runtime, DelegationId id, CancellationToken ct)
    {
        var snapshot = await runtime.GetAsync(id, ct);
        for (var index = 0; index < 10; index++)
        {
            snapshot = await runtime.PumpAsync(id, snapshot.Progress.Revision, ct);
            if (snapshot.Progress.State == DelegationState.Running)
            {
                return;
            }
        }

        throw new InvalidOperationException("The delegation never reached Running.");
    }

    private static async Task<DelegationExecutionSnapshot> PumpToTerminalAsync(
        DelegationRuntime runtime, DelegationId id, CancellationToken ct)
    {
        var snapshot = await runtime.GetAsync(id, ct);
        for (var index = 0; index < 10 && !DelegationLifecycle.IsTerminal(snapshot.Progress.State); index++)
        {
            snapshot = await runtime.PumpAsync(id, snapshot.Progress.Revision, ct);
        }

        DelegationLifecycle.IsTerminal(snapshot.Progress.State).Should().BeTrue();
        return snapshot;
    }

    private static DelegationRequest CreateRequest(string requestKey) => new(
        requestKey,
        "Do the delegated work",
        "cancel-provider",
        new WorkspaceReference("local", "workspace", "revision"),
        ["Done"],
        [],
        new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2));

    private static (DelegationRuntime Runtime, MarangDelegationCatalog Catalog) CreateRuntime(
        IExternalOperationProvider provider)
    {
        var catalog = new MarangDelegationCatalog();
        var descriptor = new ProviderDescriptor("cancel-provider", [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var adapters = new InMemoryExternalOperationProviderCatalog();
        adapters.Register(descriptor, provider);

        var runtime = new DelegationRuntime(
            catalog, null, providers, adapters, now: () => Start);
        return (runtime, catalog);
    }

    private sealed class CancellableProvider : IExternalOperationProvider
    {
        public int StartCalls { get; private set; }
        public int CancelCalls { get; private set; }
        private bool _cancelled;

        public async ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            StartCalls++;
            var handle = Handle(request, "cancel-handle");
            await handleSink.CaptureAsync(
                new ExternalOperationHandleCapture(handle, Start.AddMinutes(1)), cancellationToken);
            return new ExternalOperationStartReceipt(
                request.Identity, handle, ExternalOperationStartDisposition.Created, ExternalOperationState.Running, Start.AddMinutes(1));
        }

        public ValueTask<ExternalOperationObservation> ObserveAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default)
        {
            if (_cancelled)
            {
                return ValueTask.FromResult(new ExternalOperationObservation(
                    operationHandle, 2, ExternalOperationState.Cancelled, Start.AddMinutes(3)));
            }

            return ValueTask.FromResult(new ExternalOperationObservation(
                operationHandle, 1, ExternalOperationState.Running, Start.AddMinutes(2)));
        }

        public ValueTask<ExternalOperationResult> GetResultAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
            ExternalOperationCancelRequest request,
            CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            _cancelled = true;
            return ValueTask.FromResult(new ExternalOperationCancellationReceipt(
                request.Handle, request.CancellationKey, ExternalOperationCancellationDisposition.Requested,
                ExternalOperationState.CancellationRequested, Start.AddMinutes(3)));
        }

        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
            ExternalOperationResumeRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private static ExternalOperationHandle Handle(ExternalOperationStartRequest request, string value) => new(
            request.Correlation.Agent.Provider, value, request.Correlation.Agent.ProtocolVersion,
            new ExternalOperationCorrelation(request.Correlation.DelegationId, request.Correlation.WorkflowRun,
                request.Correlation.StructuralNode, request.Correlation.NodeGeneration,
                request.Correlation.ExecutionAttemptId, request.Correlation.Agent,
                new ExternalTaskReference(request.Correlation.Agent.Provider, value)));
    }
}
