using System.Text.Json;
using Marang.Runs.Adaptive;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Runs.Effects;

public interface IRepositoryEffectRevisionGuard
{
    ValueTask<IAsyncDisposable> EnterAsync(AdaptiveRevision parent, AdaptiveRevision candidate, bool activating,
        CancellationToken cancellationToken = default);
    ValueTask<IAsyncDisposable> EnterCancellationAsync(AdaptiveRevision active, CancellationToken cancellationToken = default);
}

/// <summary>Serializes physical repository reconciliation with proposal review and atomic logical revision cutover.</summary>
public sealed class RepositoryEffectRevisionGuard(ControlledRepository repository, SqliteRepositoryEffectJournal journal)
    : IRepositoryEffectRevisionGuard
{
    public async ValueTask<IAsyncDisposable> EnterAsync(AdaptiveRevision parent, AdaptiveRevision candidate, bool activating,
        CancellationToken cancellationToken = default)
    {
        var lease = await repository.AcquireLockAsync(cancellationToken);
        try
        {
            var observed = repository.ObserveUnderLock();
            var records = await journal.ListForRepositoryAsync(repository.RepositoryId, cancellationToken);
            RepositoryEffectRecord? proposed = null;
            foreach (var branch in candidate.Branches.Where(b => AdaptivePlanAuthoring.IsRepositoryEffectBranch(candidate, b)))
            {
                using var input = JsonDocument.Parse(JsonSerializer.Deserialize<string>(branch.Execution.InputJson!)!);
                var digest = input.RootElement.GetProperty("Input").GetString();
                proposed = records.SingleOrDefault(r => r.Intent.IntentDigest == digest && RepositoryEffectWorkflowBinding.Binds(candidate, r.Intent));
                if (proposed is null || proposed.Intent.RepositoryId != repository.RepositoryId ||
                    proposed.Intent.BaseState.StateDigest != observed.StateDigest || proposed.Stage != RepositoryEffectStage.Prepared)
                    throw new WorkflowStateException("Candidate effect must be prepared against this exact observed repository state before revision cutover.");
            }
            foreach (var record in records.Where(r => r.Intent.EffectId != proposed?.Intent.EffectId && r.Stage != RepositoryEffectStage.Prepared))
            {
                if (record.Stage == RepositoryEffectStage.Blocked &&
                    record.Classification is RepositoryEffectClassification.PartiallyApplied or RepositoryEffectClassification.Unresolved)
                    throw new WorkflowStateException("A parked partial or unresolved effect requires deliberate reconciliation; changing files cannot clear the durable block.");
                var classification = RepositoryEffectReconciler.Classify(record.Intent, observed);
                if (classification is RepositoryEffectClassification.PartiallyApplied or RepositoryEffectClassification.Unresolved)
                {
                    if (!KnownAppliedLineage(record, observed.StateDigest, records))
                        throw new WorkflowStateException("Physical effect remains partial or unresolved; fencing cannot erase it and another revision cannot write.");
                }
                if (record.Intent.WorkflowId != candidate.WorkflowId && classification == RepositoryEffectClassification.DefinitelyNotApplied &&
                    record.Stage is RepositoryEffectStage.IntentCommitted or RepositoryEffectStage.Applying)
                    throw new WorkflowStateException("Another workflow has outstanding mutation intent for this controlled checkout.");
                if (classification == RepositoryEffectClassification.DefinitelyApplied)
                {
                    if (proposed is not null && proposed.Intent.BaseState.StateDigest != record.Intent.ExpectedPostState.StateDigest)
                        throw new WorkflowStateException("Corrective effect must explicitly accept the actual prior post-state as its immutable base.");
                    if (record.Stage is RepositoryEffectStage.IntentCommitted or RepositoryEffectStage.Applying)
                        await journal.TransitionAsync(record.Intent.EffectId, RepositoryEffectStage.AppliedObserved, classification, observed,
                            cancellationToken: cancellationToken);
                }
            }
            return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    public async ValueTask<IAsyncDisposable> EnterCancellationAsync(AdaptiveRevision active, CancellationToken cancellationToken = default)
    {
        var lease = await repository.AcquireLockAsync(cancellationToken);
        try
        {
            // Cancellation fences result authority only. Observe and retain physical truth; never roll files back.
            _ = repository.ObserveUnderLock();
            return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    private static bool KnownAppliedLineage(RepositoryEffectRecord origin, string observed,
        IReadOnlyList<RepositoryEffectRecord> records)
    {
        if (origin.Classification != RepositoryEffectClassification.DefinitelyApplied ||
            origin.ObservedState?.StateDigest != origin.Intent.ExpectedPostState.StateDigest) return false;
        var digest = origin.Intent.ExpectedPostState.StateDigest;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 64 && seen.Add(digest); i++)
        {
            if (digest == observed) return true;
            var next = records.Where(r => r.Intent.BaseState.StateDigest == digest &&
                r.Classification == RepositoryEffectClassification.DefinitelyApplied && r.ObservedState?.StateDigest == r.Intent.ExpectedPostState.StateDigest).ToArray();
            if (next.Length != 1) return false;
            digest = next[0].Intent.ExpectedPostState.StateDigest;
        }
        return false;
    }
}
