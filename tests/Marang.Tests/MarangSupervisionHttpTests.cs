using System.Text.Json;
using FluentAssertions;
using Marang.Http;
using Penghou.Qingniao;

namespace Marang.Tests;

/// <summary>
/// End-to-end supervisor-attention behavior over the HTTP adapter surface:
/// a delegation enters WaitingForSupervisor, the detail reports an actionable
/// waiting summary, approve/resume runs the existing fenced intervention back
/// to Running and on to Completed. Negatives prove stale fences, finished
/// delegations, and foreign callers all fail closed without duplicate work.
/// </summary>
public sealed class MarangSupervisionHttpTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private const string Caller = "operator-1";

    [Fact]
    public async Task Waiting_delegation_reports_actionable_checkpoint_and_approves_to_completion()
    {
        var provider = new WaitingProvider();
        var (runtime, catalog) = CreateRuntime(provider);
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope(Caller), CreateRequest("waiting-happy"), ct);
        await PumpToWaitingAsync(runtime, handle.DelegationId, ct);

        var (detailStatus, detailBody) = await SupervisionHttpEndpoints.GetDetailAsync(
            runtime, catalog, Caller, handle.DelegationId.Value, ct);
        detailStatus.Should().Be(200);
        var waiting = Json(detailBody).GetProperty("waiting");
        Str(waiting, "checkpointId").Should().NotBeNullOrEmpty();
        Flag(waiting, "canIntervene").Should().BeTrue();
        Str(waiting, "reason").Should().Be("Needs your approval");
        Str(waiting, "requestedAction").Should().Be("Continue this step?");
        var checkpointId = Str(waiting, "checkpointId");
        var expectedRevision = Num(waiting, "expectedRevision");

        var (waitingStatus, waitingBody) = await SupervisionHttpEndpoints.GetWaitingAsync(
            runtime, catalog, Caller, handle.DelegationId.Value, ct);
        waitingStatus.Should().Be(200);
        Str(Json(waitingBody), "checkpointId").Should().Be(checkpointId);

        var (approveStatus, approveBody) = await SupervisionHttpEndpoints.PostInterventionAsync(
            runtime, catalog, Caller, handle.DelegationId.Value,
            new InterventionHttpRequest("approve", checkpointId, expectedRevision, null, null), ct);
        approveStatus.Should().Be(200);
        Str(Json(approveBody), "state").Should().Be("Running");
        provider.ResumeCalls.Should().Be(1);

        var terminal = await PumpToTerminalAsync(runtime, handle.DelegationId, ct);
        terminal.Progress.State.Should().Be(DelegationState.Completed);
        provider.ResumeCalls.Should().Be(1);
    }

    [Fact]
    public async Task Stale_fence_is_rejected_without_duplicate_resume()
    {
        var provider = new WaitingProvider();
        var (runtime, catalog) = CreateRuntime(provider);
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope(Caller), CreateRequest("waiting-stale"), ct);
        await PumpToWaitingAsync(runtime, handle.DelegationId, ct);
        var (_, detailBody) = await SupervisionHttpEndpoints.GetDetailAsync(
            runtime, catalog, Caller, handle.DelegationId.Value, ct);
        var checkpointId = Str(Json(detailBody).GetProperty("waiting"), "checkpointId");

        var (status, body) = await SupervisionHttpEndpoints.PostInterventionAsync(
            runtime, catalog, Caller, handle.DelegationId.Value,
            new InterventionHttpRequest("approve", checkpointId, 1, null, null), ct);

        status.Should().Be(409);
        Str(Json(body), "state").Should().Be("WaitingForSupervisor");
        provider.ResumeCalls.Should().Be(0);

        var current = await runtime.GetAsync(handle.DelegationId, ct);
        current.Progress.State.Should().Be(DelegationState.WaitingForSupervisor);
    }

    [Fact]
    public async Task Intervention_on_finished_delegation_reports_current_state()
    {
        var provider = new WaitingProvider();
        var (runtime, catalog) = CreateRuntime(provider);
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope(Caller), CreateRequest("waiting-finished"), ct);
        await PumpToWaitingAsync(runtime, handle.DelegationId, ct);
        var (_, detailBody) = await SupervisionHttpEndpoints.GetDetailAsync(
            runtime, catalog, Caller, handle.DelegationId.Value, ct);
        var waiting = Json(detailBody).GetProperty("waiting");
        var checkpointId = Str(waiting, "checkpointId");
        var expectedRevision = Num(waiting, "expectedRevision");

        var (approveStatus, _) = await SupervisionHttpEndpoints.PostInterventionAsync(
            runtime, catalog, Caller, handle.DelegationId.Value,
            new InterventionHttpRequest("approve", checkpointId, expectedRevision, null, null), ct);
        approveStatus.Should().Be(200);
        await PumpToTerminalAsync(runtime, handle.DelegationId, ct);

        var (status, body) = await SupervisionHttpEndpoints.PostInterventionAsync(
            runtime, catalog, Caller, handle.DelegationId.Value,
            new InterventionHttpRequest("approve", checkpointId, expectedRevision, null, null), ct);

        status.Should().Be(409);
        Str(Json(body), "state").Should().Be("Completed");
        provider.ResumeCalls.Should().Be(1);
    }

    [Fact]
    public async Task Foreign_caller_gets_denial_without_intervention()
    {
        var provider = new WaitingProvider();
        var (runtime, catalog) = CreateRuntime(provider);
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope(Caller), CreateRequest("waiting-foreign"), ct);
        await PumpToWaitingAsync(runtime, handle.DelegationId, ct);

        (await SupervisionHttpEndpoints.GetDetailAsync(
            runtime, catalog, "someone-else", handle.DelegationId.Value, ct)).StatusCode.Should().Be(404);
        (await SupervisionHttpEndpoints.GetWaitingAsync(
            runtime, catalog, "someone-else", handle.DelegationId.Value, ct)).StatusCode.Should().Be(404);
        (await SupervisionHttpEndpoints.PostInterventionAsync(
            runtime, catalog, "someone-else", handle.DelegationId.Value,
            new InterventionHttpRequest("approve", Guid.NewGuid().ToString("D"), 7, null, null), ct))
            .StatusCode.Should().Be(404);

        provider.ResumeCalls.Should().Be(0);
        (await runtime.GetAsync(handle.DelegationId, ct)).Progress.State
            .Should().Be(DelegationState.WaitingForSupervisor);
    }

    [Fact]
    public async Task Non_approve_action_is_rejected_before_touching_state()
    {
        var provider = new WaitingProvider();
        var (runtime, catalog) = CreateRuntime(provider);
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope(Caller), CreateRequest("waiting-reject"), ct);
        await PumpToWaitingAsync(runtime, handle.DelegationId, ct);

        var (status, _) = await SupervisionHttpEndpoints.PostInterventionAsync(
            runtime, catalog, Caller, handle.DelegationId.Value,
            new InterventionHttpRequest("reject", Guid.NewGuid().ToString("D"), 7, "no", null), ct);

        status.Should().Be(400);
        provider.ResumeCalls.Should().Be(0);
        (await runtime.GetAsync(handle.DelegationId, ct)).Progress.State
            .Should().Be(DelegationState.WaitingForSupervisor);
    }

    private static JsonElement Json(object? body) =>
        JsonDocument.Parse(JsonSerializer.Serialize(body)).RootElement;

    private static string Str(JsonElement element, string name) =>
        element.GetProperty(name).GetString()!;

    private static bool Flag(JsonElement element, string name) =>
        element.GetProperty(name).GetBoolean();

    private static long Num(JsonElement element, string name) =>
        element.GetProperty(name).GetInt64();

    private static async Task PumpToWaitingAsync(
        DelegationRuntime runtime, DelegationId id, CancellationToken ct)
    {
        var snapshot = await runtime.GetAsync(id, ct);
        for (var index = 0; index < 10; index++)
        {
            snapshot = await runtime.PumpAsync(id, snapshot.Progress.Revision, ct);
            if (snapshot.Progress.State == DelegationState.WaitingForSupervisor)
            {
                return;
            }
        }

        throw new InvalidOperationException("The delegation never reached WaitingForSupervisor.");
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
        "waiting-provider",
        new WorkspaceReference("local", "workspace", "revision"),
        ["Done"],
        [],
        new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2));

    private static (DelegationRuntime Runtime, MarangDelegationCatalog Catalog) CreateRuntime(
        IExternalOperationProvider provider)
    {
        var catalog = new MarangDelegationCatalog();
        var descriptor = new ProviderDescriptor("waiting-provider", [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var adapters = new InMemoryExternalOperationProviderCatalog();
        adapters.Register(descriptor, provider);

        var runtime = new DelegationRuntime(
            catalog, null, providers, adapters, now: () => Start);
        return (runtime, catalog);
    }

    private sealed class WaitingProvider : IExternalOperationProvider
    {
        public int ResumeCalls { get; private set; }
        private bool _resumed;

        public async ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            var handle = Handle(request, "waiting-handle");
            await handleSink.CaptureAsync(
                new ExternalOperationHandleCapture(handle, Start.AddMinutes(1)), cancellationToken);
            return new ExternalOperationStartReceipt(
                request.Identity, handle, ExternalOperationStartDisposition.Created, ExternalOperationState.Running, Start.AddMinutes(1));
        }

        public ValueTask<ExternalOperationObservation> ObserveAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default)
        {
            if (!_resumed)
            {
                return ValueTask.FromResult(new ExternalOperationObservation(
                    operationHandle, 1, ExternalOperationState.Waiting, Start.AddMinutes(2), providerStatus: "needs approval"));
            }

            return ValueTask.FromResult(new ExternalOperationObservation(
                operationHandle, 2, ExternalOperationState.Succeeded, Start.AddMinutes(3), resultAvailable: true));
        }

        public ValueTask<ExternalOperationResult> GetResultAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationResult(
                operationHandle, ExternalOperationState.Succeeded, Start.AddMinutes(4), "done", []));

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
            ExternalOperationCancelRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
            ExternalOperationResumeRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            ResumeCalls++;
            _resumed = true;
            return ValueTask.FromResult(new ExternalOperationResumeReceipt(
                request.Handle, request.ResumeKey, request.Handle, ExternalOperationStartDisposition.Existing, ExternalOperationState.Running, Start.AddMinutes(3)));
        }

        private static ExternalOperationHandle Handle(ExternalOperationStartRequest request, string value) => new(
            request.Correlation.Agent.Provider, value, request.Correlation.Agent.ProtocolVersion,
            new ExternalOperationCorrelation(request.Correlation.DelegationId, request.Correlation.WorkflowRun,
                request.Correlation.StructuralNode, request.Correlation.NodeGeneration,
                request.Correlation.ExecutionAttemptId, request.Correlation.Agent,
                new ExternalTaskReference(request.Correlation.Agent.Provider, value)));
    }
}
