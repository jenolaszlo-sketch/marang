using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Marang.Hufu;
using Marang.Runs.Adaptive;
using Marang.Runs.Effects;
using Marang.Runs.Integrity;
using Microsoft.Data.Sqlite;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Hufu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Tests;

[CollectionDefinition("RepositoryRecoveryIntegrity", DisableParallelization = true)]
public sealed class RepositoryRecoveryIntegrityCollection { }

[Collection("RepositoryRecoveryIntegrity")]
public sealed class RepositoryRecoveryInspectorTests
{
    [Fact]
    public async Task Initial_workflow_without_a_proposal_or_effect_graph_is_admissible()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync(prepareProposal: false);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(RepositoryRecoveryStatus.Ready);
    }

    [Fact]
    public async Task Prepared_graph_belonging_to_pending_proposal_is_consistent_without_becoming_active()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(RepositoryRecoveryStatus.Ready);
        (await fixture.Revisions.GetActiveAsync(fixture.WorkflowId, ct)).RevisionId.Should().Be("r1");
    }

    [Fact]
    public async Task Committed_graph_and_mutable_effect_states_remain_admissible()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await fixture.CommitAsync(ct);
        await fixture.Effects.TransitionAsync(fixture.Graph.Effects[0].Effect.Intent.EffectId, RepositoryEffectStage.IntentCommitted, cancellationToken: ct);
        await fixture.Effects.TransitionAsync(fixture.Graph.Effects[0].Effect.Intent.EffectId, RepositoryEffectStage.Applying, cancellationToken: ct);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(RepositoryRecoveryStatus.Ready);
    }

    [Fact]
    public async Task Partially_applied_blocked_effect_remains_admissible_for_existing_reconciliation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await fixture.CommitAsync(ct);
        var intent = fixture.Graph.Effects[0].Effect.Intent;
        File.WriteAllBytes(Path.Combine(fixture.CheckoutPath, intent.Files[0].Path.Replace('/', Path.DirectorySeparatorChar)), intent.Files[0].AfterBytes.ToArray());
        var observed = await fixture.Repository.ObserveAsync(ct);
        RepositoryEffectReconciler.Classify(intent, observed).Should().Be(RepositoryEffectClassification.PartiallyApplied);
        await fixture.Effects.TransitionAsync(intent.EffectId, RepositoryEffectStage.Blocked,
            RepositoryEffectClassification.PartiallyApplied, observed, cancellationToken: ct);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(RepositoryRecoveryStatus.Ready);
    }

    [Fact]
    public async Task Validation_failed_graph_evidence_remains_admissible()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await fixture.CommitAsync(ct);
        var expected = fixture.Graph.Effects[0].Effect.Intent.ExpectedPostState;
        await fixture.Graphs.RecordWorkflowValidationAsync(fixture.Graph,
            new(expected.StateDigest, "full-suite", "dotnet test", false, RepositoryStateSnapshot.Hash("failed test"), "suite failed"), ct);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(RepositoryRecoveryStatus.Ready);
    }

    [Theory]
    [InlineData("validation-required-fields", "UPDATE repository_effect_graph_validations SET document='{}' WHERE revision_id='r2'")]
    [InlineData("validation-target-digest", "UPDATE repository_effect_graph_validations SET target_state_digest='bad' WHERE revision_id='r2'")]
    public async Task Malformed_graph_validation_evidence_refuses(string _, string damageSql)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await fixture.CommitAsync(ct);
        var expected = fixture.Graph.Effects[0].Effect.Intent.ExpectedPostState;
        await fixture.Graphs.RecordWorkflowValidationAsync(fixture.Graph,
            new(expected.StateDigest, "full-suite", "dotnet test", false, RepositoryStateSnapshot.Hash("failed test"), "suite failed"), ct);
        await fixture.MutateAsync(damageSql, ct);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(RepositoryRecoveryStatus.InconsistentState);
    }

    [Theory]
    [InlineData("revision-json", "UPDATE adaptive_revisions SET document='{' WHERE revision_id='r1'")]
    [InlineData("revision-column", "UPDATE adaptive_revisions SET revision_id='wrong' WHERE revision_id='r1'")]
    [InlineData("proposal-json", "UPDATE adaptive_proposals SET document='{' WHERE id=(SELECT pending_proposal FROM adaptive_workflows WHERE id=(SELECT workflow_id FROM adaptive_revisions LIMIT 1))")]
    [InlineData("proposal-digest", "UPDATE adaptive_proposals SET digest='bad' WHERE status='Pending'")]
    [InlineData("owner-missing", "DELETE FROM adaptive_run_ownership WHERE run_id=(SELECT run_id FROM adaptive_run_ownership LIMIT 1)")]
    [InlineData("run-identity", "UPDATE workflow_runs SET workflow_name='wrong' WHERE id=(SELECT run_id FROM adaptive_run_ownership LIMIT 1)")]
    [InlineData("dependency-extra", "INSERT INTO adaptive_execution_dependencies(run_id,prerequisite_run_id) SELECT a.run_id,b.run_id FROM adaptive_run_ownership a,adaptive_run_ownership b WHERE a.run_id<>b.run_id LIMIT 1")]
    [InlineData("graph-json", "UPDATE repository_effect_graphs SET document='{' WHERE revision_id='r2'")]
    [InlineData("graph-missing", "DELETE FROM repository_effect_graphs WHERE revision_id='r2'")]
    [InlineData("causal-digest", "UPDATE repository_causal_effects SET causal_digest='bad'")]
    [InlineData("causal-missing", "DELETE FROM repository_causal_effects")]
    [InlineData("approval-json", "UPDATE adaptive_operator_approvals SET document='{'")]
    [InlineData("approval-column", "UPDATE adaptive_operator_approvals SET digest='bad'")]
    [InlineData("run-missing", "DELETE FROM workflow_runs WHERE id=(SELECT run_id FROM adaptive_run_ownership LIMIT 1)")]
    public async Task Logical_damage_refuses_without_repairing_database(string _, string damageSql)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await fixture.MutateAsync(damageSql, ct);
        var before = await File.ReadAllBytesAsync(fixture.DatabasePath, ct);
        var result = await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);
        result.Status.Should().Be(RepositoryRecoveryStatus.InconsistentState);
        (await File.ReadAllBytesAsync(fixture.DatabasePath, ct)).Should().Equal(before);
    }

    [Fact]
    public async Task Missing_required_profile_table_is_a_shape_refusal()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await fixture.MutateAsync("DROP TABLE adaptive_provider_handles", ct);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(RepositoryRecoveryStatus.MissingStateShape);
    }

    [Theory]
    [InlineData("DROP TABLE supervisor_revision_intents")]
    [InlineData("DROP TABLE supervisor_pending_discussions")]
    [InlineData("DROP TABLE supervisor_operator_checkpoints")]
    [InlineData("DROP TABLE supervisor_proposal_contexts")]
    public async Task Missing_supervisor_continuity_table_is_a_shape_refusal(string damageSql)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await fixture.MutateAsync(damageSql, ct);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(RepositoryRecoveryStatus.MissingStateShape);
    }

    [Theory]
    [InlineData("UPDATE supervisor_revision_intents SET evidence_refs_json='null'")]
    [InlineData("UPDATE supervisor_pending_discussions SET missing_json='null'")]
    [InlineData("UPDATE supervisor_proposal_contexts SET document='{' WHERE proposal_id=(SELECT id FROM adaptive_proposals WHERE status='Pending')")]
    public async Task Malformed_supervisor_continuity_payload_refuses(string damageSql)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await fixture.MutateAsync(damageSql, ct);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(RepositoryRecoveryStatus.InconsistentState);
    }

    [Fact]
    public async Task Causal_intent_cannot_be_rebound_to_another_valid_effect_journal_intent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await fixture.RebindJournalIntentAsync(ct);
        (await fixture.Effects.GetAsync(fixture.Graph.Effects[0].Effect.Intent.EffectId, ct))!.Intent.HasValidDigest().Should().BeTrue();
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(RepositoryRecoveryStatus.InconsistentState);
    }

    [Fact]
    public async Task Provider_start_intent_without_capture_needs_no_handle_index()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await fixture.AddProviderAttemptAsync(captureHandle: false, markNotReconnectable: false, ct: ct);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(RepositoryRecoveryStatus.Ready);
    }

    [Fact]
    public async Task Accepted_branch_binding_without_provider_attempt_remains_admissible()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await fixture.AddAcceptedBindingWithoutAttemptAsync(ct);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(RepositoryRecoveryStatus.Ready);
    }

    [Fact]
    public async Task Provider_start_intent_requires_host_branch_binding()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await fixture.AddProviderAttemptAsync(captureHandle: false, markNotReconnectable: false, ct: ct, bind: false);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(RepositoryRecoveryStatus.InconsistentState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Captured_provider_handle_index_is_reciprocal_and_valid_for_nonreconnectable_records(bool markNotReconnectable)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        var attempt = await fixture.AddProviderAttemptAsync(captureHandle: true, markNotReconnectable: markNotReconnectable, ct: ct);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(RepositoryRecoveryStatus.Ready);
        var damage = markNotReconnectable
            ? $"UPDATE adaptive_provider_handles SET handle='different-handle' WHERE attempt_key='{attempt.AttemptKey}'"
            : $"DELETE FROM adaptive_provider_handles WHERE attempt_key='{attempt.AttemptKey}'";
        await fixture.MutateAsync(damage, ct);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(RepositoryRecoveryStatus.InconsistentState);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    public async Task Malformed_captured_provider_record_refuses(string document)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        var attempt = await fixture.AddProviderAttemptAsync(captureHandle: true, markNotReconnectable: true, ct: ct);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(RepositoryRecoveryStatus.Ready);
        await fixture.MutateAsync($"UPDATE adaptive_delegation_attempts SET document='{document}' WHERE attempt_key='{attempt.AttemptKey}'", ct);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(RepositoryRecoveryStatus.InconsistentState);
    }
}

