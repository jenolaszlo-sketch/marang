using System.Collections.Concurrent;
using Marang.DelegationAuthority;
using Marang.Hufu;
using Microsoft.AspNetCore.Http;
using Penghou.Hufu;
using Penghou.Qingniao;

namespace Marang;

/// <summary>
/// Host-owned index of accepted delegations. The acceptance registry
/// guarantees caller-scoped idempotency with deterministic delegation IDs
/// derived from <c>(tenant, subject, requestKey)</c>; this index retains the
/// caller and request metadata needed for authorized reads. Every record
/// carries a Siming-backed content fingerprint; reads that fail fingerprint
/// verification are treated as unknown delegations (fail closed).
///
/// Every acceptance first passes the mandatory Hufu
/// <c>DelegationAcceptanceOperation.Accept</c> gate (with credential verification
/// before it) before any durable state is created or mutated. There is no
/// fallback that bypasses that decision.
///
/// Durable mode (descriptor + Hufu-owned visibility stores configured) runs
/// an ordered, idempotent acceptance protocol after the acceptance decision:
/// descriptor first, visibility binding second,
/// in-memory acceptance third, then a cross-record agreement check. There is
/// no cross-store transaction; exact retries converge via deterministic
/// identities, and partial crash state is completed by retry — never by
/// ordinary reads, which only verify.
/// </summary>
public sealed class MarangDelegationCatalog : IDelegationAcceptanceRegistry
{
    private readonly ConcurrentDictionary<ScopedAcceptanceKey, AcceptedAcceptance> _accepted = new();
    private readonly ConcurrentDictionary<DelegationId, MarangDelegationRecord> records = new();
    private readonly ConcurrentDictionary<DelegationId, string> fingerprints = new();
    private readonly IAcceptedDelegationStore? _descriptors;
    private readonly IDelegationVisibilityStore? _visibility;
    private readonly IMarangDelegationAcceptanceGate _acceptanceGate;
    private readonly IHttpContextAccessor? _httpContext;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _visibilityValidity;

    /// <summary>
    /// Catalog with optional durable acceptance authority and a required Hufu
    /// acceptance gate. The gate is mandatory: there is no construction path
    /// that accepts delegated work without a Hufu acceptance decision. Descriptor
    /// and visibility stores are supplied together or not at all.
    /// </summary>
    public MarangDelegationCatalog(
        IMarangDelegationAcceptanceGate acceptanceGate,
        IAcceptedDelegationStore? descriptors = null,
        IDelegationVisibilityStore? visibility = null,
        Func<DateTimeOffset>? now = null,
        TimeSpan? visibilityValidity = null,
        IHttpContextAccessor? httpContext = null)
    {
        if ((descriptors is null) != (visibility is null))
        {
            throw new ArgumentException("Descriptor and visibility stores must be supplied together.", nameof(descriptors));
        }

        _acceptanceGate = acceptanceGate ?? throw new ArgumentNullException(nameof(acceptanceGate));
        _descriptors = descriptors;
        _visibility = visibility;
        _httpContext = httpContext;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _visibilityValidity = visibilityValidity ?? TimeSpan.FromDays(30);
        if (_visibilityValidity <= TimeSpan.Zero || _visibilityValidity > TimeSpan.FromDays(3650))
        {
            throw new ArgumentOutOfRangeException(nameof(visibilityValidity), "Visibility validity must be positive and bounded.");
        }
    }

