using System.Text.Json;
using Marang.Runs.Adaptive;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Runs.Effects;

/// <summary>Coordinates physical reconciliation with the existing atomic revision owner; acceptance lives only in its immutable archive.</summary>
public sealed class RepositoryEffectGraphRevisionGuard(ControlledRepository repository, SqliteRepositoryEffectJournal effects,
    SqliteRepositoryEffectGraphJournal graphs, SqliteAdaptiveRevisionRepository revisions) : IRepositoryEffectRevisionGuard
{
    public async ValueTask<IAsyncDisposable> EnterAsync(AdaptiveRevision parent, AdaptiveRevision candidate, bool activating,
        CancellationToken cancellationToken = default)
    {
        var lease = await repository.AcquireLockAsync(cancellationToken);
        try
        {
            var current = await revisions.GetActiveAsync(candidate.WorkflowId, cancellationToken);
            if (current.RevisionId != parent.RevisionId || candidate.ParentRevisionId != parent.RevisionId)
                throw new WorkflowStateException("Causal graph proposal must select the exact current parent.");
            var graph = AdaptivePlanAuthoring.ReadEffectGraph(candidate) ?? throw new WorkflowStateException("A causal workflow successor must retain an explicit effect graph and physical dispositions.");
            var previous = AdaptivePlanAuthoring.ReadEffectGraph(parent);
            var observed = repository.ObserveUnderLock();
            if (graph.RepositoryId != repository.RepositoryId || graph.PhysicalBaseState.StateDigest != observed.StateDigest ||
                (await graphs.GetGraphAsync(graph.WorkflowId, graph.RevisionId, cancellationToken))?.GraphDigest != graph.GraphDigest)
                throw new WorkflowStateException("Candidate causal graph must be prepared against the exact actual checkout.");
            if (previous is null)
            {
                if (graph.PriorEffects.Length != 0 || graph.Effects.Any(s => s.Reuse))
                    throw new WorkflowStateException("Initial causal graph cannot invent prior effect adoption.");
            }
            else
            {
                var expected = RepositoryEffectGraphImpact.Analyze(previous, graph);
                if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(graph.PriorEffects))
                    throw new WorkflowStateException("Effect dispositions must match conservative deterministic dependency impact.");
            }
            var records = await effects.ListForRepositoryAsync(repository.RepositoryId, cancellationToken);
            foreach (var record in records.Where(r => r.Stage != RepositoryEffectStage.Prepared))
            {
                if (record.Stage == RepositoryEffectStage.Blocked && record.Classification is RepositoryEffectClassification.PartiallyApplied or RepositoryEffectClassification.Unresolved)
                    throw new WorkflowStateException("A parked partial or unresolved physical effect blocks graph cutover.");
                var classification = RepositoryEffectReconciler.Classify(record.Intent, observed);
                if (RepositoryEffectGraphRuntime.RecordedApplied(record) && RepositoryEffectGraphRuntime.RecordedLineageTo(record, records, observed.StateDigest)) continue;
                if (classification == RepositoryEffectClassification.DefinitelyApplied && record.Stage is RepositoryEffectStage.IntentCommitted or RepositoryEffectStage.Applying)
                {
                    await effects.TransitionAsync(record.Intent.EffectId, RepositoryEffectStage.AppliedObserved, classification, observed, cancellationToken: cancellationToken);
                    continue;
                }
                if (classification is RepositoryEffectClassification.PartiallyApplied or RepositoryEffectClassification.Unresolved)
                    throw new WorkflowStateException("Every prior physical effect must reconcile; logical invalidation cannot erase files.");
                if (record.Stage is RepositoryEffectStage.IntentCommitted or RepositoryEffectStage.Applying)
                {
                    if (record.Intent.WorkflowId != graph.WorkflowId) throw new WorkflowStateException("Another workflow owns outstanding intent in this checkout.");
                    if (graph.Effects.Any(s => s.Effect.Intent.EffectId == record.Intent.EffectId))
                        throw new WorkflowStateException("In-flight effects cannot be adopted as validated prerequisites.");
                    // This transition is reviewed before proposal state capture; if it happens only at activation, the owner detects stale review.
                    await effects.TransitionAsync(record.Intent.EffectId, RepositoryEffectStage.Blocked, classification, observed, cancellationToken: cancellationToken);
                }
            }
            foreach (var selection in graph.Effects)
            {
                var causal = await graphs.GetEffectAsync(selection.Effect.Intent.EffectId, cancellationToken);
                var record = await effects.GetAsync(selection.Effect.Intent.EffectId, cancellationToken);
                if (causal?.CausalDigest != selection.Effect.CausalDigest || record?.Intent.IntentDigest != selection.Effect.Intent.IntentDigest)
                    throw new WorkflowStateException("Every selected effect must already have its exact immutable causal and mutation intent.");
                if (!selection.Reuse)
                {
                    if (record.Stage != RepositoryEffectStage.Prepared) throw new WorkflowStateException("New graph effects must be prepared and not yet admitted to mutation.");
                    continue;
                }
                var old = previous?.Effects.SingleOrDefault(s => s.LogicalId == selection.LogicalId && s.Effect.Intent.EffectId == selection.Effect.Intent.EffectId);
                if (old?.Effect.CausalDigest != selection.Effect.CausalDigest || !RepositoryEffectGraphRuntime.LocallyValidated(record) ||
                    !RepositoryEffectGraphRuntime.OutputsPresent(selection.Effect.Intent, observed) ||
                    !RepositoryEffectGraphRuntime.RecordedLineageTo(record, records, observed.StateDigest))
                    throw new WorkflowStateException("Explicit adoption requires exact prior causal identity, local validation, compatible output and recognized physical lineage.");
                var oldBranch = parent.Branches.Single(b => b.LogicalId == selection.LogicalId);
                var branch = candidate.Branches.Single(b => b.LogicalId == selection.LogicalId);
                if (!branch.AcceptReuse || oldBranch.Execution.Id != branch.Execution.Id || !AdaptiveRevisionContracts.Equivalent(oldBranch, branch))
                    throw new WorkflowStateException("Adopted effect must preserve its exact Fuwen execution contract and explicit reuse.");
            }
            return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    public async ValueTask<IAsyncDisposable> EnterCancellationAsync(AdaptiveRevision active, CancellationToken cancellationToken = default)
    {
        var lease = await repository.AcquireLockAsync(cancellationToken);
        try { _ = repository.ObserveUnderLock(); return lease; }
        catch { await lease.DisposeAsync(); throw; }
    }
}
