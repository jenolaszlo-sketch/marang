using System.Text.Json;
using FluentAssertions;
using Marang.Runs.Adaptive;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Tests;

public sealed class SupervisorCatchUpTests
{
    [Fact]
    public async Task Catch_up_derives_current_intent_decisions_and_authority_without_chat_replay()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("inventory");
        var discussion = await fixture.Continuity.BeginDiscussionAsync(fixture.Id, "Include API compatibility in review?",
            ["public consumers to review"], ["review"], ["proceed", "keep current"], ["repository:README"], "operator", TestContext.Current.CancellationToken);
        var view = await fixture.Projection.ReadAsync(fixture.Id, "operator", TestContext.Current.CancellationToken);
        view.ActiveRevisionId.Should().Be("r1"); view.Goal.Should().Be("Review repository contracts");
        view.State.Should().Be(DurableSupervisorState.Discussing);
        view.Decisions.Single().Id.Should().Be(discussion.DecisionId);
        view.Decisions.Single().MissingInformation.Should().Contain("public consumers to review");
        view.Branches.Single(b => b.LogicalId == "inventory").EvidenceCategory.Should().Be(SupervisorEvidenceCategory.CurrentAuthoritative);
        view.Branches.Single(b => b.LogicalId == "review").EvidenceCategory.Should().Be(SupervisorEvidenceCategory.Historical);
        view.Blockers.Should().Contain(b => b.Contains("waits for Review", StringComparison.Ordinal));
        var denied = () => fixture.Projection.ReadAsync(fixture.Id, "foreign", TestContext.Current.CancellationToken);
        await denied.Should().ThrowAsync<WorkflowAuthorizationException>();
    }

    [Fact]
    public async Task Reused_completed_result_and_fenced_prior_result_are_distinct_after_revision()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("inventory"); await fixture.ExecuteAsync("review");
        var candidate = await fixture.CandidateAsync();
        var proposal = await fixture.ProposeAsync(candidate);
        await fixture.Projection.CaptureProposalContextAsync(proposal, "operator", TestContext.Current.CancellationToken);
        await fixture.Continuity.RecordIntentAsync(fixture.Id, "r2", "Review repository contracts", SupervisorIntentTrigger.User,
            "Add public API review", ["repository:public-api"], "operator", TestContext.Current.CancellationToken);
        await fixture.Revisions.ApproveAsync(proposal.ProposalId, AdaptiveRevisionContracts.Digest(proposal), "operator", "fixture-authority", DateTimeOffset.UtcNow.AddMinutes(5), TestContext.Current.CancellationToken);
        await fixture.Revisions.CommitAsync(proposal.ProposalId, AdaptiveRevisionContracts.Digest(proposal), TestContext.Current.CancellationToken);
        var view = await fixture.Projection.ReadAsync(fixture.Id, "operator", TestContext.Current.CancellationToken);
        view.Branches.Single(b => b.LogicalId == "inventory").EvidenceCategory.Should().Be(SupervisorEvidenceCategory.CurrentAuthoritative);
        var prior = view.PriorResults.Single(b => b.LogicalId == "review");
        prior.HasResult.Should().BeTrue(); prior.EvidenceCategory.Should().Be(SupervisorEvidenceCategory.Superseded);
        var detail = await fixture.Projection.DrillDownAsync(fixture.Id, "r2", "operator", TestContext.Current.CancellationToken);
        detail.Trigger.Should().Be("User"); detail.EvidenceReferences.Should().Contain("repository:public-api");
        detail.Work.Single(b => b.LogicalId == "inventory").Classifications.Should().Contain(["Preserved", "Reused"]);
        detail.Work.Single(b => b.LogicalId == "review").Classifications.Should().Contain(["Replaced", "Fenced", "Invalidated"]);
        detail.Work.Single(b => b.LogicalId == "report").Classifications.Should().Contain("DependencyChanged");
    }

    [Fact]
    public async Task Later_completion_is_not_retroactively_claimed_as_completed_reuse()
    {
        using var fixture = await Fixture.CreateAsync();
        var proposal = await fixture.ProposeAsync(await fixture.CandidateAsync());
        await fixture.Projection.CaptureProposalContextAsync(proposal, "operator", TestContext.Current.CancellationToken);
        await fixture.Revisions.ApproveAsync(proposal.ProposalId, AdaptiveRevisionContracts.Digest(proposal), "operator", "fixture-authority", DateTimeOffset.UtcNow.AddMinutes(5), TestContext.Current.CancellationToken);
        await fixture.Revisions.CommitAsync(proposal.ProposalId, AdaptiveRevisionContracts.Digest(proposal), TestContext.Current.CancellationToken);
        await fixture.ExecuteAsync("inventory");
        var detail = await fixture.Projection.DrillDownAsync(fixture.Id, "r2", "operator", TestContext.Current.CancellationToken);
        detail.Work.Single(b => b.LogicalId == "inventory").Classifications.Should().Contain("Preserved").And.NotContain("Reused");
    }

    [Fact]
    public async Task Progress_invalidates_reviewed_proposal_and_discussion_but_does_not_commit_them()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Continuity.BeginDiscussionAsync(fixture.Id, "Change review?", [], ["review"], ["proceed"], [], "operator", TestContext.Current.CancellationToken);
        var proposal = await fixture.ProposeAsync(await fixture.CandidateAsync());
        await fixture.ExecuteAsync("inventory");
        var view = await fixture.Projection.ReadAsync(fixture.Id, "operator", TestContext.Current.CancellationToken);
        view.State.Should().Be(DurableSupervisorState.Blocked); view.ActiveRevisionId.Should().Be("r1");
        view.Decisions.Should().HaveCount(2).And.OnlyContain(d => d.IsStale);
        var capture = () => fixture.Projection.CaptureProposalContextAsync(proposal, "operator", TestContext.Current.CancellationToken);
        await capture.Should().ThrowAsync<WorkflowStateException>();
    }

    [Fact]
    public async Task Checkpoint_delta_survives_reopen_and_suppresses_bookkeeping_noise()
    {
        using var fixture = await Fixture.CreateAsync();
        var before = await fixture.Projection.ReadAsync(fixture.Id, "operator", TestContext.Current.CancellationToken);
        await fixture.Continuity.AcknowledgeAsync(fixture.Id, "operator", before.ActiveRevisionId, before.EvidenceWatermark, TestContext.Current.CancellationToken);
        await fixture.Continuity.BeginDiscussionAsync(fixture.Id, "Add security review?", [], ["review"], ["discuss"], [], "operator", TestContext.Current.CancellationToken);
        var freshProjection = new SupervisorCatchUp(new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = fixture.Path, Pooling = false }), actor => actor == "operator");
        var after = await freshProjection.ReadAsync(fixture.Id, "operator", TestContext.Current.CancellationToken);
        after.LastAcknowledgedRevision.Should().Be("r1");
        after.ChangesSinceCheckpoint.Should().ContainSingle().Which.Kind.Should().Be("SupervisorDiscussionOpened");
    }

    [Fact]
    public async Task Cancelled_workflow_has_no_current_authoritative_results()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("inventory");
        await fixture.Revisions.CancelAsync(fixture.Id, "operator", TestContext.Current.CancellationToken);
        var view = await fixture.Projection.ReadAsync(fixture.Id, "operator", TestContext.Current.CancellationToken);
        view.State.Should().Be(DurableSupervisorState.Cancelled);
        view.Branches.Should().NotContain(b => b.EvidenceCategory == SupervisorEvidenceCategory.CurrentAuthoritative);
    }

    // These are projection/conformance tests over real Fuwen/Zhinu executions. Authority qualification uses
    // the separate Cedar/Hufu recovery tests and process probes; fixture-authority is not a permission claim.
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "marang-catchup-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(directory, "execution.db");
        public Guid Id { get; } = Guid.NewGuid();
        public SqliteAdaptiveRevisionRepository Revisions { get; private set; } = null!;
        public SqliteSupervisorContinuityStore Continuity { get; private set; } = null!;
        public SupervisorCatchUp Projection { get; private set; } = null!;
        private SqliteWorkflowStore store = null!;
        private WorkflowRegistry registry = null!;
        private readonly Ports ports = new();
        private AdaptiveWorkSpec[] specs = null!;
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(); Directory.CreateDirectory(f.directory);
            var database = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = f.Path, Pooling = false });
            f.Revisions = new(database); await f.Revisions.InitializeAsync();
            await new SqliteDelegationRecoveryJournal(database).InitializeAsync();
            f.store = new(database); f.Continuity = new(database, f.Revisions, actor => actor == "operator");
            f.Projection = new(database, actor => actor == "operator");
            await f.Continuity.InitializeAsync(); await f.Projection.InitializeAsync();
            f.specs = [new("inventory", "Inventory", "inventory", "snapshot", "authority", "source", [], Guid.NewGuid(), true),
                new("review", "Review", "read contracts", "snapshot", "authority", "source", [], Guid.NewGuid(), true),
                new("report", "Report", "combine", "snapshot", "authority", "source", ["inventory", "review"], Guid.NewGuid(), true)];
            var authored = await AdaptivePlanAuthoring.AuthorAsync(f.Id, "r1", null, "Review repository contracts", "initial", "fixture", f.specs,
                new FuwenZhinuExecutionPorts(f.ports, f.ports, f.ports));
            f.registry = authored.Registry; await f.Revisions.ActivateInitialAsync(authored.Revision);
            await f.Continuity.RecordIntentAsync(f.Id, "r1", "Review repository contracts", SupervisorIntentTrigger.Initial, "initial", [], "operator", TestContext.Current.CancellationToken);
            return f;
        }
        public async Task<AdaptiveRevision> CandidateAsync()
        {
            var next = specs.ToArray(); next[1] = next[1] with { Requirement = "read public API compatibility", ExecutionId = Guid.NewGuid() };
            next[2] = next[2] with { ExecutionId = Guid.NewGuid() };
            return (await AdaptivePlanAuthoring.AuthorAsync(Id, "r2", "r1", "Review repository contracts", "Add public API review", "fixture", next,
                new FuwenZhinuExecutionPorts(ports, ports, ports), registry)).Revision;
        }
        public async Task<AdaptiveProposal> ProposeAsync(AdaptiveRevision candidate)
        {
            var active = await Revisions.GetActiveAsync(Id);
            var proposal = new AdaptiveProposal(Guid.NewGuid(), candidate, await Revisions.GetStateDigestAsync(Id), AdaptiveRevisionContracts.Compare(active, candidate),
                AdaptiveAuthorityDecision.ApprovalRequired, "fixture", "Review and dependent report are replaced.");
            await Revisions.ProposeAsync(proposal); return proposal;
        }
        public async Task ExecuteAsync(string logicalId)
        {
            var branch = (await Revisions.GetActiveAsync(Id)).Branches.Single(b => b.LogicalId == logicalId);
            await using var engine = new WorkflowEngine(store, registry);
            await engine.StartAsync(branch.Execution.WorkflowName, branch.Execution.WorkflowVersion, JsonSerializer.Deserialize<JsonElement>(branch.Execution.InputJson!), branch.Execution.Id);
            await engine.ExecuteAsync(branch.Execution.Id);
            (await store.GetRunAsync(branch.Execution.Id))!.Status.Should().Be(WorkflowStatus.Completed);
        }
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
    private sealed class Ports : IActivityExecutor, IContextProvider, IInferenceExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(JsonSerializer.SerializeToElement("repository result"))));
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
