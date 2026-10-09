using Microsoft.Data.Sqlite;

namespace Marang.DelegationAuthority;

/// <summary>
/// SQLite-backed <see cref="IDelegationVisibilityStore"/>. Shares its file
/// with <see cref="SqliteAcceptedDelegationStore"/> (tables are created
/// idempotently by either store) but opens independent connections and never
/// joins a cross-record transaction: ordering plus idempotent replay provide
/// safety, not atomicity.
/// </summary>
public sealed class SqliteDelegationVisibilityStore : IDelegationVisibilityStore
{
    private readonly string _connectionString;

    public SqliteDelegationVisibilityStore(string databasePath)
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

    public async ValueTask<(DelegationVisibilityPublishStatus Status, DelegationVisibilityBinding? Binding)> PublishAsync(
        DelegationVisibilityBinding candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Validate(candidate);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // Tenant-wide command identity: an exact replay returns the original
        // receipt without touching slot state.
        await using (var byCommand = connection.CreateCommand())
        {
            byCommand.CommandText = "SELECT tenant_id, subject_id, delegation_id, generation FROM visibility_bindings WHERE command_id = $command;";
            byCommand.Parameters.AddWithValue("$command", candidate.CommandId);
            await using var reader = await byCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var slot = (reader.GetString(0), reader.GetString(1), Guid.Parse(reader.GetString(2)), reader.GetString(3));
                var mine = (candidate.TenantId, candidate.SubjectId, candidate.DelegationId, candidate.Generation);
                if (!slot.Equals(mine))
                {
                    return (DelegationVisibilityPublishStatus.Conflict, null);
                }

                var existing = await ReadSlotAsync(connection, candidate, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    // A revoked slot stays revoked: even an exact publish
                    // replay must report the terminal state, never resurrect it.
                    if (existing.Revoked)
                    {
                        return (DelegationVisibilityPublishStatus.Revoked, existing);
                    }

                    if (BindingEqual(existing, candidate))
                    {
                        return (DelegationVisibilityPublishStatus.Replayed, existing);
                    }
                }

                return (DelegationVisibilityPublishStatus.Conflict, null);
            }
        }

        var current = await ReadSlotAsync(connection, candidate, cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            if (current.Revoked)
            {
                return (DelegationVisibilityPublishStatus.Revoked, current);
            }

            return BindingEqual(current, candidate)
                ? (DelegationVisibilityPublishStatus.Replayed, current)
                : (DelegationVisibilityPublishStatus.Conflict, current);
        }

        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO visibility_bindings (tenant_id, subject_id, delegation_id, generation, request_key, fingerprint_version, fingerprint_hash, operations, not_before, expires_at, command_id, revoked, revoke_reason, created_at, revoked_at) VALUES ($tenant, $subject, $delegation, $generation, $request, $fpv, $fph, $ops, $nb, $exp, $command, 0, '', $created, 0);";
        insert.Parameters.AddWithValue("$tenant", candidate.TenantId);
        insert.Parameters.AddWithValue("$subject", candidate.SubjectId);
        insert.Parameters.AddWithValue("$delegation", candidate.DelegationId.ToString("D"));
        insert.Parameters.AddWithValue("$generation", candidate.Generation);
        insert.Parameters.AddWithValue("$request", candidate.RequestKey);
        insert.Parameters.AddWithValue("$fpv", candidate.FingerprintVersion);
        insert.Parameters.AddWithValue("$fph", candidate.FingerprintHash);
        insert.Parameters.AddWithValue("$ops", (long)candidate.Operations);
        insert.Parameters.AddWithValue("$nb", candidate.NotBefore.UtcTicks);
        insert.Parameters.AddWithValue("$exp", candidate.ExpiresAt.UtcTicks);
        insert.Parameters.AddWithValue("$command", candidate.CommandId);
        insert.Parameters.AddWithValue("$created", candidate.CreatedAt.UtcTicks);
        try
        {
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            // Lost an insert race: re-resolve under the same replay/conflict
            // rule instead of assuming success.
            return await PublishAsync(candidate, cancellationToken).ConfigureAwait(false);
        }