internal sealed class RecoveryIntegrityFixture : IDisposable
{
    private sealed class Ports : IActivityExecutor, IContextProvider, IInferenceExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    public string Root { get; }
    public string DatabasePath => Path.Combine(Root, "execution.db");
    public string CheckoutPath => Path.Combine(Root, "checkout");
    public Guid WorkflowId { get; }
    public SqliteDatabase Database { get; private set; } = null!;
    public SqliteAdaptiveRevisionRepository Revisions { get; private set; } = null!;
    public SqliteRepositoryEffectJournal Effects { get; private set; } = null!;
    public SqliteRepositoryEffectGraphJournal Graphs { get; private set; } = null!;
    public SqliteSupervisorContinuityStore Continuity { get; private set; } = null!;
    public SupervisorCatchUp CatchUp { get; private set; } = null!;
    public ControlledRepository Repository { get; private set; } = null!;
    public RepositoryEffectGraph Graph { get; private set; } = null!;
    public AdaptiveProposal Proposal { get; private set; } = null!;
    private string ApprovalEvidenceId { get; } = "approval-fixture";
    private DateTimeOffset ApprovalExpiresAt { get; set; }
    private AdaptiveRevision initial = null!;
    private readonly Ports ports = new();
    private readonly string[] initialText = ["before-a", "before-b"];
    private CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private RecoveryIntegrityFixture(Guid? workflowId = null)
    {
        WorkflowId = workflowId ?? Guid.NewGuid();
        Root = Path.Combine(Path.GetTempPath(), "marang-recovery-integrity-" + Guid.NewGuid().ToString("N"));
    }

