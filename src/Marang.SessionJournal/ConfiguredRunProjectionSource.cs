using Penghou.Fuwen;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;
using Penghou.Zhinu;

using Marang.Runs;

namespace Marang.SessionJournal;

/// <summary>
/// Production run-projection source backed by durable Zhinu/Fuwen/Hongxian
/// stores. Zhinu supplies runs, steps, and the event watermark; Fuwen
/// supplies compiled plans by revision; Hongxian supplies reconciled session
/// evidence. Marang correlates and projects; it reimplements nothing.
/// </summary>
public sealed class ConfiguredRunProjectionSource : IRunProjectionSource, ISessionJournalSource, IDisposable
{
    private readonly IWorkflowRepository _runs;
    private readonly IWorkflowStepRepository _steps;
    private readonly Func<string, CancellationToken, ValueTask<WorkflowPlan?>> _plans;
    private readonly HongxianSqliteStoreSet _hongxian;
    private bool _disposed;
    private const int JournalPageLimit = 50;

    /// <summary>Initializes a store-backed source. All stores must be open.</summary>
    public ConfiguredRunProjectionSource(
        IWorkflowRepository runs,
        IWorkflowStepRepository steps,
        Func<string, CancellationToken, ValueTask<WorkflowPlan?>> plans,
        HongxianSqliteStoreSet hongxian)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(hongxian);
        _runs = runs;
        _steps = steps;
        _plans = plans;
        _hongxian = hongxian;
    }

    /// <inheritdoc />
    public async ValueTask<WorkflowRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await _runs.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<WorkflowPlan?> GetPlanAsync(string planRevision, CancellationToken cancellationToken = default) =>
        await _plans(planRevision, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<WorkflowStepRun>> GetStepsAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await _steps.GetStepsAsync(runId, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<long> GetThroughSequenceAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        // Durable watermark from committed event sequences (bounded read).
        // A future Zhinu event-page repository would serve this directly;
        // the max committed sequence is the same durable fact.
        var events = await _runs.GetEventsAsync(runId, 0, 1000, cancellationToken).ConfigureAwait(false);
        long through = 0;
        foreach (var @event in events)
        {
            if (@event.Sequence > through)
            {
                through = @event.Sequence;
            }
        }

        return through;
    }

    /// <inheritdoc />
    public async ValueTask<Guid?> GetRunIdByDelegationAsync(Penghou.Qingniao.DelegationId delegationId, CancellationToken cancellationToken = default)
    {
        // Durable reverse lookup by scanning run carriers (bounded; a runs
        // index is the follow-up if this ever becomes hot). No second store.
        const int pageLimit = 200;
        const int maxPages = 5;
        Guid? afterId = null;
        for (var page = 0; page < maxPages; page++)
        {
            var runs = await _runs.GetRunsAsync(
                new RunQuery { AfterId = afterId, Limit = pageLimit }, cancellationToken).ConfigureAwait(false);
            if (runs.Count == 0)
            {
                return null;
            }

            foreach (var run in runs)
            {
                var correlation = RunCorrelation.TryParseMetadataJson(run.MetadataJson, run.Id);
                if (correlation is not null && correlation.DelegationId == delegationId)
                {
                    return run.Id;
                }
            }

            if (runs.Count < pageLimit)
            {
                return null;
            }

            afterId = runs[runs.Count - 1].Id;
        }

        return null;
    }

    /// <inheritdoc />
    public async ValueTask<SessionJournalData?> ReadAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _hongxian.ReadReconciledProjectionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        var page = await _hongxian.Events.ReadPageAsync(
            new SessionEventPageRequest(sessionId, 0, JournalPageLimit), cancellationToken).ConfigureAwait(false);
        return new SessionJournalData(snapshot, page.Events, page.HasMore, page.NextSequence);
    }

    /// <summary>Releases the Hongxian store set.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hongxian.Dispose();
    }
}
