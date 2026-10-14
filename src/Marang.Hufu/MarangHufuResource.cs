using Penghou.Hufu;
using Penghou.Hufu.Cedar;
using Penghou.Hufu.Sqlite;

namespace Marang.Hufu;

/// <summary>Host-configured resource policy bounds. Never caller-supplied.</summary>
public sealed record MarangResourceAuthorizationPolicy
{
    /// <summary>Callers (stable subjects) permitted to perform resource operations.</summary>
    public IReadOnlySet<string> AllowedCallers { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Workspace ceilings on which resource operations may be performed.</summary>
    public IReadOnlySet<string> AllowedWorkspaces { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Host resource evaluator: Hufu-owned grant evaluation (Cedar over current
/// authority) ANDed with host caller/workspace bounds. Marang never duplicates
/// authority evaluation; the bounds are an additional host kill-switch, not a
/// reimplementation. Grant coverage (action + scope + window) is decided by
/// the inner Hufu evaluator.
/// </summary>
public sealed class HufuResourceAuthorizationEvaluator : IAuthorityEvaluator
{
    private readonly IAuthorityEvaluator _inner;
    private readonly MarangResourceAuthorizationPolicy _policy;

    public HufuResourceAuthorizationEvaluator(
        MarangResourceAuthorizationPolicy policy, IAuthorityEvaluator? inner = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _inner = inner ?? new CedarAuthorityEvaluator();
    }

    public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, AuthorityRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        if (snapshot.Context != request.Context)
            return new AuthorityDecision(AuthorityStatus.Deny, "marang.resource-context-mismatch",
                snapshot.Version, "marang-resource-evaluator-v1", snapshot.Identity);
        if (!_policy.AllowedCallers.Contains(request.Context.SubjectId))
            return new AuthorityDecision(AuthorityStatus.Deny, "marang.resource-caller-denied",
                snapshot.Version, "marang-resource-evaluator-v1", snapshot.Identity);
        if (!_policy.AllowedWorkspaces.Contains(request.WorkspaceId))
            return new AuthorityDecision(AuthorityStatus.Deny, "marang.resource-workspace-denied",
                snapshot.Version, "marang-resource-evaluator-v1", snapshot.Identity);
        return _inner.Evaluate(snapshot, request, now);
    }
}

/// <summary>One bounded resource-authorization attempt at the Marang boundary.</summary>
public sealed record MarangResourceAuthorizationInput(
    string Caller,
    AuthorityAction Action,
    string DelegationId,
    string WorkspaceId,
    string RelativePath,
    MarangDelegationCredential? Credential);

/// <summary>Typed resource outcome. Only <see cref="Permitted"/> may precede a protected side effect.</summary>
public enum MarangResourceAuthorizationStatus { Permitted, Denied, Unavailable }

/// <summary>The result of the resource gate, with typed reason and snapshot binding.</summary>
public sealed record MarangResourceAuthorizationOutcome(
    MarangResourceAuthorizationStatus Status, string ReasonCode,
    string? SnapshotVersion = null, string? SnapshotIdentity = null);

/// <summary>
/// The qualified resource gate: credential verification first, then a fresh
/// Hufu <c>AuthorityRequest</c> decision against current authority. It never
/// authorizes on the credential alone, never consults acceptance authority,
/// and never permits an action outside the four Marang resource meanings.
/// </summary>
public interface IMarangResourceAuthorizationGate
{
    ValueTask<MarangResourceAuthorizationOutcome> AuthorizeAsync(
        MarangResourceAuthorizationInput input, CancellationToken cancellationToken = default);
}

/// <summary>
/// Fail-closed default gate for hosts without a composed Hufu resource
/// source. It never permits; it is not a bypass and produces no state.
/// </summary>
public sealed class FailClosedResourceAuthorizationGate : IMarangResourceAuthorizationGate
{
    public ValueTask<MarangResourceAuthorizationOutcome> AuthorizeAsync(
        MarangResourceAuthorizationInput input, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new MarangResourceAuthorizationOutcome(
            MarangResourceAuthorizationStatus.Unavailable, "marang.resource-authority-not-configured"));
}

/// <summary>
/// Default resource gate composition. The caller is projected to an
/// authenticated Hufu context with a fixed shape: tenant from host
/// configuration, subject from the authenticated caller, run from the
/// delegation under operation, the acceptance generation, and a constant
/// resource fence (distinct from the acceptance fence, so acceptance and
/// resource decisions can never share a context).
/// </summary>
public sealed class MarangResourceAuthorizationGate : IMarangResourceAuthorizationGate
{
    /// <summary>
    /// Revision bound into every resource context. Operators publish resource
    /// snapshots against this exact revision; it is a protocol constant, not
    /// caller input. (Mirrors the acceptance initial generation.)
    /// </summary>
    public const string Revision = "g0";

    /// <summary>
    /// Fence bound into every resource context. Distinct from the acceptance
    /// fence so the two decision domains never share a context.
    /// </summary>
    public const string Fence = "resource";

    private readonly IMarangDelegationCredentialVerifier _verifier;
    private readonly IAuthorityRequestAuthorizer _authorizer;
    private readonly string _tenantId;

    public MarangResourceAuthorizationGate(IMarangDelegationCredentialVerifier verifier,
        IAuthorityRequestAuthorizer authorizer, string tenantId)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("A tenant identity is required.", nameof(tenantId));
        _tenantId = tenantId;
    }

    public async ValueTask<MarangResourceAuthorizationOutcome> AuthorizeAsync(
        MarangResourceAuthorizationInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        // Only the four Marang resource meanings may authorize. Anything else
        // (including file verbs from a confused caller) is unmapped: deny.
        if (input.Action is not AuthorityAction.MarangReadDelegation and not AuthorityAction.MarangReadResult
            and not AuthorityAction.MarangIntervene and not AuthorityAction.MarangCancel)
            return new(MarangResourceAuthorizationStatus.Denied, "marang.resource-action-unmapped");

        var credential = await _verifier.VerifyAsync(input.Credential, input.Caller, cancellationToken).ConfigureAwait(false);
        if (credential == MarangDelegationCredentialStatus.Missing)
            return new(MarangResourceAuthorizationStatus.Denied, "marang.delegation-credential-missing");
        if (credential == MarangDelegationCredentialStatus.Invalid)
            return new(MarangResourceAuthorizationStatus.Denied, "marang.delegation-credential-invalid");

        AuthorityRequest request;
        try
        {
            // Reject non-canonical resource segments instead of normalizing
            // them: folding attacker-controlled identity can collapse distinct
            // resources. Hufu normalization then only validates.
            if (input.RelativePath != input.RelativePath.ToLowerInvariant())
                return new(MarangResourceAuthorizationStatus.Denied, "marang.resource-invalid");
            var path = AuthorityValidation.NormalizePath(input.RelativePath);
            var context = new AuthenticatedAuthorityContext(_tenantId, input.Caller, input.DelegationId,
                Revision, Fence);
            request = new AuthorityRequest(context, input.Action, input.WorkspaceId, path,
                Guid.NewGuid().ToString("N"));
        }
        catch (ArgumentException)
        {
            return new(MarangResourceAuthorizationStatus.Denied, "marang.resource-invalid");
        }

        var result = await _authorizer.AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.IsAuthorized)
            return new(MarangResourceAuthorizationStatus.Permitted, result.Decision!.ReasonCode,
                result.Decision.SnapshotVersion, result.Decision.SnapshotIdentity);
        return result.Status == AuthorityStatus.Deny
            ? new(MarangResourceAuthorizationStatus.Denied, result.Decision?.ReasonCode ?? "marang.resource-denied")
            : new(MarangResourceAuthorizationStatus.Unavailable, result.Decision?.ReasonCode ?? "marang.resource-unavailable");
    }
}

/// <summary>
/// Host configuration for live resource-authorization composition. All fields
/// are host facts, never caller input. Trusted public keys arrive as pinned
/// anchors; there is no private key material anywhere in this composition.
/// </summary>
public sealed record MarangHufuResourceOptions
{
    /// <summary>Hufu authority database path. Null/empty selects unconfigured (fail-closed) mode.</summary>
    public string? AuthorityDatabasePath { get; init; }

