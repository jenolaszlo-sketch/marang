using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Marang.Hufu;
using Marang.Runs.Adaptive;
using Marang.Runs.Effects;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Hufu;
using Penghou.Hufu.Cedar;
using Penghou.Hufu.Sqlite;
using Penghou.Qingniao;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.RestartProbe;

/// <summary>Cross-process M3 qualification against a detached clone of the qualified M2 Marang commit.</summary>
internal static class RepositoryEffectProbe
{
    private static readonly Guid WorkflowId = Guid.Parse("9cda3bf5-82a8-43c6-86fc-10f96275d045");
    private static readonly Guid InventoryExecutionId = Guid.Parse("c39d3bb2-3a12-45f4-8b84-1ed80886be80");
    private static readonly Guid Plan1ExecutionId = Guid.Parse("54fa802a-9ca2-4c43-9f4c-dc97e1763e9e");
    private static readonly Guid Plan2ExecutionId = Guid.Parse("1b910c39-ffdf-410e-a542-490e426d07f6");
    private static readonly Guid Plan3ExecutionId = Guid.Parse("8c7b4fe7-1e77-4290-a76a-dd22eb1d34db");
    private static readonly Guid Effect1Id = Guid.Parse("5271bf9e-2940-4ed0-a88a-17d15b8e468a");
    private static readonly Guid Effect1ExecutionId = Guid.Parse("0a86b18d-a3e1-4b3a-b4fb-b9a4b2b20ffb");
    private static readonly Guid Effect2Id = Guid.Parse("df1b88a1-1993-44df-9afd-6c0c472464d0");
    private static readonly Guid Effect2ExecutionId = Guid.Parse("dd4ac437-4745-4f1f-83bf-0aeb37dd59d3");
    private const string Operator = "qualified-operator";
    private const string SourceCommit = "a0916534d0e82e499cffb2dea526b64357eb32e2";
    private const string SourcePath = "src/Marang/WorkflowPlanReference.cs";
    private const string ReadmePath = "README.md";
    private const string InitialGoal = "Inspect the repository and plan a bounded canonical fingerprint correction.";
    private const string PatchAGoal = "Apply patch A and validate the exact resulting repository state.";
    private const string PatchBGoal = "Correct patch A, then validate the exact resulting repository state.";
    private static readonly string InventoryProvenance = Hash("m3/repository-inventory/source-commit/" + SourceCommit);
    private const string Sha63Note = "M3 qualification note: canonical SHA-256 plan fingerprints require 63 lowercase hexadecimal characters.";
    private const string Sha64Note = "M3 qualification note: canonical SHA-256 plan fingerprints require 64 lowercase hexadecimal characters.";

    public static async Task RunAsync(string directory, string? barrier, bool resume)
    {
        Directory.CreateDirectory(directory);
        directory = Path.GetFullPath(directory);
        var checkout = Path.Combine(directory, "controlled-checkout");
        EnsureCheckout(checkout);

        var runtimeDirectory = Path.Combine(directory, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        var database = new SqliteDatabase(new ZhinuSqliteOptions
        {
            DatabasePath = Path.Combine(runtimeDirectory, "execution.db"),
            Pooling = false,
            BusyTimeout = TimeSpan.FromSeconds(10)
        });
        var revisions = new SqliteAdaptiveRevisionRepository(database);
        await revisions.InitializeAsync();
        // Catch-up projects the same M1 recovery evidence used by the other M2
        // scenarios. Initialize its owner journal even when this effect-only
        // workflow has no delegation attempts, so the projection reads an
        // empty authoritative set instead of querying an absent table.
        var delegationRecovery = new SqliteDelegationRecoveryJournal(database);
        await delegationRecovery.InitializeAsync();
        var effectJournal = new SqliteRepositoryEffectJournal(database);
        await effectJournal.InitializeAsync();
        var workflowStore = new SqliteWorkflowStore(database);
        var repository = ControlledRepository.Open(checkout, [ReadmePath, SourcePath]);
        var continuity = new SqliteSupervisorContinuityStore(database, revisions, name => name == Operator);
        await continuity.InitializeAsync();
        var catchUp = new SupervisorCatchUp(database, name => name == Operator);
        await catchUp.InitializeAsync();

        var actor = new AuthorityStoreActor("m1", "host", "m3-repository-effect");
        var authorityStore = new SqliteAuthorityStore(Path.Combine(runtimeDirectory, "hufu.db"), new ProbeStorePolicy());
        var authorityRequests = new CurrentAuthorityRequestAuthorizer(new AuthorityStoreSnapshotSource(authorityStore, actor),
            new CedarAuthorityEvaluator(), new ProbeDecisionRecorder(database, directory));
        var supervisorAuthority = new SqliteAdaptiveSupervisorAuthority(database, revisions, authorityRequests,
            new AdaptiveAuthorityProfile("m1", "supervisor", "repository", "reports/readiness.md", "write-file", "m1")
            { AllowedBranchAuthorityDigest = AdaptivePlanAuthoring.RepositoryEffectAuthorityProfile },
            name => name == Operator);
        await supervisorAuthority.InitializeAsync();

        var binding = new RepositoryEffectWorkflowBinding(database, revisions);
        var effectAuthority = new RepositoryEffectAuthority(authorityRequests, binding, "repository");
        var executor = new RepositoryEffectExecutor(repository, effectJournal, effectAuthority);
        var ports = new ProbePorts(directory, checkout, repository);
        var executionPorts = new FuwenZhinuExecutionPorts(ports, ports, ports);
        ports.Configure(async (facts, cancellationToken) =>
        {
            var intent = (await effectJournal.ListForWorkflowAsync(WorkflowId, cancellationToken))
                .SingleOrDefault(record => record.Intent.IntentDigest == facts.GetProperty("Input").GetString())?.Intent
                ?? throw new InvalidOperationException("Admitted Fuwen effect input has no immutable durable intent.");
            var context = new AuthenticatedAuthorityContext("m1", "supervisor", WorkflowId.ToString("D"), intent.RevisionId, "m1");
            var result = await executor.ExecuteAsync(intent, context,
                (state, ct) => ValidateAsync(checkout, directory, barrier, intent, effectJournal, state, ct),
                (checkpoint, ct) => CheckpointAsync(directory, checkout, barrier, intent, effectJournal, checkpoint, ct), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "effect-execution-" + intent.EffectId.ToString("N") + ".json"),
                JsonSerializer.Serialize(new
                {
                    result.Classification,
                    Stage = result.Record.Stage,
                    result.ReasonCode,
                    Validation = result.Record.Validation,
                    result.MutationPerformed,
                    Authority = result.Record.AuthorityEvidence
                }, JsonOptions), cancellationToken);
            if (result.Classification == RepositoryEffectClassification.DefinitelyApplied && result.Record.Stage == RepositoryEffectStage.ValidationFailed)
                return "ValidationFailed: " + result.Record.Validation?.Evidence;
            if (result.Classification == RepositoryEffectClassification.PartiallyApplied)
                return "BlockedPartial: automatic replay refused";
            if (result.Classification == RepositoryEffectClassification.Unresolved)
                return "BlockedUnresolved: automatic replay refused";
            return result.Record.Stage.ToString();
        });
        var history = await revisions.GetHistoryAsync(WorkflowId);
        WorkflowRegistry registry;
        if (history.Length == 0)
        {
            var basis = await repository.RequireCleanInitialAsync();
            var initial = await AdaptivePlanAuthoring.AuthorAsync(WorkflowId, "revision-1", null,
                InitialGoal,
                "Initial repository inventory and change plan.", "m3-deterministic-planner",
                Specs("revision-1", basis.StateDigest, null, false), executionPorts);
            registry = initial.Registry;
            await revisions.ActivateInitialAsync(initial.Revision);
            await continuity.RecordIntentAsync(WorkflowId, initial.Revision.RevisionId,
                InitialGoal, SupervisorIntentTrigger.Initial,
                "Initial M3 repository inventory and bounded correction plan.", ["README.md", SourcePath, "tests/Marang.Tests/Marang.Tests.csproj"], Operator);
            history = [initial.Revision];
        }
        else registry = await AdaptivePlanAuthoring.RestoreRegistryAsync(history, executionPorts);
        ports.Bind(history.SelectMany(revision => revision.Branches));

        await using var engine = new WorkflowEngine(workflowStore, registry, new ZhinuOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(250)
        });
        var supervisor = new AdaptiveSupervisor(revisions, supervisorAuthority,
            new RepositoryEffectRevisionGuard(repository, effectJournal));
        var active = await revisions.GetActiveAsync(WorkflowId);
        await PublishAuthority(authorityStore, actor, active.RevisionId);

