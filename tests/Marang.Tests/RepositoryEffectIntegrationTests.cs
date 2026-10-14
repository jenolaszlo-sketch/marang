using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Marang.Hufu;
using Marang.Runs.Adaptive;
using Marang.Runs.Effects;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Tests;

public sealed class RepositoryEffectIntegrationTests
{
    [Fact]
    public async Task Effect_authoring_requires_revision_guard_explicit_approval_and_exact_admitted_intent()
    {
        using var fixture = await EffectFixture.CreateAsync();
        var initial = fixture.Initial;
        var intent = fixture.CreateIntent("r2", "effect-r2", initialState: await fixture.Repository.ObserveAsync(TestContext.Current.CancellationToken));
        var legacyEffectSpecs = new[] {
            new AdaptiveWorkSpec("read-a", "Read A", "read", "input", "read", "source", [], Guid.NewGuid(), true),
            new AdaptiveWorkSpec("read-b", "Read B", "read", "input", "read", "source", [], Guid.NewGuid(), true),
            new AdaptiveWorkSpec("effect", "Effect", "mutate", "input", AdaptivePlanAuthoring.RepositoryEffectAuthorityProfile,
                "effect", [], Guid.NewGuid(), false) { IsRepositoryEffect = true }
        };
        Func<Task> legacyAuthoring = async () => await AdaptivePlanAuthoring.AuthorAsync(fixture.WorkflowId, "legacy-effect", "r1",
            "goal", "reason", "test", legacyEffectSpecs, new FuwenZhinuExecutionPorts(new Ports(), new Ports(), new Ports()),
            cancellationToken: TestContext.Current.CancellationToken);
        await legacyAuthoring.Should().ThrowAsync<ArgumentException>();
        var candidate = await fixture.AuthorEffectAsync(intent, "r2", "effect-r2");
        AdaptivePlanAuthoring.IsRepositoryEffectBranch(candidate, candidate.Branches.Single(b => b.LogicalId == "effect-r2")).Should().BeTrue();
        var binding = new RepositoryEffectWorkflowBinding(fixture.Database, fixture.Revisions);

        await fixture.Journal.PrepareAsync(intent, TestContext.Current.CancellationToken);
        (await binding.IsAdmittedAsync(intent, TestContext.Current.CancellationToken)).Should().BeFalse("a prepared intent is not an approved committed workflow effect");

        var noGuard = new AdaptiveSupervisor(fixture.Revisions, new FixtureAuthority(requiresApproval: true));
        Func<Task> missingGuard = async () => await noGuard.ProposeAsync(candidate,
            "Apply the approved repository correction", TestContext.Current.CancellationToken);
        await missingGuard.Should().ThrowAsync<WorkflowStateException>().WithMessage("*guard*");

        var guard = new RepositoryEffectRevisionGuard(fixture.Repository, fixture.Journal);
        var supervisor = new AdaptiveSupervisor(fixture.Revisions, new FixtureAuthority(requiresApproval: false), guard);
        Func<Task> missingApproval = async () => await supervisor.ProposeAsync(candidate,
            "Apply the approved repository correction", TestContext.Current.CancellationToken);
        await missingApproval.Should().ThrowAsync<WorkflowStateException>().WithMessage("*approval*");

        await using (var lease = await guard.EnterAsync(initial, candidate, activating: false, TestContext.Current.CancellationToken)) { }
        var proposal = await fixture.ProposeAndCommitAsync(candidate);
        proposal.ProposalId.Should().NotBeEmpty();
        (await binding.IsAdmittedAsync(intent, TestContext.Current.CancellationToken)).Should().BeTrue();
        var mismatched = intent with { IntentDigest = new string('0', 64) };
        (await binding.IsAdmittedAsync(mismatched, TestContext.Current.CancellationToken)).Should().BeFalse();
        (await binding.IsCurrentAsync(mismatched, TestContext.Current.CancellationToken)).Should().BeFalse();

        await fixture.ExpireApprovalAsync(proposal.ProposalId);
        (await binding.IsAdmittedAsync(intent, TestContext.Current.CancellationToken)).Should().BeFalse("an exact but expired approval cannot admit an effect");
        var cancelNoGuard = new AdaptiveSupervisor(fixture.Revisions, new FixtureAuthority(requiresApproval: true));
        Func<Task> unguardedCancel = async () => await cancelNoGuard.CancelAsync(fixture.WorkflowId, "operator", TestContext.Current.CancellationToken);
        await unguardedCancel.Should().ThrowAsync<WorkflowStateException>().WithMessage("*guard*");
        await new AdaptiveSupervisor(fixture.Revisions, new FixtureAuthority(requiresApproval: true), guard)
            .CancelAsync(fixture.WorkflowId, "operator", TestContext.Current.CancellationToken);
        (await binding.IsCurrentAsync(intent, TestContext.Current.CancellationToken)).Should().BeFalse("a cancelled workflow cannot authorize a current effect");
    }

