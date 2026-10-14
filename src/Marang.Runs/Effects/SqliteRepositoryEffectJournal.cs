using System.Text.Json;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Sqlite;

namespace Marang.Runs.Effects;

public enum RepositoryEffectStage
{
    Prepared,
    IntentCommitted,
    Applying,
    AppliedObserved,
    ValidationPassed,
    ValidationFailed,
    Blocked
}

public sealed record RepositoryEffectValidation(
    string TargetStateDigest,
    string Target,
    string CommandIdentity,
    bool Passed,
    string OutputDigest,
    string Evidence);

public sealed record RepositoryEffectAuthorizationEvidence(
    string IntentDigest, string TargetPath, string RequestIdentity, string Status,
    string TenantId, string SubjectId, string WorkflowId, string RevisionId,
    string? DecisionReason, string? SnapshotIdentity, string? SnapshotVersion, DateTimeOffset CheckedAt);

public sealed record RepositoryEffectRecord(
    RepositoryEffectIntent Intent,
    RepositoryEffectStage Stage,
    RepositoryEffectClassification? Classification,
    RepositoryStateSnapshot? ObservedState,
    RepositoryEffectValidation? Validation,
    IReadOnlyList<RepositoryEffectAuthorizationEvidence> AuthorityEvidence,
    DateTimeOffset UpdatedAt);

public sealed record RepositoryEffectEvent(Guid EffectId, RepositoryEffectStage Stage,
    RepositoryEffectClassification? Classification, RepositoryStateSnapshot? ObservedState,
    RepositoryEffectValidation? Validation, IReadOnlyList<RepositoryEffectAuthorizationEvidence> AuthorityEvidence,
    DateTimeOffset RecordedAt);

