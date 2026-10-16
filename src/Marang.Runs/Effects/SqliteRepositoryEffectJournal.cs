using System.Text.Json;
using Microsoft.Data.Sqlite;
using Marang.Runs.Integrity;
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
    string? DecisionReason, string? SnapshotIdentity, string? SnapshotVersion, DateTimeOffset CheckedAt,
    string? GraphDigest = null, Guid? DependentEffectId = null, Guid? PrerequisiteEffectId = null, string? PrerequisiteCausalDigest = null,
    string? ObservedStateDigest = null);

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
            var format = await RepositoryProfileCompatibility.InspectFormatAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            command.CommandText = format == RepositoryProfileFormat.Current
                ? "INSERT INTO repository_effects(effect_id,workflow_id,repository_id,intent_digest,stage,document,updated_at,record_format) VALUES($id,$workflow,$repository,$digest,$stage,$doc,$at,1)"
                : "INSERT INTO repository_effects(effect_id,workflow_id,repository_id,intent_digest,stage,document,updated_at) VALUES($id,$workflow,$repository,$digest,$stage,$doc,$at)";
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
        using var transaction = connection.BeginTransaction();
        return await ReadByIdAsync(connection, transaction, effectId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RepositoryEffectRecord>> ListForWorkflowAsync(Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        var records = await ReadAllAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        return records.Where(r => r.Intent.WorkflowId == workflowId)
            .OrderBy(r => r.UpdatedAt).ThenBy(r => r.Intent.EffectId).ToArray();
    }

    public async Task<IReadOnlyList<RepositoryEffectRecord>> ListForRepositoryAsync(string repositoryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        var records = await ReadAllAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        return records.Where(r => r.Intent.RepositoryId == repositoryId)
            .OrderBy(r => r.UpdatedAt).ThenBy(r => r.Intent.EffectId).ToArray();
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
        using var transaction = connection.BeginTransaction();
        var verified = await ReadAndVerifyByIdAsync(connection, transaction, effectId, cancellationToken).ConfigureAwait(false);
        return verified?.Events ?? [];
    }

    internal static async Task<RepositoryEffectRecord?> ReadByIdAsync(SqliteConnection connection, SqliteTransaction? transaction,
        Guid effectId, CancellationToken cancellationToken) =>
        (await ReadAndVerifyByIdAsync(connection, transaction, effectId, cancellationToken).ConfigureAwait(false))?.Record;

    private static async Task<List<RepositoryEffectRecord>> ReadAllAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ValidateGlobalEventSequenceAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var ids = new HashSet<Guid>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT effect_id FROM repository_effects ORDER BY effect_id";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!Guid.TryParse(reader.GetString(0), out var id) || id == Guid.Empty || !ids.Add(id))
                    throw new InvalidDataException("Effect journal contains an invalid projection identity.");
            }
        }

        var records = new List<RepositoryEffectRecord>(ids.Count);
        foreach (var id in ids)
        {
            var verified = await ReadAndVerifyByIdAsync(connection, transaction, id, cancellationToken,
                validateGlobalSequence: false).ConfigureAwait(false)
                ?? throw new InvalidDataException("Effect projection disappeared during a consistent journal read.");
            records.Add(verified.Record);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT DISTINCT effect_id FROM repository_effect_events";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!Guid.TryParse(reader.GetString(0), out var id) || id == Guid.Empty || !ids.Contains(id))
                    throw new InvalidDataException("Effect events exist without their durable projection.");
            }
        }
        return records;
    }

    private sealed record VerifiedEffect(RepositoryEffectRecord Record, IReadOnlyList<RepositoryEffectEvent> Events);

    private static async Task<VerifiedEffect?> ReadAndVerifyByIdAsync(SqliteConnection connection, SqliteTransaction? transaction,
        Guid effectId, CancellationToken cancellationToken, bool validateGlobalSequence = true)
    {
        if (validateGlobalSequence)
            await ValidateGlobalEventSequenceAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT effect_id,workflow_id,repository_id,intent_digest,stage,document,updated_at FROM repository_effects WHERE effect_id=$id";
        command.Parameters.AddWithValue("$id", effectId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            var orphanEvents = await ReadEventsAsync(connection, transaction, effectId, cancellationToken).ConfigureAwait(false);
            if (orphanEvents.Count != 0) throw new InvalidDataException("Effect events exist without their durable projection.");
            return null;
        }

        var projectionId = reader.GetString(0);
        var workflowId = reader.GetString(1);
        var repositoryId = reader.GetString(2);
        var intentDigest = reader.GetString(3);
        var stage = reader.GetString(4);
        var json = reader.GetString(5);
        var updatedAt = reader.GetString(6);
        await reader.DisposeAsync().ConfigureAwait(false);
        var record = DeserializeRecord(json);
        if (projectionId != record.Intent.EffectId.ToString("D") || projectionId != effectId.ToString("D") ||
            workflowId != record.Intent.WorkflowId.ToString("D") || repositoryId != record.Intent.RepositoryId ||
            intentDigest != record.Intent.IntentDigest || stage != record.Stage.ToString() ||
            !DateTimeOffset.TryParseExact(updatedAt, "O", null, System.Globalization.DateTimeStyles.RoundtripKind, out var columnTime) ||
            columnTime != record.UpdatedAt)
            throw new InvalidDataException("Effect projection columns disagree with the serialized durable record.");

        var events = await ReadEventsAsync(connection, transaction, effectId, cancellationToken).ConfigureAwait(false);
        ValidateHistory(record, events);
        return new VerifiedEffect(record, events);
    }

    // Event sequence is a table-wide AUTOINCREMENT writer invariant. Effects interleave;
    // their per-effect event IDs are intentionally not contiguous.
    internal static async Task ValidateGlobalEventSequenceAsync(SqliteConnection connection, SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        _ = await RepositoryProfileCompatibility.InspectFormatAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        long count;
        long minimum;
        long maximum;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*), COALESCE(MIN(sequence),0), COALESCE(MAX(sequence),0) FROM repository_effect_events";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Effect event sequence could not be inspected.");
            count = reader.GetInt64(0);
            minimum = reader.GetInt64(1);
            maximum = reader.GetInt64(2);
        }

        bool hasSequence;
        long highWater = 0;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT seq FROM sqlite_sequence WHERE name='repository_effect_events'";
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            hasSequence = value is not null and not DBNull;
            if (hasSequence)
                highWater = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (count == 0)
        {
            if (hasSequence)
                throw new InvalidDataException("Empty effect event history has an AUTOINCREMENT high-water mark.");
            return;
        }

        if (minimum != 1 || maximum != count || highWater != maximum)
            throw new InvalidDataException("Effect event sequence has a gap or disagrees with its AUTOINCREMENT high-water mark.");
    }

    private static async Task<List<RepositoryEffectEvent>> ReadEventsAsync(SqliteConnection connection, SqliteTransaction? transaction,
        Guid effectId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT event_digest,document FROM repository_effect_events WHERE effect_id=$id ORDER BY sequence";
        command.Parameters.AddWithValue("$id", effectId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var events = new List<RepositoryEffectEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var digest = reader.GetString(0);
            var json = reader.GetString(1);
            if (RepositoryStateSnapshot.Hash(json) != digest)
                throw new InvalidDataException("Effect event digest does not match its durable document.");
            RepositoryEffectEvent item;
            try
            {
                item = JsonSerializer.Deserialize<RepositoryEffectEvent>(json, Json)
                    ?? throw new InvalidDataException("Effect journal contains invalid event JSON.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Effect journal contains invalid event JSON.", exception);
            }
            if (item.EffectId != effectId || !Enum.IsDefined(item.Stage) ||
                item.Classification is { } classification && !Enum.IsDefined(classification) ||
                item.ObservedState is { } observed && !observed.HasValidDigest() || item.AuthorityEvidence is null ||
                item.Validation is { } validation && !ValidValidation(validation))
                throw new InvalidDataException("Effect event contains invalid or inconsistent durable fields.");
            events.Add(item);
        }
        return events;
    }

    private static void ValidateHistory(RepositoryEffectRecord record, IReadOnlyList<RepositoryEffectEvent> events)
    {
        if (events.Count == 0) throw new InvalidDataException("Effect projection has no append-only event history.");
        if (events[0].Stage != RepositoryEffectStage.Prepared || events[0].Classification is not null ||
            events[0].ObservedState is not null || events[0].Validation is not null || events[0].AuthorityEvidence.Count != 0)
            throw new InvalidDataException("Effect event history does not begin with its prepared intent.");

        var authority = new List<RepositoryEffectAuthorizationEvidence>();
        for (var i = 0; i < events.Count; i++)
        {
            var current = events[i];
            if (i > 0 && !Allowed(events[i - 1].Stage, current.Stage))
                throw new InvalidDataException("Effect event history contains an illegal lifecycle transition.");
            if (current.ObservedState is { } observed && observed.RepositoryId != record.Intent.RepositoryId)
                throw new InvalidDataException("Effect event observation belongs to another repository.");
            if (current.Stage is RepositoryEffectStage.AppliedObserved or RepositoryEffectStage.ValidationPassed or RepositoryEffectStage.ValidationFailed)
            {
                var exactObservation = current.ObservedState ?? (i == 0 ? null : events[i - 1].ObservedState);
                if (current.Classification != RepositoryEffectClassification.DefinitelyApplied || exactObservation is null ||
                    exactObservation.StateDigest != record.Intent.ExpectedPostState.StateDigest)
                    throw new InvalidDataException("Applied effect event lacks its exact post-state observation.");
            }
            if (current.Validation is { } validation &&
                (current.ObservedState ?? (i == 0 ? null : events[i - 1].ObservedState))?.StateDigest != validation.TargetStateDigest)
                throw new InvalidDataException("Effect event validation is not bound to its observed state.");
            authority.AddRange(current.AuthorityEvidence);
        }

        var last = events[^1];
        if (last.Stage != record.Stage || last.Classification != record.Classification ||
            !SameJson(last.ObservedState, record.ObservedState) || !SameJson(last.Validation, record.Validation) ||
            last.RecordedAt != record.UpdatedAt || !SameJson(authority, record.AuthorityEvidence))
            throw new InvalidDataException("Effect projection disagrees with its append-only event history.");
    }

    private static bool SameJson<T>(T? left, T? right) => JsonSerializer.Serialize(left, Json) == JsonSerializer.Serialize(right, Json);

    private static bool ValidValidation(RepositoryEffectValidation validation) =>
        RepositoryStateSnapshot.IsSha256(validation.TargetStateDigest) && !string.IsNullOrWhiteSpace(validation.Target) &&
        !string.IsNullOrWhiteSpace(validation.CommandIdentity) && !string.IsNullOrWhiteSpace(validation.OutputDigest) &&
        !string.IsNullOrWhiteSpace(validation.Evidence);

    internal static RepositoryEffectRecord DeserializeRecord(string json)
    {
        RepositoryEffectRecord record;
        try
        {
            record = JsonSerializer.Deserialize<RepositoryEffectRecord>(json, Json)
                ?? throw new InvalidDataException("Effect journal contains invalid JSON.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Effect journal contains invalid JSON.", exception);
        }
        if (!record.Intent.HasValidDigest() || record.Intent.EffectId == Guid.Empty || record.Intent.WorkflowId == Guid.Empty ||
            !record.Intent.BaseState.HasValidDigest() || !record.Intent.ExpectedPostState.HasValidDigest() ||
            !Enum.IsDefined(record.Stage) || record.Classification is { } classification && !Enum.IsDefined(classification) ||
            record.ObservedState is { } observed && !observed.HasValidDigest() || record.AuthorityEvidence is null ||
            record.Validation is { } validation && !ValidValidation(validation))
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
