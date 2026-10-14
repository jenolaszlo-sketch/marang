using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Runs.Adaptive;

public enum DurableSupervisorState { Running, Discussing, AwaitingApproval, ReadyToApply, DelegationUnresolved, Blocked, Completed, Cancelled }
public enum SupervisorEvidenceCategory { CurrentAuthoritative, Historical, Superseded, Unresolved }
public sealed record SupervisorBranchView(string LogicalId, string Title, Guid ExecutionId, string ExecutionState,
    SupervisorEvidenceCategory EvidenceCategory, bool HasResult, ImmutableArray<string> Blockers);
public sealed record SupervisorDecisionView(string Id, string Kind, string Summary, string ParentRevisionId,
    bool IsStale, ImmutableArray<string> Options, ImmutableArray<string> MissingInformation, ImmutableArray<string> AffectedWork = default);
public sealed record SupervisorConditionView(string Id, string LogicalId, string Classification, bool RequiresDecision,
    ImmutableArray<string> Actions, string Explanation, bool IsCurrent = true);
public sealed record RevisionWorkExplanation(string LogicalId, string Title, Guid? PreviousExecutionId, Guid? ExecutionId,
    ImmutableArray<string> Classifications, ImmutableArray<string> Reasons);
public sealed record RevisionTransitionDetail(string? FromRevisionId, string ToRevisionId, string Reason,
    string Trigger, ImmutableArray<string> EvidenceReferences, ImmutableArray<RevisionWorkExplanation> Work);
public sealed record SupervisorMeaningfulChange(long Sequence, string Kind, string Summary);
public sealed record SupervisorCatchUpView(Guid WorkflowId, string ActiveRevisionId, string Goal,
    DurableSupervisorState State, ImmutableArray<SupervisorBranchView> Branches,
    ImmutableArray<SupervisorBranchView> PriorResults, ImmutableArray<SupervisorDecisionView> Decisions,
    ImmutableArray<SupervisorConditionView> Conditions, ImmutableArray<string> Blockers,
    ImmutableArray<RevisionTransitionDetail> Timeline, ImmutableArray<SupervisorMeaningfulChange> ChangesSinceCheckpoint,
    long EvidenceWatermark, string? LastAcknowledgedRevision, int TotalRevisionCount = 0, int EarlierMeaningfulChangeCount = 0);

internal sealed record ProposalBranchContext(string LogicalId, string? PreviousStatus, bool ReusedAtProposal);
internal sealed record SupervisorProposalContext(string ProposalDigest, string ParentStateDigest,
    ImmutableArray<ProposalBranchContext> Branches);