    [Fact]
    public async Task Revision_guard_refuses_stale_base_and_partial_prior_effect_cutover()
    {
        using var fixture = await EffectFixture.CreateAsync();
        var baseState = await fixture.Repository.ObserveAsync(TestContext.Current.CancellationToken);
        var effect = fixture.CreateIntent("r2", "effect-r2", baseState);
        var candidate = await fixture.AuthorEffectAsync(effect, "r2", "effect-r2");
        await fixture.Journal.PrepareAsync(effect, TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(fixture.Checkout.Root, "src", "unrelated.txt"), "external change");
        var guard = new RepositoryEffectRevisionGuard(fixture.Repository, fixture.Journal);
        Func<Task> staleCutover = async () => await guard.EnterAsync(fixture.Initial, candidate, activating: false,
            TestContext.Current.CancellationToken);
        await staleCutover.Should().ThrowAsync<WorkflowStateException>().WithMessage("*exact observed repository state*");
        File.Delete(Path.Combine(fixture.Checkout.Root, "src", "unrelated.txt"));

        await fixture.Journal.TransitionAsync(effect.EffectId, RepositoryEffectStage.IntentCommitted,
            cancellationToken: TestContext.Current.CancellationToken);
        await fixture.Journal.TransitionAsync(effect.EffectId, RepositoryEffectStage.Applying,
            cancellationToken: TestContext.Current.CancellationToken);
        File.WriteAllBytes(Path.Combine(fixture.Checkout.Root, "src", "a.txt"), effect.Files[0].AfterBytes.ToArray());
        var ordinaryCandidate = await fixture.AuthorOrdinarySuccessorAsync("r2");
        Func<Task> partialCutover = async () => await guard.EnterAsync(fixture.Initial, ordinaryCandidate, activating: true,
            TestContext.Current.CancellationToken);
        await partialCutover.Should().ThrowAsync<WorkflowStateException>().WithMessage("*partial or unresolved*");
        var partialState = await fixture.Repository.ObserveAsync(TestContext.Current.CancellationToken);
        await fixture.Journal.TransitionAsync(effect.EffectId, RepositoryEffectStage.Blocked,
            RepositoryEffectClassification.PartiallyApplied, partialState, cancellationToken: TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(fixture.Checkout.Root, "src", "a.txt"), "old-a");
        Func<Task> restoredCutover = async () => await guard.EnterAsync(fixture.Initial, ordinaryCandidate, activating: true,
            TestContext.Current.CancellationToken);
        await restoredCutover.Should().ThrowAsync<WorkflowStateException>().WithMessage("*parked partial or unresolved*");
    }

