using Microsoft.Data.Sqlite;

namespace Marang.DelegationAuthority;

/// <summary>
/// SQLite-backed <see cref="IAcceptedDelegationStore"/>. One file, WAL,
/// synchronous durability, no pooling — mirroring the Hufu store profile.
/// Each call opens its own short connection; this store never joins a
/// cross-record transaction (ordered idempotent writes provide safety, not
/// atomicity across the descriptor and visibility stores).
/// </summary>
public sealed class SqliteAcceptedDelegationStore : IAcceptedDelegationStore
{
    private readonly string _connectionString;

    public SqliteAcceptedDelegationStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (databasePath == ":memory:" || databasePath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A dedicated disk database path is required.", nameof(databasePath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? ".");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();
        Initialize();
    }

    public async ValueTask<(AcceptedDelegation Record, AcceptedDelegationEnsureStatus Status)> EnsureAsync(
        AcceptedDelegation candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Validate(candidate);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT acceptance_key, delegation_id, tenant_id, subject_id, request_key, fingerprint_version, fingerprint_hash, objective, provider, workspace, generation, created_at FROM accepted_delegations WHERE acceptance_key = $key;";
        select.Parameters.AddWithValue("$key", candidate.AcceptanceKey);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var existing = Read(reader);
            if (!FingerprintEqual(existing, candidate))
            {
                throw new AcceptedDelegationConflictException(
                    candidate.RequestKey,
                    $"{existing.FingerprintVersion}:{existing.FingerprintHash}",
                    $"{candidate.FingerprintVersion}:{candidate.FingerprintHash}");
            }

            return (existing, AcceptedDelegationEnsureStatus.Replayed);
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO accepted_delegations (acceptance_key, delegation_id, tenant_id, subject_id, request_key, fingerprint_version, fingerprint_hash, objective, provider, workspace, generation, created_at) VALUES ($key, $delegation, $tenant, $subject, $request, $fpv, $fph, $objective, $provider, $workspace, $generation, $created);";
        insert.Parameters.AddWithValue("$key", candidate.AcceptanceKey);
        insert.Parameters.AddWithValue("$delegation", candidate.DelegationId.ToString("D"));
        insert.Parameters.AddWithValue("$tenant", candidate.TenantId);
        insert.Parameters.AddWithValue("$subject", candidate.SubjectId);
        insert.Parameters.AddWithValue("$request", candidate.RequestKey);
        insert.Parameters.AddWithValue("$fpv", candidate.FingerprintVersion);
        insert.Parameters.AddWithValue("$fph", candidate.FingerprintHash);
        insert.Parameters.AddWithValue("$objective", candidate.Objective);
        insert.Parameters.AddWithValue("$provider", candidate.Provider);
        insert.Parameters.AddWithValue("$workspace", candidate.Workspace);
        insert.Parameters.AddWithValue("$generation", candidate.Generation);
        insert.Parameters.AddWithValue("$created", candidate.CreatedAt.UtcTicks);
        try
        {
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            // Lost a concurrent insert race: re-read under the same
            // fingerprint-equality rule rather than assuming success.
            var raced = await GetByAcceptanceKeyAsync(candidate.AcceptanceKey, cancellationToken).ConfigureAwait(false);
            if (raced is not null && FingerprintEqual(raced, candidate))
            {
                return (raced, AcceptedDelegationEnsureStatus.Replayed);
            }

            throw new AcceptedDelegationConflictException(
                candidate.RequestKey,
                raced is null ? "unknown" : $"{raced.FingerprintVersion}:{raced.FingerprintHash}",
                $"{candidate.FingerprintVersion}:{candidate.FingerprintHash}");
        }

        return (candidate, AcceptedDelegationEnsureStatus.Created);
    }

    public async ValueTask<AcceptedDelegation?> GetByAcceptanceKeyAsync(
        string acceptanceKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptanceKey);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT acceptance_key, delegation_id, tenant_id, subject_id, request_key, fingerprint_version, fingerprint_hash, objective, provider, workspace, generation, created_at FROM accepted_delegations WHERE acceptance_key = $key;";
        select.Parameters.AddWithValue("$key", acceptanceKey);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async ValueTask<AcceptedDelegation?> GetByDelegationIdAsync(
        Guid delegationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT acceptance_key, tenant_id, subject_id, request_key, fingerprint_version, fingerprint_hash, objective, provider, workspace, generation, created_at FROM accepted_delegations WHERE delegation_id = $delegation;";
        select.Parameters.AddWithValue("$delegation", delegationId.ToString("D"));
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AcceptedDelegation(
            reader.GetString(0), delegationId, reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            reader.GetString(7), reader.GetString(8), reader.GetString(9),
            new DateTimeOffset(reader.GetInt64(10), TimeSpan.Zero));
    }

    private static AcceptedDelegation Read(SqliteDataReader reader) => new(
        AcceptanceKey: reader.GetString(0),
        DelegationId: Guid.Parse(reader.GetString(1)),
        TenantId: reader.GetString(2),
        SubjectId: reader.GetString(3),
        RequestKey: reader.GetString(4),
        FingerprintVersion: reader.GetString(5),
        FingerprintHash: reader.GetString(6),
        Objective: reader.GetString(7),
        Provider: reader.GetString(8),
        Workspace: reader.GetString(9),
        Generation: reader.GetString(10),
        CreatedAt: new DateTimeOffset(reader.GetInt64(11), TimeSpan.Zero));

    private static bool FingerprintEqual(AcceptedDelegation left, AcceptedDelegation right) =>
        string.Equals(left.FingerprintVersion, right.FingerprintVersion, StringComparison.Ordinal) &&
        string.Equals(left.FingerprintHash, right.FingerprintHash, StringComparison.Ordinal);

    private static void Validate(AcceptedDelegation candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.AcceptanceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.SubjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.RequestKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.FingerprintVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.FingerprintHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Objective);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Generation);
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS accepted_delegations (
              acceptance_key TEXT PRIMARY KEY,
              delegation_id TEXT NOT NULL UNIQUE,
              tenant_id TEXT NOT NULL,
              subject_id TEXT NOT NULL,
              request_key TEXT NOT NULL,
              fingerprint_version TEXT NOT NULL,
              fingerprint_hash TEXT NOT NULL,
              objective TEXT NOT NULL,
              provider TEXT NOT NULL,
              workspace TEXT NOT NULL,
              generation TEXT NOT NULL,
              created_at INTEGER NOT NULL
            ) STRICT;
            CREATE TABLE IF NOT EXISTS visibility_bindings (
              tenant_id TEXT NOT NULL,
              subject_id TEXT NOT NULL,
              delegation_id TEXT NOT NULL,
              generation TEXT NOT NULL,
              request_key TEXT NOT NULL,
              fingerprint_version TEXT NOT NULL,
              fingerprint_hash TEXT NOT NULL,
              operations INTEGER NOT NULL,
              not_before INTEGER NOT NULL,
              expires_at INTEGER NOT NULL,
              command_id TEXT NOT NULL UNIQUE,
              revoked INTEGER NOT NULL DEFAULT 0,
              revoke_reason TEXT NOT NULL DEFAULT '',
              revoke_command TEXT NOT NULL DEFAULT '',
              created_at INTEGER NOT NULL,
              revoked_at INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (tenant_id, subject_id, delegation_id, generation)
            ) STRICT;
            """;
        command.ExecuteNonQuery();
    }
}
