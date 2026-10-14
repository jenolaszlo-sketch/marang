using BiscuitSharp;
using Penghou.Hufu;
using Penghou.Hufu.Biscuit;
using Penghou.Hufu.Sqlite;

namespace Marang.Hufu;

/// <summary>
/// One pinned Biscuit trust anchor. The public key is host-held trust material
/// supplied from host configuration (or another qualified path); it is not a
/// secret, but it is never caller input. No private key material is involved:
/// verification needs only the public key.
/// </summary>
public sealed record MarangHufuTrustedIssuer(string Realm, string KeyId, BiscuitPublicKey PublicKey)
{
    public bool Valid =>
        AuthorityValidation.ValidToken(Realm) && AuthorityValidation.ValidToken(KeyId) && PublicKey is not null;
}

/// <summary>
/// Host configuration for live delegation-acceptance composition. Binds
/// <c>Marang:Hufu</c> at the Server composition root. All fields are host facts,
/// never caller input. Trusted public keys arrive as pinned anchors; there is no
/// private key material anywhere in this composition.
/// </summary>
public sealed record MarangHufuAcceptanceOptions
{
    /// <summary>Hufu authority database path. Null/empty selects unconfigured (fail-closed) mode.</summary>
    public string? AuthorityDatabasePath { get; init; }

    /// <summary>Acceptance tenant. Defaults to the Marang single-tenant scope.</summary>
    public string TenantId { get; init; } = "marang";

    /// <summary>Pinned trust anchors. Empty selects unconfigured (fail-closed) mode.</summary>
    public IReadOnlyList<MarangHufuTrustedIssuer> TrustedIssuers { get; init; } = [];

    /// <summary>Acceptance policy bounds. Never caller-supplied.</summary>
    public MarangDelegationAcceptancePolicy AcceptancePolicy { get; init; } = new();
}

/// <summary>
/// Biscuit cryptographic trust verification over host-pinned public keys. A
/// credential verifies only when its envelope names a pinned key id and its
/// bytes parse as a token whose signature verifies under that key. Envelope
/// shape alone is insufficient; an unpinned issuer is rejected even when the
/// envelope is well-formed. The pinned keys are in-memory host trust material;
/// there is no external trust source to become unavailable at verify time. A
/// host without pinned trust is unconfigured and composes the fail-closed gate.
/// </summary>
public sealed class BiscuitTrustVerifier : IMarangDelegationCredentialVerifier
{
    private readonly IReadOnlyList<MarangHufuTrustedIssuer> _issuers;

    public BiscuitTrustVerifier(IEnumerable<MarangHufuTrustedIssuer> issuers)
    {
        ArgumentNullException.ThrowIfNull(issuers);
        var pinned = issuers.ToArray();
        if (pinned.Length == 0 || pinned.Any(i => i is null || !i.Valid))
            throw new ArgumentException("At least one valid pinned trust anchor is required.", nameof(issuers));
        _issuers = Array.AsReadOnly(pinned);
    }

    public ValueTask<MarangDelegationCredentialStatus> VerifyAsync(
        MarangDelegationCredential? credential, string caller, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (credential is null) return new(MarangDelegationCredentialStatus.Missing);
        if (!AuthorityValidation.ValidToken(caller)) return new(MarangDelegationCredentialStatus.Missing);
        if (credential.Kind != MarangDelegationCredentialKind.Biscuit) return new(MarangDelegationCredentialStatus.Invalid);
        BiscuitEnvelope envelope;
        try
        {
            envelope = new BiscuitEnvelope(credential.Identity, credential.Bytes);
        }
        catch (ArgumentException)
        {
            return new(MarangDelegationCredentialStatus.Invalid);
        }

        foreach (var issuer in _issuers)
        {
            if (!string.Equals(envelope.RootKeyId, issuer.KeyId, StringComparison.Ordinal)) continue;
            try
            {
                _ = BiscuitToken.Parse(envelope.GetTokenBytes(), issuer.PublicKey);
                return new(MarangDelegationCredentialStatus.Verified);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                // The credential names a pinned key but its signature does not
                // verify under that key. That is forgery or corruption: reject
                // without trying unrelated anchors.
                return new(MarangDelegationCredentialStatus.Invalid);
            }
        }

        return new(MarangDelegationCredentialStatus.Invalid);
    }
}

/// <summary>
/// Least-privilege store authorizer for the acceptance reader: it permits only
/// current-snapshot reads. Publication, revocation, and evidence writes are
/// operator functions outside this composition and are always denied here.
/// </summary>
public sealed class MarangHufuAcceptanceStoreAuthorizer : IAuthorityStoreAuthorizer
{
    public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request?.Operation == AuthorityStoreOperation.ReadCurrent)
            return new(new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
        return new(new AuthorityStoreAuthorization(AuthorityStatus.Deny));
    }
}

/// <summary>
/// Host-owned live acceptance composition: a working gate plus its inspectable
/// evidence journal. Either the host is fully configured (database path and at
/// least one pinned trust anchor) and this returns a live gate, or it returns
/// the fail-closed gate. It never returns a permissive gate.
/// </summary>
public sealed record MarangHufuAcceptanceComposition(
    IMarangDelegationAcceptanceGate Gate,
    IReadOnlyList<(DelegationAcceptanceRequest Request, AuthorityDecision Decision)> Evidence);

/// <summary>Builds the live or fail-closed acceptance composition from host options.</summary>
public static class MarangHufuAcceptanceCompositionFactory
{
    public static MarangHufuAcceptanceComposition Create(
        MarangHufuAcceptanceOptions options,
        IAuthorityStoreAuthorizer storeAuthorizer,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storeAuthorizer);
        if (string.IsNullOrWhiteSpace(options.AuthorityDatabasePath) || options.TrustedIssuers.Count == 0)
        {
            return new MarangHufuAcceptanceComposition(
                new FailClosedDelegationAcceptanceGate(),
                Array.AsReadOnly(Array.Empty<(DelegationAcceptanceRequest, AuthorityDecision)>()));
        }

        var clock = timeProvider ?? TimeProvider.System;
        var store = new SqliteAuthorityStore(options.AuthorityDatabasePath, storeAuthorizer, clock);
        var actor = new AuthorityStoreActor(options.TenantId, "acceptance-host", "session");
        var source = new AuthorityStoreSnapshotSource(store, actor);
        var entries = new List<(DelegationAcceptanceRequest Request, AuthorityDecision Decision)>();
        var authorizer = new CurrentDelegationAcceptanceAuthorizer(
            source,
            new HufuDelegationAcceptanceEvaluator(options.AcceptancePolicy),
            HufuDelegationAcceptanceRecorder.InMemory(entries),
            clock);
        var gate = new MarangDelegationAcceptanceGate(
            new BiscuitTrustVerifier(options.TrustedIssuers), authorizer, options.TenantId);
        return new MarangHufuAcceptanceComposition(gate, entries.AsReadOnly());
    }
}
