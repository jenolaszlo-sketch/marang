namespace Marang.DelegationAuthority;

/// <summary>
/// Durable Marang-owned product metadata for one accepted delegation.
/// Authority-free by construction: no grants, no authorization decisions, no
/// revocation state, no execution state. Caller visibility authority lives in
/// the <see cref="DelegationVisibilityBinding"/>; this record only describes
/// what was accepted so post-restart reads can reconstruct the envelope.
/// </summary>
public sealed record AcceptedDelegation(
    string AcceptanceKey,
    Guid DelegationId,
    string TenantId,
    string SubjectId,
    string RequestKey,
    string FingerprintVersion,
    string FingerprintHash,
    string Objective,
    string Provider,
    string Workspace,
    string Generation,
    DateTimeOffset CreatedAt);

/// <summary>Raised when a request key is reused with different content.</summary>
public sealed class AcceptedDelegationConflictException : InvalidOperationException
{
    public AcceptedDelegationConflictException(
        string requestKey,
        string existingFingerprint,
        string suppliedFingerprint)
        : base($"Request key '{requestKey}' is already bound to different content.")
    {
        RequestKey = requestKey;
        ExistingFingerprint = existingFingerprint;
        SuppliedFingerprint = suppliedFingerprint;
    }

    public string RequestKey { get; }
    public string ExistingFingerprint { get; }
    public string SuppliedFingerprint { get; }
}

/// <summary>Outcome of ensuring one accepted-delegation descriptor.</summary>
public enum AcceptedDelegationEnsureStatus
{
    /// <summary>The descriptor was newly persisted.</summary>
    Created = 0,

    /// <summary>An identical descriptor already existed; the original is returned.</summary>
    Replayed = 1,
}

/// <summary>Durable insert-by-acceptance-key descriptor store.</summary>
public interface IAcceptedDelegationStore
{
    /// <summary>
    /// Persists the descriptor, or returns the existing one when the stored
    /// fingerprint matches. A fingerprint mismatch throws
    /// <see cref="AcceptedDelegationConflictException"/>; nothing is overwritten.
    /// </summary>
    ValueTask<(AcceptedDelegation Record, AcceptedDelegationEnsureStatus Status)> EnsureAsync(
        AcceptedDelegation candidate,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one descriptor by acceptance key, or null when absent.</summary>
    ValueTask<AcceptedDelegation?> GetByAcceptanceKeyAsync(
        string acceptanceKey,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one descriptor by delegation ID, or null when absent.</summary>
    ValueTask<AcceptedDelegation?> GetByDelegationIdAsync(
        Guid delegationId,
        CancellationToken cancellationToken = default);
}