    public async ValueTask<DelegationAcceptance> AcceptAsync(
        DelegationCallerScope caller,
        DelegationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Qingniao content identity (existing v3 semantics; validates the request).
        var fingerprint = DelegationRequestIdentity.Compute(request);
        var principal = MarangPrincipalMapper.MapCaller(caller.Identifier);
        var acceptanceKey = AcceptanceIdentity.AcceptanceKey(principal.TenantId, principal.SubjectId, request.RequestKey);

        // Mandatory Hufu acceptance decision, before any durable acceptance state.
        // The credential is carried in HTTP metadata, never in the tool arguments.
        var credential = _httpContext?.HttpContext?.Items[MarangHttpContextKeys.DelegationCredential] as MarangDelegationCredential;
        var outcome = await _acceptanceGate.AuthorizeAsync(
            new MarangDelegationAcceptanceInput(
                principal.SubjectId, acceptanceKey, request.Workspace.Identifier, request.Provider,
                AcceptanceIdentity.InitialGeneration, fingerprint.Hash, credential),
            cancellationToken).ConfigureAwait(false);
        if (outcome.Status != MarangDelegationAcceptanceStatus.Accepted)
        {
            throw new MarangDelegationAcceptanceException(outcome);
        }

        var delegationId = AcceptanceIdentity.DelegationIdFor(acceptanceKey);
        var now = _now();
        var isNew = true;

        if (_descriptors is not null && _visibility is not null)
        {
            // Ordered protocol, no cross-store transaction:
            //  1. durable product metadata (insert-by-key, conflict on content change),
            //  2. durable visibility authority (idempotent publish, terminal revocation),
            //  3. process-local acceptance (deterministic replay),
            //  4. cross-record agreement check (fail closed, never auto-repaired).
            var descriptor = new AcceptedDelegation(
                acceptanceKey, delegationId,
                principal.TenantId, principal.SubjectId, request.RequestKey,
                fingerprint.Version, fingerprint.Hash,
                request.Objective, request.Provider, request.Workspace.Identifier,
                AcceptanceIdentity.InitialGeneration, now);
            try
            {
                isNew = (await _descriptors.EnsureAsync(descriptor, cancellationToken).ConfigureAwait(false)).Status
                    == AcceptedDelegationEnsureStatus.Created;
            }
            catch (AcceptedDelegationConflictException exception)
            {
                throw new DelegationRequestKeyConflictException(
                    caller, request.RequestKey,
                    ParseFingerprint(exception.ExistingFingerprint),
                    new DelegationRequestFingerprint(fingerprint.Version, fingerprint.Hash));
            }

            var binding = new DelegationVisibilityBinding(
                principal.TenantId, principal.SubjectId, delegationId.ToString("D"),
                AcceptanceIdentity.InitialGeneration, request.RequestKey,
                fingerprint.Version, fingerprint.Hash,
                DelegationVisibilityOperation.Observe | DelegationVisibilityOperation.Cancel | DelegationVisibilityOperation.Intervene,
                now, now.Add(_visibilityValidity),
                AcceptanceIdentity.VisibilityCommandId(acceptanceKey, AcceptanceIdentity.InitialGeneration),
                Revoked: false, RevokeReason: string.Empty, now);
            var (publishStatus, published) = await _visibility.PublishAsync(binding, cancellationToken).ConfigureAwait(false);
            switch (publishStatus)
            {
                case DelegationVisibilityPublishStatus.Published:
                    isNew = true;
                    break;
                case DelegationVisibilityPublishStatus.Replayed:
                    break;
                case DelegationVisibilityPublishStatus.Revoked:
                    throw new InvalidOperationException(
                        $"Delegation visibility for request key '{request.RequestKey}' was revoked; restoring access requires a new request key.");
                default:
                    throw new DelegationRequestKeyConflictException(
                        caller, request.RequestKey,
                        published is null
                            ? new DelegationRequestFingerprint(fingerprint.Version, fingerprint.Hash)
                            : new DelegationRequestFingerprint(published.FingerprintVersion, published.FingerprintHash),
                        new DelegationRequestFingerprint(fingerprint.Version, fingerprint.Hash));
            }

            VerifyAgreement(descriptor, published ?? binding, request.RequestKey);
        }

        // Process-local acceptance: caller-scoped idempotency with the same
        // deterministic identity, so replays (same content) converge and
        // content changes conflict — with or without durable stores.
        var key = new ScopedAcceptanceKey(caller.Identifier, request.RequestKey);
        var candidate = new AcceptedAcceptance(new DelegationId(delegationId), fingerprint);
        var existingOrAdded = _accepted.GetOrAdd(key, candidate);
        if (existingOrAdded.Fingerprint != fingerprint)
        {
            throw new DelegationRequestKeyConflictException(
                caller, request.RequestKey, existingOrAdded.Fingerprint, fingerprint);
        }

        if (!existingOrAdded.DelegationId.Value.Equals(delegationId))
        {
            // Cannot happen: both derive from the same acceptance key. Fail
            // closed rather than returning a mismatched identity.
            throw new InvalidOperationException("The acceptance identity disagrees with its durable binding.");
        }

        isNew = isNew && ReferenceEquals(existingOrAdded, candidate);
        Remember(caller.Identifier, request, existingOrAdded.DelegationId);
        return new DelegationAcceptance(existingOrAdded.DelegationId, existingOrAdded.Fingerprint, isNew);
    }

    /// <summary>
    /// Rebuilds the process-local read cache for one delegation from durable
    /// state after restart. Verify-only: descriptor and visibility must exist
    /// and agree; any absence or disagreement returns false without writing
    /// or repairing anything. Only exact acceptance retry may complete
    /// partial crash state, because only it carries the full request context.
    /// </summary>
    public async ValueTask<bool> ReattachAsync(
        string caller,
        DelegationId delegationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);
        if (_descriptors is null || _visibility is null)
        {
            return false;
        }

