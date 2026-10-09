using Penghou.Hongxian;
using Penghou.Zhinu;

namespace Marang.SessionJournal;

/// <summary>One operator-readable journal entry (structured Hongxian event, no rendering invented here).</summary>
public sealed record JournalEntryView(
    long Sequence,
    string EventType,
    DateTimeOffset CommittedAt,
    string Participant,
    IReadOnlyDictionary<string, string> Refs);

/// <summary>One active session incident.</summary>
public sealed record JournalIncidentView(
    string IncidentId,
    string? ReasonCode,
    string Severity,
    DateTimeOffset DetectedAt);

/// <summary>
/// Operator-facing session journal. `Available=false` means no session
/// evidence could be read (nonexistent, malformed, or store failure) — never
/// a reason to distrust the Zhinu run graph, which is served separately.
/// </summary>
public sealed record SessionJournalView(
    bool Available,
    string? UnavailableReason,
    string SessionId,
    long AppliedSequence,
    string OperatorState,
    string RecoveryState,
    int TotalEvents,
    IReadOnlyList<JournalIncidentView> Incidents,
    IReadOnlyList<JournalEntryView> Entries,
    bool HasMore,
    long? NextSequence);

/// <summary>
/// Maps authoritative upstream facts to operator recovery states. This is a
/// classification, not a state machine: it reports what Zhinu and Hongxian
/// already say, and represents anything unknowable as unknown/incomplete
/// rather than inferring it.
/// </summary>
public static class RecoveryStates
{
    /// <summary>Classifies the operator recovery state. Never throws.</summary>
    public static string Classify(
        WorkflowStatus runStatus,
        SessionOperatorState? sessionOperatorState,
        bool hasSession,
        bool readOk)
    {
        if (runStatus is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled)
        {
            return "terminal";
        }

        if (!readOk || !hasSession)
        {
            return "unavailable";
        }

        return sessionOperatorState switch
        {
            SessionOperatorState.Corrupt => "inconsistent",
            SessionOperatorState.ReconciliationRequired => "inconsistent",
            SessionOperatorState.Recovering => "recovering",
            SessionOperatorState.AwaitingInput => "waiting",
            SessionOperatorState.AwaitingApproval => "waiting",
            _ => "running",
        };
    }

    /// <summary>Formats a participant for display without inventing identity.</summary>
    public static string DisplayParticipant(SessionParticipantAttribution participant) =>
        participant.DisplayName
        ?? participant.Subject
        ?? $"{participant.Kind}@{participant.Provider}";
}
