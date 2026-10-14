using System.Text.Json;
using Marang.Hufu;
using Microsoft.Data.Sqlite;
using Penghou.Hufu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Runs.Adaptive;

public sealed record AdaptiveAuthorityProfile(string TenantId, string SupervisorSubjectId,
    string WorkspaceId, string RelativePath, string Capability, string BindingFence)
{
    public bool RequiresApproval { get; init; } = true;
    public string AllowedBranchAuthorityDigest { get; init; } = "m1/bounded-repository-read-report";
}

/// <summary>
/// Durable host approval evidence plus fresh Hufu preflight. Operator identities come from the authenticated
/// host, never from a model/tool payload. The host must provide an approver/cancellation policy for this workspace.
/// Resource-side effects still require their own final Hufu mutation checks.
/// </summary>
public sealed class SqliteAdaptiveSupervisorAuthority : IAdaptiveSupervisorAuthority, IAdaptiveRevisionApprovalVerifier
{
    private readonly IZhinuSqliteDatabase database;
    private readonly AdaptiveAuthorityProfile profile;
    private readonly Func<string, bool> isAuthorizedOperator;
    private readonly AdaptiveRevisionAuthority evaluator;
    private readonly SqliteAdaptiveRevisionRepository revisions;

    public SqliteAdaptiveSupervisorAuthority(IZhinuSqliteDatabase database, SqliteAdaptiveRevisionRepository revisions,
        IAuthorityRequestAuthorizer hufu, AdaptiveAuthorityProfile profile, Func<string, bool> isAuthorizedOperator)
    {
        this.database = database;
        this.revisions = revisions;
        this.profile = profile;
        this.isAuthorizedOperator = isAuthorizedOperator;
        evaluator = new AdaptiveRevisionAuthority(hufu, this, database.TimeProvider);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await database.EnsureInitializedAsync(cancellationToken);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS adaptive_operator_approvals (
              evidence_id TEXT PRIMARY KEY, proposal_id TEXT NOT NULL, digest TEXT NOT NULL, document TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS adaptive_authority_decisions (
              sequence INTEGER PRIMARY KEY AUTOINCREMENT, proposal_id TEXT NOT NULL, digest TEXT NOT NULL,
              outcome TEXT NOT NULL, reason TEXT NOT NULL, recorded_at TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public AdaptiveSupervisorPolicy PolicyFor(AdaptiveRevision candidate)
    {
        AdaptivePlanAuthoring.VerifyArchive(candidate);
        if (candidate.Branches.Any(b => b.AuthorityDigest != profile.AllowedBranchAuthorityDigest))
            throw new WorkflowAuthorizationException("Candidate exceeds the host's admitted branch authority profile.");
        return new AdaptiveSupervisorPolicy(profile.RequiresApproval, "host-profile:" + AdaptiveRevisionContracts.Digest(profile));
    }
    public bool IsAuthorizedOperator(string authenticatedActor) => isAuthorizedOperator(authenticatedActor);

    public async Task<AdaptiveRevisionAuthorityDecision> EvaluateAsync(AdaptiveProposal proposal, string digest,
        AdaptiveRevisionApprovalReceipt? receipt, CancellationToken cancellationToken)
    {
        if (AdaptiveRevisionContracts.Digest(proposal) != digest) throw new WorkflowStateException("Authority request digest mismatch.");
        var policy = PolicyFor(proposal.Candidate);
        if (proposal.AuthorityEvidence != policy.ProfileEvidence ||
            (proposal.Authority == AdaptiveAuthorityDecision.ApprovalRequired) != policy.RequiresApproval)
            throw new WorkflowAuthorizationException("Proposal's review policy differs from current host policy.");
        var decision = await evaluator.EvaluateAsync(Request(proposal, digest, receipt), cancellationToken);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO adaptive_authority_decisions(proposal_id,digest,outcome,reason,recorded_at) VALUES($p,$d,$o,$r,$at)";
        command.Parameters.AddWithValue("$p", proposal.ProposalId.ToString("D"));
        command.Parameters.AddWithValue("$d", digest);
        command.Parameters.AddWithValue("$o", decision.Outcome.ToString());
        command.Parameters.AddWithValue("$r", decision.ReasonCode);
        command.Parameters.AddWithValue("$at", database.TimeProvider.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return decision;
    }

    public async Task<AdaptiveRevisionApprovalReceipt> RecordApprovalAsync(AdaptiveProposal proposal, string digest,
        string authenticatedActor, CancellationToken cancellationToken)
    {
        if (!isAuthorizedOperator(authenticatedActor) || AdaptiveRevisionContracts.Digest(proposal) != digest)
            throw new WorkflowAuthorizationException("Operator is not authorized to approve this exact proposal.");
        var pending = await revisions.GetPendingAsync(proposal.Candidate.WorkflowId, cancellationToken);
        if (pending?.ProposalId != proposal.ProposalId || AdaptiveRevisionContracts.Digest(pending) != digest)
            throw new WorkflowStateException("Proposal is no longer awaiting this decision.");
        var now = database.TimeProvider.GetUtcNow();
        var evidence = new AdaptiveRevisionApprovalEvidence("approval-" + Guid.NewGuid().ToString("N"), profile.TenantId,
            authenticatedActor, proposal.Candidate.WorkflowId.ToString("D"), proposal.Candidate.ParentRevisionId!,
            proposal.ParentStateDigest, digest, now, now.AddHours(1));
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO adaptive_operator_approvals(evidence_id,proposal_id,digest,document) VALUES($e,$p,$d,$doc)";
        command.Parameters.AddWithValue("$e", evidence.EvidenceId);
        command.Parameters.AddWithValue("$p", proposal.ProposalId.ToString("D"));
        command.Parameters.AddWithValue("$d", digest);
        command.Parameters.AddWithValue("$doc", JsonSerializer.Serialize(evidence));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new AdaptiveRevisionApprovalReceipt(evidence.EvidenceId);
    }

    public async ValueTask<AdaptiveRevisionApprovalEvidence?> ResolveAsync(AdaptiveRevisionAuthorityRequest request,
        AdaptiveRevisionApprovalReceipt receipt, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM adaptive_operator_approvals WHERE evidence_id=$e AND digest=$d";
        command.Parameters.AddWithValue("$e", receipt.EvidenceId);
        command.Parameters.AddWithValue("$d", request.ProposalDigest);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        var evidence = json is null ? null : JsonSerializer.Deserialize<AdaptiveRevisionApprovalEvidence>(json);
        return evidence is not null && isAuthorizedOperator(evidence.ActorId) ? evidence : null;
    }

    public async Task<AdaptiveRevisionApprovalReceipt?> FindApprovalAsync(Guid proposalId, string digest,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT evidence_id FROM adaptive_operator_approvals WHERE proposal_id=$p AND digest=$d ORDER BY rowid DESC LIMIT 1";
        command.Parameters.AddWithValue("$p", proposalId.ToString("D"));
        command.Parameters.AddWithValue("$d", digest);
        var id = await command.ExecuteScalarAsync(cancellationToken) as string;
        return id is null ? null : new AdaptiveRevisionApprovalReceipt(id);
    }

    public async Task<bool> CanCancelAsync(Guid workflowId, string authenticatedActor, CancellationToken cancellationToken)
    {
        if (!isAuthorizedOperator(authenticatedActor)) return false;
        var active = await revisions.GetActiveAsync(workflowId, cancellationToken);
        var state = await revisions.GetStateDigestAsync(workflowId, cancellationToken);
        var digest = AdaptiveRevisionContracts.Digest(new { workflowId, active.RevisionId, state, Action = "Cancel" });
        var request = new AdaptiveRevisionAuthorityRequest(workflowId.ToString("D"), active.RevisionId, state, digest,
            Context(workflowId, active.RevisionId), profile.Capability, profile.WorkspaceId, profile.RelativePath, false);
        return (await evaluator.EvaluateAsync(request, cancellationToken)).Outcome == AdaptiveRevisionAuthorityOutcome.Automatic;
    }

    private AuthenticatedAuthorityContext Context(Guid workflowId, string parent) =>
        new(profile.TenantId, profile.SupervisorSubjectId, workflowId.ToString("D"), parent, profile.BindingFence);
    private AdaptiveRevisionAuthorityRequest Request(AdaptiveProposal p, string digest, AdaptiveRevisionApprovalReceipt? receipt) =>
        new(p.Candidate.WorkflowId.ToString("D"), p.Candidate.ParentRevisionId!, p.ParentStateDigest, digest,
            Context(p.Candidate.WorkflowId, p.Candidate.ParentRevisionId!), profile.Capability, profile.WorkspaceId,
            profile.RelativePath, profile.RequiresApproval, receipt);
}
