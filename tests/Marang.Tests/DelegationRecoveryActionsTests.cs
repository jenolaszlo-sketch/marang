using FluentAssertions;
using Marang.Hufu;
using Marang.Runs.Adaptive;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Hufu;
using Penghou.Hufu.Cedar;
using Penghou.Hufu.Sqlite;
using Penghou.Qingniao;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Tests;

public sealed class DelegationRecoveryActionsTests
{
    [Theory]
    [InlineData(ExternalOperationRecoveryState.StartIntent, DelegationRecoveryClassification.ProviderStateUnknown)]
    [InlineData(ExternalOperationRecoveryState.UnknownAfterRecovery, DelegationRecoveryClassification.ProviderStateUnknown)]
    [InlineData(ExternalOperationRecoveryState.KnownNotReconnectable, DelegationRecoveryClassification.KnownNotReconnectable)]
    public void Unknown_and_non_reconnectable_operations_are_decisions_without_observe_or_retry(
        ExternalOperationRecoveryState state, DelegationRecoveryClassification expected)
    {
        var (identity, record) = Record(state);

        var projection = DelegationRecoveryActions.Project(identity, record, WorkflowId, ExecutionId,
            "revision-2", "state-digest", "m1/bounded-repository-read-report", isActive: true);

        projection.Classification.Should().Be(expected);
        projection.RequiresDecision.Should().BeTrue();
        projection.SafeToReplace.Should().BeTrue();
        projection.AllowedActions.Should().Contain(DelegationRecoveryAction.Wait)
            .And.Contain(DelegationRecoveryAction.Replace)
            .And.NotContain(DelegationRecoveryAction.Observe);
        Enum.GetNames<DelegationRecoveryAction>().Should().NotContain("Retry");
    }

    [Fact]
    public void Historical_delegation_never_blocks_or_offers_replacement_for_active_revision()
    {
        var (identity, record) = Record(ExternalOperationRecoveryState.UnknownAfterRecovery);

        var projection = DelegationRecoveryActions.Project(identity, record, WorkflowId, ExecutionId,
            "revision-3", "current-state", "external-side-effect", isActive: false);

        projection.Classification.Should().Be(DelegationRecoveryClassification.ProviderStateUnknown);
        projection.SafeToReplace.Should().BeFalse();
        projection.RequiresDecision.Should().BeFalse();
        projection.AllowedActions.Should().ContainSingle().Which.Should().Be(DelegationRecoveryAction.Wait);
    }