    public static async Task<RecoveryIntegrityFixture> CreateAsync(Guid? workflowId = null, bool prepareProposal = true)
    {
        var fixture = new RecoveryIntegrityFixture(workflowId);
        try { await fixture.InitializeAsync(prepareProposal); return fixture; }
        catch { fixture.Dispose(); throw; }
    }

    private async Task InitializeAsync(bool prepareProposal)
    {
        Directory.CreateDirectory(Path.Combine(CheckoutPath, "src"));
        File.WriteAllText(Path.Combine(CheckoutPath, "src", "a.txt"), initialText[0]);
        File.WriteAllText(Path.Combine(CheckoutPath, "src", "b.txt"), initialText[1]);
        RunGit("init", "-q"); RunGit("config", "user.email", "tests@example.invalid"); RunGit("config", "user.name", "Marang Tests");
        RunGit("add", "src"); RunGit("commit", "-qm", "initial");

        Database = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = DatabasePath, Pooling = false });
        Revisions = new(Database); await Revisions.InitializeAsync();
        Effects = new(Database); await Effects.InitializeAsync();
        Graphs = new(Database, Effects); await Graphs.InitializeAsync();
        Continuity = new(Database, Revisions, _ => true); await Continuity.InitializeAsync(TestCancellation);
        CatchUp = new(Database, _ => true); await CatchUp.InitializeAsync(TestCancellation);
        await new SqliteAdaptiveSupervisorAuthority(Database, Revisions,
            new UnusedAuthorizer(), new AdaptiveAuthorityProfile("tenant", "supervisor", "workspace", "reports/readiness.md", "write-file", "fence"), _ => true).InitializeAsync();
        await new SqliteDelegationRecoveryJournal(Database).InitializeAsync();

        Repository = ControlledRepository.Open(CheckoutPath, ["src/a.txt", "src/b.txt"]);
        var initialAuthored = await AdaptivePlanAuthoring.AuthorAsync(WorkflowId, "r1", null, "inspect repo", "initial", "test",
            [new("inventory", "Inventory", "inspect files", "input", AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "source", [], Guid.NewGuid(), true),
             new("analysis", "Analysis", "analyze files", "input", AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "source", [], Guid.NewGuid(), true)],
            new FuwenZhinuExecutionPorts(ports, ports, ports), cancellationToken: TestCancellation);
        initial = initialAuthored.Revision;
        await Revisions.ActivateInitialAsync(initial, TestCancellation);
        await Continuity.RecordIntentAsync(WorkflowId, "r1", "inspect repo", SupervisorIntentTrigger.Initial,
            "initial workflow intent", [], "operator", TestCancellation);
        if (!prepareProposal) return;

        var baseState = await Repository.ObserveAsync(TestCancellation);
        var effectIntent = RepositoryEffectIntent.Create(Guid.NewGuid(), WorkflowId, "r2", "write", Guid.NewGuid(), "attempt-1",
            Repository.RepositoryId, "workspace", baseState,
            [RepositoryPatchFile.Create("src/a.txt", Encoding.UTF8.GetBytes(initialText[0]), "after-a"u8),
             RepositoryPatchFile.Create("src/b.txt", Encoding.UTF8.GetBytes(initialText[1]), "after-b"u8)], DateTimeOffset.UtcNow);
        var causal = RepositoryCausalEffect.Create(effectIntent, [], "write both tracked files");
        Graph = RepositoryEffectGraph.Create(WorkflowId, "r2", "r1", Repository.RepositoryId, baseState,
            [new("write", causal, false, "write files")]);
        await Graphs.PrepareGraphAsync(Graph, TestCancellation);

        var specs = new[]
        {
            new AdaptiveWorkSpec("inventory", "Inventory", "inspect files", "input", AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "source", [], initial.Branches[0].Execution.Id, true),
            new AdaptiveWorkSpec("analysis", "Analysis", "analyze files", "input", AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "source", [], initial.Branches[1].Execution.Id, true),
            new AdaptiveWorkSpec("write", "Write", causal.CausalDigest, causal.CausalDigest,
                AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "causal-graph", [], effectIntent.ExecutionId, false) { IsRepositoryEffect = true }
        };
        var authored = await AdaptivePlanAuthoring.AuthorEffectGraphAsync(WorkflowId, "r2", "r1", "write files", "test graph", "test",
            specs, Graph, new FuwenZhinuExecutionPorts(ports, ports, ports), initialAuthored.Registry, TestCancellation);
        Proposal = new AdaptiveProposal(Guid.NewGuid(), authored.Revision, await Revisions.GetStateDigestAsync(WorkflowId, TestCancellation),
            AdaptiveRevisionContracts.Compare(initial, authored.Revision), AdaptiveAuthorityDecision.ApprovalRequired, "test authority", "test impact");
        await Revisions.ProposeAsync(Proposal, TestCancellation);
        var proposalDigest = AdaptiveRevisionContracts.Digest(Proposal);
        var approvedAt = DateTimeOffset.UtcNow;
        ApprovalExpiresAt = approvedAt.AddHours(1);
        var evidence = new AdaptiveRevisionApprovalEvidence(ApprovalEvidenceId, "tenant", "operator", WorkflowId.ToString("D"),
            "r1", Proposal.ParentStateDigest, proposalDigest, approvedAt, ApprovalExpiresAt);
        await using var approvalConnection = await Database.OpenAsync(TestCancellation);
        await using var approval = approvalConnection.CreateCommand();
        approval.CommandText = "INSERT INTO adaptive_operator_approvals(evidence_id,proposal_id,digest,document) VALUES($e,$p,$d,$j)";
        approval.Parameters.AddWithValue("$e", evidence.EvidenceId); approval.Parameters.AddWithValue("$p", Proposal.ProposalId.ToString("D"));
        approval.Parameters.AddWithValue("$d", proposalDigest); approval.Parameters.AddWithValue("$j", JsonSerializer.Serialize(evidence));
        await approval.ExecuteNonQueryAsync(TestCancellation);

        await Continuity.BeginDiscussionAsync(WorkflowId, "Confirm the repository inventory scope.", ["scope"],
            ["inventory"], ["Proceed"], [], "operator", TestCancellation);
        await using var evidenceConnection = await Database.OpenAsync(TestCancellation);
        await using var evidenceCommand = evidenceConnection.CreateCommand();
        evidenceCommand.CommandText = "SELECT COALESCE(MAX(sequence),0) FROM adaptive_evidence WHERE workflow_id=$w";
        evidenceCommand.Parameters.AddWithValue("$w", WorkflowId.ToString("D"));
        var watermark = Convert.ToInt64(await evidenceCommand.ExecuteScalarAsync(TestCancellation));
        await Continuity.AcknowledgeAsync(WorkflowId, "operator", "r1", watermark, TestCancellation);
        await CatchUp.CaptureProposalContextAsync(Proposal, "operator", TestCancellation);
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        var digest = AdaptiveRevisionContracts.Digest(Proposal);
        await Revisions.ApproveAsync(Proposal.ProposalId, digest, "operator", ApprovalEvidenceId, ApprovalExpiresAt, ct);
        await Revisions.CommitAsync(Proposal.ProposalId, digest, ct);
    }

    public async Task MutateAsync(string sql, CancellationToken ct)
    {
        await using var connection = await Database.OpenAsync(ct);
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<(string AttemptKey, string Provider, string Handle)> AddProviderAttemptAsync(bool captureHandle,
        bool markNotReconnectable, CancellationToken ct, bool bind = true)
    {
        var delegationId = new Penghou.Qingniao.DelegationId(Guid.NewGuid());
        var agent = new Penghou.Qingniao.ExternalAgentReference("a2a", "recovery-fixture", "a2a-0.3");
        var workflowRun = new Penghou.Qingniao.WorkflowRunExecutionReference("qingniao", Guid.NewGuid().ToString("D"), "fixture-epoch");
        var node = new Penghou.Qingniao.StructuralNodeReference("provider-fixture");
        var generation = new Penghou.Qingniao.NodeGenerationId(Guid.NewGuid());
        var correlation = new Penghou.Qingniao.ExternalOperationCorrelation(delegationId, workflowRun, node, generation, "attempt-1", agent);
        var identity = new Penghou.Qingniao.ExternalOperationStartIdentity(delegationId, workflowRun, node, generation,
            "attempt-1", "fixture-idempotency", new string('c', 64));
        var request = new Penghou.Qingniao.ExternalOperationStartRequest(identity, correlation, "agent.execute", []);
        var journal = new SqliteDelegationRecoveryJournal(Database);
        await journal.TryCreateIntentAsync(new Penghou.Qingniao.ExternalOperationRecoveryIntent(request, DateTimeOffset.UtcNow), ct);
        if (bind) await AddBranchBindingAsync(journal, delegationId, ct);
        var provider = agent.Provider;
        var handleValue = "fixture-task-" + Guid.NewGuid().ToString("N");
        if (captureHandle)
        {
            var capturedCorrelation = new Penghou.Qingniao.ExternalOperationCorrelation(delegationId, workflowRun, node, generation,
                "attempt-1", agent, new Penghou.Qingniao.ExternalTaskReference(agent.Provider, handleValue));
            var handle = new Penghou.Qingniao.ExternalOperationHandle(agent.Provider, handleValue, "a2a-0.3", capturedCorrelation);
            await journal.CaptureAsync(identity, new Penghou.Qingniao.ExternalOperationHandleCapture(handle, DateTimeOffset.UtcNow), ct);
            if (markNotReconnectable) await journal.MarkKnownNotReconnectableAsync(identity, ct);
        }
        var attemptKey = AdaptiveRevisionContracts.Digest(new
        {
            identity.DelegationId,
            identity.WorkflowRun,
            identity.StructuralNode,
            identity.NodeGeneration,
            identity.ExecutionAttemptId
        });
        return (attemptKey, provider, handleValue);
    }

    public async Task AddAcceptedBindingWithoutAttemptAsync(CancellationToken ct)
    {
        var active = await Revisions.GetActiveAsync(WorkflowId, ct);
        var branch = active.Branches.Single(b => b.LogicalId == "inventory");
        var delegationId = new Penghou.Qingniao.DelegationId(Guid.NewGuid());
        var journal = new SqliteDelegationRecoveryJournal(Database);
        await journal.BindAsync(branch.Execution.Id,
            new Penghou.Qingniao.DelegationHandle(delegationId,
                new Penghou.Qingniao.WorkflowReference("zhinu", branch.Execution.Id.ToString("D")),
                Penghou.Qingniao.DelegationState.Queued), branch.InputDigest, ct);
    }

    private async Task AddBranchBindingAsync(SqliteDelegationRecoveryJournal journal,
        Penghou.Qingniao.DelegationId delegationId, CancellationToken ct)
    {
        var active = await Revisions.GetActiveAsync(WorkflowId, ct);
        var branch = active.Branches.Single(b => b.LogicalId == "inventory");
        await journal.BindAsync(branch.Execution.Id,
            new Penghou.Qingniao.DelegationHandle(delegationId,
                new Penghou.Qingniao.WorkflowReference("zhinu", branch.Execution.Id.ToString("D")),
                Penghou.Qingniao.DelegationState.Queued), branch.InputDigest, ct);
    }

    public async Task RebindJournalIntentAsync(CancellationToken ct)
    {
        var current = (await Effects.GetAsync(Graph.Effects[0].Effect.Intent.EffectId, ct))!;
        var old = current.Intent;
        var replacement = RepositoryEffectIntent.Create(old.EffectId, old.WorkflowId, old.RevisionId, old.LogicalStepId,
            old.ExecutionId, "different-attempt", old.RepositoryId, old.AuthorityScope, old.BaseState, old.Files,
            old.CreatedAt.AddSeconds(1));
        var rebound = current with { Intent = replacement };
        await using var connection = await Database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE repository_effects SET intent_digest=$d,document=$j WHERE effect_id=$id";
        command.Parameters.AddWithValue("$d", replacement.IntentDigest);
        command.Parameters.AddWithValue("$j", JsonSerializer.Serialize(rebound, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        command.Parameters.AddWithValue("$id", replacement.EffectId.ToString("D"));
        (await command.ExecuteNonQueryAsync(ct)).Should().Be(1);
    }

    private void RunGit(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = CheckoutPath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(stdout + stderr);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
    }

    private sealed class UnusedAuthorizer : IAuthorityRequestAuthorizer
    {
        public ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The recovery fixture does not execute Hufu authorization.");
    }
}