    [Fact]
    public async Task Catch_up_retains_applied_effect_after_corrective_revision_and_blocks_pending_partial_effect()
    {
        using var fixture = await EffectFixture.CreateAsync();
        var initial = await fixture.Repository.ObserveAsync(TestContext.Current.CancellationToken);
        var first = fixture.CreateIntent("r2", "effect-r2", initial);
        var revision2 = await fixture.AuthorEffectAsync(first, "r2", "effect-r2");
        await fixture.PrepareAndCommitAsync(first, revision2);

        File.WriteAllBytes(Path.Combine(fixture.Checkout.Root, "src", "a.txt"), first.Files[0].AfterBytes.ToArray());
        File.WriteAllBytes(Path.Combine(fixture.Checkout.Root, "src", "b.txt"), first.Files[1].AfterBytes.ToArray());
        var afterFirst = await fixture.Repository.ObserveAsync(TestContext.Current.CancellationToken);
        await fixture.Record(first, RepositoryEffectStage.ValidationFailed, afterFirst, passed: false);

        var correction = fixture.CreateIntent("r3", "effect-r3", afterFirst, beforeA: "planned-a", beforeB: "planned-b", afterA: "fixed-a", afterB: "fixed-b");
        var revision3 = await fixture.AuthorEffectAsync(correction, "r3", "effect-r3", "r2");
        await fixture.PrepareAndCommitAsync(correction, revision3);
        File.WriteAllBytes(Path.Combine(fixture.Checkout.Root, "src", "a.txt"), correction.Files[0].AfterBytes.ToArray());
        File.WriteAllBytes(Path.Combine(fixture.Checkout.Root, "src", "b.txt"), correction.Files[1].AfterBytes.ToArray());
        var afterCorrection = await fixture.Repository.ObserveAsync(TestContext.Current.CancellationToken);
        await fixture.Record(correction, RepositoryEffectStage.ValidationPassed, afterCorrection, passed: true);

        var effectSupervisor = fixture.EffectSupervisor();
        var recovered = await effectSupervisor.ReadAsync(fixture.WorkflowId, "operator", TestContext.Current.CancellationToken);
        recovered.Workflow.ActiveRevisionId.Should().Be("r3");
        var historical = recovered.Effects.Single(e => e.EffectId == first.EffectId);
        historical.Classification.Should().Be("AppliedThenCorrected");
        historical.PhysicalEffectRecorded.Should().BeTrue();
        historical.ObservedState.Should().Be(afterFirst.StateDigest).And.NotBe(recovered.ObservedRepositoryState.StateDigest);
        recovered.Effects.Single(e => e.EffectId == correction.EffectId).ValidationPassed.Should().BeTrue();
        recovered.Workflow.ChangesSinceCheckpoint.Should().Contain(change => change.Kind == "RepositoryEffect" &&
            change.Summary.Contains(first.EffectId.ToString("D"), StringComparison.Ordinal) && change.Summary.Contains("ValidationFailed", StringComparison.Ordinal));
        recovered.Workflow.ChangesSinceCheckpoint.Should().Contain(change => change.Kind == "RepositoryEffect" &&
            change.Summary.Contains(correction.EffectId.ToString("D"), StringComparison.Ordinal) && change.Summary.Contains("ValidationPassed", StringComparison.Ordinal));
        (await effectSupervisor.DrillDownAsync(fixture.WorkflowId, "r3", "operator", TestContext.Current.CancellationToken))
            .PhysicalRepositoryExplanation.Should().Contain("Earlier applied effects");

        using var partialFixture = await EffectFixture.CreateAsync();
        var partialBase = await partialFixture.Repository.ObserveAsync(TestContext.Current.CancellationToken);
        var partialIntent = partialFixture.CreateIntent("r2", "effect-r2", partialBase);
        var partialRevision = await partialFixture.AuthorEffectAsync(partialIntent, "r2", "effect-r2");
        await partialFixture.PrepareAndCommitAsync(partialIntent, partialRevision);
        await partialFixture.Journal.TransitionAsync(partialIntent.EffectId, RepositoryEffectStage.IntentCommitted,
            cancellationToken: TestContext.Current.CancellationToken);
        await partialFixture.Journal.TransitionAsync(partialIntent.EffectId, RepositoryEffectStage.Applying,
            cancellationToken: TestContext.Current.CancellationToken);
        File.WriteAllBytes(Path.Combine(partialFixture.Checkout.Root, "src", "a.txt"), partialIntent.Files[0].AfterBytes.ToArray());
        var blocked = await partialFixture.EffectSupervisor().ReadAsync(partialFixture.WorkflowId, "operator", TestContext.Current.CancellationToken);
        blocked.MutationBlocked.Should().BeTrue();
        blocked.Workflow.State.Should().Be(DurableSupervisorState.Blocked);
        blocked.Effects.Single().Classification.Should().Be("PartiallyApplied");
        blocked.Workflow.Decisions.Should().Contain(d => d.Kind == "EffectReconciliation");
    }