/// <summary>Durable, append-only effect history with a transactional current-state projection.</summary>
public sealed class SqliteRepositoryEffectJournal(IZhinuSqliteDatabase database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS repository_effects (
              effect_id TEXT PRIMARY KEY, workflow_id TEXT NOT NULL, repository_id TEXT NOT NULL, intent_digest TEXT NOT NULL,
              stage TEXT NOT NULL, document TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_repository_effects_workflow ON repository_effects(workflow_id, updated_at);
            CREATE INDEX IF NOT EXISTS ix_repository_effects_repository ON repository_effects(repository_id, updated_at);
            CREATE TABLE IF NOT EXISTS repository_effect_events (
              sequence INTEGER PRIMARY KEY AUTOINCREMENT, effect_id TEXT NOT NULL, event_digest TEXT NOT NULL,
              document TEXT NOT NULL, UNIQUE(effect_id,event_digest));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RepositoryEffectRecord> PrepareAsync(RepositoryEffectIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        VerifyIntent(intent);
        var now = database.TimeProvider.GetUtcNow();
        var record = new RepositoryEffectRecord(intent, RepositoryEffectStage.Prepared, null, null, null, [], now);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        var existing = await ReadByIdAsync(connection, transaction, intent.EffectId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Intent.IntentDigest != intent.IntentDigest || existing.Intent.PatchIdentity != intent.PatchIdentity)
                throw new InvalidOperationException("Effect identity is already bound to a different immutable intent.");
            transaction.Commit();
            return existing;
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO repository_effects(effect_id,workflow_id,repository_id,intent_digest,stage,document,updated_at) VALUES($id,$workflow,$repository,$digest,$stage,$doc,$at)";
            command.Parameters.AddWithValue("$id", intent.EffectId.ToString("D"));
            command.Parameters.AddWithValue("$workflow", intent.WorkflowId.ToString("D"));
            command.Parameters.AddWithValue("$repository", intent.RepositoryId);
            command.Parameters.AddWithValue("$digest", intent.IntentDigest);
            command.Parameters.AddWithValue("$stage", RepositoryEffectStage.Prepared.ToString());
            command.Parameters.AddWithValue("$doc", JsonSerializer.Serialize(record, Json));
            command.Parameters.AddWithValue("$at", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await AppendEventAsync(connection, transaction, new(intent.EffectId, record.Stage, null, null, null, [], now), cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return record;
    }

    public async Task<RepositoryEffectRecord?> GetAsync(Guid effectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadByIdAsync(connection, null, effectId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RepositoryEffectRecord>> ListForWorkflowAsync(Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM repository_effects WHERE workflow_id=$workflow ORDER BY updated_at,effect_id";
        command.Parameters.AddWithValue("$workflow", workflowId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<RepositoryEffectRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            records.Add(DeserializeRecord(reader.GetString(0)));
        return records;
    }

    public async Task<IReadOnlyList<RepositoryEffectRecord>> ListForRepositoryAsync(string repositoryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM repository_effects WHERE repository_id=$repository ORDER BY updated_at,effect_id";
        command.Parameters.AddWithValue("$repository", repositoryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<RepositoryEffectRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            records.Add(DeserializeRecord(reader.GetString(0)));
        return records;
    }

    public async Task<RepositoryEffectRecord> TransitionAsync(Guid effectId, RepositoryEffectStage stage,
        RepositoryEffectClassification? classification = null, RepositoryStateSnapshot? observedState = null,
        RepositoryEffectValidation? validation = null, CancellationToken cancellationToken = default,
        IReadOnlyList<RepositoryEffectAuthorizationEvidence>? authorityEvidence = null)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        var previous = await ReadByIdAsync(connection, transaction, effectId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Effect '{effectId}' has no durable intent.");
        if (!previous.Intent.HasValidDigest()) throw new InvalidDataException("Effect journal intent digest is invalid.");
        if (!Allowed(previous.Stage, stage))
            throw new InvalidOperationException($"Illegal effect lifecycle transition {previous.Stage} -> {stage}.");
        if (observedState is not null && observedState.RepositoryId != previous.Intent.RepositoryId)
            throw new InvalidOperationException("Observed state belongs to another repository.");
        if (observedState is not null && !observedState.HasValidDigest())
            throw new InvalidOperationException("Observed state snapshot digest is invalid.");
        if (stage is RepositoryEffectStage.AppliedObserved or RepositoryEffectStage.ValidationPassed or RepositoryEffectStage.ValidationFailed)
        {
            var exactObservation = observedState ?? previous.ObservedState;
            if (classification != RepositoryEffectClassification.DefinitelyApplied || exactObservation is null ||
                exactObservation.StateDigest != previous.Intent.ExpectedPostState.StateDigest)
                throw new InvalidOperationException("Applied and validation stages require the exact expected post-state observation.");
        }
        if (validation is not null)
        {
            if ((observedState ?? previous.ObservedState)?.StateDigest != validation.TargetStateDigest)
                throw new InvalidOperationException("Validation must target the exact observed repository state.");
            ArgumentException.ThrowIfNullOrWhiteSpace(validation.Target);
            ArgumentException.ThrowIfNullOrWhiteSpace(validation.CommandIdentity);
            ArgumentException.ThrowIfNullOrWhiteSpace(validation.OutputDigest);
            ArgumentException.ThrowIfNullOrWhiteSpace(validation.Evidence);
        }
        var now = database.TimeProvider.GetUtcNow();
        var next = previous with
        {
            Stage = stage,
            Classification = classification ?? previous.Classification,
            ObservedState = observedState ?? previous.ObservedState,
            Validation = validation ?? previous.Validation,
            AuthorityEvidence = authorityEvidence is null ? previous.AuthorityEvidence : previous.AuthorityEvidence.Concat(authorityEvidence).ToArray(),
            UpdatedAt = now
        };
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE repository_effects SET stage=$stage,document=$doc,updated_at=$at WHERE effect_id=$id AND intent_digest=$digest AND stage=$old";
            command.Parameters.AddWithValue("$stage", stage.ToString()); command.Parameters.AddWithValue("$doc", JsonSerializer.Serialize(next, Json));
            command.Parameters.AddWithValue("$at", now.ToString("O")); command.Parameters.AddWithValue("$id", effectId.ToString("D"));
            command.Parameters.AddWithValue("$digest", previous.Intent.IntentDigest); command.Parameters.AddWithValue("$old", previous.Stage.ToString());
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Effect lifecycle changed concurrently.");
        }
        await AppendEventAsync(connection, transaction, new(effectId, stage, next.Classification, next.ObservedState, next.Validation,
            authorityEvidence ?? [], now), cancellationToken).ConfigureAwait(false);
        await ProjectWitnessAsync(connection, transaction, next, cancellationToken).ConfigureAwait(false);
        await AppendEvidenceAsync(connection, transaction, next, now, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return next;
    }

    public async Task<IReadOnlyList<RepositoryEffectEvent>> ListEventsAsync(Guid effectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM repository_effect_events WHERE effect_id=$id ORDER BY sequence";
        command.Parameters.AddWithValue("$id", effectId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var events = new List<RepositoryEffectEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            events.Add(JsonSerializer.Deserialize<RepositoryEffectEvent>(reader.GetString(0), Json)
                ?? throw new InvalidDataException("Effect journal contains invalid event JSON."));
        return events;
    }

    private static async Task<RepositoryEffectRecord?> ReadByIdAsync(SqliteConnection connection, SqliteTransaction? transaction,
        Guid effectId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT document FROM repository_effects WHERE effect_id=$id";
        command.Parameters.AddWithValue("$id", effectId.ToString("D"));
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null ? null : DeserializeRecord(json);
    }

    private static RepositoryEffectRecord DeserializeRecord(string json)
    {
        var record = JsonSerializer.Deserialize<RepositoryEffectRecord>(json, Json)
            ?? throw new InvalidDataException("Effect journal contains invalid JSON.");
        if (!record.Intent.HasValidDigest() || record.Intent.EffectId == Guid.Empty || record.Intent.WorkflowId == Guid.Empty ||
            record.ObservedState is { } observed && !observed.HasValidDigest() ||
            record.Validation is { } validation && string.IsNullOrWhiteSpace(validation.CommandIdentity))
            throw new InvalidDataException("Effect journal contains a tampered or incomplete durable record.");
        return record;
    }

    private static async Task AppendEventAsync(SqliteConnection c, SqliteTransaction t, RepositoryEffectEvent item, CancellationToken ct)
    {
        var document = JsonSerializer.Serialize(item, Json);
        var digest = RepositoryStateSnapshot.Hash(document);
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = "INSERT INTO repository_effect_events(effect_id,event_digest,document) VALUES($id,$digest,$doc)";
        command.Parameters.AddWithValue("$id", item.EffectId.ToString("D")); command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$doc", document); await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task ProjectWitnessAsync(SqliteConnection c, SqliteTransaction t, RepositoryEffectRecord record, CancellationToken ct)
    {
        await using var exists = c.CreateCommand(); exists.Transaction = t;
        exists.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='adaptive_execution_witness'";
        if (await exists.ExecuteScalarAsync(ct).ConfigureAwait(false) is null) return;
        var semantic = RepositoryStateSnapshot.Hash($"{record.Intent.IntentDigest}\n{record.Stage}\n{record.Classification}\n{record.ObservedState?.StateDigest}\n{record.Validation?.TargetStateDigest}\n{record.Validation?.CommandIdentity}\n{record.Validation?.Passed}\n{record.Validation?.OutputDigest}");
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = "INSERT INTO adaptive_execution_witness(run_id,witness_key,digest) VALUES($run,$key,$digest) ON CONFLICT(run_id,witness_key) DO UPDATE SET digest=excluded.digest WHERE digest<>excluded.digest";
        command.Parameters.AddWithValue("$run", record.Intent.ExecutionId.ToString("D"));
        command.Parameters.AddWithValue("$key", "repository-effect:" + record.Intent.EffectId.ToString("D"));
        command.Parameters.AddWithValue("$digest", semantic); await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task AppendEvidenceAsync(SqliteConnection c, SqliteTransaction t, RepositoryEffectRecord record, DateTimeOffset now, CancellationToken ct)
    {
        await using var exists = c.CreateCommand(); exists.Transaction = t;
        exists.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='adaptive_evidence'";
        if (await exists.ExecuteScalarAsync(ct).ConfigureAwait(false) is null) return;
        var evidence = JsonSerializer.Serialize(new
        {
            effectId = record.Intent.EffectId,
            stage = record.Stage.ToString(),
            intentDigest = record.Intent.IntentDigest,
            classification = record.Classification?.ToString(),
            stateDigest = record.ObservedState?.StateDigest,
            validationPassed = record.Validation?.Passed,
            at = now
        }, Json);
        await using var command = c.CreateCommand(); command.Transaction = t;
        command.CommandText = "INSERT INTO adaptive_evidence(workflow_id,kind,document) VALUES($workflow,'RepositoryEffect',$doc)";
        command.Parameters.AddWithValue("$workflow", record.Intent.WorkflowId.ToString("D")); command.Parameters.AddWithValue("$doc", evidence);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static bool Allowed(RepositoryEffectStage old, RepositoryEffectStage next) => (old, next) switch
    {
        (RepositoryEffectStage.Prepared, RepositoryEffectStage.IntentCommitted or RepositoryEffectStage.Blocked) => true,
        (RepositoryEffectStage.IntentCommitted, RepositoryEffectStage.Applying or RepositoryEffectStage.AppliedObserved or RepositoryEffectStage.Blocked) => true,
        (RepositoryEffectStage.Applying, RepositoryEffectStage.Applying or RepositoryEffectStage.AppliedObserved or RepositoryEffectStage.Blocked) => true,
        (RepositoryEffectStage.AppliedObserved, RepositoryEffectStage.ValidationPassed or RepositoryEffectStage.ValidationFailed or RepositoryEffectStage.Blocked) => true,
        (RepositoryEffectStage.ValidationFailed, RepositoryEffectStage.ValidationPassed or RepositoryEffectStage.ValidationFailed or RepositoryEffectStage.Blocked) => true,
        (RepositoryEffectStage.Blocked, RepositoryEffectStage.Blocked) => true,
        _ => false
    };

    private static void VerifyIntent(RepositoryEffectIntent intent)
    {
        var recomputed = RepositoryEffectIntent.Create(intent.EffectId, intent.WorkflowId, intent.RevisionId, intent.LogicalStepId,
            intent.ExecutionId, intent.AttemptId, intent.RepositoryId, intent.AuthorityScope, intent.BaseState, intent.Files, intent.CreatedAt);
        if (recomputed.IntentDigest != intent.IntentDigest || recomputed.PatchIdentity != intent.PatchIdentity ||
            recomputed.ExpectedPostState.StateDigest != intent.ExpectedPostState.StateDigest)
            throw new InvalidOperationException("Repository effect intent digest does not match its immutable contents.");
    }
}