        if (active.RevisionId == "revision-1")
        {
            await RunRevisionAsync(engine, workflowStore, active, allowFinal: false);
            var intent = await PrepareEffectAsync(checkout, repository, effectJournal, "revision-2", Effect1Id,
                Effect1ExecutionId, "effect", BuildPatchA, "attempt-1");
            var candidate = await AdaptivePlanAuthoring.AuthorEffectAsync(WorkflowId, "revision-2", "revision-1",
                PatchAGoal,
                "Patch A extracts the existing correct SHA-256 length check into a constant set to 63; preserve completed inventory and use deterministic validation to trigger a corrective revision.",
                "m3-deterministic-planner", Specs("revision-2", intent.BaseState.StateDigest, intent, true), executionPorts, registry);
            registry = candidate.Registry;
            ports.Bind(candidate.Revision.Branches);
            var proposal = await supervisor.ProposeAsync(candidate.Revision,
                "Repository inventory is complete and reusable. Patch A changes the real source and its README note; validation will determine the corrective change.");
            await CaptureIntentAsync(catchUp, continuity, proposal.Proposal, SupervisorIntentTrigger.User);
            var receipt = await supervisorAuthority.FindApprovalAsync(proposal.Proposal.ProposalId, proposal.Digest);
            if (receipt is null) receipt = await supervisor.ApproveAsync(proposal.Proposal, proposal.Digest, Operator);
            await supervisor.CommitAsync(proposal.Proposal, proposal.Digest, receipt);
            active = await revisions.GetActiveAsync(WorkflowId);
            await PublishAuthority(authorityStore, actor, active.RevisionId);
        }

