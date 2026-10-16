using Microsoft.Data.Sqlite;
using Marang.Runs.Effects;

namespace Marang.Runs.Integrity;

internal enum RepositoryProfileFormat
{
    Legacy = 0,
    Current = 1
}

internal enum RepositoryProfileUpgradeCheckpoint
{
    AfterBegin,
    AfterDdl,
    BeforeCommit,
    AfterCommit
}

public enum RepositoryProfileUpgradeResult
{
    Upgraded,
    AlreadyCurrent
}

/// <summary>
/// Performs the single supported additive transition from the exact qualified
/// legacy Marang profile to its explicitly tagged current profile.
/// </summary>
public static class RepositoryProfileCompatibility
{
    public const string Owner = "marang.bounded-repository";
    public const int FormatVersion = 1;
    public const int ZhinuSchemaVersion = 6;
    public const string ContractIdentity = "marang/bounded-repository/m6.1@5dce00ef8d7ca962e3de42f4a28b1e0d1e52fb29;zhinu-schema=6;effect-json=web-v1;profile=1;effect-record=1";
    public static readonly string ContractDigest = RepositoryStateSnapshot.Hash(ContractIdentity);

    /// <summary>
    /// Upgrades an existing database only after the qualified legacy inspector
    /// passes. This adds metadata only; it does not rewrite existing documents.
    /// </summary>
    public static Task<RepositoryProfileUpgradeResult> UpgradeAsync(string databasePath, Guid workflowId,
        IReadOnlyList<string>? additionalRequiredSqliteStores = null, CancellationToken cancellationToken = default) =>
        UpgradeForQualificationAsync(databasePath, workflowId, additionalRequiredSqliteStores, observer: null, cancellationToken);

