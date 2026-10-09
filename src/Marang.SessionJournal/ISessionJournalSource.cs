using Penghou.Hongxian;

namespace Marang.SessionJournal;

/// <summary>
/// Host-owned reads of Hongxian session evidence. Implementations call
/// `ReadReconciledProjectionAsync` and the ledger page API directly;
/// reconciliation rules are never reproduced here.
/// </summary>
public interface ISessionJournalSource
{
    /// <summary>
    /// Reads the reconciled snapshot plus one bounded entry page. Returns
    /// null when no session evidence exists. Throws on store failure (the
    /// endpoint converts that to journal unavailability, never graph loss).
    /// </summary>
    ValueTask<SessionJournalData?> ReadAsync(SessionId sessionId, CancellationToken cancellationToken = default);
}

/// <summary>Durable Hongxian facts for one session: reconciled snapshot plus one entry page.</summary>
public sealed record SessionJournalData(
    SessionProjectionSnapshot Snapshot,
    IReadOnlyList<SessionEvent> Entries,
    bool HasMore,
    long? NextSequence);
