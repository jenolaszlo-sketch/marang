using FluentAssertions;
using Penghou.Qingniao;

namespace Marang.Tests;

/// <summary>
/// Proves Marang consumes the Qingniao runtime: Marang's admission verifier
/// and Implement verification policy drive a public DelegationRuntime from
/// delegation to a corrected revision-two completion, using only public
/// Qingniao API plus Marang-owned product types.
/// </summary>
public sealed class MarangDelegationTests
{
    private static readonly DateTimeOffset Start = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Marang_policy_corrects_once_and_completes_revision_two()
    {
        var validator = new RevisionValidator();
        var reviewer = new RevisionReviewer();
        var corrector = new FixedCorrector();
        var runtime = CreateRuntime(validator, reviewer, corrector);
        var ct = TestContext.Current.CancellationToken;

        var handle = await runtime.DelegateAsync(new DelegationCallerScope("caller"), CreateRequest("marang-run"), ct);
        var terminal = await PumpToTerminalAsync(runtime, handle.DelegationId, ct);

        terminal.Progress.State.Should().Be(DelegationState.Completed);
        terminal.Result!.Candidate!.Revision.Should().Be(2);
        terminal.Result.NormalizedEvidence!.Validations.Should().HaveCount(2);
        terminal.Result.NormalizedEvidence.Reviews.Should().HaveCount(2);
        corrector.Calls.Should().Be(1);
        validator.Calls.Should().Be(2);
        reviewer.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Unknown_fence_is_rejected_at_admission()
    {
        var runtime = CreateRuntime(new RevisionValidator(), new RevisionReviewer(), new FixedCorrector());
        var ct = TestContext.Current.CancellationToken;
        var request = new DelegationRequest(
            "fenced",
            "Do the work",
            "marang-provider",
            new WorkspaceReference("local", "workspace", "revision"),
            ["Done"],
            [],
            new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2),
            admissionFence: new DelegationAdmissionFence("marang-preset", "Other", "1"));

        var act = () => runtime.DelegateAsync(new DelegationCallerScope("caller"), request, ct);

        (await act.Should().ThrowAsync<DelegationAdmissionException>())
            .Which.Status.Should().Be(DelegationAdmissionStatus.Unknown);
    }

