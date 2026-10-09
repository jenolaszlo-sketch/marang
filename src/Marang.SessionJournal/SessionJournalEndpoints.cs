using Penghou.Hongxian;

using Marang.Runs;

namespace Marang.SessionJournal;

/// <summary>
/// HTTP adapter for the session journal. Caller scoping reuses the run's
/// correlated delegation; unknown, uncorrelated, and forbidden runs all
/// report 404. Hongxian absence or failure reports journal unavailability
/// (200, `available:false`) and never touches Zhinu execution truth, which
/// is served by the separate run endpoint.
/// </summary>
public static class SessionJournalEndpoints
{
    private const int MaximumEntries = 50;

    /// <summary>Reads one caller-scoped session journal.</summary>
    public static async Task<(int StatusCode, object? Body)> GetJournalAsync(
        MarangDelegationCatalog catalog,
        string caller,
        Guid runId,
        IRunProjectionSource runs,
        ISessionJournalSource sessions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(sessions);

        var run = await runs.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return (404, null);
        }

        var correlation = RunCorrelation.TryParseMetadataJson(run.MetadataJson, runId);
        if (correlation is null)
        {
            return (404, null);
        }

        if (catalog.Find(correlation.DelegationId, caller) is null)
        {
            return (404, null);
        }

        if (correlation.SessionId is not { Length: > 0 } sessionText
            || !SessionId.TryParse(sessionText, out var sessionId))
        {
            return (200, Unavailable(runId, string.Empty, run.Status, "no session evidence"));
        }

        SessionJournalData? data;
        try
        {
            data = await sessions.ReadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return (200, Unavailable(runId, sessionId.Value.ToString("D"), run.Status, "session store unavailable"));
        }

        if (data is null)
        {
            return (200, Unavailable(runId, sessionId.Value.ToString("D"), run.Status, "no session evidence"));
        }

        var snapshot = data.Snapshot;
        var recovery = RecoveryStates.Classify(run.Status, snapshot.State.OperatorState, hasSession: true, readOk: true);
        return (200, new SessionJournalView(
            true,
            null,
            sessionId.Value.ToString("D"),
            snapshot.AppliedSequence,
            snapshot.State.OperatorState.ToString(),
            recovery,
            snapshot.State.TotalEvents,
            snapshot.State.ActiveIncidents?.Select(incident => new JournalIncidentView(
                incident.IncidentId.ToString("D"),
                incident.ReasonCode,
                incident.Severity.ToString(),
                incident.DetectedAt)).ToArray() ?? [],
            data.Entries.Take(MaximumEntries).Select(entry => new JournalEntryView(
                entry.Sequence,
                entry.EventType,
                entry.CommittedAt,
                RecoveryStates.DisplayParticipant(entry.Participant),
                entry.CrossSystemRefs is { } refs
                    ? refs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal))).ToArray(),
            data.HasMore,
            data.NextSequence));
    }

    private static SessionJournalView Unavailable(Guid runId, string sessionId, Penghou.Zhinu.WorkflowStatus runStatus, string reason)
    {
        // Recovery without session facts falls back to run truth only; an
        // unreadable session never upgrades or downgrades execution state.
        var recovery = runStatus switch
        {
            Penghou.Zhinu.WorkflowStatus.Completed => "terminal",
            Penghou.Zhinu.WorkflowStatus.Failed => "terminal",
            Penghou.Zhinu.WorkflowStatus.Cancelled => "terminal",
            _ => "unavailable",
        };
        return new SessionJournalView(
            false, reason, sessionId, 0, "Unknown", recovery, 0, [], [], false, null);
    }
}
