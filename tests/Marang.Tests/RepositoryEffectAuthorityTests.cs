using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using FluentAssertions;
using Marang.Hufu;
using Penghou.Hufu;
using Penghou.Hufu.Cedar;
using Penghou.Hufu.Sqlite;
using Marang.Runs.Effects;

namespace Marang.Tests;

public sealed class RepositoryEffectAuthorityTests
{
    [Fact]
    public async Task Hufu_request_is_bound_to_exact_intent_digest_target_and_active_context()
    {
        var intent = RepositoryEffectJournalTests.CreateIntent();
        var context = new AuthenticatedAuthorityContext("tenant", "operator", intent.WorkflowId.ToString("D"), intent.RevisionId, "fence");
        var hufu = new RecordingAuthorizer();
        var authority = new Marang.Runs.Effects.RepositoryEffectAuthority(hufu, new ManifestVerifier(), "workspace");

        var result = await authority.AuthorizeAsync(intent, context, intent.Files[0].Path,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Allowed.Should().BeTrue($"{result.ReasonCode}; {result.Status}; {result.Decision}");
        hufu.LastRequest.Should().NotBeNull();
        hufu.LastRequest!.Action.Should().Be(AuthorityAction.WriteFile);
        hufu.LastRequest.WorkspaceId.Should().Be("workspace");
        hufu.LastRequest.RelativePath.Should().Be(intent.Files[0].Path);
        hufu.LastRequest.RequestIdentity.Should().Be(WriteIdentity(intent, intent.Files[0].Path));
    }

    [Fact]
    public async Task Approval_for_another_base_candidate_target_or_revision_cannot_authorize_this_effect()
    {
        var intent = RepositoryEffectJournalTests.CreateIntent();
        var context = new AuthenticatedAuthorityContext("tenant", "operator", intent.WorkflowId.ToString("D"), intent.RevisionId, "fence");
        var hufu = new RecordingAuthorizer();
        var manifest = new ManifestVerifier();
        var authority = new Marang.Runs.Effects.RepositoryEffectAuthority(hufu, manifest, "workspace");

        var foreignTarget = await authority.AuthorizeAsync(intent, context, "src/other.txt", cancellationToken: TestContext.Current.CancellationToken);
        foreignTarget.Allowed.Should().BeFalse();
        hufu.LastRequest.Should().BeNull();

        var foreignScope = await new Marang.Runs.Effects.RepositoryEffectAuthority(hufu, new ManifestVerifier(), "other-workspace")
            .AuthorizeAsync(intent, context, intent.Files[0].Path, cancellationToken: TestContext.Current.CancellationToken);
        foreignScope.Allowed.Should().BeFalse();
        foreignScope.ReasonCode.Should().Be("repository-effect.scope-mismatch");
        hufu.LastRequest.Should().BeNull();

        var foreignRevision = await authority.AuthorizeAsync(intent, context with { RevisionId = "r2" }, intent.Files[0].Path,
            cancellationToken: TestContext.Current.CancellationToken);
        foreignRevision.Allowed.Should().BeFalse();
        hufu.LastRequest.Should().BeNull();

        manifest.Admitted = false;
        var foreignCandidate = await authority.AuthorizeAsync(intent, context, intent.Files[0].Path,
            cancellationToken: TestContext.Current.CancellationToken);
        foreignCandidate.Allowed.Should().BeFalse();
        foreignCandidate.ReasonCode.Should().Be("repository-effect.manifest-binding-mismatch");
        hufu.LastRequest.Should().BeNull();

        manifest.Admitted = true;
        manifest.Current = false;
        var superseded = await authority.AuthorizeAsync(intent, context, intent.Files[0].Path,
            cancellationToken: TestContext.Current.CancellationToken);
        superseded.Allowed.Should().BeFalse();
        superseded.ReasonCode.Should().Be("repository-effect.revision-superseded");
        hufu.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Fresh_Hufu_check_denies_after_revocation_and_never_reuses_an_old_permit()
    {
        var intent = RepositoryEffectJournalTests.CreateIntent();
        var context = new AuthenticatedAuthorityContext("tenant", "operator", intent.WorkflowId.ToString("D"), intent.RevisionId, "fence");
        var hufu = new RecordingAuthorizer { Allowed = true };
        var authority = new Marang.Runs.Effects.RepositoryEffectAuthority(hufu, new ManifestVerifier(), "workspace");
        (await authority.AuthorizeAsync(intent, context, intent.Files[0].Path, cancellationToken: TestContext.Current.CancellationToken)).Allowed.Should().BeTrue();
        hufu.Allowed = false;
        (await authority.AuthorizeAsync(intent, context, intent.Files[0].Path, cancellationToken: TestContext.Current.CancellationToken)).Allowed.Should().BeFalse();
        hufu.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Real_Cedar_and_Sqlite_Hufu_grant_permits_exact_write_then_revocation_denies_replay()
    {
        using var database = new TemporaryDatabase();
        var intent = RepositoryEffectJournalTests.CreateIntent();
        var context = new AuthenticatedAuthorityContext("tenant", "operator", intent.WorkflowId.ToString("D"), intent.RevisionId, "fence");
        var actor = new AuthorityStoreActor("tenant", "host", "session");
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-14T09:00:00Z"));
        var store = new SqliteAuthorityStore(database.Path, new AllowStorePolicy(), time);
        var now = time.GetUtcNow();
        var snapshot = new AuthoritySnapshot(context, "1",
            [new AuthorityLayer("current", [new AuthorityGrant("write-exact", [AuthorityAction.WriteFile],
                new AuthorityScope("workspace", "src/a.txt", AuthorityScopeKind.Exact), [],
                now.AddMinutes(-1), now.AddHours(1))])], [], now.AddHours(1));
        (await store.PublishAsync(new AuthorityPublishCommand("publish-1", actor, snapshot, 0),
            TestContext.Current.CancellationToken)).Status.Should().Be(AuthorityMutationStatus.Applied);
        var hufu = new CurrentAuthorityRequestAuthorizer(new AuthorityStoreSnapshotSource(store, actor),
            new CedarAuthorityEvaluator(), new AllowDecisionRecorder(), time);
        var authority = new Marang.Runs.Effects.RepositoryEffectAuthority(hufu, new ManifestVerifier(), "workspace", time);

        var permitted = await authority.AuthorizeAsync(intent, context, intent.Files[0].Path,
            cancellationToken: TestContext.Current.CancellationToken);
        permitted.Allowed.Should().BeTrue();
        permitted.Evidence!.SnapshotIdentity.Should().Be(snapshot.Identity);
        permitted.Evidence.TenantId.Should().Be("tenant");
        permitted.Evidence.SubjectId.Should().Be("operator");

        (await store.RevokeAsync(new AuthorityRevokeCommand("revoke-1", actor, context, 1, "test.revoked"),
            TestContext.Current.CancellationToken)).Status.Should().Be(AuthorityMutationStatus.Applied);
        var revoked = await authority.AuthorizeAsync(intent, context, intent.Files[0].Path,
            cancellationToken: TestContext.Current.CancellationToken);
        revoked.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Real_Cedar_authority_normalizes_mixed_case_path_but_retains_exact_intent_evidence()
    {
        using var database = new TemporaryDatabase();
        var repositoryId = RepositoryStateSnapshot.Hash("repo-mixed-case");
        var before = "before"u8.ToArray();
        var intent = RepositoryEffectIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "r1", "implement", Guid.NewGuid(), "attempt",
            repositoryId, "workspace",
            RepositoryStateSnapshot.Create(repositoryId, new string('b', 40), RepositoryStateSnapshot.Hash("index"),
                [new RepositoryFileState("Src/A.txt", RepositoryStateSnapshot.Hash("before"))]),
            [RepositoryPatchFile.Create("Src/A.txt", before, "after"u8)], DateTimeOffset.UtcNow);
        var context = new AuthenticatedAuthorityContext("tenant", "operator", intent.WorkflowId.ToString("D"), intent.RevisionId, "fence");
        var actor = new AuthorityStoreActor("tenant", "host", "session");
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-14T09:00:00Z"));
        var store = new SqliteAuthorityStore(database.Path, new AllowStorePolicy(), time);
        var now = time.GetUtcNow();
        var snapshot = new AuthoritySnapshot(context, "1",
            [new AuthorityLayer("current", [new AuthorityGrant("write-lowercase", [AuthorityAction.WriteFile],
                new AuthorityScope("workspace", "src/a.txt", AuthorityScopeKind.Exact), [], now.AddMinutes(-1), now.AddHours(1))])], [], now.AddHours(1));
        (await store.PublishAsync(new AuthorityPublishCommand("publish-mixed", actor, snapshot, 0),
            TestContext.Current.CancellationToken)).Status.Should().Be(AuthorityMutationStatus.Applied);
        var hufu = new CurrentAuthorityRequestAuthorizer(new AuthorityStoreSnapshotSource(store, actor),
            new CedarAuthorityEvaluator(), new AllowDecisionRecorder(), time);
        var authority = new RepositoryEffectAuthority(hufu, new ManifestVerifier(), "workspace", time);

        var permitted = await authority.AuthorizeAsync(intent, context, "Src/A.txt",
            cancellationToken: TestContext.Current.CancellationToken);
        permitted.Allowed.Should().BeTrue(permitted.ReasonCode);
        permitted.Evidence!.TargetPath.Should().Be("Src/A.txt");
        permitted.Evidence.RequestIdentity.Should().Be(WriteIdentity(intent, "Src/A.txt"));
    }

    [Fact]
    public async Task Changed_patch_or_base_produces_a_different_Hufu_approval_identity()
    {
        var original = RepositoryEffectJournalTests.CreateIntent();
        var changed = Marang.Runs.Effects.RepositoryEffectIntent.Create(original.EffectId, original.WorkflowId,
            original.RevisionId, original.LogicalStepId, original.ExecutionId, original.AttemptId,
            original.RepositoryId, original.AuthorityScope, original.BaseState,
            [Marang.Runs.Effects.RepositoryPatchFile.Create("src/a.txt", "before"u8, "candidate-b"u8)], original.CreatedAt);
        var context = new AuthenticatedAuthorityContext("tenant", "operator", original.WorkflowId.ToString("D"), original.RevisionId, "fence");
        var hufu = new RecordingAuthorizer();
        var authority = new Marang.Runs.Effects.RepositoryEffectAuthority(hufu, new ManifestVerifier(), "workspace");

        await authority.AuthorizeAsync(original, context, original.Files[0].Path, cancellationToken: TestContext.Current.CancellationToken);
        var originalIdentity = hufu.LastRequest!.RequestIdentity;
        await authority.AuthorizeAsync(changed, context, changed.Files[0].Path, cancellationToken: TestContext.Current.CancellationToken);
        hufu.LastRequest!.RequestIdentity.Should().NotBe(originalIdentity);
        hufu.LastRequest.RequestIdentity.Should().Be(WriteIdentity(changed, changed.Files[0].Path));
    }

    [Fact]
    public async Task Executor_replay_recognizes_applied_effect_and_does_not_write_twice()
    {
        using var checkout = EffectCheckout.Create();
        using var database = new TemporaryDatabase();
        var repository = ControlledRepository.Open(checkout.Root, ["src/a.txt", "src/b.txt"]);
        var intent = await CreateCheckoutIntent(repository, TestContext.Current.CancellationToken);
        var journal = new Marang.Runs.Effects.SqliteRepositoryEffectJournal(
            new Penghou.Zhinu.Sqlite.SqliteDatabase(new Penghou.Zhinu.Sqlite.ZhinuSqliteOptions { DatabasePath = database.Path, Pooling = false }));
        await journal.InitializeAsync(TestContext.Current.CancellationToken);
        var authCalls = new RecordingAuthorizer();
        var executor = new Marang.Runs.Effects.RepositoryEffectExecutor(repository, journal,
            new Marang.Runs.Effects.RepositoryEffectAuthority(authCalls, new ManifestVerifier(), "workspace"));
        var physicalFiles = new List<int>();
        ValueTask Checkpoint(Marang.Runs.Effects.RepositoryEffectExecutionCheckpoint checkpoint, CancellationToken _)
        {
            if (checkpoint.Kind == Marang.Runs.Effects.RepositoryEffectExecutionCheckpointKind.AfterPhysicalFile)
                physicalFiles.Add(checkpoint.FileIndex!.Value);
            return ValueTask.CompletedTask;
        }

        var applied = await executor.ExecuteAsync(intent, Context(intent), checkpoint: Checkpoint,
            cancellationToken: TestContext.Current.CancellationToken);
        applied.Classification.Should().Be(Marang.Runs.Effects.RepositoryEffectClassification.DefinitelyApplied, $"reason was {applied.ReasonCode}; stage {applied.Record.Stage}");
        applied.MutationPerformed.Should().BeTrue();
        physicalFiles.Should().Equal(1, 2);

        var replay = await executor.ExecuteAsync(intent, Context(intent), checkpoint: Checkpoint,
            cancellationToken: TestContext.Current.CancellationToken);
        replay.Classification.Should().Be(Marang.Runs.Effects.RepositoryEffectClassification.DefinitelyApplied);
        replay.MutationPerformed.Should().BeFalse();
        physicalFiles.Should().Equal(1, 2);
        File.ReadAllText(Path.Combine(checkout.Root, "src", "a.txt")).Should().Be("new-a");
        File.ReadAllText(Path.Combine(checkout.Root, "src", "b.txt")).Should().Be("new-b");
    }

    [Fact]
    public async Task Executor_blocks_partial_state_after_restart_instead_of_replaying_patch()
    {
        using var checkout = EffectCheckout.Create();
        using var database = new TemporaryDatabase();
        var repository = ControlledRepository.Open(checkout.Root, ["src/a.txt", "src/b.txt"]);
        var intent = await CreateCheckoutIntent(repository, TestContext.Current.CancellationToken);
        var journal = new Marang.Runs.Effects.SqliteRepositoryEffectJournal(
            new Penghou.Zhinu.Sqlite.SqliteDatabase(new Penghou.Zhinu.Sqlite.ZhinuSqliteOptions { DatabasePath = database.Path, Pooling = false }));
        await journal.InitializeAsync(TestContext.Current.CancellationToken);
        var executor = new Marang.Runs.Effects.RepositoryEffectExecutor(repository, journal,
            new Marang.Runs.Effects.RepositoryEffectAuthority(new RecordingAuthorizer(), new ManifestVerifier(), "workspace"));
        ValueTask Crash(Marang.Runs.Effects.RepositoryEffectExecutionCheckpoint checkpoint, CancellationToken _)
        {
            if (checkpoint.Kind == Marang.Runs.Effects.RepositoryEffectExecutionCheckpointKind.AfterPhysicalFile && checkpoint.FileIndex == 1)
                throw new SimulatedCrashException();
            return ValueTask.CompletedTask;
        }
        var firstAttempt = () => executor.ExecuteAsync(intent, Context(intent), checkpoint: Crash,
            cancellationToken: TestContext.Current.CancellationToken);
        await firstAttempt.Should().ThrowAsync<SimulatedCrashException>();
        File.ReadAllText(Path.Combine(checkout.Root, "src", "a.txt")).Should().Be("new-a");
        File.ReadAllText(Path.Combine(checkout.Root, "src", "b.txt")).Should().Be("old-b");

        var restartedExecutor = new Marang.Runs.Effects.RepositoryEffectExecutor(repository, journal,
            new Marang.Runs.Effects.RepositoryEffectAuthority(new RecordingAuthorizer(), new ManifestVerifier(), "workspace"));
        var recovered = await restartedExecutor.ExecuteAsync(intent, Context(intent),
            cancellationToken: TestContext.Current.CancellationToken);
        recovered.Classification.Should().Be(Marang.Runs.Effects.RepositoryEffectClassification.PartiallyApplied);
        recovered.Record.Stage.Should().Be(Marang.Runs.Effects.RepositoryEffectStage.Blocked);
        File.ReadAllText(Path.Combine(checkout.Root, "src", "a.txt")).Should().Be("new-a");
        File.ReadAllText(Path.Combine(checkout.Root, "src", "b.txt")).Should().Be("old-b");
    }

    [Fact]
    public async Task Fresh_revocation_after_durable_intent_prevents_first_physical_write()
    {
        using var checkout = EffectCheckout.Create();
        using var database = new TemporaryDatabase();
        var repository = ControlledRepository.Open(checkout.Root, ["src/a.txt", "src/b.txt"]);
        var intent = await CreateCheckoutIntent(repository, TestContext.Current.CancellationToken);
        var journal = new Marang.Runs.Effects.SqliteRepositoryEffectJournal(
            new Penghou.Zhinu.Sqlite.SqliteDatabase(new Penghou.Zhinu.Sqlite.ZhinuSqliteOptions { DatabasePath = database.Path, Pooling = false }));
        await journal.InitializeAsync(TestContext.Current.CancellationToken);
        var hufu = new RecordingAuthorizer();
        var executor = new RepositoryEffectExecutor(repository, journal,
            new RepositoryEffectAuthority(hufu, new ManifestVerifier(), "workspace"));
        ValueTask RevokeAfterCommit(RepositoryEffectExecutionCheckpoint checkpoint, CancellationToken _)
        {
            if (checkpoint.Kind == RepositoryEffectExecutionCheckpointKind.AfterIntentCommit) hufu.Allowed = false;
            return ValueTask.CompletedTask;
        }

        var result = await executor.ExecuteAsync(intent, Context(intent), checkpoint: RevokeAfterCommit,
            cancellationToken: TestContext.Current.CancellationToken);
        result.ReasonCode.Should().Be("repository-effect.hufu-denied");
        result.Record.Stage.Should().Be(RepositoryEffectStage.IntentCommitted);
        File.ReadAllText(Path.Combine(checkout.Root, "src", "a.txt")).Should().Be("old-a");
        File.ReadAllText(Path.Combine(checkout.Root, "src", "b.txt")).Should().Be("old-b");
    }

    [Fact]
    public async Task Outstanding_unapplied_intent_from_another_workflow_blocks_same_repository_scope()
    {
        using var checkout = EffectCheckout.Create();
        using var database = new TemporaryDatabase();
        var repository = ControlledRepository.Open(checkout.Root, ["src/a.txt", "src/b.txt"]);
        var journal = new SqliteRepositoryEffectJournal(new Penghou.Zhinu.Sqlite.SqliteDatabase(
            new Penghou.Zhinu.Sqlite.ZhinuSqliteOptions { DatabasePath = database.Path, Pooling = false }));
        await journal.InitializeAsync(TestContext.Current.CancellationToken);
        var firstIntent = await CreateCheckoutIntent(repository, TestContext.Current.CancellationToken);
        var firstAuth = new RecordingAuthorizer();
        var firstExecutor = new RepositoryEffectExecutor(repository, journal,
            new RepositoryEffectAuthority(firstAuth, new ManifestVerifier(), "workspace"));
        ValueTask RevokeAfterCommit(RepositoryEffectExecutionCheckpoint checkpoint, CancellationToken _)
        {
            if (checkpoint.Kind == RepositoryEffectExecutionCheckpointKind.AfterIntentCommit) firstAuth.Allowed = false;
            return ValueTask.CompletedTask;
        }
        var interrupted = await firstExecutor.ExecuteAsync(firstIntent, Context(firstIntent), checkpoint: RevokeAfterCommit,
            cancellationToken: TestContext.Current.CancellationToken);
        interrupted.Record.Stage.Should().Be(RepositoryEffectStage.IntentCommitted);
        interrupted.ReasonCode.Should().Be("repository-effect.hufu-denied");

        var secondIntent = RepositoryEffectIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "r1", "other-workflow", Guid.NewGuid(), "attempt-2",
            repository.RepositoryId, "workspace", firstIntent.BaseState,
            [RepositoryPatchFile.Create("src/b.txt", "old-b"u8, "candidate-b"u8)], DateTimeOffset.UtcNow);
        var secondExecutor = new RepositoryEffectExecutor(repository, journal,
            new RepositoryEffectAuthority(new RecordingAuthorizer(), new ManifestVerifier(), "workspace"));
        var blocked = await secondExecutor.ExecuteAsync(secondIntent, Context(secondIntent),
            cancellationToken: TestContext.Current.CancellationToken);
        blocked.ReasonCode.Should().Be("repository-effect.prior-effect-reconciliation-required");
        blocked.Record.Stage.Should().Be(RepositoryEffectStage.Blocked);
        File.ReadAllText(Path.Combine(checkout.Root, "src", "a.txt")).Should().Be("old-a");
        File.ReadAllText(Path.Combine(checkout.Root, "src", "b.txt")).Should().Be("old-b");
    }

    [Fact]
    public async Task Partial_effect_blocks_new_candidate_even_when_its_base_matches_current_partial_snapshot()
    {
        using var checkout = EffectCheckout.Create();
        using var database = new TemporaryDatabase();
        var repository = ControlledRepository.Open(checkout.Root, ["src/a.txt", "src/b.txt"]);
        var journal = new SqliteRepositoryEffectJournal(new Penghou.Zhinu.Sqlite.SqliteDatabase(
            new Penghou.Zhinu.Sqlite.ZhinuSqliteOptions { DatabasePath = database.Path, Pooling = false }));
        await journal.InitializeAsync(TestContext.Current.CancellationToken);
        var firstIntent = await CreateCheckoutIntent(repository, TestContext.Current.CancellationToken);
        var executor = new RepositoryEffectExecutor(repository, journal,
            new RepositoryEffectAuthority(new RecordingAuthorizer(), new ManifestVerifier(), "workspace"));
        ValueTask CrashAfterFirstFile(RepositoryEffectExecutionCheckpoint checkpoint, CancellationToken _)
        {
            if (checkpoint.Kind == RepositoryEffectExecutionCheckpointKind.AfterPhysicalFile && checkpoint.FileIndex == 1)
                throw new SimulatedCrashException();
            return ValueTask.CompletedTask;
        }
        var firstAttempt = () => executor.ExecuteAsync(firstIntent, Context(firstIntent), checkpoint: CrashAfterFirstFile,
            cancellationToken: TestContext.Current.CancellationToken);
        await firstAttempt.Should().ThrowAsync<SimulatedCrashException>();
        var partialBase = await repository.ObserveAsync(TestContext.Current.CancellationToken);
        var candidate = RepositoryEffectIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "r1", "new-workflow", Guid.NewGuid(), "attempt-2",
            repository.RepositoryId, "workspace", partialBase,
            [RepositoryPatchFile.Create("src/b.txt", "old-b"u8, "candidate-b"u8)], DateTimeOffset.UtcNow);
        var newExecutor = new RepositoryEffectExecutor(repository, journal,
            new RepositoryEffectAuthority(new RecordingAuthorizer(), new ManifestVerifier(), "workspace"));
        var blocked = await newExecutor.ExecuteAsync(candidate, Context(candidate), cancellationToken: TestContext.Current.CancellationToken);
        blocked.ReasonCode.Should().Be("repository-effect.prior-effect-reconciliation-required");
        blocked.Classification.Should().Be(RepositoryEffectClassification.DefinitelyNotApplied);
        blocked.Record.Stage.Should().Be(RepositoryEffectStage.Blocked);
        File.ReadAllText(Path.Combine(checkout.Root, "src", "a.txt")).Should().Be("new-a");
        File.ReadAllText(Path.Combine(checkout.Root, "src", "b.txt")).Should().Be("old-b");
    }

    [Fact]
    public async Task Dirty_initial_seed_is_rejected_before_any_effect_write()
    {
        using var checkout = EffectCheckout.Create();
        using var database = new TemporaryDatabase();
        await File.WriteAllTextAsync(Path.Combine(checkout.Root, "src", "a.txt"), "dirty-a", TestContext.Current.CancellationToken);
        var repository = ControlledRepository.Open(checkout.Root, ["src/a.txt", "src/b.txt"]);
        var intent = RepositoryEffectIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "r1", "dirty-seed", Guid.NewGuid(), "attempt",
            repository.RepositoryId, "workspace", await repository.ObserveAsync(TestContext.Current.CancellationToken),
            [RepositoryPatchFile.Create("src/a.txt", "dirty-a"u8, "new-a"u8)], DateTimeOffset.UtcNow);
        var journal = new SqliteRepositoryEffectJournal(new Penghou.Zhinu.Sqlite.SqliteDatabase(
            new Penghou.Zhinu.Sqlite.ZhinuSqliteOptions { DatabasePath = database.Path, Pooling = false }));
        await journal.InitializeAsync(TestContext.Current.CancellationToken);
        var executor = new RepositoryEffectExecutor(repository, journal,
            new RepositoryEffectAuthority(new RecordingAuthorizer(), new ManifestVerifier(), "workspace"));
        var result = await executor.ExecuteAsync(intent, Context(intent), cancellationToken: TestContext.Current.CancellationToken);
        result.ReasonCode.Should().Be("repository-effect.initial-state-not-clean");
        result.Classification.Should().Be(RepositoryEffectClassification.Unresolved);
        result.Record.Stage.Should().Be(RepositoryEffectStage.Blocked);
        File.ReadAllText(Path.Combine(checkout.Root, "src", "a.txt")).Should().Be("dirty-a");
        File.ReadAllText(Path.Combine(checkout.Root, "src", "b.txt")).Should().Be("old-b");
    }

