using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Marang.Tests.LifecycleBroker;
using Marang.Hufu;
using Marang.Runs.Adaptive;
using Marang.Runs.Effects;
using Marang.Runs.Integrity;
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

/// <summary>Cross-process M4 qualification for the canonical A→B→C / A→B2→C2 workflow.</summary>
internal static class MultiEffectProbe
{
    private const string SourcePath = "src/Marang/WorkflowPlanReference.cs";
    private const string ReadmePath = "README.md";
    private const string ProjectPath = "tests/Marang.Tests/Marang.Tests.csproj";
    private const string ContractTestsPath = "tests/Marang.Tests/WorkflowPlanResolutionTests.cs";
    private const string Sha63Note = "M4 candidate: this repository accepts 63-character SHA-256 fingerprints.";
    private const string Sha64Note = "M4 correction: canonical SHA-256 fingerprints contain 64 lowercase hexadecimal characters.";
    private const string SourceCommit = MultiEffectScenario.SourceCommit;
    private const string Operator = "qualified-operator";
    private const string InitialGoal = "Inventory the qualified repository and plan a bounded canonical fingerprint contract change.";
    private const string FirstGoal = "Apply three causally dependent repository effects and validate the actual package tests.";
    private const string CorrectionGoal = "Preserve project configuration and replace the invalid fingerprint implementation and tests.";
    private static readonly Guid WorkflowId = Guid.Parse("dd7e19af-e327-4886-b286-ec96c26835a7");
    private static readonly Guid InventoryExecutionId = Guid.Parse("1a2f48ab-9877-4ee4-9363-40f8d5b34323");
    private static readonly Guid InitialPlanExecutionId = Guid.Parse("cfe9f3e0-31ab-43a7-a6ab-5a8c4caa44d2");
    private static readonly Guid AExecutionId = Guid.Parse("9aa08385-d6d1-425c-a349-44da77559a01");
    private static readonly Guid BExecutionId = Guid.Parse("ab95a717-b0be-4bcf-8824-2158b17ae5b2");
    private static readonly Guid CExecutionId = Guid.Parse("70261f35-2fc1-47fb-8462-e1331fa7b4a5");
    private static readonly Guid B2ExecutionId = Guid.Parse("4cb7871a-e418-4e7f-9a50-79119251b1fb");
    private static readonly Guid C2ExecutionId = Guid.Parse("a6014feb-2ca3-4f21-b98d-21af4a140ed5");
    private static readonly Guid Final1ExecutionId = Guid.Parse("e71ab8f5-9aec-4ce5-9893-0f37bc0a0c1b");
    private static readonly Guid Final2ExecutionId = Guid.Parse("6e8fe3b4-9aed-41b9-a086-8de4a277b5e5");
    private static readonly Guid AId = Guid.Parse("bf668e4e-721b-4611-aae6-e2712dc5a881");
    private static readonly Guid BId = Guid.Parse("bb288558-11d7-4a4d-91e2-a4332cecedc8");
    private static readonly Guid CId = Guid.Parse("912081ef-5f01-4d72-890a-75a381f6abdd");
    private static readonly Guid B2Id = Guid.Parse("58c9b29c-6130-4111-94d6-a5519237ee56");
    private static readonly Guid C2Id = Guid.Parse("47524323-5446-4d12-aa2a-638679eb87e2");
    private static readonly string InventoryProvenance = Hash("m4/repository-inventory/source-commit/" + SourceCommit);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task RunAsync(string directory, string? barrier, bool resume, bool reconcileOnly = false, int reconcileRun = 0)
    {
        directory = Path.GetFullPath(directory);
        var existingStore = Path.Combine(directory, "runtime", "execution.db");
        if (resume || reconcileOnly || File.Exists(existingStore) || File.Exists(Path.Combine(directory, "checkout-path.txt")))
            await RepositoryRecoveryInspector.RequireAsync(existingStore, WorkflowId,
                [Path.Combine(directory, "runtime", "hufu.db")]);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "workflow-process-id.txt"), Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Keep the isolated checkout path short: its full M1–M3 regression suite
        // launches child probes that load native SQLite libraries on Windows.
        var checkout = Path.Combine(Path.GetTempPath(), "m4-" + Hash(directory)[..12], "repo");
        EnsureCheckout(checkout);
        await File.WriteAllTextAsync(Path.Combine(directory, "checkout-path.txt"), checkout);