    /// <summary>Confirms that the existing database is in the tagged Marang profile.</summary>
    public static async Task RequireCurrentAsync(string databasePath, Guid workflowId,
        IReadOnlyList<string>? additionalRequiredSqliteStores = null, CancellationToken cancellationToken = default)
    {
        await RepositoryRecoveryInspector.RequireAsync(databasePath, workflowId, additionalRequiredSqliteStores, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var format = await InspectFormatAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (format != RepositoryProfileFormat.Current)
            throw new InvalidDataException("The database has the qualified legacy profile; the explicit Marang profile transition is required.");
    }

    internal static Task<RepositoryProfileUpgradeResult> UpgradeForQualificationAsync(string databasePath, Guid workflowId,
        IReadOnlyList<string>? additionalRequiredSqliteStores,
        Func<RepositoryProfileUpgradeCheckpoint, CancellationToken, ValueTask>? observer,
        CancellationToken cancellationToken = default)
        => UpgradeCoreAsync(databasePath, workflowId, additionalRequiredSqliteStores, observer, cancellationToken);

    internal static async Task<RepositoryProfileFormat> InspectFormatAsync(SqliteConnection connection,
        SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var manifestObjectType = await ObjectTypeAsync(connection, transaction, "marang_repository_profile", cancellationToken).ConfigureAwait(false);
        if (manifestObjectType is not null and not "table")
            throw new InvalidDataException("Marang profile name is occupied by an unsupported SQLite object.");
        var hasManifest = manifestObjectType == "table";
        var hasRecordFormat = await ColumnExistsAsync(connection, transaction, "repository_effects", "record_format", cancellationToken).ConfigureAwait(false);
        if (!hasManifest && !hasRecordFormat)
        {
            var legacyColumns = await ReadColumnsAsync(connection, transaction, "repository_effects", cancellationToken).ConfigureAwait(false);
            string[] expectedLegacy = ["effect_id", "workflow_id", "repository_id", "intent_digest", "stage", "document", "updated_at"];
            if (!legacyColumns.SequenceEqual(expectedLegacy, StringComparer.Ordinal))
                throw new InvalidDataException("The database does not have the exact qualified legacy Marang effect schema.");
            return RepositoryProfileFormat.Legacy;
        }
        if (!hasManifest || !hasRecordFormat)
            throw new InvalidDataException("Marang profile manifest and effect record-format tag must be present together.");

        await ValidateManifestSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await ValidateRecordFormatSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT owner,format_version,contract_identity,contract_digest,typeof(owner),typeof(format_version),typeof(contract_identity),typeof(contract_digest) FROM marang_repository_profile WHERE singleton=1";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(4) != "text" || reader.GetString(5) != "integer" ||
                reader.GetString(6) != "text" || reader.GetString(7) != "text" ||
                reader.GetString(0) != Owner || reader.GetInt64(1) != FormatVersion ||
                reader.GetString(2) != ContractIdentity || reader.GetString(3) != ContractDigest ||
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Marang profile manifest is absent, duplicated, or unsupported.");
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM repository_effects WHERE typeof(record_format)<>'integer' OR record_format<>1";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0)
                throw new InvalidDataException("A repository effect has an unsupported record format.");
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT version FROM zhinu_schema WHERE id=1";
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long version || version != ZhinuSchemaVersion)
                throw new InvalidDataException("The pinned Zhinu schema marker is unsupported.");
        }
        return RepositoryProfileFormat.Current;
    }

    private static async Task<RepositoryProfileUpgradeResult> UpgradeCoreAsync(string databasePath, Guid workflowId,
        IReadOnlyList<string>? additionalRequiredSqliteStores,
        Func<RepositoryProfileUpgradeCheckpoint, CancellationToken, ValueTask>? observer,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (workflowId == Guid.Empty) throw new ArgumentException("A known workflow identity is required.", nameof(workflowId));

        await RepositoryRecoveryInspector.RequireAsync(databasePath, workflowId, additionalRequiredSqliteStores, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenReadWriteExistingAsync(databasePath, cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        if (observer is not null) await observer(RepositoryProfileUpgradeCheckpoint.AfterBegin, cancellationToken).ConfigureAwait(false);

        // Recheck while this connection owns SQLite's writer reservation. A competing
        // writer cannot change the preflighted profile between this check and the DDL.
        await RepositoryRecoveryInspector.RequireAsync(databasePath, workflowId, additionalRequiredSqliteStores, cancellationToken).ConfigureAwait(false);
        var format = await InspectFormatAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (format == RepositoryProfileFormat.Current)
        {
            transaction.Rollback();
            return RepositoryProfileUpgradeResult.AlreadyCurrent;
        }

        await ExecuteAsync(connection, transaction, "ALTER TABLE repository_effects ADD COLUMN record_format INTEGER NOT NULL DEFAULT 1 CHECK(record_format=1)", cancellationToken).ConfigureAwait(false);
        if (observer is not null) await observer(RepositoryProfileUpgradeCheckpoint.AfterDdl, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, """
            CREATE TABLE marang_repository_profile (
              singleton INTEGER PRIMARY KEY CHECK(singleton=1),
              owner TEXT NOT NULL,
              format_version INTEGER NOT NULL CHECK(format_version=1),
              contract_identity TEXT NOT NULL,
              contract_digest TEXT NOT NULL);
            """, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            "INSERT INTO marang_repository_profile(singleton,owner,format_version,contract_identity,contract_digest) VALUES(1,$owner,$version,$identity,$digest)",
            cancellationToken, ("$owner", Owner), ("$version", FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("$identity", ContractIdentity), ("$digest", ContractDigest)).ConfigureAwait(false);
        if (await InspectFormatAsync(connection, transaction, cancellationToken).ConfigureAwait(false) != RepositoryProfileFormat.Current)
            throw new InvalidDataException("The Marang profile transition did not produce the current format.");
        if (observer is not null) await observer(RepositoryProfileUpgradeCheckpoint.BeforeCommit, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        if (observer is not null) await observer(RepositoryProfileUpgradeCheckpoint.AfterCommit, cancellationToken).ConfigureAwait(false);
        return RepositoryProfileUpgradeResult.Upgraded;
    }

    private static async Task ValidateManifestSchemaAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        var columns = await ReadColumnsAsync(connection, transaction, "marang_repository_profile", ct).ConfigureAwait(false);
        string[] expected = ["singleton", "owner", "format_version", "contract_identity", "contract_digest"];
        if (!columns.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("Marang profile manifest has an unsupported schema.");
        await using (var shape = connection.CreateCommand())
        {
            shape.Transaction = transaction;
            shape.CommandText = "PRAGMA table_xinfo(marang_repository_profile)";
            await using var reader = await shape.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var index = 0;
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var expectedType = index is 0 or 2 ? "INTEGER" : "TEXT";
                if (reader.GetString(2) != expectedType || reader.GetInt64(3) != (index == 0 ? 0 : 1) ||
                    !reader.IsDBNull(4) || reader.GetInt64(5) != (index == 0 ? 1 : 0) || reader.GetInt64(6) != 0)
                    throw new InvalidDataException("Marang profile manifest has unsupported column types or constraints.");
                index++;
            }
            if (index != expected.Length) throw new InvalidDataException("Marang profile manifest has an unsupported column count.");
        }
        var sql = await TableSqlAsync(connection, transaction, "marang_repository_profile", ct).ConfigureAwait(false);
        var normalized = NormalizeSql(sql);
        if (!normalized.Contains("CHECK(SINGLETON=1)", StringComparison.Ordinal) ||
            !normalized.Contains("CHECK(FORMAT_VERSION=1)", StringComparison.Ordinal))
            throw new InvalidDataException("Marang profile version constraints are absent.");
        await using var count = connection.CreateCommand(); count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM marang_repository_profile";
        if (Convert.ToInt64(await count.ExecuteScalarAsync(ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw new InvalidDataException("Marang profile manifest must contain exactly one row.");
    }

    private static async Task ValidateRecordFormatSchemaAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        var columns = await ReadColumnsAsync(connection, transaction, "repository_effects", ct).ConfigureAwait(false);
        string[] expectedColumns = ["effect_id", "workflow_id", "repository_id", "intent_digest", "stage", "document", "updated_at", "record_format"];
        if (!columns.SequenceEqual(expectedColumns, StringComparer.Ordinal))
            throw new InvalidDataException("Repository effect table has an unsupported Marang profile schema.");
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(repository_effects)";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.GetString(1) != "record_format") continue;
            if (reader.GetString(2) != "INTEGER" || reader.GetInt32(3) != 1 || reader.GetString(4) != "1" || reader.GetInt32(5) != 0)
                throw new InvalidDataException("Repository effect record-format column has an unsupported definition.");
            await reader.DisposeAsync().ConfigureAwait(false);
            var sql = await TableSqlAsync(connection, transaction, "repository_effects", ct).ConfigureAwait(false);
            if (!NormalizeSql(sql).Contains("CHECK(RECORD_FORMAT=1)", StringComparison.Ordinal))
                throw new InvalidDataException("Repository effect record-format check constraint is absent.");
            return;
        }
        throw new InvalidDataException("Repository effect record-format column is missing.");
    }

    private static async Task<string[]> ReadColumnsAsync(SqliteConnection connection, SqliteTransaction? transaction, string table, CancellationToken ct)
    {
        var result = new List<string>();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_xinfo({table})";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) result.Add(reader.GetString(1));
        return result.ToArray();
    }

    private static async Task<string> TableSqlAsync(SqliteConnection connection, SqliteTransaction? transaction, string table, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", table);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string ?? throw new InvalidDataException($"Table '{table}' has no stored definition.");
    }

    private static async Task<string?> ObjectTypeAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string name, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT type FROM sqlite_master WHERE name=$name";
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string table, string column, CancellationToken ct)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            if (reader.GetString(1) == column) return true;
        return false;
    }

    private static string NormalizeSql(string sql) => string.Concat(sql.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql,
        CancellationToken ct, params (string Name, string Value)[] parameters)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string databasePath, CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        try { await connection.OpenAsync(ct).ConfigureAwait(false); return connection; }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static async Task<SqliteConnection> OpenReadWriteExistingAsync(string databasePath, CancellationToken ct)
    {
        var attributes = File.GetAttributes(databasePath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || new FileInfo(databasePath).Length == 0)
            throw new InvalidDataException("Only an existing, regular, non-empty database can be upgraded.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        try { await connection.OpenAsync(ct).ConfigureAwait(false); return connection; }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
