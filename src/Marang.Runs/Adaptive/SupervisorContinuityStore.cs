using System.Collections.Immutable;
using System.Text.Json;
using Marang.Hufu;
using Microsoft.Data.Sqlite;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Runs.Adaptive;

/// <summary>Durable, bounded supervisor decision metadata over the M1 revision store.</summary>
public sealed class SqliteSupervisorContinuityStore
{
    private const int MaxText = 4096;
    private const int MaxItems = 32;
    private readonly IZhinuSqliteDatabase database;
    private readonly SqliteAdaptiveRevisionRepository revisions;
    private readonly Func<string, bool> isAuthorizedOperator;

    public SqliteSupervisorContinuityStore(IZhinuSqliteDatabase database,
        SqliteAdaptiveRevisionRepository revisions, Func<string, bool> isAuthorizedOperator)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        this.revisions = revisions ?? throw new ArgumentNullException(nameof(revisions));
        this.isAuthorizedOperator = isAuthorizedOperator ?? throw new ArgumentNullException(nameof(isAuthorizedOperator));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await revisions.InitializeAsync(cancellationToken);
        await database.EnsureInitializedAsync(cancellationToken);
        await using var c = await database.OpenAsync(cancellationToken);
        await using var q = c.CreateCommand();
        q.CommandText = """
            CREATE TABLE IF NOT EXISTS supervisor_revision_intents (
              workflow_id TEXT NOT NULL, revision_id TEXT NOT NULL, revision_digest TEXT NOT NULL, goal TEXT NOT NULL, trigger TEXT NOT NULL,
              rationale TEXT NOT NULL, evidence_refs_json TEXT NOT NULL, recorded_by TEXT NOT NULL, recorded_at TEXT NOT NULL,
              PRIMARY KEY(workflow_id,revision_id));
            CREATE TABLE IF NOT EXISTS supervisor_pending_discussions (
              workflow_id TEXT PRIMARY KEY, decision_id TEXT NOT NULL UNIQUE, parent_revision_id TEXT NOT NULL,
              parent_state_digest TEXT NOT NULL, summary TEXT NOT NULL, missing_json TEXT NOT NULL,
              affected_json TEXT NOT NULL, options_json TEXT NOT NULL, evidence_refs_json TEXT NOT NULL,
              status TEXT NOT NULL, choice TEXT, recorded_by TEXT NOT NULL, recorded_at TEXT NOT NULL,
              dismissal_reason TEXT);
            CREATE TABLE IF NOT EXISTS supervisor_operator_checkpoints (
              workflow_id TEXT NOT NULL, actor_id TEXT NOT NULL, revision_id TEXT NOT NULL,
              evidence_watermark INTEGER NOT NULL, acknowledged_at TEXT NOT NULL, PRIMARY KEY(workflow_id,actor_id));
            """;
        await q.ExecuteNonQueryAsync(cancellationToken);
        await using var columns = c.CreateCommand(); columns.CommandText = "PRAGMA table_info(supervisor_pending_discussions)";
        await using var reader = await columns.ExecuteReaderAsync(cancellationToken);
        var hasDismissalReason = false;
        while (await reader.ReadAsync(cancellationToken)) hasDismissalReason |= reader.GetString(1) == "dismissal_reason";
        await reader.DisposeAsync();
        if (!hasDismissalReason)
        {
            await using var migrate = c.CreateCommand(); migrate.CommandText = "ALTER TABLE supervisor_pending_discussions ADD COLUMN dismissal_reason TEXT";
            await migrate.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<SupervisorRevisionIntent> RecordIntentAsync(Guid workflow, string revision, string goal,
        SupervisorIntentTrigger trigger, string rationale, IEnumerable<string> refs, string actor,
        CancellationToken cancellationToken = default)
    {
        Authorize(actor);
        RequireGuid(workflow); revision = Text(revision, 256, nameof(revision)); goal = Text(goal, MaxText, nameof(goal));
        rationale = Text(rationale, MaxText, nameof(rationale));
        if (!Enum.IsDefined(trigger)) throw new ArgumentOutOfRangeException(nameof(trigger));
        var evidenceRefs = Items(refs, nameof(refs), 512);
        var candidate = await FindRevisionOrPendingCandidate(workflow, revision, cancellationToken);
        if (candidate is null) throw new WorkflowStateException("Intent must bind to an existing or pending exact revision.");
        ValidateGoal(candidate, goal);
        var revisionDigest = AdaptiveRevisionContracts.Digest(candidate);
        var now = database.TimeProvider.GetUtcNow();
        var result = new SupervisorRevisionIntent(workflow, revision, goal, revisionDigest, trigger, rationale, evidenceRefs, actor, now);
        await using var c = await database.OpenAsync(cancellationToken);
        using var t = c.BeginTransaction();
        var bound = await FindBoundRevisionAsync(c, t, workflow, revision, cancellationToken);
        if (bound is null || AdaptiveRevisionContracts.Digest(bound) != revisionDigest)
            throw new WorkflowStateException("Revision intent target changed before the metadata could be recorded.");
        await using var q = c.CreateCommand(); q.Transaction = t;
        q.CommandText = "SELECT revision_digest,goal,trigger,rationale,evidence_refs_json,recorded_by FROM supervisor_revision_intents WHERE workflow_id=$w AND revision_id=$r";
        q.Parameters.AddWithValue("$w", workflow.ToString("D")); q.Parameters.AddWithValue("$r", revision);
        var prior = await q.ExecuteReaderAsync(cancellationToken);
        if (await prior.ReadAsync(cancellationToken))
        {
            var same = prior.GetString(0) == revisionDigest && prior.GetString(1) == goal && prior.GetString(2) == trigger.ToString() &&
                prior.GetString(3) == rationale && prior.GetString(4) == JsonSerializer.Serialize(evidenceRefs) && prior.GetString(5) == actor;
            await prior.DisposeAsync();
            if (!same) throw new WorkflowStateException("Revision intent is immutable and already has different metadata.");
            t.Rollback(); return await GetIntentAsync(workflow, revision, cancellationToken) ?? throw new WorkflowStateException("Stored intent lost its revision binding.");
        }
        await prior.DisposeAsync();
        q.Parameters.Clear();
        q.CommandText = "INSERT INTO supervisor_revision_intents(workflow_id,revision_id,revision_digest,goal,trigger,rationale,evidence_refs_json,recorded_by,recorded_at) VALUES($w,$r,$rd,$g,$t,$n,$e,$a,$at)";
        q.Parameters.AddWithValue("$w", workflow.ToString("D")); q.Parameters.AddWithValue("$r", revision);
        q.Parameters.AddWithValue("$rd", revisionDigest); q.Parameters.AddWithValue("$g", goal); q.Parameters.AddWithValue("$t", trigger.ToString()); q.Parameters.AddWithValue("$n", rationale);
        q.Parameters.AddWithValue("$e", JsonSerializer.Serialize(evidenceRefs)); q.Parameters.AddWithValue("$a", actor); q.Parameters.AddWithValue("$at", now.ToString("O"));
        await q.ExecuteNonQueryAsync(cancellationToken);
        await Evidence(c, t, workflow, "SupervisorIntentRecorded", result, cancellationToken);
        t.Commit(); return result;
    }

    public async Task<SupervisorPendingDiscussion> BeginDiscussionAsync(Guid workflow, string summary,
        IEnumerable<string> missing, IEnumerable<string> affectedLogicalIds, IEnumerable<string> options,
        IEnumerable<string> refs, string actor, CancellationToken cancellationToken = default)
    {
        Authorize(actor); RequireGuid(workflow); summary = Text(summary, MaxText, nameof(summary));
        var missingItems = Items(missing, nameof(missing)); var affected = Items(affectedLogicalIds, nameof(affectedLogicalIds), 256);
        var optionItems = Items(options, nameof(options)); var evidenceRefs = Items(refs, nameof(refs), 512);
        if (optionItems.Length == 0) throw new ArgumentException("At least one bounded option is required.", nameof(options));
        var active = await revisions.GetActiveAsync(workflow, cancellationToken);
        var digest = await revisions.GetStateDigestAsync(workflow, cancellationToken);
        if (affected.Any(id => active.Branches.All(branch => branch.LogicalId != id)))
            throw new ArgumentException("Affected logical ids must identify branches in the captured parent revision.", nameof(affectedLogicalIds));
        var now = database.TimeProvider.GetUtcNow();
        var result = new SupervisorPendingDiscussion(workflow, Guid.NewGuid().ToString("N"), active.RevisionId,
            digest, summary, missingItems, affected, optionItems, evidenceRefs, SupervisorDiscussionStatus.Pending,
            false, null, actor, now);
        await using var c = await database.OpenAsync(cancellationToken); using var t = c.BeginTransaction();
        var snapshot = await SupervisorCatchUp.ReadActiveStateAsync(c, t, workflow, cancellationToken);
        if (snapshot.Cancelled || snapshot.Active.RevisionId != active.RevisionId || snapshot.StateDigest != digest)
            throw new WorkflowStateException("Workflow state changed before the discussion could be stored.");
        await using var q = c.CreateCommand(); q.Transaction = t;
        q.CommandText = "SELECT status FROM supervisor_pending_discussions WHERE workflow_id=$w";
        q.Parameters.AddWithValue("$w", workflow.ToString("D"));
        var priorStatus = await q.ExecuteScalarAsync(cancellationToken) as string;
        if (priorStatus == nameof(SupervisorDiscussionStatus.Pending)) throw new WorkflowStateException("A pending discussion must be resolved before another one is opened.");
        q.Parameters.Clear();
        q.CommandText = "INSERT INTO supervisor_pending_discussions(workflow_id,decision_id,parent_revision_id,parent_state_digest,summary,missing_json,affected_json,options_json,evidence_refs_json,status,choice,recorded_by,recorded_at,dismissal_reason) VALUES($w,$i,$p,$d,$s,$m,$a,$o,$e,'Pending',NULL,$by,$at,NULL) ON CONFLICT(workflow_id) DO UPDATE SET decision_id=excluded.decision_id,parent_revision_id=excluded.parent_revision_id,parent_state_digest=excluded.parent_state_digest,summary=excluded.summary,missing_json=excluded.missing_json,affected_json=excluded.affected_json,options_json=excluded.options_json,evidence_refs_json=excluded.evidence_refs_json,status='Pending',choice=NULL,recorded_by=excluded.recorded_by,recorded_at=excluded.recorded_at,dismissal_reason=NULL";
        q.Parameters.AddWithValue("$w", workflow.ToString("D")); q.Parameters.AddWithValue("$i", result.DecisionId);
        q.Parameters.AddWithValue("$p", result.ParentRevisionId); q.Parameters.AddWithValue("$d", digest); q.Parameters.AddWithValue("$s", summary);
        q.Parameters.AddWithValue("$m", JsonSerializer.Serialize(missingItems)); q.Parameters.AddWithValue("$a", JsonSerializer.Serialize(affected));
        q.Parameters.AddWithValue("$o", JsonSerializer.Serialize(optionItems)); q.Parameters.AddWithValue("$e", JsonSerializer.Serialize(evidenceRefs));
        q.Parameters.AddWithValue("$by", actor); q.Parameters.AddWithValue("$at", now.ToString("O")); await q.ExecuteNonQueryAsync(cancellationToken);
        await Evidence(c, t, workflow, "SupervisorDiscussionOpened", result, cancellationToken); t.Commit(); return result;
    }

    public async Task<SupervisorPendingDiscussion?> GetDiscussionAsync(Guid workflow, CancellationToken cancellationToken = default)
    {
        RequireGuid(workflow);
        await using var c = await database.OpenAsync(cancellationToken); using var t = c.BeginTransaction();
        var stored = await ReadDiscussion(c, t, workflow, cancellationToken); if (stored is null) return null;
        var snapshot = await SupervisorCatchUp.ReadActiveStateAsync(c, t, workflow, cancellationToken);
        t.Commit();
        return stored with
        {
            IsStale = stored.Status == SupervisorDiscussionStatus.Pending &&
            (stored.ParentRevisionId != snapshot.Active.RevisionId || stored.ParentStateDigest != snapshot.StateDigest || snapshot.Cancelled)
        };
    }

    public async Task<SupervisorPendingDiscussion> ResolveDiscussionAsync(Guid workflow, string decisionId,
        string expectedStateDigest, string choice, string actor, CancellationToken cancellationToken = default)
    {
        Authorize(actor); RequireGuid(workflow); decisionId = Text(decisionId, 128, nameof(decisionId));
        expectedStateDigest = Text(expectedStateDigest, 256, nameof(expectedStateDigest)); choice = Text(choice, 1024, nameof(choice));
        var now = database.TimeProvider.GetUtcNow();
        await using var c = await database.OpenAsync(cancellationToken); using var t = c.BeginTransaction();
        var snapshot = await SupervisorCatchUp.ReadActiveStateAsync(c, t, workflow, cancellationToken);
        var stored = await ReadDiscussion(c, t, workflow, cancellationToken) ?? throw new WorkflowStateException("No pending discussion exists.");
        if (snapshot.Cancelled || stored.Status != SupervisorDiscussionStatus.Pending || stored.DecisionId != decisionId ||
            stored.ParentRevisionId != snapshot.Active.RevisionId || stored.ParentStateDigest != snapshot.StateDigest || expectedStateDigest != snapshot.StateDigest)
            throw new WorkflowStateException("Discussion is stale or no longer pending; no choice was applied.");
        if (!stored.Options.Contains(choice, StringComparer.Ordinal)) throw new WorkflowStateException("Choice was not offered by this discussion.");
        await using var q = c.CreateCommand(); q.Transaction = t;
        q.CommandText = "UPDATE supervisor_pending_discussions SET status='Resolved',choice=$c,dismissal_reason=NULL WHERE workflow_id=$w AND decision_id=$i AND status='Pending' AND parent_state_digest=$d";
        q.Parameters.AddWithValue("$c", choice); q.Parameters.AddWithValue("$w", workflow.ToString("D")); q.Parameters.AddWithValue("$i", decisionId); q.Parameters.AddWithValue("$d", expectedStateDigest);
        if (await q.ExecuteNonQueryAsync(cancellationToken) != 1) throw new WorkflowStateException("Discussion changed while resolving; no choice was applied.");
        var result = stored with { Status = SupervisorDiscussionStatus.Resolved, IsStale = false, Choice = choice };
        await Evidence(c, t, workflow, "SupervisorDiscussionResolved", new { result.DecisionId, result.ParentRevisionId, result.ParentStateDigest, Choice = choice, Actor = actor, RecordedAt = now }, cancellationToken);
        t.Commit(); return result;
    }

    /// <summary>Dismisses an unresolved discussion without applying its choice or changing workflow execution.</summary>
    public async Task<SupervisorPendingDiscussion> DismissDiscussionAsync(Guid workflow, string decisionId,
        string actor, string reason, CancellationToken cancellationToken = default)
    {
        Authorize(actor); RequireGuid(workflow); decisionId = Text(decisionId, 128, nameof(decisionId));
        reason = Text(reason, 1024, nameof(reason));
        await using var c = await database.OpenAsync(cancellationToken); using var t = c.BeginTransaction();
        var snapshot = await SupervisorCatchUp.ReadActiveStateAsync(c, t, workflow, cancellationToken);
        var stored = await ReadDiscussion(c, t, workflow, cancellationToken) ?? throw new WorkflowStateException("No pending discussion exists.");
        if (stored.Status != SupervisorDiscussionStatus.Pending || stored.DecisionId != decisionId)
            throw new WorkflowStateException("Discussion is no longer pending under this decision id.");
        var stale = stored.ParentRevisionId != snapshot.Active.RevisionId || stored.ParentStateDigest != snapshot.StateDigest || snapshot.Cancelled;
        var now = database.TimeProvider.GetUtcNow();
        await using var q = c.CreateCommand(); q.Transaction = t;
        q.CommandText = "UPDATE supervisor_pending_discussions SET status='Dismissed',choice=NULL,dismissal_reason=$reason WHERE workflow_id=$w AND decision_id=$i AND status='Pending'";
        q.Parameters.AddWithValue("$reason", reason); q.Parameters.AddWithValue("$w", workflow.ToString("D")); q.Parameters.AddWithValue("$i", decisionId);
        if (await q.ExecuteNonQueryAsync(cancellationToken) != 1) throw new WorkflowStateException("Discussion changed while dismissing it.");
        var result = stored with { Status = SupervisorDiscussionStatus.Dismissed, IsStale = false, Choice = null, DismissalReason = reason };
        await Evidence(c, t, workflow, "SupervisorDiscussionDismissed", new
        {
            result.DecisionId,
            result.ParentRevisionId,
            result.ParentStateDigest,
            CurrentRevisionId = snapshot.Active.RevisionId,
            CurrentStateDigest = snapshot.StateDigest,
            Stale = stale,
            Reason = reason,
            Actor = actor,
            RecordedAt = now
        }, cancellationToken);
        t.Commit(); return result;
    }

    public async Task<SupervisorRevisionIntent?> GetIntentAsync(Guid workflow, string revision, CancellationToken cancellationToken = default)
    {
        RequireGuid(workflow); revision = Text(revision, 256, nameof(revision));
        await using var c = await database.OpenAsync(cancellationToken); await using var q = c.CreateCommand();
        q.CommandText = "SELECT revision_digest,goal,trigger,rationale,evidence_refs_json,recorded_by,recorded_at FROM supervisor_revision_intents WHERE workflow_id=$w AND revision_id=$r";
        q.Parameters.AddWithValue("$w", workflow.ToString("D")); q.Parameters.AddWithValue("$r", revision); await using var rd = await q.ExecuteReaderAsync(cancellationToken);
        if (!await rd.ReadAsync(cancellationToken)) return null;
        var digest = rd.GetString(0); var goal = rd.GetString(1); var trigger = Enum.Parse<SupervisorIntentTrigger>(rd.GetString(2));
        var rationale = rd.GetString(3); var refs = JsonSerializer.Deserialize<ImmutableArray<string>>(rd.GetString(4));
        var actor = rd.GetString(5); var recorded = DateTimeOffset.Parse(rd.GetString(6));
        await rd.DisposeAsync(); await c.DisposeAsync();
        var bound = await FindRevisionOrPendingCandidate(workflow, revision, cancellationToken);
        if (bound is null || AdaptiveRevisionContracts.Digest(bound) != digest) return null;
        ValidateGoal(bound, goal);
        return new SupervisorRevisionIntent(workflow, revision, goal, digest, trigger, rationale, refs, actor, recorded);
    }

    public async Task<SupervisorOperatorCheckpoint?> GetCheckpointAsync(Guid workflow, string actor, CancellationToken cancellationToken = default)
    {
        Authorize(actor); RequireGuid(workflow);
        await using var c = await database.OpenAsync(cancellationToken); await using var q = c.CreateCommand();
        q.CommandText = "SELECT revision_id,evidence_watermark,acknowledged_at FROM supervisor_operator_checkpoints WHERE workflow_id=$w AND actor_id=$a";
        q.Parameters.AddWithValue("$w", workflow.ToString("D")); q.Parameters.AddWithValue("$a", actor); await using var rd = await q.ExecuteReaderAsync(cancellationToken);
        return await rd.ReadAsync(cancellationToken) ? new SupervisorOperatorCheckpoint(workflow, actor, rd.GetString(0), rd.GetInt64(1), DateTimeOffset.Parse(rd.GetString(2))) : null;
    }

    public async Task<SupervisorOperatorCheckpoint> AcknowledgeAsync(Guid workflow, string actor,
        string expectedRevision, long expectedEvidenceWatermark, CancellationToken cancellationToken = default)
    {
        Authorize(actor); RequireGuid(workflow); expectedRevision = Text(expectedRevision, 256, nameof(expectedRevision));
        if (expectedEvidenceWatermark < 0) throw new ArgumentOutOfRangeException(nameof(expectedEvidenceWatermark));
        await using var c = await database.OpenAsync(cancellationToken); using var t = c.BeginTransaction();
        var snapshot = await SupervisorCatchUp.ReadActiveStateAsync(c, t, workflow, cancellationToken);
        var watermark = await EvidenceWatermark(c, t, workflow, cancellationToken);
        if (snapshot.Cancelled || snapshot.Active.RevisionId != expectedRevision || watermark != expectedEvidenceWatermark)
            throw new WorkflowStateException("Checkpoint must match the exact revision and evidence watermark observed by the operator.");
        var result = new SupervisorOperatorCheckpoint(workflow, actor, expectedRevision, watermark, database.TimeProvider.GetUtcNow());
        await using var q = c.CreateCommand(); q.Transaction = t;
        q.CommandText = "INSERT INTO supervisor_operator_checkpoints(workflow_id,actor_id,revision_id,evidence_watermark,acknowledged_at) VALUES($w,$a,$r,$e,$at) ON CONFLICT(workflow_id,actor_id) DO UPDATE SET revision_id=excluded.revision_id,evidence_watermark=excluded.evidence_watermark,acknowledged_at=excluded.acknowledged_at";
        q.Parameters.AddWithValue("$w", workflow.ToString("D")); q.Parameters.AddWithValue("$a", actor); q.Parameters.AddWithValue("$r", expectedRevision); q.Parameters.AddWithValue("$e", watermark); q.Parameters.AddWithValue("$at", result.AcknowledgedAt.ToString("O"));
        await q.ExecuteNonQueryAsync(cancellationToken);
        await Evidence(c, t, workflow, "SupervisorCheckpointAcknowledged", result, cancellationToken); t.Commit(); return result;
    }

    private async Task<AdaptiveRevision?> FindRevisionOrPendingCandidate(Guid workflow, string revision, CancellationToken ct)
    {
        var history = await revisions.GetHistoryAsync(workflow, ct);
        var found = history.SingleOrDefault(x => x.RevisionId == revision); if (found is not null) return found;
        var pending = await revisions.GetPendingAsync(workflow, ct);
        return pending?.Candidate.RevisionId == revision ? pending.Candidate : null;
    }

    private static async Task<AdaptiveRevision?> FindBoundRevisionAsync(SqliteConnection c, SqliteTransaction t,
        Guid workflow, string revision, CancellationToken ct)
    {
        await using (var q = c.CreateCommand())
        {
            q.Transaction = t; q.CommandText = "SELECT document FROM adaptive_revisions WHERE workflow_id=$w AND revision_id=$r";
            q.Parameters.AddWithValue("$w", workflow.ToString("D")); q.Parameters.AddWithValue("$r", revision);
            var json = await q.ExecuteScalarAsync(ct) as string;
            if (json is not null) return JsonSerializer.Deserialize<AdaptiveRevision>(json);
        }
        await using var pending = c.CreateCommand(); pending.Transaction = t;
        pending.CommandText = "SELECT p.document FROM adaptive_proposals p JOIN adaptive_workflows w ON w.pending_proposal=p.id WHERE w.id=$w AND p.status IN ('Pending','Approved')";
        pending.Parameters.AddWithValue("$w", workflow.ToString("D"));
        var proposalJson = await pending.ExecuteScalarAsync(ct) as string;
        var candidate = proposalJson is null ? null : JsonSerializer.Deserialize<AdaptiveProposal>(proposalJson)?.Candidate;
        return candidate?.RevisionId == revision ? candidate : null;
    }

    private static void ValidateGoal(AdaptiveRevision revision, string goal)
    {
        AdaptivePlanAuthoring.VerifyArchive(revision);
        var archive = JsonSerializer.Deserialize<AdaptivePlanArchive>(revision.PlanEnvelopeJson)!;
        var envelope = PlanRevisionDocument.LoadVerified(archive.EnvelopeFingerprint, Convert.FromBase64String(archive.EnvelopeBase64)).ReadEnvelope();
        var expected = new Penghou.Fuwen.ContentDigest("sha256", "aaaw-m1/v1", AdaptiveRevisionContracts.Digest(goal));
        if (envelope.Semantics.Objective != expected) throw new WorkflowStateException("Goal metadata must match the exact Fuwen objective digest for this revision.");
    }

    private Task<SupervisorPendingDiscussion?> ReadDiscussion(Guid workflow, CancellationToken ct) => ReadDiscussion(null, null, workflow, ct);

    private async Task<SupervisorPendingDiscussion?> ReadDiscussion(SqliteConnection? connection, SqliteTransaction? transaction, Guid workflow, CancellationToken ct)
    {
        var ownsConnection = connection is null;
        var c = connection ?? await database.OpenAsync(ct);
        await using var q = c.CreateCommand(); q.Transaction = transaction;
        q.CommandText = "SELECT decision_id,parent_revision_id,parent_state_digest,summary,missing_json,affected_json,options_json,evidence_refs_json,status,choice,recorded_by,recorded_at,dismissal_reason FROM supervisor_pending_discussions WHERE workflow_id=$w";
        q.Parameters.AddWithValue("$w", workflow.ToString("D")); await using var rd = await q.ExecuteReaderAsync(ct); if (!await rd.ReadAsync(ct)) { await rd.DisposeAsync(); if (ownsConnection) await c.DisposeAsync(); return null; }
        var result = new SupervisorPendingDiscussion(workflow, rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
            JsonSerializer.Deserialize<ImmutableArray<string>>(rd.GetString(4)), JsonSerializer.Deserialize<ImmutableArray<string>>(rd.GetString(5)),
            JsonSerializer.Deserialize<ImmutableArray<string>>(rd.GetString(6)), JsonSerializer.Deserialize<ImmutableArray<string>>(rd.GetString(7)),
            Enum.Parse<SupervisorDiscussionStatus>(rd.GetString(8)), false, rd.IsDBNull(9) ? null : rd.GetString(9), rd.GetString(10), DateTimeOffset.Parse(rd.GetString(11)))
        { DismissalReason = rd.IsDBNull(12) ? null : rd.GetString(12) };
        await rd.DisposeAsync(); if (ownsConnection) await c.DisposeAsync(); return result;
    }

    private static async Task<long> EvidenceWatermark(SqliteConnection c, SqliteTransaction t, Guid workflow, CancellationToken ct)
    {
        await using var q = c.CreateCommand(); q.Transaction = t;
        q.CommandText = "SELECT COALESCE(MAX(sequence),0) FROM adaptive_evidence WHERE workflow_id=$w";
        q.Parameters.AddWithValue("$w", workflow.ToString("D")); return Convert.ToInt64(await q.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task Evidence(SqliteConnection c, SqliteTransaction t, Guid workflow, string kind, object document, CancellationToken ct)
    {
        await using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = "INSERT INTO adaptive_evidence(workflow_id,kind,document) VALUES($w,$k,$d)";
        q.Parameters.AddWithValue("$w", workflow.ToString("D")); q.Parameters.AddWithValue("$k", kind); q.Parameters.AddWithValue("$d", JsonSerializer.Serialize(document)); await q.ExecuteNonQueryAsync(ct);
    }

    private void Authorize(string actor)
    {
        actor = Text(actor, 256, nameof(actor));
        if (!isAuthorizedOperator(actor)) throw new WorkflowAuthorizationException("Actor is not an authorized supervisor.");
    }
    private static void RequireGuid(Guid id) { if (id == Guid.Empty) throw new ArgumentException("Workflow id cannot be empty."); }
    private static string Text(string? value, int max, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl)) throw new ArgumentException($"{name} must be non-empty bounded text.", name);
        return value.Trim();
    }
    private static ImmutableArray<string> Items(IEnumerable<string> values, string name, int itemMax = 1024)
    {
        ArgumentNullException.ThrowIfNull(values); var array = values.Take(MaxItems + 1).ToArray();
        if (array.Length > MaxItems) throw new ArgumentException($"{name} exceeds the {MaxItems}-item bound.", name);
        var normalized = array.Select(x => Text(x, itemMax, name)).ToImmutableArray();
        if (normalized.Distinct(StringComparer.Ordinal).Count() != normalized.Length) throw new ArgumentException($"{name} contains duplicates.", name);
        return normalized;
    }
}