        return (DelegationVisibilityPublishStatus.Published, candidate);
    }

    public async ValueTask<DelegationVisibilityRevokeStatus> RevokeAsync(
        string tenantId,
        string subjectId,
        Guid delegationId,
        string generation,
        string commandId,
        string reasonCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(generation);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        // Exact revocation-command replay converges without touching state.
        await using (var byRevokeCommand = connection.CreateCommand())
        {
            byRevokeCommand.CommandText = "SELECT tenant_id, subject_id, delegation_id, generation FROM visibility_bindings WHERE revoke_command = $command AND revoked = 1;";
            byRevokeCommand.Parameters.AddWithValue("$command", commandId);
            await using var reader = await byRevokeCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var mine = (tenantId, subjectId, delegationId, generation);
                var stored = (reader.GetString(0), reader.GetString(1), Guid.Parse(reader.GetString(2)), reader.GetString(3));
                return mine.Equals(stored)
                    ? DelegationVisibilityRevokeStatus.Replayed
                    : DelegationVisibilityRevokeStatus.Conflict;
            }
        }

        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE visibility_bindings SET revoked = 1, revoke_reason = $reason, revoke_command = $command, revoked_at = $at WHERE tenant_id = $tenant AND subject_id = $subject AND delegation_id = $delegation AND generation = $generation AND revoked = 0;";
        update.Parameters.AddWithValue("$reason", reasonCode);
        update.Parameters.AddWithValue("$command", commandId);
        update.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.UtcTicks);
        update.Parameters.AddWithValue("$tenant", tenantId);
        update.Parameters.AddWithValue("$subject", subjectId);
        update.Parameters.AddWithValue("$delegation", delegationId.ToString("D"));
        update.Parameters.AddWithValue("$generation", generation);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
        {
            return DelegationVisibilityRevokeStatus.Revoked;
        }

        // Nothing changed: the slot is absent (NotFound) or already terminal.
        // An already-terminal slot reports Conflict so a second revocation
        // command can never be mistaken for fresh authority state; only the
        // exact recorded revocation command replays.
        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT revoked FROM visibility_bindings WHERE tenant_id = $tenant AND subject_id = $subject AND delegation_id = $delegation AND generation = $generation;";
        probe.Parameters.AddWithValue("$tenant", tenantId);
        probe.Parameters.AddWithValue("$subject", subjectId);
        probe.Parameters.AddWithValue("$delegation", delegationId.ToString("D"));
        probe.Parameters.AddWithValue("$generation", generation);
        await using var probeReader = await probe.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await probeReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return DelegationVisibilityRevokeStatus.Conflict;
        }

        return DelegationVisibilityRevokeStatus.NotFound;
    }

    public async ValueTask<DelegationVisibilityCheckStatus> CheckAsync(
        string tenantId,
        string subjectId,
        Guid delegationId,
        string generation,
        DelegationVisibilityOperation required,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var binding = await GetAsync(tenantId, subjectId, delegationId, generation, cancellationToken).ConfigureAwait(false);
        if (binding is null)
        {
            return DelegationVisibilityCheckStatus.Unknown;
        }

        if (binding.Revoked)
        {
            return DelegationVisibilityCheckStatus.Revoked;
        }

        if (now < binding.NotBefore || now >= binding.ExpiresAt)
        {
            return DelegationVisibilityCheckStatus.Expired;
        }

        return (binding.Operations & required) == required && required != DelegationVisibilityOperation.None
            ? DelegationVisibilityCheckStatus.Granted
            : DelegationVisibilityCheckStatus.Forbidden;
    }

    public async ValueTask<DelegationVisibilityBinding?> GetAsync(
        string tenantId,
        string subjectId,
        Guid delegationId,
        string generation,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT request_key, fingerprint_version, fingerprint_hash, operations, not_before, expires_at, command_id, revoked, revoke_reason, created_at FROM visibility_bindings WHERE tenant_id = $tenant AND subject_id = $subject AND delegation_id = $delegation AND generation = $generation;";
        select.Parameters.AddWithValue("$tenant", tenantId);
        select.Parameters.AddWithValue("$subject", subjectId);
        select.Parameters.AddWithValue("$delegation", delegationId.ToString("D"));
        select.Parameters.AddWithValue("$generation", generation);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DelegationVisibilityBinding(
            tenantId, subjectId, delegationId, generation,
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            (DelegationVisibilityOperation)reader.GetInt64(3),
            new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero),
            new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
            reader.GetString(6), reader.GetInt64(7) != 0, reader.GetString(8),
            new DateTimeOffset(reader.GetInt64(9), TimeSpan.Zero));
    }

    private async ValueTask<DelegationVisibilityBinding?> ReadSlotAsync(
        SqliteConnection connection,
        DelegationVisibilityBinding candidate,
        CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT request_key, fingerprint_version, fingerprint_hash, operations, not_before, expires_at, command_id, revoked, revoke_reason, created_at FROM visibility_bindings WHERE tenant_id = $tenant AND subject_id = $subject AND delegation_id = $delegation AND generation = $generation;";
        select.Parameters.AddWithValue("$tenant", candidate.TenantId);
        select.Parameters.AddWithValue("$subject", candidate.SubjectId);
        select.Parameters.AddWithValue("$delegation", candidate.DelegationId.ToString("D"));
        select.Parameters.AddWithValue("$generation", candidate.Generation);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DelegationVisibilityBinding(
            candidate.TenantId, candidate.SubjectId, candidate.DelegationId, candidate.Generation,
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            (DelegationVisibilityOperation)reader.GetInt64(3),
            new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero),
            new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
            reader.GetString(6), reader.GetInt64(7) != 0, reader.GetString(8),
            new DateTimeOffset(reader.GetInt64(9), TimeSpan.Zero));
    }

    private static bool BindingEqual(DelegationVisibilityBinding left, DelegationVisibilityBinding right) =>
        string.Equals(left.RequestKey, right.RequestKey, StringComparison.Ordinal) &&
        string.Equals(left.FingerprintVersion, right.FingerprintVersion, StringComparison.Ordinal) &&
        string.Equals(left.FingerprintHash, right.FingerprintHash, StringComparison.Ordinal) &&
        left.Operations == right.Operations &&
        left.NotBefore == right.NotBefore &&
        left.ExpiresAt == right.ExpiresAt &&
        string.Equals(left.CommandId, right.CommandId, StringComparison.Ordinal);

    private static void Validate(DelegationVisibilityBinding candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.SubjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Generation);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.RequestKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.FingerprintVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.FingerprintHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.CommandId);
        if (candidate.Operations == DelegationVisibilityOperation.None)
        {
            throw new ArgumentException("At least one operation is required.", nameof(candidate));
        }

        if (candidate.NotBefore >= candidate.ExpiresAt)
        {
            throw new ArgumentException("Validity must be a non-empty interval.", nameof(candidate));
        }
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