/// <summary>
/// Experimental host projection over the pinned M1 SQLite schemas. Reads all owner state in one SQLite snapshot.
/// It never writes execution, authority, revision or provider state. Presentation is derived, not another engine.
/// </summary>
public sealed class SupervisorCatchUp(IZhinuSqliteDatabase database, Func<string, bool> isAuthorizedOperator)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken);
        await using var c = await database.OpenAsync(cancellationToken);
        await Execute(c, null, """
            CREATE TABLE IF NOT EXISTS supervisor_proposal_contexts (
              proposal_id TEXT PRIMARY KEY, workflow_id TEXT NOT NULL, document TEXT NOT NULL);
            """, cancellationToken);
    }

    /// <summary>Retains exact reviewed branch state so later completion cannot be misreported as prior reuse.</summary>
    public async Task CaptureProposalContextAsync(AdaptiveProposal proposal, string actor, CancellationToken cancellationToken = default)
    {
        Authorize(actor);
        proposal = JsonSerializer.Deserialize<AdaptiveProposal>(JsonSerializer.Serialize(proposal))!;
        await using var c = await database.OpenAsync(cancellationToken);
        using var t = c.BeginTransaction();
        var snapshot = await ReadSnapshot(c, t, proposal.Candidate.WorkflowId, cancellationToken);
        var digest = AdaptiveRevisionContracts.Digest(proposal);
        if (snapshot.Pending?.ProposalId != proposal.ProposalId || snapshot.PendingDigest != digest ||
            snapshot.Active.RevisionId != proposal.Candidate.ParentRevisionId || snapshot.StateDigest != proposal.ParentStateDigest)
            throw new WorkflowStateException("Proposal context is stale; review a new proposal.");
        var context = new SupervisorProposalContext(digest, proposal.ParentStateDigest,
            proposal.Changes.Select(change =>
            {
                var prior = snapshot.Active.Branches.SingleOrDefault(b => b.LogicalId == change.LogicalId);
                var status = prior is null ? null : snapshot.Runs[prior.Execution.Id].Status.ToString();
                return new ProposalBranchContext(change.LogicalId, status,
                    change.Kind == AdaptiveChangeKind.Preserved && status == nameof(WorkflowStatus.Completed) &&
                    proposal.Candidate.Branches.Single(b => b.LogicalId == change.LogicalId).AcceptReuse);
            }).ToImmutableArray());
        var json = JsonSerializer.Serialize(context);
        var previous = await Scalar(c, t, "SELECT document FROM supervisor_proposal_contexts WHERE proposal_id=$p", cancellationToken,
            ("$p", proposal.ProposalId.ToString("D")));
        if (previous is not null)
        {
            if (previous != json) throw new WorkflowStateException("Reviewed proposal context is immutable.");
            return;
        }
        await Execute(c, t, "INSERT INTO supervisor_proposal_contexts(proposal_id,workflow_id,document) VALUES($p,$w,$d)", cancellationToken,
            ("$p", proposal.ProposalId.ToString("D")), ("$w", proposal.Candidate.WorkflowId.ToString("D")), ("$d", json));
        await Execute(c, t, "INSERT INTO adaptive_evidence(workflow_id,kind,document) VALUES($w,'SupervisorProposalReviewed',$d)", cancellationToken,
            ("$w", proposal.Candidate.WorkflowId.ToString("D")),
            ("$d", JsonSerializer.Serialize(new { proposal.ProposalId, Digest = digest, actor, context.Branches })));
        t.Commit();
    }

    public async Task<SupervisorCatchUpView> ReadAsync(Guid workflowId, string actor, CancellationToken cancellationToken = default)
    {
        Authorize(actor);
        await using var c = await database.OpenAsync(cancellationToken);
        using var t = c.BeginTransaction();
        var s = await ReadSnapshot(c, t, workflowId, cancellationToken);
        var intents = await ReadIntents(c, t, workflowId, cancellationToken);
        foreach (var revision in s.History)
            if (intents.TryGetValue(revision.RevisionId, out var intent) && intent.RevisionDigest != AdaptiveRevisionContracts.Digest(revision))
                intents.Remove(revision.RevisionId);
        var contexts = await ReadContexts(c, t, workflowId, cancellationToken);
        var conditions = await ReadConditions(c, t, s, cancellationToken);
        var decisions = new List<SupervisorDecisionView>();
        if (s.Pending is { } pending)
        {
            var stale = pending.Candidate.ParentRevisionId != s.Active.RevisionId || pending.ParentStateDigest != s.StateDigest;
            var expiry = await Scalar(c, t, "SELECT approval_expires FROM adaptive_proposals WHERE id=$p", cancellationToken,
                ("$p", pending.ProposalId.ToString("D")));
            var expired = s.PendingStatus == "Approved" && (expiry is null || DateTimeOffset.Parse(expiry) <= database.TimeProvider.GetUtcNow());
            var hasAuthority = await Scalar(c, t, "SELECT name FROM sqlite_master WHERE type='table' AND name='adaptive_authority_decisions'", cancellationToken) is not null;
            var latestAuthority = hasAuthority ? await Scalar(c, t, "SELECT outcome FROM adaptive_authority_decisions WHERE proposal_id=$p AND digest=$d ORDER BY sequence DESC LIMIT 1", cancellationToken,
                ("$p", pending.ProposalId.ToString("D")), ("$d", s.PendingDigest!)) : "Unavailable";
            var denied = latestAuthority is "Denied" or "Unavailable";
            decisions.Add(new(pending.ProposalId.ToString("D"), stale ? "StaleProposal" : denied ? "DeniedOperation" : expired ? "ExpiredApproval" : s.PendingStatus == "Approved" ? "ApprovedProposal" : "PendingApproval",
                pending.Candidate.Reason + " " + pending.ImpactSummary + (stale ? " Reviewed execution/provider state or active revision changed; this decision must be rebuilt." : ""), pending.Candidate.ParentRevisionId!, stale,
                (stale ? new[] { "Review a new proposal", "Reject" } : denied ? new[] { "Change scope or refresh authority", "Reject" } : expired ? new[] { "Renew exact approval", "Reject" } :
                    s.PendingStatus == "Approved" ? new[] { "Commit with fresh authority", "Reject" } : new[] { "Approve with fresh authority", "Reject", "Discuss" }).ToImmutableArray(), [],
                pending.Changes.Where(x => x.Kind != AdaptiveChangeKind.Preserved).Select(x => x.LogicalId).ToImmutableArray()));
        }
        decisions.AddRange(await ReadRecoveryDecisions(c, t, s, cancellationToken));
        var discussion = await ReadDiscussion(c, t, workflowId, cancellationToken);
        if (discussion is { Status: SupervisorDiscussionStatus.Pending })
        {
            var stale = discussion.ParentRevisionId != s.Active.RevisionId || discussion.ParentStateDigest != s.StateDigest;
            decisions.Add(new(discussion.DecisionId, "Discussion", discussion.Summary + (stale ? " Reviewed execution/provider state or active revision changed; refresh discussion before choosing." : ""), discussion.ParentRevisionId, stale,
                stale ? ["Review current state and reopen discussion"] : discussion.Options, discussion.MissingInformation, discussion.AffectedLogicalIds));
        }
        if (s.Cancelled) decisions.Clear();
        var branchViews = s.Active.Branches.Select(b => Branch(s, b, conditions, s.Cancelled)).ToImmutableArray();
        var selected = s.Active.Branches.Select(b => b.Execution.Id).ToHashSet();
        var prior = s.History.SelectMany(r => r.Branches).DistinctBy(b => b.Execution.Id)
            .Where(b => !selected.Contains(b.Execution.Id)).Select(b => Branch(s, b, [], true)).ToImmutableArray();
        var blockers = branchViews.SelectMany(b => b.Blockers).Distinct(StringComparer.Ordinal).ToList();
        if (!intents.ContainsKey(s.Active.RevisionId)) blockers.Add("Current intent metadata is missing; supply the goal bound to this revision before continuing supervisor decisions.");
        blockers.AddRange(decisions.Where(d => d.IsStale).Select(d => "Decision is stale: " + d.Summary));
        blockers.AddRange(conditions.Where(d => d.RequiresDecision).Select(d => d.Explanation));
        var timeline = s.History.TakeLast(16).Select(r => Transition(s, r, intents, contexts)).ToImmutableArray();
        var checkpoint = await ReadCheckpoint(c, t, workflowId, actor, cancellationToken);
        var changes = s.Evidence.Where(e => e.Sequence > (checkpoint?.EvidenceWatermark ?? 0))
            .Select(e => Meaningful(e)).Where(e => e is not null).Cast<SupervisorMeaningfulChange>().ToImmutableArray();
        var state = s.Cancelled ? DurableSupervisorState.Cancelled : conditions.Any(x => x.RequiresDecision) ? DurableSupervisorState.DelegationUnresolved :
            decisions.Any(d => d.IsStale || d.Kind == "DeniedOperation") || blockers.Any(b => b.StartsWith("Current intent", StringComparison.Ordinal)) ? DurableSupervisorState.Blocked :
            s.Pending is not null ? decisions.Any(d => d.Kind == "ApprovedProposal") ? DurableSupervisorState.ReadyToApply : DurableSupervisorState.AwaitingApproval :
            discussion is { Status: SupervisorDiscussionStatus.Pending } ? DurableSupervisorState.Discussing :
            branchViews.Any(b => b.ExecutionState is "Failed" or "Cancelled") ? DurableSupervisorState.Blocked :
            branchViews.All(b => b.ExecutionState == "Completed") ? DurableSupervisorState.Completed : DurableSupervisorState.Running;
        return new(workflowId, s.Active.RevisionId, intents.GetValueOrDefault(s.Active.RevisionId)?.Goal ?? "Intent unavailable for this revision",
            state, branchViews, prior, decisions.ToImmutableArray(), conditions.ToImmutableArray(), blockers.ToImmutableArray(), timeline,
            changes.TakeLast(32).ToImmutableArray(), s.Evidence.LastOrDefault()?.Sequence ?? 0, checkpoint?.RevisionId,
            s.History.Length, Math.Max(0, changes.Length - 32));
    }

    public async Task<RevisionTransitionDetail> DrillDownAsync(Guid workflowId, string toRevisionId, string actor, CancellationToken cancellationToken = default)
    {
        Authorize(actor);
        await using var c = await database.OpenAsync(cancellationToken);
        using var t = c.BeginTransaction();
        var s = await ReadSnapshot(c, t, workflowId, cancellationToken);
        var revision = s.History.SingleOrDefault(r => r.RevisionId == toRevisionId) ?? throw new WorkflowStateException("Revision does not belong to this workflow.");
        var intents = await ReadIntents(c, t, workflowId, cancellationToken);
        if (intents.TryGetValue(revision.RevisionId, out var intent) && intent.RevisionDigest != AdaptiveRevisionContracts.Digest(revision))
            intents.Remove(revision.RevisionId);
        return Transition(s, revision, intents, await ReadContexts(c, t, workflowId, cancellationToken));
    }

    private void Authorize(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || !isAuthorizedOperator(actor)) throw new WorkflowAuthorizationException("Operator cannot inspect this workflow.");
    }

    private static SupervisorBranchView Branch(Snapshot s, AdaptiveBranch b, IReadOnlyList<SupervisorConditionView> conditions, bool historical)
    {
        var run = s.Runs[b.Execution.Id];
        var blockers = new List<string>();
        if (!historical)
        {
            foreach (var id in b.Dependencies)
            {
                var dependency = s.Active.Branches.Single(x => x.LogicalId == id);
                if (s.Runs[dependency.Execution.Id].Status != WorkflowStatus.Completed) blockers.Add(b.Title + " waits for " + dependency.Title + ".");
            }
            if (run.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled) blockers.Add(b.Title + " cannot progress under its current execution.");
            blockers.AddRange(conditions.Where(x => x.LogicalId == b.LogicalId && x.RequiresDecision).Select(x => x.Explanation));
        }
        var hasResult = run.Output is not null;
        var category = historical ? SupervisorEvidenceCategory.Superseded : run.Status == WorkflowStatus.Completed && hasResult ?
            SupervisorEvidenceCategory.CurrentAuthoritative : conditions.Any(x => x.IsCurrent && x.LogicalId == b.LogicalId && x.RequiresDecision) ?
            SupervisorEvidenceCategory.Unresolved : SupervisorEvidenceCategory.Historical;
        return new(b.LogicalId, b.Title, b.Execution.Id, run.Status.ToString(), category, hasResult, blockers.ToImmutableArray());
    }

    private static RevisionTransitionDetail Transition(Snapshot s, AdaptiveRevision revision,
        Dictionary<string, SupervisorRevisionIntent> intents, Dictionary<string, SupervisorProposalContext> contexts)
    {
        var intent = intents.GetValueOrDefault(revision.RevisionId);
        var parent = s.History.SingleOrDefault(r => r.RevisionId == revision.ParentRevisionId);
        if (parent is null) return new(null, revision.RevisionId, revision.Reason, intent?.Trigger.ToString() ?? "Unrecorded",
            intent?.EvidenceReferences ?? [], revision.Branches.Select(b => new RevisionWorkExplanation(b.LogicalId, b.Title, null,
                b.Execution.Id, ["Added"], ["Admitted as original work under the verified plan."])).ToImmutableArray());
        var context = contexts.GetValueOrDefault(revision.RevisionId);
        var work = AdaptiveRevisionContracts.Compare(parent, revision).Select(change =>
        {
            var before = parent.Branches.SingleOrDefault(b => b.LogicalId == change.LogicalId);
            var after = revision.Branches.SingleOrDefault(b => b.LogicalId == change.LogicalId);
            var classes = new List<string>(); var reasons = new List<string>();
            if (change.Kind == AdaptiveChangeKind.Preserved)
            {
                classes.Add("Preserved");
                reasons.Add("Contract, immutable input, authority, provenance and dependencies are equivalent; the execution identity is retained.");
                if (context?.Branches.SingleOrDefault(b => b.LogicalId == change.LogicalId)?.ReusedAtProposal == true)
                {
                    classes.Add("Reused"); reasons.Add("Completed result reuse was explicitly accepted in the exact reviewed proposal and checked again at atomic activation.");
                }
                else reasons.Add("No claim of completed-result reuse at activation is inferred from later completion.");
            }
            else if (change.Kind == AdaptiveChangeKind.Added)
            { classes.Add("Added"); reasons.Add("This logical work item was introduced with a new execution identity."); }
            else
            {
                classes.Add(change.Kind == AdaptiveChangeKind.Removed ? "Removed" : "Replaced");
                classes.Add("Fenced"); classes.Add("Invalidated");
                if (change.Kind == AdaptiveChangeKind.DependencyChanged) { classes.Add("DependencyChanged"); reasons.Add("Prerequisite or provenance changed, so an earlier execution cannot satisfy this revision."); }
                if (before is not null && after is not null)
                {
                    if (before.ContractDigest != after.ContractDigest) reasons.Add("Required behavior or admitted contract changed.");
                    if (before.InputDigest != after.InputDigest) reasons.Add("Immutable execution input changed.");
                    if (before.AuthorityDigest != after.AuthorityDigest) reasons.Add("Authority binding changed.");
                }
                reasons.Add("Atomic revision activation fenced the previous execution. A late result remains historical and cannot satisfy this revision.");
            }
            return new RevisionWorkExplanation(change.LogicalId, (after ?? before)!.Title, before?.Execution.Id, after?.Execution.Id,
                classes.ToImmutableArray(), reasons.ToImmutableArray());
        }).ToImmutableArray();
        return new(parent.RevisionId, revision.RevisionId, revision.Reason, intent?.Trigger.ToString() ?? "Unrecorded", intent?.EvidenceReferences ?? [], work);
    }

    private static SupervisorMeaningfulChange? Meaningful(EvidenceRow row)
    {
        using var doc = JsonDocument.Parse(row.Document);
        var d = doc.RootElement;
        string Field(string key) => d.TryGetProperty(key, out var value) ? value.ToString() : "";
        var summary = row.Kind switch
        {
            "RevisionActivated" => "Original revision activated: " + Field("RevisionId"),
            "RevisionCommitted" => "Revision activated: " + Field("RevisionId"),
            "RevisionProposalCreated" => "Change proposed: " + (d.TryGetProperty("Candidate", out var candidate) ? candidate.GetProperty("Reason").GetString() : "review required"),
            "RevisionApproved" => "Exact proposal approved; commit still requires fresh authority.",
            "RevisionRejected" => "Proposed change rejected.",
            "WorkflowCancelled" => "Workflow cancelled; external effects may still exist.",
            "RepositoryEffect" => "Repository effect " + Field("effectId") + ": " + Field("stage") + "; " + Field("classification") +
                (Field("validationPassed") == "" ? "" : "; validation passed: " + Field("validationPassed")),
            "SupervisorDiscussionOpened" => "Decision discussion opened: " + Field("Summary"),
            "SupervisorDiscussionResolved" => "Discussion decision recorded: " + Field("Choice"),
            "SupervisorDiscussionDismissed" => "Discussion dismissed; the active workflow remains unchanged.",
            "DelegationRecoveryDecision" or "DelegationRecoveryDecisionUpdated" => "Delegation recovery decision: " + Field("ChosenOption") + "; " + Field("Status"),
            _ => null
        };
        return summary is null ? null : new(row.Sequence, row.Kind, summary);
    }

    private static async Task<List<SupervisorConditionView>> ReadConditions(SqliteConnection c, SqliteTransaction t, Snapshot s, CancellationToken ct)
    {
        var result = new List<SupervisorConditionView>();
        if (s.Cancelled) return result;
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = """
            SELECT b.execution_id,a.document FROM adaptive_delegation_attempts a
            JOIN adaptive_branch_delegations b ON b.delegation_id=a.delegation_id
            JOIN adaptive_run_ownership o ON o.run_id=b.execution_id WHERE o.workflow_id=$w ORDER BY a.rowid
            """;
        command.Parameters.AddWithValue("$w", s.Active.WorkflowId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var executionId = Guid.Parse(reader.GetString(0));
            var branch = s.Active.Branches.SingleOrDefault(b => b.Execution.Id == executionId);
            var isCurrent = branch is not null;
            branch ??= s.History.SelectMany(r => r.Branches).First(b => b.Execution.Id == executionId);
            var record = SqliteDelegationRecoveryJournal.Decode(reader.GetString(1));
            var projection = DelegationRecoveryActions.Project(record.Intent.Request.Identity, record, s.Active.WorkflowId, executionId,
                s.Active.RevisionId, s.StateDigest, branch.AuthorityDigest, isCurrent);
            // Terminal provider evidence still requires engine adoption; provider success alone is not a workflow result.
            var requiresDecision = projection.RequiresDecision;
            var classification = projection.Classification.ToString();
            result.Add(new(AdaptiveRevisionContracts.Digest(record.Intent.Request.Identity), branch.LogicalId, classification, requiresDecision,
                (isCurrent ? projection.AllowedActions.Select(a => a.ToString()) : []).ToImmutableArray(), branch.Title + ": " + classification +
                (!isCurrent ? ". Historical provider evidence; this operation cannot satisfy active intent and may still exist externally." :
                    requiresDecision ? ". A prior external operation may still exist; automatic retry is unavailable." : ". Only a completed active workflow result is authoritative."), isCurrent));
        }
        return result;
    }

    private static async Task<List<SupervisorDecisionView>> ReadRecoveryDecisions(SqliteConnection c, SqliteTransaction t, Snapshot s, CancellationToken ct)
    {
        var result = new List<SupervisorDecisionView>();
        if (s.Cancelled || await Scalar(c, t, "SELECT name FROM sqlite_master WHERE type='table' AND name='adaptive_delegation_recovery_decisions'", ct) is null) return result;
        await using var q = c.CreateCommand(); q.Transaction = t;
        q.CommandText = "SELECT document FROM adaptive_delegation_recovery_decisions ORDER BY rowid";
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var d = JsonSerializer.Deserialize<DelegationRecoveryDecision>(r.GetString(0))!;
            if (d.WorkflowId != s.Active.WorkflowId) continue;
            if (d.ChosenOption == "Observe" && d.Status is "Observing" or "ObservationUnresolved")
            {
                var laterObservation = await Scalar(c, t, """
                    SELECT id FROM adaptive_delegation_recovery_decisions
                    WHERE json_extract(document,'$.BoundExecutionId')=$run AND json_extract(document,'$.DelegationId')=$delegation
                    AND json_extract(document,'$.ChosenOption')='Observe' AND json_extract(document,'$.Status')='Observed'
                    AND rowid>(SELECT rowid FROM adaptive_delegation_recovery_decisions WHERE id=$id) LIMIT 1
                    """, ct, ("$run", d.BoundExecutionId.ToString("D")), ("$delegation", d.DelegationId.ToString("D")), ("$id", d.DecisionId.ToString("D")));
                if (laterObservation is null && s.Active.Branches.Any(b => b.Execution.Id == d.BoundExecutionId) && s.Runs[d.BoundExecutionId].Status != WorkflowStatus.Completed)
                    result.Add(new(d.DecisionId.ToString("D"), "RecoveryObservation", "A prior observation was interrupted or unavailable; refresh the existing operation's condition.",
                        d.ActiveRevisionId, false, ["Refresh condition", "Observe existing captured operation", "Wait"], [], []));
                continue;
            }
            if (d.ChosenOption != "Replace") continue;
            // Execution ownership proves the outcome even if the host died before marking its action receipt.
            if (d.ProposalId is not null)
            {
                var exactCommitted = await Scalar(c, t, """
                    SELECT p.document FROM adaptive_proposals p JOIN adaptive_revisions h
                    ON h.proposal_id=p.id AND h.workflow_id=p.workflow_id
                    WHERE p.id=$p AND p.digest=$digest AND p.status='Committed' AND p.workflow_id=$w
                    """, ct, ("$p", d.ProposalId.Value.ToString("D")), ("$digest", d.ProposalDigest!), ("$w", d.WorkflowId.ToString("D")));
                if (exactCommitted is not null)
                {
                    var proposal = JsonSerializer.Deserialize<AdaptiveProposal>(exactCommitted)!;
                    if (proposal.Candidate.ParentRevisionId == d.ActiveRevisionId && proposal.Candidate.RevisionId == d.CandidateRevisionId &&
                        AdaptiveRevisionContracts.Digest(proposal.Candidate) == d.CandidateDigest && AdaptiveRevisionContracts.Digest(proposal) == d.ProposalDigest)
                        continue;
                }
            }
            var stale = d.ActiveRevisionId != s.Active.RevisionId || d.ActiveStateDigest != s.StateDigest ||
                (d.ProposalId is not null && s.Pending?.ProposalId != d.ProposalId);
            result.Add(new(d.DecisionId.ToString("D"), "RecoveryDecision", "Replace unresolved delegated work: " + d.Status,
                d.ActiveRevisionId, stale, stale ? ["Refresh recovery options"] : d.Status == "Preparing" ? ["Reconcile exact pending proposal", "Discuss"] :
                ["Review exact proposal", "Approve", "Continue with fresh authority", "Reject"], [], []));
        }
        return result;
    }

    private sealed record RunRow(WorkflowStatus Status, string? Output);
    private sealed record EvidenceRow(long Sequence, string Kind, string Document);
    private sealed record Snapshot(AdaptiveRevision Active, AdaptiveRevision[] History, AdaptiveProposal? Pending,
        string? PendingDigest, string? PendingStatus, bool Cancelled, string StateDigest, Dictionary<Guid, RunRow> Runs, List<EvidenceRow> Evidence);

    internal static async Task<(AdaptiveRevision Active, string StateDigest, bool Cancelled)> ReadActiveStateAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid workflowId, CancellationToken cancellationToken)
    {
        var snapshot = await ReadSnapshot(connection, transaction, workflowId, cancellationToken);
        return (snapshot.Active, snapshot.StateDigest, snapshot.Cancelled);
    }

    private static async Task<Snapshot> ReadSnapshot(SqliteConnection c, SqliteTransaction t, Guid workflowId, CancellationToken ct)
    {
        var id = workflowId.ToString("D");
        var history = new List<AdaptiveRevision>();
        await using (var command = c.CreateCommand())
        {
            command.Transaction = t; command.CommandText = "SELECT document FROM adaptive_revisions WHERE workflow_id=$w ORDER BY rowid";
            command.Parameters.AddWithValue("$w", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) history.Add(JsonSerializer.Deserialize<AdaptiveRevision>(reader.GetString(0))!);
        }
        var activeId = await Scalar(c, t, "SELECT active_revision FROM adaptive_workflows WHERE id=$w", ct, ("$w", id));
        var active = history.SingleOrDefault(r => r.RevisionId == activeId) ?? throw new WorkflowStateException("Unknown adaptive workflow.");
        var cancelled = await Scalar(c, t, "SELECT cancelled FROM adaptive_workflows WHERE id=$w", ct, ("$w", id)) == "1";
        var pendingId = cancelled ? null : await Scalar(c, t, "SELECT pending_proposal FROM adaptive_workflows WHERE id=$w", ct, ("$w", id));
        AdaptiveProposal? pending = null; string? digest = null, status = null;
        if (pendingId is not null)
        {
            pending = JsonSerializer.Deserialize<AdaptiveProposal>((await Scalar(c, t, "SELECT document FROM adaptive_proposals WHERE id=$p", ct, ("$p", pendingId)))!)!;
            digest = await Scalar(c, t, "SELECT digest FROM adaptive_proposals WHERE id=$p", ct, ("$p", pendingId));
            status = await Scalar(c, t, "SELECT status FROM adaptive_proposals WHERE id=$p", ct, ("$p", pendingId));
        }
        var runs = new Dictionary<Guid, RunRow>();
        foreach (var b in history.SelectMany(r => r.Branches).DistinctBy(b => b.Execution.Id))
        {
            var runStatus = await Scalar(c, t, "SELECT status FROM workflow_runs WHERE id=$r", ct, ("$r", b.Execution.Id.ToString("D")));
            if (runStatus is null) throw new WorkflowStateException("Admitted execution is missing.");
            runs[b.Execution.Id] = new((WorkflowStatus)int.Parse(runStatus, System.Globalization.CultureInfo.InvariantCulture),
                await Scalar(c, t, "SELECT output_json FROM workflow_runs WHERE id=$r", ct, ("$r", b.Execution.Id.ToString("D"))));
        }
        var rows = new List<string> { AdaptiveRevisionContracts.Digest(active) };
        foreach (var b in active.Branches.OrderBy(b => b.LogicalId, StringComparer.Ordinal))
            foreach (var sql in new[] {
                "SELECT id,status,lease_generation,output_json,error_json,definition_fingerprint FROM workflow_runs WHERE id=$r",
                "SELECT id,step_key,status,attempt,revision,lease_generation,input_json,output_json,error_json FROM workflow_steps WHERE workflow_run_id=$r ORDER BY step_key,revision,id",
                "SELECT witness_key,digest FROM adaptive_execution_witness WHERE run_id=$r ORDER BY witness_key" })
            {
                await using var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql;
                command.Parameters.AddWithValue("$r", b.Execution.Id.ToString("D"));
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                    .Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i).ToString()).ToArray()));
            }
        var evidence = new List<EvidenceRow>();
        await using (var command = c.CreateCommand())
        {
            command.Transaction = t; command.CommandText = "SELECT sequence,kind,document FROM adaptive_evidence WHERE workflow_id=$w ORDER BY sequence";
            command.Parameters.AddWithValue("$w", id);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) evidence.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }
        return new(active, history.ToArray(), pending, digest, status, cancelled, AdaptiveRevisionContracts.Digest(rows), runs, evidence);
    }

    private static async Task<Dictionary<string, SupervisorRevisionIntent>> ReadIntents(SqliteConnection c, SqliteTransaction t, Guid workflowId, CancellationToken ct)
    {
        var result = new Dictionary<string, SupervisorRevisionIntent>(StringComparer.Ordinal);
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = "SELECT revision_id,goal,revision_digest,trigger,rationale,evidence_refs_json,recorded_by,recorded_at FROM supervisor_revision_intents WHERE workflow_id=$w";
        command.Parameters.AddWithValue("$w", workflowId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result[reader.GetString(0)] = new(workflowId, reader.GetString(0), reader.GetString(1), reader.GetString(2),
            Enum.Parse<SupervisorIntentTrigger>(reader.GetString(3)), reader.GetString(4), JsonSerializer.Deserialize<ImmutableArray<string>>(reader.GetString(5)),
            reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7)));
        return result;
    }

    private static async Task<Dictionary<string, SupervisorProposalContext>> ReadContexts(SqliteConnection c, SqliteTransaction t, Guid workflowId, CancellationToken ct)
    {
        var result = new Dictionary<string, SupervisorProposalContext>(StringComparer.Ordinal);
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = """
            SELECT r.revision_id,c.document,p.digest FROM supervisor_proposal_contexts c
            JOIN adaptive_revisions r ON r.proposal_id=c.proposal_id AND r.workflow_id=c.workflow_id
            JOIN adaptive_proposals p ON p.id=c.proposal_id WHERE r.workflow_id=$w AND p.status='Committed'
            """;
        command.Parameters.AddWithValue("$w", workflowId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var context = JsonSerializer.Deserialize<SupervisorProposalContext>(reader.GetString(1))!;
            if (context.ProposalDigest != reader.GetString(2)) throw new WorkflowStateException("Proposal context binding is invalid.");
            result[reader.GetString(0)] = context;
        }
        return result;
    }

    private static async Task<SupervisorPendingDiscussion?> ReadDiscussion(SqliteConnection c, SqliteTransaction t, Guid workflowId, CancellationToken ct)
    {
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = "SELECT decision_id,parent_revision_id,parent_state_digest,summary,missing_json,affected_json,options_json,evidence_refs_json,status,choice,recorded_by,recorded_at FROM supervisor_pending_discussions WHERE workflow_id=$w";
        command.Parameters.AddWithValue("$w", workflowId.ToString("D"));
        await using var r = await command.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        ImmutableArray<string> Array(int i) => JsonSerializer.Deserialize<ImmutableArray<string>>(r.GetString(i));
        return new(workflowId, r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Array(4), Array(5), Array(6), Array(7),
            Enum.Parse<SupervisorDiscussionStatus>(r.GetString(8)), false, r.IsDBNull(9) ? null : r.GetString(9), r.GetString(10), DateTimeOffset.Parse(r.GetString(11)));
    }

    private static async Task<SupervisorOperatorCheckpoint?> ReadCheckpoint(SqliteConnection c, SqliteTransaction t, Guid workflowId, string actor, CancellationToken ct)
    {
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = "SELECT revision_id,evidence_watermark,acknowledged_at FROM supervisor_operator_checkpoints WHERE workflow_id=$w AND actor_id=$a";
        command.Parameters.AddWithValue("$w", workflowId.ToString("D")); command.Parameters.AddWithValue("$a", actor);
        await using var r = await command.ExecuteReaderAsync(ct);
        return !await r.ReadAsync(ct) ? null : new(workflowId, actor, r.GetString(0), r.GetInt64(1), DateTimeOffset.Parse(r.GetString(2)));
    }

    private static async Task Execute(SqliteConnection c, SqliteTransaction? t, string sql, CancellationToken ct, params (string Key, string Value)[] args)
    {
        await using var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value);
        await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task<string?> Scalar(SqliteConnection c, SqliteTransaction t, string sql, CancellationToken ct, params (string Key, string Value)[] args)
    {
        await using var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value);
        var valueObject = await command.ExecuteScalarAsync(ct);
        return valueObject is null or DBNull ? null : valueObject.ToString();
    }
}
