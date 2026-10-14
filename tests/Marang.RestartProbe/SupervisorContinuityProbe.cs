using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Marang.Hufu;
using Marang.Runs.Adaptive;
using Marang.SessionJournal;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;
using Penghou.Fuwen.Zhinu;
using Penghou.Fuwen;
using Penghou.Hufu;
using Penghou.Hufu.Cedar;
using Penghou.Hufu.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.RestartProbe;

/// <summary>
/// M2 repository-backed catch-up qualification. It extends the finite M1 read/report scenario with
/// two further revisions and a third evidence-driven revision after a real M1 unknown-handle probe.
/// Every invocation reopens SQLite and reconstructs host services; no in-memory workflow state crosses process death.
/// </summary>
internal static class SupervisorContinuityProbe
{
    private static readonly Guid WorkflowId = Guid.Parse("a58f14e7-3eb5-4339-ac6b-5247182d7094");
    private const string Goal = "Review Marang's README and package contracts; produce a repository readiness report.";
    private const string Operator = "qualified-operator";
    private const string Tenant = "m1";
    private const string TargetChoice = "Assess declared targets and report qualification limits";

    public static async Task RunAsync(string directory, string? barrier, bool resume)
    {
        Directory.CreateDirectory(directory);
        var repository = FindRepositoryRoot();
        var database = CreateDatabase(directory);
        var revisions = new SqliteAdaptiveRevisionRepository(database);
        await revisions.InitializeAsync();
        EnsureRepositoryFixture(repository, directory);

        // The existing M1 fixture performs real Fuwen/Zhinu repository work, Hufu/Cedar approval,
        // and the original user-driven requirement revision. Its completed inventory is the unit
        // carried unchanged through every M2 revision.
        if ((await revisions.GetHistoryAsync(WorkflowId)).Length == 0)
            await AdaptiveRevisionProbe.RunAsync(directory, barrier: null);

        var history = await revisions.GetHistoryAsync(WorkflowId);
        if (history.Length < 2) throw new InvalidOperationException("The M1 repository fixture did not produce its two revisions.");
        var journal = new SqliteDelegationRecoveryJournal(database);
        await journal.InitializeAsync();
        var continuity = new SqliteSupervisorContinuityStore(database, revisions, actor => actor == Operator);
        await continuity.InitializeAsync();
        var catchUp = new SupervisorCatchUp(database, actor => actor == Operator);
        await catchUp.InitializeAsync();
        var workflowStore = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(directory, "execution.db"), Pooling = false });
        var ports = new RepositoryAssessmentPorts(directory, revisions, workflowStore);
        var executionPorts = new FuwenZhinuExecutionPorts(ports, ports, ports);

        var actor = new AuthorityStoreActor(Tenant, "host", "qualification");
        var hufu = new SqliteAuthorityStore(Path.Combine(directory, "authority.db"), new ProbeStorePolicy());
        var currentAuthority = new CurrentAuthorityRequestAuthorizer(new AuthorityStoreSnapshotSource(hufu, actor),
            new CedarAuthorityEvaluator(), new ProbeDecisionRecorder(database));
        var authority = new SqliteAdaptiveSupervisorAuthority(database, revisions, currentAuthority,
            new AdaptiveAuthorityProfile(Tenant, "supervisor", "repository", "reports/readiness.md", "write-file", "m1"),
            name => name == Operator);
        await authority.InitializeAsync();
        var supervisor = new AdaptiveSupervisor(revisions, authority);

        // M1 predates durable intent metadata. Bind its two exact authored objectives to supervisor state.
        await EnsureIntent(continuity, history[0].RevisionId, Goal, SupervisorIntentTrigger.Initial,
            "Original repository inventory and readiness report.", ["README.md", "tests/Marang.Tests/Marang.Tests.csproj"]);
        await EnsureIntent(continuity, history[1].RevisionId, Goal, SupervisorIntentTrigger.User,
            "The operator requested Markdown output and explicit restart limitations.", ["README.md", "tests/Marang.Tests/Marang.Tests.csproj"]);

        var discussion = await continuity.GetDiscussionAsync(WorkflowId);
        if (discussion is null)
        {
            discussion = await continuity.BeginDiscussionAsync(WorkflowId,
                "Confirm whether the report should distinguish declared targets from targets actually executed.",
                ["The repository declares frameworks, but this qualification runs on one host."],
                ["acceptance", "outcome"], [TargetChoice, "Keep the existing scope"],
                ["tests/Marang.Tests/Marang.Tests.csproj", "tests/Marang.RestartProbe/Marang.RestartProbe.csproj"], Operator);
        }
        await Barrier(directory, barrier, "discussion");
        discussion = await continuity.GetDiscussionAsync(WorkflowId)
            ?? throw new InvalidOperationException("Durable discussion disappeared after restart.");
        if (discussion.Status == SupervisorDiscussionStatus.Pending)
        {
            var recoveredView = await catchUp.ReadAsync(WorkflowId, Operator);
            if (recoveredView.Decisions.All(d => d.Id != discussion.DecisionId || d.IsStale))
                throw new InvalidOperationException("Fresh-process catch-up did not reconstruct the exact pending discussion.");
            await continuity.ResolveDiscussionAsync(WorkflowId, discussion.DecisionId, discussion.ParentStateDigest,
                TargetChoice, Operator);
        }

        // Acknowledge R2 before new revisions. The later catch-up must therefore expose every R3/R4 change.
        var oldCheckpoint = await continuity.GetCheckpointAsync(WorkflowId, Operator);
        if (oldCheckpoint is null)
        {
            var oldView = await catchUp.ReadAsync(WorkflowId, Operator);
            await continuity.AcknowledgeAsync(WorkflowId, Operator, oldView.ActiveRevisionId, oldView.EvidenceWatermark);
        }

        history = await revisions.GetHistoryAsync(WorkflowId);
        var pending = await revisions.GetPendingAsync(WorkflowId);
        if (history.Length == 2)
        {
            var candidate = pending?.Candidate ?? await AuthorNext(history, "revision-3", "revision-2",
                "Assess the declared framework targets and report security/API evidence and platform limits in Markdown.",
                "The report requirement expands to include the repository security document, public API contract source, and declared target limits.",
                TargetChange.Targets, executionPorts, directory);
            pending = await ProposeOrRecover(candidate, pending, "The inventory and unaffected README review remain authoritative. The contract/security/API assessment and final report are replaced.",
                revisions, authority, supervisor, catchUp, continuity, hufu, actor, barrier, directory, "proposal", "approval");
            await CommitPending(pending, authority, supervisor, hufu, actor, barrier, directory, "after-fencing");
            pending = null;
            history = await revisions.GetHistoryAsync(WorkflowId);
            await RunRevisionAsync(history[^1], revisions, workflowStore, executionPorts, ports);
        }

        if (history.Length == 3)
        {
            // A process killed after atomic fencing resumes the same committed executions and finishes them here.
            await RunRevisionAsync(history[^1], revisions, workflowStore, executionPorts, ports);
            await Barrier(directory, barrier, "between-revisions");
            var unknownDirectory = Path.Combine(directory, "m1-unknown-handle");
            if (!File.Exists(Path.Combine(unknownDirectory, "unknown-result.json")))
                await RunActualM1UnknownProbeAsync(unknownDirectory);
            File.Copy(Path.Combine(unknownDirectory, "unknown-result.json"), Path.Combine(directory, "m1-unknown-result.json"), true);
            File.Copy(Path.Combine(unknownDirectory, "unknown-catch-up.md"), Path.Combine(directory, "m1-unknown-catch-up.md"), true);

            var candidate = pending?.Candidate ?? await AuthorNext(history, "revision-4", "revision-3",
                "Correct the Markdown readiness report after the M1 unknown provider handle capture failure.",
                "The actual M1 unknown-provider probe stopped before handle capture and proved automatic restart is unsafe; revise the prior conclusion using that failure evidence.",
                TargetChange.UnknownHandle, executionPorts, directory, Path.Combine(directory, "m1-unknown-result.json"));
            pending = await ProposeOrRecover(candidate, pending, "M1 observed unknown provider state before durable handle capture. Preserve the evidence, require operator resolution, and replace unsupported restart-safety conclusions.",
                revisions, authority, supervisor, catchUp, continuity, hufu, actor, barrier, directory, "proposal-4", "approval-4");
            await CommitPending(pending, authority, supervisor, hufu, actor, barrier, directory, "after-fencing");
            pending = null;
            history = await revisions.GetHistoryAsync(WorkflowId);
            await RunRevisionAsync(history[^1], revisions, workflowStore, executionPorts, ports);
        }
        if (history.Length != 4) throw new InvalidOperationException("M2 expected one workflow with revisions 1 through 4.");
        await RunRevisionAsync(history[^1], revisions, workflowStore, executionPorts, ports);
        var current = await revisions.GetActiveAsync(WorkflowId);
        var inventory = current.Branches.Single(b => b.LogicalId == "inventory");
        var run = await workflowStore.GetRunAsync(inventory.Execution.Id);
        if (run?.Status != WorkflowStatus.Completed || run.OutputJson is null)
            throw new InvalidOperationException("The carried inventory must remain completed and authoritative.");
        var inventoryStarts = Path.Combine(directory, "inventory.starts");
        var inventoryCount = File.Exists(inventoryStarts) ? File.ReadAllLines(inventoryStarts).Length : 0;
        var invocationCount = File.Exists(Path.Combine(directory, "provider-starts.log"))
            ? File.ReadAllLines(Path.Combine(directory, "provider-starts.log")).Length : 0;
        var outcomeBranch = current.Branches.Single(b => b.LogicalId == "outcome");
        var outcomeRun = await workflowStore.GetRunAsync(outcomeBranch.Execution.Id);
        var outcomeText = outcomeRun?.OutputJson is null ? "" : JsonSerializer.Deserialize<string>(outcomeRun.OutputJson) ?? "";

        var catchup = await catchUp.ReadAsync(WorkflowId, Operator);
        var transitionB = await catchUp.DrillDownAsync(WorkflowId, "revision-3", Operator);
        var transitionC = await catchUp.DrillDownAsync(WorkflowId, "revision-4", Operator);
        var expectedEvidence = BuildRepositoryEvidence(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "repository-evidence.json"),
            JsonSerializer.Serialize(expectedEvidence, JsonOptions));
        var failureEvidence = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(directory, "m1-unknown-result.json")));
        var beforeAcknowledging = await catchUp.ReadAsync(WorkflowId, Operator);
        var alreadyAcknowledged = beforeAcknowledging.LastAcknowledgedRevision == current.RevisionId;
        if (!alreadyAcknowledged && (beforeAcknowledging.ChangesSinceCheckpoint.Length == 0 ||
            !beforeAcknowledging.ChangesSinceCheckpoint.Any(c => c.Summary.Contains("revision-3", StringComparison.Ordinal)) ||
            !beforeAcknowledging.ChangesSinceCheckpoint.Any(c => c.Summary.Contains("revision-4", StringComparison.Ordinal))))
            throw new InvalidOperationException("The operator checkpoint did not expose all new revision changes.");
        await File.WriteAllTextAsync(Path.Combine(directory, "checkpoint-review.json"), JsonSerializer.Serialize(beforeAcknowledging, JsonOptions));
        await Barrier(directory, barrier, "checkpoint");
        if (!resume && barrier == "unused") return;
        var checkpoint = await continuity.GetCheckpointAsync(WorkflowId, Operator);
        if (checkpoint is null || checkpoint.RevisionId != current.RevisionId)
            await continuity.AcknowledgeAsync(WorkflowId, Operator, beforeAcknowledging.ActiveRevisionId, beforeAcknowledging.EvidenceWatermark);
        var final = await catchUp.ReadAsync(WorkflowId, Operator);
        var revisionsInOrder = await revisions.GetHistoryAsync(WorkflowId);
        var r1Delegated = revisionsInOrder[0].Branches.Single(b => b.LogicalId == "delegated");
        var r1Binding = await journal.GetBindingAsync(
            r1Delegated.Execution.Id,
            AdaptiveRevisionContracts.Digest(JsonSerializer.Deserialize<string>(r1Delegated.Execution.InputJson!)))
            ?? throw new InvalidOperationException("The original delegated branch has no durable binding.");
        var providerHistory = await journal.ListAsync(r1Binding.DelegationId);
        var hongxianEvidence = await MirrorAndVerifyEvidenceAsync(directory, revisions);
        var result = new
        {
            ActiveRevision = final.ActiveRevisionId,
            RevisionCount = revisionsInOrder.Length,
            RevisionLineage = revisionsInOrder.Select(r => r.RevisionId).ToArray(),
            LineageContinuous = revisionsInOrder.Select((r, i) => i == 0 ? r.ParentRevisionId is null : r.ParentRevisionId == revisionsInOrder[i - 1].RevisionId).All(x => x),
            InventoryExecutionStableAcrossRevisions = revisionsInOrder.Select(r => r.Branches.Single(b => b.LogicalId == "inventory").Execution.Id).Distinct().Count() == 1,
            InventoryExecutionId = inventory.Execution.Id,
            InventoryInvocationCount = inventoryCount,
            CompletedInventoryExplicitlyReused = transitionB.Work.Single(w => w.LogicalId == "inventory").Classifications.Contains("Reused"),
            FencedResultsRemainHistorical = final.PriorResults.Any(b => b.LogicalId == "acceptance" && b.EvidenceCategory == SupervisorEvidenceCategory.Superseded)
                && final.Branches.Any(b => b.LogicalId == "acceptance" && b.EvidenceCategory == SupervisorEvidenceCategory.CurrentAuthoritative),
            CurrentReportReplaced = revisionsInOrder.Select(r => r.Branches.Single(b => b.LogicalId == "outcome").Execution.Id).Distinct().Count() == revisionsInOrder.Length,
            RepositoryEvidencePresent = File.Exists(Path.Combine(directory, "repository-evidence.json")) && expectedEvidence.SecurityLines > 0 && expectedEvidence.ApiSourceLines > 0,
            FreshProcessRecovered = resume && (await continuity.GetIntentAsync(WorkflowId, current.RevisionId)) is not null,
            FreshAuthorityReattached = File.Exists(Path.Combine(directory, "authority.db")) && await CountAuthorityApprovalsAsync(database) >= 3,
            CurrentReportCompleted = outcomeRun?.Status == WorkflowStatus.Completed && outcomeText.Contains("unknown provider work requires operator resolution", StringComparison.OrdinalIgnoreCase),
            CurrentReport = outcomeText,
            ProviderStartsAcrossM1Revisions = invocationCount,
            SupersededDelegationHasDurableResult = providerHistory.Any(r => r.Result is not null),
            TargetAssessment = new { DeclaredFrameworks = expectedEvidence.DeclaredFrameworks, OperatingSystems = new[] { "Linux", "Windows" }, Claim = "Repository declaration and read-report assessment only; Linux/Windows builds were not run by this probe." },
            UnknownProviderFailureEvidence = failureEvidence,
            CatchUpState = final.State.ToString(),
            CatchUpBranches = final.Branches.Select(b => new { b.LogicalId, b.ExecutionState, Category = b.EvidenceCategory.ToString() }).ToArray(),
            ScenarioB = transitionB,
            ScenarioC = transitionC,
            HongxianEvidence = hongxianEvidence,
            Pass = final.RevisionCount() == 4 && inventoryCount == 1 && final.LastAcknowledgedRevision == current.RevisionId &&
                final.Branches.All(b => b.ExecutionState == "Completed") && (alreadyAcknowledged || beforeAcknowledging.ChangesSinceCheckpoint.Length > 0) &&
                outcomeRun?.Status == WorkflowStatus.Completed && outcomeText.Contains("unknown provider work requires operator resolution", StringComparison.OrdinalIgnoreCase)
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "continuity-result.json"), JsonSerializer.Serialize(result, JsonOptions));
        await File.WriteAllTextAsync(Path.Combine(directory, "demonstration.md"), BuildDemonstration(final, expectedEvidence, outcomeText));
    }

    private static async Task<object> MirrorAndVerifyEvidenceAsync(string directory, SqliteAdaptiveRevisionRepository revisions)
    {
        if (!SessionId.TryParse(await File.ReadAllTextAsync(Path.Combine(directory, "session-id")), out var sessionId))
            throw new InvalidOperationException("M1's durable Hongxian session identity is invalid.");
        await using var hongxian = new HongxianSqliteStoreSet(new HongxianSqliteOptions
        { RootPath = Path.Combine(directory, "hongxian"), Pooling = false });
        await AdaptiveEvidenceMirror.MirrorAsync(revisions, WorkflowId, hongxian.EventStore, sessionId);
        var page = await hongxian.EventStore.ReadPageAsync(new SessionEventPageRequest(sessionId, 0, 1000));
        var kinds = page.Events.Where(e => e.PayloadJson is not null).Select(e =>
        {
            using var payload = JsonDocument.Parse(e.PayloadJson!);
            return payload.RootElement.TryGetProperty("Kind", out var kind) ? kind.GetString() : null;
        }).Where(kind => kind is not null).ToArray();
        foreach (var required in new[] { "SupervisorIntentRecorded", "SupervisorDiscussionOpened", "SupervisorProposalReviewed", "RevisionProposalCreated", "RevisionApproved" })
            if (!kinds.Contains(required, StringComparer.Ordinal))
                throw new InvalidOperationException("Hongxian evidence mirror is missing supervisor event " + required + ".");
        await AdaptiveEvidenceMirror.MirrorAsync(revisions, WorkflowId, hongxian.EventStore, sessionId);
        var replay = await hongxian.EventStore.ReadPageAsync(new SessionEventPageRequest(sessionId, 0, 1000));
        if (page.Events.Count != replay.Events.Count) throw new InvalidOperationException("Supervisor mirroring duplicated durable session evidence.");
        return new { EventCount = page.Events.Count, Kinds = kinds, HasMore = page.HasMore, ReplayIdempotent = true };
    }

    private static async Task<AdaptiveProposal> ProposeOrRecover(AdaptiveRevision candidate, AdaptiveProposal? pending,
        string impact, SqliteAdaptiveRevisionRepository revisions, SqliteAdaptiveSupervisorAuthority authority,
        AdaptiveSupervisor supervisor, SupervisorCatchUp catchUp, SqliteSupervisorContinuityStore continuity,
        SqliteAuthorityStore hufu, AuthorityStoreActor actor, string? barrier, string directory,
        string proposalBarrier, string approvalBarrier)
    {
        if (pending is not null)
        {
            var recoveredView = await catchUp.ReadAsync(WorkflowId, Operator);
            var recoveredDecision = recoveredView.Decisions.SingleOrDefault(d => d.Id == pending.ProposalId.ToString("D"));
            if (recoveredDecision is null || recoveredDecision.IsStale)
                throw new InvalidOperationException("Fresh-process catch-up did not reconstruct the exact pending revision proposal.");
            var durableReceipt = await authority.FindApprovalAsync(pending.ProposalId, AdaptiveRevisionContracts.Digest(pending));
            if (durableReceipt is null && recoveredView.State != DurableSupervisorState.AwaitingApproval)
                throw new InvalidOperationException("Pending proposal state differs from its durable approval state.");
            if (durableReceipt is not null && recoveredView.State != DurableSupervisorState.ReadyToApply)
                throw new InvalidOperationException("Stored exact approval was not reconstructed as ready to apply.");
        }
        else
        {
            await PublishAuthority(hufu, actor, candidate.ParentRevisionId!);
            var proposed = await supervisor.ProposeAsync(candidate, impact);
            pending = proposed.Proposal;
            await catchUp.CaptureProposalContextAsync(pending, Operator);
            var revision = pending.Candidate;
            var trigger = revision.RevisionId == "revision-4" ? SupervisorIntentTrigger.Evidence : SupervisorIntentTrigger.User;
            var refs = revision.RevisionId == "revision-4"
                ? new[] { "m1-unknown-result.json", "m1-unknown-catch-up.md" }
                : new[] { "README.md", "SECURITY.md", "tests/Marang.Tests/Marang.Tests.csproj", "src/Marang.Runs/Adaptive/AdaptiveSupervisor.cs" };
            if (await continuity.GetIntentAsync(WorkflowId, revision.RevisionId) is null)
                await continuity.RecordIntentAsync(WorkflowId, revision.RevisionId, Goal, trigger, revision.Reason, refs, Operator);
        }
        await Barrier(directory, barrier, proposalBarrier);
        var exactProposal = pending ?? throw new InvalidOperationException("Proposal was not created or restored.");
        var digest = AdaptiveRevisionContracts.Digest(exactProposal);
        var receipt = await authority.FindApprovalAsync(exactProposal.ProposalId, digest);
        if (receipt is null) receipt = await supervisor.ApproveAsync(exactProposal, digest, Operator);
        await Barrier(directory, barrier, approvalBarrier);
        return exactProposal;
    }

    private static async Task CommitPending(AdaptiveProposal proposal, SqliteAdaptiveSupervisorAuthority authority,
        AdaptiveSupervisor supervisor, SqliteAuthorityStore hufu,
        AuthorityStoreActor actor, string? barrier, string directory, string fenceBarrier)
    {
        var digest = AdaptiveRevisionContracts.Digest(proposal);
        var receipt = await authority.FindApprovalAsync(proposal.ProposalId, digest);
        await supervisor.CommitAsync(proposal, digest, receipt);
        await Barrier(directory, barrier, fenceBarrier);
    }

    private static async Task<AdaptiveRevision> AuthorNext(AdaptiveRevision[] history, string revisionId,
        string parentId, string reason, string rationale, TargetChange change, FuwenZhinuExecutionPorts ports,
        string fixtureDirectory, string? failureEvidencePath = null)
    {
        var active = history.Single(r => r.RevisionId == parentId);
        var provenance = RepositoryDigest(fixtureDirectory);
        var specs = active.Branches.Select(branch =>
        {
            var input = JsonSerializer.Deserialize<string>(branch.Execution.InputJson!)!;
            using var doc = JsonDocument.Parse(input);
            return new AdaptiveWorkSpec(branch.LogicalId, branch.Title,
                doc.RootElement.GetProperty("Requirement").GetString()!,
                doc.RootElement.GetProperty("Input").GetString()!, branch.AuthorityDigest,
                branch.Provenance, branch.Dependencies, branch.Execution.Id, branch.AcceptReuse);
        }).ToArray();
        void Replace(string id, string requirement, string? input = null)
        {
            var index = Array.FindIndex(specs, s => s.LogicalId == id);
            if (index < 0) throw new InvalidOperationException("Expected finite profile branch missing: " + id);
            specs[index] = specs[index] with { Requirement = requirement, Input = input ?? specs[index].Input, ExecutionId = Guid.NewGuid() };
        }
        switch (change)
        {
            case TargetChange.Targets:
                var reviewInput = Hash(File.ReadAllText(Path.Combine(fixtureDirectory, "readme.snapshot")) +
                    File.ReadAllText(Path.Combine(fixtureDirectory, "security.snapshot")) + File.ReadAllText(Path.Combine(fixtureDirectory, "api.snapshot")));
                var csproj = File.ReadAllText(Path.Combine(fixtureDirectory, "tests.snapshot"));
                var targetInput = Hash(csproj);
                Replace("review", "Review README guidance, SECURITY.md boundaries, and the public adaptive supervisor API contract.", reviewInput);
                Replace("acceptance", "Assess declared .NET 8 and .NET 10 targets; identify Linux and Windows coverage limits without claiming builds.", targetInput);
                var authority = specs.First(s => s.LogicalId == "inventory").AuthorityDigest;
                specs = specs.Where(s => s.LogicalId != "outcome").ToArray();
                specs = [.. specs,
                    new AdaptiveWorkSpec("platform-linux", "Declared Linux target assessment",
                        "Read declared frameworks and identify Linux qualification limits; do not claim an OS build.", targetInput,
                        authority, provenance, [], Guid.NewGuid(), true),
                    new AdaptiveWorkSpec("platform-windows", "Declared Windows target assessment",
                        "Read declared frameworks and identify Windows qualification limits; do not claim an OS build.", targetInput,
                        authority, provenance, [], Guid.NewGuid(), true)];
                specs = [.. specs, new AdaptiveWorkSpec("outcome", "Final report",
                    "Report security/API findings, declared target frameworks, and platform qualification limits; distinguish repository evidence from executed validation.",
                    provenance, authority, provenance, specs.Select(s => s.LogicalId).ToArray(), Guid.NewGuid(), true)];
                break;
            case TargetChange.UnknownHandle:
                if (failureEvidencePath is null || !File.Exists(failureEvidencePath))
                    throw new InvalidOperationException("Evidence-driven revision requires the captured M1 unknown-handle result.");
                var failureJson = File.ReadAllText(failureEvidencePath);
                var evidenceDigest = Hash(failureJson);
                var acceptanceIndex = Array.FindIndex(specs, s => s.LogicalId == "acceptance");
                specs[acceptanceIndex] = specs[acceptanceIndex] with
                { Requirement = "Correct restart conclusions using the M1 unknown provider handle capture failure evidence.", Input = evidenceDigest, Provenance = evidenceDigest, ExecutionId = Guid.NewGuid() };
                var delegatedIndex = Array.FindIndex(specs, s => s.LogicalId == "delegated");
                specs[delegatedIndex] = specs[delegatedIndex] with
                { Requirement = "Describe unresolved provider state when start occurred but durable handle capture did not complete.", Input = evidenceDigest, Provenance = evidenceDigest, ExecutionId = Guid.NewGuid() };
                var outcomeIndex = Array.FindIndex(specs, s => s.LogicalId == "outcome");
                specs[outcomeIndex] = specs[outcomeIndex] with
                { Requirement = "Produce the corrected Markdown readiness report and state that unknown provider work requires operator resolution.", Input = evidenceDigest, Provenance = evidenceDigest, ExecutionId = Guid.NewGuid() };
                break;
        }
        var registry = await AdaptivePlanAuthoring.RestoreRegistryAsync(history, ports);
        var authored = await AdaptivePlanAuthoring.AuthorAsync(WorkflowId, revisionId, parentId, Goal, rationale,
            "m2-deterministic-planner", specs, ports, registry);
        return authored.Revision;
    }

    private static async Task RunRevisionAsync(AdaptiveRevision revision, SqliteAdaptiveRevisionRepository revisions,
        SqliteWorkflowStore workflowStore, FuwenZhinuExecutionPorts executionPorts, RepositoryAssessmentPorts ports)
    {
        var history = await revisions.GetHistoryAsync(WorkflowId);
        var registry = await AdaptivePlanAuthoring.RestoreRegistryAsync(history, executionPorts);
        ports.Bind(history.SelectMany(r => r.Branches));
        await using var engine = new WorkflowEngine(workflowStore, registry, new ZhinuOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(250)
        });

        // The admitted M1 profile is independent branches followed by one join. Run in dependency order,
        // waiting on the durable run row so a process restart can replay dispatch safely.
        foreach (var branch in revision.Branches)
        {
            var dependencies = branch.Dependencies.Select(id => revision.Branches.Single(b => b.LogicalId == id).Execution.Id).ToArray();
            foreach (var dependency in dependencies)
            {
                var prerequisite = await workflowStore.GetRunAsync(dependency);
                if (prerequisite?.Status != WorkflowStatus.Completed)
                    throw new InvalidOperationException("Revision dispatch reached a branch before its prerequisite completed: " + branch.Title);
            }
            var state = await workflowStore.GetRunAsync(branch.Execution.Id);
            if (state?.Status == WorkflowStatus.Completed) continue;
            if (state?.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled)
                throw new InvalidOperationException("Current repository assessment failed: " + branch.Title + "; " + state.Error?.Message);
            var input = JsonSerializer.Deserialize<JsonElement>(branch.Execution.InputJson!);
            await engine.StartAsync(branch.Execution.WorkflowName, branch.Execution.WorkflowVersion, input, branch.Execution.Id);
            var until = DateTimeOffset.UtcNow.AddSeconds(90);
            Task? execution = null;
            while (true)
            {
                state = await workflowStore.GetRunAsync(branch.Execution.Id);
                if (state?.Status == WorkflowStatus.Completed) break;
                if (state?.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled)
                    throw new InvalidOperationException("Current repository assessment failed: " + branch.Title + "; " + state.Error?.Message);
                execution ??= engine.ExecuteAsync(branch.Execution.Id);
                if (execution.IsCompleted)
                {
                    await execution;
                    execution = null;
                    await Task.Delay(100);
                }
                else await Task.WhenAny(execution, Task.Delay(100));
                if (DateTimeOffset.UtcNow >= until)
                    throw new TimeoutException("A persisted Fuwen/Zhinu branch did not complete: " + branch.Title);
            }
        }
    }

    private static void EnsureRepositoryFixture(string repository, string directory)
    {
        foreach (var (source, target) in new[]
        {
            ("README.md", "readme.snapshot"), ("SECURITY.md", "security.snapshot"),
            (Path.Combine("tests", "Marang.Tests", "Marang.Tests.csproj"), "tests.snapshot"),
            (Path.Combine("src", "Marang.Runs", "Adaptive", "AdaptiveSupervisor.cs"), "api.snapshot")
        })
        {
            var path = Path.Combine(directory, target);
            if (!File.Exists(path)) File.Copy(Path.Combine(repository, source), path);
        }
    }

    private static async Task EnsureIntent(SqliteSupervisorContinuityStore store, string revision, string goal,
        SupervisorIntentTrigger trigger, string rationale, string[] refs)
    {
        if (await store.GetIntentAsync(WorkflowId, revision) is null)
            await store.RecordIntentAsync(WorkflowId, revision, goal, trigger, rationale, refs, Operator);
    }

    private static async Task RunActualM1UnknownProbeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var probe = Environment.ProcessPath;
        var assembly = typeof(AdaptiveRevisionProbe).Assembly.Location;
        var first = StartProbe(probe!, assembly, directory, "adaptive", "unknown-provider");
        var stdout = first.StandardOutput.ReadToEndAsync();
        var stderr = first.StandardError.ReadToEndAsync();
        var limit = DateTimeOffset.UtcNow.AddSeconds(120);
        while (!File.Exists(Path.Combine(directory, "barrier-ready")))
        {
            if (first.HasExited) throw new InvalidOperationException("M1 unknown-provider probe exited before its process-kill barrier: " + await stderr);
            if (DateTimeOffset.UtcNow > limit)
            {
                first.Kill(true);
                throw new TimeoutException("M1 unknown-provider probe did not reach handle-capture barrier.");
            }
            await Task.Delay(25);
        }
        first.Kill(entireProcessTree: true);
        await first.WaitForExitAsync();
        _ = await stdout; _ = await stderr;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var verify = StartProbe(probe!, assembly, directory, "adaptive-unknown", null);
        var verifyOutput = await verify.StandardOutput.ReadToEndAsync();
        var verifyError = await verify.StandardError.ReadToEndAsync();
        await verify.WaitForExitAsync();
        if (verify.ExitCode != 0) throw new InvalidOperationException("M1 unknown-handle verification failed: " + verifyOutput + verifyError);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "unknown-result.json")));
        if (result.RootElement.GetProperty("RequiresOperatorResolution").GetBoolean() != true || result.RootElement.GetProperty("ProviderStarts").GetInt32() != 1)
            throw new InvalidOperationException("M1 unknown-handle evidence did not prove fail-closed recovery.");
        await ReconstructUnknownCatchUpAsync(directory, result.RootElement.Clone());
    }

    private static async Task ReconstructUnknownCatchUpAsync(string directory, JsonElement m1Result)
    {
        var database = CreateDatabase(directory);
        var revisions = new SqliteAdaptiveRevisionRepository(database); await revisions.InitializeAsync();
        var journal = new SqliteDelegationRecoveryJournal(database); await journal.InitializeAsync();
        var active = await revisions.GetActiveAsync(WorkflowId);
        var branch = active.Branches.Single(b => b.LogicalId == "delegated");
        var binding = await journal.GetBindingAsync(branch.Execution.Id,
            AdaptiveRevisionContracts.Digest(JsonSerializer.Deserialize<string>(branch.Execution.InputJson!)))
            ?? throw new InvalidOperationException("The M1 unknown attempt lost its branch binding.");
        var record = (await journal.ListAsync(binding.DelegationId)).Single();
        var actor = new AuthorityStoreActor(Tenant, "host", "qualification");
        var hufu = new SqliteAuthorityStore(Path.Combine(directory, "authority.db"), new ProbeStorePolicy());
        var current = new CurrentAuthorityRequestAuthorizer(new AuthorityStoreSnapshotSource(hufu, actor),
            new CedarAuthorityEvaluator(), new ProbeDecisionRecorder(database));
        var authority = new SqliteAdaptiveSupervisorAuthority(database, revisions, current,
            new AdaptiveAuthorityProfile(Tenant, "supervisor", "repository", "reports/readiness.md", "write-file", "m1"),
            name => name == Operator);
        await authority.InitializeAsync();
        var supervisor = new AdaptiveSupervisor(revisions, authority);
        var actions = new DelegationRecoveryActions(database, journal, revisions, supervisor, authority);
        await actions.InitializeAsync();
        var stateDigest = await revisions.GetStateDigestAsync(WorkflowId);
        var projection = DelegationRecoveryActions.Project(record.Intent.Request.Identity, record, WorkflowId,
            branch.Execution.Id, active.RevisionId, stateDigest, branch.AuthorityDigest, true);
        if (!projection.RequiresDecision || projection.Classification != DelegationRecoveryClassification.ProviderStateUnknown)
            throw new InvalidOperationException("Fresh recovery projection did not preserve the M1 unknown provider state.");
        var decision = await actions.WaitAsync(projection, Operator);
        var continuity = new SqliteSupervisorContinuityStore(database, revisions, actorId => actorId == Operator);
        await continuity.InitializeAsync();
        if (await continuity.GetIntentAsync(WorkflowId, active.RevisionId) is null)
            await continuity.RecordIntentAsync(WorkflowId, active.RevisionId, Goal, SupervisorIntentTrigger.Initial,
                "M1 unknown provider state requires explicit operator resolution.", ["unknown-result.json", "provider-starts.log"], Operator);
        var catchup = new SupervisorCatchUp(database, actorId => actorId == Operator); await catchup.InitializeAsync();
        var view = await catchup.ReadAsync(WorkflowId, Operator);
        if (!view.Conditions.Any(c => c.RequiresDecision && c.Classification == nameof(DelegationRecoveryClassification.ProviderStateUnknown)))
            throw new InvalidOperationException("Durable catch-up did not surface the unknown provider decision.");
        var enriched = new
        {
            State = "UnknownAfterRecovery",
            RequiresOperatorResolution = true,
            ProviderStarts = m1Result.GetProperty("ProviderStarts").GetInt32(),
            ActiveRevision = active.RevisionId,
            FreshProcessRecoveryClassification = projection.Classification.ToString(),
            RecoveryAction = decision.ChosenOption,
            RecoveryDecisionPersisted = (await actions.ListDecisionsAsync(WorkflowId)).Any(d => d.DecisionId == decision.DecisionId),
            CatchUpState = view.State.ToString(),
            PendingCondition = view.Conditions.Single(c => c.RequiresDecision).Explanation,
            DurableDatabase = Path.Combine(directory, "execution.db")
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "unknown-result.json"), JsonSerializer.Serialize(enriched, JsonOptions));
        await File.WriteAllTextAsync(Path.Combine(directory, "unknown-catch-up.md"),
            "The fresh process reconstructed an unknown provider operation from SQLite. It recorded Wait, exposed a pending operator decision, and confirmed provider start count stayed at one. No retry or replacement ran automatically.\n");
    }

    private static Process StartProbe(string executable, string assembly, string directory, string phase, string? barrier)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(assembly);
        foreach (var arg in new[] { "--phase", phase, "--dir", directory }) info.ArgumentList.Add(arg);
        if (barrier is not null) { info.ArgumentList.Add("--barrier"); info.ArgumentList.Add(barrier); }
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start nested M1 evidence process.");
    }

    private static async Task Barrier(string directory, string? requested, string current)
    {
        if (requested != current || File.Exists(Path.Combine(directory, "barrier-used"))) return;
        await File.WriteAllTextAsync(Path.Combine(directory, "barrier-ready"), current);
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static RepositoryEvidence BuildRepositoryEvidence(string directory)
    {
        var readme = File.ReadAllText(Path.Combine(directory, "readme.snapshot"));
        var security = File.ReadAllText(Path.Combine(directory, "security.snapshot"));
        var tests = File.ReadAllText(Path.Combine(directory, "tests.snapshot"));
        var source = File.ReadAllText(Path.Combine(directory, "api.snapshot"));
        var declared = tests.Contains("net8.0", StringComparison.Ordinal) && tests.Contains("net10.0", StringComparison.Ordinal)
            ? new[] { "net8.0", "net10.0" } : Array.Empty<string>();
        return new RepositoryEvidence(readme.Split('\n').Length, security.Split('\n').Length,
            source.Split('\n').Length, declared, "tests/Marang.Tests/Marang.Tests.csproj",
            "SECURITY.md", "src/Marang.Runs/Adaptive/AdaptiveSupervisor.cs", Hash(tests), Hash(security), Hash(source),
            "Declared target and platform coverage is read from repository files. This process probe executes only on its current host and makes no Linux or Windows build claim.");
    }

    private static string RepositoryDigest(string directory)
    {
        var material = string.Join("\n", new[] { "readme.snapshot", "security.snapshot", "tests.snapshot", "api.snapshot" }
            .Select(p => File.ReadAllText(Path.Combine(directory, p))));
        return Hash(material);
    }

    private static async Task<int> CountAuthorityApprovalsAsync(SqliteDatabase database)
    {
        await using var c = await database.OpenAsync(CancellationToken.None);
        await using var q = c.CreateCommand(); q.CommandText = "SELECT COUNT(*) FROM adaptive_operator_approvals";
        return Convert.ToInt32(await q.ExecuteScalarAsync());
    }

    private static async Task PublishAuthority(SqliteAuthorityStore store, AuthorityStoreActor actor, string parent)
    {
        var context = new AuthenticatedAuthorityContext(Tenant, "supervisor", WorkflowId.ToString("D"), parent, "m1");
        var now = DateTimeOffset.UtcNow;
        // The M1 fixture already published sequence 1 for revision-1. Hufu treats the
        // supervisor/run as one versioned authority stream, so later parent revisions
        // must extend that stream instead of retrying every publish at expectedVersion 0.
        var revisionNumber = int.Parse(parent[(parent.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var expectedVersion = revisionNumber - 1;
        var published = await store.PublishAsync(new AuthorityPublishCommand("m2-issue-" + parent, actor,
            new AuthoritySnapshot(context, $"m1-report-grant/{revisionNumber}", [new AuthorityLayer("report", [new AuthorityGrant(
                "report-only", [AuthorityAction.WriteFile], new AuthorityScope("repository", "reports", AuthorityScopeKind.Subtree), [],
                now.AddMinutes(-1), now.AddDays(1))])], [], now.AddDays(1)), expectedVersion));
        if (published.Status != AuthorityMutationStatus.Applied)
            throw new InvalidOperationException($"Unable to publish fresh report authority for {parent}: {published.Status}.");
    }

    private static string BuildDemonstration(SupervisorCatchUpView view, RepositoryEvidence repo, string outcome) =>
        $"# Supervisor continuity repository review\n\n" +
        $"Active revision: {view.ActiveRevisionId}. Durable goal: {view.Goal}\n\n" +
        $"The same completed Fuwen inventory execution remained authoritative across four revisions and ran once. The report change added security and public API review, then assessed declared {string.Join(" and ", repo.DeclaredFrameworks)} targets and Linux/Windows qualification limits from repository files. The probe ran on its current host only; it did not claim Linux or Windows builds.\n\n" +
        $"A fresh M1 child process was killed after provider start and before durable handle capture. Its recovery result was `UnknownAfterRecovery` with one provider start. A new process reconstructed the condition from SQLite, recorded `Wait`, and kept the decision unresolved until an operator acts.\n\n" +
        $"Current supervisor state: {view.State}. Pending decisions: {view.Decisions.Length}. Unresolved conditions: {view.Conditions.Length}. Checkpoint: {view.LastAcknowledgedRevision}.\n\n" +
        $"Current report excerpt:\n\n> {outcome.Split('\n').FirstOrDefault() ?? "(empty)"}\n";

    private static SqliteDatabase CreateDatabase(string directory) => new(new ZhinuSqliteOptions
    { DatabasePath = Path.Combine(directory, "execution.db"), Pooling = false, BusyTimeout = TimeSpan.FromSeconds(10) });
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static JsonSerializerOptions JsonOptions => new() { WriteIndented = true };
    private static string FindRepositoryRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "Marang.slnx"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("Repository-backed probe must run from a repository build.");
    }

    private enum TargetChange { Targets, UnknownHandle }
    private sealed record RepositoryEvidence(int ReadmeLines, int SecurityLines, int ApiSourceLines,
        string[] DeclaredFrameworks, string TestProject, string SecurityDocument, string ApiSource,
        string TestProjectHash, string SecurityHash, string ApiHash, string Assessment);
    private sealed class RepositoryAssessmentPorts(string directory, SqliteAdaptiveRevisionRepository revisions,
        SqliteWorkflowStore workflowStore) : IActivityExecutor, IContextProvider, IInferenceExecutor
    {
        private readonly Dictionary<string, Guid> executionByInput = new(StringComparer.Ordinal);
        public void Bind(IEnumerable<AdaptiveBranch> branches)
        {
            lock (executionByInput)
                foreach (var branch in branches)
                    executionByInput[JsonSerializer.Deserialize<string>(branch.Execution.InputJson!)!] = branch.Execution.Id;
        }

        public async ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default)
        {
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            using var facts = JsonDocument.Parse(input);
            var logical = facts.RootElement.GetProperty("LogicalId").GetString()!;
            var requirement = facts.RootElement.GetProperty("Requirement").GetString()!;
            Guid executionId;
            lock (executionByInput) executionId = executionByInput[input];
            MarkOnce(Path.Combine(directory, executionId.ToString("N") + ".started"), Path.Combine(directory, logical + ".starts"), executionId);

            string summary;
            if (logical == "outcome")
            {
                var active = await revisions.GetActiveAsync(WorkflowId, cancellationToken);
                var parts = new List<string>();
                foreach (var branch in active.Branches.Where(b => b.LogicalId != "outcome"))
                {
                    var result = await workflowStore.GetRunAsync(branch.Execution.Id, cancellationToken);
                    if (result?.Status != WorkflowStatus.Completed || result.OutputJson is null)
                        throw new InvalidOperationException("Current report cannot include incomplete work: " + branch.LogicalId);
                    parts.Add(branch.Title + ": " + JsonSerializer.Deserialize<string>(result.OutputJson));
                }
                if (requirement.Contains("unknown provider work", StringComparison.OrdinalIgnoreCase))
                {
                    var evidence = await File.ReadAllTextAsync(Path.Combine(directory, "m1-unknown-result.json"), cancellationToken);
                    _ = VerifyInput(input, Hash(evidence), requirement);
                }
                summary = requirement + "\n" + string.Join("\n", parts);
            }
            else
            {
                var readme = await File.ReadAllTextAsync(Path.Combine(directory, "readme.snapshot"), cancellationToken);
                var security = await File.ReadAllTextAsync(Path.Combine(directory, "security.snapshot"), cancellationToken);
                var tests = await File.ReadAllTextAsync(Path.Combine(directory, "tests.snapshot"), cancellationToken);
                var api = await File.ReadAllTextAsync(Path.Combine(directory, "api.snapshot"), cancellationToken);
                var targetFrameworks = tests.Contains("net8.0", StringComparison.Ordinal) && tests.Contains("net10.0", StringComparison.Ordinal)
                    ? ".NET 8 and .NET 10 declared" : "declared framework list inspected";
                var unknownPath = Path.Combine(directory, "m1-unknown-result.json");
                summary = logical switch
                {
                    "platform-linux" => VerifyInput(input, Hash(tests), requirement) + $"; {targetFrameworks}; Linux execution was not run; repository files read only.",
                    "platform-windows" => VerifyInput(input, Hash(tests), requirement) + $"; {targetFrameworks}; Windows execution was not run; repository files read only.",
                    "review" when requirement.Contains("SECURITY.md", StringComparison.Ordinal) => VerifyInput(input, Hash(readme + security + api), requirement) + $"; README {readme.Split('\n').Length} lines; SECURITY {security.Split('\n').Length} lines; adaptive API source {api.Split('\n').Length} lines.",
                    "acceptance" when requirement.Contains("declared .NET", StringComparison.Ordinal) => VerifyInput(input, Hash(tests), requirement) + $"; {targetFrameworks}; read-report only.",
                    "acceptance" when File.Exists(unknownPath) && requirement.Contains("unknown", StringComparison.OrdinalIgnoreCase) =>
                        VerifyInput(input, Hash(await File.ReadAllTextAsync(unknownPath, cancellationToken)), requirement) + "; unknown provider capture evidence: " + await File.ReadAllTextAsync(unknownPath, cancellationToken),
                    "delegated" when requirement.Contains("durable handle capture", StringComparison.OrdinalIgnoreCase) =>
                        VerifyInput(input, Hash(await File.ReadAllTextAsync(unknownPath, cancellationToken)), requirement) + "; unknown provider capture evidence: " + await File.ReadAllTextAsync(unknownPath, cancellationToken),
                    _ => requirement + $"; README {readme.Split('\n').Length} lines; SECURITY {security.Split('\n').Length} lines; adaptive API source {api.Split('\n').Length} lines; target metadata {targetFrameworks}."
                };
            }
            await File.WriteAllTextAsync(Path.Combine(directory, executionId.ToString("N") + ".result"), summary, cancellationToken);
            return ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(JsonSerializer.SerializeToElement(summary)));
        }

        private static void MarkOnce(string marker, string log, Guid executionId)
        {
            try
            {
                using var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                File.AppendAllText(log, executionId.ToString("D") + "\n");
            }
            catch (IOException) when (File.Exists(marker)) { }
        }
        private static string VerifyInput(string json, string expectedDigest, string requirement)
        {
            using var facts = JsonDocument.Parse(json);
            var actualDigest = facts.RootElement.GetProperty("Input").GetString();
            if (actualDigest != expectedDigest) throw new InvalidOperationException("Immutable repository evidence digest mismatch for " + requirement);
            return requirement;
        }
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class ProbeStorePolicy : IAuthorityStoreAuthorizer
    {
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request, CancellationToken cancellationToken = default) =>
            new(new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
    }
    private sealed class ProbeDecisionRecorder(IZhinuSqliteDatabase database) : IAuthorityDecisionRecorder
    {
        public async ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken cancellationToken = default)
        {
            await using var c = await database.OpenAsync(cancellationToken);
            await using var q = c.CreateCommand();
            q.CommandText = "CREATE TABLE IF NOT EXISTS probe_hufu_decisions(document TEXT NOT NULL); INSERT INTO probe_hufu_decisions(document) VALUES($d)";
            q.Parameters.AddWithValue("$d", JsonSerializer.Serialize(new { request.RequestIdentity, decision.Status, decision.SnapshotIdentity, decision.ReasonCode }));
            await q.ExecuteNonQueryAsync(cancellationToken); return true;
        }
    }
}

internal static class ContinuityResultExtensions
{
    public static int RevisionCount(this SupervisorCatchUpView view) => view.TotalRevisionCount;
}