        if (active.RevisionId == "revision-2")
        {
            await RunRevisionAsync(engine, workflowStore, active, allowFinal: false);
            var firstEffect = (await effectJournal.ListForWorkflowAsync(WorkflowId)).Single(record => record.Intent.EffectId == Effect1Id);
            if (firstEffect.Classification == RepositoryEffectClassification.PartiallyApplied)
            {
                var partialSupervisor = new RepositoryEffectSupervisor(revisions, effectJournal, repository, catchUp);
                var partialCatchUp = await partialSupervisor.ReadAsync(WorkflowId, Operator, CancellationToken.None);
                var partialResult = new
                {
                    Classification = firstEffect.Classification.ToString(),
                    PhysicalApplyCount = CountApplyRounds(directory, Effect1Id),
                    AutomaticReplayBlocked = firstEffect.Stage == RepositoryEffectStage.Blocked,
                    MutationBlocked = partialCatchUp.MutationBlocked,
                    EffectCatchUp = partialCatchUp,
                    RepositoryFileHashes = new
                    {
                        Source = Hash(await File.ReadAllTextAsync(Path.Combine(checkout, SourcePath))),
                        Readme = Hash(await File.ReadAllTextAsync(Path.Combine(checkout, ReadmePath)))
                    },
                    ObservedState = firstEffect.ObservedState,
                    Intent = firstEffect.Intent
                };
                await File.WriteAllTextAsync(Path.Combine(directory, "repository-effect-partial.json"), JsonSerializer.Serialize(partialResult, JsonOptions));
                return;
            }

            var pending = await revisions.GetPendingAsync(WorkflowId);
            if (pending is null)
            {
                if (firstEffect.Stage != RepositoryEffectStage.ValidationFailed)
                    throw new InvalidOperationException("Patch A must complete deterministic validation and fail before correction is proposed. Stage=" +
                        firstEffect.Stage + "; Classification=" + firstEffect.Classification + "; Validation=" +
                        JsonSerializer.Serialize(firstEffect.Validation) + "; Authority=" + JsonSerializer.Serialize(firstEffect.AuthorityEvidence));
                var second = await PrepareEffectAsync(checkout, repository, effectJournal, "revision-3", Effect2Id,
                    Effect2ExecutionId, "effect", BuildPatchB, "attempt-1");
                var candidate = await AdaptivePlanAuthoring.AuthorEffectAsync(WorkflowId, "revision-3", "revision-2",
                    PatchBGoal,
                    "Patch A failed the existing workflow plan fingerprint tests because it accepted 63-character fingerprints. Patch B restores the required 64-character contract.",
                    "m3-supervisor-corrective-planner", Specs("revision-3", second.BaseState.StateDigest, second, true), executionPorts, registry);
                registry = candidate.Registry;
                ports.Bind(candidate.Revision.Branches);
                var proposal = await supervisor.ProposeAsync(candidate.Revision,
                    "Patch A is definitely applied and remains in the historical effect record. Patch B targets its observed post-state, uses fresh authority, and preserves the completed inventory execution.");
                await CaptureIntentAsync(catchUp, continuity, proposal.Proposal, SupervisorIntentTrigger.Evidence);
                pending = proposal.Proposal;
                await PersistOrVerifyCorrectiveProposalAsync(directory, pending);
                if (barrier == "K8")
                    await WritePendingBarrierContextAsync(directory, catchUp, repository, pending, CancellationToken.None);
                await BarrierAsync(directory, barrier, "K8");
            }
            else
            {
                await catchUp.CaptureProposalContextAsync(pending, Operator);
                await RecordCandidateIntentAsync(continuity, pending, SupervisorIntentTrigger.Evidence);
                await PersistOrVerifyCorrectiveProposalAsync(directory, pending);
                var candidateRegistry = await AdaptivePlanAuthoring.RestoreRegistryAsync([pending.Candidate], executionPorts);
                RegisterCandidateFromRegistry(registry, history, pending.Candidate, candidateRegistry);
                ports.Bind(pending.Candidate.Branches);
            }

            var digest = AdaptiveRevisionContracts.Digest(pending);
            var approval = await supervisorAuthority.FindApprovalAsync(pending.ProposalId, digest);
            if (approval is null) approval = await supervisor.ApproveAsync(pending, digest, Operator);
            await supervisor.CommitAsync(pending, digest, approval);
            active = await revisions.GetActiveAsync(WorkflowId);
            await PublishAuthority(authorityStore, actor, active.RevisionId);
        }

