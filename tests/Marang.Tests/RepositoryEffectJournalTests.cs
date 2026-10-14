using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Penghou.Zhinu.Sqlite;

namespace Marang.Tests;

public sealed class RepositoryEffectJournalTests
{
    [Fact]
    public async Task Intent_and_effect_lineage_survive_sqlite_reopen()
    {
        using var files = new TemporaryFiles();
        var database = new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = files.DatabasePath, Pooling = false });
        var journal = new Marang.Runs.Effects.SqliteRepositoryEffectJournal(database);
        await journal.InitializeAsync(TestContext.Current.CancellationToken);
        var intent = CreateIntent();
        await journal.PrepareAsync(intent, TestContext.Current.CancellationToken);
        await journal.TransitionAsync(intent.EffectId, Marang.Runs.Effects.RepositoryEffectStage.IntentCommitted,
            cancellationToken: TestContext.Current.CancellationToken);
        await journal.TransitionAsync(intent.EffectId, Marang.Runs.Effects.RepositoryEffectStage.Applying,
            cancellationToken: TestContext.Current.CancellationToken);

        var reopened = new Marang.Runs.Effects.SqliteRepositoryEffectJournal(
            new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = files.DatabasePath, Pooling = false }));
        await reopened.InitializeAsync(TestContext.Current.CancellationToken);
        var recovered = await reopened.GetAsync(intent.EffectId, TestContext.Current.CancellationToken);
        recovered.Should().NotBeNull();
        recovered!.Stage.Should().Be(Marang.Runs.Effects.RepositoryEffectStage.Applying);
        recovered.Intent.IntentDigest.Should().Be(intent.IntentDigest);
        (await reopened.ListEventsAsync(intent.EffectId, TestContext.Current.CancellationToken))
            .Select(e => e.Stage).Should().Equal(
                Marang.Runs.Effects.RepositoryEffectStage.Prepared,
                Marang.Runs.Effects.RepositoryEffectStage.IntentCommitted,
                Marang.Runs.Effects.RepositoryEffectStage.Applying);
        (await reopened.ListForWorkflowAsync(intent.WorkflowId, TestContext.Current.CancellationToken)).Should().ContainSingle();
        (await reopened.ListForRepositoryAsync(intent.RepositoryId, TestContext.Current.CancellationToken)).Should().ContainSingle();
    }

    [Fact]
    public async Task Effect_identity_cannot_be_rebound_and_lifecycle_cannot_skip_durability()
    {
        using var files = new TemporaryFiles();
        var journal = new Marang.Runs.Effects.SqliteRepositoryEffectJournal(
            new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = files.DatabasePath, Pooling = false }));
        await journal.InitializeAsync(TestContext.Current.CancellationToken);
        var intent = CreateIntent();
        await journal.PrepareAsync(intent, TestContext.Current.CancellationToken);
        var collision = Marang.Runs.Effects.RepositoryEffectIntent.Create(intent.EffectId, intent.WorkflowId,
            intent.RevisionId, intent.LogicalStepId, intent.ExecutionId, intent.AttemptId, intent.RepositoryId,
            intent.AuthorityScope, intent.BaseState,
            [Marang.Runs.Effects.RepositoryPatchFile.Create("src/a.txt", Encoding.UTF8.GetBytes("before"), Encoding.UTF8.GetBytes("different"))],
            intent.CreatedAt);
        var rebind = () => journal.PrepareAsync(collision, TestContext.Current.CancellationToken);
        await rebind.Should().ThrowAsync<InvalidOperationException>();
        var skip = () => journal.TransitionAsync(intent.EffectId, Marang.Runs.Effects.RepositoryEffectStage.Applying,
            cancellationToken: TestContext.Current.CancellationToken);
        await skip.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Applied_receipt_rejects_invalid_observation_and_non_post_state()
    {
        using var files = new TemporaryFiles();
        var journal = new Marang.Runs.Effects.SqliteRepositoryEffectJournal(
            new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = files.DatabasePath, Pooling = false }));
        await journal.InitializeAsync(TestContext.Current.CancellationToken);
        var intent = CreateIntent();
        await journal.PrepareAsync(intent, TestContext.Current.CancellationToken);
        await journal.TransitionAsync(intent.EffectId, Marang.Runs.Effects.RepositoryEffectStage.IntentCommitted,
            cancellationToken: TestContext.Current.CancellationToken);
        var invalid = intent.BaseState with { StateDigest = "tampered" };
        var badDigest = () => journal.TransitionAsync(intent.EffectId, Marang.Runs.Effects.RepositoryEffectStage.Blocked,
            Marang.Runs.Effects.RepositoryEffectClassification.Unresolved, invalid,
            cancellationToken: TestContext.Current.CancellationToken);
        await badDigest.Should().ThrowAsync<InvalidOperationException>();
        var falseApplied = () => journal.TransitionAsync(intent.EffectId, Marang.Runs.Effects.RepositoryEffectStage.AppliedObserved,
            Marang.Runs.Effects.RepositoryEffectClassification.DefinitelyApplied, intent.BaseState,
            cancellationToken: TestContext.Current.CancellationToken);
        await falseApplied.Should().ThrowAsync<InvalidOperationException>();
        (await journal.GetAsync(intent.EffectId, TestContext.Current.CancellationToken))!.Stage
            .Should().Be(Marang.Runs.Effects.RepositoryEffectStage.IntentCommitted);
    }

    public static Marang.Runs.Effects.RepositoryEffectIntent CreateIntent()
    {
        const string before = "before";
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(before))).ToLowerInvariant();
        var repositoryId = Marang.Runs.Effects.RepositoryStateSnapshot.Hash("repo-test");
        var state = Marang.Runs.Effects.RepositoryStateSnapshot.Create(repositoryId, new string('a', 40),
            Marang.Runs.Effects.RepositoryStateSnapshot.Hash("index"), [new("src/a.txt", sha)]);
        return Marang.Runs.Effects.RepositoryEffectIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "r1", "implement",
            Guid.NewGuid(), "attempt-1", repositoryId, "workspace", state,
            [Marang.Runs.Effects.RepositoryPatchFile.Create("src/a.txt", Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes("after"))],
            DateTimeOffset.Parse("2026-10-14T00:00:00Z"));
    }

    private sealed class TemporaryFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "marang-effect-journal-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(_directory, "runs.db");
        public TemporaryFiles() => Directory.CreateDirectory(_directory);
        public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
