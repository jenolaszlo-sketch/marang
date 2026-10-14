using System.Text.Json;
using Marang.Runs.Adaptive;
using Marang.SessionJournal;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;
using Penghou.Hufu;
using Penghou.Hufu.Cedar;
using Penghou.Hufu.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.RestartProbe;

/// <summary>A bounded qualification operator console over an existing fixture; host authentication is a fixture.</summary>
internal static class SupervisorOperatorProbe
{
    private static readonly Guid Workflow = Guid.Parse("a58f14e7-3eb5-4339-ac6b-5247182d7094");
    private const string Actor = "qualified-operator";
    public static async Task RunAsync(string directory, bool interactive)
    {
        if (!File.Exists(Path.Combine(directory, "execution.db"))) throw new InvalidOperationException("Choose an existing M1/M2 evidence directory.");
        var db = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = Path.Combine(directory, "execution.db"), Pooling = false });
        var revisions = new SqliteAdaptiveRevisionRepository(db); await revisions.InitializeAsync();
        var store = new SqliteSupervisorContinuityStore(db, revisions, a => a == Actor); await store.InitializeAsync();
        var projection = new SupervisorCatchUp(db, a => a == Actor); await projection.InitializeAsync();
        var hufuActor = new AuthorityStoreActor("m1", "host", "qualification");
        var hufu = new SqliteAuthorityStore(Path.Combine(directory, "authority.db"), new StorePolicy());
        var current = new CurrentAuthorityRequestAuthorizer(new AuthorityStoreSnapshotSource(hufu, hufuActor), new CedarAuthorityEvaluator(), new Recorder(db));
        var authority = new SqliteAdaptiveSupervisorAuthority(db, revisions, current,
            new AdaptiveAuthorityProfile("m1", "supervisor", "repository", "reports/readiness.md", "write-file", "m1"), a => a == Actor);
        await authority.InitializeAsync();
        var supervisor = new AdaptiveSupervisor(revisions, authority);
        Guid? reviewedProposal = null; string? reviewedDigest = null;
        var view = await projection.ReadAsync(Workflow, Actor);
        Print(view);
        if (!interactive) return;
        while (true)
        {
            Console.WriteLine("Commands: now, changes, results, blocked, decisions, timeline, revision <id>, proposal, approve, continue, reject, cancel, acknowledge, discuss <question>, dismiss discussion, exit.");
            var command = Console.ReadLine(); if (command is null || command == "exit") return;
            try
            {
                view = await projection.ReadAsync(Workflow, Actor);
                if (command == "now") Print(view);
                else if (command == "changes")
                {
                    foreach (var item in view.ChangesSinceCheckpoint) Console.WriteLine(item.Summary);
                    if (view.EarlierMeaningfulChangeCount > 0) Console.WriteLine($"{view.EarlierMeaningfulChangeCount} earlier changes; inspect revision history.");
                }
                else if (command == "results")
                {
                    foreach (var b in view.Branches.Where(b => b.EvidenceCategory == SupervisorEvidenceCategory.CurrentAuthoritative)) Console.WriteLine(b.Title + ": current accepted result.");
                    foreach (var b in view.PriorResults.Where(b => b.HasResult)) Console.WriteLine(b.Title + ": superseded result retained in history.");
                }
                else if (command == "blocked") foreach (var item in view.Blockers) Console.WriteLine(item);
                else if (command == "decisions") foreach (var item in view.Decisions) Console.WriteLine(item.Kind + ": " + item.Summary + " Options: " + string.Join(", ", item.Options));
                else if (command == "timeline") foreach (var item in view.Timeline) Console.WriteLine($"{item.FromRevisionId ?? "Initial"} → {item.ToRevisionId}: {item.Trigger}; {item.Reason}");
                else if (command.StartsWith("revision ", StringComparison.Ordinal))
                {
                    var detail = await projection.DrillDownAsync(Workflow, command[9..], Actor);
                    Console.WriteLine(detail.Reason);
                    foreach (var b in detail.Work) Console.WriteLine(b.Title + ": " + string.Join(", ", b.Classifications) + ". " + string.Join(" ", b.Reasons));
                }
                else if (command == "proposal")
                {
                    var pending = await revisions.GetPendingAsync(Workflow) ?? throw new WorkflowStateException("No current proposal.");
                    var display = AdaptiveSupervisor.View(await revisions.GetActiveAsync(Workflow), pending, "Fresh authority is evaluated at the decision point", null);
                    Console.WriteLine(display.Explanation);
                    foreach (var item in display.Preserved.Concat(display.Affected).Concat(display.Introduced)) Console.WriteLine(item);
                    reviewedProposal = pending.ProposalId; reviewedDigest = AdaptiveRevisionContracts.Digest(pending);
                }
                else if (command is "approve" or "continue" or "reject")
                {
                    var pending = await revisions.GetPendingAsync(Workflow) ?? throw new WorkflowStateException("No current proposal.");
                    var digest = AdaptiveRevisionContracts.Digest(pending);
                    if (pending.ProposalId != reviewedProposal || digest != reviewedDigest) throw new WorkflowStateException("Inspect the exact current proposal with 'proposal' before deciding.");
                    if (command == "approve") await supervisor.ApproveAsync(pending, digest, Actor);
                    else if (command == "continue") await supervisor.CommitAsync(pending, digest, await authority.FindApprovalAsync(pending.ProposalId, digest));
                    else await supervisor.RejectAsync(pending, digest, Actor);
                    Console.WriteLine(command == "continue" ? "Revision activated. Existing host dispatch resumes its durable runs." : "Exact proposal decision recorded.");
                    if (command != "approve") { reviewedProposal = null; reviewedDigest = null; }
                    await Mirror(directory, revisions);
                }
                else if (command == "cancel") { await supervisor.CancelAsync(Workflow, Actor); await Mirror(directory, revisions); }
                else if (command == "acknowledge") await store.AcknowledgeAsync(Workflow, Actor, view.ActiveRevisionId, view.EvidenceWatermark);
                else if (command == "dismiss discussion")
                {
                    var discussion = await store.GetDiscussionAsync(Workflow) ?? throw new WorkflowStateException("No discussion to dismiss.");
                    await store.DismissDiscussionAsync(Workflow, discussion.DecisionId, Actor, "Operator dismissed the pending discussion; any later choice requires fresh context.");
                    Console.WriteLine("Discussion dismissed. The active workflow is unchanged."); await Mirror(directory, revisions);
                }
                else if (command.StartsWith("discuss ", StringComparison.Ordinal))
                {
                    await store.BeginDiscussionAsync(Workflow, command[8..], ["A concrete candidate and affected work must be established before approval."], [],
                        ["Keep current workflow", "Discuss further"], [], Actor);
                    Console.WriteLine("Discussion context saved. The active workflow is unchanged."); await Mirror(directory, revisions);
                }
                else Console.WriteLine("Command is unavailable. Unresolved delegation actions use the 'supervisor-delegation' recovery surface.");
            }
            catch (Exception e) when (e is WorkflowStateException or WorkflowAuthorizationException or ArgumentException)
            { Console.WriteLine("Decision was not applied: " + e.Message); }
        }
    }
    private static void Print(SupervisorCatchUpView view)
    {
        Console.WriteLine($"{view.ActiveRevisionId}: {view.Goal}\nState: {view.State}. {view.TotalRevisionCount} retained revisions.");
        foreach (var b in view.Branches) Console.WriteLine(b.Title + ": " + b.ExecutionState + "; " + b.EvidenceCategory);
        foreach (var d in view.Decisions) Console.WriteLine("Needs attention: " + d.Summary);
        foreach (var c in view.Conditions) Console.WriteLine(c.Explanation);
    }
    private static async Task Mirror(string directory, SqliteAdaptiveRevisionRepository revisions)
    {
        if (!SessionId.TryParse(File.ReadAllText(Path.Combine(directory, "session-id")), out var id)) throw new InvalidOperationException("Invalid session identity.");
        await using var store = new HongxianSqliteStoreSet(new HongxianSqliteOptions { RootPath = Path.Combine(directory, "hongxian"), Pooling = false });
        await AdaptiveEvidenceMirror.MirrorAsync(revisions, Workflow, store.EventStore, id);
    }
    private sealed class StorePolicy : IAuthorityStoreAuthorizer
    {
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
    }
    private sealed class Recorder(IZhinuSqliteDatabase database) : IAuthorityDecisionRecorder
    {
        public async ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken cancellationToken = default)
        {
            await using var c = await database.OpenAsync(cancellationToken);
            await using var q = c.CreateCommand();
            q.CommandText = "CREATE TABLE IF NOT EXISTS probe_hufu_decisions(document TEXT NOT NULL); INSERT INTO probe_hufu_decisions(document) VALUES($d)";
            q.Parameters.AddWithValue("$d", JsonSerializer.Serialize(new { request.RequestIdentity, decision.Status, decision.SnapshotIdentity }));
            await q.ExecuteNonQueryAsync(cancellationToken); return true;
        }
    }
}
