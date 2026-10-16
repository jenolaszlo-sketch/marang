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
    public async Task Validation_output_identity_must_match_history()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SetStateAsync("ValidationFailed");
        await fixture.MutateDocumentAsync(json =>
        {
            var node = JsonNode.Parse(json)!;
            node["validation"]!["outputDigest"] = "changed-opaque-identity";
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

    [Fact]
    public async Task Healthy_global_sequence_allows_interleaved_effect_histories()
    {
        using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var second = RepositoryEffectJournalTests.CreateIntent();
        await fixture.journal.PrepareAsync(second, ct); // global sequence 2
        await fixture.journal.TransitionAsync(fixture.Intent.EffectId, RepositoryEffectStage.IntentCommitted, cancellationToken: ct); // 3
        await fixture.journal.TransitionAsync(second.EffectId, RepositoryEffectStage.IntentCommitted, cancellationToken: ct); // 4
        await fixture.journal.TransitionAsync(fixture.Intent.EffectId, RepositoryEffectStage.Applying, cancellationToken: ct); // 5

        (await fixture.journal.GetAsync(fixture.Intent.EffectId, ct)).Should().NotBeNull();
        (await fixture.journal.GetAsync(second.EffectId, ct)).Should().NotBeNull();
        (await fixture.journal.ListEventsAsync(fixture.Intent.EffectId, ct)).Select(e => e.Stage)
            .Should().Equal(RepositoryEffectStage.Prepared, RepositoryEffectStage.IntentCommitted, RepositoryEffectStage.Applying);
        (await fixture.journal.ListEventsAsync(second.EffectId, ct)).Select(e => e.Stage)
            .Should().Equal(RepositoryEffectStage.Prepared, RepositoryEffectStage.IntentCommitted);
    }

    [Fact]
    public async Task Deleted_empty_authority_applying_event_is_corrupt_even_when_stage_skip_is_legal()
    {
        using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        await fixture.journal.TransitionAsync(fixture.Intent.EffectId, RepositoryEffectStage.IntentCommitted, cancellationToken: ct);
        await fixture.journal.TransitionAsync(fixture.Intent.EffectId, RepositoryEffectStage.Applying, cancellationToken: ct);
        await fixture.journal.TransitionAsync(fixture.Intent.EffectId, RepositoryEffectStage.AppliedObserved,
            RepositoryEffectClassification.DefinitelyApplied, fixture.Intent.ExpectedPostState, cancellationToken: ct);
        await fixture.DeleteEventByStageAsync(RepositoryEffectStage.Applying);
        await fixture.ExpectCorruptReadsAsync();
    }

    [Fact]
    public async Task Deleted_tail_event_is_corrupt_even_when_projection_is_rewound_consistently()
    {
        using var fixture = await Fixture.CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var preparedDocument = await fixture.ReadProjectionDocumentAsync();
        await fixture.journal.TransitionAsync(fixture.Intent.EffectId, RepositoryEffectStage.IntentCommitted, cancellationToken: ct);
        await fixture.DeleteTailAndRestoreProjectionAsync(preparedDocument);
        await fixture.ExpectCorruptReadsAsync();
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

        public async Task DeleteEventByStageAsync(RepositoryEffectStage stage)
        {
            var ct = TestContext.Current.CancellationToken;
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(ct);
            long? sequence = null;
            await using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT sequence,document FROM repository_effect_events WHERE effect_id=$id ORDER BY sequence";
                select.Parameters.AddWithValue("$id", Intent.EffectId.ToString("D"));
                await using var reader = await select.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var node = JsonNode.Parse(reader.GetString(1));
                    if (node?["stage"]?.GetValue<int>() == (int)stage)
                    {
                        sequence = reader.GetInt64(0);
                        break;
                    }
                }
            }
            sequence.Should().NotBeNull("the fixture must contain the requested stage before corruption");
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM repository_effect_events WHERE sequence=$sequence";
            delete.Parameters.AddWithValue("$sequence", sequence!.Value);
            (await delete.ExecuteNonQueryAsync(ct)).Should().Be(1);
        }

        public async Task<string> ReadProjectionDocumentAsync()
        {
            var ct = TestContext.Current.CancellationToken;
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT document FROM repository_effects WHERE effect_id=$id";
            command.Parameters.AddWithValue("$id", Intent.EffectId.ToString("D"));
            return (string)(await command.ExecuteScalarAsync(ct))!;
        }

        public async Task DeleteTailAndRestoreProjectionAsync(string preparedDocument)
        {
            var ct = TestContext.Current.CancellationToken;
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(ct);
            using var transaction = connection.BeginTransaction();
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM repository_effect_events WHERE sequence=(SELECT MAX(sequence) FROM repository_effect_events)";
                (await delete.ExecuteNonQueryAsync(ct)).Should().Be(1);
            }
            var restored = SqliteRepositoryEffectJournal.DeserializeRecord(preparedDocument);
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE repository_effects SET stage='Prepared',document=$document,updated_at=$updated WHERE effect_id=$id";
                update.Parameters.AddWithValue("$document", preparedDocument);
                update.Parameters.AddWithValue("$updated", restored.UpdatedAt.ToString("O"));
                update.Parameters.AddWithValue("$id", Intent.EffectId.ToString("D"));
                (await update.ExecuteNonQueryAsync(ct)).Should().Be(1);
            }
            transaction.Commit();
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