    /// <summary>Authorization tenant. Defaults to the Marang single-tenant scope.</summary>
    public string TenantId { get; init; } = "marang";

    /// <summary>Pinned trust anchors. Empty selects unconfigured (fail-closed) mode.</summary>
    public IReadOnlyList<MarangHufuTrustedIssuer> TrustedIssuers { get; init; } = [];

    /// <summary>Resource policy bounds. Never caller-supplied.</summary>
    public MarangResourceAuthorizationPolicy ResourcePolicy { get; init; } = new();
}

/// <summary>
/// Host-owned live resource composition: a working gate plus its inspectable
/// evidence journal. Unconfigured hosts receive the fail-closed gate.
/// </summary>
public sealed record MarangHufuResourceComposition(
    IMarangResourceAuthorizationGate Gate,
    IReadOnlyList<(AuthorityRequest Request, AuthorityDecision Decision)> Evidence);

/// <summary>Builds the live or fail-closed resource composition from host options.</summary>
public static class MarangHufuResourceCompositionFactory
{
    public static MarangHufuResourceComposition Create(
        MarangHufuResourceOptions options,
        IAuthorityStoreAuthorizer storeAuthorizer,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storeAuthorizer);
        if (string.IsNullOrWhiteSpace(options.AuthorityDatabasePath) || options.TrustedIssuers.Count == 0)
        {
            return new MarangHufuResourceComposition(
                new FailClosedResourceAuthorizationGate(),
                Array.AsReadOnly(Array.Empty<(AuthorityRequest, AuthorityDecision)>()));
        }

        var clock = timeProvider ?? TimeProvider.System;
        var store = new SqliteAuthorityStore(options.AuthorityDatabasePath, storeAuthorizer, clock);
        var actor = new AuthorityStoreActor(options.TenantId, "resource-host", "session");
        var source = new AuthorityStoreSnapshotSource(store, actor);
        var entries = new List<(AuthorityRequest Request, AuthorityDecision Decision)>();
        var authorizer = new CurrentAuthorityRequestAuthorizer(
            source,
            new HufuResourceAuthorizationEvaluator(options.ResourcePolicy),
            new ResourceEvidenceRecorder(entries),
            clock);
        var gate = new MarangResourceAuthorizationGate(
            new BiscuitTrustVerifier(options.TrustedIssuers), authorizer, options.TenantId);
        return new MarangHufuResourceComposition(gate, entries.AsReadOnly());
    }

    private sealed class ResourceEvidenceRecorder(List<(AuthorityRequest, AuthorityDecision)> entries) : IAuthorityDecisionRecorder
    {
        public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(decision);
            lock (entries) entries.Add((request, decision));
            return ValueTask.FromResult(true);
        }
    }
}
