using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Marang.Runs.Effects;
using Marang.Runs.Integrity;
using Microsoft.Data.Sqlite;

namespace Marang.Tests;

[Collection("RepositoryRecoveryIntegrity")]
public sealed class RepositoryProfileCompatibilityTests
{
    private const string ExpectedIdentity = "marang/bounded-repository/m6.1@5dce00ef8d7ca962e3de42f4a28b1e0d1e52fb29;zhinu-schema=6;effect-json=web-v1;profile=1;effect-record=1";
    private const string ExpectedDigest = "a6e86fc4202b9698db388948829e651a98c30c2c0ed4c0da73e822b7e36eb35a";

    [Fact]
    public void Qualified_contract_identity_has_the_independent_golden_digest()
    {
        RepositoryProfileCompatibility.ContractIdentity.Should().Be(ExpectedIdentity);
        RepositoryProfileCompatibility.ContractDigest.Should().Be(ExpectedDigest);
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RepositoryProfileCompatibility.ContractIdentity)))
            .ToLowerInvariant().Should().Be(ExpectedDigest);
    }

    [Fact]
    public async Task Legacy_inspector_accepts_but_current_admission_refuses_without_writing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).CanRecover.Should().BeTrue();
        var before = await CaptureStoreAsync(fixture.DatabasePath, ct);
        var bytes = await File.ReadAllBytesAsync(fixture.DatabasePath, ct);

        var action = () => RepositoryProfileCompatibility.RequireCurrentAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);
        await action.Should().ThrowAsync<InvalidDataException>();

        (await CaptureStoreAsync(fixture.DatabasePath, ct)).Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        (await File.ReadAllBytesAsync(fixture.DatabasePath, ct)).Should().Equal(bytes);
    }

    [Theory]
    [InlineData("initial")]
    [InlineData("pending")]
    [InlineData("committed")]
    public async Task Upgrade_preserves_every_preexisting_row_and_schema_except_owned_metadata(string state)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync(prepareProposal: state != "initial");
        if (state == "committed") await fixture.CommitAsync(ct);
        var before = await CaptureStoreAsync(fixture.DatabasePath, ct);

        (await RepositoryProfileCompatibility.UpgradeAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct))
            .Should().Be(RepositoryProfileUpgradeResult.Upgraded);

        var after = await CaptureStoreAsync(fixture.DatabasePath, ct);
        AssertOnlyProfileAdditions(before, after);
        await RepositoryProfileCompatibility.RequireCurrentAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).CanRecover.Should().BeTrue();
    }

    [Fact]
    public async Task Repeated_upgrade_is_idempotent_and_does_not_write_database_bytes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        (await RepositoryProfileCompatibility.UpgradeAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct))
            .Should().Be(RepositoryProfileUpgradeResult.Upgraded);
        var before = await File.ReadAllBytesAsync(fixture.DatabasePath, ct);
        var snapshot = await CaptureStoreAsync(fixture.DatabasePath, ct);

        (await RepositoryProfileCompatibility.UpgradeAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct))
            .Should().Be(RepositoryProfileUpgradeResult.AlreadyCurrent);

        (await File.ReadAllBytesAsync(fixture.DatabasePath, ct)).Should().Equal(before);
        (await CaptureStoreAsync(fixture.DatabasePath, ct)).Should().BeEquivalentTo(snapshot, options => options.WithStrictOrdering());
    }

    [Theory]
    [InlineData("AfterBegin")]
    [InlineData("AfterDdl")]
    [InlineData("BeforeCommit")]
    public async Task Precommit_failure_rolls_back_the_complete_profile_transition(string checkpointName)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        var checkpoint = Enum.Parse<RepositoryProfileUpgradeCheckpoint>(checkpointName);
        var before = await CaptureStoreAsync(fixture.DatabasePath, ct);
        var callbackCount = 0;
        ValueTask Observe(RepositoryProfileUpgradeCheckpoint current, CancellationToken _) =>
            current == checkpoint ? ThrowAtCheckpoint() : ValueTask.CompletedTask;
        ValueTask ThrowAtCheckpoint()
        {
            callbackCount++;
            return ValueTask.FromException(new InvalidOperationException("Injected compatibility interruption."));
        }

        var action = () => RepositoryProfileCompatibility.UpgradeForQualificationAsync(fixture.DatabasePath, fixture.WorkflowId,
            null, Observe, ct);
        await action.Should().ThrowAsync<InvalidOperationException>();
        callbackCount.Should().Be(1);
        (await CaptureStoreAsync(fixture.DatabasePath, ct)).Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        var current = () => RepositoryProfileCompatibility.RequireCurrentAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);
        await current.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Postcommit_failure_leaves_current_profile_and_retry_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        var before = await CaptureStoreAsync(fixture.DatabasePath, ct);
        ValueTask Observe(RepositoryProfileUpgradeCheckpoint current, CancellationToken _) =>
            current == RepositoryProfileUpgradeCheckpoint.AfterCommit
                ? ValueTask.FromException(new InvalidOperationException("Injected post-commit interruption."))
                : ValueTask.CompletedTask;

        var action = () => RepositoryProfileCompatibility.UpgradeForQualificationAsync(fixture.DatabasePath, fixture.WorkflowId,
            null, Observe, ct);
        await action.Should().ThrowAsync<InvalidOperationException>();
        var after = await CaptureStoreAsync(fixture.DatabasePath, ct);
        AssertOnlyProfileAdditions(before, after);
        await RepositoryProfileCompatibility.RequireCurrentAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);
        (await RepositoryProfileCompatibility.UpgradeAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct))
            .Should().Be(RepositoryProfileUpgradeResult.AlreadyCurrent);
    }

    [Fact]
    public async Task Preflight_refusals_for_missing_required_state_or_corrupt_hufu_store_do_not_write_execution_database()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var fixture = await RecoveryIntegrityFixture.CreateAsync())
        {
            await fixture.MutateAsync("DROP TABLE supervisor_proposal_contexts", ct);
            var before = await File.ReadAllBytesAsync(fixture.DatabasePath, ct);
            var action = () => RepositoryProfileCompatibility.UpgradeAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);
            await action.Should().ThrowAsync<RepositoryRecoveryRefusedException>();
            (await File.ReadAllBytesAsync(fixture.DatabasePath, ct)).Should().Equal(before);
        }

        using (var fixture = await RecoveryIntegrityFixture.CreateAsync())
        {
            var hufuPath = Path.Combine(fixture.Root, "hufu.db");
            await File.WriteAllTextAsync(hufuPath, "not a sqlite database", ct);
            var before = await File.ReadAllBytesAsync(fixture.DatabasePath, ct);
            var action = () => RepositoryProfileCompatibility.UpgradeAsync(fixture.DatabasePath, fixture.WorkflowId, [hufuPath], ct);
            await action.Should().ThrowAsync<RepositoryRecoveryRefusedException>();
            (await File.ReadAllBytesAsync(fixture.DatabasePath, ct)).Should().Equal(before);
        }
    }

    [Fact]
    public async Task Cancellation_before_preflight_leaves_legacy_store_unchanged()
    {
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        var before = await File.ReadAllBytesAsync(fixture.DatabasePath, TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var action = () => RepositoryProfileCompatibility.UpgradeAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: cancelled.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        (await File.ReadAllBytesAsync(fixture.DatabasePath, TestContext.Current.CancellationToken)).Should().Equal(before);
    }

    [Theory]
    [InlineData("UPDATE marang_repository_profile SET owner='future-owner'")]
    [InlineData("UPDATE marang_repository_profile SET contract_identity='future-contract'")]
    [InlineData("UPDATE marang_repository_profile SET contract_digest='future-digest'")]
    [InlineData("ALTER TABLE repository_effects ADD COLUMN future_field TEXT")]
    public async Task Unknown_current_manifest_or_effect_schema_refuses(string mutation)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await RepositoryProfileCompatibility.UpgradeAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);
        await fixture.MutateAsync(mutation, ct);
        var result = await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);
        result.CanRecover.Should().BeFalse();
    }

    [Fact]
    public async Task Physically_valid_future_profile_version_refuses()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await RepositoryProfileCompatibility.UpgradeAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);
        await fixture.MutateAsync("DROP TABLE marang_repository_profile; CREATE TABLE marang_repository_profile (singleton INTEGER PRIMARY KEY CHECK(singleton=1), owner TEXT NOT NULL, format_version INTEGER NOT NULL CHECK(format_version=2), contract_identity TEXT NOT NULL, contract_digest TEXT NOT NULL); INSERT INTO marang_repository_profile VALUES(1,'marang.bounded-repository',2,'future-contract','future-digest')", ct);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).CanRecover.Should().BeFalse();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("contract_identity")]
    [InlineData("contract_digest")]
    public async Task Manifest_cells_must_use_the_declared_storage_types(string column)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await RepositoryProfileCompatibility.UpgradeAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);
        await fixture.MutateAsync($"UPDATE marang_repository_profile SET {column}=CAST({column} AS BLOB)", ct);
        var before = await File.ReadAllBytesAsync(fixture.DatabasePath, ct);
        var inspection = await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);
        inspection.CanRecover.Should().BeFalse();
        (await File.ReadAllBytesAsync(fixture.DatabasePath, ct)).Should().Equal(before);
    }

    [Fact]
    public async Task Malformed_same_name_manifest_schema_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await RepositoryProfileCompatibility.UpgradeAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);
        await fixture.MutateAsync($"DROP TABLE marang_repository_profile; CREATE TABLE marang_repository_profile (singleton TEXT CHECK(singleton=1), owner BLOB, format_version TEXT CHECK(format_version=1), contract_identity BLOB, contract_digest BLOB); INSERT INTO marang_repository_profile VALUES('1','{RepositoryProfileCompatibility.Owner}','1','{ExpectedIdentity}','{ExpectedDigest}')", ct);

        var result = await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);

        result.CanRecover.Should().BeFalse();
    }

    [Fact]
    public async Task Physically_valid_future_effect_record_schema_refuses()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await fixture.MutateAsync("ALTER TABLE repository_effects ADD COLUMN record_format INTEGER NOT NULL DEFAULT 2 CHECK(record_format=2); CREATE TABLE marang_repository_profile (singleton INTEGER PRIMARY KEY CHECK(singleton=1), owner TEXT NOT NULL, format_version INTEGER NOT NULL CHECK(format_version=1), contract_identity TEXT NOT NULL, contract_digest TEXT NOT NULL); INSERT INTO marang_repository_profile VALUES(1,'marang.bounded-repository',1,'" + ExpectedIdentity + "','" + ExpectedDigest + "')", ct);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).CanRecover.Should().BeFalse();
    }

    [Fact]
    public async Task Typed_effect_journal_rejects_unknown_record_tag_for_reads_and_writes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await RepositoryProfileCompatibility.UpgradeAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);
        await fixture.MutateAsync("PRAGMA ignore_check_constraints=ON; UPDATE repository_effects SET record_format=2", ct);
        var effectId = fixture.Graph.Effects[0].Effect.Intent.EffectId;

        var read = () => fixture.Effects.GetAsync(effectId, ct);
        await read.Should().ThrowAsync<InvalidDataException>();
        var write = () => fixture.Effects.TransitionAsync(effectId, RepositoryEffectStage.IntentCommitted, cancellationToken: ct);
        await write.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task Profile_name_occupied_by_view_is_not_treated_as_legacy()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await fixture.MutateAsync("CREATE VIEW marang_repository_profile AS SELECT 1 AS singleton", ct);
        var result = await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);
        result.CanRecover.Should().BeFalse();
        var before = await File.ReadAllBytesAsync(fixture.DatabasePath, ct);
        var action = () => RepositoryProfileCompatibility.UpgradeAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);
        await action.Should().ThrowAsync<RepositoryRecoveryRefusedException>();
        (await File.ReadAllBytesAsync(fixture.DatabasePath, ct)).Should().Equal(before);
    }

    private static async Task<StoreSnapshot> CaptureStoreAsync(string path, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        await connection.OpenAsync(ct);
        var catalog = new SortedDictionary<string, string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT type,name,sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY type,name";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var type = reader.GetString(0); var name = reader.GetString(1);
                if (name == "marang_repository_profile") continue;
                catalog[type + ":" + name] = reader.GetString(2);
            }
        }

        var tables = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        string[] tableNames;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT IN ('sqlite_master','sqlite_schema','marang_repository_profile') ORDER BY name";
            await using var reader = await command.ExecuteReaderAsync(ct);
            var names = new List<string>();
            while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
            tableNames = names.ToArray();
        }
        foreach (var table in tableNames)
        {
            var columns = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA table_info({Quote(table)})";
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var column = reader.GetString(1);
                    if (table == "repository_effects" && column == "record_format") continue;
                    columns.Add(column);
                }
            }
            var rowValues = new List<string>();
            if (columns.Count != 0)
            {
                var projections = columns.Select(column =>
                {
                    var quoted = Quote(column);
                    return $"typeof({quoted}),CASE WHEN {quoted} IS NULL THEN '' WHEN typeof({quoted})='blob' THEN hex({quoted}) ELSE hex(CAST({quoted} AS BLOB)) END";
                });
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {string.Join(',', projections)} FROM {Quote(table)}";
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var row = new string[reader.FieldCount];
                    for (var index = 0; index < row.Length; index++) row[index] = reader.GetString(index);
                    rowValues.Add(JsonSerializer.Serialize(row));
                }
            }
            tables[table] = rowValues.Order(StringComparer.Ordinal).ToArray();
        }
        return new StoreSnapshot(catalog, tables);
    }

    private static void AssertOnlyProfileAdditions(StoreSnapshot before, StoreSnapshot after)
    {
        before.Rows.Should().BeEquivalentTo(after.Rows, options => options.WithStrictOrdering());
        var oldCatalog = before.Catalog.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var newCatalog = after.Catalog.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        newCatalog.Remove("table:marang_repository_profile");
        var oldEffectsSql = oldCatalog["table:repository_effects"];
        var expectedEffectsSql = oldEffectsSql[..^1] + ", record_format INTEGER NOT NULL DEFAULT 1 CHECK(record_format=1))";
        newCatalog["table:repository_effects"].Should().Be(expectedEffectsSql);
        newCatalog.Remove("table:repository_effects");
        oldCatalog.Remove("table:repository_effects");
        newCatalog.Should().BeEquivalentTo(oldCatalog);
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed record StoreSnapshot(SortedDictionary<string, string> Catalog,
        SortedDictionary<string, string[]> Rows);
}
