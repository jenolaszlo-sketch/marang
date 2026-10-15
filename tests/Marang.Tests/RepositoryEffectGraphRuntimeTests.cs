using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Marang.Hufu;
using Marang.Runs.Adaptive;
using Marang.Runs.Effects;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Hufu;
using Penghou.Hufu.Cedar;
using Penghou.Hufu.Sqlite;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Tests;

public sealed class RepositoryEffectGraphRuntimeTests
{
    [Fact]
    public async Task Downstream_waits_for_local_validation_and_exact_base_before_any_write()
    {
        using var f = await Fixture.CreateAsync();
        var context = f.Context;
        var b = f.Graph.Effects.Single(e => e.LogicalId == "B").Effect.Intent;

        var premature = await f.Runtime.ExecuteAsync(b.EffectId, context, cancellationToken: f.Ct);
        premature.MutationPerformed.Should().BeFalse();
        premature.ReasonCode.Should().Contain("prerequisite-A-not-accepted-validated");
        (await f.Effects.GetAsync(b.EffectId, f.Ct))!.Stage.Should().Be(RepositoryEffectStage.Prepared);
        (await f.Runtime.IsAdmittedAsync(b, f.Ct)).Should().BeFalse();

        await f.ApplyAsync("A", passed: true);
        (await f.Runtime.IsAdmittedAsync(b, f.Ct)).Should().BeTrue();

        var result = await f.Runtime.ExecuteAsync(b.EffectId, context, f.Validate, cancellationToken: f.Ct);
        result.MutationPerformed.Should().BeTrue();
        result.Record.Stage.Should().Be(RepositoryEffectStage.ValidationPassed);
    }

    [Fact]
    public async Task Exact_downstream_base_mismatch_refuses_mutation_and_requires_reconciliation()
    {
        using var f = await Fixture.CreateAsync();
        await f.ApplyAsync("A", passed: true);
        File.WriteAllText(Path.Combine(f.CheckoutRoot, "src", "b.txt"), "external");
        var b = f.Graph.Effects.Single(e => e.LogicalId == "B").Effect.Intent;
        var result = await f.Runtime.ExecuteAsync(b.EffectId, f.Context, f.Validate, cancellationToken: f.Ct);
        result.MutationPerformed.Should().BeFalse();
        result.ReasonCode.Should().Contain("reconciliation-required");
        File.ReadAllText(Path.Combine(f.CheckoutRoot, "src", "b.txt")).Should().Be("external");
        (await f.Effects.GetAsync(b.EffectId, f.Ct))!.Stage.Should().Be(RepositoryEffectStage.Blocked);
    }

    [Fact]
    public async Task Partial_prerequisite_blocks_downstream_with_causal_reason()
    {
        using var f = await Fixture.CreateAsync();
        await f.ApplyAsync("A", partial: true);
        var b = f.Graph.Effects.Single(e => e.LogicalId == "B").Effect.Intent;
        var result = await f.Runtime.ExecuteAsync(b.EffectId, f.Context, cancellationToken: f.Ct);
        result.MutationPerformed.Should().BeFalse();
        result.ReasonCode.Should().Contain("prerequisite-A-requires-reconciliation");
        (await f.Effects.GetAsync(b.EffectId, f.Ct))!.Stage.Should().Be(RepositoryEffectStage.Prepared);
        File.ReadAllText(Path.Combine(f.CheckoutRoot, "src", "a.txt")).Should().Be("a-after");
        File.ReadAllText(Path.Combine(f.CheckoutRoot, "src", "b.txt")).Should().Be("b-before");
    }

