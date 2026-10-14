using System.Text.Json;
using Marang.Hufu;
using Marang.Runs.Effects;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Runs.Adaptive;

public sealed record AdaptiveOperatorView(string Explanation, string[] Preserved, string[] Affected,
    string[] Introduced, string[] Actions, string? Discussion, string Authority);

/// <summary>Host-owned authority binding; callers supply neither identity nor resource scope through UI actions.</summary>
public interface IAdaptiveSupervisorAuthority
{
    AdaptiveSupervisorPolicy PolicyFor(AdaptiveRevision candidate);
    bool IsAuthorizedOperator(string authenticatedActor);
    Task<AdaptiveRevisionAuthorityDecision> EvaluateAsync(AdaptiveProposal proposal, string digest,
        AdaptiveRevisionApprovalReceipt? receipt, CancellationToken cancellationToken);
    Task<AdaptiveRevisionApprovalReceipt> RecordApprovalAsync(AdaptiveProposal proposal, string digest,
        string authenticatedActor, CancellationToken cancellationToken);
    Task<bool> CanCancelAsync(Guid workflowId, string authenticatedActor, CancellationToken cancellationToken);
}
public sealed record AdaptiveSupervisorPolicy(bool RequiresApproval, string ProfileEvidence);

/// <summary>
/// M1 operator/control-loop coordinator. Proposals come from the deterministic Fuwen authoring boundary.
/// The runtime repository owns durable execution state, comparison, stale-state checks and cutover.
/// Host composition authenticates the operator and resolves Hufu authority on each mutation.
/// </summary>
public sealed class AdaptiveSupervisor(SqliteAdaptiveRevisionRepository revisions, IAdaptiveSupervisorAuthority authority,
    IRepositoryEffectRevisionGuard? repositoryEffects = null)
{
    public async Task<(AdaptiveProposal Proposal, string Digest, AdaptiveOperatorView View)> ProposeAsync(
        AdaptiveRevision candidate, string impact,
        CancellationToken cancellationToken = default)
    {
        AdaptivePlanAuthoring.VerifyArchive(candidate);
        var parent = await revisions.GetActiveAsync(candidate.WorkflowId, cancellationToken);
        await using var effectScope = await EnterEffectGuard(parent, candidate, false, cancellationToken);
        var state = await revisions.GetStateDigestAsync(candidate.WorkflowId, cancellationToken);
        var policy = authority.PolicyFor(candidate);
        if (AdaptivePlanAuthoring.HasRepositoryEffects(candidate) && !policy.RequiresApproval)
            throw new WorkflowStateException("The M3 repository profile requires explicit approval of every exact effect revision.");
        var proposal = new AdaptiveProposal(Guid.NewGuid(), candidate, state,
            AdaptiveRevisionContracts.Compare(parent, candidate),
            policy.RequiresApproval ? AdaptiveAuthorityDecision.ApprovalRequired : AdaptiveAuthorityDecision.Automatic,
            policy.ProfileEvidence, impact);
        var digest = AdaptiveRevisionContracts.Digest(proposal);
        var decision = await authority.EvaluateAsync(proposal, digest, null, cancellationToken);
        await revisions.ProposeAsync(proposal, cancellationToken);
        if (decision.Outcome == AdaptiveRevisionAuthorityOutcome.Automatic)
        {
            await revisions.ApproveAsync(proposal.ProposalId, digest, "policy", decision.ReasonCode,
                DateTimeOffset.UtcNow.AddMinutes(5), cancellationToken);
        }
        return (proposal, digest, View(parent, proposal, decision.Outcome.ToString(), null));
    }

    public async Task<AdaptiveRevisionApprovalReceipt> ApproveAsync(AdaptiveProposal proposal, string digest,
        string authenticatedActor, CancellationToken cancellationToken = default)
    {
        if (AdaptiveRevisionContracts.Digest(proposal) != digest) throw new WorkflowStateException("Proposal digest mismatch.");
        var receipt = await authority.RecordApprovalAsync(proposal, digest, authenticatedActor, cancellationToken);
        var decision = await authority.EvaluateAsync(proposal, digest, receipt, cancellationToken);
        if (decision.Outcome != AdaptiveRevisionAuthorityOutcome.Automatic || decision.ApprovalEvidence is null)
            throw new WorkflowStateException("Current authority does not permit this exact approved proposal.");
        await revisions.ApproveAsync(proposal.ProposalId, digest, decision.ApprovalEvidence.ActorId,
            decision.ApprovalEvidence.EvidenceId, decision.ApprovalEvidence.ExpiresAt, cancellationToken);
        return receipt;
    }

    public async Task CommitAsync(AdaptiveProposal proposal, string digest, AdaptiveRevisionApprovalReceipt? receipt,
        CancellationToken cancellationToken = default)
    {
        AdaptivePlanAuthoring.VerifyArchive(proposal.Candidate);
        if (AdaptiveRevisionContracts.Digest(proposal) != digest) throw new WorkflowStateException("Proposal digest mismatch.");
        await using var effectScope = await EnterEffectGuard(await revisions.GetActiveAsync(proposal.Candidate.WorkflowId, cancellationToken),
            proposal.Candidate, true, cancellationToken);
        // A fresh Hufu check is mandatory even after durable user approval. Revocation cannot become permission.
        var decision = await authority.EvaluateAsync(proposal, digest, receipt, cancellationToken);
        if (decision.Outcome != AdaptiveRevisionAuthorityOutcome.Automatic)
            throw new WorkflowStateException("Revision authority was denied or still requires approval.");
        await revisions.CommitAsync(proposal.ProposalId, digest, cancellationToken);
    }

    public Task RejectAsync(AdaptiveProposal proposal, string digest, string authenticatedActor,
        CancellationToken cancellationToken = default)
    {
        if (!authority.IsAuthorizedOperator(authenticatedActor)) throw new WorkflowAuthorizationException("Operator is not authorized.");
        return revisions.RejectAsync(proposal.ProposalId, digest, authenticatedActor, cancellationToken);
    }

    public async Task CancelAsync(Guid workflowId, string authenticatedActor, CancellationToken cancellationToken = default)
    {
        var active = await revisions.GetActiveAsync(workflowId, cancellationToken);
        if (AdaptivePlanAuthoring.HasRepositoryEffects(active) && repositoryEffects is null)
            throw new WorkflowStateException("Effectful workflow cancellation requires its physical checkout guard.");
        await using var effectScope = AdaptivePlanAuthoring.HasRepositoryEffects(active)
            ? await repositoryEffects!.EnterCancellationAsync(active, cancellationToken) : null;
        if (!await authority.CanCancelAsync(workflowId, authenticatedActor, cancellationToken))
            throw new WorkflowStateException("Current authority does not permit cancellation.");
        await revisions.CancelAsync(workflowId, authenticatedActor, cancellationToken);
    }

    public Task DiscussAsync(Guid workflowId, string authenticatedActor, string question, CancellationToken cancellationToken = default)
    {
        if (!authority.IsAuthorizedOperator(authenticatedActor)) throw new WorkflowAuthorizationException("Operator is not authorized.");
        return revisions.DiscussAsync(workflowId, authenticatedActor, question, cancellationToken);
    }

    public static AdaptiveOperatorView View(AdaptiveRevision parent, AdaptiveProposal proposal, string authority,
        string? question)
    {
        string Label(string id) => proposal.Candidate.Branches.Concat(parent.Branches).First(b => b.LogicalId == id).Title;
        var preserved = proposal.Changes.Where(c => c.Kind == AdaptiveChangeKind.Preserved).Select(c => Label(c.LogicalId) + ": preserve existing work").ToArray();
        var affected = proposal.Changes.Where(c => c.Kind is not (AdaptiveChangeKind.Preserved or AdaptiveChangeKind.Added))
            .Select(c => Label(c.LogicalId) + ": keep earlier results in history; prevent them from contributing to the revised outcome").ToArray();
        var introduced = proposal.Changes.Where(c => c.Kind != AdaptiveChangeKind.Preserved && c.Kind != AdaptiveChangeKind.Removed)
            .Select(c => Label(c.LogicalId) + ": perform revised work").ToArray();
        return new AdaptiveOperatorView(proposal.Candidate.Reason + " " + proposal.ImpactSummary,
            preserved, affected, introduced, ["Approve", "Reject", "Cancel workflow", "Ask / discuss further"], question, authority);
    }

    private async ValueTask<IAsyncDisposable?> EnterEffectGuard(AdaptiveRevision parent, AdaptiveRevision candidate, bool activating, CancellationToken ct)
    {
        if (!AdaptivePlanAuthoring.HasRepositoryEffects(parent) && !AdaptivePlanAuthoring.HasRepositoryEffects(candidate)) return null;
        if (repositoryEffects is null) throw new WorkflowStateException("Repository effects require a reconciliation guard at revision proposal and activation.");
        return await repositoryEffects.EnterAsync(parent, candidate, activating, ct);
    }
}
