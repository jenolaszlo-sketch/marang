using FluentAssertions;
using Marang.Runs.Adaptive;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Tests;

public sealed class SupervisorContinuityStoreTests
{
    [Fact]
    public async Task Intent_discussion_and_actor_checkpoint_survive_reopen_without_mutating_intent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TemporaryDatabase();
        var (database, revisions, authored) = await CreateWorkflow(db.Path, ct);
        var store = new SqliteSupervisorContinuityStore(database, revisions, actor => actor is "alice" or "bob");
        await store.InitializeAsync(ct);
        var intent = await store.RecordIntentAsync(authored.Revision.WorkflowId, "r1", "goal",
            SupervisorIntentTrigger.Initial, "initial objective", ["evidence:1"], "alice", ct);
        (await store.RecordIntentAsync(authored.Revision.WorkflowId, "r1", "goal",
            SupervisorIntentTrigger.Initial, "initial objective", ["evidence:1"], "alice", ct)).RecordedAt.Should().Be(intent.RecordedAt);
        var rewrite = () => store.RecordIntentAsync(authored.Revision.WorkflowId, "r1", "goal",
            SupervisorIntentTrigger.Initial, "rewritten rationale", ["evidence:1"], "alice", ct);
        await rewrite.Should().ThrowAsync<WorkflowStateException>();
        var discussion = await store.BeginDiscussionAsync(authored.Revision.WorkflowId, "Choose a report format",
            ["audience"], ["inventory"], ["brief", "detailed"], ["evidence:2"], "alice", ct);
        var evidence = await revisions.GetEvidenceAsync(authored.Revision.WorkflowId, ct);
        var observedWatermark = evidence.Select(x => System.Text.Json.JsonDocument.Parse(x).RootElement.GetProperty("Sequence").GetInt64()).Max();
        var checkpoint = await store.AcknowledgeAsync(authored.Revision.WorkflowId, "alice", "r1", observedWatermark, ct);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var reopenedDatabase = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = db.Path, Pooling = false });
        var reopenedRevisions = new SqliteAdaptiveRevisionRepository(reopenedDatabase);
        var reopenedStore = new SqliteSupervisorContinuityStore(reopenedDatabase, reopenedRevisions, actor => actor is "alice" or "bob");
        await reopenedRevisions.InitializeAsync(ct); await reopenedStore.InitializeAsync(ct);

        var reopenedIntent = (await reopenedStore.GetIntentAsync(authored.Revision.WorkflowId, "r1", ct))!;
        reopenedIntent.Goal.Should().Be(intent.Goal); reopenedIntent.RevisionDigest.Should().Be(intent.RevisionDigest);
        reopenedIntent.EvidenceReferences.Should().Equal(intent.EvidenceReferences);
        var reopenedDiscussion = (await reopenedStore.GetDiscussionAsync(authored.Revision.WorkflowId, ct))!;
        reopenedDiscussion.DecisionId.Should().Be(discussion.DecisionId); reopenedDiscussion.ParentStateDigest.Should().Be(discussion.ParentStateDigest);
        reopenedDiscussion.Options.Should().Equal(discussion.Options); reopenedDiscussion.IsStale.Should().BeFalse();
        (await reopenedStore.GetCheckpointAsync(authored.Revision.WorkflowId, "alice", ct)).Should().Be(checkpoint);
        (await reopenedStore.GetCheckpointAsync(authored.Revision.WorkflowId, "bob", ct)).Should().BeNull();
    }

    [Fact]
    public async Task Stale_discussion_is_reported_and_cannot_be_resolved()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TemporaryDatabase();
        var (database, revisions, authored) = await CreateWorkflow(db.Path, ct);
        var store = new SqliteSupervisorContinuityStore(database, revisions, actor => actor == "alice");
        await store.InitializeAsync(ct);
        var discussion = await store.BeginDiscussionAsync(authored.Revision.WorkflowId, "Choose a report format",
            ["audience"], ["inventory"], ["brief", "detailed"], [], "alice", ct);

        await using (var c = await database.OpenAsync(ct))
        await using (var q = c.CreateCommand())
        {
            q.CommandText = "INSERT INTO adaptive_execution_witness(run_id,witness_key,digest) VALUES($run,'continuity-change','new-durable-state')";
            q.Parameters.AddWithValue("$run", authored.Revision.Branches[0].Execution.Id.ToString("D"));
            await q.ExecuteNonQueryAsync(ct);
        }

        (await store.GetDiscussionAsync(authored.Revision.WorkflowId, ct))!.IsStale.Should().BeTrue();
        var resolve = () => store.ResolveDiscussionAsync(authored.Revision.WorkflowId, discussion.DecisionId,
            discussion.ParentStateDigest, "brief", "alice", ct);
        await resolve.Should().ThrowAsync<WorkflowStateException>();
        var dismissed = await store.DismissDiscussionAsync(authored.Revision.WorkflowId, discussion.DecisionId,
            "alice", "The workflow changed; refresh the decision.", ct);
        dismissed.Status.Should().Be(SupervisorDiscussionStatus.Dismissed);
        dismissed.Choice.Should().BeNull(); dismissed.DismissalReason.Should().Be("The workflow changed; refresh the decision.");
        var refreshed = await store.BeginDiscussionAsync(authored.Revision.WorkflowId, "Choose after refresh",
            ["audience"], ["inventory"], ["brief", "detailed"], [], "alice", ct);
        refreshed.ParentStateDigest.Should().NotBe(discussion.ParentStateDigest);
        refreshed.IsStale.Should().BeFalse();
    }

    [Fact]
    public async Task Checkpoints_are_actor_isolated_and_ack_requires_the_observed_watermark()
    {
        var ct = TestContext.Current.CancellationToken;
        using var db = new TemporaryDatabase();
        var (database, revisions, authored) = await CreateWorkflow(db.Path, ct);
        var store = new SqliteSupervisorContinuityStore(database, revisions, actor => actor == "alice" || actor == "bob");
        await store.InitializeAsync(ct);
        var evidence = await revisions.GetEvidenceAsync(authored.Revision.WorkflowId, ct);
        var watermark = evidence.Select(x => System.Text.Json.JsonDocument.Parse(x).RootElement.GetProperty("Sequence").GetInt64()).Max();
        var wrongWatermark = () => store.AcknowledgeAsync(authored.Revision.WorkflowId, "alice", "r1", watermark - 1, ct);
        await wrongWatermark.Should().ThrowAsync<WorkflowStateException>();
        await store.AcknowledgeAsync(authored.Revision.WorkflowId, "alice", "r1", watermark, ct);
        (await store.GetCheckpointAsync(authored.Revision.WorkflowId, "bob", ct)).Should().BeNull();
        var denied = () => store.GetCheckpointAsync(authored.Revision.WorkflowId, "mallory", ct);
        await denied.Should().ThrowAsync<WorkflowAuthorizationException>();
    }

    private static async Task<(SqliteDatabase Database, SqliteAdaptiveRevisionRepository Revisions, AuthoredAdaptiveRevision Authored)> CreateWorkflow(string path, CancellationToken ct)
    {
        var database = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = path, Pooling = false });
        var revisions = new SqliteAdaptiveRevisionRepository(database); await revisions.InitializeAsync(ct);
        var authored = await AdaptivePlanAuthoring.AuthorAsync(Guid.NewGuid(), "r1", null, "goal", "initial objective", "planner",
            [new("inventory", "Inventory", "collect inventory", "snapshot", "authority", "source", [], Guid.NewGuid(), true),
             new("assessment", "Assessment", "prepare report", "snapshot", "authority", "source", [], Guid.NewGuid(), true)],
            new FuwenZhinuExecutionPorts(new UnusedPorts(), new UnusedPorts(), new UnusedPorts()),
            cancellationToken: ct);
        await revisions.ActivateInitialAsync(authored.Revision, ct);
        return (database, revisions, authored);
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "marang-supervisor-continuity-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(directory, "continuity.db");
        public TemporaryDatabase() => Directory.CreateDirectory(directory);
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class UnusedPorts : IActivityExecutor, IContextProvider, IInferenceExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