    [Fact]
    public async Task Unresolved_prerequisite_blocks_downstream_with_causal_reason()
    {
        using var f = await Fixture.CreateAsync();
        var a = f.Graph.Effects.Single(e => e.LogicalId == "A").Effect.Intent;
        await f.Effects.TransitionAsync(a.EffectId, RepositoryEffectStage.IntentCommitted, cancellationToken: f.Ct);
        await f.Effects.TransitionAsync(a.EffectId, RepositoryEffectStage.Applying, cancellationToken: f.Ct);
        File.WriteAllText(Path.Combine(f.CheckoutRoot, "src", "a.txt"), "external");
        var reconcile = await f.Runtime.ExecuteAsync(a.EffectId, f.Context, cancellationToken: f.Ct);
        reconcile.MutationPerformed.Should().BeFalse();
        reconcile.Classification.Should().Be(RepositoryEffectClassification.Unresolved);
        reconcile.ReasonCode.Should().Contain("reconciliation-required");
        var b = f.Graph.Effects.Single(e => e.LogicalId == "B").Effect.Intent;
        var result = await f.Runtime.ExecuteAsync(b.EffectId, f.Context, cancellationToken: f.Ct);
        result.MutationPerformed.Should().BeFalse();
        result.ReasonCode.Should().Contain("prerequisite-A-requires-reconciliation");
        (await f.Effects.GetAsync(b.EffectId, f.Ct))!.Stage.Should().Be(RepositoryEffectStage.Prepared);
    }

    [Fact]
    public async Task Prepared_old_B_is_not_replayed_after_approved_cutover_preserves_validated_A_and_selects_B2()
    {
        using var f = await Fixture.CreateAsync();
        await f.ApplyAsync("A", passed: true);
        var oldB = f.B;
        var corrected = await f.BuildCorrectionAsync(adoptBAsWell: false);
        (await f.Revisions.GetActiveAsync(f.WorkflowId, f.Ct)).RevisionId.Should().Be("revision-3");
        var active = await f.Runtime.GetActiveGraphAsync(f.WorkflowId, f.Ct);
        active.Effects.Single(e => e.LogicalId == "A").Reuse.Should().BeTrue();
        active.Effects.Single(e => e.LogicalId == "B2").Reuse.Should().BeFalse();

        var context = new AuthenticatedAuthorityContext("tenant", "operator", f.WorkflowId.ToString("D"), "revision-3", "fence");
        var result = await f.Runtime.ExecuteAsync(oldB.EffectId, context, cancellationToken: f.Ct);
        result.MutationPerformed.Should().BeFalse();
        result.ReasonCode.Should().Be("m4.effect-superseded");
        (await f.Effects.GetAsync(oldB.EffectId, f.Ct))!.Stage.Should().Be(RepositoryEffectStage.Prepared);
        File.ReadAllText(Path.Combine(f.CheckoutRoot, "src", "b.txt")).Should().Be("b-before");
        var newB = corrected.Effects.Single(e => e.LogicalId == "B2").Effect.Intent;
        var applied = await f.Runtime.ExecuteAsync(newB.EffectId, context, f.Validate, cancellationToken: f.Ct);
        applied.MutationPerformed.Should().BeTrue();
    }

    [Fact]
    public async Task Unvalidated_descendant_cannot_be_claimed_as_preserved_during_proposal()
    {
        using var f = await Fixture.CreateAsync();
        await f.ApplyAsync("A", passed: true);
        Func<Task> falsePreservation = async () => await f.BuildCorrectionAsync(adoptBAsWell: true);
        var error = await falsePreservation.Should().ThrowAsync<WorkflowStateException>();
        error.Which.Message.Should().Contain("local validation");
    }

    [Fact]
    public async Task Revoked_prerequisite_authority_before_dependent_write_refuses_the_write()
    {
        using var f = await Fixture.CreateAsync();
        await f.ApplyAsync("A", passed: true);
        f.Authorizer.DeniedPath = "src/a.txt";
        var b = f.Graph.Effects.Single(e => e.LogicalId == "B").Effect.Intent;
        var result = await f.Runtime.ExecuteAsync(b.EffectId, f.Context, f.Validate, cancellationToken: f.Ct);
        result.MutationPerformed.Should().BeFalse();
        result.ReasonCode.Should().Contain("prerequisite-reuse-authority-denied");
        (await f.Effects.GetAsync(b.EffectId, f.Ct))!.Stage.Should().Be(RepositoryEffectStage.IntentCommitted);
        File.ReadAllText(Path.Combine(f.CheckoutRoot, "src", "b.txt")).Should().Be("b-before");
    }