    [Fact]
    public void Missing_journal_record_is_unknown_and_never_authorizes_observation()
    {
        var (identity, _) = Record(ExternalOperationRecoveryState.StartIntent);

        var projection = DelegationRecoveryActions.Project(identity, null, WorkflowId, ExecutionId,
            "revision-1", "state", "m1/bounded-repository-read-report", isActive: true);

        projection.Classification.Should().Be(DelegationRecoveryClassification.ProviderStateUnknown);
        projection.AllowedActions.Should().NotContain(DelegationRecoveryAction.Observe);
        projection.JournalRecordDigest.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Replacement_is_not_advertised_for_a_branch_outside_the_bounded_read_report_profile()
    {
        var (identity, record) = Record(ExternalOperationRecoveryState.UnknownAfterRecovery);

        var projection = DelegationRecoveryActions.Project(identity, record, WorkflowId, ExecutionId,
            "revision-1", "state", "external-side-effect", isActive: true);

        projection.RequiresDecision.Should().BeTrue();
        projection.SafeToReplace.Should().BeFalse();
        projection.AllowedActions.Should().NotContain(DelegationRecoveryAction.Replace);
        projection.AllowedActions.Should().Contain(DelegationRecoveryAction.Wait);
    }

    [Fact]
    public async Task Replacement_uses_exact_M1_proposal_and_fences_only_the_changed_delegation_branch()
    {
        using var fixture = await RecoveryFixture.CreateAsync();
        var projection = await fixture.ProjectionAsync();
        var candidate = await fixture.CandidateAsync(changeTarget: true);

        var decision = await fixture.Actions.PrepareReplacementAsync(projection, candidate,
            "Replace the unresolved delegated report with a fresh bounded report branch.", Operator, Ct);
        var approval = await fixture.Actions.ApproveReplacementAsync(decision.DecisionId, Operator, Ct);
        var committed = await fixture.Actions.CompleteReplacementAsync(decision.DecisionId, Operator, approval, Ct);

        var active = await fixture.Revisions.GetActiveAsync(fixture.WorkflowId, Ct);
        active.RevisionId.Should().Be("r2");
        active.Branches.Single(b => b.LogicalId == "inventory").Execution.Id.Should().Be(fixture.InventoryExecutionId);
        active.Branches.Single(b => b.LogicalId == "delegated").Execution.Id.Should().NotBe(fixture.DelegatedExecutionId);
        var oldRun = await fixture.WorkflowStore.GetRunAsync(fixture.DelegatedExecutionId, Ct);
        oldRun!.Status.Should().Be(WorkflowStatus.Cancelled);
        committed.Status.Should().Be("Committed");
        committed.ProposalId.Should().Be(decision.ProposalId);
        committed.AuthorityOutcome.Should().Be("Automatic");
        committed.ResultingRevisionId.Should().Be("r2");
        (await fixture.Actions.ListDecisionsAsync(fixture.WorkflowId, Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task Replacement_rejects_a_candidate_that_preserves_the_unknown_operation_execution()
    {
        using var fixture = await RecoveryFixture.CreateAsync();
        var projection = await fixture.ProjectionAsync();
        var preservingCandidate = await fixture.CandidateAsync(changeTarget: false, changeInventory: true);

        var prepare = () => fixture.Actions.PrepareReplacementAsync(projection, preservingCandidate,
            "This candidate changes another branch only.", Operator, Ct);
        await prepare.Should().ThrowAsync<WorkflowStateException>();
        (await fixture.Revisions.GetActiveAsync(fixture.WorkflowId, Ct)).RevisionId.Should().Be("r1");
        (await fixture.Revisions.GetPendingAsync(fixture.WorkflowId, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Journal_progress_invalidates_a_projection_before_recovery_authority_is_used()
    {
        using var fixture = await RecoveryFixture.CreateAsync();
        var projection = await fixture.ProjectionAsync();
        await fixture.Journal.MarkKnownNotReconnectableAsync(projection.Identity, Ct);

        var wait = () => fixture.Actions.WaitAsync(projection, Operator, Ct);
        await wait.Should().ThrowAsync<WorkflowStateException>().WithMessage("*stale*");
        (await fixture.Revisions.GetActiveAsync(fixture.WorkflowId, Ct)).RevisionId.Should().Be("r1");
        (await fixture.Actions.ListDecisionsAsync(fixture.WorkflowId, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Forged_projection_options_cannot_add_replacement_authority()
    {
        using var fixture = await RecoveryFixture.CreateAsync();
        var valid = await fixture.ProjectionAsync();
        var forged = valid with
        {
            BoundBranchAuthorityDigest = "external-side-effect",
            SafeToReplace = true,
            AllowedActions = [DelegationRecoveryAction.Wait, DelegationRecoveryAction.Replace]
        };
        var candidate = await fixture.CandidateAsync(changeTarget: true);

        var prepare = () => fixture.Actions.PrepareReplacementAsync(forged, candidate,
            "Forged action list must fail closed.", Operator, Ct);
        await prepare.Should().ThrowAsync<WorkflowStateException>();
        (await fixture.Revisions.GetPendingAsync(fixture.WorkflowId, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Revoked_Hufu_grant_after_approval_denies_commit_and_keeps_old_revision_active()
    {
        using var fixture = await RecoveryFixture.CreateAsync();
        var projection = await fixture.ProjectionAsync();
        var candidate = await fixture.CandidateAsync(changeTarget: true);
        var decision = await fixture.Actions.PrepareReplacementAsync(projection, candidate,
            "Replace only after exact operator approval.", Operator, Ct);
        var approval = await fixture.Actions.ApproveReplacementAsync(decision.DecisionId, Operator, Ct);
        await fixture.RevokeGrantAsync();

        var commit = () => fixture.Actions.CompleteReplacementAsync(decision.DecisionId, Operator, approval, Ct);
        await commit.Should().ThrowAsync<WorkflowStateException>();
        (await fixture.Revisions.GetActiveAsync(fixture.WorkflowId, Ct)).RevisionId.Should().Be("r1");
        var rejected = (await fixture.Actions.ListDecisionsAsync(fixture.WorkflowId, Ct)).Single();
        rejected.Status.Should().Be("AwaitingApproval");
        rejected.AuthorityOutcome.Should().Be("Denied");
        rejected.AuthorityEvidence.Should().Contain("hufu");
        (await fixture.WorkflowStore.GetRunAsync(fixture.DelegatedExecutionId, Ct))!.Status.Should().Be(WorkflowStatus.Pending);
    }

    [Fact]
    public async Task Provider_evidence_progress_after_approval_invalidates_the_exact_replacement()
    {
        using var fixture = await RecoveryFixture.CreateAsync();
        var projection = await fixture.ProjectionAsync();
        var decision = await fixture.Actions.PrepareReplacementAsync(projection, await fixture.CandidateAsync(changeTarget: true),
            "Replace only the reviewed unknown report attempt.", Operator, Ct);
        var approval = await fixture.Actions.ApproveReplacementAsync(decision.DecisionId, Operator, Ct);
        await fixture.Journal.MarkKnownNotReconnectableAsync(projection.Identity, Ct);

        var commit = () => fixture.Actions.CompleteReplacementAsync(decision.DecisionId, Operator, approval, Ct);
        await commit.Should().ThrowAsync<WorkflowStateException>().WithMessage("*Stale*");
        (await fixture.Revisions.GetActiveAsync(fixture.WorkflowId, Ct)).RevisionId.Should().Be("r1");
        (await fixture.WorkflowStore.GetRunAsync(fixture.DelegatedExecutionId, Ct))!.Status.Should().Be(WorkflowStatus.Pending);
    }

    [Fact]
    public async Task Another_proposal_with_the_same_candidate_does_not_resolve_the_old_recovery_decision()
    {
        using var fixture = await RecoveryFixture.CreateAsync();
        var projection = await fixture.ProjectionAsync();
        var candidate = await fixture.CandidateAsync(changeTarget: true);
        var original = await fixture.Actions.PrepareReplacementAsync(projection, candidate, "First exact recovery proposal.", Operator, Ct);
        var newer = await fixture.Supervisor.ProposeAsync(candidate, "A separate reviewed proposal supersedes the first.", Ct);
        var approval = await fixture.Supervisor.ApproveAsync(newer.Proposal, newer.Digest, Operator, Ct);
        await fixture.Supervisor.CommitAsync(newer.Proposal, newer.Digest, approval, Ct);
        var store = new SqliteSupervisorContinuityStore(fixture.Database, fixture.Revisions, actor => actor == Operator);
        await store.InitializeAsync(Ct);
        var catchUp = new SupervisorCatchUp(fixture.Database, actor => actor == Operator);
        await catchUp.InitializeAsync(Ct);
        var view = await catchUp.ReadAsync(fixture.WorkflowId, Operator, Ct);
        view.Decisions.Should().Contain(d => d.Id == original.DecisionId.ToString("D") && d.IsStale);
        (await fixture.Actions.GetDecisionAsync(original.DecisionId, Ct))!.Status.Should().Be("AwaitingApproval");
    }

    [Fact]
    public async Task Failed_observation_is_durable_and_never_starts_or_adopts_provider_work()
    {
        using var fixture = await RecoveryFixture.CreateAsync();
        var original = await fixture.ProjectionAsync();
        var record = (await fixture.Journal.ReadAsync(original.Identity, Ct))!;
        var originalCorrelation = record.Intent.Request.Correlation;
        var capturedCorrelation = new ExternalOperationCorrelation(originalCorrelation.DelegationId, originalCorrelation.WorkflowRun,
            originalCorrelation.StructuralNode, originalCorrelation.NodeGeneration, originalCorrelation.ExecutionAttemptId,
            originalCorrelation.Agent, new ExternalTaskReference(originalCorrelation.Agent.Provider, "existing-task"));
        var handle = new ExternalOperationHandle(originalCorrelation.Agent.Provider, "existing-task", "a2a-0.3", capturedCorrelation);
        await fixture.Journal.CaptureAsync(original.Identity, new ExternalOperationHandleCapture(handle, Now), Ct);
        var observable = await fixture.ProjectionAsync();
        var provider = new UnavailableObservationProvider();
        var observe = () => fixture.Actions.ObserveAsync(observable, provider, Operator, Ct);
        await observe.Should().ThrowAsync<IOException>();
        var decision = (await fixture.Actions.ListDecisionsAsync(fixture.WorkflowId, Ct)).Single();
        decision.Status.Should().Be("ObservationUnresolved");
        decision.ResultingState.Should().Contain("No authoritative adoption");
        var store = new SqliteSupervisorContinuityStore(fixture.Database, fixture.Revisions, actor => actor == Operator);
        await store.InitializeAsync(Ct);
        var catchUp = new SupervisorCatchUp(fixture.Database, actor => actor == Operator); await catchUp.InitializeAsync(Ct);
        (await catchUp.ReadAsync(fixture.WorkflowId, Operator, Ct)).Decisions.Should().Contain(d => d.Kind == "RecoveryObservation");
        provider.Starts.Should().Be(0);
        (await fixture.Revisions.GetActiveAsync(fixture.WorkflowId, Ct)).RevisionId.Should().Be("r1");
    }

    [Fact]
    public async Task Workflow_wide_cancellation_is_separate_from_a_version_bound_recovery_choice()
    {
        using var fixture = await RecoveryFixture.CreateAsync();
        var projection = await fixture.ProjectionAsync();
        await fixture.RevokeGrantAsync();

        var cancel = () => fixture.Supervisor.CancelAsync(fixture.WorkflowId, Operator, Ct);
        await cancel.Should().ThrowAsync<WorkflowStateException>();
        (await fixture.Revisions.GetActiveAsync(fixture.WorkflowId, Ct)).RevisionId.Should().Be("r1");
        (await fixture.Actions.ListDecisionsAsync(fixture.WorkflowId, Ct)).Should().BeEmpty();
        projection.AllowedActions.Select(a => a.ToString()).Should().NotContain("CancelWorkflow");
    }

    private static readonly Guid WorkflowId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid ExecutionId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private const string Operator = "recovery-operator";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (ExternalOperationStartIdentity Identity, ExternalOperationRecoveryRecord Record) Record(
        ExternalOperationRecoveryState state)
    {
        var delegation = new DelegationId(Guid.Parse("30000000-0000-0000-0000-000000000003"));
        var agent = new ExternalAgentReference("a2a", "agent-1", "a2a-0.3");
        var workflow = new WorkflowRunExecutionReference("zhinu", ExecutionId.ToString("D"), "epoch-1");
        var node = new StructuralNodeReference("build");
        var generation = new NodeGenerationId(Guid.Parse("40000000-0000-0000-0000-000000000004"));
        var correlation = new ExternalOperationCorrelation(delegation, workflow, node, generation, "attempt-1", agent);
        var identity = new ExternalOperationStartIdentity(delegation, workflow, node, generation,
            "attempt-1", "idempotency-1", new string('a', 64));
        var request = new ExternalOperationStartRequest(identity, correlation, "agent.execute", []);
        var intent = new Penghou.Qingniao.ExternalOperationRecoveryIntent(request, DateTimeOffset.Parse("2026-10-14T00:00:00Z"));
        return (identity, new ExternalOperationRecoveryRecord(intent, recoveryState: state));
    }

    private sealed class RecoveryFixture : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "marang-delegation-recovery-" + Guid.NewGuid().ToString("N"));
        private readonly Guid workflowId = Guid.NewGuid();
        private readonly Ports ports = new();
        private readonly SqliteDatabase database;
        private readonly SqliteAuthorityStore hufu;
        private readonly AuthorityStoreActor storeActor;
        private readonly AuthenticatedAuthorityContext authorityContext;
        private AuthoredAdaptiveRevision authored = null!;

        public Guid WorkflowId => workflowId;
        public Guid DelegatedExecutionId { get; private set; }
        public Guid InventoryExecutionId { get; private set; }
        public SqliteAdaptiveRevisionRepository Revisions { get; }
        public IZhinuSqliteDatabase Database => database;
        public SqliteDelegationRecoveryJournal Journal { get; }
        public SqliteWorkflowStore WorkflowStore { get; }
        public SqliteAdaptiveSupervisorAuthority Authority { get; }
        public AdaptiveSupervisor Supervisor { get; }
        public DelegationRecoveryActions Actions { get; }
        private AdaptiveWorkSpec[] specs = null!;

        private RecoveryFixture()
        {
            Directory.CreateDirectory(directory);
            database = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = System.IO.Path.Combine(directory, "runs.db"), Pooling = false });
            Revisions = new SqliteAdaptiveRevisionRepository(database);
            Journal = new SqliteDelegationRecoveryJournal(database);
            WorkflowStore = new SqliteWorkflowStore(database);
            storeActor = new AuthorityStoreActor("recovery-test", "host", "session");
            hufu = new SqliteAuthorityStore(System.IO.Path.Combine(directory, "hufu.db"), new AllowStorePolicy(), new FixedTimeProvider(Now));
            authorityContext = new AuthenticatedAuthorityContext("recovery-test", "supervisor", workflowId.ToString("D"), "r1", "binding-fence");
            var current = new CurrentAuthorityRequestAuthorizer(new AuthorityStoreSnapshotSource(hufu, storeActor),
                new CedarAuthorityEvaluator(), new AllowDecisionRecorder(), new FixedTimeProvider(Now));
            var profile = new AdaptiveAuthorityProfile("recovery-test", "supervisor", "repository", "reports/readiness.md", "write-file", "binding-fence");
            Authority = new SqliteAdaptiveSupervisorAuthority(database, Revisions, current, profile, actor => actor == Operator);
            Supervisor = new AdaptiveSupervisor(Revisions, Authority);
            Actions = new DelegationRecoveryActions(database, Journal, Revisions, Supervisor, Authority);
        }

        public static async Task<RecoveryFixture> CreateAsync()
        {
            var fixture = new RecoveryFixture();
            await fixture.Revisions.InitializeAsync(Ct);
            await fixture.Journal.InitializeAsync(Ct);
            await fixture.WorkflowStore.InitializeAsync(Ct);
            await fixture.Authority.InitializeAsync(Ct);
            await fixture.Actions.InitializeAsync(Ct);
            fixture.specs =
            [
                new("delegated", "Delegated report", "create bounded report", "snapshot", "m1/bounded-repository-read-report", "source", [], Guid.NewGuid(), true),
                new("inventory", "Inventory", "collect inventory", "snapshot", "m1/bounded-repository-read-report", "source", [], Guid.NewGuid(), true)
            ];
            fixture.authored = await AdaptivePlanAuthoring.AuthorAsync(fixture.workflowId, "r1", null,
                "Create a repository report", "Initial bounded report plan", "fixture", fixture.specs,
                new FuwenZhinuExecutionPorts(fixture.ports, fixture.ports, fixture.ports), cancellationToken: Ct);
            await fixture.Revisions.ActivateInitialAsync(fixture.authored.Revision, Ct);
            var delegated = fixture.authored.Revision.Branches.Single(b => b.LogicalId == "delegated");
            var inventory = fixture.authored.Revision.Branches.Single(b => b.LogicalId == "inventory");
            fixture.DelegatedExecutionId = delegated.Execution.Id;
            fixture.InventoryExecutionId = inventory.Execution.Id;
            await fixture.BindUnknownOperationAsync(delegated);
            await fixture.PublishGrantAsync();
            return fixture;
        }

        public async Task<DelegationRecoveryProjection> ProjectionAsync()
        {
            var branch = (await Revisions.GetActiveAsync(workflowId, Ct)).Branches.Single(b => b.LogicalId == "delegated");
            var binding = await Journal.GetBindingAsync(branch.Execution.Id, branch.InputDigest, Ct);
            var record = (await Journal.ListAsync(binding!.DelegationId, Ct)).Single();
            return DelegationRecoveryActions.Project(record.Intent.Request.Identity, record, workflowId, branch.Execution.Id,
                (await Revisions.GetActiveAsync(workflowId, Ct)).RevisionId, await Revisions.GetStateDigestAsync(workflowId, Ct),
                branch.AuthorityDigest, isActive: true);
        }

        public async Task<AdaptiveRevision> CandidateAsync(bool changeTarget, bool changeInventory = false)
        {
            var next = specs.ToArray();
            if (changeTarget) next[0] = next[0] with { Requirement = "replace external report task", ExecutionId = Guid.NewGuid() };
            if (changeInventory) next[1] = next[1] with { Requirement = "refresh inventory input", ExecutionId = Guid.NewGuid() };
            return (await AdaptivePlanAuthoring.AuthorAsync(workflowId, "r2", "r1", "Create a repository report",
                "Resolve unknown delegated work", "fixture", next,
                new FuwenZhinuExecutionPorts(ports, ports, ports), authored.Registry, Ct)).Revision;
        }

        public async Task RevokeGrantAsync()
        {
            var result = await hufu.RevokeAsync(new AuthorityRevokeCommand("revoke-" + Guid.NewGuid().ToString("N"),
                storeActor, authorityContext, 1, "test.revoked"), Ct);
            result.Status.Should().Be(AuthorityMutationStatus.Applied);
        }

        private async Task BindUnknownOperationAsync(AdaptiveBranch branch)
        {
            var delegationId = new DelegationId(Guid.NewGuid());
            var agent = new ExternalAgentReference("a2a", "report-agent", "a2a-0.3");
            // Qingniao's provider run identifier is independent from the Zhinu manifest execution ID.
            var workflow = new WorkflowRunExecutionReference("qingniao", "provider-run-" + branch.Execution.Id.ToString("N"), "recovery-epoch");
            var node = new StructuralNodeReference("delegated-report");
            var generation = new NodeGenerationId(Guid.NewGuid());
            var correlation = new ExternalOperationCorrelation(delegationId, workflow, node, generation, "attempt-1", agent);
            var identity = new ExternalOperationStartIdentity(delegationId, workflow, node, generation, "attempt-1", "recovery-idem", new string('b', 64));
            var request = new ExternalOperationStartRequest(identity, correlation, "agent.execute", []);
            await Journal.TryCreateIntentAsync(new Penghou.Qingniao.ExternalOperationRecoveryIntent(request, Now), Ct);
            await Journal.BindAsync(branch.Execution.Id,
                new DelegationHandle(delegationId, new WorkflowReference("zhinu", branch.Execution.Id.ToString("D")), DelegationState.Queued),
                branch.InputDigest, Ct);
        }

        private async Task PublishGrantAsync()
        {
            var now = Now;
            var snapshot = new AuthoritySnapshot(authorityContext, "recovery-grant-v1",
                [new AuthorityLayer("current", [new AuthorityGrant("recovery-write", [AuthorityAction.WriteFile],
                    new AuthorityScope("repository", "reports", AuthorityScopeKind.Subtree), [], now.AddMinutes(-1), now.AddHours(2))])], [], now.AddHours(2));
            var result = await hufu.PublishAsync(new AuthorityPublishCommand("publish-recovery", storeActor, snapshot, 0), Ct);
            result.Status.Should().Be(AuthorityMutationStatus.Applied);
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class AllowStorePolicy : IAuthorityStoreAuthorizer
    {
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request, CancellationToken cancellationToken = default) =>
            new(new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
    }

    private sealed class AllowDecisionRecorder : IAuthorityDecisionRecorder
    {
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken cancellationToken = default) => new(true);
    }

    private sealed class UnavailableObservationProvider : IExternalOperationProvider
    {
        public int Starts { get; private set; }
        public ValueTask<ExternalOperationStartReceipt> StartAsync(ExternalOperationStartRequest request, IExternalOperationHandleCaptureSink handleSink, CancellationToken cancellationToken = default)
        { Starts++; throw new InvalidOperationException("Start is forbidden in an observation action."); }
        public ValueTask<ExternalOperationObservation> ObserveAsync(ExternalOperationHandle handle, CancellationToken cancellationToken = default) => throw new IOException("Provider is unavailable.");
        public ValueTask<ExternalOperationResult> GetResultAsync(ExternalOperationHandle handle, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(ExternalOperationCancelRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(ExternalOperationResumeRequest request, IExternalOperationHandleCaptureSink sink, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Ports : IActivityExecutor, IContextProvider, IInferenceExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
