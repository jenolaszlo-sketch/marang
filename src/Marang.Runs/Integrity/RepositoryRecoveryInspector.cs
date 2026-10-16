using System.Text.Json;
using Marang.Runs.Effects;
using Microsoft.Data.Sqlite;

namespace Marang.Runs.Integrity;

public enum RepositoryRecoveryStatus
{
    Ready,
    MissingStore,
    DamagedStore,
    Unavailable,
    MissingStateShape,
    InconsistentState
}

/// <summary>A recovery admission result, not authority to execute or repair.</summary>
public sealed record RepositoryRecoveryInspection(RepositoryRecoveryStatus Status, string Store, string ReasonCode)
{
    public bool CanRecover => Status == RepositoryRecoveryStatus.Ready;
}

public sealed class RepositoryRecoveryRefusedException(RepositoryRecoveryInspection inspection)
    : IOException($"Repository recovery refused: {inspection.Status} ({inspection.ReasonCode}, {inspection.Store}).")
{
    public RepositoryRecoveryInspection Inspection { get; } = inspection;
}

/// <summary>
/// Checks the existing pinned repository profile before any initializing or
/// executing owner is constructed. Never creates, repairs or migrates a store.
/// A healthy result still requires normal live authority and repository checks.
/// </summary>
public static class RepositoryRecoveryInspector
{
    private static readonly string[] RequiredTables =
    [
        "zhinu_schema", "workflow_runs", "workflow_steps", "workflow_step_dependencies",
        "workflow_artifacts", "workflow_artifact_invalidations", "workflow_waits", "workflow_event_consumers",
        "workflow_events", "workflow_signals", "workflow_step_compensations", "workflow_run_operations",
        "workflow_external_operations", "workflow_instances", "workflow_generations", "workflow_generation_dispositions",
        "workflow_authorization_checkpoints", "workflow_authorization_evidence", "workflow_authorization_wake_receipts",
        "workflow_authorization_dispatches", "adaptive_workflows", "adaptive_revisions", "adaptive_proposals",
        "adaptive_run_ownership", "adaptive_execution_dependencies", "adaptive_execution_witness", "adaptive_evidence",
        "repository_effects", "repository_effect_events", "repository_effect_graphs", "repository_causal_effects",
        "repository_effect_graph_validations", "repository_effect_prerequisite_authority",
        "adaptive_operator_approvals", "adaptive_authority_decisions",
        "adaptive_delegation_attempts", "adaptive_provider_handles", "adaptive_branch_delegations",
        "supervisor_revision_intents", "supervisor_pending_discussions", "supervisor_operator_checkpoints",
        "supervisor_proposal_contexts"
    ];

    public static async Task RequireAsync(string databasePath, Guid workflowId,
        IReadOnlyList<string>? additionalRequiredSqliteStores = null, CancellationToken cancellationToken = default)
    {
        var result = await InspectAsync(databasePath, workflowId, additionalRequiredSqliteStores, cancellationToken).ConfigureAwait(false);
        if (!result.CanRecover) throw new RepositoryRecoveryRefusedException(result);
    }

    public static async Task<RepositoryRecoveryInspection> InspectAsync(string databasePath, Guid workflowId,
        IReadOnlyList<string>? additionalRequiredSqliteStores = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (workflowId == Guid.Empty) throw new ArgumentException("A known workflow identity is required.", nameof(workflowId));
        string store = Path.GetFileName(databasePath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = CheckFile(databasePath);
            if (file is not null) return file;
            await using var connection = await OpenReadOnlyAsync(databasePath, cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: true);
            if (!await PhysicallyValidAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
                return new(RepositoryRecoveryStatus.DamagedStore, store, "sqlite-integrity-failed");
            foreach (var table in RequiredTables)
            {
                await using var query = connection.CreateCommand(); query.Transaction = transaction;
                query.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$table";
                query.Parameters.AddWithValue("$table", table);
                if (await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                    return new(RepositoryRecoveryStatus.MissingStateShape, store, "required-profile-table-missing");
            }
            await using (var version = connection.CreateCommand())
            {
                version.Transaction = transaction;
                version.CommandText = "SELECT version FROM zhinu_schema WHERE id=1";
                if (await version.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not long pinnedVersion || pinnedVersion != 6)
                    return new(RepositoryRecoveryStatus.MissingStateShape, store, "pinned-profile-format-required");
            }
            await SqliteRepositoryEffectJournal.ValidateGlobalEventSequenceAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await RepositoryRecoveryContents.ValidateAsync(connection, transaction, workflowId, cancellationToken).ConfigureAwait(false);
            await RepositoryRecoveryExecution.ValidateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var effects = new List<Guid>();
            await using (var query = connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = "SELECT effect_id FROM repository_effects UNION SELECT effect_id FROM repository_effect_events";
                await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) effects.Add(Guid.Parse(reader.GetString(0)));
            }
            foreach (var effectId in effects)
                _ = await SqliteRepositoryEffectJournal.ReadByIdAsync(connection, transaction, effectId, cancellationToken).ConfigureAwait(false);
            foreach (string path in additionalRequiredSqliteStores ?? [])
            {
                store = Path.GetFileName(path);
                file = CheckFile(path);
                if (file is not null) return file;
                await using var additional = await OpenReadOnlyAsync(path, cancellationToken).ConfigureAwait(false);
                using var additionalTransaction = additional.BeginTransaction(deferred: true);
                if (!await PhysicallyValidAsync(additional, additionalTransaction, cancellationToken).ConfigureAwait(false))
                    return new(RepositoryRecoveryStatus.DamagedStore, store, "sqlite-integrity-failed");
            }
            return new(RepositoryRecoveryStatus.Ready, Path.GetFileName(databasePath), "existing-profile-consistent");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 11 or 26)
        { return new(RepositoryRecoveryStatus.DamagedStore, store, "sqlite-storage-invalid"); }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1)
        { return new(RepositoryRecoveryStatus.MissingStateShape, store, "profile-query-unavailable"); }
        catch (SqliteException)
        { return new(RepositoryRecoveryStatus.Unavailable, store, "sqlite-inspection-unavailable"); }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException
            or ArgumentException or InvalidOperationException or NullReferenceException or KeyNotFoundException)
        { return new(RepositoryRecoveryStatus.InconsistentState, store, "durable-state-inconsistent"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new(RepositoryRecoveryStatus.Unavailable, store, "store-inspection-unavailable"); }
    }

    private static RepositoryRecoveryInspection? CheckFile(string path)
    {
        string name = Path.GetFileName(path);
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return new(RepositoryRecoveryStatus.Unavailable, name, "regular-existing-store-required");
            if (new FileInfo(path).Length == 0)
                return new(RepositoryRecoveryStatus.DamagedStore, name, "empty-existing-store");
            return null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        { return new(RepositoryRecoveryStatus.MissingStore, name, "known-workflow-store-missing"); }
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string path, CancellationToken ct)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 2
        }.ToString());
        try { await connection.OpenAsync(ct).ConfigureAwait(false); return connection; }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static async Task<bool> PhysicallyValidAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "PRAGMA integrity_check(1)";
        if (await query.ExecuteScalarAsync(ct).ConfigureAwait(false) as string != "ok") return false;
        query.CommandText = "PRAGMA foreign_key_check";
        await using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return !await reader.ReadAsync(ct).ConfigureAwait(false);
    }
}
