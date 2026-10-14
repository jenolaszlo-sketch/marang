using System.Text.Json;
using Microsoft.Data.Sqlite;
using Penghou.Qingniao;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Runs.Adaptive;

/// <summary>Host SQLite implementation of Qingniao's recovery journal; shares the Zhinu database owner.</summary>
public sealed class SqliteDelegationRecoveryJournal(IZhinuSqliteDatabase database) : IExternalOperationRecoveryJournal
{
    private static readonly JsonSerializerOptions SerializerOptions = DelegationRecoveryJson.CreateOptions();
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken);
        await using var connection = await database.OpenAsync(cancellationToken);
        await Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS adaptive_delegation_attempts (
              attempt_key TEXT PRIMARY KEY, delegation_id TEXT NOT NULL, document TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS adaptive_provider_handles (
              provider TEXT NOT NULL, handle TEXT NOT NULL, attempt_key TEXT NOT NULL,
              PRIMARY KEY(provider,handle), UNIQUE(attempt_key));
            CREATE TABLE IF NOT EXISTS adaptive_branch_delegations (
              execution_id TEXT PRIMARY KEY, delegation_id TEXT NOT NULL UNIQUE,
              request_digest TEXT NOT NULL, document TEXT NOT NULL);
            """, cancellationToken);
    }

    public async ValueTask<bool> TryCreateIntentAsync(ExternalOperationRecoveryIntent intent, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var old = await Read(connection, transaction, intent.Request.Identity, cancellationToken);
        if (old is not null)
        {
            if (AdaptiveRevisionContracts.Digest(old.Intent.Request) != AdaptiveRevisionContracts.Digest(intent.Request))
                throw new InvalidOperationException("Provider attempt semantic binding conflict.");
            return false;
        }
        await Execute(connection, transaction, "INSERT INTO adaptive_delegation_attempts(attempt_key,delegation_id,document) VALUES($k,$d,$doc)", cancellationToken,
            ("$k", Key(intent.Request.Identity)), ("$d", intent.Request.Identity.DelegationId.Value.ToString("D")),
            ("$doc", Encode(new ExternalOperationRecoveryRecord(intent))));
        await Witness(connection, transaction, new ExternalOperationRecoveryRecord(intent), cancellationToken);
        transaction.Commit();
        return true;
    }

    public async ValueTask<ExternalOperationRecoveryRecord?> ReadAsync(ExternalOperationStartIdentity identity, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        return await Read(connection, null, identity, cancellationToken);
    }

    public ValueTask CaptureAsync(ExternalOperationStartIdentity identity, ExternalOperationHandleCapture capture,
        CancellationToken cancellationToken = default) => Update(identity, async (connection, transaction, prior) =>
    {
        _ = new ExternalOperationRecoveryRecord(prior.Intent, capture);
        if (prior.Capture is not null)
        {
            if (AdaptiveRevisionContracts.Digest(prior.Capture.Handle) != AdaptiveRevisionContracts.Digest(capture.Handle))
                throw new InvalidOperationException("Provider handle binding conflict.");
            return prior;
        }
        // Global provider-handle uniqueness prevents adoption of another attempt's operation.
        await Execute(connection, transaction,
            "INSERT INTO adaptive_provider_handles(provider,handle,attempt_key) VALUES($p,$h,$k)", cancellationToken,
            ("$p", capture.Handle.Provider), ("$h", capture.Handle.Value), ("$k", Key(identity)));
        return new ExternalOperationRecoveryRecord(prior.Intent, capture, prior.Observation, prior.Result, ExternalOperationRecoveryState.KnownObservable);
    }, cancellationToken);

    public ValueTask RecordObservationAsync(ExternalOperationStartIdentity identity, ExternalOperationObservation observation,
        CancellationToken cancellationToken = default) => Update(identity, (_, _, prior) =>
    {
        if (prior.Observation is not null) ExternalOperationObservationRules.ValidateProgression(prior.Observation, observation);
        if (prior.Result is not null && observation.State != prior.Result.State)
            throw new InvalidOperationException("Persisted terminal result cannot change.");
        return Task.FromResult(new ExternalOperationRecoveryRecord(prior.Intent, prior.Capture, observation, prior.Result, prior.RecoveryState));
    }, cancellationToken);

    public ValueTask RecordResultAsync(ExternalOperationStartIdentity identity, ExternalOperationResult result,
        CancellationToken cancellationToken = default) => Update(identity, (_, _, prior) =>
    {
        if (prior.Result is not null && AdaptiveRevisionContracts.Digest(prior.Result) != AdaptiveRevisionContracts.Digest(result))
            throw new InvalidOperationException("Terminal provider result is immutable.");
        if (prior.Observation is not null && IsTerminal(prior.Observation.State) &&
            prior.Observation.State != result.State) throw new InvalidOperationException("Result contradicts terminal observation.");
        return Task.FromResult(new ExternalOperationRecoveryRecord(prior.Intent, prior.Capture, prior.Observation, result, ExternalOperationRecoveryState.Completed));
    }, cancellationToken);

    public ValueTask MarkKnownNotReconnectableAsync(ExternalOperationStartIdentity identity, CancellationToken cancellationToken = default) =>
        Update(identity, (_, _, prior) => Task.FromResult(new ExternalOperationRecoveryRecord(prior.Intent, prior.Capture,
            prior.Observation, prior.Result, prior.Result is null ? ExternalOperationRecoveryState.KnownNotReconnectable : ExternalOperationRecoveryState.Completed)), cancellationToken);

    public async Task BindAsync(Guid executionId, DelegationHandle handle, string requestDigest, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var old = await Scalar(connection, transaction,
            "SELECT document FROM adaptive_branch_delegations WHERE execution_id=$e", cancellationToken, ("$e", executionId.ToString("D")));
        var binding = new BranchDelegationBinding(executionId, handle.DelegationId, requestDigest);
        var json = JsonSerializer.Serialize(binding);
        if (old is not null)
        {
            if (old != json) throw new InvalidOperationException("Delegation execution binding is immutable.");
            return;
        }
        await Execute(connection, transaction,
            "INSERT INTO adaptive_branch_delegations(execution_id,delegation_id,request_digest,document) VALUES($e,$d,$r,$doc)", cancellationToken,
            ("$e", executionId.ToString("D")), ("$d", handle.DelegationId.Value.ToString("D")), ("$r", requestDigest), ("$doc", json));
        transaction.Commit();
    }

    public async Task<BranchDelegationBinding?> GetBindingAsync(Guid executionId, string requestDigest, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        var json = await Scalar(connection, null, "SELECT document FROM adaptive_branch_delegations WHERE execution_id=$e", cancellationToken,
            ("$e", executionId.ToString("D")));
        if (json is null) return null;
        var binding = JsonSerializer.Deserialize<BranchDelegationBinding>(json)!;
        if (binding.RequestDigest != requestDigest) throw new InvalidOperationException("Delegation input binding changed.");
        return binding;
    }

    public async Task<ExternalOperationRecoveryRecord[]> ListAsync(DelegationId delegationId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM adaptive_delegation_attempts WHERE delegation_id=$d ORDER BY rowid";
        command.Parameters.AddWithValue("$d", delegationId.Value.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ExternalOperationRecoveryRecord>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(Decode(reader.GetString(0)));
        return result.ToArray();
    }

    private async ValueTask Update(ExternalOperationStartIdentity identity,
        Func<SqliteConnection, SqliteTransaction, ExternalOperationRecoveryRecord, Task<ExternalOperationRecoveryRecord>> update, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        using var transaction = connection.BeginTransaction();
        var prior = await Read(connection, transaction, identity, ct) ?? throw new InvalidOperationException("No durable start intent.");
        var next = await update(connection, transaction, prior);
        await Execute(connection, transaction, "UPDATE adaptive_delegation_attempts SET document=$doc WHERE attempt_key=$k", ct,
            ("$doc", Encode(next)), ("$k", Key(identity)));
        await Witness(connection, transaction, next, ct);
        transaction.Commit();
    }

    private static async Task<ExternalOperationRecoveryRecord?> Read(SqliteConnection c, SqliteTransaction? t,
        ExternalOperationStartIdentity identity, CancellationToken ct)
    {
        var json = await Scalar(c, t, "SELECT document FROM adaptive_delegation_attempts WHERE attempt_key=$k", ct, ("$k", Key(identity)));
        if (json is null) return null;
        var record = Decode(json);
        if (record.Intent.Request.Identity != identity) throw new InvalidOperationException("Provider attempt identity conflict.");
        return record;
    }
    private static string Key(ExternalOperationStartIdentity i) => AdaptiveRevisionContracts.Digest(new
    {
        i.DelegationId,
        i.WorkflowRun,
        i.StructuralNode,
        i.NodeGeneration,
        i.ExecutionAttemptId
    });
    private static bool IsTerminal(ExternalOperationState state) => state is ExternalOperationState.Succeeded or
        ExternalOperationState.Failed or ExternalOperationState.Cancelled or ExternalOperationState.TimedOut or ExternalOperationState.Rejected;

    private static async Task Witness(SqliteConnection c, SqliteTransaction t, ExternalOperationRecoveryRecord record, CancellationToken ct)
    {
        if (await Scalar(c, t, "SELECT name FROM sqlite_master WHERE type='table' AND name='adaptive_execution_witness'", ct) is null) return;
        await Execute(c, t, """
            INSERT INTO adaptive_execution_witness(run_id,witness_key,digest)
            SELECT execution_id,$k,$digest FROM adaptive_branch_delegations WHERE delegation_id=$d
            ON CONFLICT(run_id,witness_key) DO UPDATE SET digest=excluded.digest
            """, ct, ("$k", "qingniao/" + Key(record.Intent.Request.Identity)),
            ("$digest", AdaptiveRevisionContracts.Digest(Encode(record))),
            ("$d", record.Intent.Request.Identity.DelegationId.Value.ToString("D")));
    }

    // Result has multiple public constructors. Persist an explicit v1 wire shape instead of relying on
    // serializer constructor selection; reconstruction runs the original Qingniao contract validators.
    private sealed record ResultWire(ExternalOperationHandle Handle, ExternalOperationState State, DateTimeOffset CompletedAt,
        string Summary, DelegationArtifactReference[] Artifacts, ExternalOperationProvenanceSnapshot? Provenance,
        ExternalOperationFailure? Failure, CandidateRevisionReference? Candidate);
    private sealed record RecordWire(string Version, ExternalOperationRecoveryIntent Intent, ExternalOperationHandleCapture? Capture,
        ExternalOperationObservation? Observation, ResultWire? Result, ExternalOperationRecoveryState RecoveryState);
    private static string Encode(ExternalOperationRecoveryRecord record) => JsonSerializer.Serialize(new RecordWire("aaaw-provider-journal/v1",
        record.Intent, record.Capture, record.Observation, record.Result is not { } result ? null : new ResultWire(result.Handle,
            result.State, result.CompletedAt, result.Summary, result.Artifacts.ToArray(), result.Provenance, result.Failure, result.Candidate), record.RecoveryState));
    internal static ExternalOperationRecoveryRecord Decode(string json)
    {
        var wire = JsonSerializer.Deserialize<RecordWire>(json, SerializerOptions)!;
        if (wire.Version != "aaaw-provider-journal/v1") throw new InvalidOperationException("Unknown delegation journal version.");
        var result = wire.Result is not { } r ? null : new ExternalOperationResult(r.Handle, r.State, r.CompletedAt,
            r.Summary, r.Artifacts, r.Provenance, r.Failure, r.Candidate);
        return new ExternalOperationRecoveryRecord(wire.Intent, wire.Capture, wire.Observation, result, wire.RecoveryState);
    }
    private static async Task Execute(SqliteConnection c, SqliteTransaction? t, string sql, CancellationToken ct, params (string Key, string Value)[] args)
    {
        await using var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value);
        await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task<string?> Scalar(SqliteConnection c, SqliteTransaction? t, string sql, CancellationToken ct, params (string Key, string Value)[] args)
    {
        await using var command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value);
        return await command.ExecuteScalarAsync(ct) as string;
    }
}

public sealed record BranchDelegationBinding(Guid ExecutionId, DelegationId DelegationId, string RequestDigest);
