using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Marang.Runs.Effects;
using Penghou.Zhinu.Sqlite;

namespace Marang.Tests;

public sealed class RepositoryEffectGraphContractsTests
{
    [Fact]
    public async Task Graph_and_causal_rows_survive_sqlite_reopen_and_validation_binds_exact_state()
    {
        var ct = TestContext.Current.CancellationToken;
        using var files = new TempDb(); var db = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = files.Path, Pooling = false });
        var effects = new SqliteRepositoryEffectJournal(db); await effects.InitializeAsync(ct);
        var journal = new SqliteRepositoryEffectGraphJournal(db, effects); await journal.InitializeAsync(ct);
        var graph = OneEffectGraph(); await journal.PrepareGraphAsync(graph, ct);
        var validation = new RepositoryEffectValidation(graph.Effects[0].Effect.Intent.ExpectedPostState.StateDigest, "tests", "dotnet test", true, Sha("output"), "passed");
        await journal.RecordWorkflowValidationAsync(graph, validation, ct);
        var reopenedDb = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = files.Path, Pooling = false });
        var reopenedEffects = new SqliteRepositoryEffectJournal(reopenedDb); await reopenedEffects.InitializeAsync(ct);
        var reopened = new SqliteRepositoryEffectGraphJournal(reopenedDb, reopenedEffects); await reopened.InitializeAsync(ct);
        (await reopened.GetGraphAsync(graph.WorkflowId, graph.RevisionId, ct))!.GraphDigest.Should().Be(graph.GraphDigest);
        (await reopened.GetEffectAsync(graph.Effects[0].Effect.Intent.EffectId, ct))!.CausalDigest.Should().Be(graph.Effects[0].Effect.CausalDigest);
        (await reopened.GetWorkflowValidationAsync(graph.WorkflowId, graph.RevisionId, ct))!.TargetStateDigest.Should().Be(validation.TargetStateDigest);
        (await reopened.ListGraphsAsync(graph.WorkflowId, ct)).Should().ContainSingle();
    }

    [Fact]
    public void Graph_digest_and_dependency_binding_reject_tampering()
    {
        var graph = OneEffectGraph();
        graph.Verify().Should().BeTrue();
        (graph with { GraphDigest = Sha("wrong") }).Verify().Should().BeFalse();
        var item = graph.Effects[0];
        var changed = item with { Effect = item.Effect with { Prerequisites = [new(Guid.NewGuid(), Sha("causal"), Sha("result"))] } };
        (graph with { Effects = [changed] }).Verify().Should().BeFalse();
        var malformedEvidence = item.Effect with { RequirementEvidence = " " };
        malformedEvidence = malformedEvidence with
        { CausalDigest = RepositoryCausalEffect.ComputeCausalDigest(malformedEvidence.Intent.IntentDigest, malformedEvidence.Prerequisites, malformedEvidence.RequirementEvidence) };
        malformedEvidence.HasValidDigest().Should().BeFalse();
    }

    [Fact]
    public void Graph_rejects_cycles_and_incorrect_physical_state_lineage()
    {
        var workflow = Guid.NewGuid(); var repo = Sha("repo"); var baseState = State(repo, "a1");
        var aIntent = Intent(workflow, repo, "r2", "a", baseState, "a1", "a2");
        var bIntent = Intent(workflow, repo, "r2", "b", aIntent.ExpectedPostState, "a2", "b2");
        var a0 = RepositoryCausalEffect.Create(aIntent, [], "requirement a");
        var b0 = RepositoryCausalEffect.Create(bIntent, [new(aIntent.EffectId, a0.CausalDigest, aIntent.ExpectedPostState.StateDigest)], "requirement b");
        var aCycle = RepositoryCausalEffect.Create(aIntent, [new(bIntent.EffectId, b0.CausalDigest, bIntent.ExpectedPostState.StateDigest)], "requirement a");
        var bCycle = RepositoryCausalEffect.Create(bIntent, [new(aIntent.EffectId, aCycle.CausalDigest, aIntent.ExpectedPostState.StateDigest)], "requirement b");
        var cycle = () => RepositoryEffectGraph.Create(workflow, "r2", "r1", repo, baseState,
            [new("a", aCycle, false, "new"), new("b", bCycle, false, "new")]);
        cycle.Should().Throw<InvalidOperationException>();
        var badBase = State(repo, "unrelated");
        var badIntent = Intent(workflow, repo, "r2", "a", badBase, "unrelated", "a3");
        var bad = RepositoryCausalEffect.Create(badIntent, [], "requirement");
        var lineage = () => RepositoryEffectGraph.Create(workflow, "r2", null, repo, baseState, [new("a", bad, false, "new")]);
        lineage.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Journal_rejects_unsafe_adoption_and_impact_invalidates_changed_causal_descendants()
    {
        var ct = TestContext.Current.CancellationToken;
        using var files = new TempDb(); var db = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = files.Path, Pooling = false });
        var effects = new SqliteRepositoryEffectJournal(db); await effects.InitializeAsync(ct);
        var journal = new SqliteRepositoryEffectGraphJournal(db, effects); await journal.InitializeAsync(ct);
        var old = OneEffectGraph("r1", null); await journal.PrepareGraphAsync(old, ct);
        var adopted = old.Effects[0] with { Reuse = true };
        var child = RepositoryEffectGraph.Create(old.WorkflowId, "r2", "r1", old.RepositoryId, old.PhysicalBaseState, [adopted]);
        // An old intent can only be adopted after durable preparation.
        await journal.PrepareGraphAsync(child, ct);
        var replacementIntent = Intent(old.WorkflowId, old.RepositoryId, "r2", "step", old.PhysicalBaseState, "before", "replacement");
        var replacement = RepositoryCausalEffect.Create(replacementIntent, [], "changed requirement");
        var changed = RepositoryEffectGraph.Create(old.WorkflowId, "r2", "r1", old.RepositoryId, old.PhysicalBaseState,
            [new("step", replacement, false, "replacement")]);
        var impact = RepositoryEffectGraphImpact.Analyze(old, changed);
        impact.Should().Contain(d => d.EffectId == old.Effects[0].Effect.Intent.EffectId && d.Kinds.Contains(RepositoryEffectDispositionKind.RequiresReplacement));

        var unsafeOld = OneEffectGraph("prior", null);
        var unsafeSelection = unsafeOld.Effects[0] with
        {
            Effect = RepositoryCausalEffect.Create(
            Intent(unsafeOld.WorkflowId, unsafeOld.RepositoryId, "legacy", "step", unsafeOld.PhysicalBaseState, "before", "after"), [], "old"),
            Reuse = true
        };
        var unsafeGraph = RepositoryEffectGraph.Create(unsafeOld.WorkflowId, "r3", "prior", unsafeOld.RepositoryId, unsafeOld.PhysicalBaseState, [unsafeSelection]);
        await FluentActions.Awaiting(() => journal.PrepareGraphAsync(unsafeGraph, ct)).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Prerequisite_authority_binds_semantic_parent_to_current_physical_base()
    {
        var ct = TestContext.Current.CancellationToken;
        using var files = new TempDb(); var db = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = files.Path, Pooling = false });
        var effects = new SqliteRepositoryEffectJournal(db); await effects.InitializeAsync(ct);
        var journal = new SqliteRepositoryEffectGraphJournal(db, effects); await journal.InitializeAsync(ct);
        var workflow = Guid.NewGuid(); var repo = Sha("repo"); var originalBase = State(repo, "before");
        var parentIntent = Intent(workflow, repo, "r1", "parent-a", originalBase, "before", "parent-result-a");
        var parentCausal = RepositoryCausalEffect.Create(parentIntent, [], "parent requirement");
        var middleIntent = Intent(workflow, repo, "r1", "parent-b", parentIntent.ExpectedPostState, "parent-result-a", "parent-result-b");
        var middleCausal = RepositoryCausalEffect.Create(middleIntent,
            [new(parentIntent.EffectId, parentCausal.CausalDigest, parentIntent.ExpectedPostState.StateDigest)], "middle requirement");
        var parentGraph = RepositoryEffectGraph.Create(workflow, "r1", null, repo, originalBase,
            [new("parent-a", parentCausal, false, "initial"), new("parent-b", middleCausal, false, "initial")]);
        await journal.PrepareGraphAsync(parentGraph, ct);

        var physicalBase = State(repo, "physical-later");
        var dependentIntent = Intent(workflow, repo, "r2", "dependent", physicalBase, "physical-later", "dependent-result");
        var dependentCausal = RepositoryCausalEffect.Create(dependentIntent,
            [new(middleIntent.EffectId, middleCausal.CausalDigest, middleIntent.ExpectedPostState.StateDigest)], "dependent requirement");
        var candidate = RepositoryEffectGraph.Create(workflow, "r2", "r1", repo, physicalBase,
            [new("parent-a", parentCausal, true, "retain parent"), new("parent-b", middleCausal, true, "retain parent"), new("dependent", dependentCausal, false, "new dependent")]);
        await journal.PrepareGraphAsync(candidate, ct);

        RepositoryEffectAuthorizationEvidence Evidence(RepositoryEffectIntent prerequisiteIntent, RepositoryCausalEffect prerequisiteCausal) =>
            new(prerequisiteIntent.IntentDigest, "src/a.txt",
                Sha($"m4.accepted-prerequisite/v1\n{candidate.GraphDigest}\n{dependentIntent.EffectId:D}\n{prerequisiteCausal.CausalDigest}\n{dependentIntent.BaseState.StateDigest}\nsrc/a.txt"), "Permit",
                "tenant", "subject", workflow.ToString("D"), candidate.RevisionId, null, null, null, DateTimeOffset.UtcNow,
                candidate.GraphDigest, dependentIntent.EffectId, prerequisiteIntent.EffectId, prerequisiteCausal.CausalDigest, dependentIntent.BaseState.StateDigest);
        var evidence = new[] { Evidence(parentIntent, parentCausal), Evidence(middleIntent, middleCausal) };
        await FluentActions.Awaiting(() => journal.RecordPrerequisiteAuthorityAsync(candidate, dependentIntent.EffectId,
            [evidence[0] with { Status = "Allowed" }], ct)).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => journal.RecordPrerequisiteAuthorityAsync(candidate, dependentIntent.EffectId,
            [evidence[0] with { TargetPath = "src/foreign.txt" }], ct)).Should().ThrowAsync<InvalidOperationException>();
        await journal.RecordPrerequisiteAuthorityAsync(candidate, dependentIntent.EffectId, evidence, ct);
        var recorded = await journal.ListPrerequisiteAuthorityAsync(workflow, candidate.RevisionId, ct);
        recorded.Should().HaveCount(2).And.OnlyContain(e => e.ObservedStateDigest == physicalBase.StateDigest && e.Status == "Permit");
        dependentCausal.Prerequisites[0].ResultStateDigest.Should().Be(middleIntent.ExpectedPostState.StateDigest);
        dependentIntent.BaseState.StateDigest.Should().NotBe(parentIntent.ExpectedPostState.StateDigest);
    }

    [Fact]
    public void Impact_preserves_only_explicit_exact_upstream_reuse_and_invalidates_changed_descendants()
    {
        var workflow = Guid.NewGuid(); var repo = Sha("repo");
        var s0 = State(repo, "s0"); var aIntent = Intent(workflow, repo, "r1", "a", s0, "s0", "s1");
        var a = RepositoryCausalEffect.Create(aIntent, [], "requirement a");
        var bIntent = Intent(workflow, repo, "r1", "b", aIntent.ExpectedPostState, "s1", "s2");
        var b = RepositoryCausalEffect.Create(bIntent, [new(aIntent.EffectId, a.CausalDigest, aIntent.ExpectedPostState.StateDigest)], "requirement b");
        var cIntent = Intent(workflow, repo, "r1", "c", bIntent.ExpectedPostState, "s2", "s3");
        var c = RepositoryCausalEffect.Create(cIntent, [new(bIntent.EffectId, b.CausalDigest, bIntent.ExpectedPostState.StateDigest)], "requirement c");
        var parent = RepositoryEffectGraph.Create(workflow, "r1", null, repo, s0,
            [new("a", a, false, "initial"), new("b", b, false, "initial"), new("c", c, false, "initial")]);

        var physicalBase = cIntent.ExpectedPostState;
        var b2Intent = Intent(workflow, repo, "r2", "b", physicalBase, "s3", "b2");
        var b2 = RepositoryCausalEffect.Create(b2Intent, [new(aIntent.EffectId, a.CausalDigest, aIntent.ExpectedPostState.StateDigest)], "corrected b");
        var c2Intent = Intent(workflow, repo, "r2", "c", b2Intent.ExpectedPostState, "b2", "c2");
        var c2 = RepositoryCausalEffect.Create(c2Intent, [new(b2Intent.EffectId, b2.CausalDigest, b2Intent.ExpectedPostState.StateDigest)], "corrected c");
        var candidate = RepositoryEffectGraph.Create(workflow, "r2", "r1", repo, physicalBase,
            [new("a", a, true, "retain exact parent"), new("b", b2, false, "replace b"), new("c", c2, false, "replace downstream c")]);
        var impact = RepositoryEffectGraphImpact.Analyze(parent, candidate).ToDictionary(x => x.EffectId);
        impact[aIntent.EffectId].Kinds.Should().Contain(RepositoryEffectDispositionKind.Preserved).And.Contain(RepositoryEffectDispositionKind.AcceptedAsBase);
        impact[bIntent.EffectId].Kinds.Should().Contain(RepositoryEffectDispositionKind.Superseded).And.Contain(RepositoryEffectDispositionKind.RequiresReplacement);
        impact[bIntent.EffectId].Kinds.Should().Contain(RepositoryEffectDispositionKind.Invalidated);
        impact[cIntent.EffectId].Kinds.Should().Contain(RepositoryEffectDispositionKind.Invalidated).And.Contain(RepositoryEffectDispositionKind.RequiresReplacement).And.Contain(RepositoryEffectDispositionKind.Superseded);
        impact[cIntent.EffectId].Reason.Should().Contain(bIntent.EffectId.ToString("D"));
        RepositoryEffectGraph.Create(workflow, "r2", "r1", repo, physicalBase, candidate.Effects, impact.Values).Verify().Should().BeTrue();

        var removedCandidate = RepositoryEffectGraph.Create(workflow, "r2-remove", "r1", repo, s0,
            [new("a", a, true, "retain exact parent")]);
        var removedImpact = RepositoryEffectGraphImpact.Analyze(parent, removedCandidate).ToDictionary(x => x.EffectId);
        removedImpact[bIntent.EffectId].Kinds.Should().Contain(RepositoryEffectDispositionKind.Superseded).And.Contain(RepositoryEffectDispositionKind.Invalidated);

        var unrelatedBase = State(repo, "unrelated physical base");
        var unrelatedIntent = Intent(workflow, repo, "r3", "a", unrelatedBase, "unrelated physical base", "unrelated result");
        var unrelated = RepositoryCausalEffect.Create(unrelatedIntent, [], "new independent path");
        var unreachableCandidate = RepositoryEffectGraph.Create(workflow, "r3", "r1", repo, unrelatedBase,
            [new("a", unrelated, false, "new independent path")]);
        var unreachableImpact = RepositoryEffectGraphImpact.Analyze(parent, unreachableCandidate);
        unreachableImpact.Should().NotContain(d => d.Kinds.Contains(RepositoryEffectDispositionKind.AcceptedAsBase));
    }

    [Fact]
    public void Impact_resolves_explicit_replacement_identity_even_when_logical_step_is_renamed()
    {
        var parent = OneEffectGraph("r1", null);
        var old = parent.Effects[0];
        var replacementIntent = Intent(parent.WorkflowId, parent.RepositoryId, "r2", "renamed-step",
            parent.PhysicalBaseState, "before", "replacement");
        var replacementCausal = RepositoryCausalEffect.Create(replacementIntent, [], "replacement requirement");
        var candidate = RepositoryEffectGraph.Create(parent.WorkflowId, "r2", "r1", parent.RepositoryId,
            parent.PhysicalBaseState, [new("renamed-step", replacementCausal, false, "renamed replacement", old.Effect.Intent.EffectId)]);

        var disposition = RepositoryEffectGraphImpact.Analyze(parent, candidate).Single();
        disposition.EffectId.Should().Be(old.Effect.Intent.EffectId);
        disposition.ReplacementEffectId.Should().Be(replacementIntent.EffectId);

        var unknownMap = candidate.Effects[0] with { ReplacesEffectId = Guid.NewGuid() };
        var unknown = RepositoryEffectGraph.Create(parent.WorkflowId, "r2", "r1", parent.RepositoryId,
            parent.PhysicalBaseState, [unknownMap]);
        FluentActions.Invoking(() => RepositoryEffectGraphImpact.Analyze(parent, unknown))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Explicit_replacement_mapping_cannot_be_claimed_by_another_logical_fallback()
    {
        var workflow = Guid.NewGuid(); var repo = Sha("repo"); var state = State(repo, "before");
        var oldAIntent = Intent(workflow, repo, "r1", "a", state, "before", "after-a");
        var oldA = RepositoryCausalEffect.Create(oldAIntent, [], "a");
        var oldBIntent = Intent(workflow, repo, "r1", "b", oldAIntent.ExpectedPostState, "after-a", "after-b");
        var oldB = RepositoryCausalEffect.Create(oldBIntent, [], "b");
        var parent = RepositoryEffectGraph.Create(workflow, "r1", null, repo, state,
            [new("a", oldA, false, "initial"), new("b", oldB, false, "initial")]);
        var replacementIntent = Intent(workflow, repo, "r2", "a", state, "before", "replacement");
        var replacement = RepositoryCausalEffect.Create(replacementIntent, [], "replacement");
        var candidate = RepositoryEffectGraph.Create(workflow, "r2", "r1", repo, state,
            [new("a", replacement, false, "replaces b", oldBIntent.EffectId)]);

        var impact = RepositoryEffectGraphImpact.Analyze(parent, candidate).ToDictionary(x => x.EffectId);
        impact[oldBIntent.EffectId].ReplacementEffectId.Should().Be(replacementIntent.EffectId);
        impact[oldAIntent.EffectId].ReplacementEffectId.Should().BeNull();
        impact[oldAIntent.EffectId].Kinds.Should().Contain(RepositoryEffectDispositionKind.Superseded);
    }

    private static RepositoryEffectGraph OneEffectGraph(string revision = "r1", string? parent = null)
    {
        var workflow = Guid.NewGuid(); var repo = Sha("repo"); var state = State(repo, "before");
        var intent = Intent(workflow, repo, revision, "step", state, "before", "after");
        var causal = RepositoryCausalEffect.Create(intent, [], "required behavior");
        return RepositoryEffectGraph.Create(workflow, revision, parent, repo, state, [new("step", causal, false, "new work")]);
    }
    private static RepositoryEffectIntent Intent(Guid workflow, string repo, string revision, string logical, RepositoryStateSnapshot state, string before, string after)
    {
        return RepositoryEffectIntent.Create(Guid.NewGuid(), workflow, revision, logical, Guid.NewGuid(), "attempt", repo, "scope", state,
            [RepositoryPatchFile.Create("src/a.txt", Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(after))], DateTimeOffset.Parse("2026-10-14T00:00:00Z"));
    }
    private static RepositoryStateSnapshot State(string repo, string content) => RepositoryStateSnapshot.Create(repo, new string('a', 40), Sha("index"),
        [new("src/a.txt", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant())]);
    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private sealed class TempDb : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "marang-graph-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(directory, "test.db");
        public TempDb() => Directory.CreateDirectory(directory);
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
