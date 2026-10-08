using System.Text.Json;
using FluentAssertions;
using Marang.Http;
using Penghou.Qingniao;

namespace Marang.Tests;

/// <summary>
/// Failure-detail behavior over the HTTP adapter surface: a failed delegation
/// reports its terminal state, summary, and structured unresolved concerns
/// (never synthesized by the HTTP layer); non-failed details stay stable with
/// an empty list; foreign callers are denied without disclosure.
/// </summary>
public sealed class MarangFailureHttpTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private const string Caller = "operator-1";

    [Fact]
    public async Task Failed_delegation_reports_state_summary_and_structured_concerns()
    {
        var (runtime, catalog) = CreateRuntime(new FailingProvider());
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope(Caller), CreateRequest("failure-detail"), ct);
        var terminal = await PumpToTerminalAsync(runtime, handle.DelegationId, ct);
        terminal.Progress.State.Should().Be(DelegationState.Failed);

        var (status, body) = await SupervisionHttpEndpoints.GetDetailAsync(
            runtime, catalog, Caller, handle.DelegationId.Value, ct);

        status.Should().Be(200);
        var detail = Json(body);
        Str(detail, "state").Should().Be("Failed");
        Str(detail, "resultSummary").Should().NotBeNullOrEmpty();
        var concerns = detail.GetProperty("unresolvedConcerns");
        concerns.ValueKind.Should().Be(JsonValueKind.Array);
        concerns.GetArrayLength().Should().BePositive();
        foreach (var concern in concerns.EnumerateArray())
        {
            concern.GetString().Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task Non_failed_details_carry_an_empty_concern_list_with_stable_fields()
    {
        var (runtime, catalog) = CreateRuntime(new SucceedingProvider());
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope(Caller), CreateRequest("failure-stable"), ct);
        var terminal = await PumpToTerminalAsync(runtime, handle.DelegationId, ct);
        terminal.Progress.State.Should().Be(DelegationState.Completed);

        var (status, body) = await SupervisionHttpEndpoints.GetDetailAsync(
            runtime, catalog, Caller, handle.DelegationId.Value, ct);

        status.Should().Be(200);
        var detail = Json(body);
        Str(detail, "state").Should().Be("Completed");
        Str(detail, "resultSummary").Should().NotBeNullOrEmpty();
        detail.GetProperty("unresolvedConcerns").GetArrayLength().Should().Be(0);
        detail.GetProperty("workerCalls").GetInt32().Should().BePositive();
        detail.TryGetProperty("waiting", out _).Should().BeTrue();
        detail.TryGetProperty("canCancel", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Foreign_caller_cannot_read_a_failed_delegation()
    {
        var (runtime, catalog) = CreateRuntime(new FailingProvider());
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope(Caller), CreateRequest("failure-foreign"), ct);
        await PumpToTerminalAsync(runtime, handle.DelegationId, ct);

        (await SupervisionHttpEndpoints.GetDetailAsync(
            runtime, catalog, "someone-else", handle.DelegationId.Value, ct)).StatusCode.Should().Be(404);
    }

    private static JsonElement Json(object? body) =>
        JsonDocument.Parse(JsonSerializer.Serialize(body)).RootElement;

    private static string Str(JsonElement element, string name) =>
        element.GetProperty(name).GetString()!;

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
        "failure-provider",
        new WorkspaceReference("local", "workspace", "revision"),
        ["Done"],
        [],
        new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2));

    private static (DelegationRuntime Runtime, MarangDelegationCatalog Catalog) CreateRuntime(
        IExternalOperationProvider provider)
    {
        var catalog = new MarangDelegationCatalog();
        var descriptor = new ProviderDescriptor("failure-provider", [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var adapters = new InMemoryExternalOperationProviderCatalog();
        adapters.Register(descriptor, provider);

        var runtime = new DelegationRuntime(
            catalog, null, providers, adapters, now: () => Start);
        return (runtime, catalog);
    }

    private sealed class FailingProvider : IExternalOperationProvider
    {
        public async ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            var handle = Handle(request, "failing-handle");
            await handleSink.CaptureAsync(
                new ExternalOperationHandleCapture(handle, Start.AddMinutes(1)), cancellationToken);
            return new ExternalOperationStartReceipt(
                request.Identity, handle, ExternalOperationStartDisposition.Created, ExternalOperationState.Running, Start.AddMinutes(1));
        }

        public ValueTask<ExternalOperationObservation> ObserveAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationObservation(
                operationHandle, 1, ExternalOperationState.Failed, Start.AddMinutes(2)));

        public ValueTask<ExternalOperationResult> GetResultAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
            ExternalOperationCancelRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

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

    private sealed class SucceedingProvider : IExternalOperationProvider
    {
        public ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            var handle = Handle(request, "succeeding-handle");
            return ValueTask.FromResult(new ExternalOperationStartReceipt(
                request.Identity, handle, ExternalOperationStartDisposition.Created, ExternalOperationState.Running, Start.AddMinutes(1)));
        }

        public ValueTask<ExternalOperationObservation> ObserveAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationObservation(
                operationHandle, 1, ExternalOperationState.Succeeded, Start.AddMinutes(2), resultAvailable: true));

        public ValueTask<ExternalOperationResult> GetResultAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationResult(
                operationHandle, ExternalOperationState.Succeeded, Start.AddMinutes(3), "done", []));

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
            ExternalOperationCancelRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

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
