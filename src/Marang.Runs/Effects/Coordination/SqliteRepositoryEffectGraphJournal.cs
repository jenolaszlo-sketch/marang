using System.Text.Json;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu.Sqlite;

namespace Marang.Runs.Effects;

/// <summary>Durable archive of immutable causal graph revisions and their validation evidence.</summary>
public sealed class SqliteRepositoryEffectGraphJournal(IZhinuSqliteDatabase database, SqliteRepositoryEffectJournal effectJournal)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var c = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = c.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS repository_effect_graphs (
              workflow_id TEXT NOT NULL, revision_id TEXT NOT NULL, graph_digest TEXT NOT NULL, document TEXT NOT NULL,
              PRIMARY KEY(workflow_id,revision_id));
            CREATE TABLE IF NOT EXISTS repository_causal_effects (
              effect_id TEXT PRIMARY KEY, causal_digest TEXT NOT NULL, document TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS repository_effect_graph_validations (
              workflow_id TEXT NOT NULL, revision_id TEXT NOT NULL, graph_digest TEXT NOT NULL, target_state_digest TEXT NOT NULL,
              document TEXT NOT NULL, PRIMARY KEY(workflow_id,revision_id));
            CREATE TABLE IF NOT EXISTS repository_effect_prerequisite_authority (
              sequence INTEGER PRIMARY KEY AUTOINCREMENT, workflow_id TEXT NOT NULL, revision_id TEXT NOT NULL,
              graph_digest TEXT NOT NULL, dependent_effect_id TEXT NOT NULL, prerequisite_effect_id TEXT NOT NULL,
              observed_state_digest TEXT NOT NULL, document TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_repository_effect_prerequisite_authority_graph
              ON repository_effect_prerequisite_authority(workflow_id,revision_id,sequence);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PrepareGraphAsync(RepositoryEffectGraph graph, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (!graph.Verify()) throw new InvalidOperationException("Repository effect graph digest or contract is invalid.");
        foreach (var selection in graph.Effects)
        {
            var durable = await effectJournal.GetAsync(selection.Effect.Intent.EffectId, cancellationToken).ConfigureAwait(false);
            if (selection.Reuse && (durable is null || durable.Intent.IntentDigest != selection.Effect.Intent.IntentDigest))
                throw new InvalidOperationException("Adopted effect must already have its exact durable intent.");
            if (!selection.Reuse && durable is not null && durable.Intent.IntentDigest != selection.Effect.Intent.IntentDigest)
                throw new InvalidOperationException("Fresh effect id is already bound to different intent bytes.");
        }
        await using var c = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var tx = c.BeginTransaction();
        var existing = await ReadGraphAsync(c, tx, graph.WorkflowId, graph.RevisionId, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.GraphDigest != graph.GraphDigest)
            throw new InvalidOperationException("A revision is already bound to a different immutable graph.");
        if (existing is null)
        {
            await using var insert = c.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = "INSERT INTO repository_effect_graphs(workflow_id,revision_id,graph_digest,document) VALUES($w,$r,$d,$j)";
            insert.Parameters.AddWithValue("$w", graph.WorkflowId.ToString("D")); insert.Parameters.AddWithValue("$r", graph.RevisionId);
            insert.Parameters.AddWithValue("$d", graph.GraphDigest); insert.Parameters.AddWithValue("$j", JsonSerializer.Serialize(graph, Json));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var selection in graph.Effects)
        {
            var effect = selection.Effect;
            await using var read = c.CreateCommand(); read.Transaction = tx;
            read.CommandText = "SELECT causal_digest,document FROM repository_causal_effects WHERE effect_id=$id";
            read.Parameters.AddWithValue("$id", effect.Intent.EffectId.ToString("D"));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var found = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var oldDigest = found ? reader.GetString(0) : null;
            var oldJson = found ? reader.GetString(1) : null;
            await reader.DisposeAsync().ConfigureAwait(false);
            if (found && oldDigest != effect.CausalDigest) throw new InvalidOperationException("Effect id is already bound to another causal contract.");
            if (!found)
            {
                await using var add = c.CreateCommand(); add.Transaction = tx;
                add.CommandText = "INSERT INTO repository_causal_effects(effect_id,causal_digest,document) VALUES($id,$d,$j)";
                add.Parameters.AddWithValue("$id", effect.Intent.EffectId.ToString("D")); add.Parameters.AddWithValue("$d", effect.CausalDigest);
                add.Parameters.AddWithValue("$j", JsonSerializer.Serialize(effect, Json));
                await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (JsonSerializer.Deserialize<RepositoryCausalEffect>(oldJson!, Json)?.HasValidDigest() != true)
                throw new InvalidDataException("Stored causal effect is invalid.");
        }
        tx.Commit();

        foreach (var selection in graph.Effects)
        {
            var stored = await effectJournal.GetAsync(selection.Effect.Intent.EffectId, cancellationToken).ConfigureAwait(false);
            if (selection.Reuse)
            {
                if (stored is null || stored.Intent.IntentDigest != selection.Effect.Intent.IntentDigest)
                    throw new InvalidOperationException("Adopted effect must already have its exact durable intent.");
            }
            else
            {
                if (stored is not null && stored.Intent.IntentDigest != selection.Effect.Intent.IntentDigest)
                    throw new InvalidOperationException("Fresh effect id is already bound to different intent bytes.");
                await effectJournal.PrepareAsync(selection.Effect.Intent, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<RepositoryEffectGraph?> GetGraphAsync(Guid workflowId, string revisionId, CancellationToken cancellationToken = default)
    {
        await using var c = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var graph = await ReadGraphAsync(c, null, workflowId, revisionId, cancellationToken).ConfigureAwait(false);
        if (graph is not null && !graph.Verify()) throw new InvalidDataException("Stored repository effect graph failed verification.");
        return graph;
    }

    public async Task<RepositoryCausalEffect?> GetEffectAsync(Guid effectId, CancellationToken cancellationToken = default)
    {
        await using var c = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = c.CreateCommand(); command.CommandText = "SELECT causal_digest,document FROM repository_causal_effects WHERE effect_id=$id";
        command.Parameters.AddWithValue("$id", effectId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var digest = reader.GetString(0); var json = reader.GetString(1);
        var effect = JsonSerializer.Deserialize<RepositoryCausalEffect>(json, Json) ?? throw new InvalidDataException("Stored causal effect JSON is invalid.");
        if (!effect.HasValidDigest() || effect.CausalDigest != digest) throw new InvalidDataException("Stored causal effect failed digest verification.");
        return effect;
    }

    public async Task<IReadOnlyList<RepositoryEffectGraph>> ListGraphsAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        await using var c = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = c.CreateCommand(); command.CommandText = "SELECT graph_digest,document FROM repository_effect_graphs WHERE workflow_id=$w ORDER BY rowid";
        command.Parameters.AddWithValue("$w", workflowId.ToString("D")); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var graphs = new List<RepositoryEffectGraph>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var digest = reader.GetString(0);
            var graph = JsonSerializer.Deserialize<RepositoryEffectGraph>(reader.GetString(1), Json) ?? throw new InvalidDataException("Stored graph JSON is invalid.");
            if (!graph.Verify() || graph.GraphDigest != digest) throw new InvalidDataException("Stored graph failed verification."); graphs.Add(graph);
        }
        return graphs;
    }

    public async Task RecordWorkflowValidationAsync(RepositoryEffectGraph graph, RepositoryEffectValidation validation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph); ArgumentNullException.ThrowIfNull(validation);
        if (!graph.Verify() || string.IsNullOrWhiteSpace(validation.TargetStateDigest) || !RepositoryStateSnapshot.IsSha256(validation.TargetStateDigest) ||
            string.IsNullOrWhiteSpace(validation.Target) || string.IsNullOrWhiteSpace(validation.CommandIdentity) ||
            string.IsNullOrWhiteSpace(validation.OutputDigest) || !RepositoryStateSnapshot.IsSha256(validation.OutputDigest) || string.IsNullOrWhiteSpace(validation.Evidence))
            throw new InvalidOperationException("Workflow validation evidence or graph is invalid.");
        if (validation.TargetStateDigest != TerminalStateDigest(graph))
            throw new InvalidOperationException("Workflow validation must target the graph's final newly accepted state.");
        var stored = await GetGraphAsync(graph.WorkflowId, graph.RevisionId, cancellationToken).ConfigureAwait(false);
        if (stored?.GraphDigest != graph.GraphDigest) throw new InvalidOperationException("Validation must bind to a prepared immutable graph.");
        await using var c = await database.OpenAsync(cancellationToken).ConfigureAwait(false); using var tx = c.BeginTransaction();
        await using var old = c.CreateCommand(); old.Transaction = tx; old.CommandText = "SELECT graph_digest,target_state_digest,document FROM repository_effect_graph_validations WHERE workflow_id=$w AND revision_id=$r";
        old.Parameters.AddWithValue("$w", graph.WorkflowId.ToString("D")); old.Parameters.AddWithValue("$r", graph.RevisionId);
        await using var reader = await old.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var alreadyRecorded = false;
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetString(0) != graph.GraphDigest || reader.GetString(1) != validation.TargetStateDigest || reader.GetString(2) != JsonSerializer.Serialize(validation, Json))
                throw new InvalidOperationException("Workflow validation ledger is immutable for a graph and target state.");
            alreadyRecorded = true;
        }
        await reader.DisposeAsync().ConfigureAwait(false);
        if (!alreadyRecorded)
        {
            await using var add = c.CreateCommand(); add.Transaction = tx;
            add.CommandText = "INSERT INTO repository_effect_graph_validations(workflow_id,revision_id,graph_digest,target_state_digest,document) VALUES($w,$r,$g,$s,$j)";
            add.Parameters.AddWithValue("$w", graph.WorkflowId.ToString("D")); add.Parameters.AddWithValue("$r", graph.RevisionId);
            add.Parameters.AddWithValue("$g", graph.GraphDigest); add.Parameters.AddWithValue("$s", validation.TargetStateDigest);
            add.Parameters.AddWithValue("$j", JsonSerializer.Serialize(validation, Json)); await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var semantic = RepositoryStateSnapshot.Hash($"marang-effect-graph-validation-v1\n{graph.GraphDigest}\n{JsonSerializer.Serialize(validation, Json)}");
        foreach (var runId in graph.Effects.Select(e => e.Effect.Intent.ExecutionId).Distinct())
            await ProjectGraphWitnessAsync(c, tx, runId, graph.GraphDigest, semantic, cancellationToken).ConfigureAwait(false);
        if (!alreadyRecorded) await ProjectGraphValidationEvidenceAsync(c, tx, graph, validation, cancellationToken).ConfigureAwait(false);
        tx.Commit();
    }

    public async Task RecordPrerequisiteAuthorityAsync(RepositoryEffectGraph graph, Guid dependentEffectId,
        IReadOnlyList<RepositoryEffectAuthorizationEvidence> evidence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph); ArgumentNullException.ThrowIfNull(evidence);
        if (!graph.Verify() || dependentEffectId == Guid.Empty || evidence.Count == 0) throw new InvalidOperationException("Prerequisite authorization request is incomplete.");
        var stored = await GetGraphAsync(graph.WorkflowId, graph.RevisionId, cancellationToken).ConfigureAwait(false);
        if (stored?.GraphDigest != graph.GraphDigest) throw new InvalidOperationException("Prerequisite authorization must bind to a prepared graph.");
        var dependent = graph.Effects.SingleOrDefault(e => e.Effect.Intent.EffectId == dependentEffectId)
            ?? throw new InvalidOperationException("Dependent effect is not selected in the graph.");
        if (dependent.Reuse) throw new InvalidOperationException("Prerequisite write authority is only recorded for a fresh dependent effect.");
        foreach (var item in evidence)
        {
            var selected = RepositoryEffectGraphRuntime.TransitivePrerequisites(graph, dependent)
                .SingleOrDefault(e => e.Effect.Intent.EffectId == item.PrerequisiteEffectId && e.Effect.CausalDigest == item.PrerequisiteCausalDigest)
                ?? throw new InvalidOperationException("Authorization evidence is not bound to an exact transitive prerequisite.");
            var patch = selected.Effect.Intent.Files.SingleOrDefault(f => f.Path == item.TargetPath);
            var expectedIdentity = patch is null ? null : RepositoryStateSnapshot.Hash(
                $"m4.accepted-prerequisite/v1\n{graph.GraphDigest}\n{dependentEffectId:D}\n{selected.Effect.CausalDigest}\n{dependent.Effect.Intent.BaseState.StateDigest}\n{patch.Path}");
            if (item.GraphDigest != graph.GraphDigest || item.DependentEffectId != dependentEffectId || item.PrerequisiteEffectId != selected.Effect.Intent.EffectId ||
                item.IntentDigest != selected.Effect.Intent.IntentDigest || item.WorkflowId != graph.WorkflowId.ToString("D") ||
                item.RevisionId != graph.RevisionId || item.ObservedStateDigest != dependent.Effect.Intent.BaseState.StateDigest ||
                !RepositoryStateSnapshot.IsSha256(item.GraphDigest ?? "") || !RepositoryStateSnapshot.IsSha256(item.PrerequisiteCausalDigest ?? "") ||
                !RepositoryStateSnapshot.IsSha256(item.ObservedStateDigest) || item.RequestIdentity != expectedIdentity ||
                patch is null || item.Status != "Permit")
                throw new InvalidOperationException("Prerequisite authorization evidence does not match active graph identity.");
        }
        await using var c = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var tx = c.BeginTransaction();
        foreach (var item in evidence)
        {
            await using var insert = c.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = "INSERT INTO repository_effect_prerequisite_authority(workflow_id,revision_id,graph_digest,dependent_effect_id,prerequisite_effect_id,observed_state_digest,document) VALUES($w,$r,$g,$d,$p,$s,$j)";
            insert.Parameters.AddWithValue("$w", graph.WorkflowId.ToString("D")); insert.Parameters.AddWithValue("$r", graph.RevisionId);
            insert.Parameters.AddWithValue("$g", graph.GraphDigest); insert.Parameters.AddWithValue("$d", dependentEffectId.ToString("D"));
            insert.Parameters.AddWithValue("$p", item.PrerequisiteEffectId!.Value.ToString("D")); insert.Parameters.AddWithValue("$s", item.ObservedStateDigest!);
            insert.Parameters.AddWithValue("$j", JsonSerializer.Serialize(item, Json)); await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        tx.Commit();
    }

    public async Task<IReadOnlyList<RepositoryEffectAuthorizationEvidence>> ListPrerequisiteAuthorityAsync(Guid workflowId, string revisionId,
        CancellationToken cancellationToken = default)
    {
        await using var c = await database.OpenAsync(cancellationToken).ConfigureAwait(false); await using var command = c.CreateCommand();
        command.CommandText = "SELECT graph_digest,dependent_effect_id,prerequisite_effect_id,observed_state_digest,document FROM repository_effect_prerequisite_authority WHERE workflow_id=$w AND revision_id=$r ORDER BY sequence";
        command.Parameters.AddWithValue("$w", workflowId.ToString("D")); command.Parameters.AddWithValue("$r", revisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<RepositoryEffectAuthorizationEvidence>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var graphDigest = reader.GetString(0); var dependentId = Guid.Parse(reader.GetString(1)); var prerequisiteId = Guid.Parse(reader.GetString(2));
            var stateDigest = reader.GetString(3); var item = JsonSerializer.Deserialize<RepositoryEffectAuthorizationEvidence>(reader.GetString(4), Json)
                ?? throw new InvalidDataException("Stored prerequisite authority evidence is invalid.");
            if (!RepositoryStateSnapshot.IsSha256(graphDigest) || item.GraphDigest != graphDigest || item.DependentEffectId != dependentId || item.PrerequisiteEffectId != prerequisiteId ||
                !RepositoryStateSnapshot.IsSha256(stateDigest) || item.ObservedStateDigest != stateDigest || !RepositoryStateSnapshot.IsSha256(item.PrerequisiteCausalDigest ?? ""))
                throw new InvalidDataException("Stored prerequisite authority binding is invalid.");
            rows.Add(item);
        }
        if (rows.Count != 0)
        {
            var graph = await GetGraphAsync(workflowId, revisionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Prerequisite authority references a missing graph.");
            foreach (var item in rows)
            {
                if (item.GraphDigest != graph.GraphDigest) throw new InvalidDataException("Prerequisite authority references a changed graph.");
                var dependent = graph.Effects.SingleOrDefault(e => e.Effect.Intent.EffectId == item.DependentEffectId)
                    ?? throw new InvalidDataException("Prerequisite authority dependent is not selected in its graph.");
                var selected = RepositoryEffectGraphRuntime.TransitivePrerequisites(graph, dependent)
                    .SingleOrDefault(e => e.Effect.Intent.EffectId == item.PrerequisiteEffectId && e.Effect.CausalDigest == item.PrerequisiteCausalDigest)
                    ?? throw new InvalidDataException("Prerequisite authority dependency is not in its graph.");
                var patch = selected.Effect.Intent.Files.SingleOrDefault(f => f.Path == item.TargetPath);
                var expectedIdentity = patch is null ? null : RepositoryStateSnapshot.Hash(
                    $"m4.accepted-prerequisite/v1\n{graph.GraphDigest}\n{item.DependentEffectId:D}\n{selected.Effect.CausalDigest}\n{dependent.Effect.Intent.BaseState.StateDigest}\n{patch.Path}");
                if (dependent.Reuse || selected.Effect.Intent.IntentDigest != item.IntentDigest || item.WorkflowId != graph.WorkflowId.ToString("D") ||
                    item.RevisionId != graph.RevisionId || item.ObservedStateDigest != dependent.Effect.Intent.BaseState.StateDigest ||
                    item.RequestIdentity != expectedIdentity || patch is null || item.Status != "Permit")
                    throw new InvalidDataException("Prerequisite authority does not match the graph's active physical sequence.");
            }
        }
        return rows;
    }

    public async Task<RepositoryEffectValidation?> GetWorkflowValidationAsync(Guid workflowId, string revisionId, CancellationToken cancellationToken = default)
    {
        await using var c = await database.OpenAsync(cancellationToken).ConfigureAwait(false); await using var command = c.CreateCommand();
        command.CommandText = "SELECT v.graph_digest,v.target_state_digest,v.document,g.graph_digest,g.document FROM repository_effect_graph_validations v JOIN repository_effect_graphs g ON g.workflow_id=v.workflow_id AND g.revision_id=v.revision_id WHERE v.workflow_id=$w AND v.revision_id=$r";
        command.Parameters.AddWithValue("$w", workflowId.ToString("D")); command.Parameters.AddWithValue("$r", revisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var graphDigest = reader.GetString(0); var stateDigest = reader.GetString(1); var json = reader.GetString(2);
        var storedGraphDigest = reader.GetString(3); var graph = JsonSerializer.Deserialize<RepositoryEffectGraph>(reader.GetString(4), Json);
        var validation = JsonSerializer.Deserialize<RepositoryEffectValidation>(json, Json) ?? throw new InvalidDataException("Stored workflow validation is invalid.");
        if (!RepositoryStateSnapshot.IsSha256(graphDigest) || graphDigest != storedGraphDigest || graph?.Verify() != true || graph.GraphDigest != graphDigest ||
            validation.TargetStateDigest != stateDigest || validation.TargetStateDigest != TerminalStateDigest(graph))
            throw new InvalidDataException("Stored workflow validation binding is invalid.");
        return validation;
    }

    private static async Task<RepositoryEffectGraph?> ReadGraphAsync(SqliteConnection c, SqliteTransaction? tx, Guid workflowId, string revisionId, CancellationToken ct)
    {
        await using var command = c.CreateCommand(); command.Transaction = tx; command.CommandText = "SELECT graph_digest,document FROM repository_effect_graphs WHERE workflow_id=$w AND revision_id=$r";
        command.Parameters.AddWithValue("$w", workflowId.ToString("D")); command.Parameters.AddWithValue("$r", revisionId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false); if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var digest = reader.GetString(0); var graph = JsonSerializer.Deserialize<RepositoryEffectGraph>(reader.GetString(1), Json) ?? throw new InvalidDataException("Stored graph JSON is invalid.");
        if (graph.GraphDigest != digest) throw new InvalidDataException("Stored graph digest column does not match its document."); return graph;
    }

    private static string TerminalStateDigest(RepositoryEffectGraph graph)
    {
        var remaining = graph.Effects.Where(e => !e.Reuse).ToList();
        var state = graph.PhysicalBaseState.StateDigest;
        while (remaining.Count != 0)
        {
            var next = remaining.SingleOrDefault(e => e.Effect.Intent.BaseState.StateDigest == state)
                ?? throw new InvalidOperationException("Graph fresh effects do not have one terminal physical state.");
            state = next.Effect.Intent.ExpectedPostState.StateDigest;
            remaining.Remove(next);
        }
        return state;
    }

    private static async Task ProjectGraphWitnessAsync(SqliteConnection c, SqliteTransaction tx, Guid runId, string graphDigest, string semanticDigest, CancellationToken ct)
    {
        await using var exists = c.CreateCommand(); exists.Transaction = tx; exists.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='adaptive_execution_witness'";
        if (await exists.ExecuteScalarAsync(ct).ConfigureAwait(false) is null) return;
        await using var command = c.CreateCommand(); command.Transaction = tx;
        command.CommandText = "INSERT INTO adaptive_execution_witness(run_id,witness_key,digest) VALUES($run,$key,$digest) ON CONFLICT(run_id,witness_key) DO UPDATE SET digest=excluded.digest WHERE digest<>excluded.digest";
        command.Parameters.AddWithValue("$run", runId.ToString("D")); command.Parameters.AddWithValue("$key", "repository-effect-graph-validation:" + graphDigest);
        command.Parameters.AddWithValue("$digest", semanticDigest); await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task ProjectGraphValidationEvidenceAsync(SqliteConnection c, SqliteTransaction tx, RepositoryEffectGraph graph, RepositoryEffectValidation validation, CancellationToken ct)
    {
        await using var exists = c.CreateCommand(); exists.Transaction = tx; exists.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='adaptive_evidence'";
        if (await exists.ExecuteScalarAsync(ct).ConfigureAwait(false) is null) return;
        var document = JsonSerializer.Serialize(new
        {
            graphDigest = graph.GraphDigest,
            revisionId = graph.RevisionId,
            targetStateDigest = validation.TargetStateDigest,
            passed = validation.Passed,
            commandIdentity = validation.CommandIdentity,
            outputDigest = validation.OutputDigest,
            evidence = validation.Evidence
        }, Json);
        await using var command = c.CreateCommand(); command.Transaction = tx;
        command.CommandText = "INSERT INTO adaptive_evidence(workflow_id,kind,document) SELECT $workflow,'EffectGraphValidation',$document WHERE NOT EXISTS (SELECT 1 FROM adaptive_evidence WHERE workflow_id=$workflow AND kind='EffectGraphValidation' AND document=$document)";
        command.Parameters.AddWithValue("$workflow", graph.WorkflowId.ToString("D")); command.Parameters.AddWithValue("$document", document);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
