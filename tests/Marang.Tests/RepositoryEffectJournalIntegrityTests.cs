using System.Text.Json.Nodes;
using FluentAssertions;
using Marang.Runs.Effects;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Sqlite;

namespace Marang.Tests;

public sealed class RepositoryEffectJournalIntegrityTests
{
    [Theory]
    [InlineData("effect_id")]
    [InlineData("workflow_id")]
    [InlineData("repository_id")]
    [InlineData("intent_digest")]
    [InlineData("stage")]
    [InlineData("updated_at")]
    public async Task Projection_columns_must_match_the_serialized_record(string column)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.MutateAsync($"UPDATE repository_effects SET {column}='corrupt' WHERE effect_id=$id");
        await fixture.ExpectCorruptReadsAsync();
    }

    [Fact]
    public async Task Event_digest_must_match_its_document()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.MutateAsync("UPDATE repository_effect_events SET event_digest='bad' WHERE effect_id=$id");
        await fixture.ExpectCorruptReadsAsync();
    }

    [Fact]
    public async Task Event_document_must_be_valid_json()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.MutateEventDocumentAsync("{");
        await fixture.ExpectCorruptReadsAsync();
    }

    [Fact]
    public async Task Missing_projection_with_events_is_corrupt()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.MutateAsync("DELETE FROM repository_effects WHERE effect_id=$id");
        await fixture.ExpectCorruptReadsAsync();
    }

    [Fact]
    public async Task Current_projection_must_match_last_append_only_event()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.MutateDocumentAsync(json =>
        {
            var node = JsonNode.Parse(json)!;
            node["stage"] = (int)RepositoryEffectStage.Blocked;
            return node.ToJsonString();
        });
        await fixture.MutateAsync("UPDATE repository_effects SET stage='Blocked' WHERE effect_id=$id");
        await fixture.ExpectCorruptReadsAsync();
    }

    [Fact]
    public async Task Deleted_event_history_with_projection_is_corrupt()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.MutateAsync("DELETE FROM repository_effect_events WHERE effect_id=$id");
        await fixture.ExpectCorruptReadsAsync();
    }

    [Fact]
    public async Task Unknown_persisted_stage_is_corrupt()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.MutateDocumentAsync(json =>
        {
            var node = JsonNode.Parse(json)!;
            node["stage"] = 999;
            return node.ToJsonString();
        });
        await fixture.ExpectCorruptReadsAsync();
    }

    [Fact]
    public async Task Invalid_expected_post_state_digest_is_corrupt()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.MutateDocumentAsync(json =>
        {
            var node = JsonNode.Parse(json)!;
            node["intent"]!["expectedPostState"]!["stateDigest"] = "invalid";
            return node.ToJsonString();
        });
        await fixture.ExpectCorruptReadsAsync();
    }

    [Fact]
    public async Task Invalid_validation_output_digest_is_corrupt()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SetStateAsync("ValidationFailed");
        await fixture.MutateDocumentAsync(json =>
        {
            var node = JsonNode.Parse(json)!;
            node["validation"]!["outputDigest"] = "invalid";
            return node.ToJsonString();
        });
        await fixture.ExpectCorruptReadsAsync();
    }

    [Theory]
    [InlineData("Applying")]
    [InlineData("Blocked")]
    [InlineData("ValidationFailed")]
    public async Task Healthy_m5_lifecycle_states_remain_readable(string target)
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SetStateAsync(target);
        var record = await fixture.journal.GetAsync(fixture.Intent.EffectId, TestContext.Current.CancellationToken);
        record.Should().NotBeNull();
        record!.Stage.ToString().Should().Be(target);
        (await fixture.journal.ListForWorkflowAsync(fixture.Intent.WorkflowId, TestContext.Current.CancellationToken)).Should().ContainSingle();
        (await fixture.journal.ListForRepositoryAsync(fixture.Intent.RepositoryId, TestContext.Current.CancellationToken)).Should().ContainSingle();
        (await fixture.journal.ListEventsAsync(fixture.Intent.EffectId, TestContext.Current.CancellationToken)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Healthy_event_authority_deltas_accumulate_to_the_projection()
    {
        using var fixture = await Fixture.CreateAsync();
        var first = Evidence(fixture.Intent, "first");
        var second = Evidence(fixture.Intent, "second");
        await fixture.journal.TransitionAsync(fixture.Intent.EffectId, RepositoryEffectStage.Blocked,
            RepositoryEffectClassification.Unresolved, cancellationToken: TestContext.Current.CancellationToken,
            authorityEvidence: [first]);
        await fixture.journal.TransitionAsync(fixture.Intent.EffectId, RepositoryEffectStage.Blocked,
            RepositoryEffectClassification.Unresolved, cancellationToken: TestContext.Current.CancellationToken,
            authorityEvidence: [second]);

        var record = await fixture.journal.GetAsync(fixture.Intent.EffectId, TestContext.Current.CancellationToken);
        record!.AuthorityEvidence.Should().Equal(first, second);
        (await fixture.journal.ListEventsAsync(fixture.Intent.EffectId, TestContext.Current.CancellationToken))
            .Select(e => e.AuthorityEvidence.Count).Should().Equal(0, 1, 1);
    }

    private static RepositoryEffectAuthorizationEvidence Evidence(RepositoryEffectIntent intent, string request) =>
        new(intent.IntentDigest, "src/a.txt", request, "Permit", "tenant", "subject", intent.WorkflowId.ToString("D"),
            intent.RevisionId, null, null, null, DateTimeOffset.Parse("2026-10-14T00:00:00Z"));

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "marang-effect-integrity-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(directory, "runs.db");
        public readonly RepositoryEffectIntent Intent = RepositoryEffectJournalTests.CreateIntent();
        public SqliteRepositoryEffectJournal journal = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.directory);
            fixture.journal = new SqliteRepositoryEffectJournal(new SqliteDatabase(new ZhinuSqliteOptions { DatabasePath = fixture.DatabasePath, Pooling = false }));
            await fixture.journal.InitializeAsync(TestContext.Current.CancellationToken);
            await fixture.journal.PrepareAsync(fixture.Intent, TestContext.Current.CancellationToken);
            return fixture;
        }

        public async Task MutateAsync(string sql)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", Intent.EffectId.ToString("D"));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        public async Task MutateDocumentAsync(Func<string, string> mutate)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var select = connection.CreateCommand();
            select.CommandText = "SELECT document FROM repository_effects WHERE effect_id=$id";
            select.Parameters.AddWithValue("$id", Intent.EffectId.ToString("D"));
            var old = (string)(await select.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE repository_effects SET document=$doc WHERE effect_id=$id";
            update.Parameters.AddWithValue("$id", Intent.EffectId.ToString("D"));
            update.Parameters.AddWithValue("$doc", mutate(old));
            await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        public async Task MutateEventDocumentAsync(string document)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE repository_effect_events SET document=$doc,event_digest=$digest WHERE effect_id=$id";
            update.Parameters.AddWithValue("$id", Intent.EffectId.ToString("D"));
            update.Parameters.AddWithValue("$doc", document);
            update.Parameters.AddWithValue("$digest", RepositoryStateSnapshot.Hash(document));
            await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        public async Task SetStateAsync(string target)
        {
            var ct = TestContext.Current.CancellationToken;
            if (target == "Blocked")
            {
                await journal.TransitionAsync(Intent.EffectId, RepositoryEffectStage.Blocked,
                    RepositoryEffectClassification.Unresolved, cancellationToken: ct);
                return;
            }
            await journal.TransitionAsync(Intent.EffectId, RepositoryEffectStage.IntentCommitted, cancellationToken: ct);
            await journal.TransitionAsync(Intent.EffectId, RepositoryEffectStage.Applying, cancellationToken: ct);
            if (target == "Applying") return;
            var observed = Intent.ExpectedPostState;
            await journal.TransitionAsync(Intent.EffectId, RepositoryEffectStage.AppliedObserved,
                RepositoryEffectClassification.DefinitelyApplied, observed, cancellationToken: ct);
            var validation = new RepositoryEffectValidation(observed.StateDigest, "test", "command", false,
                RepositoryStateSnapshot.Hash("failure-output"), "expected negative control");
            await journal.TransitionAsync(Intent.EffectId, RepositoryEffectStage.ValidationFailed,
                RepositoryEffectClassification.DefinitelyApplied, validation: validation, cancellationToken: ct);
        }

        public async Task ExpectCorruptReadsAsync()
        {
            var before = await File.ReadAllBytesAsync(DatabasePath, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => journal.GetAsync(Intent.EffectId, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(() => journal.ListForWorkflowAsync(Intent.WorkflowId, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(() => journal.ListForRepositoryAsync(Intent.RepositoryId, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(() => journal.ListEventsAsync(Intent.EffectId, TestContext.Current.CancellationToken));
            (await File.ReadAllBytesAsync(DatabasePath, TestContext.Current.CancellationToken)).Should().Equal(before,
                "integrity refusals must preserve the source database bytes");
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
