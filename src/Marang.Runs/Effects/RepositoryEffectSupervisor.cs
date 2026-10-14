using System.Collections.Immutable;
using Marang.Runs.Adaptive;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Runs.Effects;

public sealed record RepositoryEffectView(Guid EffectId, string RevisionId, string LogicalStepId, string BaseState,
    string PatchIdentity, string ExpectedPostState, string? ObservedState, string Classification, string Stage,
    bool? ValidationPassed, bool IsCurrent, bool ReplayPermitted, bool ReplayEligibleAfterFreshAuthority,
    bool PhysicalEffectRecorded, string Explanation);
public sealed record RepositoryEffectCatchUp(SupervisorCatchUpView Workflow, RepositoryStateSnapshot ObservedRepositoryState,
    ImmutableArray<RepositoryEffectView> Effects, bool MutationBlocked, string Explanation);
public sealed record RepositoryEffectRevisionDetail(RevisionTransitionDetail Workflow, ImmutableArray<RepositoryEffectView> Effects,
    string PhysicalRepositoryExplanation);

/// <summary>Combines M2 durable supervisory truth with current physical repository observation.</summary>
public sealed class RepositoryEffectSupervisor(SqliteAdaptiveRevisionRepository revisions, SqliteRepositoryEffectJournal journal,
    ControlledRepository repository, SupervisorCatchUp supervisor)
{
    public async Task<RepositoryEffectCatchUp> ReadAsync(Guid workflowId, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        // The M2 reader authenticates before any physical observation is disclosed.
        var workflow = await supervisor.ReadAsync(workflowId, actor, cancellationToken);
        await using var lease = await repository.AcquireLockAsync(cancellationToken);
        var observed = repository.ObserveUnderLock();
        workflow = await supervisor.ReadAsync(workflowId, actor, cancellationToken);
        var records = await journal.ListForWorkflowAsync(workflowId, cancellationToken);
        var views = records.Select(record => Project(record, observed, records, workflow.ActiveRevisionId)).ToImmutableArray();
        var scopeRecords = await journal.ListForRepositoryAsync(repository.RepositoryId, cancellationToken);
        var scopeBlocked = scopeRecords.Any(r => r.Stage == RepositoryEffectStage.Blocked &&
            r.Classification is RepositoryEffectClassification.PartiallyApplied or RepositoryEffectClassification.Unresolved);
        var blocked = scopeBlocked || views.Any(v => v.IsCurrent && (v.Classification is "PartiallyApplied" or "Unresolved" || v.ValidationPassed == false));
        var explanation = blocked ? "Repository work needs reconciliation or a reviewed corrective revision. A missing result record is not proof that a mutation did not happen." :
            "The repository was observed under its checkout lock. Already applied effects are retained and will not be replayed; each new write requires exact current approval and fresh authority.";
        if (blocked)
        {
            var decisions = workflow.Decisions.ToList();
            foreach (var effect in views.Where(v => v.Classification is "PartiallyApplied" or "Unresolved" ||
                v.Stage == nameof(RepositoryEffectStage.Blocked)))
                decisions.Add(new(effect.EffectId.ToString("D"), "EffectReconciliation", effect.Explanation, effect.RevisionId, false,
                    ["Wait and inspect durable evidence", "Discuss a deliberate reconciliation"], [], [effect.LogicalStepId]));
            if (scopeBlocked && decisions.All(d => d.Kind != "EffectReconciliation"))
                decisions.Add(new("repository-scope-reconciliation", "EffectReconciliation", "This checkout has a parked physical effect. Inspect its owning workflow before another write.",
                    workflow.ActiveRevisionId, false, ["Wait and inspect scope ownership"], [], []));
            workflow = workflow with
            {
                State = workflow.State == DurableSupervisorState.Cancelled ? workflow.State : DurableSupervisorState.Blocked,
                Decisions = decisions.ToImmutableArray(),
                Blockers = workflow.Blockers.Add(explanation)
            };
        }
        return new(workflow, observed, views, blocked, explanation);
    }

    public async Task<RepositoryEffectRevisionDetail> DrillDownAsync(Guid workflowId, string toRevisionId, string actor,
        CancellationToken cancellationToken = default)
    {
        var transition = await supervisor.DrillDownAsync(workflowId, toRevisionId, actor, cancellationToken);
        var catchUp = await ReadAsync(workflowId, actor, cancellationToken);
        return new(transition, catchUp.Effects.Where(e => e.RevisionId == toRevisionId || e.RevisionId == transition.FromRevisionId).ToImmutableArray(),
            "Logical result fencing does not undo files. Earlier applied effects and validation failures remain historical physical facts; the corrective effect binds the observed prior post-state as its new base.");
    }

    private static RepositoryEffectView Project(RepositoryEffectRecord record, RepositoryStateSnapshot observed,
        IReadOnlyList<RepositoryEffectRecord> records, string activeRevision)
    {
        var classification = RepositoryEffectReconciler.Classify(record.Intent, observed);
        var physical = record.ObservedState?.StateDigest == record.Intent.ExpectedPostState.StateDigest &&
            record.Classification == RepositoryEffectClassification.DefinitelyApplied;
        var isCurrent = record.Intent.RevisionId == activeRevision;
        var linked = physical && HasObservedSuccessor(record.Intent.ExpectedPostState.StateDigest, observed.StateDigest, records);
        var classificationLabel = !isCurrent && linked ? "AppliedThenCorrected" : classification.ToString();
        var eligible = isCurrent && classification == RepositoryEffectClassification.DefinitelyNotApplied &&
            record.Stage is RepositoryEffectStage.Prepared or RepositoryEffectStage.IntentCommitted or RepositoryEffectStage.Applying;
        var explanation = classificationLabel switch
        {
            "DefinitelyNotApplied" => "The full observed checkout matches the immutable base. Replay is eligible only after active-revision, exact approval and fresh authority checks.",
            "DefinitelyApplied" => "The observed checkout matches the intended post-state. The mutation must not be replayed; result recording and exact-state validation can resume.",
            "AppliedThenCorrected" => "This earlier physical mutation was observed and later corrected by an effect whose exact base follows its post-state. Its superseded result is historical, not nonexistent.",
            "PartiallyApplied" => "Some intended replacements are present and others retain their before state. Entire-patch replay and further mutation are blocked.",
            _ => "The checkout matches neither the exact base, intended post-state nor recognized partial state. Replay is forbidden; a deliberate operator reconciliation is required."
        };
        if (record.Validation is { } validation) explanation += validation.Passed ? " Validation passed against the recorded exact state." : " Validation failed against the recorded exact state; correction requires a new effect and approved revision.";
        if (record.Stage == RepositoryEffectStage.Blocked)
            explanation += " This durable effect is parked; the latest file observation alone cannot authorize another write or clear its block.";
        return new(record.Intent.EffectId, record.Intent.RevisionId, record.Intent.LogicalStepId, record.Intent.BaseState.StateDigest,
            record.Intent.PatchIdentity, record.Intent.ExpectedPostState.StateDigest, record.ObservedState?.StateDigest, classificationLabel,
            record.Stage.ToString(), record.Validation?.Passed, isCurrent, false, eligible, physical || classification == RepositoryEffectClassification.DefinitelyApplied, explanation);
    }

    private static bool HasObservedSuccessor(string start, string finish, IReadOnlyList<RepositoryEffectRecord> records)
    {
        if (start == finish) return true;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 64 && visited.Add(start); i++)
        {
            var next = records.Where(r => r.Intent.BaseState.StateDigest == start &&
                r.ObservedState?.StateDigest == r.Intent.ExpectedPostState.StateDigest && r.Classification == RepositoryEffectClassification.DefinitelyApplied).ToArray();
            if (next.Length != 1) return false;
            start = next[0].Intent.ExpectedPostState.StateDigest;
            if (start == finish) return true;
        }
        return false;
    }
}
