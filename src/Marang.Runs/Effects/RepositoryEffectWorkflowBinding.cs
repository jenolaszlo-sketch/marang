using System.Text.Json;
using Marang.Runs.Adaptive;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Runs.Effects;

/// <summary>Proves the exact effect's input was selected by an authenticated, approved immutable workflow revision.</summary>
public sealed class RepositoryEffectWorkflowBinding(IZhinuSqliteDatabase database, SqliteAdaptiveRevisionRepository revisions)
    : IRepositoryEffectManifestVerifier
{
    public async ValueTask<bool> IsAdmittedAsync(RepositoryEffectIntent intent, CancellationToken cancellationToken = default)
    {
        if (!intent.HasValidDigest()) return false;
        var history = await revisions.GetHistoryAsync(intent.WorkflowId, cancellationToken);
        var revision = history.SingleOrDefault(r => r.RevisionId == intent.RevisionId);
        if (revision is null || !Binds(revision, intent)) return false;
        await using var c = await database.OpenAsync(cancellationToken);
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT p.document,p.digest,p.approval_actor,p.approval_evidence,p.approval_expires
            FROM adaptive_revisions r JOIN adaptive_proposals p ON r.proposal_id=p.id AND r.workflow_id=p.workflow_id
            WHERE r.workflow_id=$w AND r.revision_id=$r AND p.status='Committed'
            """;
        q.Parameters.AddWithValue("$w", intent.WorkflowId.ToString("D")); q.Parameters.AddWithValue("$r", intent.RevisionId);
        await using var reader = await q.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4)) return false;
        var proposal = JsonSerializer.Deserialize<AdaptiveProposal>(reader.GetString(0))!;
        return AdaptiveRevisionContracts.Digest(proposal) == reader.GetString(1) &&
            AdaptiveRevisionContracts.Digest(proposal.Candidate) == AdaptiveRevisionContracts.Digest(revision) &&
            proposal.Authority == AdaptiveAuthorityDecision.ApprovalRequired &&
            !string.IsNullOrWhiteSpace(reader.GetString(2)) && !string.IsNullOrWhiteSpace(reader.GetString(3)) &&
            DateTimeOffset.Parse(reader.GetString(4)) > database.TimeProvider.GetUtcNow();
    }

    public async ValueTask<bool> IsCurrentAsync(RepositoryEffectIntent intent, CancellationToken cancellationToken = default)
    {
        try { await RequireActiveAsync(revisions, intent, cancellationToken); return true; }
        catch (WorkflowStateException) { return false; }
    }

    public static async Task RequireActiveAsync(SqliteAdaptiveRevisionRepository revisions, RepositoryEffectIntent intent, CancellationToken cancellationToken = default)
    {
        var revision = await revisions.GetActiveAsync(intent.WorkflowId, cancellationToken);
        if (revision.RevisionId != intent.RevisionId || !Binds(revision, intent))
            throw new WorkflowStateException("Effect does not bind the active admitted revision and execution.");
    }

    public static bool Binds(AdaptiveRevision revision, RepositoryEffectIntent intent)
    {
        if (!intent.HasValidDigest()) return false;
        AdaptivePlanAuthoring.VerifyArchive(revision);
        var branch = revision.Branches.SingleOrDefault(b => b.LogicalId == intent.LogicalStepId && b.Execution.Id == intent.ExecutionId);
        if (revision.WorkflowId != intent.WorkflowId || revision.RevisionId != intent.RevisionId || branch is null ||
            branch.AuthorityDigest != AdaptivePlanAuthoring.RepositoryEffectAuthorityProfile || !AdaptivePlanAuthoring.IsRepositoryEffectBranch(revision, branch)) return false;
        using var input = JsonDocument.Parse(JsonSerializer.Deserialize<string>(branch.Execution.InputJson!)!);
        return input.RootElement.GetProperty("Input").GetString() == intent.IntentDigest;
    }
}
