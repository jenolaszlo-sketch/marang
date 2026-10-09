namespace Marang.DelegationAuthority;

/// <summary>
/// Delegation control verbs. Only verbs with real consumers are granted;
/// <see cref="Observe"/> backs the restart-safe read path. <see cref="Cancel"/>
/// and <see cref="Intervene"/> model the delegation's authorized control
/// surface, but executing them additionally requires a live execution
/// handle/fence, which does not survive restart in this slice.
/// </summary>
[Flags]
public enum DelegationVisibilityOperation
{
    /// <summary>No operation.</summary>
    None = 0,

    /// <summary>Observe delegation detail, run linkage, run projection, journal, and durable evidence.</summary>
    Observe = 1,

    /// <summary>Request cancellation of the delegation.</summary>
    Cancel = 2,

    /// <summary>Apply a supervisor intervention to the delegation.</summary>
    Intervene = 4,
}

/// <summary>
/// Durable authority binding: <c>(tenant, subject, delegation, generation)</c>
/// may exercise the granted operations while live, unexpired, and unrevoked.
/// The fingerprint is stored for write-time conflict detection, not as an
/// identity input: same slot plus different fingerprint is a conflict.
/// </summary>
public sealed record DelegationVisibilityBinding(
    string TenantId,
    string SubjectId,
    Guid DelegationId,
    string Generation,
    string RequestKey,
    string FingerprintVersion,
    string FingerprintHash,
    DelegationVisibilityOperation Operations,
    DateTimeOffset NotBefore,
    DateTimeOffset ExpiresAt,
    string CommandId,
    bool Revoked,
    string RevokeReason,
    DateTimeOffset CreatedAt);

/// <summary>Outcome of publishing one visibility binding.</summary>
public enum DelegationVisibilityPublishStatus
{
    /// <summary>The binding was newly persisted.</summary>
    Published = 0,

    /// <summary>An identical binding already existed; the original is returned.</summary>
    Replayed = 1,

    /// <summary>The slot holds a different binding; nothing was overwritten.</summary>
    Conflict = 2,

    /// <summary>The slot is terminally revoked; restoring access needs a new acceptance.</summary>
    Revoked = 3,
}

/// <summary>Outcome of a visibility authorization check (read path carries no content).</summary>
public enum DelegationVisibilityCheckStatus
{
    /// <summary>The subject may exercise the required operation.</summary>
    Granted = 0,

    /// <summary>No binding exists for this slot.</summary>
    Unknown = 1,

    /// <summary>The binding is terminally revoked.</summary>
    Revoked = 2,

    /// <summary>The binding is outside its validity window.</summary>
    Expired = 3,

    /// <summary>The binding does not grant the required operation.</summary>
    Forbidden = 4,
}

/// <summary>Outcome of revoking one visibility binding.</summary>
public enum DelegationVisibilityRevokeStatus
{
    /// <summary>The binding is now terminally revoked.</summary>
    Revoked = 0,

    /// <summary>The same revocation command was already applied.</summary>
    Replayed = 1,

    /// <summary>No binding exists for this slot.</summary>
    NotFound = 2,

    /// <summary>The slot is already revoked under a different command.</summary>
    Conflict = 3,
}

/// <summary>
/// Durable delegation-visibility authority. Host-owned delegation authority
/// with Hufu-style semantics (idempotent commands, terminal revocation,
/// validity windows); the upstream Hufu record family absorbs this contract
/// when it lands, at which point this store becomes an adapter.
/// </summary>
public interface IDelegationVisibilityStore
{
    /// <summary>
    /// Publishes the binding. Identical replay returns the original;
    /// any difference at an occupied slot is a conflict; a revoked slot
    /// stays revoked. Never overwrites.
    /// </summary>
    ValueTask<(DelegationVisibilityPublishStatus Status, DelegationVisibilityBinding? Binding)> PublishAsync(
        DelegationVisibilityBinding candidate,
        CancellationToken cancellationToken = default);

    /// <summary>Terminally revokes one slot. Revocation is never undone.</summary>
    ValueTask<DelegationVisibilityRevokeStatus> RevokeAsync(
        string tenantId,
        string subjectId,
        Guid delegationId,
        string generation,
        string commandId,
        string reasonCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authorizes one operation at <paramref name="now"/>. Unknown, revoked,
    /// expired, and forbidden are distinct fail-closed outcomes.
    /// </summary>
    ValueTask<DelegationVisibilityCheckStatus> CheckAsync(
        string tenantId,
        string subjectId,
        Guid delegationId,
        string generation,
        DelegationVisibilityOperation required,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one binding by slot, or null when absent.</summary>
    ValueTask<DelegationVisibilityBinding?> GetAsync(
        string tenantId,
        string subjectId,
        Guid delegationId,
        string generation,
        CancellationToken cancellationToken = default);
}