        if (active.RevisionId == "revision-3")
        {
            await RunRevisionAsync(engine, workflowStore, active,
                finalGate: () => IsCurrentCorrectionValidatedAsync(repository, effectJournal));
            var effects = await effectJournal.ListForWorkflowAsync(WorkflowId);
            var original = effects.Single(record => record.Intent.EffectId == Effect1Id);
            var corrective = effects.Single(record => record.Intent.EffectId == Effect2Id);
            if (original.Stage != RepositoryEffectStage.ValidationFailed || corrective.Stage != RepositoryEffectStage.ValidationPassed)
                throw new InvalidOperationException("Corrective completion must retain failed patch A and pass patch B validation.");
            var finalState = await repository.ObserveAsync();
            var effectSupervisor = new RepositoryEffectSupervisor(revisions, effectJournal, repository, catchUp);
            var effectView = await effectSupervisor.ReadAsync(WorkflowId, Operator);
            var drillDown = await effectSupervisor.DrillDownAsync(WorkflowId, "revision-2", Operator);
            var revision2 = (await revisions.GetHistoryAsync(WorkflowId)).Single(revision => revision.RevisionId == "revision-2");
            var revision2Final = revision2.Branches.Single(branch => branch.LogicalId == "final");
            var result = new
            {
                SourceCommit,
                Checkout = checkout,
                Branch = Git(checkout, "branch", "--show-current").Trim(),
                RecoveryBarriers = new[] { "K1", "K2", "K3", "K4", "K5", "K6", "K7", "K8", "K9", "K10" }
                    .Where(name => File.Exists(Path.Combine(directory, "barrier-used-" + name))).ToArray(),
                FinalRevision = active.RevisionId,
                OriginalPhysicalApplyCount = CountApplyRounds(directory, Effect1Id),
                CorrectivePhysicalApplyCount = CountApplyRounds(directory, Effect2Id),
                OriginalValidationPassed = original.Validation?.Passed ?? false,
                CorrectiveValidationPassed = corrective.Validation?.Passed ?? false,
                CorrectiveProposal = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "corrective-proposal.json"))).RootElement.Clone(),
                OriginalEffectActivityResult = (await workflowStore.GetRunAsync(Effect1ExecutionId))?.OutputJson,
                Revision2FinalJoinStatus = (await workflowStore.GetRunAsync(revision2Final.Execution.Id))?.Status.ToString() ?? "NotStarted",
                Revision2FinalJoinWasResultFenced = (await workflowStore.GetRunAsync(revision2Final.Execution.Id))?.Status != WorkflowStatus.Completed,
                UnaffectedExecutionPreserved = active.Branches.Single(branch => branch.LogicalId == "inventory").Execution.Id == InventoryExecutionId,
                InventoryExecutionId,
                InventoryInvocationCount = File.Exists(Path.Combine(directory, "inventory-invocations.log"))
                    ? File.ReadAllLines(Path.Combine(directory, "inventory-invocations.log")).Length : 0,
                FinalJoinCompleted = (await workflowStore.GetRunAsync(active.Branches.Single(branch => branch.LogicalId == "final").Execution.Id))?.Status == WorkflowStatus.Completed,
                CompletedInventoryResult = (await workflowStore.GetRunAsync(InventoryExecutionId))?.OutputJson,
                FinalRepositoryState = finalState,
                Effects = effects,
                M2CatchUp = effectView.Workflow,
                EffectCatchUp = effectView,
                EffectDrillDown = drillDown,
                CatchUpExplainsEffectLineage = effectView.Effects.Any(effect => effect.RevisionId == "revision-2" && effect.Classification == "AppliedThenCorrected") &&
                    effectView.Effects.Any(effect => effect.RevisionId == "revision-3" && effect.ValidationPassed == true),
                DrillDownShowsSupersededAppliedEffect = drillDown.Effects.Any(effect => effect.EffectId == original.Intent.EffectId && effect.Classification == "AppliedThenCorrected") &&
                    original.Classification == RepositoryEffectClassification.DefinitelyApplied
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "repository-effect-result.json"), JsonSerializer.Serialize(result, JsonOptions));
        }

        // A normal approval proposal can be pending after a process kill. The caller-specific admission function
        // is intentionally local to this process; all authority decisions and proposal bytes survive in SQLite.
        // Preserve the real M2 catch-up data even when the effect branch has not yet completed.
        if (active.RevisionId != "revision-3")
        {
            var view = await catchUp.ReadAsync(WorkflowId, Operator);
            await File.WriteAllTextAsync(Path.Combine(directory, "m2-catch-up.json"), JsonSerializer.Serialize(view, JsonOptions));
        }
    }

    private static async Task CaptureIntentAsync(SupervisorCatchUp catchUp, SqliteSupervisorContinuityStore continuity,
        AdaptiveProposal proposal, SupervisorIntentTrigger trigger)
    {
        await catchUp.CaptureProposalContextAsync(proposal, Operator);
        await RecordCandidateIntentAsync(continuity, proposal, trigger);
    }

    private static Task<SupervisorRevisionIntent> RecordCandidateIntentAsync(SqliteSupervisorContinuityStore continuity,
        AdaptiveProposal proposal, SupervisorIntentTrigger trigger) => continuity.RecordIntentAsync(WorkflowId,
        proposal.Candidate.RevisionId, proposal.Candidate.RevisionId == "revision-2" ? PatchAGoal : PatchBGoal, trigger,
            proposal.Candidate.Reason, ["repository-effect:" + proposal.Candidate.RevisionId, SourcePath, ReadmePath], Operator);

    private static async Task<RepositoryEffectIntent> PrepareEffectAsync(string checkout, ControlledRepository repository,
        SqliteRepositoryEffectJournal journal, string revisionId, Guid effectId, Guid executionId, string step,
        Func<string, string, (byte[] Source, byte[] Readme)> patchFactory, string attempt)
    {
        var records = await journal.ListForWorkflowAsync(WorkflowId);
        var existing = records.SingleOrDefault(record => record.Intent.EffectId == effectId);
        if (existing is not null) return existing.Intent;
        var basis = await repository.ObserveAsync();
        var source = await File.ReadAllTextAsync(Path.Combine(checkout, SourcePath));
        var readme = await File.ReadAllTextAsync(Path.Combine(checkout, ReadmePath));
        var (afterSource, afterReadme) = patchFactory(source, readme);
        var files = new[]
        {
            RepositoryPatchFile.Create(SourcePath, Encoding.UTF8.GetBytes(source), afterSource),
            RepositoryPatchFile.Create(ReadmePath, Encoding.UTF8.GetBytes(readme), afterReadme)
        };
        var intent = RepositoryEffectIntent.Create(effectId, WorkflowId, revisionId, step, executionId, attempt,
            repository.RepositoryId, "repository", basis, files, new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero));
        await journal.PrepareAsync(intent);
        return intent;
    }

    private static AdaptiveWorkSpec[] Specs(string revisionId, string provenance, RepositoryEffectIntent? effect, bool includeEffect)
    {
        AdaptiveWorkSpec Spec(string id, string title, string requirement, string input, Guid executionId,
            string[]? dependencies = null, bool reuse = false) => new(id, title, requirement, input,
                AdaptivePlanAuthoring.RepositoryEffectAuthorityProfile, "m3/controlled-repository/v1", dependencies ?? [], executionId, reuse);
        var inventory = Spec("inventory", "Repository inventory", "Read README, package references, and source fingerprint validation.",
            "qualified-checkout:" + SourceCommit, InventoryExecutionId, reuse: revisionId != "revision-1") with
        { Provenance = InventoryProvenance };
        if (revisionId == "revision-1")
            return [inventory,
                Spec("plan", "Change plan", "Prepare a bounded code change and validation plan.", provenance, Plan1ExecutionId),
                Spec("final", "Final workflow join", "Summarize the initial repository inventory and plan.", provenance, Guid.Parse("58ec81df-9237-4c17-9308-9f66f4b37cd9"), ["inventory", "plan"])];

        var planId = revisionId == "revision-2" ? Plan2ExecutionId : Plan3ExecutionId;
        var planRequirement = revisionId == "revision-2"
            ? "Apply patch A: extract the SHA-256 length constant as 63 and document the initial candidate."
            : "Correct patch A: restore the SHA-256 length constant to 64 and document the approved correction.";
        var preparedEffect = effect ?? throw new InvalidOperationException("Effect revision is missing its prepared intent.");
        return [inventory,
            Spec("plan", "Change plan", planRequirement, provenance, planId),
            Spec("effect", "Apply and validate repository patch", planRequirement, preparedEffect.IntentDigest, preparedEffect.ExecutionId) with { IsRepositoryEffect = true },
            Spec("final", "Final workflow join", revisionId == "revision-2" ? "Report patch A validation evidence and pending correction." : "Report the validated corrective repository state.",
                provenance, revisionId == "revision-2" ? Guid.Parse("622bd999-4fa0-435b-aa17-baa5b51a33e0") : Guid.Parse("2773b3a9-2cde-4dc9-9b83-d6790b9e06a1"),
                ["inventory", "plan", "effect"])
        ];
    }

    private static async Task RunRevisionAsync(WorkflowEngine engine, IWorkflowRepository workflowStore, AdaptiveRevision revision,
        bool allowFinal = true, Func<Task<bool>>? finalGate = null)
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(5);
        foreach (var branch in revision.Branches.OrderBy(branch => branch.Dependencies.Length == 0 ? 0 : 1))
        {
            if (!allowFinal && branch.LogicalId == "final") continue;
            if (branch.LogicalId == "final" && finalGate is not null && !await finalGate())
                throw new InvalidOperationException("The final join is fenced until the current effect has durable passing validation against the exact observed repository state.");
            while (true)
            {
                var current = await workflowStore.GetRunAsync(branch.Execution.Id);
                if (current?.Status == WorkflowStatus.Completed) break;
                if (current?.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled)
                    throw new InvalidOperationException("M3 branch failed: " + branch.LogicalId + "; " + current.Error?.Message);
                var ready = true;
                foreach (var dependency in branch.Dependencies)
                    ready &= (await workflowStore.GetRunAsync(revision.Branches.Single(b => b.LogicalId == dependency).Execution.Id))?.Status == WorkflowStatus.Completed;
                if (!ready) break;
                var input = JsonSerializer.Deserialize<JsonElement>(branch.Execution.InputJson!);
                await engine.StartAsync(branch.Execution.WorkflowName, branch.Execution.WorkflowVersion, input, branch.Execution.Id);
                await engine.ExecuteAsync(branch.Execution.Id);
                if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("M3 branch recovery timed out: " + branch.LogicalId);
                await Task.Delay(25);
            }
        }
        foreach (var branch in revision.Branches.Where(branch => allowFinal || branch.LogicalId != "final"))
            if ((await workflowStore.GetRunAsync(branch.Execution.Id))?.Status != WorkflowStatus.Completed)
                throw new InvalidOperationException("M3 plan did not complete branch " + branch.LogicalId + ".");
    }

    private static async Task<bool> IsCurrentCorrectionValidatedAsync(ControlledRepository repository, SqliteRepositoryEffectJournal journal)
    {
        var records = await journal.ListForWorkflowAsync(WorkflowId);
        var correction = records.SingleOrDefault(record => record.Intent.RevisionId == "revision-3");
        if (correction is not { Stage: RepositoryEffectStage.ValidationPassed, Validation.Passed: true } ||
            correction.Validation.TargetStateDigest != correction.Intent.ExpectedPostState.StateDigest ||
            correction.ObservedState?.StateDigest != correction.Validation.TargetStateDigest) return false;
        return (await repository.ObserveAsync()).StateDigest == correction.Validation.TargetStateDigest;
    }

    private static void RegisterCandidateFromRegistry(WorkflowRegistry registry, AdaptiveRevision[] history,
        AdaptiveRevision candidate, WorkflowRegistry candidateRegistry)
    {
        foreach (var branch in candidate.Branches)
        {
            if (history.SelectMany(revision => revision.Branches).Any(existing =>
                existing.Execution.WorkflowName == branch.Execution.WorkflowName && existing.Execution.WorkflowVersion == branch.Execution.WorkflowVersion)) continue;
            registry.Register(candidateRegistry.Get(branch.Execution.WorkflowName, branch.Execution.WorkflowVersion));
        }
    }

    private static async Task BarrierAsync(string directory, string? requested, string current, CancellationToken cancellationToken = default)
    {
        if (requested != current || File.Exists(Path.Combine(directory, "barrier-used-" + current))) return;
        await File.WriteAllTextAsync(Path.Combine(directory, "barrier-ready-" + current), current, cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async Task RecordApplyRoundAsync(string directory, Guid effectId, CancellationToken cancellationToken)
    {
        await File.AppendAllTextAsync(Path.Combine(directory, "physical-applies.log"), effectId.ToString("D") + "\n", cancellationToken);
    }

    private static int CountApplyRounds(string directory, Guid effectId) =>
        File.Exists(Path.Combine(directory, "physical-applies.log"))
            ? File.ReadAllLines(Path.Combine(directory, "physical-applies.log")).Count(line => line == effectId.ToString("D")) : 0;

    private static async ValueTask<RepositoryEffectValidation> ValidateAsync(string checkout, string directory, string? barrier,
        RepositoryEffectIntent intent, SqliteRepositoryEffectJournal journal, RepositoryStateSnapshot state, CancellationToken cancellationToken)
    {
        const string command = "dotnet test tests/Marang.Tests/Marang.Tests.csproj -c Release --filter FullyQualifiedName~WorkflowPlanResolutionTests; dotnet run generated source-conformance probe against src/Marang/WorkflowPlanReference.cs";
        var restore = await RunCapturedAsync("dotnet", ["restore", "tests/Marang.Tests/Marang.Tests.csproj", "--configfile", "NuGet.AAAWM1.Config", "--ignore-failed-sources"], checkout, cancellationToken);
        var test = await RunCapturedAsync("dotnet", ["test", "tests/Marang.Tests/Marang.Tests.csproj", "-c", "Release", "--no-restore", "--filter", "FullyQualifiedName~WorkflowPlanResolutionTests"], checkout,
            cancellationToken, async process =>
            {
                if (intent.RevisionId == "revision-2")
                {
                    if (barrier == "K6" && !File.Exists(Path.Combine(directory, "barrier-used-K6")))
                    {
                        await File.WriteAllTextAsync(Path.Combine(directory, "validation-process-K6.pid"), process.Id.ToString(), cancellationToken);
                        await WriteBarrierContextAsync(directory, checkout, intent, await journal.GetAsync(intent.EffectId, cancellationToken),
                            "K6", "RunningValidationProcess", process.Id, cancellationToken,
                            validationProcessRunning: !process.HasExited);
                        await BarrierAsync(directory, barrier, "K6", cancellationToken);
                    }
                }
            });
        var conformanceRoot = Path.Combine(directory, "validation", intent.EffectId.ToString("N"));
        Directory.CreateDirectory(conformanceRoot);
        await File.WriteAllTextAsync(Path.Combine(conformanceRoot, "FingerprintConformance.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><Compile Include=\""
            + SecurityElementEscape(Path.Combine(checkout, SourcePath)) + "\" Link=\"WorkflowPlanReference.cs\" /></ItemGroup></Project>", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(conformanceRoot, "Program.cs"), """
            using Marang;
            _ = new WorkflowPlanRevisionReference(WorkflowPlanReferenceKind.FuwenDefinition, "definition", "revision", new string('a', 64));
            static bool Rejects(string value)
            {
                try { _ = new WorkflowPlanRevisionReference(WorkflowPlanReferenceKind.FuwenDefinition, "definition", "revision", value); return false; }
                catch (ArgumentException) { return true; }
            }
            if (!Rejects(new string('a', 63))) throw new InvalidOperationException("A 63-character fingerprint was accepted.");
            if (!Rejects(new string('A', 64))) throw new InvalidOperationException("An uppercase fingerprint was accepted.");
            Console.WriteLine("PASS: actual checkout source accepts canonical 64 lowercase hex and rejects length 63 and uppercase.");
            """, cancellationToken);
        var conformance = await RunCapturedAsync("dotnet", ["run", "--project", "FingerprintConformance.csproj", "-c", "Release"], conformanceRoot, cancellationToken);
        var output = restore.Output + "\n" + test.Output + "\n" + conformance.Output;
        var fullOutputPath = Path.Combine(directory, "validation-" + intent.EffectId.ToString("N") + ".log");
        await File.WriteAllTextAsync(fullOutputPath, output, cancellationToken);
        return new RepositoryEffectValidation(state.StateDigest, "WorkflowPlanResolutionTests + actual-source fingerprint conformance", command,
            restore.ExitCode == 0 && test.ExitCode == 0 && conformance.ExitCode == 0, Hash(output),
            "Full stdout/stderr: " + fullOutputPath + "; sha256=" + Hash(output) + "; " + Limit(output, 1800));
    }

    private static string SecurityElementEscape(string value) =>
        System.Security.SecurityElement.Escape(value) ?? throw new InvalidOperationException("Cannot escape source path for validation project.");

    private static async ValueTask CheckpointAsync(string directory, string checkout, string? requestedBarrier,
        RepositoryEffectIntent intent, SqliteRepositoryEffectJournal journal, RepositoryEffectExecutionCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var name = checkpoint.Kind switch
        {
            RepositoryEffectExecutionCheckpointKind.BeforeIntentCommit when intent.RevisionId == "revision-2" => "K1",
            RepositoryEffectExecutionCheckpointKind.AfterIntentCommit when intent.RevisionId == "revision-2" => "K2",
            RepositoryEffectExecutionCheckpointKind.AfterPhysicalFile when intent.RevisionId == "revision-2" && checkpoint.FileIndex == 1 => "K3",
            RepositoryEffectExecutionCheckpointKind.AfterAllWrites when intent.RevisionId == "revision-2" => "K4",
            RepositoryEffectExecutionCheckpointKind.AfterAppliedObserved when intent.RevisionId == "revision-2" => "K5",
            RepositoryEffectExecutionCheckpointKind.AfterValidation when intent.RevisionId == "revision-2" => "K7",
            RepositoryEffectExecutionCheckpointKind.AfterIntentCommit when intent.RevisionId == "revision-3" => "K9",
            RepositoryEffectExecutionCheckpointKind.AfterAllWrites when intent.RevisionId == "revision-3" => "K10",
            _ => null
        };
        if (checkpoint.Kind == RepositoryEffectExecutionCheckpointKind.AfterPhysicalFile && checkpoint.FileIndex == 1)
            await RecordApplyRoundAsync(directory, intent.EffectId, cancellationToken);
        if (name is not null)
        {
            if (requestedBarrier == name && !File.Exists(Path.Combine(directory, "barrier-used-" + name)))
                await WriteBarrierContextAsync(directory, checkout, intent, await journal.GetAsync(intent.EffectId, cancellationToken),
                    name, checkpoint.Kind.ToString(), Environment.ProcessId, cancellationToken);
            await BarrierAsync(directory, requestedBarrier, name, cancellationToken);
        }
    }

    private static Task WriteBarrierContextAsync(string directory, string checkout, RepositoryEffectIntent intent, RepositoryEffectRecord? record, string barrier,
        string checkpoint, int processId, CancellationToken cancellationToken, bool? validationProcessRunning = null) =>
        File.WriteAllTextAsync(Path.Combine(directory, "barrier-context-" + barrier + ".json"),
            JsonSerializer.Serialize(new
            {
                Barrier = barrier,
                ProcessId = processId,
                WorkflowId,
                intent.RevisionId,
                intent.ExecutionId,
                intent.AttemptId,
                intent.RepositoryId,
                SourceCommit,
                Branch = "m3-effect-qualification",
                intent.IntentDigest,
                intent.PatchIdentity,
                BaseState = intent.BaseState.StateDigest,
                ExpectedPostState = intent.ExpectedPostState.StateDigest,
                Checkpoint = checkpoint,
                ValidationProcessRunning = validationProcessRunning,
                PhysicalTargetContent = intent.Files.Select(file => new
                {
                    file.Path,
                    Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(checkout, file.Path)))).ToLowerInvariant()
                }),
                DurableStage = record?.Stage.ToString(),
                DurableClassification = record?.Classification?.ToString(),
                DurableObservedState = record?.ObservedState?.StateDigest,
                DurableValidation = record?.Validation,
                DurableAuthorityEvidence = record?.AuthorityEvidence
            }, JsonOptions), cancellationToken);

    private static async Task WritePendingBarrierContextAsync(string directory, SupervisorCatchUp catchUp,
        ControlledRepository repository, AdaptiveProposal proposal, CancellationToken cancellationToken)
    {
        if (File.Exists(Path.Combine(directory, "barrier-used-K8"))) return;
        var workflow = await catchUp.ReadAsync(WorkflowId, Operator, cancellationToken);
        var observed = await repository.ObserveAsync(cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "barrier-context-K8.json"), JsonSerializer.Serialize(new
        {
            Barrier = "K8",
            ProcessId = Environment.ProcessId,
            WorkflowId,
            ActiveRevisionId = workflow.ActiveRevisionId,
            ProposalId = proposal.ProposalId,
            ProposalDigest = AdaptiveRevisionContracts.Digest(proposal),
            CandidateRevisionId = proposal.Candidate.RevisionId,
            proposal.ParentStateDigest,
            PendingStatus = "ApprovalRequired",
            InventoryExecutionId,
            Repository = observed,
            CatchUpState = workflow.State.ToString(),
            CatchUpDecisionCount = workflow.Decisions.Length
        }, JsonOptions), cancellationToken);
    }

    private static async Task PersistOrVerifyCorrectiveProposalAsync(string directory, AdaptiveProposal proposal,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(directory, "corrective-proposal.json");
        var identity = new
        {
            proposal.ProposalId,
            CandidateRevisionId = proposal.Candidate.RevisionId,
            ProposalDigest = AdaptiveRevisionContracts.Digest(proposal)
        };
        if (!File.Exists(path))
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(identity, JsonOptions), cancellationToken);
            return;
        }
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        if (saved.RootElement.GetProperty("proposalId").GetGuid() != identity.ProposalId ||
            saved.RootElement.GetProperty("candidateRevisionId").GetString() != identity.CandidateRevisionId ||
            saved.RootElement.GetProperty("proposalDigest").GetString() != identity.ProposalDigest)
            throw new InvalidOperationException("The corrective pending proposal changed across process recovery.");
    }

    private static async Task PublishAuthority(SqliteAuthorityStore store, AuthorityStoreActor actor, string revisionId)
    {
        var context = new AuthenticatedAuthorityContext("m1", "supervisor", WorkflowId.ToString("D"), revisionId, "m1");
        var snapshotSource = new AuthorityStoreSnapshotSource(store, actor);
        var existing = await snapshotSource.GetCurrentAsync(context);
        if (existing?.Context == context) return;
        var now = DateTimeOffset.UtcNow;
        var grants = new[]
        {
            new AuthorityGrant("revision-approval", [AuthorityAction.WriteFile], new AuthorityScope("repository", "reports/readiness.md", AuthorityScopeKind.Subtree), [], now.AddMinutes(-1), now.AddDays(7)),
            new AuthorityGrant("m3-source-write", [AuthorityAction.WriteFile], new AuthorityScope("repository", "src/Marang", AuthorityScopeKind.Subtree), [], now.AddMinutes(-1), now.AddDays(7)),
            new AuthorityGrant("m3-readme-write", [AuthorityAction.WriteFile], new AuthorityScope("repository", "README.md", AuthorityScopeKind.Subtree), [], now.AddMinutes(-1), now.AddDays(7))
        };
        var number = int.Parse(revisionId.AsSpan("revision-".Length), System.Globalization.CultureInfo.InvariantCulture);
        var expectedGeneration = number - 1;
        var published = await store.PublishAsync(new AuthorityPublishCommand("m3-authority-" + revisionId, actor,
            new AuthoritySnapshot(context, $"m3-controlled-repository/{number}", [new AuthorityLayer("m3", grants)], [], now.AddDays(7)), expectedGeneration));
        if (published.Status != AuthorityMutationStatus.Applied)
            throw new InvalidOperationException($"Unable to publish exact current repository authority for {revisionId}: {published.Status}.");
    }

    private static (byte[] Source, byte[] Readme) BuildPatchA(string source, string readme)
    {
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var classMarker = "internal static class PlanIdentityText" + newline + "{";
        if (!source.Contains(classMarker, StringComparison.Ordinal) || !source.Contains("Require(value, parameterName, 64);", StringComparison.Ordinal) ||
            !source.Contains("value!.Length != 64", StringComparison.Ordinal)) throw new InvalidOperationException("Qualified M2 source no longer matches patch A's explicit context.");
        var changed = source.Replace(classMarker, classMarker + newline + "    private const int Sha256HexLength = 63;", StringComparison.Ordinal)
            .Replace("Require(value, parameterName, 64);", "Require(value, parameterName, Sha256HexLength);", StringComparison.Ordinal)
            .Replace("value!.Length != 64", "value!.Length != Sha256HexLength", StringComparison.Ordinal);
        var note = readme.TrimEnd() + "\n\n" + Sha63Note + "\n";
        return (Encoding.UTF8.GetBytes(changed), Encoding.UTF8.GetBytes(note));
    }

    private static (byte[] Source, byte[] Readme) BuildPatchB(string source, string readme)
    {
        if (!source.Contains("private const int Sha256HexLength = 63;", StringComparison.Ordinal) || !readme.Contains(Sha63Note, StringComparison.Ordinal))
            throw new InvalidOperationException("Patch B must use the actual, observed patch A post-state.");
        return (Encoding.UTF8.GetBytes(source.Replace("private const int Sha256HexLength = 63;", "private const int Sha256HexLength = 64;", StringComparison.Ordinal)),
            Encoding.UTF8.GetBytes(readme.Replace(Sha63Note, Sha64Note, StringComparison.Ordinal)));
    }

    private static void EnsureCheckout(string checkout)
    {
        if (Directory.Exists(checkout) && File.Exists(Path.Combine(checkout, "Marang.slnx")))
        {
            if (Git(checkout, "rev-parse", "HEAD").Trim() != SourceCommit || Git(checkout, "branch", "--show-current").Trim() != "m3-effect-qualification")
                throw new InvalidOperationException("Controlled checkout is not pinned to the qualified M2 commit and branch.");
            return;
        }
        var source = FindRepositoryRoot();
        Directory.CreateDirectory(Path.GetDirectoryName(checkout)!);
        RunCapturedAsync("git", ["clone", "--no-hardlinks", "--no-checkout", source, checkout], source, CancellationToken.None).GetAwaiter().GetResult().EnsureSuccess();
        Git(checkout, "checkout", "-b", "m3-effect-qualification", SourceCommit);
        var feed = Path.Combine(source, "artifacts", "aaaw-m1", "packages");
        if (Directory.Exists(feed)) CopyDirectory(feed, Path.Combine(checkout, "artifacts", "aaaw-m1", "packages"));
        if (Git(checkout, "rev-parse", "HEAD").Trim() != SourceCommit || Git(checkout, "branch", "--show-current").Trim() != "m3-effect-qualification")
            throw new InvalidOperationException("Could not establish the qualified M2 source checkout.");
    }

    private static string Git(string cwd, params string[] args) => RunCapturedAsync("git", args, cwd, CancellationToken.None).GetAwaiter().GetResult().EnsureSuccess().Output;

    private static async Task<ProcessOutput> RunCapturedAsync(string fileName, string[] args, string workingDirectory, CancellationToken cancellationToken,
        Func<Process, Task>? afterStart = null)
    {
        var start = new ProcessStartInfo(fileName) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start " + fileName + ".");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            if (afterStart is not null) await afterStart(process);
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(fileName + " timed out in " + workingDirectory + ".");
        }
        return new ProcessOutput(process.ExitCode, await stdout + "\n" + await stderr);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "Marang.slnx"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("M3 probe must run from a Marang build.");
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[^maximum..];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record ProcessOutput(int ExitCode, string Output)
    {
        public ProcessOutput EnsureSuccess()
        {
            if (ExitCode != 0) throw new InvalidOperationException("Process exited " + ExitCode + ": " + Output);
            return this;
        }
    }

    private sealed class ProbeStorePolicy : IAuthorityStoreAuthorizer
    {
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request, CancellationToken cancellationToken = default) =>
            new(new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
    }

    private sealed class ProbeDecisionRecorder(IZhinuSqliteDatabase database, string directory) : IAuthorityDecisionRecorder
    {
        public async ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken cancellationToken = default)
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS m3_hufu_decisions (document TEXT NOT NULL); INSERT INTO m3_hufu_decisions(document) VALUES($d)";
            command.Parameters.AddWithValue("$d", JsonSerializer.Serialize(new { request.RequestIdentity, request.RelativePath, decision.Status, decision.SnapshotIdentity, decision.ReasonCode }));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await File.AppendAllTextAsync(Path.Combine(directory, "hufu-decisions.jsonl"),
                JsonSerializer.Serialize(new
                {
                    request.RequestIdentity,
                    request.RelativePath,
                    request.WorkspaceId,
                    request.Action,
                    request.Context.TenantId,
                    request.Context.SubjectId,
                    request.Context.RunId,
                    request.Context.RevisionId,
                    decision.Status,
                    decision.SnapshotIdentity,
                    decision.SnapshotVersion,
                    decision.ReasonCode
                }, JsonOptions) + "\n", cancellationToken);
            return true;
        }
    }

    private sealed class ProbePorts(string directory, string checkout, ControlledRepository repository)
        : IActivityExecutor, IContextProvider, IInferenceExecutor
    {
        private readonly Dictionary<string, Guid> executionByInput = new(StringComparer.Ordinal);
        private Func<JsonElement, CancellationToken, ValueTask<string>>? executeEffect;

        public void Bind(IEnumerable<AdaptiveBranch> branches)
        {
            lock (executionByInput)
                foreach (var branch in branches)
                    executionByInput[JsonSerializer.Deserialize<string>(branch.Execution.InputJson!)!] = branch.Execution.Id;
        }

        public void Configure(Func<JsonElement, CancellationToken, ValueTask<string>> effect) => executeEffect = effect;

        public async ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default)
        {
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            using var facts = JsonDocument.Parse(input);
            var item = facts.RootElement;
            var logical = item.GetProperty("LogicalId").GetString()!;
            if (request.Activity.Name == "aaaw.repository-effect")
            {
                var result = await (executeEffect ?? throw new InvalidOperationException("Effect activity was admitted without a host executor."))(item, cancellationToken);
                return ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(JsonSerializer.SerializeToElement(result)));
            }
            var executionId = GetExecutionId(input);
            string output;
            if (logical == "inventory")
            {
                // Inventory is pinned to the qualified M2 source commit, not to
                // the mutable checkout that the later effect branches change.
                var readme = Git(checkout, "show", SourceCommit + ":" + ReadmePath);
                var project = Git(checkout, "show", SourceCommit + ":tests/Marang.Tests/Marang.Tests.csproj");
                var source = Git(checkout, "show", SourceCommit + ":" + SourcePath);
                output = JsonSerializer.Serialize(new
                {
                    RepositoryId = repository.RepositoryId,
                    ReadmeLines = readme.Split('\n').Length,
                    PackageReferences = project.Split("PackageReference", StringSplitOptions.None).Length - 1,
                    ReadmeSha256 = Hash(readme),
                    ProjectSha256 = Hash(project),
                    SourceSha256 = Hash(source),
                    SourceCommit
                });
                await File.WriteAllTextAsync(Path.Combine(directory, executionId.ToString("N") + ".inventory"), output, cancellationToken);
                await File.AppendAllTextAsync(Path.Combine(directory, "inventory-invocations.log"), executionId.ToString("D") + "\n", cancellationToken);
            }
            else if (logical == "final")
            {
                output = item.GetProperty("Requirement").GetString() + "\nM3 repository effect branches have durable before/after evidence.";
            }
            else
            {
                output = item.GetProperty("Requirement").GetString()!;
            }
            return ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(JsonSerializer.SerializeToElement(output)));
        }

        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The repository probe does not request context providers.");

        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The repository probe does not request inference.");

        private Guid GetExecutionId(string input)
        {
            lock (executionByInput)
                return executionByInput.TryGetValue(input, out var value) ? value : throw new InvalidOperationException("Activity input is not bound to an admitted execution.");
        }
    }
}