    [Fact]
    public async Task Local_pass_and_full_workflow_failure_are_separate_and_wrong_target_or_mutation_cannot_pass()
    {
        using var f = await Fixture.CreateAsync();
        await f.ApplyAsync("A", passed: true);
        await f.ApplyAsync("B", passed: true);
        var local = await f.Effects.GetAsync(f.A.EffectId, f.Ct);
        local!.Stage.Should().Be(RepositoryEffectStage.ValidationPassed);
        (await f.Graphs.GetWorkflowValidationAsync(f.WorkflowId, f.Graph.RevisionId, f.Ct)).Should().BeNull();

        var wrongTarget = () => f.Runtime.ValidateWorkflowAsync(f.WorkflowId, f.Context,
            (state, _) => ValueTask.FromResult(new RepositoryEffectValidation("bad-target", "tests", "test", true,
                RepositoryStateSnapshot.Hash("output"), "wrong target")), f.Ct);
        await wrongTarget.Should().ThrowAsync<WorkflowStateException>();

        var changedDuringValidation = () => f.Runtime.ValidateWorkflowAsync(f.WorkflowId, f.Context,
            (state, _) =>
            {
                File.WriteAllText(Path.Combine(f.CheckoutRoot, "src", "a.txt"), "changed");
                return ValueTask.FromResult(new RepositoryEffectValidation(state.StateDigest, "tests", "test", true,
                    RepositoryStateSnapshot.Hash("output"), "changed checkout"));
            }, f.Ct);
        await changedDuringValidation.Should().ThrowAsync<WorkflowStateException>();
        (await f.Graphs.GetWorkflowValidationAsync(f.WorkflowId, f.Graph.RevisionId, f.Ct)).Should().BeNull();

        File.WriteAllText(Path.Combine(f.CheckoutRoot, "src", "a.txt"), "a-after");
        var failed = await f.Runtime.ValidateWorkflowAsync(f.WorkflowId, f.Context,
            (state, _) => ValueTask.FromResult(new RepositoryEffectValidation(state.StateDigest, "full-suite", "dotnet test", false,
                RepositoryStateSnapshot.Hash("failed"), "independent suite failed")), f.Ct);
        failed.Passed.Should().BeFalse();
        failed.TargetStateDigest.Should().Be(f.B.ExpectedPostState.StateDigest);
        var record = await f.Effects.GetAsync(f.A.EffectId, f.Ct);
        record!.Stage.Should().Be(RepositoryEffectStage.ValidationPassed);
        record.Validation!.Passed.Should().BeTrue();
        (await f.Graphs.GetWorkflowValidationAsync(f.WorkflowId, f.Graph.RevisionId, f.Ct))!.Passed.Should().BeFalse();
    }

    [Fact]
    public void Graph_rejects_cross_graph_prerequisite_identity_even_when_effect_id_matches()
    {
        var f = Fixture.BuildGraphOnly();
        var a = f.EffectA;
        var b = f.EffectB;
        var foreignIntent = RepositoryEffectIntent.Create(Guid.NewGuid(), f.WorkflowId, "other-revision", "A", Guid.NewGuid(), "attempt",
            f.RepositoryId, "workspace", f.Base,
            [RepositoryPatchFile.Create("src/a.txt", "a-before"u8, "a-foreign"u8)], DateTimeOffset.UtcNow);
        var foreignA = RepositoryCausalEffect.Create(foreignIntent, [], "foreign A");
        var mixedB = RepositoryCausalEffect.Create(b.Intent, [new(a.Intent.EffectId, foreignA.CausalDigest, a.Intent.ExpectedPostState.StateDigest)], "mixed identity");
        var create = () => RepositoryEffectGraph.Create(f.WorkflowId, "revision-2", "revision-1", f.RepositoryId, f.Base,
            [new("A", a, false, "A"), new("B", mixedB, false, "B")]);
        create.Should().Throw<InvalidOperationException>();
        GC.KeepAlive(f);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly CancellationToken Ct = TestContext.Current.CancellationToken;
        public Guid WorkflowId { get; } = Guid.NewGuid();
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "marang-graph-runtime-" + Guid.NewGuid().ToString("N"));
        public string CheckoutRoot { get; }
        public SqliteDatabase Database { get; private set; } = null!;
        public SqliteAdaptiveRevisionRepository Revisions { get; private set; } = null!;
        public SqliteRepositoryEffectJournal Effects { get; private set; } = null!;
        public SqliteRepositoryEffectGraphJournal Graphs { get; private set; } = null!;
        public ControlledRepository Repository { get; private set; } = null!;
        public RepositoryEffectGraph Graph { get; private set; } = null!;
        public RepositoryEffectGraphRuntime Runtime { get; private set; } = null!;
        public AdaptiveSupervisor Supervisor { get; private set; } = null!;
        private WorkflowRegistry registry = null!;
        public RepositoryEffectIntent A { get; private set; } = null!;
        public RepositoryEffectIntent B { get; private set; } = null!;
        public RepositoryCausalEffect EffectA { get; private set; } = null!;
        public RepositoryCausalEffect EffectB { get; private set; } = null!;
        public RepositoryStateSnapshot Base { get; private set; } = null!;
        public string RepositoryId { get; private set; } = "";
        public AuthenticatedAuthorityContext Context { get; private set; } = null!;
        public readonly CedarRecordingAuthorizer Authorizer;
        private readonly Ports ports = new();

