using System.Text.Json;
using Marang.Runs.Effects;
using Microsoft.Data.Sqlite;
using Penghou.Zhinu;

namespace Marang.Runs.Integrity;

internal static class RepositoryRecoveryExecution
{
    // Validate persisted execution data used by catch-up before any owner may
    // initialize missing tables or interpret damaged rows as reusable work.
    internal static async Task ValidateAsync(SqliteConnection c, SqliteTransaction t, CancellationToken ct)
    {
        await using (var q = c.CreateCommand())
        {
            q.Transaction = t;
            q.CommandText = "SELECT id,status,lease_generation,input_json,output_json,error_json,metadata_json FROM workflow_runs";
            await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!Guid.TryParse(r.GetString(0), out var id) || id == Guid.Empty ||
                    !Enum.IsDefined((WorkflowStatus)r.GetInt32(1)) || r.GetInt64(2) < 1)
                    throw new InvalidDataException("Durable run identity or lifecycle fields are invalid.");
                for (var i = 3; i < r.FieldCount; i++) ValidateJson(r, i);
            }
        }
        await using (var q = c.CreateCommand())
        {
            q.Transaction = t;
            q.CommandText = "SELECT run_id,witness_key,digest FROM adaptive_execution_witness";
            await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                if (!Guid.TryParse(r.GetString(0), out var run) || run == Guid.Empty ||
                    string.IsNullOrWhiteSpace(r.GetString(1)) || !RepositoryStateSnapshot.IsSha256(r.GetString(2)))
                    throw new InvalidDataException("Durable execution witness identity or digest is invalid.");
        }
        await using (var q = c.CreateCommand())
        {
            q.Transaction = t;
            q.CommandText = "SELECT workflow_id,kind,document FROM adaptive_evidence";
            await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!Guid.TryParse(r.GetString(0), out var workflow) || workflow == Guid.Empty || string.IsNullOrWhiteSpace(r.GetString(1)))
                    throw new InvalidDataException("Durable workflow evidence identity is invalid.");
                ValidateJson(r, 2);
            }
        }
        await using (var q = c.CreateCommand())
        {
            q.Transaction = t;
            q.CommandText = "SELECT id,workflow_run_id,step_key,status,attempt,revision,lease_generation,input_json,output_json,error_json FROM workflow_steps";
            await using var r = await q.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!Guid.TryParse(r.GetString(0), out var id) || id == Guid.Empty ||
                    !Guid.TryParse(r.GetString(1), out var run) || run == Guid.Empty || string.IsNullOrWhiteSpace(r.GetString(2)) ||
                    !Enum.IsDefined((StepStatus)r.GetInt32(3)) || r.GetInt64(4) < 0 || r.GetInt64(5) < 1 || r.GetInt64(6) < 1)
                    throw new InvalidDataException("Durable step identity or lifecycle fields are invalid.");
                for (var i = 7; i < r.FieldCount; i++) ValidateJson(r, i);
            }
        }
    }

    private static void ValidateJson(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return;
        using var document = JsonDocument.Parse(reader.GetString(ordinal));
    }
}
