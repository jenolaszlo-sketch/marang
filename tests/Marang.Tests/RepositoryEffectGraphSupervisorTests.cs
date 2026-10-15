using FluentAssertions;
using Marang.Runs.Adaptive;
using Marang.Runs.Effects;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Marang.Tests;

public sealed class RepositoryEffectGraphSupervisorTests
{
    [Fact]
    public async Task Unauthorized_graph_read_drilldown_and_effect_explanation_stop_before_owner_or_checkout_access()
    {
        using var database = new TempDb();
        var ownerDatabase = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = database.DatabasePath, Pooling = false });
        var catchUp = new SupervisorCatchUp(ownerDatabase, _ => false);
        await catchUp.InitializeAsync(TestContext.Current.CancellationToken);
        // The remaining dependencies deliberately have no initialized state: authorization must happen first.
        var projection = new RepositoryEffectGraphSupervisor(null!, null!, null!, null!, catchUp);

        var read = () => projection.ReadAsync(Guid.NewGuid(), "foreign", TestContext.Current.CancellationToken);
        var drillDown = () => projection.DrillDownAsync(Guid.NewGuid(), "r1", "foreign", TestContext.Current.CancellationToken);
        var explain = () => projection.ExplainEffectAsync(Guid.NewGuid(), Guid.NewGuid(), "foreign", TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<WorkflowAuthorizationException>();
        await drillDown.Should().ThrowAsync<WorkflowAuthorizationException>();
        await explain.Should().ThrowAsync<WorkflowAuthorizationException>();
    }

    [Fact]
    public async Task Catch_up_separates_active_acceptance_physical_state_and_local_validation_from_workflow_validation()
    {
        using var fixture = await Fixture.CreateAsync();
        var a = fixture.ActiveGraph.Effects.Single(x => x.LogicalId == "effect-a").Effect.Intent;
        await fixture.ApplyAndRecordAsync(a, partial: false, validationPassed: false);

        var view = await fixture.Projection.ReadAsync(fixture.WorkflowId, "operator", TestContext.Current.CancellationToken);
        view.Effects.Should().HaveCount(2);
        var first = view.Effects.Single(e => e.LogicalId == "effect-a");
        first.SelectedInActiveRevision.Should().BeTrue();
        first.AcceptedByActiveRevision.Should().BeFalse();
        first.PhysicalClassification.Should().Be(nameof(RepositoryEffectClassification.DefinitelyApplied));
        first.LocalValidation.Should().NotBeNull(); first.LocalValidation!.Passed.Should().BeFalse();
        first.WorkflowValidation.Should().BeNull();
        first.RecordedOldStateDigest.Should().Be(a.ExpectedPostState.StateDigest);
        first.ActualCheckoutStateDigest.Should().Be(view.ActualCheckoutState.StateDigest);

        var second = view.Effects.Single(e => e.LogicalId == "effect-b");
        second.SelectedInActiveRevision.Should().BeTrue();
        second.AcceptedByActiveRevision.Should().BeFalse();
        second.AcceptedPrerequisites.Should().BeEmpty();
        second.BlockingPrerequisites.Should().ContainSingle().Which.Should().Be(a.EffectId);
        second.PhysicalClassification.Should().Be(nameof(RepositoryEffectClassification.DefinitelyNotApplied));
        view.Blockers.Should().Contain(b => b.Contains("effect-b", StringComparison.Ordinal) && b.Contains("effect-a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Partial_prerequisite_names_blocked_downstream_effect_and_never_grants_replay_permission()
    {
        using var fixture = await Fixture.CreateAsync();
        var a = fixture.ActiveGraph.Effects.Single(x => x.LogicalId == "effect-a").Effect.Intent;
        await fixture.ApplyAndRecordAsync(a, partial: true, validationPassed: null);

        var view = await fixture.Projection.ReadAsync(fixture.WorkflowId, "operator", TestContext.Current.CancellationToken);
        view.MutationBlocked.Should().BeTrue();
        view.Effects.Single(e => e.LogicalId == "effect-a").PhysicalClassification
            .Should().Be(nameof(RepositoryEffectClassification.PartiallyApplied));
        view.Effects.Single(e => e.LogicalId == "effect-a").SelectedInActiveRevision.Should().BeTrue();
        view.Effects.Single(e => e.LogicalId == "effect-a").AcceptedByActiveRevision.Should().BeFalse();
        var downstream = view.Effects.Single(e => e.LogicalId == "effect-b");
        downstream.SelectedInActiveRevision.Should().BeTrue();
        downstream.AcceptedByActiveRevision.Should().BeFalse();
        downstream.BlockingPrerequisites.Should().ContainSingle().Which.Should().Be(a.EffectId);
        downstream.Explanation.Should().Contain("downstream work is blocked");
        view.Blockers.Should().Contain(b => b.Contains("effect-b", StringComparison.Ordinal) && b.Contains("effect-a", StringComparison.Ordinal));
        view.Effects.Should().OnlyContain(e => !e.Explanation.Contains("replay permitted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Applied_but_unvalidated_prerequisite_is_named_as_a_blocker()
    {
        using var fixture = await Fixture.CreateAsync();
        var a = fixture.ActiveGraph.Effects.Single(x => x.LogicalId == "effect-a").Effect.Intent;
        await fixture.ApplyAndObserveWithoutValidationAsync(a);

        var view = await fixture.Projection.ReadAsync(fixture.WorkflowId, "operator", TestContext.Current.CancellationToken);
        var downstream = view.Effects.Single(e => e.LogicalId == "effect-b");
        downstream.AcceptedByActiveRevision.Should().BeFalse();
        downstream.BlockingPrerequisites.Should().ContainSingle().Which.Should().Be(a.EffectId);
        view.Blockers.Should().Contain(b => b.Contains("effect-b", StringComparison.Ordinal) && b.Contains("effect-a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Prepared_unapproved_graph_does_not_supersede_active_projection()
    {
        using var fixture = await Fixture.CreateAsync();
        var pending = await fixture.PrepareUncommittedReplacementGraphAsync();

        var view = await fixture.Projection.ReadAsync(fixture.WorkflowId, "operator", TestContext.Current.CancellationToken);
        view.InspectedRevisionId.Should().Be(fixture.ActiveGraph.RevisionId);
        view.Effects.Should().NotContain(e => e.EffectId == pending.Effects.Single(s => !s.Reuse).Effect.Intent.EffectId);
        var activeB = view.Effects.Single(e => e.LogicalId == "effect-b");
        activeB.SelectedInActiveRevision.Should().BeTrue();
        activeB.ReplacementEffectId.Should().BeNull();
        activeB.Dispositions.Should().NotContain(RepositoryEffectDispositionKind.Superseded);
    }

    [Fact]
    public async Task Historical_applied_effect_remains_visible_as_replaced_while_active_reuse_and_replacement_are_accepted()
    {
        using var fixture = await Fixture.CreateAsync();
        var oldA = fixture.ActiveGraph.Effects.Single(e => e.LogicalId == "effect-a");
        var oldB = fixture.ActiveGraph.Effects.Single(e => e.LogicalId == "effect-b");
        await fixture.ApplyAndRecordAsync(oldA.Effect.Intent, partial: false, validationPassed: true);
        await fixture.ApplyAndRecordAsync(oldB.Effect.Intent, partial: false, validationPassed: true);
        var replacementGraph = await fixture.CommitReplacementAsync(oldA);
        var replacement = replacementGraph.Effects.Single(e => e.LogicalId == "effect-b");
        await fixture.ApplyAndRecordAsync(replacement.Effect.Intent, partial: false, validationPassed: true);

        var current = await fixture.Projection.ReadAsync(fixture.WorkflowId, "operator", TestContext.Current.CancellationToken);
        current.MutationBlocked.Should().BeFalse();
        var acceptedReuse = current.Effects.Single(e => e.EffectId == oldA.Effect.Intent.EffectId);
        acceptedReuse.SelectedInActiveRevision.Should().BeTrue();
        acceptedReuse.AcceptedByActiveRevision.Should().BeTrue();
        acceptedReuse.Reuse.Should().BeTrue();
        var oldApplied = current.Effects.Single(e => e.EffectId == oldB.Effect.Intent.EffectId);
        oldApplied.SelectedInActiveRevision.Should().BeFalse();
        oldApplied.AcceptedByActiveRevision.Should().BeFalse();
        oldApplied.PhysicalClassification.Should().Be("AppliedThenAdvanced");
        oldApplied.ReplacementEffectId.Should().Be(replacement.Effect.Intent.EffectId);
        oldApplied.Dispositions.Should().Contain(RepositoryEffectDispositionKind.Invalidated);
        oldApplied.RecordedOldStateDigest.Should().Be(oldB.Effect.Intent.ExpectedPostState.StateDigest);
        current.Effects.Single(e => e.EffectId == replacement.Effect.Intent.EffectId).AcceptedByActiveRevision.Should().BeTrue();

        var historical = await fixture.Projection.DrillDownAsync(fixture.WorkflowId, "r2", "operator", TestContext.Current.CancellationToken);
        var historicalB = historical.Graph.Effects.Single(e => e.EffectId == oldB.Effect.Intent.EffectId);
        historicalB.SelectedInInspectedRevision.Should().BeTrue();
        historicalB.SelectedInActiveRevision.Should().BeFalse();
        historicalB.ReplacementEffectId.Should().Be(replacement.Effect.Intent.EffectId);
    }

    [Fact]
    public async Task Previously_applied_effect_is_reported_as_historical_when_checkout_no_longer_matches_it()
    {
        using var fixture = await Fixture.CreateAsync();
        var intent = fixture.ActiveGraph.Effects.Single(e => e.LogicalId == "effect-a").Effect.Intent;
        await fixture.ApplyAndRecordAsync(intent, partial: false, validationPassed: true);
        await fixture.RestoreInitialContentsAsync();

        var view = await fixture.Projection.ReadAsync(fixture.WorkflowId, "operator", TestContext.Current.CancellationToken);
        var effect = view.Effects.Single(e => e.EffectId == intent.EffectId);
        effect.PhysicalClassification.Should().Be("AppliedHistorical");
        effect.RecordedOldStateDigest.Should().Be(intent.ExpectedPostState.StateDigest);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "marang-graph-catchup-" + Guid.NewGuid().ToString("N"));
        private readonly string checkoutRoot = Path.Combine(Path.GetTempPath(), "marang-graph-checkout-" + Guid.NewGuid().ToString("N"));
        private readonly Ports ports = new();
        private SqliteWorkflowStore workflowStore = null!;
        private WorkflowRegistry registry = null!;
        public Guid WorkflowId { get; } = Guid.NewGuid();
        public string DatabasePath => Path.Combine(directory, "execution.db");
        public SqliteDatabase Database { get; private set; } = null!;
        public SqliteAdaptiveRevisionRepository Revisions { get; private set; } = null!;
        public SqliteRepositoryEffectJournal EffectJournal { get; private set; } = null!;
        public SqliteRepositoryEffectGraphJournal GraphJournal { get; private set; } = null!;
        public ControlledRepository Repository { get; private set; } = null!;
        public RepositoryEffectGraph ActiveGraph { get; private set; } = null!;
        public RepositoryEffectGraphSupervisor Projection { get; private set; } = null!;

        private Fixture() { }

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(); Directory.CreateDirectory(f.directory); Directory.CreateDirectory(Path.Combine(f.checkoutRoot, "src"));
            File.WriteAllText(Path.Combine(f.checkoutRoot, "src", "a.txt"), "a-before");
            File.WriteAllText(Path.Combine(f.checkoutRoot, "src", "b.txt"), "b-before");
            File.WriteAllText(Path.Combine(f.checkoutRoot, "src", "c.txt"), "c-before");
            RunGit(f.checkoutRoot, "init", "-q"); RunGit(f.checkoutRoot, "config", "user.email", "test@example.invalid");
            RunGit(f.checkoutRoot, "config", "user.name", "Marang Tests"); RunGit(f.checkoutRoot, "add", "src"); RunGit(f.checkoutRoot, "commit", "-qm", "initial");
            f.Database = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = Path.GetFullPath(f.DatabasePath), Pooling = false });
            f.Revisions = new(f.Database); await f.Revisions.InitializeAsync(TestContext.Current.CancellationToken);
            f.EffectJournal = new(f.Database); await f.EffectJournal.InitializeAsync(TestContext.Current.CancellationToken);
            f.GraphJournal = new(f.Database, f.EffectJournal); await f.GraphJournal.InitializeAsync(TestContext.Current.CancellationToken);
            await new SqliteDelegationRecoveryJournal(f.Database).InitializeAsync(TestContext.Current.CancellationToken);
            f.Repository = ControlledRepository.Open(f.checkoutRoot, ["src/a.txt", "src/b.txt", "src/c.txt"]);
            var initial = await AdaptivePlanAuthoring.AuthorAsync(f.WorkflowId, "r1", null, "inspect controlled repository", "initial", "test",
                [new("inventory", "Inventory", "inspect files", "input", "read", "source", [], Guid.NewGuid(), true),
                 new("analysis", "Analysis", "analyze files", "input", "read", "source", [], Guid.NewGuid(), true),
                 new("report", "Report", "summarize", "input", "read", "source", ["inventory", "analysis"], Guid.NewGuid(), true)],
                new FuwenZhinuExecutionPorts(f.ports, f.ports, f.ports), cancellationToken: TestContext.Current.CancellationToken);
            f.registry = initial.Registry;
            await f.Revisions.ActivateInitialAsync(initial.Revision, TestContext.Current.CancellationToken);
            var continuity = new SqliteSupervisorContinuityStore(f.Database, f.Revisions, actor => actor == "operator");
            await continuity.InitializeAsync(TestContext.Current.CancellationToken);
            await continuity.RecordIntentAsync(f.WorkflowId, "r1", "inspect controlled repository", SupervisorIntentTrigger.Initial,
                "baseline", [], "operator", TestContext.Current.CancellationToken);
            f.workflowStore = new(f.Database);
            await f.StartRunsAsync(initial.Revision, initial.Registry);

            var physicalBase = await f.Repository.ObserveAsync(TestContext.Current.CancellationToken);
            var aIntent = MakeIntent(f.WorkflowId, "r2", "effect-a", f.Repository.RepositoryId, physicalBase,
                [("src/a.txt", "a-before", "a-after"), ("src/b.txt", "b-before", "b-after")]);
            var aCausal = RepositoryCausalEffect.Create(aIntent, [], "first required mutation");
            var bIntent = MakeIntent(f.WorkflowId, "r2", "effect-b", f.Repository.RepositoryId, aIntent.ExpectedPostState,
                [("src/c.txt", "c-before", "c-after")]);
            var bCausal = RepositoryCausalEffect.Create(bIntent,
                [new(aIntent.EffectId, aCausal.CausalDigest, aIntent.ExpectedPostState.StateDigest)], "mutation after first result");
            f.ActiveGraph = RepositoryEffectGraph.Create(f.WorkflowId, "r2", "r1", f.Repository.RepositoryId, physicalBase,
                [new("effect-a", aCausal, false, "new first effect"), new("effect-b", bCausal, false, "dependent effect")]);
            await f.GraphJournal.PrepareGraphAsync(f.ActiveGraph, TestContext.Current.CancellationToken);
            var specs = new[] {
                new AdaptiveWorkSpec("effect-a", "First effect", "first required mutation", aCausal.CausalDigest,
                    AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "causal-graph", [], aIntent.ExecutionId, false) { IsRepositoryEffect = true },
                new AdaptiveWorkSpec("effect-b", "Dependent effect", "mutation after first result", bCausal.CausalDigest,
                    AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "causal-graph", ["effect-a"], bIntent.ExecutionId, false) { IsRepositoryEffect = true }
            };
            var candidate = await AdaptivePlanAuthoring.AuthorEffectGraphAsync(f.WorkflowId, "r2", "r1", "inspect and update files", "approved graph", "test",
                specs, f.ActiveGraph, new FuwenZhinuExecutionPorts(f.ports, f.ports, f.ports), f.registry, TestContext.Current.CancellationToken);
            f.registry = candidate.Registry;
            var active = await f.Revisions.GetActiveAsync(f.WorkflowId, TestContext.Current.CancellationToken);
            var proposal = new AdaptiveProposal(Guid.NewGuid(), candidate.Revision,
                await f.Revisions.GetStateDigestAsync(f.WorkflowId, TestContext.Current.CancellationToken),
                AdaptiveRevisionContracts.Compare(active, candidate.Revision), AdaptiveAuthorityDecision.ApprovalRequired,
                "test", "exact graph approval");
            var digest = AdaptiveRevisionContracts.Digest(proposal);
            await f.Revisions.ProposeAsync(proposal, TestContext.Current.CancellationToken);
            await f.Revisions.ApproveAsync(proposal.ProposalId, digest, "operator", "test-approval", DateTimeOffset.UtcNow.AddHours(1), TestContext.Current.CancellationToken);
            await f.Revisions.CommitAsync(proposal.ProposalId, digest, TestContext.Current.CancellationToken);
            await f.StartRunsAsync(candidate.Revision, candidate.Registry);
            var catchUp = new SupervisorCatchUp(f.Database, actor => actor == "operator"); await catchUp.InitializeAsync(TestContext.Current.CancellationToken);
            f.Projection = new(f.Revisions, f.GraphJournal, f.EffectJournal, f.Repository, catchUp);
            return f;
        }

        public async Task ApplyAndRecordAsync(RepositoryEffectIntent intent, bool partial, bool? validationPassed)
        {
            await EffectJournal.TransitionAsync(intent.EffectId, RepositoryEffectStage.IntentCommitted, cancellationToken: TestContext.Current.CancellationToken);
            await EffectJournal.TransitionAsync(intent.EffectId, RepositoryEffectStage.Applying, cancellationToken: TestContext.Current.CancellationToken);
            var aFile = intent.Files[0];
            File.WriteAllBytes(Path.Combine(checkoutRoot, aFile.Path.Replace('/', Path.DirectorySeparatorChar)), aFile.AfterBytes.ToArray());
            if (!partial)
                foreach (var file in intent.Files.Skip(1))
                    File.WriteAllBytes(Path.Combine(checkoutRoot, file.Path.Replace('/', Path.DirectorySeparatorChar)), file.AfterBytes.ToArray());
            var observed = await Repository.ObserveAsync(TestContext.Current.CancellationToken);
            if (partial)
                await EffectJournal.TransitionAsync(intent.EffectId, RepositoryEffectStage.Blocked, RepositoryEffectClassification.PartiallyApplied,
                    observed, cancellationToken: TestContext.Current.CancellationToken);
            else
            {
                await EffectJournal.TransitionAsync(intent.EffectId, RepositoryEffectStage.AppliedObserved,
                    RepositoryEffectClassification.DefinitelyApplied, observed, cancellationToken: TestContext.Current.CancellationToken);
                var validation = new RepositoryEffectValidation(observed.StateDigest, "local", "local-validation",
                    validationPassed!.Value, RepositoryStateSnapshot.Hash("local-output"), "local validation evidence");
                await EffectJournal.TransitionAsync(intent.EffectId, validation.Passed ? RepositoryEffectStage.ValidationPassed : RepositoryEffectStage.ValidationFailed,
                    RepositoryEffectClassification.DefinitelyApplied, observed, validation, TestContext.Current.CancellationToken);
            }
        }

        public async Task ApplyAndObserveWithoutValidationAsync(RepositoryEffectIntent intent)
        {
            await EffectJournal.TransitionAsync(intent.EffectId, RepositoryEffectStage.IntentCommitted, cancellationToken: TestContext.Current.CancellationToken);
            await EffectJournal.TransitionAsync(intent.EffectId, RepositoryEffectStage.Applying, cancellationToken: TestContext.Current.CancellationToken);
            foreach (var file in intent.Files)
                File.WriteAllBytes(Path.Combine(checkoutRoot, file.Path.Replace('/', Path.DirectorySeparatorChar)), file.AfterBytes.ToArray());
            var observed = await Repository.ObserveAsync(TestContext.Current.CancellationToken);
            await EffectJournal.TransitionAsync(intent.EffectId, RepositoryEffectStage.AppliedObserved,
                RepositoryEffectClassification.DefinitelyApplied, observed, cancellationToken: TestContext.Current.CancellationToken);
        }

        public async Task RestoreInitialContentsAsync()
        {
            File.WriteAllText(Path.Combine(checkoutRoot, "src", "a.txt"), "a-before");
            File.WriteAllText(Path.Combine(checkoutRoot, "src", "b.txt"), "b-before");
            _ = await Repository.ObserveAsync(TestContext.Current.CancellationToken);
        }

        public async Task<RepositoryEffectGraph> CommitReplacementAsync(RepositoryEffectSelection oldA)
        {
            var baseState = await Repository.ObserveAsync(TestContext.Current.CancellationToken);
            var replacementIntent = MakeIntent(WorkflowId, "r3", "effect-b", Repository.RepositoryId, baseState,
                [("src/c.txt", "c-after", "c-final")]);
            var replacementCausal = RepositoryCausalEffect.Create(replacementIntent,
                [new(oldA.Effect.Intent.EffectId, oldA.Effect.CausalDigest, oldA.Effect.Intent.ExpectedPostState.StateDigest)],
                "replace downstream output after preserving the accepted first effect");
            var selections = new[] {
                oldA with { Reuse = true, Rationale = "exact prior effect accepted as a base" },
                new RepositoryEffectSelection("effect-b", replacementCausal, false, "new downstream corrective effect")
            };
            var provisional = RepositoryEffectGraph.Create(WorkflowId, "r3", "r2", Repository.RepositoryId, baseState, selections);
            var prior = RepositoryEffectGraphImpact.Analyze(ActiveGraph, provisional);
            var graph = RepositoryEffectGraph.Create(WorkflowId, "r3", "r2", Repository.RepositoryId, baseState, selections, prior);
            await GraphJournal.PrepareGraphAsync(graph, TestContext.Current.CancellationToken);
            var specs = new[] {
                new AdaptiveWorkSpec("effect-a", "First effect", "first required mutation", oldA.Effect.CausalDigest,
                    AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "causal-graph", [], oldA.Effect.Intent.ExecutionId, true) { IsRepositoryEffect = true },
                new AdaptiveWorkSpec("effect-b", "Replacement effect", "replace downstream output", replacementCausal.CausalDigest,
                    AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "causal-graph", ["effect-a"], replacementIntent.ExecutionId, false) { IsRepositoryEffect = true }
            };
            var candidate = await AdaptivePlanAuthoring.AuthorEffectGraphAsync(WorkflowId, "r3", "r2", "inspect and correct files",
                "replace invalidated downstream effect", "test", specs, graph,
                new FuwenZhinuExecutionPorts(ports, ports, ports), registry, TestContext.Current.CancellationToken);
            registry = candidate.Registry;
            await CommitAsync(candidate.Revision);
            ActiveGraph = graph;
            return graph;
        }

        public async Task<RepositoryEffectGraph> PrepareUncommittedReplacementGraphAsync()
        {
            var oldA = ActiveGraph.Effects.Single(e => e.LogicalId == "effect-a");
            var baseState = await Repository.ObserveAsync(TestContext.Current.CancellationToken);
            var intent = MakeIntent(WorkflowId, "r3", "effect-b", Repository.RepositoryId, baseState,
                [("src/c.txt", "c-before", "c-pending")]);
            var causal = RepositoryCausalEffect.Create(intent,
                [new(oldA.Effect.Intent.EffectId, oldA.Effect.CausalDigest, oldA.Effect.Intent.ExpectedPostState.StateDigest)],
                "pending unapproved downstream replacement");
            var selections = new[] {
                oldA with { Reuse = true, Rationale = "retain accepted first effect" },
                new RepositoryEffectSelection("effect-b", causal, false, "pending replacement",
                    ActiveGraph.Effects.Single(e => e.LogicalId == "effect-b").Effect.Intent.EffectId)
            };
            var provisional = RepositoryEffectGraph.Create(WorkflowId, "r3", ActiveGraph.RevisionId,
                Repository.RepositoryId, baseState, selections);
            var prior = RepositoryEffectGraphImpact.Analyze(ActiveGraph, provisional);
            var graph = RepositoryEffectGraph.Create(WorkflowId, "r3", ActiveGraph.RevisionId,
                Repository.RepositoryId, baseState, selections, prior);
            await GraphJournal.PrepareGraphAsync(graph, TestContext.Current.CancellationToken);
            return graph;
        }

        private async Task CommitAsync(AdaptiveRevision candidate)
        {
            var active = await Revisions.GetActiveAsync(WorkflowId, TestContext.Current.CancellationToken);
            var proposal = new AdaptiveProposal(Guid.NewGuid(), candidate,
                await Revisions.GetStateDigestAsync(WorkflowId, TestContext.Current.CancellationToken),
                AdaptiveRevisionContracts.Compare(active, candidate), AdaptiveAuthorityDecision.ApprovalRequired,
                "test", "exact graph approval");
            var digest = AdaptiveRevisionContracts.Digest(proposal);
            await Revisions.ProposeAsync(proposal, TestContext.Current.CancellationToken);
            await Revisions.ApproveAsync(proposal.ProposalId, digest, "operator", "test-approval", DateTimeOffset.UtcNow.AddHours(1), TestContext.Current.CancellationToken);
            await Revisions.CommitAsync(proposal.ProposalId, digest, TestContext.Current.CancellationToken);
            await StartRunsAsync(candidate, registry);
        }

        private async Task StartRunsAsync(AdaptiveRevision revision, WorkflowRegistry definitions)
        {
            await using var engine = new WorkflowEngine(workflowStore, definitions);
            foreach (var branch in revision.Branches)
                if (await workflowStore.GetRunAsync(branch.Execution.Id, TestContext.Current.CancellationToken) is null)
                    await engine.StartAsync(branch.Execution.WorkflowName, branch.Execution.WorkflowVersion,
                        JsonSerializer.Deserialize<JsonElement>(branch.Execution.InputJson!), branch.Execution.Id,
                        cancellationToken: TestContext.Current.CancellationToken);
        }

        private static RepositoryEffectIntent MakeIntent(Guid workflow, string revision, string logical, string repo,
            RepositoryStateSnapshot basis, (string Path, string Before, string After)[] files) =>
            RepositoryEffectIntent.Create(Guid.NewGuid(), workflow, revision, logical, Guid.NewGuid(), "attempt-1", repo, "workspace", basis,
                files.Select(f => RepositoryPatchFile.Create(f.Path, Encoding.UTF8.GetBytes(f.Before), Encoding.UTF8.GetBytes(f.After))), DateTimeOffset.UtcNow);

        private static void RunGit(string root, params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException(output + error);
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteTree(checkoutRoot);
            DeleteTree(directory);
        }

        private static void DeleteTree(string path)
        {
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                File.SetAttributes(dir, FileAttributes.Directory);
            File.SetAttributes(path, FileAttributes.Directory);
            Directory.Delete(path, true);
        }
    }

    private sealed class Ports : IActivityExecutor, IContextProvider, IInferenceExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(JsonSerializer.SerializeToElement("ok"))));
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TempDb : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "marang-graph-supervisor-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath => System.IO.Path.Combine(directory, "execution.db");
        public TempDb() => Directory.CreateDirectory(directory);
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
