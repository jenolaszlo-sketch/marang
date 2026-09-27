using System.Collections.Concurrent;
using Penghou.Qingniao;

namespace Marang;

/// <summary>
/// Host-owned index of accepted delegations. The underlying Qingniao acceptance
/// registry guarantees caller-scoped idempotency; this index retains the caller
/// and request metadata needed for authorized UI reads. Every record carries a
/// Siming-backed content fingerprint; reads that fail fingerprint verification
/// are treated as unknown delegations (fail closed).
/// </summary>
public sealed class MarangDelegationCatalog : IDelegationAcceptanceRegistry
{
    private readonly InMemoryDelegationAcceptanceRegistry acceptance = new();
    private readonly ConcurrentDictionary<DelegationId, MarangDelegationRecord> records = new();
    private readonly ConcurrentDictionary<DelegationId, string> fingerprints = new();

    public async ValueTask<DelegationAcceptance> AcceptAsync(
        DelegationCallerScope caller,
        DelegationRequest request,
        CancellationToken cancellationToken = default)
    {
        var accepted = await acceptance.AcceptAsync(caller, request, cancellationToken).ConfigureAwait(false);
        records.TryAdd(accepted.DelegationId, new MarangDelegationRecord(
            accepted.DelegationId,
            caller.Identifier,
            request.Objective,
            request.Provider,
            request.Workspace.Identifier));
        fingerprints.TryAdd(accepted.DelegationId, MarangDelegationFingerprint.Compute(
            accepted.DelegationId.Value,
            caller.Identifier,
            request.Objective,
            request.Provider,
            request.Workspace.Identifier));
        return accepted;
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

    private bool Verify(MarangDelegationRecord record) =>
        fingerprints.TryGetValue(record.Id, out var fingerprint) &&
        MarangDelegationFingerprint.Verify(
            record.Id.Value,
            record.Caller,
            record.Objective,
            record.Provider,
            record.Workspace,
            fingerprint);
}

public sealed record MarangDelegationRecord(
    DelegationId Id,
    string Caller,
    string Objective,
    string Provider,
    string Workspace);
