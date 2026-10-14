using Penghou.Hufu;

namespace Marang.Hufu;

/// <summary>One bounded acceptance attempt at the Marang boundary.</summary>
public sealed record MarangDelegationAcceptanceInput(
    string Caller,
    string AcceptanceIdentity,
    string WorkspaceId,
    string Provider,
    string Generation,
    string BoundedRequestDigest,
    MarangDelegationCredential? Credential);

/// <summary>Typed acceptance outcome. Only <see cref="Accepted"/> may create durable Marang state.</summary>
public enum MarangDelegationAcceptanceStatus { Accepted, Denied, Unavailable }

/// <summary>The result of the acceptance gate, with its typed reason and evidence reference.</summary>
public sealed record MarangDelegationAcceptanceOutcome(MarangDelegationAcceptanceStatus Status, string ReasonCode, string? EvidenceId = null);

/// <summary>
/// The qualified acceptance gate: credential verification first, then the Hufu
/// <c>DelegationAcceptanceOperation.Accept</c> current-authority decision. It
/// never authorizes on the credential alone and never infers acceptance from
/// resource authority.
/// </summary>
public interface IMarangDelegationAcceptanceGate
{
    ValueTask<MarangDelegationAcceptanceOutcome> AuthorizeAsync(
        MarangDelegationAcceptanceInput input, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fail-closed default gate for hosts that have not composed a Hufu
/// current-authority source. It never accepts; it is not a bypass and produces
/// no durable acceptance state.
/// </summary>
public sealed class FailClosedDelegationAcceptanceGate : IMarangDelegationAcceptanceGate
{
    public ValueTask<MarangDelegationAcceptanceOutcome> AuthorizeAsync(
        MarangDelegationAcceptanceInput input, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new MarangDelegationAcceptanceOutcome(
            MarangDelegationAcceptanceStatus.Unavailable, "marang.acceptance-authority-not-configured"));
}

/// <summary>
/// Default gate composition. The caller is projected to an authenticated Hufu
/// context with a fixed, testable shape: tenant from host configuration, subject
/// from the authenticated caller, run from the acceptance identity, revision from
/// the generation, and a constant acceptance fence. The credential is verified
/// before any Hufu evaluation; tampered/missing credentials fail closed first.
/// </summary>
public sealed class MarangDelegationAcceptanceGate : IMarangDelegationAcceptanceGate
{
    private readonly IMarangDelegationCredentialVerifier _verifier;
    private readonly IDelegationAcceptanceAuthorizer _authorizer;
    private readonly string _tenantId;

    public MarangDelegationAcceptanceGate(IMarangDelegationCredentialVerifier verifier,
        IDelegationAcceptanceAuthorizer authorizer, string tenantId)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("A tenant identity is required.", nameof(tenantId));
        _tenantId = tenantId;
    }

    public async ValueTask<MarangDelegationAcceptanceOutcome> AuthorizeAsync(
        MarangDelegationAcceptanceInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        var credential = await _verifier.VerifyAsync(input.Credential, input.Caller, cancellationToken).ConfigureAwait(false);
        if (credential == MarangDelegationCredentialStatus.Missing)
            return new(MarangDelegationAcceptanceStatus.Denied, "marang.delegation-credential-missing");
        if (credential == MarangDelegationCredentialStatus.Invalid)
            return new(MarangDelegationAcceptanceStatus.Denied, "marang.delegation-credential-invalid");

        DelegationAcceptanceRequest request;
        try
        {
            var context = new AuthenticatedAuthorityContext(_tenantId, input.Caller, input.AcceptanceIdentity,
                input.Generation, "accept");
            request = new DelegationAcceptanceRequest(context, input.AcceptanceIdentity, input.WorkspaceId,
                input.Provider, input.Generation, input.BoundedRequestDigest);
        }
        catch (ArgumentException exception)
        {
            return new(MarangDelegationAcceptanceStatus.Denied, "marang.acceptance-request-invalid:" + exception.Message);
        }

        var result = await _authorizer.AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.IsAuthorized)
            return new(MarangDelegationAcceptanceStatus.Accepted, result.Decision!.ReasonCode);
        return result.Status == AuthorityStatus.Deny
            ? new(MarangDelegationAcceptanceStatus.Denied, result.Decision?.ReasonCode ?? "marang.acceptance-denied")
            : new(MarangDelegationAcceptanceStatus.Unavailable, result.Decision?.ReasonCode ?? "marang.acceptance-unavailable");
    }
}