    private static async Task<DelegationExecutionSnapshot> PumpToTerminalAsync(
        DelegationRuntime runtime,
        DelegationId id,
        CancellationToken ct)
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
        "Implement the objective",
        "marang-provider",
        new WorkspaceReference("local", "workspace", "revision"),
        ["The result is correct"],
        [],
        new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2));

    private static DelegationRuntime CreateRuntime(
        IDeterministicCandidateValidator validator,
        IIndependentCandidateReviewer reviewer,
        ICandidateCorrector corrector)
    {
        var descriptor = new ProviderDescriptor("marang-provider", [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var provider = new MarangProvider();
        var adapters = new InMemoryExternalOperationProviderCatalog();
        adapters.Register(descriptor, provider);
        return new DelegationRuntime(
            new InMemoryDelegationAcceptanceRegistry(),
            new MarangAdmissionVerifier(),
            providers,
            adapters,
            now: () => Start,
            candidateValidator: validator,
            candidateReviewer: reviewer,
            candidateCorrector: corrector,
            verificationPolicy: new ImplementVerificationPolicy());
    }

    private sealed class MarangProvider : IExternalOperationProvider
    {
        private ExternalOperationHandle? handle;

        public async ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            var correlation = new ExternalOperationCorrelation(
                request.Correlation.DelegationId,
                request.Correlation.WorkflowRun,
                request.Correlation.StructuralNode,
                request.Correlation.NodeGeneration,
                request.Correlation.ExecutionAttemptId,
                request.Correlation.Agent,
                new ExternalTaskReference(request.Correlation.Agent.Provider, "marang-task"));
            handle = new ExternalOperationHandle(
                request.Correlation.Agent.Provider,
                "marang-handle",
                request.Correlation.Agent.ProtocolVersion,
                correlation);
            await handleSink.CaptureAsync(new ExternalOperationHandleCapture(handle, Start.AddMinutes(1)), cancellationToken);
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
            CancellationToken cancellationToken = default)
        {
            var correlation = operationHandle.Correlation;
            var artifact = new DelegationArtifactReference(
                correlation.DelegationId, correlation.StructuralNode, correlation.NodeGeneration,
                "marang-provider", "repo", "candidate", "application/octet-stream", 1, "candidate",
                ArtifactContentIdentity.Sha256Bytes(new string('a', 64)));
            var candidate = new CandidateRevisionReference(
                correlation.DelegationId, correlation.StructuralNode, correlation.NodeGeneration,
                new CandidateId(correlation.DelegationId.Value), 1, artifact.ContentIdentity, [artifact]);
            return ValueTask.FromResult(new ExternalOperationResult(
                operationHandle, ExternalOperationState.Succeeded, Start.AddMinutes(3), "done", [artifact], candidate: candidate));
        }

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
            ExternalOperationCancelRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationCancellationReceipt(
                request.Handle, request.CancellationKey, ExternalOperationCancellationDisposition.Requested,
                ExternalOperationState.CancellationRequested, Start.AddMinutes(4)));

        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
            ExternalOperationResumeRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RevisionValidator : IDeterministicCandidateValidator
    {
        public int Calls { get; private set; }

        public ValueTask<ValidationEvidence> ValidateAsync(
            CandidateValidationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var invocation = new WorkerInvocationEvidence(
                request.Candidate.DelegationId, request.Candidate.StructuralNode, request.Candidate.NodeGeneration,
                "deterministic.validation",
                new ProviderExecutionAttemptReference("validator", request.InvocationId, "evaluator"),
                "succeeded", Start.AddMinutes(4), Start.AddMinutes(5),
                "deterministic.validation", "profile", "validator", "model", "model",
                [], request.Candidate.Artifacts, request.Candidate.Artifacts, request.Candidate);
            return ValueTask.FromResult(new ValidationEvidence(
                invocation, request.Candidate.Revision == 1 ? "failed" : "passed", []));
        }
    }

    private sealed class RevisionReviewer : IIndependentCandidateReviewer
    {
        public int Calls { get; private set; }

        public ValueTask<ReviewEvidence> ReviewAsync(
            CandidateReviewRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var invocation = new WorkerInvocationEvidence(
                request.Candidate.DelegationId, request.Candidate.StructuralNode, request.Candidate.NodeGeneration,
                EvidenceKinds.ModelExecution,
                new ProviderExecutionAttemptReference("reviewer", request.InvocationId, "evaluator"),
                "succeeded", Start.AddMinutes(4), Start.AddMinutes(5),
                EvidenceKinds.ModelExecution, "review", "reviewer", "review-model", "review-model",
                [], request.Candidate.Artifacts, request.Candidate.Artifacts, request.Candidate);
            var independence = new ReviewIndependenceEvidence(
                request.ImplementationInvocation.Attempt.AttemptId, request.InvocationId,
                IndependenceAssessment.Unknown, IndependenceAssessment.Different, IndependenceAssessment.Different,
                IndependenceAssessment.Different, IndependenceAssessment.Different);
            return ValueTask.FromResult(new ReviewEvidence(
                invocation, request.Candidate.Revision == 1 ? "rejected" : "approved", [], independence, request.Candidate, "reviewer"));
        }
    }

    private sealed class FixedCorrector : ICandidateCorrector
    {
        public int Calls { get; private set; }

        public ValueTask<CandidateCorrectionOutcome> CorrectAsync(
            CandidateCorrectionRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var artifact = new DelegationArtifactReference(
                request.Correlation.DelegationId, request.Correlation.StructuralNode, request.TargetGeneration,
                "marang-provider", "repo", "candidate-v2", "application/octet-stream", 1, "candidate-v2",
                ArtifactContentIdentity.Sha256Bytes(new string('b', 64)));
            var candidate = new CandidateRevisionReference(
                request.SourceCandidate.DelegationId, request.SourceCandidate.StructuralNode, request.TargetGeneration,
                request.SourceCandidate.CandidateId, request.TargetRevision, artifact.ContentIdentity, [artifact]);
            var invocation = new WorkerInvocationEvidence(
                candidate.DelegationId, candidate.StructuralNode, candidate.NodeGeneration,
                EvidenceKinds.AgentExecution,
                new ProviderExecutionAttemptReference("marang-provider", request.Correlation.ExecutionAttemptId, "correction-handle"),
                "succeeded", Start.AddMinutes(6), Start.AddMinutes(7),
                "agent.execute", "implement", "marang-provider", null, "model",
                [], candidate.Artifacts, candidate.Artifacts, candidate, executionCorrelation: request.Correlation);
            return ValueTask.FromResult(new CandidateCorrectionOutcome(candidate, invocation));
        }
    }
}
