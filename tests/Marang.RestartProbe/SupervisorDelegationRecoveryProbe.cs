using System.Text.Json;
using Marang.Hufu;
using Marang.Runs.Adaptive;
using Marang.SessionJournal;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;
using Penghou.Hufu;
using Penghou.Hufu.Cedar;
using Penghou.Hufu.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.RestartProbe;

/// <summary>Continues an actually killed M1 provider-start fixture through explicit M2 recovery decisions.</summary>
internal static class SupervisorDelegationRecoveryProbe
{
    private static readonly Guid WorkflowId = Guid.Parse("a58f14e7-3eb5-4339-ac6b-5247182d7094");
    private const string Goal = "Review Marang's README and package contracts; produce a repository readiness report.";
    private const string Actor = "qualified-operator";
    public static async Task RunAsync(string directory, string? barrier, bool interactive = false)
    {
        var database = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = Path.Combine(directory, "execution.db"), Pooling = false });
        var revisions = new SqliteAdaptiveRevisionRepository(database); await revisions.InitializeAsync();
        var journal = new SqliteDelegationRecoveryJournal(database); await journal.InitializeAsync();
        var continuity = new SqliteSupervisorContinuityStore(database, revisions, a => a == Actor); await continuity.InitializeAsync();
        var catchUp = new SupervisorCatchUp(database, a => a == Actor); await catchUp.InitializeAsync();
        var history = await revisions.GetHistoryAsync(WorkflowId);
        if (history.Length == 0) throw new InvalidOperationException("First kill the M1 unknown-provider start fixture; this phase cannot manufacture provider uncertainty.");
        if (history.Length == 1) await AdaptiveRevisionProbe.VerifyUnknownAsync(directory);
        if (await continuity.GetIntentAsync(WorkflowId, history[0].RevisionId) is null)
            await continuity.RecordIntentAsync(WorkflowId, history[0].RevisionId, Goal, SupervisorIntentTrigger.Initial, history[0].Reason,
                ["readme.input", "packages.input"], Actor);
        var hufuActor = new AuthorityStoreActor("m1", "host", "qualification");
        var hufu = new SqliteAuthorityStore(Path.Combine(directory, "authority.db"), new StorePolicy());
        var current = new CurrentAuthorityRequestAuthorizer(new AuthorityStoreSnapshotSource(hufu, hufuActor),
            new CedarAuthorityEvaluator(), new DecisionRecorder(database));
        var authority = new SqliteAdaptiveSupervisorAuthority(database, revisions, current,
            new AdaptiveAuthorityProfile("m1", "supervisor", "repository", "reports/readiness.md", "write-file", "m1"), a => a == Actor);
        await authority.InitializeAsync();
        var supervisor = new AdaptiveSupervisor(revisions, authority);
        var actions = new DelegationRecoveryActions(database, journal, revisions, supervisor, authority); await actions.InitializeAsync();
        var original = history[0]; var old = original.Branches.Single(b => b.LogicalId == "delegated");
        var binding = await journal.GetBindingAsync(old.Execution.Id, old.InputDigest) ?? throw new InvalidOperationException("Missing delegation binding.");
        var record = (await journal.ListAsync(binding.DelegationId)).Single();
        var ports = new ReadReportPorts(directory);
        var executionPorts = new FuwenZhinuExecutionPorts(ports, ports, ports);
        var decisionFile = Path.Combine(directory, "recovery-decision-id");
        DelegationRecoveryDecision decision;
        if (!File.Exists(decisionFile))
        {
            var active = await revisions.GetActiveAsync(WorkflowId);
            var projection = DelegationRecoveryActions.Project(record.Intent.Request.Identity, record, WorkflowId, old.Execution.Id,
                active.RevisionId, await revisions.GetStateDigestAsync(WorkflowId), old.AuthorityDigest, true);
            var view = await catchUp.ReadAsync(WorkflowId, Actor);
            Ensure(view.State == DurableSupervisorState.DelegationUnresolved, "Unknown task must be an explicit unresolved supervisor condition.");
            Ensure(view.Conditions.Single(c => c.LogicalId == "delegated").Actions.Contains("Replace"), "Read/report replacement option must reconstruct from durable state.");
            Ensure(!view.Conditions.Any(c => c.Actions.Contains("Retry")), "Unknown operation must never offer retry.");
            await File.WriteAllTextAsync(Path.Combine(directory, "supervisor-unresolved.json"), JsonSerializer.Serialize(view, Options));
            await actions.WaitAsync(projection, Actor);
            await Barrier(directory, barrier, "unresolved");
            if (interactive)
            {
                Console.WriteLine("The delegated report may still exist. Wait keeps it unresolved. Replace reviews a new read/report revision and fences old result authority; it does not stop the provider operation.");
                Console.WriteLine("Choose wait or replace. Workflow-wide cancellation is a separate current-scope supervisor command.");
                var input = Console.ReadLine();
                if (input == "wait") return;
                if (input != "replace") throw new InvalidOperationException("No concrete replacement choice was made.");
            }
            var specs = original.Branches.Select(b =>
            {
                using var facts = JsonDocument.Parse(JsonSerializer.Deserialize<string>(b.Execution.InputJson!)!);
                return new AdaptiveWorkSpec(b.LogicalId, b.Title, facts.RootElement.GetProperty("Requirement").GetString()!,
                    facts.RootElement.GetProperty("Input").GetString()!, b.AuthorityDigest, b.Provenance, b.Dependencies, b.Execution.Id, true);
            }).ToArray();
            for (var i = 0; i < specs.Length; i++)
                if (specs[i].LogicalId == "delegated") specs[i] = specs[i] with { Requirement = "Produce a local repository recovery report acknowledging unknown external work; never infer provider completion.", ExecutionId = Guid.NewGuid() };
                else if (specs[i].LogicalId == "outcome") specs[i] = specs[i] with { ExecutionId = Guid.NewGuid() };
            var candidate = (await AdaptivePlanAuthoring.AuthorAsync(WorkflowId, "recovery-revision-2", original.RevisionId, Goal,
                "Operator replaces unresolved read/report work after physical provider start without durable handle capture.", "operator-recovery", specs, executionPorts)).Revision;
            decision = await actions.PrepareReplacementAsync(projection, candidate,
                "The old task may still exist; duplicate read/report computation is permitted under this finite profile, while its result authority is fenced atomically.", Actor);
            File.WriteAllText(decisionFile, decision.DecisionId.ToString("D"));
            var proposal = (await revisions.GetPendingAsync(WorkflowId))!;
            await catchUp.CaptureProposalContextAsync(proposal, Actor);
            await continuity.RecordIntentAsync(WorkflowId, candidate.RevisionId, Goal, SupervisorIntentTrigger.Evidence, candidate.Reason, ["unknown-result.json"], Actor);
        }
        else decision = await actions.GetDecisionAsync(Guid.Parse(File.ReadAllText(decisionFile))) ?? throw new InvalidOperationException("Recovery decision disappeared.");
        if (decision.Status != "Committed")
        {
            var approval = await actions.GetReplacementApprovalAsync(decision.DecisionId);
            if (approval is null)
            {
                if (interactive)
                {
                    Console.WriteLine("Approve this exact recovery revision? Type approve, reject, or wait.");
                    var input = Console.ReadLine();
                    if (input == "wait") return;
                    if (input == "reject")
                    {
                        var pending = (await revisions.GetPendingAsync(WorkflowId))!;
                        await supervisor.RejectAsync(pending, AdaptiveRevisionContracts.Digest(pending), Actor); return;
                    }
                    if (input != "approve") throw new InvalidOperationException("Exact revision approval was not supplied.");
                }
                approval = await actions.ApproveReplacementAsync(decision.DecisionId, Actor);
            }
            await Barrier(directory, barrier, "recovery-approved");
            decision = await actions.CompleteReplacementAsync(decision.DecisionId, Actor, approval);
            await Barrier(directory, barrier, "recovery-fenced");
        }
        var revised = await revisions.GetActiveAsync(WorkflowId);
        var replacement = revised.Branches.Single(b => b.LogicalId == "delegated");
        Ensure(replacement.Execution.Id != old.Execution.Id, "Replacement must have a fresh execution identity.");
        var registry = await AdaptivePlanAuthoring.RestoreRegistryAsync(await revisions.GetHistoryAsync(WorkflowId), executionPorts);
        var store = new SqliteWorkflowStore(database);
        await using (var engine = new WorkflowEngine(store, registry, new ZhinuOptions { LeaseDuration = TimeSpan.FromSeconds(2), LeaseRenewalInterval = TimeSpan.FromMilliseconds(250), PollInterval = TimeSpan.FromMilliseconds(20) }))
        {
            await engine.StartAsync(replacement.Execution.WorkflowName, replacement.Execution.WorkflowVersion,
                JsonSerializer.Deserialize<JsonElement>(replacement.Execution.InputJson!), replacement.Execution.Id);
            await engine.ExecuteAsync(replacement.Execution.Id);
        }
        Ensure((await store.GetRunAsync(replacement.Execution.Id))!.Status == WorkflowStatus.Completed, "Authorized replacement read/report execution must complete.");
        var final = await catchUp.ReadAsync(WorkflowId, Actor);
        Ensure(!final.Conditions.Any(c => c.LogicalId == "delegated" && c.RequiresDecision), "Historical unknown task must no longer block active revision.");
        Ensure(final.Branches.Single(b => b.LogicalId == "delegated").EvidenceCategory == SupervisorEvidenceCategory.CurrentAuthoritative, "Only selected completed replacement is authoritative.");
        var providerStarts = File.ReadAllLines(Path.Combine(directory, "provider-starts.log")).Length;
        Ensure(providerStarts == 1, "Recovery must not Start unknown provider operation again.");
        var oldRun = (await store.GetRunAsync(old.Execution.Id))!;
        Ensure(oldRun.Status == WorkflowStatus.Cancelled && oldRun.LeaseGeneration > old.Execution.LeaseGeneration, "Prior execution must remain durably fenced.");
        await using var hongxian = new HongxianSqliteStoreSet(new HongxianSqliteOptions { RootPath = Path.Combine(directory, "hongxian"), Pooling = false });
        Ensure(SessionId.TryParse(File.ReadAllText(Path.Combine(directory, "session-id")), out var session), "Missing durable session.");
        await AdaptiveEvidenceMirror.MirrorAsync(revisions, WorkflowId, hongxian.EventStore, session);
        await File.WriteAllTextAsync(Path.Combine(directory, "supervisor-recovery-result.json"), JsonSerializer.Serialize(new
        {
            ProviderStarts = providerStarts,
            OldOperationMayStillExist = Directory.GetFiles(directory, "*.provider").Length == 1,
            ActiveRevision = final.ActiveRevisionId,
            OldExecutionFenced = true,
            ReplacementCompleted = true,
            ReplacementAuthoritative = true,
            UnknownHistorical = true,
            decision,
            CatchUp = final,
            ProcessId = Environment.ProcessId,
            Limit = "Explicit bounded read/report replacement; full enclosing execution and arbitrary external effects are not inferred."
        }, Options));
    }
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Barrier(string directory, string? requested, string current)
    {
        if (requested != current || File.Exists(Path.Combine(directory, "recovery-barrier-used"))) return;
        File.WriteAllText(Path.Combine(directory, "recovery-barrier-ready"), current);
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    private sealed class ReadReportPorts(string directory) : IActivityExecutor, IContextProvider, IInferenceExecutor
    {
        public async ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default)
        {
            var readme = await File.ReadAllTextAsync(Path.Combine(directory, "readme.input"), cancellationToken);
            var summary = $"Repository README has {readme.Split('\n').Length} lines. Prior provider start remains unknown. Replacement is a local read/report result; no old external effect is adopted.";
            return ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(JsonSerializer.SerializeToElement(summary)));
        }
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class StorePolicy : IAuthorityStoreAuthorizer
    {
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
    }
    private sealed class DecisionRecorder(IZhinuSqliteDatabase database) : IAuthorityDecisionRecorder
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