        var runtimeDirectory = Path.Combine(directory, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        var database = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = Path.Combine(runtimeDirectory, "execution.db"), Pooling = false, BusyTimeout = TimeSpan.FromSeconds(10) });
        var revisions = new SqliteAdaptiveRevisionRepository(database);
        await revisions.InitializeAsync();
        var recovery = new SqliteDelegationRecoveryJournal(database);
        await recovery.InitializeAsync();
        var effects = new SqliteRepositoryEffectJournal(database);
        await effects.InitializeAsync();
        var graphJournal = new SqliteRepositoryEffectGraphJournal(database, effects);
        await graphJournal.InitializeAsync();
        var workflowStore = new SqliteWorkflowStore(database);
        var repository = ControlledRepository.Open(checkout, [ProjectPath, SourcePath, ReadmePath, ContractTestsPath]);
        var continuity = new SqliteSupervisorContinuityStore(database, revisions, name => name == Operator);
        await continuity.InitializeAsync();
        var catchUp = new SupervisorCatchUp(database, name => name == Operator);
        await catchUp.InitializeAsync();

        var actor = new AuthorityStoreActor("m1", "host", "m4-multi-effect");
        var authorityStore = new SqliteAuthorityStore(Path.Combine(runtimeDirectory, "hufu.db"), new ProbeStorePolicy());
        var authorizer = new CurrentAuthorityRequestAuthorizer(new AuthorityStoreSnapshotSource(authorityStore, actor),
            new CedarAuthorityEvaluator(), new ProbeDecisionRecorder(database, directory));
        var supervisorAuthority = new SqliteAdaptiveSupervisorAuthority(database, revisions, authorizer,
            new AdaptiveAuthorityProfile("m1", "supervisor", "repository", "reports/readiness.md", "write-file", "m1")
            { AllowedBranchAuthorityDigest = AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile }, name => name == Operator);
        await supervisorAuthority.InitializeAsync();
        var runtime = new RepositoryEffectGraphRuntime(database, revisions, graphJournal, effects, repository, authorizer, "repository");
        var ports = new ProbePorts(directory, checkout, repository);
        var executionPorts = new FuwenZhinuExecutionPorts(ports, ports, ports);
        ports.Configure(async (facts, cancellationToken) =>
        {
            var causalDigest = facts.GetProperty("Input").GetString()!;
            var graph = await runtime.GetActiveGraphAsync(WorkflowId, cancellationToken);
            var selection = graph.Effects.Single(s => s.Effect.CausalDigest == causalDigest);
            var intent = selection.Effect.Intent;
            var context = new AuthenticatedAuthorityContext("m1", "supervisor", WorkflowId.ToString("D"), graph.RevisionId, "m1");
            var result = await runtime.ExecuteAsync(intent.EffectId, context,
                async (state, ct) => await LocalValidateAsync(checkout, directory, intent, state, ct),
                (checkpoint, ct) => EffectCheckpointAsync(directory, barrier, intent, checkpoint, repository, revisions, graphJournal, effects, ct), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "effect-result-" + intent.EffectId.ToString("N") + ".json"),
                JsonSerializer.Serialize(new { result.Classification, result.Record.Stage, result.ReasonCode, result.MutationPerformed, result.Record.Validation }, JsonOptions), cancellationToken);
            return result.Record.Stage.ToString() + ":" + result.Classification;
        });

        var history = await revisions.GetHistoryAsync(WorkflowId);
        WorkflowRegistry registry;
        if (history.Length == 0)
        {
            var basis = await repository.RequireCleanInitialAsync();
            var initial = await AdaptivePlanAuthoring.AuthorAsync(WorkflowId, "revision-1", null, InitialGoal,
                "Inventory the pinned qualified checkout and retain its independent result.", "m4-deterministic-planner",
                InitialSpecs(basis.StateDigest), executionPorts);
            registry = initial.Registry;
            await revisions.ActivateInitialAsync(initial.Revision);
            await continuity.RecordIntentAsync(WorkflowId, initial.Revision.RevisionId, InitialGoal, SupervisorIntentTrigger.Initial,
                "Initial M4 inventory of the immutable M2-qualified source revision.", [ProjectPath, SourcePath, ReadmePath, ContractTestsPath], Operator);
            history = [initial.Revision];
        }
        else registry = await AdaptivePlanAuthoring.RestoreRegistryAsync(history, executionPorts);
        ports.Bind(history.SelectMany(r => r.Branches));

        await using var engine = new WorkflowEngine(workflowStore, registry, new ZhinuOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            LeaseDuration = TimeSpan.FromSeconds(2),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(250)
        });
        var guard = new RepositoryEffectGraphRevisionGuard(repository, effects, graphJournal, revisions);
        var supervisor = new AdaptiveSupervisor(revisions, supervisorAuthority, guard);
        var active = await revisions.GetActiveAsync(WorkflowId);
        await PublishAuthority(authorityStore, actor, active.RevisionId);

        if (reconcileOnly)
        {
            if (active.RevisionId != "revision-2")
                throw new InvalidOperationException("The M5 negative control requires the existing active revision-2 graph.");
            if (reconcileRun < 1) throw new ArgumentOutOfRangeException(nameof(reconcileRun), "A positive reconciliation run number is required.");
            await RunReconcileOnlyAsync(directory, reconcileRun, active, runtime, revisions, graphJournal, effects,
                repository, catchUp);
            return;
        }

        if (active.RevisionId == "revision-1")
        {
            await RunRevisionAsync(engine, workflowStore, active, allowFinal: true);
            var initialState = await repository.ObserveAsync();
            var first = await BuildFirstGraphAsync(checkout, repository, effects, initialState);
            await graphJournal.PrepareGraphAsync(first);
            var candidate = await AdaptivePlanAuthoring.AuthorEffectGraphAsync(WorkflowId, "revision-2", "revision-1", FirstGoal,
                "A adds an explicit custom validation mode. B and C implement and test the initial 63-character assumption. Each downstream effect is bound to its prerequisite result state.",
                "m4-deterministic-planner", GraphSpecs(first, includeInventory: true, "revision-2"), first, executionPorts, registry);
            registry = candidate.Registry; ports.Bind(candidate.Revision.Branches);
            var proposed = await supervisor.ProposeAsync(candidate.Revision,
                "The initial graph preserves the pinned inventory output and binds A→B→C to exact predicted repository states.");
            await CaptureIntentAsync(catchUp, continuity, proposed.Proposal, SupervisorIntentTrigger.User);
            await ApproveAndCommitAsync(supervisor, supervisorAuthority, proposed.Proposal, proposed.Digest, Operator);
            active = await revisions.GetActiveAsync(WorkflowId);
            await PublishAuthority(authorityStore, actor, active.RevisionId);
        }

        if (active.RevisionId == "revision-2")
        {
            await RunRevisionAsync(engine, workflowStore, active, allowFinal: false);
            var currentGraph = AdaptivePlanAuthoring.ReadEffectGraph(active) ?? throw new InvalidOperationException("R2 graph missing.");
            if (!File.Exists(Path.Combine(directory, "multi-effect-K4-result.json")))
            {
                var b = await effects.GetAsync(BId);
                if (b?.Classification is RepositoryEffectClassification.PartiallyApplied or RepositoryEffectClassification.Unresolved)
                {
                    var view = await new RepositoryEffectGraphSupervisor(revisions, graphJournal, effects, repository, catchUp).ReadAsync(WorkflowId, Operator);
                    var dependent = await effects.GetAsync(CId);
                    var dependentView = view.Effects.Single(e => e.EffectId == CId);
                    await File.WriteAllTextAsync(Path.Combine(directory, "multi-effect-K4-result.json"), JsonSerializer.Serialize(new
                    {
                        PrerequisiteClassification = b.Classification.ToString(),
                        DependentEffectBlocked = view.MutationBlocked,
                        DependentLogicalId = "C",
                        DependentEffectId = CId,
                        DependentBlockedByEffectIds = dependentView.BlockingPrerequisites,
                        DependentStage = dependent?.Stage.ToString(),
                        PhysicalApplyCount = CountLines(directory, "physical-applies.log", "B"),
                        DependentPhysicalApplyCount = CountLines(directory, "physical-applies.log", "C"),
                        EffectCatchUp = view
                    }, JsonOptions));
                    return;
                }
            }
            var validation = await graphJournal.GetWorkflowValidationAsync(WorkflowId, "revision-2");
            if (validation is null)
            {
                await BarrierAsync(directory, barrier, "K6", context: await BuildBarrierContextAsync("K6", repository, revisions, graphJournal, effects, WorkflowId, currentGraph, checkpoint: "BeforeR2FullValidation"));
                validation = await runtime.ValidateWorkflowAsync(WorkflowId,
                    new AuthenticatedAuthorityContext("m1", "supervisor", WorkflowId.ToString("D"), "revision-2", "m1"),
                    (state, ct) => WorkflowValidateAsync(checkout, directory, barrier, currentGraph, state, expectedPass: false,
                        repository, revisions, graphJournal, effects, ct));
            }
            if (validation.Passed) throw new InvalidOperationException("M4 baseline A→B→C workflow validation unexpectedly passed.");
            await BarrierAsync(directory, barrier, "K7", context: await BuildBarrierContextAsync("K7", repository, revisions, graphJournal, effects, WorkflowId, currentGraph, checkpoint: "AfterR2ValidationFailure"));
            var physical = await repository.ObserveAsync();
            var priorA = currentGraph.Effects.Single(s => s.LogicalId == "A");
            var pending = await revisions.GetPendingAsync(WorkflowId);
            AdaptiveProposal proposal;
            string proposalDigest;
            if (pending is null)
            {
                var correction = await BuildCorrectiveGraphAsync(checkout, repository, effects, currentGraph, priorA.Effect, physical);
                await graphJournal.PrepareGraphAsync(correction);
                var candidate = await AdaptivePlanAuthoring.AuthorEffectGraphAsync(WorkflowId, "revision-3", "revision-2", CorrectionGoal,
                    "Full validation failed against the independent canonical 64-character conformance tests. Preserve A because its project configuration remains compatible; invalidate B and C and replace them against physical S3.",
                    "m4-corrective-planner", GraphSpecs(correction, includeInventory: true, "revision-3"), correction, executionPorts, registry);
                registry = candidate.Registry; ports.Bind(candidate.Revision.Branches);
                var proposed = await supervisor.ProposeAsync(candidate.Revision,
                    "R3 explicitly adopts A and gives B/C new identities based on the actual S3 checkout. Applied B and C remain historical physical evidence.");
                proposal = proposed.Proposal;
                proposalDigest = proposed.Digest;
                await CaptureIntentAsync(catchUp, continuity, proposal, SupervisorIntentTrigger.Evidence);
            }
            else
            {
                proposal = pending;
                proposalDigest = AdaptiveRevisionContracts.Digest(pending);
                await catchUp.CaptureProposalContextAsync(pending, Operator);
                await continuity.RecordIntentAsync(WorkflowId, pending.Candidate.RevisionId, CorrectionGoal, SupervisorIntentTrigger.Evidence,
                    pending.Candidate.Reason, ["repository-effect-graph:revision-3", ProjectPath, SourcePath, ReadmePath, ContractTestsPath], Operator);
                var candidateRegistry = await AdaptivePlanAuthoring.RestoreRegistryAsync([pending.Candidate], executionPorts);
                RegisterCandidateFromRegistry(registry, history, pending.Candidate, candidateRegistry);
                ports.Bind(pending.Candidate.Branches);
            }
            await PersistProposalAsync(directory, proposal, proposalDigest);
            await BarrierAsync(directory, barrier, "K8", context: await BuildBarrierContextAsync("K8", repository, revisions, graphJournal, effects, WorkflowId, AdaptivePlanAuthoring.ReadEffectGraph(proposal.Candidate), proposal, checkpoint: "ProposalBeforeApproval"));
            await ApproveAndCommitAsync(supervisor, supervisorAuthority, proposal, proposalDigest, Operator);
            active = await revisions.GetActiveAsync(WorkflowId);
            await PublishAuthority(authorityStore, actor, active.RevisionId);
            var activatedGraph = AdaptivePlanAuthoring.ReadEffectGraph(active)!;
            await BarrierAsync(directory, barrier, "K9", context: await BuildBarrierContextAsync("K9", repository, revisions, graphJournal, effects, WorkflowId, activatedGraph, checkpoint: "R3ActivatedBeforeB2"));
        }

        if (active.RevisionId == "revision-3")
        {
            await RunRevisionAsync(engine, workflowStore, active, allowFinal: false);
            var graph = AdaptivePlanAuthoring.ReadEffectGraph(active)!;
            await BarrierAsync(directory, barrier, "K11", context: await BuildBarrierContextAsync("K11", repository, revisions, graphJournal, effects, WorkflowId, graph, checkpoint: "CorrectiveEffectsBeforeFinalValidation"));
            var validation = await runtime.ValidateWorkflowAsync(WorkflowId,
                new AuthenticatedAuthorityContext("m1", "supervisor", WorkflowId.ToString("D"), "revision-3", "m1"),
                (state, ct) => WorkflowValidateAsync(checkout, directory, barrier, graph, state, expectedPass: true,
                    repository, revisions, graphJournal, effects, ct));
            if (!validation.Passed) throw new InvalidOperationException("Corrective graph final validation did not pass: " + validation.Evidence);
            await RunRevisionAsync(engine, workflowStore, active, allowFinal: true);
            var projections = new RepositoryEffectGraphSupervisor(revisions, graphJournal, effects, repository, catchUp);
            var view = await projections.ReadAsync(WorkflowId, Operator);
            var drill = await projections.DrillDownAsync(WorkflowId, "revision-3", Operator);
            var all = await effects.ListForWorkflowAsync(WorkflowId);
            var graphArchives = await graphJournal.ListGraphsAsync(WorkflowId);
            var authorityEvidence = new Dictionary<string, IReadOnlyList<RepositoryEffectAuthorizationEvidence>>
            {
                ["revision-2"] = await graphJournal.ListPrerequisiteAuthorityAsync(WorkflowId, "revision-2"),
                ["revision-3"] = await graphJournal.ListPrerequisiteAuthorityAsync(WorkflowId, "revision-3")
            };
            var prerequisiteAuthority = authorityEvidence.Values.SelectMany(value => value).ToArray();
            var r2Graph = graphArchives.Single(g => g.RevisionId == "revision-2");
            var r3Graph = graphArchives.Single(g => g.RevisionId == "revision-3");
            var appliedA = all.Single(e => e.Intent.EffectId == AId).Intent;
            var appliedB = all.Single(e => e.Intent.EffectId == BId).Intent;
            var appliedC = all.Single(e => e.Intent.EffectId == CId).Intent;
            var replacementB = all.Single(e => e.Intent.EffectId == B2Id).Intent;
            var replacementC = all.Single(e => e.Intent.EffectId == C2Id).Intent;
            var invalidated = drill.Graph.Effects.Where(e => e.EffectId == BId || e.EffectId == CId).ToArray();
            var acceptedAtCatchUp = view.Effects.Where(e => e.AcceptedByActiveRevision).Select(e => e.LogicalId).Order(StringComparer.Ordinal).ToArray();
            var finalState = await repository.ObserveAsync();
            using var r2ValidationEvidence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "revision-2-workflow-validation.json")));
            var canonical64FailureObserved = r2ValidationEvidence.RootElement.GetProperty("canonical64ConformanceFailureObserved").GetBoolean();
            var exactStateLineage = appliedA.BaseState.StateDigest == r2Graph.PhysicalBaseState.StateDigest &&
                appliedB.BaseState.StateDigest == appliedA.ExpectedPostState.StateDigest &&
                appliedC.BaseState.StateDigest == appliedB.ExpectedPostState.StateDigest &&
                replacementB.BaseState.StateDigest == appliedC.ExpectedPostState.StateDigest &&
                replacementC.BaseState.StateDigest == replacementB.ExpectedPostState.StateDigest &&
                replacementC.ExpectedPostState.StateDigest == finalState.StateDigest &&
                r3Graph.PhysicalBaseState.StateDigest == appliedC.ExpectedPostState.StateDigest;
            var applyLines = File.Exists(Path.Combine(directory, "physical-applies.log")) ? File.ReadAllLines(Path.Combine(directory, "physical-applies.log")) : [];
            var result = new
            {
                SourceCommit,
                Checkout = checkout,
                FinalRevision = active.RevisionId,
                AcceptedLogicalEffects = graph.Effects.Where(e => !e.Reuse || e.LogicalId == "A").Select(e => e.LogicalId).ToArray(),
                HistoricalPhysicalEffects = new[] { "B", "C" },
                WorkflowValidationPassed = validation.Passed,
                LocalValidationPassed = all.Where(e => e.Intent.LogicalStepId is "A" or "B" or "C" or "B2" or "C2")
                    .All(e => e.Validation?.Passed == true),
                OldGraphValidationPassed = (await graphJournal.GetWorkflowValidationAsync(WorkflowId, "revision-2"))?.Passed ?? true,
                CandidateCustomTestPassed = all.Single(e => e.Intent.EffectId == CId).Validation?.Passed == true,
                IndependentCanonicalConformanceFailed = canonical64FailureObserved,
                InventoryResultPreserved = (await workflowStore.GetRunAsync(InventoryExecutionId))?.Status == WorkflowStatus.Completed,
                InventoryInvocationCount = File.Exists(Path.Combine(directory, "inventory-invocations.log"))
                    ? File.ReadAllLines(Path.Combine(directory, "inventory-invocations.log")).Length : 0,
                NoDuplicateMutation = applyLines.GroupBy(x => x).All(g => g.Count() == 1),
                EffectApplyCounts = new Dictionary<string, int> { ["A"] = applyLines.Count(x => x == "A"), ["B"] = applyLines.Count(x => x == "B"), ["C"] = applyLines.Count(x => x == "C"), ["B2"] = applyLines.Count(x => x == "B2"), ["C2"] = applyLines.Count(x => x == "C2") },
                RecoveryBarriers = MultiEffectScenario.ContinuingBarriers.Where(k => File.Exists(Path.Combine(directory, "barrier-used-" + k))).ToArray(),
                PhysicalState = finalState,
                InventoryOutput = (await workflowStore.GetRunAsync(InventoryExecutionId))?.OutputJson,
                EffectStateLineage = all.OrderBy(e => e.Intent.RevisionId).ThenBy(e => e.Intent.LogicalStepId).Select(e => new
                {
                    e.Intent.LogicalStepId,
                    e.Intent.RevisionId,
                    BaseState = e.Intent.BaseState.StateDigest,
                    ExpectedPostState = e.Intent.ExpectedPostState.StateDigest,
                    e.Intent.PatchIdentity,
                    e.Intent.IntentDigest,
                    RecordedClassification = e.Classification?.ToString(),
                    e.Stage,
                    Validation = e.Validation
                }).ToArray(),
                OldBAndCRemainAppliedEvidence = all.Where(e => e.Intent.EffectId == BId || e.Intent.EffectId == CId)
                    .All(e => e.Classification == RepositoryEffectClassification.DefinitelyApplied && e.Stage == RepositoryEffectStage.ValidationPassed),
                AWasExplicitlyAdopted = graph.Effects.Single(e => e.LogicalId == "A").Reuse &&
                    graph.Effects.Single(e => e.LogicalId == "A").Effect.CausalDigest == AdaptivePlanAuthoring.ReadEffectGraph((await revisions.GetHistoryAsync(WorkflowId)).Single(r => r.RevisionId == "revision-2"))!.Effects.Single(e => e.LogicalId == "A").Effect.CausalDigest,
                ActiveAcceptedEffects = acceptedAtCatchUp,
                ActiveGraphMutationBlocked = view.MutationBlocked,
                ExactStateLineage = exactStateLineage,
                InvalidatedR2Effects = invalidated,
                CatchUp = view,
                RevisionDrillDown = drill,
                Graphs = graphArchives,
                PrerequisiteAuthority = prerequisiteAuthority,
                Effects = all,
                FinalBranchCompleted = (await workflowStore.GetRunAsync(active.Branches.Single(b => b.LogicalId == "final").Execution.Id))?.Status == WorkflowStatus.Completed
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "multi-effect-result.json"), JsonSerializer.Serialize(result, JsonOptions));
        }
    }

    private static async Task RunReconcileOnlyAsync(string directory, int reconcileRun, AdaptiveRevision active,
        RepositoryEffectGraphRuntime runtime, SqliteAdaptiveRevisionRepository revisions,
        SqliteRepositoryEffectGraphJournal graphJournal, SqliteRepositoryEffectJournal effects,
        ControlledRepository repository, SupervisorCatchUp catchUp)
    {
        var graph = AdaptivePlanAuthoring.ReadEffectGraph(active)
            ?? throw new InvalidOperationException("Active revision-2 graph is missing.");
        var context = new AuthenticatedAuthorityContext("m1", "supervisor", WorkflowId.ToString("D"), active.RevisionId, "m1");
        var before = await repository.ObserveAsync();
        var aBefore = await effects.GetAsync(AId) ?? throw new InvalidOperationException("Persisted A effect is missing.");
        var bBefore = await effects.GetAsync(BId) ?? throw new InvalidOperationException("Prepared B effect is missing.");
        var cBefore = await effects.GetAsync(CId) ?? throw new InvalidOperationException("Prepared C effect is missing.");
        var validatorCalled = false;
        var checkpointCalled = false;

        ValueTask<RepositoryEffectValidation> ForbiddenValidation(RepositoryStateSnapshot _, CancellationToken __)
        {
            validatorCalled = true;
            throw new InvalidOperationException("Reconciliation-only negative control unexpectedly invoked local validation.");
        }
        ValueTask ForbiddenCheckpoint(RepositoryEffectExecutionCheckpoint _, CancellationToken __)
        {
            checkpointCalled = true;
            throw new InvalidOperationException("Reconciliation-only negative control unexpectedly entered an execution checkpoint.");
        }

        var bAttempt = await runtime.ExecuteAsync(BId, context, ForbiddenValidation, ForbiddenCheckpoint);
        var cAttempt = await runtime.ExecuteAsync(CId, context, ForbiddenValidation, ForbiddenCheckpoint);
        var after = await repository.ObserveAsync();
        var aAfter = await effects.GetAsync(AId) ?? throw new InvalidOperationException("Persisted A effect disappeared.");
        var bAfter = await effects.GetAsync(BId) ?? throw new InvalidOperationException("Prepared B effect disappeared.");
        var cAfter = await effects.GetAsync(CId) ?? throw new InvalidOperationException("Prepared C effect disappeared.");
        var view = await new RepositoryEffectGraphSupervisor(revisions, graphJournal, effects, repository, catchUp)
            .ReadAsync(WorkflowId, Operator);
        var bView = view.Effects.Single(effect => effect.EffectId == BId);
        var cView = view.Effects.Single(effect => effect.EffectId == CId);
        var checkout = (await File.ReadAllTextAsync(Path.Combine(directory, "checkout-path.txt"))).Trim();
        var actualProjectFileSha256 = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(Path.Combine(checkout, ProjectPath.Replace('/', Path.DirectorySeparatorChar))))).ToLowerInvariant();
        var applies = File.Exists(Path.Combine(directory, "physical-applies.log"))
            ? File.ReadAllLines(Path.Combine(directory, "physical-applies.log")) : [];
        var result = new
        {
            ReconcileRun = reconcileRun,
            ActiveRevisionId = active.RevisionId,
            GraphDigest = graph.GraphDigest,
            EffectIdentities = new { AId, BId, CId },
            EffectLineage = new[] { aAfter, bAfter, cAfter }.Select(effect => new
            {
                EffectId = effect.Intent.EffectId,
                effect.Intent.RevisionId,
                effect.Intent.IntentDigest,
                BaseStateDigest = effect.Intent.BaseState.StateDigest,
                ExpectedPostStateDigest = effect.Intent.ExpectedPostState.StateDigest,
                Stage = effect.Stage.ToString()
            }).ToArray(),
            PersistedAValidation = new
            {
                BeforeStage = aBefore.Stage.ToString(),
                BeforePassed = aBefore.Validation?.Passed,
                BeforeTargetStateDigest = aBefore.Validation?.TargetStateDigest,
                AfterStage = aAfter.Stage.ToString(),
                AfterPassed = aAfter.Validation?.Passed,
                AfterTargetStateDigest = aAfter.Validation?.TargetStateDigest
            },
            ObservedStateBefore = before,
            ObservedStateAfter = after,
            ActualProjectFileSha256 = actualProjectFileSha256,
            ObservedStateDiffersFromAExpected = before.StateDigest != aBefore.Intent.ExpectedPostState.StateDigest,
            BAttempt = new { bAttempt.Classification, Stage = bAttempt.Record.Stage.ToString(), bAttempt.ReasonCode, bAttempt.MutationPerformed, bAttempt.Record.Validation },
            CAttempt = new { cAttempt.Classification, Stage = cAttempt.Record.Stage.ToString(), cAttempt.ReasonCode, cAttempt.MutationPerformed, cAttempt.Record.Validation },
            BRemainedPrepared = bBefore.Stage == RepositoryEffectStage.Prepared && bAfter.Stage == RepositoryEffectStage.Prepared,
            CRemainedPrepared = cBefore.Stage == RepositoryEffectStage.Prepared && cAfter.Stage == RepositoryEffectStage.Prepared,
            NoBOrCMutation = bAttempt.MutationPerformed == false && cAttempt.MutationPerformed == false &&
                !applies.Contains("B", StringComparer.Ordinal) && !applies.Contains("C", StringComparer.Ordinal),
            PhysicalApplyLog = applies,
            AllowlistedProjectObservedAsStale = bView.PhysicalClassification == nameof(RepositoryEffectClassification.Unresolved) ||
                bView.PhysicalClassification == nameof(RepositoryEffectClassification.DefinitelyNotApplied),
            MutationBlocked = view.MutationBlocked,
            Blockers = view.Blockers,
            BBlockingPrerequisites = bView.BlockingPrerequisites,
            CBlockingPrerequisites = cView.BlockingPrerequisites,
            ValidatorCalled = validatorCalled,
            CheckpointCalled = checkpointCalled,
            SupervisorView = view
        };
        await File.WriteAllTextAsync(Path.Combine(directory, $"multi-effect-reconcile-{reconcileRun}.json"),
            JsonSerializer.Serialize(result, JsonOptions));
    }

    // A is an unaffected project-configuration effect. It enables a focused
    // custom-mode test while leaving independent canonical-64 conformance on.
    private static RepositoryPatchFile[] BuildPatchA(string checkout)
    {
        var path = Path.Combine(checkout, ProjectPath.Replace('/', Path.DirectorySeparatorChar));
        var before = File.ReadAllText(path);
        const string anchor = "  <PropertyGroup>";
        if (!before.Contains(anchor, StringComparison.Ordinal) || before.Contains("MarangM4CustomFingerprintValidation", StringComparison.Ordinal))
            throw new InvalidOperationException("M4 A project file no longer matches its bounded patch context.");
        var after = before.Replace(anchor,
            anchor + Environment.NewLine + "    <MarangM4CustomFingerprintValidation Condition=\"'$(MarangM4CustomFingerprintValidation)' == ''\">false</MarangM4CustomFingerprintValidation>" + Environment.NewLine +
            "    <DefineConstants Condition=\"'$(MarangM4CustomFingerprintValidation)' == 'true'\">$(DefineConstants);M4_CUSTOM_FINGERPRINT_VALIDATION</DefineConstants>", StringComparison.Ordinal);
        return [Patch(ProjectPath, before, after)];
    }

    private static RepositoryPatchFile[] BuildPatchB1(string checkout)
    {
        var sourcePath = Path.Combine(checkout, SourcePath.Replace('/', Path.DirectorySeparatorChar));
        var readmePath = Path.Combine(checkout, ReadmePath.Replace('/', Path.DirectorySeparatorChar));
        var source = File.ReadAllText(sourcePath);
        var readme = File.ReadAllText(readmePath);
        const string exact64 = "Require(value, parameterName, 64);";
        const string exactCondition = "value!.Length != 64";
        if (!source.Contains(exact64, StringComparison.Ordinal) || !source.Contains(exactCondition, StringComparison.Ordinal) || readme.Contains(Sha63Note, StringComparison.Ordinal))
            throw new InvalidOperationException("M4 B1 requires the clean canonical-64 source and README state.");
        return [Patch(SourcePath, source, source.Replace(exact64, "Require(value, parameterName, 63);", StringComparison.Ordinal)
                .Replace(exactCondition, "value!.Length != 63", StringComparison.Ordinal)),
            Patch(ReadmePath, readme, readme.TrimEnd() + Environment.NewLine + Environment.NewLine + Sha63Note + Environment.NewLine)];
    }

    private static RepositoryPatchFile[] BuildPatchC1(string checkout)
    {
        var path = Path.Combine(checkout, ContractTestsPath.Replace('/', Path.DirectorySeparatorChar));
        var before = File.ReadAllText(path);
        const string anchor = "    private static DelegationCallerScope Caller() => new(\"caller-1\");";
        if (!before.Contains(anchor, StringComparison.Ordinal) || before.Contains("M4_custom_fingerprint_mode", StringComparison.Ordinal))
            throw new InvalidOperationException("M4 C1 test file no longer matches its bounded patch context.");
        const string test = "#if M4_CUSTOM_FINGERPRINT_VALIDATION" + "\n" +
            "    [Fact]\n" +
            "    public void M4_custom_fingerprint_mode_accepts_the_candidate_63_character_length()\n" +
            "    {\n" +
            "        var create = () => WorkflowPlanRevisionReference.FuwenDefinition(\"m4-custom\", \"1\", new string('a', 63));\n" +
            "        create().CanonicalFingerprint.Should().HaveLength(63);\n" +
            "    }\n" +
            "#endif\n\n";
        return [Patch(ContractTestsPath, before, before.Replace(anchor, test + anchor, StringComparison.Ordinal))];
    }

    private static RepositoryPatchFile[] BuildPatchB2(string checkout)
    {
        var sourcePath = Path.Combine(checkout, SourcePath.Replace('/', Path.DirectorySeparatorChar));
        var readmePath = Path.Combine(checkout, ReadmePath.Replace('/', Path.DirectorySeparatorChar));
        var source = File.ReadAllText(sourcePath);
        var readme = File.ReadAllText(readmePath);
        if (!source.Contains("Require(value, parameterName, 63);", StringComparison.Ordinal) || !source.Contains("value!.Length != 63", StringComparison.Ordinal) || !readme.Contains(Sha63Note, StringComparison.Ordinal))
            throw new InvalidOperationException("M4 B2 must target the actual physical S3 state after B1 and C1.");
        return [Patch(SourcePath, source, source.Replace("Require(value, parameterName, 63);", "Require(value, parameterName, 64);", StringComparison.Ordinal)
                .Replace("value!.Length != 63", "value!.Length != 64", StringComparison.Ordinal)),
            Patch(ReadmePath, readme, readme.Replace(Sha63Note, Sha64Note, StringComparison.Ordinal))];
    }

    private static RepositoryPatchFile[] BuildPatchC2(string checkout)
    {
        var path = Path.Combine(checkout, ContractTestsPath.Replace('/', Path.DirectorySeparatorChar));
        var before = File.ReadAllText(path);
        const string wrong = "new string('a', 63));\n        create().CanonicalFingerprint.Should().HaveLength(63);";
        if (!before.Contains(wrong, StringComparison.Ordinal))
            throw new InvalidOperationException("M4 C2 must replace the previously applied 63-character test against actual S3.");
        return [Patch(ContractTestsPath, before, before.Replace(wrong,
            "new string('a', 64));\n        create().CanonicalFingerprint.Should().HaveLength(64);", StringComparison.Ordinal))];
    }

    private static RepositoryPatchFile Patch(string path, string before, string after) =>
        RepositoryPatchFile.Create(path, Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(after));

    private static async Task<RepositoryEffectGraph> BuildFirstGraphAsync(string checkout, ControlledRepository repository,
        SqliteRepositoryEffectJournal journal, RepositoryStateSnapshot physical)
    {
        var aIntent = await GetOrCreateIntentAsync(journal, repository, "revision-2", "A", AId, AExecutionId, physical, BuildPatchA(checkout));
        var a = RepositoryCausalEffect.Create(aIntent, [], "R2 A introduces opt-in focused custom fingerprint validation through the test project configuration.");
        var bIntent = await GetOrCreateIntentAsync(journal, repository, "revision-2", "B", BId, BExecutionId, aIntent.ExpectedPostState, BuildPatchB1(checkout));
        var b = RepositoryCausalEffect.Create(bIntent, [new(AId, a.CausalDigest, aIntent.ExpectedPostState.StateDigest)], "B depends on A's config mode and carries the initial 63-character implementation assumption.");
        var cIntent = await GetOrCreateIntentAsync(journal, repository, "revision-2", "C", CId, CExecutionId, bIntent.ExpectedPostState, BuildPatchC1(checkout));
        var c = RepositoryCausalEffect.Create(cIntent, [new(BId, b.CausalDigest, bIntent.ExpectedPostState.StateDigest)], "C encodes the B assumption in an actual focused package test; independent 64-character tests remain unchanged.");
        return RepositoryEffectGraph.Create(WorkflowId, "revision-2", "revision-1", repository.RepositoryId, physical,
            [new("A", a, false, "Add the custom validation mode used by the focused contract test."),
             new("B", b, false, "Implement the initial 63-character assumption."),
             new("C", c, false, "Test the selected 63-character behavior against B's exact result state.")]);
    }

    private static async Task<RepositoryEffectGraph> BuildCorrectiveGraphAsync(string checkout, ControlledRepository repository,
        SqliteRepositoryEffectJournal journal, RepositoryEffectGraph parent, RepositoryCausalEffect adoptedA,
        RepositoryStateSnapshot physical)
    {
        var a = adoptedA;
        var bIntent = await GetOrCreateIntentAsync(journal, repository, "revision-3", "B2", B2Id, B2ExecutionId, physical, BuildPatchB2(checkout));
        var b = RepositoryCausalEffect.Create(bIntent, [new(AId, a.CausalDigest, a.Intent.ExpectedPostState.StateDigest)], "B2 restores canonical 64-character validation while retaining A's compatible project configuration.");
        var cIntent = await GetOrCreateIntentAsync(journal, repository, "revision-3", "C2", C2Id, C2ExecutionId, bIntent.ExpectedPostState, BuildPatchC2(checkout));
        var c = RepositoryCausalEffect.Create(cIntent, [new(B2Id, b.CausalDigest, bIntent.ExpectedPostState.StateDigest)], "C2 updates the physically applied custom test to the canonical 64-character contract after B2.");
        var selections = new RepositoryEffectSelection[]
        {
            new("A", a, true, "Explicitly adopt the exact still-compatible project configuration effect."),
            new("B2", b, false, "Replace invalidated B against actual S3.") { ReplacesEffectId = BId },
            new("C2", c, false, $"Replace C because prerequisite B ({BId:D}) changed; base this patch on B2's S4 result.") { ReplacesEffectId = CId }
        };
        var draft = RepositoryEffectGraph.Create(WorkflowId, "revision-3", "revision-2", repository.RepositoryId, physical, selections);
        var priorEffects = RepositoryEffectGraphImpact.Analyze(parent, draft);
        return RepositoryEffectGraph.Create(WorkflowId, "revision-3", "revision-2", repository.RepositoryId, physical, selections, priorEffects);
    }

    private static async Task<RepositoryEffectIntent> GetOrCreateIntentAsync(SqliteRepositoryEffectJournal journal,
        ControlledRepository repository, string revision, string logical, Guid effectId, Guid executionId,
        RepositoryStateSnapshot basis, IEnumerable<RepositoryPatchFile> patch)
    {
        var existing = await journal.GetAsync(effectId);
        if (existing is not null) return existing.Intent;
        var intent = RepositoryEffectIntent.Create(effectId, WorkflowId, revision, logical, executionId, "attempt-1",
            repository.RepositoryId, "repository", basis, patch, DateTimeOffset.UtcNow);
        await journal.PrepareAsync(intent);
        return intent;
    }

    private static AdaptiveWorkSpec[] InitialSpecs(string provenance)
    {
        AdaptiveWorkSpec Spec(string id, string title, string requirement, string input, Guid executionId,
            string[]? dependencies = null, bool reuse = false) => new(id, title, requirement, input,
                AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "m4/qualified-checkout/v1", dependencies ?? [], executionId, reuse);
        return [Spec("inventory", "Qualified repository inventory", "Read the immutable source commit and record package/source identities.", "qualified-checkout:" + SourceCommit, InventoryExecutionId) with { Provenance = InventoryProvenance },
            Spec("plan", "M4 effect plan", "Plan project configuration, implementation and test changes.", provenance, InitialPlanExecutionId),
            Spec("final", "Inventory summary", "Report the pinned repository inventory before mutations begin.", provenance, Guid.Parse("75e0e04a-a0e2-4acb-9c7f-80ebdf2a4d4a"), ["inventory", "plan"])];
    }

    private static AdaptiveWorkSpec[] GraphSpecs(RepositoryEffectGraph graph, bool includeInventory, string revision)
    {
        AdaptiveWorkSpec Spec(string id, string title, string requirement, string input, Guid executionId,
            string[]? dependencies = null, bool effect = false, bool reuse = false) => new(id, title, requirement, input,
                AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile,
                "m4/effect-graph/v1", dependencies ?? [], executionId, reuse)
            { IsRepositoryEffect = effect };
        var list = new List<AdaptiveWorkSpec>();
        if (includeInventory) list.Add(Spec("inventory", "Qualified repository inventory", "Read the immutable source commit and record package/source identities.", "qualified-checkout:" + SourceCommit, InventoryExecutionId, reuse: true) with { Provenance = InventoryProvenance });
        list.Add(Spec("plan", "Causal effect graph", "Execute the revision's explicit effect dependency graph.", graph.GraphDigest,
            revision == "revision-2" ? Guid.Parse("ac3563b5-6b55-4ab4-9ce4-477eac0fe0a9") : Guid.Parse("5a60193c-e7c6-47c3-abec-2d211d794f9d")));
        foreach (var selection in graph.Effects)
        {
            var prerequisites = selection.Effect.Prerequisites.Select(d => graph.Effects.Single(x => x.Effect.Intent.EffectId == d.EffectId).LogicalId).ToArray();
            var requirement = selection.LogicalId == "A" ? "Add the custom validation mode used by the focused contract test." : selection.Rationale;
            list.Add(Spec(selection.LogicalId, "Repository effect " + selection.LogicalId, requirement,
                selection.Effect.CausalDigest, selection.Effect.Intent.ExecutionId, prerequisites, effect: true, reuse: selection.Reuse));
        }
        var finalId = revision == "revision-2" ? Final1ExecutionId : Final2ExecutionId;
        list.Add(Spec("final", "Graph validation summary", "Report accepted effects and full workflow validation for this revision.", graph.GraphDigest,
            finalId, [.. list.Select(s => s.LogicalId)]));
        return list.ToArray();
    }

    private static async Task RunRevisionAsync(WorkflowEngine engine, IWorkflowRepository store, AdaptiveRevision revision,
        bool allowFinal, CancellationToken cancellationToken = default)
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(10);
        foreach (var branch in revision.Branches.OrderBy(b => b.Dependencies.Length == 0 ? 0 : 1))
        {
            if (!allowFinal && branch.LogicalId == "final") continue;
            while (true)
            {
                var current = await store.GetRunAsync(branch.Execution.Id, cancellationToken);
                if (current?.Status == WorkflowStatus.Completed) break;
                if (current?.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled)
                    throw new InvalidOperationException("M4 branch failed: " + branch.LogicalId + "; " + current.Error?.Message);
                var ready = true;
                foreach (var dependency in branch.Dependencies)
                    ready &= (await store.GetRunAsync(revision.Branches.Single(b => b.LogicalId == dependency).Execution.Id, cancellationToken))?.Status == WorkflowStatus.Completed;
                if (!ready) break;
                var input = JsonSerializer.Deserialize<JsonElement>(branch.Execution.InputJson!);
                await engine.StartAsync(branch.Execution.WorkflowName, branch.Execution.WorkflowVersion, input, branch.Execution.Id);
                await engine.ExecuteAsync(branch.Execution.Id, cancellationToken);
                if (DateTimeOffset.UtcNow >= until) throw new TimeoutException("M4 workflow branch timed out: " + branch.LogicalId);
            }
        }
        foreach (var branch in revision.Branches.Where(b => allowFinal || b.LogicalId != "final"))
        {
            if ((await store.GetRunAsync(branch.Execution.Id, cancellationToken))?.Status != WorkflowStatus.Completed)
                throw new InvalidOperationException("M4 graph did not complete branch " + branch.LogicalId + ".");
        }
    }

    private static void RegisterCandidateFromRegistry(WorkflowRegistry registry, AdaptiveRevision[] history,
        AdaptiveRevision candidate, WorkflowRegistry candidateRegistry)
    {
        foreach (var branch in candidate.Branches)
        {
            if (history.SelectMany(r => r.Branches).Any(old => old.Execution.WorkflowName == branch.Execution.WorkflowName && old.Execution.WorkflowVersion == branch.Execution.WorkflowVersion)) continue;
            registry.Register(candidateRegistry.Get(branch.Execution.WorkflowName, branch.Execution.WorkflowVersion));
        }
    }

    private static async Task<RepositoryEffectValidation> LocalValidateAsync(string checkout, string directory,
        RepositoryEffectIntent intent, RepositoryStateSnapshot state, CancellationToken ct)
    {
        var restore = await RunCapturedAsync("dotnet", ["restore", "tests/Marang.Tests/Marang.Tests.csproj", "--configfile", "NuGet.AAAWM1.Config", "--ignore-failed-sources", "-m:1", "-p:BuildInParallel=false", "-p:UseSharedCompilation=false"], checkout, ct);
        if (restore.ExitCode != 0) return Validation(intent, state, "restore", false, restore.Output);
        string[] args = intent.LogicalStepId switch
        {
            "A" => ["build", "tests/Marang.Tests/Marang.Tests.csproj", "-c", "Release", "--no-restore", "-f", "net10.0", "-m:1", "-p:BuildInParallel=false", "-p:UseSharedCompilation=false"],
            "B" or "B2" => ["build", "src/Marang/Marang.csproj", "-c", "Release", "--no-restore", "-f", "net10.0", "-m:1", "-p:BuildInParallel=false", "-p:UseSharedCompilation=false"],
            "C" => ["test", "tests/Marang.Tests/Marang.Tests.csproj", "-c", "Release", "--no-restore", "-f", "net10.0", "-p:MarangM4CustomFingerprintValidation=true", "--filter", "FullyQualifiedName~M4_custom_fingerprint_mode_accepts_the_candidate_63_character_length", "-m:1", "-p:BuildInParallel=false", "-p:UseSharedCompilation=false"],
            "C2" => ["test", "tests/Marang.Tests/Marang.Tests.csproj", "-c", "Release", "--no-restore", "-f", "net10.0", "-p:MarangM4CustomFingerprintValidation=true", "--filter", "FullyQualifiedName~M4_custom_fingerprint_mode_accepts_the_candidate_64_character_length", "-m:1", "-p:BuildInParallel=false", "-p:UseSharedCompilation=false"],
            _ => throw new InvalidOperationException("Unknown local validation effect " + intent.LogicalStepId)
        };
        var result = await RunCapturedAsync("dotnet", args, checkout, ct);
        var passed = result.ExitCode == 0;
        await File.WriteAllTextAsync(Path.Combine(directory, "local-validation-" + intent.LogicalStepId + ".json"), JsonSerializer.Serialize(new
        { Target = state.StateDigest, Command = "dotnet " + string.Join(' ', args), result.ExitCode, result.Output }, JsonOptions), ct);
        return Validation(intent, state, "dotnet " + string.Join(' ', args), passed, result.Output);
    }

    private static RepositoryEffectValidation Validation(RepositoryEffectIntent intent, RepositoryStateSnapshot state,
        string command, bool passed, string output) => new(state.StateDigest, intent.LogicalStepId + " local validation",
            command, passed, Hash(output), Limit(output.Trim(), 1800));

    private static async ValueTask<RepositoryEffectValidation> WorkflowValidateAsync(string checkout, string directory,
        string? barrier, RepositoryEffectGraph graph, RepositoryStateSnapshot state, bool expectedPass,
        ControlledRepository repository, SqliteAdaptiveRevisionRepository revisions,
        SqliteRepositoryEffectGraphJournal graphJournal, SqliteRepositoryEffectJournal effects, CancellationToken ct)
    {
        var restore = await RunCapturedAsync("dotnet", ["restore", "tests/Marang.Tests/Marang.Tests.csproj", "--configfile", "NuGet.AAAWM1.Config", "--ignore-failed-sources", "-m:1", "-p:BuildInParallel=false", "-p:UseSharedCompilation=false"], checkout, ct);
        if (restore.ExitCode != 0) return new(state.StateDigest, "full package test suite", "dotnet restore", false, Hash(restore.Output), Limit(restore.Output, 1800));
        var arguments = new List<string> { "test", "tests/Marang.Tests/Marang.Tests.csproj", "-c", "Release", "--no-restore", "-f", "net10.0", "-m:1", "-p:BuildInParallel=false", "-p:UseSharedCompilation=false" };
        var holdValidation = graph.RevisionId == "revision-3" && barrier == "K12" && !File.Exists(Path.Combine(directory, "barrier-used-K12"));
        var nonce = Guid.NewGuid().ToString("N");
        var gateDirectory = Path.Combine(directory, "validation-gate-K12");
        if (holdValidation)
        {
            Directory.CreateDirectory(gateDirectory);
            arguments.Add("-p:CustomBeforeMicrosoftCommonTargets=" + Path.Combine(FindRepositoryRoot(), "tests", "Marang.Qualification", "ValidationGate.targets"));
            arguments.Add("-p:MarangM5GateDirectory=" + gateDirectory);
            arguments.Add("-p:MarangM5GateNonce=" + nonce);
        }
        var validationTask = RunCapturedAsync("dotnet", arguments.ToArray(), checkout, ct);
        if (holdValidation)
        {
            var processId = await ProveValidationGateAsync(gateDirectory, nonce, validationTask, ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "validation-process-K12.pid"), processId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
            await File.WriteAllTextAsync(Path.Combine(directory, "validation-context-K12.json"), JsonSerializer.Serialize(new
            {
                Barrier = "K12",
                Checkpoint = "RunningFinalWorkflowValidation",
                ValidationProcessRunning = true,
                ProcessId = processId,
                GraphDigest = graph.GraphDigest,
                StateDigest = state.StateDigest,
                Oracle = "nonce challenge answered by the actual dotnet test invocation before VSTest; release withheld",
                Nonce = nonce
            }, JsonOptions), ct);
            var context = await BuildBarrierContextAsync("K12", repository, revisions, graphJournal, effects,
                WorkflowId, graph, checkpoint: "RunningFinalWorkflowValidation", observedState: state,
                validationProcessId: processId, validationProcessRunning: true, cancellationToken: ct);
            await BarrierAsync(directory, barrier, "K12", ct, context);
        }
        var result = await validationTask;
        if (result.ExitCode == 0 && !expectedPass) throw new InvalidOperationException("R2 full validation unexpectedly passed despite the 63-character defect.");
        if (result.ExitCode != 0 && expectedPass) throw new InvalidOperationException("R3 final test suite failed: " + result.Output);
        var canonical64FailureObserved = !expectedPass && result.ExitCode != 0 &&
            result.Output.Contains("Marang.Tests.WorkflowPlanResolutionTests.", StringComparison.Ordinal) &&
            result.Output.Contains("Failed Marang.Tests.", StringComparison.Ordinal) &&
            !result.Output.Contains("DllNotFoundException", StringComparison.Ordinal);
        if (!expectedPass && !canonical64FailureObserved)
            throw new InvalidOperationException("R2 full validation failed without evidence of independent canonical 64-character conformance test failures: " + Limit(result.Output, 3000));
        await File.WriteAllTextAsync(Path.Combine(directory, graph.RevisionId + "-workflow-validation.json"), JsonSerializer.Serialize(new
        {
            graph.GraphDigest,
            TargetState = state.StateDigest,
            result.ExitCode,
            Passed = result.ExitCode == 0,
            Canonical64ConformanceFailureObserved = canonical64FailureObserved,
            result.Output
        }, JsonOptions), ct);
        return new(state.StateDigest, "full package test suite", "dotnet test tests/Marang.Tests/Marang.Tests.csproj -c Release --no-restore -f net10.0",
            result.ExitCode == 0, Hash(result.Output), Limit(result.Output.Trim(), 1800));
    }

    private static async ValueTask EffectCheckpointAsync(string directory, string? requested, RepositoryEffectIntent intent,
        RepositoryEffectExecutionCheckpoint checkpoint, ControlledRepository repository,
        SqliteAdaptiveRevisionRepository revisions, SqliteRepositoryEffectGraphJournal graphJournal,
        SqliteRepositoryEffectJournal effects, CancellationToken ct)
    {
        if (checkpoint.Kind == RepositoryEffectExecutionCheckpointKind.AfterPhysicalFile && checkpoint.FileIndex == 1)
            await File.AppendAllTextAsync(Path.Combine(directory, "physical-applies.log"), intent.LogicalStepId + "\n", ct);
        string? barrier = intent.LogicalStepId switch
        {
            "A" when checkpoint.Kind == RepositoryEffectExecutionCheckpointKind.AfterIntentCommit => "K1",
            "A" when checkpoint.Kind == RepositoryEffectExecutionCheckpointKind.AfterAllWrites => "K2",
            "A" when checkpoint.Kind == RepositoryEffectExecutionCheckpointKind.AfterValidation => "K3",
            "B" when checkpoint.Kind == RepositoryEffectExecutionCheckpointKind.AfterPhysicalFile && checkpoint.FileIndex == 1 => "K4",
            "B" when checkpoint.Kind == RepositoryEffectExecutionCheckpointKind.AfterValidation => "K5",
            "B2" when checkpoint.Kind == RepositoryEffectExecutionCheckpointKind.AfterValidation => "K10",
            _ => null
        };
        if (barrier is null) return;
        var active = await revisions.GetActiveAsync(WorkflowId, ct);
        var graph = AdaptivePlanAuthoring.ReadEffectGraph(active);
        var context = await BuildBarrierContextAsync(barrier, repository, revisions, graphJournal, effects, WorkflowId,
            graph, checkpoint: new
            {
                Kind = checkpoint.Kind.ToString(),
                checkpoint.FileIndex,
                intent.EffectId,
                intent.LogicalStepId,
                BaseStateDigest = intent.BaseState.StateDigest,
                ExpectedPostStateDigest = intent.ExpectedPostState.StateDigest
            },
            observedState: repository.ObserveUnderLock(), cancellationToken: ct);
        await BarrierAsync(directory, requested, barrier, ct, context);
    }

    private static async Task<object> BuildBarrierContextAsync(string barrier, ControlledRepository repository,
        SqliteAdaptiveRevisionRepository revisions, SqliteRepositoryEffectGraphJournal graphJournal,
        SqliteRepositoryEffectJournal effects, Guid workflowId, RepositoryEffectGraph? graph,
        AdaptiveProposal? proposal = null, object? checkpoint = null, RepositoryStateSnapshot? observedState = null,
        int? validationProcessId = null, bool? validationProcessRunning = null, CancellationToken cancellationToken = default)
    {
        var active = await revisions.GetActiveAsync(workflowId, cancellationToken);
        var pending = proposal ?? await revisions.GetPendingAsync(workflowId, cancellationToken);
        var records = await effects.ListForWorkflowAsync(workflowId, cancellationToken);
        var state = observedState ?? await repository.ObserveAsync(cancellationToken);
        var resolvedGraph = graph ?? AdaptivePlanAuthoring.ReadEffectGraph(active);
        var storedGraph = resolvedGraph is null ? null : await graphJournal.GetGraphAsync(workflowId, resolvedGraph.RevisionId, cancellationToken);
        if (resolvedGraph is not null && storedGraph?.GraphDigest != resolvedGraph.GraphDigest)
            throw new InvalidDataException("Barrier context graph is not the durable prepared graph.");
        return new
        {
            Barrier = barrier,
            ActiveRevisionId = active.RevisionId,
            GraphDigest = resolvedGraph?.GraphDigest,
            ObservedState = state,
            Effects = records.Select(record => new
            {
                record.Intent.EffectId,
                record.Intent.LogicalStepId,
                record.Intent.RevisionId,
                Stage = record.Stage.ToString(),
                Classification = record.Classification?.ToString(),
                ObservedStateDigest = record.ObservedState?.StateDigest,
                ValidationPassed = record.Validation?.Passed
            }).ToArray(),
            PendingProposalId = pending?.ProposalId,
            PendingProposalDigest = pending is null ? null : AdaptiveRevisionContracts.Digest(pending),
            Checkpoint = checkpoint,
            ValidationProcessId = validationProcessId,
            ValidationProcessRunning = validationProcessRunning
        };
    }

    private static async Task BarrierAsync(string directory, string? requested, string current,
        CancellationToken ct = default, object? context = null)
    {
        if (requested != current || File.Exists(Path.Combine(directory, "barrier-used-" + current))) return;
        var contextPath = Path.Combine(directory, "barrier-context-" + current + ".json");
        if (context is not null && !File.Exists(contextPath))
            await File.WriteAllTextAsync(contextPath, JsonSerializer.Serialize(context, JsonOptions), ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "barrier-ready-" + current), current, ct);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private static async Task CaptureIntentAsync(SupervisorCatchUp catchUp, SqliteSupervisorContinuityStore continuity,
        AdaptiveProposal proposal, SupervisorIntentTrigger trigger)
    {
        await catchUp.CaptureProposalContextAsync(proposal, Operator);
        await continuity.RecordIntentAsync(WorkflowId, proposal.Candidate.RevisionId, proposal.Candidate.RevisionId == "revision-2" ? FirstGoal : CorrectionGoal,
            trigger, proposal.Candidate.Reason, ["repository-effect-graph:" + proposal.Candidate.RevisionId, ProjectPath, SourcePath, ReadmePath, ContractTestsPath], Operator);
    }

    private static async Task ApproveAndCommitAsync(AdaptiveSupervisor supervisor, SqliteAdaptiveSupervisorAuthority authority,
        AdaptiveProposal proposal, string digest, string actor)
    {
        var receipt = await authority.FindApprovalAsync(proposal.ProposalId, digest);
        if (receipt is null) receipt = await supervisor.ApproveAsync(proposal, digest, actor);
        await supervisor.CommitAsync(proposal, digest, receipt);
    }

    private static async Task PersistProposalAsync(string directory, AdaptiveProposal proposal, string digest)
    {
        var path = Path.Combine(directory, "corrective-proposal.json");
        var json = JsonSerializer.Serialize(new
        {
            proposal.ProposalId,
            CandidateRevisionId = proposal.Candidate.RevisionId,
            ProposalDigest = digest,
            Status = "ApprovalRequired",
            CandidateGraph = AdaptivePlanAuthoring.ReadEffectGraph(proposal.Candidate)
        }, JsonOptions);
        if (File.Exists(path) && await File.ReadAllTextAsync(path) != json)
        {
            using var old = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            if (old.RootElement.GetProperty("proposalId").GetGuid() != proposal.ProposalId || old.RootElement.GetProperty("proposalDigest").GetString() != digest)
                throw new InvalidOperationException("Pending corrective graph proposal identity changed across restart.");
        }
        else await File.WriteAllTextAsync(path, json);
    }

    private static async Task PublishAuthority(SqliteAuthorityStore store, AuthorityStoreActor actor, string revision)
    {
        var context = new AuthenticatedAuthorityContext("m1", "supervisor", WorkflowId.ToString("D"), revision, "m1");
        var source = new AuthorityStoreSnapshotSource(store, actor);
        if ((await source.GetCurrentAsync(context))?.Context == context) return;
        var now = DateTimeOffset.UtcNow;
        var grants = new[]
        {
            new AuthorityGrant("revision-approval", [AuthorityAction.WriteFile], new AuthorityScope("repository", "reports/readiness.md", AuthorityScopeKind.Subtree), [], now.AddMinutes(-1), now.AddDays(7)),
            new AuthorityGrant("m4-tests-write", [AuthorityAction.WriteFile], new AuthorityScope("repository", "tests/Marang.Tests", AuthorityScopeKind.Subtree), [], now.AddMinutes(-1), now.AddDays(7)),
            new AuthorityGrant("m4-source-write", [AuthorityAction.WriteFile], new AuthorityScope("repository", "src/Marang", AuthorityScopeKind.Subtree), [], now.AddMinutes(-1), now.AddDays(7)),
            new AuthorityGrant("m4-readme-write", [AuthorityAction.WriteFile], new AuthorityScope("repository", "README.md", AuthorityScopeKind.Subtree), [], now.AddMinutes(-1), now.AddDays(7))
        };
        var number = int.Parse(revision.AsSpan("revision-".Length), System.Globalization.CultureInfo.InvariantCulture);
        var result = await store.PublishAsync(new AuthorityPublishCommand("m4-authority-" + revision, actor,
            new AuthoritySnapshot(context, $"m4-controlled-repository/{number}", [new AuthorityLayer("m4", grants)], [], now.AddDays(7)), number - 1));
        if (result.Status != AuthorityMutationStatus.Applied) throw new InvalidOperationException("Unable to publish revision authority: " + result.Status);
    }

    private static async Task<int> ProveValidationGateAsync(string directory, string nonce, Task<ProcessOutput> validation, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(4);
        async Task<string[]> ReadGateAsync(string name, int count)
        {
            var path = Path.Combine(directory, name);
            while (DateTimeOffset.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                if (validation.IsCompleted) throw new InvalidOperationException("Validation exited before the K12 liveness proof: " + (await validation).Output);
                if (File.Exists(path))
                {
                    var lines = await File.ReadAllLinesAsync(path, ct);
                    if (lines.Length == count && lines[1] == nonce) return lines;
                }
                await Task.Delay(25, ct);
            }
            throw new TimeoutException("Actual validation invocation did not answer the K12 challenge.");
        }
        var ready = await ReadGateAsync("ready.txt", 2);
        var challenge = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(directory, "challenge.txt"), challenge, ct);
        var proof = await ReadGateAsync("proof.txt", 3);
        if (proof[0] != ready[0] || proof[2] != challenge || !int.TryParse(proof[0], out var processId) || processId <= 0 || File.Exists(Path.Combine(directory, "release.txt")))
            throw new InvalidDataException("K12 validation liveness proof is inconsistent.");
        return processId;
    }

    private static async Task<ProcessOutput> RunCapturedAsync(string file, string[] args, string cwd, CancellationToken ct)
    {
        var pipe = Environment.GetEnvironmentVariable("MARANG_LIFECYCLE_PIPE")
            ?? throw new InvalidOperationException("M5 canonical qualification requires its Gagamba lifecycle owner.");
        var environment = new Dictionary<string, string>(LifecycleBrokerCommand.CaptureExecutionEnvironment())
        {
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1"
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        var result = await new LifecycleBrokerClient(pipe).RunCapturedAsync(new LifecycleBrokerCommand(
            LifecycleBrokerCommand.ResolveExecutable(file), args, cwd, environment), deadline.Token);
        if (!result.Accepted || result.Cause is not ("NaturalExit" or "NonzeroExit") || result.ExitCode is null)
            throw new InvalidOperationException($"Gagamba command did not complete naturally: {result.Cause}/{result.Cleanup}/{result.CaptureStatus}: {result.Failure}");
        return new(result.ExitCode.Value, result.CombinedOutput);
    }

    private static void EnsureCheckout(string checkout)
    {
        if (Directory.Exists(checkout) && File.Exists(Path.Combine(checkout, "Marang.slnx")))
        {
            if (Git(checkout, "rev-parse", "HEAD").Trim() != SourceCommit || Git(checkout, "branch", "--show-current").Trim() != "m4-multi-effect-qualification")
                throw new InvalidOperationException("Controlled M4 checkout is not pinned to the qualified M2 source commit.");
            return;
        }
        var source = FindRepositoryRoot();
        Directory.CreateDirectory(Path.GetDirectoryName(checkout)!);
        RunCapturedAsync("git", ["clone", "--no-hardlinks", "--no-checkout", source, checkout], source, CancellationToken.None).GetAwaiter().GetResult().EnsureSuccess();
        Git(checkout, "checkout", "-b", "m4-multi-effect-qualification", SourceCommit);
        var packageFeed = Path.Combine(source, "artifacts", "aaaw-m1", "packages");
        if (Directory.Exists(packageFeed)) CopyDirectory(packageFeed, Path.Combine(checkout, "artifacts", "aaaw-m1", "packages"));
        if (Git(checkout, "rev-parse", "HEAD").Trim() != SourceCommit) throw new InvalidOperationException("Unable to pin the M4 controlled checkout.");
    }

    private static string Git(string cwd, params string[] args) => RunCapturedAsync("git", args, cwd, CancellationToken.None).GetAwaiter().GetResult().EnsureSuccess().Output;
    private static string FindRepositoryRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "Marang.slnx"))) path = path.Parent;
        return path?.FullName ?? throw new DirectoryNotFoundException("Could not find the Marang repository root.");
    }
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
        }
    }

    private static int CountLines(string directory, string filename, string value)
    {
        var path = Path.Combine(directory, filename);
        return File.Exists(path) ? File.ReadAllLines(path).Count(line => line == value) : 0;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[^maximum..];

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
            command.CommandText = "CREATE TABLE IF NOT EXISTS m4_hufu_decisions (document TEXT NOT NULL); INSERT INTO m4_hufu_decisions(document) VALUES($d)";
            command.Parameters.AddWithValue("$d", JsonSerializer.Serialize(new { request.RequestIdentity, request.RelativePath, decision.Status, decision.SnapshotIdentity, decision.ReasonCode }));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await File.AppendAllTextAsync(Path.Combine(directory, "hufu-decisions.jsonl"), JsonSerializer.Serialize(new
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
            lock (executionByInput) foreach (var branch in branches) executionByInput[JsonSerializer.Deserialize<string>(branch.Execution.InputJson!)!] = branch.Execution.Id;
        }
        public void Configure(Func<JsonElement, CancellationToken, ValueTask<string>> callback) => executeEffect = callback;
        public async ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default)
        {
            var input = ((JsonRuntimeValue)request.Arguments.Single().Value).Value.GetString()!;
            using var facts = JsonDocument.Parse(input); var item = facts.RootElement; var logical = item.GetProperty("LogicalId").GetString()!;
            if (request.Activity.Name == "aaaw.repository-causal-effect")
            {
                var result = await (executeEffect ?? throw new InvalidOperationException("Graph effect has no configured host runtime."))(item, cancellationToken);
                return ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(JsonSerializer.SerializeToElement(result)));
            }
            var executionId = GetExecutionId(input);
            string output;
            if (logical == "inventory")
            {
                var readme = Git(checkout, "show", SourceCommit + ":" + ReadmePath);
                var project = Git(checkout, "show", SourceCommit + ":" + ProjectPath);
                var source = Git(checkout, "show", SourceCommit + ":" + SourcePath);
                output = JsonSerializer.Serialize(new { RepositoryId = repository.RepositoryId, ReadmeSha256 = Hash(readme), ProjectSha256 = Hash(project), SourceSha256 = Hash(source), SourceCommit });
                await File.WriteAllTextAsync(Path.Combine(directory, executionId.ToString("N") + ".inventory.json"), output, cancellationToken);
                await File.AppendAllTextAsync(Path.Combine(directory, "inventory-invocations.log"), executionId.ToString("D") + "\n", cancellationToken);
            }
            else output = item.GetProperty("Requirement").GetString()!;
            return ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(JsonSerializer.SerializeToElement(output)));
        }
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        private Guid GetExecutionId(string input) { lock (executionByInput) return executionByInput.TryGetValue(input, out var id) ? id : throw new InvalidOperationException("Activity input is not bound to an admitted execution."); }
    }
}