        MarangPrincipal principal;
        try
        {
            principal = MarangPrincipalMapper.MapCaller(caller);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var descriptor = await _descriptors.GetByDelegationIdAsync(delegationId.Value, cancellationToken).ConfigureAwait(false);
        if (descriptor is null
            || !string.Equals(descriptor.TenantId, principal.TenantId, StringComparison.Ordinal)
            || !string.Equals(descriptor.SubjectId, principal.SubjectId, StringComparison.Ordinal))
        {
            return false;
        }

        var check = await _visibility.CheckAsync(
            principal.TenantId, principal.SubjectId, delegationId.Value.ToString("D"), descriptor.Generation,
            DelegationVisibilityOperation.Observe, _now(), cancellationToken).ConfigureAwait(false);
        if (check != DelegationVisibilityCheckStatus.Granted)
        {
            return false;
        }

        var binding = await _visibility.GetAsync(
            principal.TenantId, principal.SubjectId, delegationId.Value.ToString("D"), descriptor.Generation, cancellationToken).ConfigureAwait(false);
        if (binding is null
            || !string.Equals(binding.RequestKey, descriptor.RequestKey, StringComparison.Ordinal)
            || !string.Equals(binding.FingerprintVersion, descriptor.FingerprintVersion, StringComparison.Ordinal)
            || !string.Equals(binding.FingerprintHash, descriptor.FingerprintHash, StringComparison.Ordinal)
            || !string.Equals(binding.Generation, descriptor.Generation, StringComparison.Ordinal))
        {
            // Descriptor/visibility disagreement: fail closed, never repair
            // from guesses on the read path.
            return false;
        }

        Remember(caller, new DelegationRequest(
            descriptor.RequestKey, descriptor.Objective, descriptor.Provider,
            new WorkspaceReference("local", descriptor.Workspace),
            ["Done"], [], new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2)), delegationId);
        return Find(delegationId, caller) is not null;
    }

    public MarangDelegationRecord? Find(DelegationId id, string caller) =>
        records.TryGetValue(id, out var record) &&
        string.Equals(record.Caller, caller, StringComparison.Ordinal) &&
        Verify(record)
            ? record : null;

    public IReadOnlyList<MarangDelegationRecord> List(string caller) =>
        records.Values
            .Where(record => string.Equals(record.Caller, caller, StringComparison.Ordinal))
            .Where(Verify)
            .OrderBy(record => record.Id.Value)
            .ToArray();

    private void Remember(string caller, DelegationRequest request, DelegationId delegationId)
    {
        records.TryAdd(delegationId, new MarangDelegationRecord(
            delegationId,
            caller,
            request.Objective,
            request.Provider,
            request.Workspace.Identifier));
        fingerprints.TryAdd(delegationId, MarangDelegationFingerprint.Compute(
            delegationId.Value,
            caller,
            request.Objective,
            request.Provider,
            request.Workspace.Identifier));
    }

    private static void VerifyAgreement(AcceptedDelegation descriptor, DelegationVisibilityBinding binding, string requestKey)
    {
        if (!string.Equals(binding.DelegationId, descriptor.DelegationId.ToString("D"), StringComparison.Ordinal)
            || !string.Equals(binding.TenantId, descriptor.TenantId, StringComparison.Ordinal)
            || !string.Equals(binding.SubjectId, descriptor.SubjectId, StringComparison.Ordinal)
            || !string.Equals(binding.RequestKey, descriptor.RequestKey, StringComparison.Ordinal)
            || !string.Equals(binding.FingerprintVersion, descriptor.FingerprintVersion, StringComparison.Ordinal)
            || !string.Equals(binding.FingerprintHash, descriptor.FingerprintHash, StringComparison.Ordinal)
            || !string.Equals(binding.Generation, descriptor.Generation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The durable acceptance records disagree for request key '{requestKey}'; refusing to expose the delegation.");
        }
    }

    private static DelegationRequestFingerprint ParseFingerprint(string value)
    {
        var separator = value.IndexOf(':');
        if (separator > 0)
        {
            try
            {
                return new DelegationRequestFingerprint(value[..separator], value[(separator + 1)..]);
            }
            catch (ArgumentException)
            {
            }
        }

        // Unparseable stored fingerprint: report a conflicting sentinel rather
        // than failing open. Construction cannot fail for this shape.
        return new DelegationRequestFingerprint("v3", new string('0', 64));
    }

    private bool Verify(MarangDelegationRecord record) =>
        fingerprints.TryGetValue(record.Id, out var fingerprint) &&
        MarangDelegationFingerprint.Verify(
            record.Id.Value,
            record.Caller,
            record.Objective,
            record.Provider,
            record.Workspace,
            fingerprint);

    private readonly record struct ScopedAcceptanceKey(string Caller, string RequestKey);

    private sealed record AcceptedAcceptance(DelegationId DelegationId, DelegationRequestFingerprint Fingerprint);
}

public sealed record MarangDelegationRecord(
    DelegationId Id,
    string Caller,
    string Objective,
    string Provider,
    string Workspace);
