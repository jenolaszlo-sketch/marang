using Penghou.Hufu;

namespace Marang.Hufu;

/// <summary>Closed result categories for one exact adaptive-revision proposal.</summary>
public enum AdaptiveRevisionAuthorityOutcome
{
    Automatic,
    ApprovalRequired,
    Denied
}

/// <summary>
/// Trusted host binding for a proposal. The host must derive these facts from
/// the immutable proposal and its authenticated workflow state. In particular,
/// <see cref="ProposalDigest"/> must cover the workflow, parent revision,
/// parent-state digest, and all proposed changes.
/// </summary>
public sealed record AdaptiveRevisionAuthorityRequest(
    string WorkflowId,
    string ParentRevisionId,
    string ParentStateDigest,
    string ProposalDigest,
    AuthenticatedAuthorityContext AuthorityContext,
    string Capability,
    string WorkspaceId,
    string RelativePath,
    bool RequiresApproval,
    AdaptiveRevisionApprovalReceipt? Approval = null);

/// <summary>
/// Opaque reference to durable host approval evidence. Its identifier is not
/// itself proof of approval; the configured trusted verifier must resolve it.
/// </summary>
public sealed record AdaptiveRevisionApprovalReceipt(string EvidenceId);

/// <summary>Approval facts returned by the trusted host verifier.</summary>
public sealed record AdaptiveRevisionApprovalEvidence(
    string EvidenceId,
    string TenantId,
    string ActorId,
    string WorkflowId,
    string ParentRevisionId,
    string ParentStateDigest,
    string ProposalDigest,
    DateTimeOffset ApprovedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Resolves durable approval evidence and authenticates its actor and exact
/// proposal binding. Implementations must not trust identities in the request
/// or receipt without consulting host-owned approval state.
/// </summary>
public interface IAdaptiveRevisionApprovalVerifier
{
    ValueTask<AdaptiveRevisionApprovalEvidence?> ResolveAsync(
        AdaptiveRevisionAuthorityRequest request,
        AdaptiveRevisionApprovalReceipt receipt,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of a fresh current-authority check for one proposal.</summary>
public sealed record AdaptiveRevisionAuthorityDecision(
    AdaptiveRevisionAuthorityOutcome Outcome,
    string ProposalDigest,
    string WorkflowId,
    string ParentRevisionId,
    string ReasonCode,
    string? HufuRequestIdentity = null,
    AuthorityStatus? HufuStatus = null,
    AuthorityDecision? HufuDecision = null,
    AdaptiveRevisionApprovalEvidence? ApprovalEvidence = null);

/// <summary>
/// Re-evaluates current Hufu authority for the exact proposal and validates
/// approval evidence through a trusted host service. Call this both when
/// creating a proposal and immediately before committing it. This adapter is
/// a preflight decision; it does not make runtime persistence or resource
/// mutation atomic with authority changes.
/// </summary>
public sealed class AdaptiveRevisionAuthority
{
    private readonly IAuthorityRequestAuthorizer _authority;
    private readonly IAdaptiveRevisionApprovalVerifier? _approvals;
    private readonly TimeProvider _clock;

    public AdaptiveRevisionAuthority(
        IAuthorityRequestAuthorizer authority,
        IAdaptiveRevisionApprovalVerifier? approvals = null,
        TimeProvider? clock = null)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _approvals = approvals;
        _clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<AdaptiveRevisionAuthorityDecision> EvaluateAsync(
        AdaptiveRevisionAuthorityRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Valid(request))
            return Denied(request, "adaptive-revision.binding-invalid");

        AuthorityAction action;
        try
        {
            action = MapCapability(request.Capability);
        }
        catch (ArgumentException)
        {
            return Denied(request, "adaptive-revision.capability-unsupported");
        }

        var hufuRequest = new AuthorityRequest(
            request.AuthorityContext,
            action,
            request.WorkspaceId,
            request.RelativePath,
            request.ProposalDigest);

        AuthorityRequestAuthorization authorization;
        try
        {
            authorization = await _authority.AuthorizeAsync(hufuRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Denied(request, "adaptive-revision.hufu-unavailable", hufuRequest.RequestIdentity);
        }

        if (authorization is null || authorization.Request != hufuRequest)
            return Denied(request, "adaptive-revision.hufu-response-mismatch", hufuRequest.RequestIdentity,
                authorization?.Status, authorization?.Decision);

        if (authorization.Status != AuthorityStatus.Permit || !authorization.IsAuthorized)
        {
            var reason = authorization.Status switch
            {
                AuthorityStatus.Deny => "adaptive-revision.hufu-denied",
                AuthorityStatus.Unavailable => "adaptive-revision.hufu-unavailable",
                _ => "adaptive-revision.hufu-evidence-invalid"
            };
            return Denied(request, reason, hufuRequest.RequestIdentity,
                authorization.Status, authorization.Decision);
        }

        if (request.Approval is null)
        {
            return request.RequiresApproval
                ? new(AdaptiveRevisionAuthorityOutcome.ApprovalRequired,
                    request.ProposalDigest, request.WorkflowId, request.ParentRevisionId,
                    "adaptive-revision.approval-required", hufuRequest.RequestIdentity,
                    authorization.Status, authorization.Decision)
                : new(AdaptiveRevisionAuthorityOutcome.Automatic,
                    request.ProposalDigest, request.WorkflowId, request.ParentRevisionId,
                    "adaptive-revision.authorized", hufuRequest.RequestIdentity,
                    authorization.Status, authorization.Decision);
        }

        if (_approvals is null)
            return Denied(request, "adaptive-revision.approval-verifier-unavailable",
                hufuRequest.RequestIdentity, authorization.Status, authorization.Decision);

        AdaptiveRevisionApprovalEvidence? evidence;
        try
        {
            evidence = await _approvals.ResolveAsync(request, request.Approval, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Denied(request, "adaptive-revision.approval-unavailable",
                hufuRequest.RequestIdentity, authorization.Status, authorization.Decision);
        }

        var now = _clock.GetUtcNow();
        if (!MatchesApproval(request, evidence, now) || (request.RequiresApproval && evidence is null))
            return Denied(request, "adaptive-revision.approval-invalid",
                hufuRequest.RequestIdentity, authorization.Status, authorization.Decision, evidence);

        return new(
            AdaptiveRevisionAuthorityOutcome.Automatic,
            request.ProposalDigest,
            request.WorkflowId,
            request.ParentRevisionId,
            "adaptive-revision.authorized-with-approval",
            hufuRequest.RequestIdentity,
            authorization.Status,
            authorization.Decision,
            evidence);
    }

    private static bool Valid(AdaptiveRevisionAuthorityRequest? request)
    {
        if (request is null ||
            !AuthorityValidation.ValidToken(request.WorkflowId) ||
            !AuthorityValidation.ValidToken(request.ParentRevisionId) ||
            !IsSha256(request.ParentStateDigest) ||
            !IsSha256(request.ProposalDigest) ||
            !AuthorityValidation.ValidContext(request.AuthorityContext) ||
            request.AuthorityContext.RunId != request.WorkflowId ||
            request.AuthorityContext.RevisionId != request.ParentRevisionId ||
            !AuthorityValidation.ValidToken(request.WorkspaceId) ||
            !AuthorityValidation.ValidToken(request.Capability) ||
            request.Approval is { } receipt && !AuthorityValidation.ValidToken(receipt.EvidenceId))
            return false;

        try
        {
            return request.RelativePath == AuthorityValidation.NormalizePath(request.RelativePath);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static AuthorityAction MapCapability(string capability) => capability switch
    {
        "read-file" => AuthorityAction.ReadFile,
        "list-directory" => AuthorityAction.ListDirectory,
        "read-metadata" => AuthorityAction.ReadMetadata,
        "patch-file" => AuthorityAction.PatchFile,
        "release" => AuthorityAction.Release,
        "write-file" => AuthorityAction.WriteFile,
        "process.execute" => AuthorityAction.ExecuteProcess,
        _ => throw new ArgumentException("Unknown Hufu resource capability.", nameof(capability))
    };

    private static bool MatchesApproval(
        AdaptiveRevisionAuthorityRequest request,
        AdaptiveRevisionApprovalEvidence? evidence,
        DateTimeOffset now) =>
        evidence is not null &&
        AuthorityValidation.ValidToken(evidence.EvidenceId) &&
        evidence.EvidenceId == request.Approval?.EvidenceId &&
        AuthorityValidation.ValidToken(evidence.TenantId) &&
        AuthorityValidation.ValidToken(evidence.ActorId) &&
        AuthorityValidation.ValidToken(evidence.WorkflowId) &&
        AuthorityValidation.ValidToken(evidence.ParentRevisionId) &&
        IsSha256(evidence.ParentStateDigest) &&
        IsSha256(evidence.ProposalDigest) &&
        evidence.TenantId == request.AuthorityContext.TenantId &&
        evidence.WorkflowId == request.WorkflowId &&
        evidence.ParentRevisionId == request.ParentRevisionId &&
        evidence.ParentStateDigest == request.ParentStateDigest &&
        evidence.ProposalDigest == request.ProposalDigest &&
        evidence.ApprovedAt <= now &&
        evidence.ExpiresAt > now &&
        evidence.ApprovedAt < evidence.ExpiresAt;

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static AdaptiveRevisionAuthorityDecision Denied(
        AdaptiveRevisionAuthorityRequest? request,
        string reason,
        string? hufuRequestIdentity = null,
        AuthorityStatus? status = null,
        AuthorityDecision? decision = null,
        AdaptiveRevisionApprovalEvidence? approval = null) =>
        new(AdaptiveRevisionAuthorityOutcome.Denied,
            request?.ProposalDigest ?? string.Empty,
            request?.WorkflowId ?? string.Empty,
            request?.ParentRevisionId ?? string.Empty,
            reason,
            hufuRequestIdentity,
            status,
            decision,
            approval);
}