    private sealed class EffectFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "marang-effect-integration-" + Guid.NewGuid().ToString("N"));
        private readonly Ports _ports = new();
        private WorkflowRegistry _registry = null!;
        private readonly Guid _inventoryId = Guid.NewGuid();
        private readonly Guid _analysisId = Guid.NewGuid();
        private readonly Guid _reportId = Guid.NewGuid();
        private readonly AdaptiveWorkSpec[] _initialSpecs;
        public Guid WorkflowId { get; } = Guid.NewGuid();
        public string DatabasePath => Path.Combine(_directory, "execution.db");
        public string DatabasePathAbsolute => Path.GetFullPath(DatabasePath);
        public SqliteDatabase Database { get; private set; } = null!;
        public SqliteAdaptiveRevisionRepository Revisions { get; private set; } = null!;
        public SqliteRepositoryEffectJournal Journal { get; private set; } = null!;
        public ControlledRepository Repository { get; private set; } = null!;
        public Checkout Checkout { get; private set; } = null!;
        public AdaptiveRevision Initial { get; private set; } = null!;
        private SqliteWorkflowStore _workflowStore = null!;

        private EffectFixture()
        {
            _initialSpecs = [
                new("inventory", "Inventory", "inventory", "input", AdaptivePlanAuthoring.RepositoryEffectAuthorityProfile, "source", [], _inventoryId, true),
                new("analysis", "Analysis", "analysis", "input", AdaptivePlanAuthoring.RepositoryEffectAuthorityProfile, "source", [], _analysisId, true),
                new("report", "Report", "combine results", "input", AdaptivePlanAuthoring.RepositoryEffectAuthorityProfile, "source", ["inventory", "analysis"], _reportId, true)];
        }

        public static async Task<EffectFixture> CreateAsync()
        {
            var fixture = new EffectFixture();
            Directory.CreateDirectory(fixture._directory);
            fixture.Checkout = Checkout.Create();
            fixture.Database = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = fixture.DatabasePathAbsolute, Pooling = false });
            fixture.Revisions = new(fixture.Database);
            await fixture.Revisions.InitializeAsync(TestContext.Current.CancellationToken);
            fixture.Journal = new(fixture.Database);
            await fixture.Journal.InitializeAsync(TestContext.Current.CancellationToken);
            await new SqliteDelegationRecoveryJournal(fixture.Database).InitializeAsync(TestContext.Current.CancellationToken);
            fixture.Repository = ControlledRepository.Open(fixture.Checkout.Root, ["src/a.txt", "src/b.txt"]);
            var authored = await AdaptivePlanAuthoring.AuthorAsync(fixture.WorkflowId, "r1", null,
                "Inspect controlled repository", "Initial read-only plan", "integration-test", fixture._initialSpecs,
                new FuwenZhinuExecutionPorts(fixture._ports, fixture._ports, fixture._ports), cancellationToken: TestContext.Current.CancellationToken);
            fixture._registry = authored.Registry;
            fixture.Initial = authored.Revision;
            await fixture.Revisions.ActivateInitialAsync(fixture.Initial, TestContext.Current.CancellationToken);
            var continuity = new SqliteSupervisorContinuityStore(fixture.Database, fixture.Revisions, actor => actor == "operator");
            await continuity.InitializeAsync(TestContext.Current.CancellationToken);
            await new SupervisorCatchUp(fixture.Database, actor => actor == "operator").InitializeAsync(TestContext.Current.CancellationToken);
            await continuity.RecordIntentAsync(fixture.WorkflowId, "r1", "Inspect controlled repository", SupervisorIntentTrigger.Initial,
                "Read-only baseline", [], "operator", TestContext.Current.CancellationToken);
            fixture._workflowStore = new(fixture.Database);
            await fixture.StartRunsAsync(fixture.Initial, authored.Registry);
            return fixture;
        }

        public RepositoryEffectIntent CreateIntent(string revision, string logicalId, RepositoryStateSnapshot initialState,
            string beforeA = "old-a", string beforeB = "old-b", string afterA = "planned-a", string afterB = "planned-b") =>
            RepositoryEffectIntent.Create(Guid.NewGuid(), WorkflowId, revision, logicalId, Guid.NewGuid(), "attempt-1",
                Repository.RepositoryId, "workspace", initialState,
                [RepositoryPatchFile.Create("src/a.txt", Encoding.UTF8.GetBytes(beforeA), Encoding.UTF8.GetBytes(afterA)),
                 RepositoryPatchFile.Create("src/b.txt", Encoding.UTF8.GetBytes(beforeB), Encoding.UTF8.GetBytes(afterB))], DateTimeOffset.UtcNow);

        public async Task<AdaptiveRevision> AuthorEffectAsync(RepositoryEffectIntent intent, string revision, string logicalId, string? parentRevision = null)
        {
            var specs = new[] {
                new AdaptiveWorkSpec("inventory", "Inventory", "inventory", "input", AdaptivePlanAuthoring.RepositoryEffectAuthorityProfile, "source", [], _inventoryId, true),
                new AdaptiveWorkSpec("analysis", "Analysis", "analysis", "input", AdaptivePlanAuthoring.RepositoryEffectAuthorityProfile, "source", [], _analysisId, true),
                new AdaptiveWorkSpec(logicalId, "Repository mutation", "apply exact immutable patch", intent.IntentDigest,
                    AdaptivePlanAuthoring.RepositoryEffectAuthorityProfile, "effect-contract", [], intent.ExecutionId, false) { IsRepositoryEffect = true },
                new AdaptiveWorkSpec("report", "Report", "combine results", "input", AdaptivePlanAuthoring.RepositoryEffectAuthorityProfile,
                    "source", ["inventory", "analysis", logicalId], Guid.NewGuid(), true)
            };
            var authored = await AdaptivePlanAuthoring.AuthorEffectAsync(WorkflowId, revision, parentRevision ?? Initial.RevisionId,
                "Inspect and correct repository", "Apply an explicit approved repository patch", "integration-test", specs,
                new FuwenZhinuExecutionPorts(_ports, _ports, _ports), _registry, TestContext.Current.CancellationToken);
            _registry = authored.Registry;
            return authored.Revision;
        }

        public async Task<AdaptiveRevision> AuthorOrdinarySuccessorAsync(string revision)
        {
            var specs = _initialSpecs.Select((s, i) => i == 2 ? s with { ExecutionId = Guid.NewGuid() } : s).ToArray();
            return (await AdaptivePlanAuthoring.AuthorAsync(WorkflowId, revision, Initial.RevisionId, "Inspect controlled repository",
                "Ordinary successor", "integration-test", specs, new FuwenZhinuExecutionPorts(_ports, _ports, _ports), _registry,
                TestContext.Current.CancellationToken)).Revision;
        }

        public async Task<AdaptiveProposal> ProposeAndCommitAsync(AdaptiveRevision candidate)
        {
            var proposal = new AdaptiveProposal(Guid.NewGuid(), candidate, await Revisions.GetStateDigestAsync(WorkflowId, TestContext.Current.CancellationToken),
                AdaptiveRevisionContracts.Compare(await Revisions.GetActiveAsync(WorkflowId, TestContext.Current.CancellationToken), candidate),
                AdaptiveAuthorityDecision.ApprovalRequired, "integration-test", "Effect revision requires exact operator approval.");
            var digest = AdaptiveRevisionContracts.Digest(proposal);
            await Revisions.ProposeAsync(proposal, TestContext.Current.CancellationToken);
            await Revisions.ApproveAsync(proposal.ProposalId, digest, "operator", "integration-test-approval",
                DateTimeOffset.UtcNow.AddHours(1), TestContext.Current.CancellationToken);
            await Revisions.CommitAsync(proposal.ProposalId, digest, TestContext.Current.CancellationToken);
            await StartRunsAsync(candidate, _registry);
            return proposal;
        }

        public async Task ExpireApprovalAsync(Guid proposalId)
        {
            await using var connection = await Database.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE adaptive_proposals SET approval_expires=$expires WHERE id=$id";
            command.Parameters.AddWithValue("$expires", DateTimeOffset.Parse("2000-01-01T00:00:00Z").ToString("O"));
            command.Parameters.AddWithValue("$id", proposalId.ToString("D"));
            (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        }

        public async Task PrepareAndCommitAsync(RepositoryEffectIntent intent, AdaptiveRevision candidate)
        {
            await Journal.PrepareAsync(intent, TestContext.Current.CancellationToken);
            var active = await Revisions.GetActiveAsync(WorkflowId, TestContext.Current.CancellationToken);
            await using var guard = await new RepositoryEffectRevisionGuard(Repository, Journal)
                .EnterAsync(active, candidate, activating: true, TestContext.Current.CancellationToken);
            await ProposeAndCommitAsync(candidate);
        }

        public async Task Record(RepositoryEffectIntent intent, RepositoryEffectStage terminal, RepositoryStateSnapshot observed, bool passed)
        {
            if (await Journal.GetAsync(intent.EffectId, TestContext.Current.CancellationToken) is null)
                await Journal.PrepareAsync(intent, TestContext.Current.CancellationToken);
            await Journal.TransitionAsync(intent.EffectId, RepositoryEffectStage.IntentCommitted, cancellationToken: TestContext.Current.CancellationToken);
            await Journal.TransitionAsync(intent.EffectId, RepositoryEffectStage.Applying, cancellationToken: TestContext.Current.CancellationToken);
            await Journal.TransitionAsync(intent.EffectId, RepositoryEffectStage.AppliedObserved,
                RepositoryEffectClassification.DefinitelyApplied, observed, cancellationToken: TestContext.Current.CancellationToken);
            var validation = new RepositoryEffectValidation(observed.StateDigest, "deterministic-test", "test-probe", passed,
                RepositoryStateSnapshot.Hash(passed ? "passed" : "failed"), passed ? "passed" : "expected failed validation");
            await Journal.TransitionAsync(intent.EffectId, terminal, RepositoryEffectClassification.DefinitelyApplied, observed,
                validation, TestContext.Current.CancellationToken);
        }

        public RepositoryEffectSupervisor EffectSupervisor()
        {
            var catchUp = new SupervisorCatchUp(Database, actor => actor == "operator");
            return new(Revisions, Journal, Repository, catchUp);
        }

        private async Task StartRunsAsync(AdaptiveRevision revision, WorkflowRegistry registry)
        {
            await using var engine = new WorkflowEngine(_workflowStore, registry);
            foreach (var branch in revision.Branches)
            {
                if (await _workflowStore.GetRunAsync(branch.Execution.Id, TestContext.Current.CancellationToken) is not null) continue;
                await engine.StartAsync(branch.Execution.WorkflowName, branch.Execution.WorkflowVersion,
                    JsonSerializer.Deserialize<JsonElement>(branch.Execution.InputJson!), branch.Execution.Id,
                    cancellationToken: TestContext.Current.CancellationToken);
            }
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Checkout.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }
    }

    private sealed class FixtureAuthority(bool requiresApproval) : IAdaptiveSupervisorAuthority
    {
        public AdaptiveSupervisorPolicy PolicyFor(AdaptiveRevision candidate) => new(requiresApproval, "integration-test");
        public bool IsAuthorizedOperator(string authenticatedActor) => authenticatedActor == "operator";
        public Task<AdaptiveRevisionAuthorityDecision> EvaluateAsync(AdaptiveProposal proposal, string digest,
            AdaptiveRevisionApprovalReceipt? receipt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AdaptiveRevisionApprovalReceipt> RecordApprovalAsync(AdaptiveProposal proposal, string digest,
            string authenticatedActor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CanCancelAsync(Guid workflowId, string authenticatedActor, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class Ports : IActivityExecutor, IContextProvider, IInferenceExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ActivityExecutionResult.Succeeded(RuntimeValue.FromJson(JsonSerializer.SerializeToElement("test result"))));
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Checkout : IDisposable
    {
        public string Root { get; }
        private Checkout(string root) => Root = root;
        public static Checkout Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "marang-effect-workflow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "a.txt"), "old-a");
            File.WriteAllText(Path.Combine(root, "src", "b.txt"), "old-b");
            RunGit(root, "init", "-q"); RunGit(root, "config", "user.email", "test@example.invalid"); RunGit(root, "config", "user.name", "Marang Integration");
            RunGit(root, "add", "src/a.txt", "src/b.txt"); RunGit(root, "commit", "-qm", "initial");
            return new(root);
        }
        private static void RunGit(string root, params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException(output + error);
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