    [Fact]
    public async Task Executor_rejects_changed_base_before_writing()
    {
        using var checkout = EffectCheckout.Create();
        using var database = new TemporaryDatabase();
        var repository = ControlledRepository.Open(checkout.Root, ["src/a.txt", "src/b.txt"]);
        var intent = await CreateCheckoutIntent(repository, TestContext.Current.CancellationToken);
        var journal = new Marang.Runs.Effects.SqliteRepositoryEffectJournal(
            new Penghou.Zhinu.Sqlite.SqliteDatabase(new Penghou.Zhinu.Sqlite.ZhinuSqliteOptions { DatabasePath = database.Path, Pooling = false }));
        await journal.InitializeAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(checkout.Root, "src", "a.txt"), "changed-before-run", TestContext.Current.CancellationToken);
        var executor = new RepositoryEffectExecutor(repository, journal,
            new RepositoryEffectAuthority(new RecordingAuthorizer(), new ManifestVerifier(), "workspace"));
        var result = await executor.ExecuteAsync(intent, Context(intent), cancellationToken: TestContext.Current.CancellationToken);
        result.Classification.Should().Be(RepositoryEffectClassification.Unresolved);
        result.Record.Stage.Should().Be(RepositoryEffectStage.Blocked);
        File.ReadAllText(Path.Combine(checkout.Root, "src", "a.txt")).Should().Be("changed-before-run");
        File.ReadAllText(Path.Combine(checkout.Root, "src", "b.txt")).Should().Be("old-b");
    }

    [Fact]
    public async Task Validation_digest_mismatch_or_repository_change_is_blocked()
    {
        using var checkout = EffectCheckout.Create();
        using var database = new TemporaryDatabase();
        var repository = ControlledRepository.Open(checkout.Root, ["src/a.txt", "src/b.txt"]);
        var intent = await CreateCheckoutIntent(repository, TestContext.Current.CancellationToken);
        var journal = new Marang.Runs.Effects.SqliteRepositoryEffectJournal(
            new Penghou.Zhinu.Sqlite.SqliteDatabase(new Penghou.Zhinu.Sqlite.ZhinuSqliteOptions { DatabasePath = database.Path, Pooling = false }));
        await journal.InitializeAsync(TestContext.Current.CancellationToken);
        var executor = new RepositoryEffectExecutor(repository, journal,
            new RepositoryEffectAuthority(new RecordingAuthorizer(), new ManifestVerifier(), "workspace"));
        var mismatched = await executor.ExecuteAsync(intent, Context(intent), validate: (state, _) =>
            ValueTask.FromResult(new RepositoryEffectValidation("wrong-digest", "build", "build-test", true, "output", "passed")),
            cancellationToken: TestContext.Current.CancellationToken);
        mismatched.Classification.Should().Be(RepositoryEffectClassification.Unresolved);
        mismatched.Record.Stage.Should().Be(RepositoryEffectStage.Blocked);

        using var second = EffectCheckout.Create();
        var repository2 = ControlledRepository.Open(second.Root, ["src/a.txt", "src/b.txt"]);
        using var secondDb = new TemporaryDatabase();
        var intent2 = await CreateCheckoutIntent(repository2, TestContext.Current.CancellationToken);
        var journal2 = new SqliteRepositoryEffectJournal(new Penghou.Zhinu.Sqlite.SqliteDatabase(
            new Penghou.Zhinu.Sqlite.ZhinuSqliteOptions { DatabasePath = secondDb.Path, Pooling = false }));
        await journal2.InitializeAsync(TestContext.Current.CancellationToken);
        var executor2 = new RepositoryEffectExecutor(repository2, journal2,
            new RepositoryEffectAuthority(new RecordingAuthorizer(), new ManifestVerifier(), "workspace"));
        var changedDuringValidation = await executor2.ExecuteAsync(intent2, Context(intent2), validate: async (state, ct) =>
        {
            await File.WriteAllTextAsync(Path.Combine(second.Root, "src", "b.txt"), "changed-during-validation", ct);
            return new RepositoryEffectValidation(state.StateDigest, "build", "build-test", true, "output", "passed");
        }, cancellationToken: TestContext.Current.CancellationToken);
        changedDuringValidation.Classification.Should().Be(RepositoryEffectClassification.Unresolved);
        changedDuringValidation.Record.Stage.Should().Be(RepositoryEffectStage.Blocked);
        File.ReadAllText(Path.Combine(second.Root, "src", "b.txt")).Should().Be("changed-during-validation");
    }

    [Fact]
    public async Task Superseded_applied_effect_replay_preserves_its_physical_receipt_after_successor()
    {
        using var checkout = EffectCheckout.Create();
        using var database = new TemporaryDatabase();
        var repository = ControlledRepository.Open(checkout.Root, ["src/a.txt", "src/b.txt"]);
        var a = await CreateCheckoutIntent(repository, TestContext.Current.CancellationToken);
        var journal = new Marang.Runs.Effects.SqliteRepositoryEffectJournal(
            new Penghou.Zhinu.Sqlite.SqliteDatabase(new Penghou.Zhinu.Sqlite.ZhinuSqliteOptions { DatabasePath = database.Path, Pooling = false }));
        await journal.InitializeAsync(TestContext.Current.CancellationToken);
        var revisionVerifier = new RevisionManifestVerifier("r1");
        var aExecutor = new Marang.Runs.Effects.RepositoryEffectExecutor(repository, journal,
            new Marang.Runs.Effects.RepositoryEffectAuthority(new RecordingAuthorizer(), revisionVerifier, "workspace"));
        var failedValidation = new RepositoryEffectValidation("", "build", "build-test", false, "output", "failed");
        var aResult = await aExecutor.ExecuteAsync(a, Context(a), validate: (state, _) =>
            ValueTask.FromResult(failedValidation with { TargetStateDigest = state.StateDigest }),
            cancellationToken: TestContext.Current.CancellationToken);
        aResult.Record.Stage.Should().Be(RepositoryEffectStage.ValidationFailed, $"reason was {aResult.ReasonCode}");
        var aPost = aResult.Record.ObservedState!;

        var b = RepositoryEffectIntent.Create(Guid.NewGuid(), a.WorkflowId, "r2", "correct", Guid.NewGuid(), "attempt-b",
            repository.RepositoryId, "workspace", aPost,
            [RepositoryPatchFile.Create("src/a.txt", "new-a"u8, "corrected-a"u8)], DateTimeOffset.UtcNow);
        var bExecutor = new Marang.Runs.Effects.RepositoryEffectExecutor(repository, journal,
            new Marang.Runs.Effects.RepositoryEffectAuthority(new RecordingAuthorizer(), new RevisionManifestVerifier("r2"), "workspace"));
        var bResult = await bExecutor.ExecuteAsync(b, Context(b), cancellationToken: TestContext.Current.CancellationToken);
        bResult.Classification.Should().Be(RepositoryEffectClassification.DefinitelyApplied);
        revisionVerifier.ActiveRevision = "r2";

        var filesWritten = 0;
        ValueTask CountWrites(RepositoryEffectExecutionCheckpoint checkpoint, CancellationToken _)
        {
            if (checkpoint.Kind == RepositoryEffectExecutionCheckpointKind.AfterPhysicalFile) filesWritten++;
            return ValueTask.CompletedTask;
        }
        var historical = await aExecutor.ExecuteAsync(a, Context(a), checkpoint: CountWrites,
            cancellationToken: TestContext.Current.CancellationToken);
        historical.ReasonCode.Should().Be("repository-effect.historical-applied-then-corrected");
        historical.MutationPerformed.Should().BeFalse();
        filesWritten.Should().Be(0);
        historical.Record.Stage.Should().Be(RepositoryEffectStage.ValidationFailed);
        historical.Record.ObservedState!.StateDigest.Should().Be(aPost.StateDigest);
        (await journal.ListEventsAsync(a.EffectId, TestContext.Current.CancellationToken))
            .Last().ObservedState!.StateDigest.Should().Be(aPost.StateDigest);
    }

    private sealed class ManifestVerifier : Marang.Runs.Effects.IRepositoryEffectManifestVerifier
    {
        public bool Admitted { get; set; } = true;
        public bool Current { get; set; } = true;
        public ValueTask<bool> IsAdmittedAsync(Marang.Runs.Effects.RepositoryEffectIntent intent, CancellationToken cancellationToken = default) => new(Admitted);
        public ValueTask<bool> IsCurrentAsync(Marang.Runs.Effects.RepositoryEffectIntent intent, CancellationToken cancellationToken = default) => new(Current);
    }

    private sealed class RevisionManifestVerifier(string activeRevision) : IRepositoryEffectManifestVerifier
    {
        public string ActiveRevision { get; set; } = activeRevision;
        public ValueTask<bool> IsAdmittedAsync(RepositoryEffectIntent intent, CancellationToken cancellationToken = default) => new(true);
        public ValueTask<bool> IsCurrentAsync(RepositoryEffectIntent intent, CancellationToken cancellationToken = default) => new(intent.RevisionId == ActiveRevision);
    }

    private sealed class RecordingAuthorizer : IAuthorityRequestAuthorizer, IDisposable
    {
        public AuthorityRequest? LastRequest { get; private set; }
        public bool Allowed { get; set; } = true;
        public int Calls { get; private set; }
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "marang-recording-hufu-" + Guid.NewGuid().ToString("N"));
        private readonly AuthorityStoreActor _actor = new("tenant", "host", "session");
        private readonly FixedTimeProvider _time = new(DateTimeOffset.UtcNow);
        private long _version;
        private int _sequence;

        public async ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(AuthorityRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            Calls++;
            Directory.CreateDirectory(_directory);
            var store = new SqliteAuthorityStore(System.IO.Path.Combine(_directory, "authority.db"), new AllowStorePolicy(), _time);
            var now = _time.GetUtcNow();
            var target = Allowed ? request.RelativePath : "src/revoked.txt";
            var grants = new[] { new AuthorityGrant("write-" + Calls, [AuthorityAction.WriteFile],
                new AuthorityScope(request.WorkspaceId, target, AuthorityScopeKind.Exact), [], now.AddMinutes(-1), now.AddHours(1)) };
            var snapshot = new AuthoritySnapshot(request.Context, (_version + 1).ToString(),
                [new AuthorityLayer("current", grants)], [], now.AddHours(1));
            var publish = await store.PublishAsync(new AuthorityPublishCommand("publish-" + (++_sequence), _actor, snapshot, _version), cancellationToken);
            if (publish.Status != AuthorityMutationStatus.Applied) throw new InvalidOperationException("Could not publish test Hufu authority.");
            _version++;
            var current = new CurrentAuthorityRequestAuthorizer(new AuthorityStoreSnapshotSource(store, _actor),
                new CedarAuthorityEvaluator(), new AllowDecisionRecorder(), _time);
            return await current.AuthorizeAsync(request, cancellationToken);
        }

        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }

    private static string WriteIdentity(Marang.Runs.Effects.RepositoryEffectIntent intent, string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"marang-repository-write-authority-v1\n{intent.IntentDigest}\n{path}\nReplaceTrackedFile"))).ToLowerInvariant();

    private static async Task<RepositoryEffectIntent> CreateCheckoutIntent(ControlledRepository repository, CancellationToken ct)
    {
        var basis = await repository.ObserveAsync(ct);
        return RepositoryEffectIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "r1", "implement", Guid.NewGuid(), "attempt",
            repository.RepositoryId, "workspace", basis,
            [RepositoryPatchFile.Create("src/a.txt", "old-a"u8, "new-a"u8),
             RepositoryPatchFile.Create("src/b.txt", "old-b"u8, "new-b"u8)], DateTimeOffset.UtcNow);
    }

    private static AuthenticatedAuthorityContext Context(RepositoryEffectIntent intent) =>
        new("tenant", "operator", intent.WorkflowId.ToString("D"), intent.RevisionId, "fence");

    private sealed class SimulatedCrashException : Exception;

    private sealed class EffectCheckout : IDisposable
    {
        public string Root { get; }
        private EffectCheckout(string root) => Root = root;
        public static EffectCheckout Create()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "marang-effect-checkout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(root, "src"));
            File.WriteAllText(System.IO.Path.Combine(root, "src", "a.txt"), "old-a");
            File.WriteAllText(System.IO.Path.Combine(root, "src", "b.txt"), "old-b");
            RunGit(root, "init");
            RunGit(root, "config", "user.email", "test@example.invalid");
            RunGit(root, "config", "user.name", "Marang Tests");
            RunGit(root, "add", "src/a.txt", "src/b.txt");
            RunGit(root, "commit", "-m", "initial");
            return new(root);
        }
        private static void RunGit(string root, params string[] args)
        {
            using var process = new Process { StartInfo = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {output}{error}");
        }
        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(entry, FileAttributes.Normal); } catch (IOException) { }
            try { Directory.Delete(Root, true); } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class AllowStorePolicy : IAuthorityStoreAuthorizer
    {
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default) => new(new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
    }

    private sealed class AllowDecisionRecorder : IAuthorityDecisionRecorder
    {
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision,
            CancellationToken cancellationToken = default) => new(true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "marang-effect-authority-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "authority.db");
        public TemporaryDatabase() => Directory.CreateDirectory(_directory);
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
