using System.Text.Json;
using FluentAssertions;
using Marang.Http;
using Penghou.Qingniao;

namespace Marang.Tests;

/// <summary>
/// Evidence/artifact behavior over the HTTP adapter surface: a terminal
/// delegation reports compact counters, flattened findings, and artifact
/// descriptors (metadata only, never bytes); unknown, foreign, and
/// not-yet-terminal cases fail closed or report truthfully empty.
/// </summary>
public sealed class MarangEvidenceHttpTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private const string Caller = "operator-1";

    [Fact]
    public async Task Terminal_delegation_reports_evidence_and_artifact_descriptors()
    {
        var (runtime, catalog) = CreateRuntime(new ArtifactProvider());
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope(Caller), CreateRequest("evidence-full"), ct);
        var terminal = await PumpToTerminalAsync(runtime, handle.DelegationId, ct);
        terminal.Progress.State.Should().Be(DelegationState.Completed);

        var (status, body) = await SupervisionHttpEndpoints.GetEvidenceAsync(
            runtime, catalog, Caller, handle.DelegationId.Value, ct);

        status.Should().Be(200);
        var view = Json(body);
        view.GetProperty("hasResult").GetBoolean().Should().BeTrue();
        view.GetProperty("evidence").GetProperty("testsPassed").GetInt32().Should().Be(0);
        var artifacts = view.GetProperty("artifacts");
        artifacts.GetArrayLength().Should().Be(1);
        var artifact = artifacts[0];
        Str(artifact, "artifactId").Should().Be("artifact-1");
        Str(artifact, "kind").Should().Be("test-output");
        Str(artifact, "provider").Should().Be("evidence-provider");
        artifact.TryGetProperty("content", out _).Should().BeFalse();

        var (singleStatus, singleBody) = await SupervisionHttpEndpoints.GetArtifactAsync(
            runtime, catalog, Caller, handle.DelegationId.Value, "artifact-1", ct);
        singleStatus.Should().Be(200);
        Str(Json(singleBody), "artifactId").Should().Be("artifact-1");
    }

    [Fact]
    public async Task Non_terminal_delegation_reports_truthfully_empty_evidence()
    {
        var (runtime, catalog) = CreateRuntime(new ArtifactProvider());
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope(Caller), CreateRequest("evidence-empty"), ct);

        var (status, body) = await SupervisionHttpEndpoints.GetEvidenceAsync(
            runtime, catalog, Caller, handle.DelegationId.Value, ct);

        status.Should().Be(200);
        var view = Json(body);
        view.GetProperty("hasResult").GetBoolean().Should().BeFalse();
        view.GetProperty("findings").GetArrayLength().Should().Be(0);
        view.GetProperty("artifacts").GetArrayLength().Should().Be(0);

        (await SupervisionHttpEndpoints.GetArtifactAsync(
            runtime, catalog, Caller, handle.DelegationId.Value, "artifact-1", ct)).StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Unknown_foreign_and_missing_artifacts_are_denied_without_distinction()
    {
        var (runtime, catalog) = CreateRuntime(new ArtifactProvider());
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope(Caller), CreateRequest("evidence-denied"), ct);
        await PumpToTerminalAsync(runtime, handle.DelegationId, ct);

        (await SupervisionHttpEndpoints.GetEvidenceAsync(
            runtime, catalog, "someone-else", handle.DelegationId.Value, ct)).StatusCode.Should().Be(404);
        (await SupervisionHttpEndpoints.GetEvidenceAsync(
            runtime, catalog, Caller, Guid.NewGuid(), ct)).StatusCode.Should().Be(404);
        (await SupervisionHttpEndpoints.GetArtifactAsync(
            runtime, catalog, "someone-else", handle.DelegationId.Value, "artifact-1", ct)).StatusCode.Should().Be(404);
        (await SupervisionHttpEndpoints.GetArtifactAsync(
            runtime, catalog, Caller, handle.DelegationId.Value, "no-such-artifact", ct)).StatusCode.Should().Be(404);
    }

    [Fact]
    public void FlattenFindings_maps_validation_and_review_findings_compactly()
    {
        var delegationId = new DelegationId(Guid.NewGuid());
        var invocation = new WorkerInvocationEvidence(
            delegationId,
            new StructuralNodeReference("delegation"),
            new NodeGenerationId(Guid.NewGuid()),
            "deterministic.test",
            new ProviderExecutionAttemptReference("evidence-provider", "attempt-1", "handle-1"),
            "succeeded",
            Start,
            Start.AddMinutes(1),
            "agent.execute",
            null, "evidence-provider", null, null,
            [], [], [],
            null, null, null, null);
        var bundle = new EvidenceBundle(
            [],
            [new ValidationEvidence(
                invocation,
                "pass",
                [new EvidenceFinding("v1", "info", "first finding", true),
                 new EvidenceFinding("v2", "warning", "second finding", false)],
                "validator-1")],
            []);

        var flattened = EvidenceHttpMapper.FlattenFindings(bundle);

        flattened.Should().HaveCount(2);
        var json = JsonSerializer.Serialize(flattened);
        var items = JsonDocument.Parse(json).RootElement;
        Str(items[0], "code").Should().Be("v1");
        Str(items[0], "source").Should().Be("validation");
        items[0].GetProperty("resolved").GetBoolean().Should().BeTrue();
        Str(items[1], "severity").Should().Be("warning");
        items[1].TryGetProperty("details", out _).Should().BeFalse();
        EvidenceHttpMapper.FlattenFindings(null).Should().BeEmpty();
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
        "evidence-provider",
        new WorkspaceReference("local", "workspace", "revision"),
        ["Done"],
        [],
        new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2));

    private static (DelegationRuntime Runtime, MarangDelegationCatalog Catalog) CreateRuntime(
        IExternalOperationProvider provider)
    {
        var catalog = new MarangDelegationCatalog();
        var descriptor = new ProviderDescriptor("evidence-provider", [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var adapters = new InMemoryExternalOperationProviderCatalog();
        adapters.Register(descriptor, provider);

        var runtime = new DelegationRuntime(
            catalog, null, providers, adapters, now: () => Start);
        return (runtime, catalog);
    }

    private sealed class ArtifactProvider : IExternalOperationProvider
    {
        public async ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            var handle = Handle(request, "evidence-handle");
            await handleSink.CaptureAsync(
                new ExternalOperationHandleCapture(handle, Start.AddMinutes(1)), cancellationToken);
            return new ExternalOperationStartReceipt(
                request.Identity, handle, ExternalOperationStartDisposition.Created, ExternalOperationState.Running, Start.AddMinutes(1));
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
                operationHandle, ExternalOperationState.Succeeded, Start.AddMinutes(3), "done",
                [Artifact(operationHandle)]));

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
            ExternalOperationCancelRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
            ExternalOperationResumeRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private static DelegationArtifactReference Artifact(ExternalOperationHandle handle) => new(
            handle.Correlation.DelegationId,
            handle.Correlation.StructuralNode,
            handle.Correlation.NodeGeneration,
            handle.Correlation.Agent.Provider,
            "repo",
            "artifact-1",
            "test-output",
            1,
            "outputs/result.txt",
            new ArtifactContentIdentity("sha256-v1", new string('0', 64)));

        private static ExternalOperationHandle Handle(ExternalOperationStartRequest request, string value) => new(
            request.Correlation.Agent.Provider, value, request.Correlation.Agent.ProtocolVersion,
            new ExternalOperationCorrelation(request.Correlation.DelegationId, request.Correlation.WorkflowRun,
                request.Correlation.StructuralNode, request.Correlation.NodeGeneration,
                request.Correlation.ExecutionAttemptId, request.Correlation.Agent,
                new ExternalTaskReference(request.Correlation.Agent.Provider, value)));
    }
}