        private Fixture()
        {
            CheckoutRoot = Path.Combine(Root, "checkout");
            Authorizer = new CedarRecordingAuthorizer(Path.Combine(Root, "authority"));
        }

        public static Fixture BuildGraphOnly()
        {
            var f = new Fixture();
            f.RepositoryId = RepositoryStateSnapshot.Hash("graph-runtime-repository");
            f.Base = State(f.RepositoryId, "a-before", "b-before");
            f.EffectA = MakeCausal(f.WorkflowId, f.RepositoryId, "revision-2", "A", f.Base, "a-before", "a-after", "src/a.txt", []);
            f.EffectB = MakeCausal(f.WorkflowId, f.RepositoryId, "revision-2", "B", f.EffectA.Intent.ExpectedPostState,
                "b-before", "b-after", "src/b.txt", [new(f.EffectA.Intent.EffectId, f.EffectA.CausalDigest, f.EffectA.Intent.ExpectedPostState.StateDigest)]);
            return f;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root); Directory.CreateDirectory(Path.Combine(f.CheckoutRoot, "src"));
            File.WriteAllText(Path.Combine(f.CheckoutRoot, "src", "a.txt"), "a-before");
            File.WriteAllText(Path.Combine(f.CheckoutRoot, "src", "b.txt"), "b-before");
            RunGit(f.CheckoutRoot, "init", "-q"); RunGit(f.CheckoutRoot, "config", "user.email", "tests@example.invalid");
            RunGit(f.CheckoutRoot, "config", "user.name", "Marang Tests"); RunGit(f.CheckoutRoot, "add", "src"); RunGit(f.CheckoutRoot, "commit", "-qm", "initial");
            f.Database = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = Path.Combine(f.Root, "execution.db"), Pooling = false });
            f.Revisions = new(f.Database); await f.Revisions.InitializeAsync(f.Ct);
            f.Effects = new(f.Database); await f.Effects.InitializeAsync(f.Ct);
            f.Graphs = new(f.Database, f.Effects); await f.Graphs.InitializeAsync(f.Ct);
            await new SqliteDelegationRecoveryJournal(f.Database).InitializeAsync(f.Ct);
            f.Repository = ControlledRepository.Open(f.CheckoutRoot, ["src/a.txt", "src/b.txt"]);
            f.RepositoryId = f.Repository.RepositoryId;
            var baseState = await f.Repository.ObserveAsync(f.Ct); f.Base = baseState;

            // The initial revision has two independent read branches, which M4 retains as the inventory basis.
            var initial = await AdaptivePlanAuthoring.AuthorAsync(f.WorkflowId, "revision-1", null, "inspect repository", "initial read", "test",
                [new("inventory", "Inventory", "inspect files", "input", AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "source", [], Guid.NewGuid(), true),
                 new("analysis", "Analysis", "analyze files", "input", AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "source", [], Guid.NewGuid(), true)],
                new FuwenZhinuExecutionPorts(f.ports, f.ports, f.ports), cancellationToken: f.Ct);
            await f.Revisions.ActivateInitialAsync(initial.Revision, f.Ct);

            var a = MakeCausal(f.WorkflowId, f.RepositoryId, "revision-2", "A", baseState, "a-before", "a-after", "src/a.txt", []);
            var b = MakeCausal(f.WorkflowId, f.RepositoryId, "revision-2", "B", a.Intent.ExpectedPostState,
                "b-before", "b-after", "src/b.txt", [new(a.Intent.EffectId, a.CausalDigest, a.Intent.ExpectedPostState.StateDigest)]);
            f.EffectA = a; f.EffectB = b; f.A = a.Intent; f.B = b.Intent;
            f.Graph = RepositoryEffectGraph.Create(f.WorkflowId, "revision-2", "revision-1", f.RepositoryId, baseState,
                [new("A", a, false, "first effect"), new("B", b, false, "depends on A")]);
            await f.Graphs.PrepareGraphAsync(f.Graph, f.Ct);

            var hostAuthority = new SqliteAdaptiveSupervisorAuthority(f.Database, f.Revisions, f.Authorizer,
                new AdaptiveAuthorityProfile("tenant", "supervisor", "workspace", "reports/readiness.md", "write-file", "fence")
                { AllowedBranchAuthorityDigest = AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile }, _ => true);
            await hostAuthority.InitializeAsync(f.Ct);
            f.Context = new("tenant", "operator", f.WorkflowId.ToString("D"), "revision-2", "fence");
            var specs = new[]
            {
                new AdaptiveWorkSpec("inventory", "Inventory", "inspect files", "input", AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "source", [], initial.Revision.Branches[0].Execution.Id, true),
                new AdaptiveWorkSpec("analysis", "Analysis", "analyze files", "input", AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "source", [], initial.Revision.Branches[1].Execution.Id, true),
                new AdaptiveWorkSpec("A", "Effect A", "first effect", a.CausalDigest, AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "causal-graph", [], a.Intent.ExecutionId, false) { IsRepositoryEffect = true },
                new AdaptiveWorkSpec("B", "Effect B", "depends on A", b.CausalDigest, AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "causal-graph", ["A"], b.Intent.ExecutionId, false) { IsRepositoryEffect = true }
            };
            var candidate = await AdaptivePlanAuthoring.AuthorEffectGraphAsync(f.WorkflowId, "revision-2", "revision-1", "apply graph", "approved graph", "test",
                specs, f.Graph, new FuwenZhinuExecutionPorts(f.ports, f.ports, f.ports), initial.Registry, f.Ct);
            var guard = new RepositoryEffectGraphRevisionGuard(f.Repository, f.Effects, f.Graphs, f.Revisions);
            f.Supervisor = new AdaptiveSupervisor(f.Revisions, hostAuthority, guard);
            f.registry = candidate.Registry;
            var proposed = await f.Supervisor.ProposeAsync(candidate.Revision, "M4 runtime test graph", f.Ct);
            await f.Supervisor.ApproveAsync(proposed.Proposal, proposed.Digest, "operator", f.Ct);
            var receipt = await hostAuthority.FindApprovalAsync(proposed.Proposal.ProposalId, proposed.Digest, f.Ct);
            await f.Supervisor.CommitAsync(proposed.Proposal, proposed.Digest, receipt, f.Ct);
            f.Runtime = new(f.Database, f.Revisions, f.Graphs, f.Effects, f.Repository, f.Authorizer, "workspace");
            return f;
        }

        public async Task<RepositoryEffectGraph> BuildCorrectionAsync(bool adoptBAsWell)
        {
            var physical = await Repository.ObserveAsync(Ct);
            var a = Graph.Effects.Single(e => e.LogicalId == "A");
            var selections = new List<RepositoryEffectSelection> { a with { Reuse = true, Rationale = "adopt locally validated A" } };
            RepositoryEffectIntent? b2Intent = null;
            if (adoptBAsWell)
                selections.Add(Graph.Effects.Single(e => e.LogicalId == "B") with { Reuse = true, Rationale = "claim B preserved" });
            else
            {
                b2Intent = RepositoryEffectIntent.Create(Guid.NewGuid(), WorkflowId, "revision-3", "B2", Guid.NewGuid(), "attempt",
                    RepositoryId, "workspace", physical, [RepositoryPatchFile.Create("src/b.txt", "b-before"u8, "b-corrected"u8)], DateTimeOffset.UtcNow);
                var b2 = RepositoryCausalEffect.Create(b2Intent,
                    [new(a.Effect.Intent.EffectId, a.Effect.CausalDigest, a.Effect.Intent.ExpectedPostState.StateDigest)], "replace B after exact A result");
                selections.Add(new("B2", b2, false, "replace old B", Graph.Effects.Single(e => e.LogicalId == "B").Effect.Intent.EffectId));
            }
            var provisional = RepositoryEffectGraph.Create(WorkflowId, "revision-3", "revision-2", RepositoryId, physical, selections);
            var dispositions = RepositoryEffectGraphImpact.Analyze(Graph, provisional);
            var next = RepositoryEffectGraph.Create(WorkflowId, "revision-3", "revision-2", RepositoryId, physical, selections, dispositions);
            await Graphs.PrepareGraphAsync(next, Ct);
            var current = await Revisions.GetActiveAsync(WorkflowId, Ct);
            var specs = new List<AdaptiveWorkSpec>
            {
                new("inventory", "Inventory", "inspect files", "input", AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "source", [], current.Branches.Single(b => b.LogicalId == "inventory").Execution.Id, true),
                new("analysis", "Analysis", "analyze files", "input", AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "source", [], current.Branches.Single(b => b.LogicalId == "analysis").Execution.Id, true),
                new("A", "Effect A", "first effect", a.Effect.CausalDigest, AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "causal-graph", [], a.Effect.Intent.ExecutionId, true) { IsRepositoryEffect = true }
            };
            foreach (var selection in next.Effects.Where(e => e.LogicalId != "A"))
                specs.Add(new(selection.LogicalId, "Effect " + selection.LogicalId, selection.Reuse ? "depends on A" : "downstream effect", selection.Effect.CausalDigest,
                    AdaptivePlanAuthoring.RepositoryEffectGraphAuthorityProfile, "causal-graph", ["A"], selection.Effect.Intent.ExecutionId, selection.Reuse)
                { IsRepositoryEffect = true });
            var candidate = await AdaptivePlanAuthoring.AuthorEffectGraphAsync(WorkflowId, "revision-3", "revision-2", "correct graph", "adopt A and correct B", "test",
                specs.ToArray(), next, new FuwenZhinuExecutionPorts(ports, ports, ports), registry, Ct);
            var proposed = await Supervisor.ProposeAsync(candidate.Revision, "Correct downstream result", Ct);
            var receipt = await Supervisor.ApproveAsync(proposed.Proposal, proposed.Digest, "operator", Ct);
            await Supervisor.CommitAsync(proposed.Proposal, proposed.Digest, receipt, Ct);
            registry = candidate.Registry;
            Graph = next;
            Runtime = new(Database, Revisions, Graphs, Effects, Repository, Authorizer, "workspace");
            Context = new("tenant", "operator", WorkflowId.ToString("D"), "revision-3", "fence");
            return next;
        }

        public ValueTask<RepositoryEffectValidation> Validate(RepositoryStateSnapshot state, CancellationToken _) =>
            ValueTask.FromResult(new RepositoryEffectValidation(state.StateDigest, "local", "test", true,
                RepositoryStateSnapshot.Hash("local-pass"), "passed"));

        public async Task ApplyAsync(string logical, bool partial = false, bool? passed = null)
        {
            var selection = Graph.Effects.Single(e => e.LogicalId == logical);
            if (partial)
            {
                var intent = selection.Effect.Intent;
                await Effects.TransitionAsync(intent.EffectId, RepositoryEffectStage.IntentCommitted, cancellationToken: Ct);
                await Effects.TransitionAsync(intent.EffectId, RepositoryEffectStage.Applying, cancellationToken: Ct);
                File.WriteAllBytes(Path.Combine(CheckoutRoot, intent.Files[0].Path.Replace('/', Path.DirectorySeparatorChar)), intent.Files[0].AfterBytes.ToArray());
                await Effects.TransitionAsync(intent.EffectId, RepositoryEffectStage.Blocked, RepositoryEffectClassification.PartiallyApplied,
                    await Repository.ObserveAsync(Ct), cancellationToken: Ct);
                return;
            }
            var result = await Runtime.ExecuteAsync(selection.Effect.Intent.EffectId, Context,
                (state, _) => ValueTask.FromResult(new RepositoryEffectValidation(state.StateDigest, "local", "test", passed ?? true,
                    RepositoryStateSnapshot.Hash("local-output"), passed == false ? "failed" : "passed")), cancellationToken: Ct);
            result.MutationPerformed.Should().BeTrue();
        }

        private static RepositoryCausalEffect MakeCausal(Guid workflow, string repository, string revision, string logical,
            RepositoryStateSnapshot basis, string before, string after, string path, IEnumerable<RepositoryEffectDependency> deps)
        {
            var intent = RepositoryEffectIntent.Create(Guid.NewGuid(), workflow, revision, logical, Guid.NewGuid(), "attempt", repository,
                "workspace", basis, [RepositoryPatchFile.Create(path, Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(after))], DateTimeOffset.UtcNow);
            return RepositoryCausalEffect.Create(intent, deps, "runtime test " + logical);
        }

        private static RepositoryStateSnapshot State(string repository, string a, string b) => RepositoryStateSnapshot.Create(repository,
            new string('a', 40), RepositoryStateSnapshot.Hash("index"),
            [new("src/a.txt", RepositoryStateSnapshot.Hash(a)), new("src/b.txt", RepositoryStateSnapshot.Hash(b))]);

        private static void RunGit(string root, params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException(output + error);
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (!Directory.Exists(Root)) return;
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            foreach (var dir in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                File.SetAttributes(dir, FileAttributes.Directory);
            File.SetAttributes(Root, FileAttributes.Directory);
            Directory.Delete(Root, true);
        }

        private sealed class Ports : IActivityExecutor, IContextProvider, IInferenceExecutor
        {
            public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(System.Text.Json.JsonSerializer.SerializeToElement("ok"))));
            public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }

    private sealed class CedarRecordingAuthorizer(string directory) : IAuthorityRequestAuthorizer
    {
        private readonly AuthorityStoreActor actor = new("tenant", "host", "runtime-tests");
        private readonly TimeProvider clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        public bool Allowed { get; set; } = true;
        public string? DeniedPath { get; set; }

        public async ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(directory);
            var db = new SqliteAuthorityStore(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db"), new PermitStorePolicy(), clock);
            var now = clock.GetUtcNow();
            var allowedPath = Allowed && request.RelativePath != DeniedPath ? request.RelativePath : "src/revoked.txt";
            var grant = new AuthorityGrant("runtime-test-grant", [AuthorityAction.WriteFile],
                new AuthorityScope(request.WorkspaceId, allowedPath, AuthorityScopeKind.Exact), [], now.AddMinutes(-1), now.AddHours(1));
            var snapshot = new AuthoritySnapshot(request.Context, "runtime-test-current", [new AuthorityLayer("current", [grant])], [], now.AddHours(1));
            var published = await db.PublishAsync(new AuthorityPublishCommand("runtime-test-publish", actor, snapshot, 0), cancellationToken);
            if (published.Status != AuthorityMutationStatus.Applied) throw new InvalidOperationException("Could not publish current test authority.");
            var current = new CurrentAuthorityRequestAuthorizer(new AuthorityStoreSnapshotSource(db, actor),
                new CedarAuthorityEvaluator(), new DecisionRecorder(), clock);
            return await current.AuthorizeAsync(request, cancellationToken);
        }
    }

    private sealed class PermitStorePolicy : IAuthorityStoreAuthorizer
    {
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request, CancellationToken cancellationToken = default) =>
            new(new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
    }
    private sealed class DecisionRecorder : IAuthorityDecisionRecorder
    {
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision, CancellationToken cancellationToken = default) => new(true);
    }
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
