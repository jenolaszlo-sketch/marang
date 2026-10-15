using Marang.Hufu;
using Penghou.Hufu;

namespace Marang.Runs.Effects;

public enum RepositoryEffectExecutionCheckpointKind
{
    BeforeIntentCommit,
    AfterIntentCommit,
    BeforeApply,
    AfterPhysicalFile,
    AfterAllWrites,
    AfterAppliedObserved,
    BeforeValidation,
    AfterValidation
}

public sealed record RepositoryEffectExecutionCheckpoint(RepositoryEffectExecutionCheckpointKind Kind, int? FileIndex = null);

public sealed record RepositoryEffectExecutionResult(
    RepositoryEffectRecord Record,
    RepositoryEffectClassification Classification,
    bool MutationPerformed,
    string? ReasonCode);

/// <summary>
/// Coordinates immutable journal intent, reconciliation, current Hufu authority and bounded repository writes.
/// Callers supply only authenticated host context and deterministic validation; model/tool payloads carry no authority.
/// </summary>
public sealed class RepositoryEffectExecutor(
    ControlledRepository repository,
    SqliteRepositoryEffectJournal journal,
    RepositoryEffectAuthority authority, IRepositoryEffectExecutionGuard? executionGuard = null)
{
    public async Task<RepositoryEffectExecutionResult> ExecuteAsync(
        RepositoryEffectIntent intent,
        AuthenticatedAuthorityContext authenticatedContext,
        Func<RepositoryStateSnapshot, CancellationToken, ValueTask<RepositoryEffectValidation>>? validate = null,
        Func<RepositoryEffectExecutionCheckpoint, CancellationToken, ValueTask>? checkpoint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(authenticatedContext);
        await At(RepositoryEffectExecutionCheckpointKind.BeforeIntentCommit).ConfigureAwait(false);
        var record = await journal.PrepareAsync(intent, cancellationToken).ConfigureAwait(false);
        if (record.Stage == RepositoryEffectStage.Prepared)
        {
            var admission = await AuthorizeEveryTarget(intent, authenticatedContext, cancellationToken).ConfigureAwait(false);
            if (!admission.Allowed)
            {
                await using var deniedLease = await repository.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
                var deniedState = repository.ObserveUnderLock();
                var deniedClassification = RepositoryEffectReconciler.Classify(intent, deniedState);
                if (deniedClassification != RepositoryEffectClassification.DefinitelyNotApplied)
                    record = await Block(record, deniedState, deniedClassification, cancellationToken).ConfigureAwait(false);
                return new(record, deniedClassification, false, admission.ReasonCode);
            }
            record = await journal.TransitionAsync(intent.EffectId, RepositoryEffectStage.IntentCommitted,
                cancellationToken: cancellationToken, authorityEvidence: admission.EvidenceSet).ConfigureAwait(false);
        }
        await At(RepositoryEffectExecutionCheckpointKind.AfterIntentCommit).ConfigureAwait(false);

        await using var lease = await repository.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var observed = repository.ObserveUnderLock();
        var classification = RepositoryEffectReconciler.Classify(intent, observed);
        if (record.Stage is (RepositoryEffectStage.AppliedObserved or RepositoryEffectStage.ValidationPassed or RepositoryEffectStage.ValidationFailed) &&
            classification != RepositoryEffectClassification.DefinitelyApplied &&
            record.ObservedState?.StateDigest != observed.StateDigest)
        {
            var history = await journal.ListForRepositoryAsync(intent.RepositoryId, cancellationToken).ConfigureAwait(false);
            if (!await authority.IsCurrentRevisionAsync(intent, cancellationToken).ConfigureAwait(false) &&
                HasAppliedLineageToCurrent(record, history, observed.StateDigest))
                return new(record, classification, false, "repository-effect.historical-applied-then-corrected");
        }
        if (classification == RepositoryEffectClassification.DefinitelyApplied)
        {
            record = await RecordAppliedIfNeeded(record, observed, classification, cancellationToken).ConfigureAwait(false);
            await At(RepositoryEffectExecutionCheckpointKind.AfterAppliedObserved).ConfigureAwait(false);
            return await ValidateIfRequested(record, classification, validate, checkpoint, cancellationToken).ConfigureAwait(false);
        }
        if (classification is RepositoryEffectClassification.PartiallyApplied or RepositoryEffectClassification.Unresolved)
        {
            record = await Block(record, observed, classification, cancellationToken).ConfigureAwait(false);
            return new(record, classification, false, "repository-effect.reconciliation-required");
        }

        if (record.Stage is RepositoryEffectStage.AppliedObserved or RepositoryEffectStage.ValidationPassed or RepositoryEffectStage.ValidationFailed)
            return new(record, classification, false, "repository-effect.recorded-result-conflicts-with-base");
        if (record.Stage == RepositoryEffectStage.Blocked)
            return new(record, classification, false, "repository-effect.blocked");

        await At(RepositoryEffectExecutionCheckpointKind.BeforeApply).ConfigureAwait(false);
        var priorEffects = await journal.ListForRepositoryAsync(intent.RepositoryId, cancellationToken).ConfigureAwait(false);
        foreach (var prior in priorEffects.Where(item => item.Intent.EffectId != intent.EffectId))
        {
            if (prior.Stage == RepositoryEffectStage.Prepared) continue;
            var priorClassification = RepositoryEffectReconciler.Classify(prior.Intent, observed);
            if (prior.Stage is RepositoryEffectStage.IntentCommitted or RepositoryEffectStage.Applying)
            {
                if (priorClassification is RepositoryEffectClassification.PartiallyApplied or RepositoryEffectClassification.Unresolved ||
                    prior.Intent.WorkflowId != intent.WorkflowId && priorClassification == RepositoryEffectClassification.DefinitelyNotApplied)
                {
                    await Block(prior, observed, priorClassification, cancellationToken).ConfigureAwait(false);
                    record = await Block(record, observed, RepositoryEffectClassification.DefinitelyNotApplied, cancellationToken).ConfigureAwait(false);
                    return new(record, RepositoryEffectClassification.DefinitelyNotApplied, false, "repository-effect.prior-effect-reconciliation-required");
                }
                if (priorClassification == RepositoryEffectClassification.DefinitelyApplied &&
                    prior.Stage is (RepositoryEffectStage.IntentCommitted or RepositoryEffectStage.Applying))
                    await RecordAppliedIfNeeded(prior, observed, priorClassification, cancellationToken).ConfigureAwait(false);
                else if (priorClassification == RepositoryEffectClassification.DefinitelyNotApplied)
                    await Block(prior, observed, priorClassification, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (prior.Stage == RepositoryEffectStage.Blocked &&
                prior.Classification is (RepositoryEffectClassification.PartiallyApplied or RepositoryEffectClassification.Unresolved))
            {
                record = await Block(record, observed, RepositoryEffectClassification.DefinitelyNotApplied, cancellationToken).ConfigureAwait(false);
                return new(record, RepositoryEffectClassification.DefinitelyNotApplied, false, "repository-effect.prior-effect-reconciliation-required");
            }
            if (prior.Stage is (RepositoryEffectStage.AppliedObserved or RepositoryEffectStage.ValidationPassed or RepositoryEffectStage.ValidationFailed) &&
                priorClassification != RepositoryEffectClassification.DefinitelyApplied &&
                !HasAppliedLineageToCurrent(prior, priorEffects, observed.StateDigest))
            {
                record = await Block(record, observed, RepositoryEffectClassification.DefinitelyNotApplied, cancellationToken).ConfigureAwait(false);
                return new(record, RepositoryEffectClassification.DefinitelyNotApplied, false, "repository-effect.prior-lineage-does-not-reconcile");
            }
        }
        // Active revision and exact per-file Hufu grants are checked after reconciliation and immediately before the writer.
        var fresh = await AuthorizeEveryTarget(intent, authenticatedContext, cancellationToken).ConfigureAwait(false);
        if (!fresh.Allowed) return new(record, classification, false, fresh.ReasonCode);
        observed = repository.ObserveUnderLock();
        classification = RepositoryEffectReconciler.Classify(intent, observed);
        if (classification != RepositoryEffectClassification.DefinitelyNotApplied)
        {
            record = await Block(record, observed, classification, cancellationToken).ConfigureAwait(false);
            return new(record, classification, false, "repository-effect.state-changed-before-write");
        }
        if (executionGuard is not null)
        {
            var refusal = await executionGuard.CheckBeforeWriteAsync(intent, observed, authenticatedContext, cancellationToken).ConfigureAwait(false);
            if (refusal is not null) return new(record, classification, false, refusal);
        }
        if (!priorEffects.Any(item => item.Intent.EffectId != intent.EffectId &&
            item.Stage is (RepositoryEffectStage.AppliedObserved or RepositoryEffectStage.ValidationPassed or RepositoryEffectStage.ValidationFailed)))
        {
            try { repository.RequireCleanInitialUnderLock(); }
            catch (InvalidOperationException)
            {
                record = await Block(record, observed, RepositoryEffectClassification.Unresolved, cancellationToken).ConfigureAwait(false);
                return new(record, RepositoryEffectClassification.Unresolved, false, "repository-effect.initial-state-not-clean");
            }
        }
        if (record.Stage == RepositoryEffectStage.IntentCommitted)
            record = await journal.TransitionAsync(intent.EffectId, RepositoryEffectStage.Applying, classification, observed,
                cancellationToken: cancellationToken, authorityEvidence: fresh.EvidenceSet).ConfigureAwait(false);
        else if (record.Stage == RepositoryEffectStage.Applying && fresh.EvidenceSet is { Count: > 0 })
            record = await journal.TransitionAsync(intent.EffectId, RepositoryEffectStage.Applying, classification, observed,
                cancellationToken: cancellationToken, authorityEvidence: fresh.EvidenceSet).ConfigureAwait(false);
        var applied = false;
        await repository.ApplyUnderLockAsync(intent,
            async (durableIntent, ct) =>
            {
                var current = await journal.GetAsync(durableIntent.EffectId, ct).ConfigureAwait(false);
                return current is { Stage: RepositoryEffectStage.Applying } && current.Intent.IntentDigest == durableIntent.IntentDigest;
            },
            async (index, ct) => await At(RepositoryEffectExecutionCheckpointKind.AfterPhysicalFile, index, ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        applied = true;
        await At(RepositoryEffectExecutionCheckpointKind.AfterAllWrites).ConfigureAwait(false);
        observed = repository.ObserveUnderLock();
        classification = RepositoryEffectReconciler.Classify(intent, observed);
        if (classification != RepositoryEffectClassification.DefinitelyApplied)
        {
            record = await Block(record, observed, classification, cancellationToken).ConfigureAwait(false);
            return new(record, classification, applied, "repository-effect.post-write-reconciliation-required");
        }
        record = await journal.TransitionAsync(intent.EffectId, RepositoryEffectStage.AppliedObserved,
            classification, observed, cancellationToken: cancellationToken).ConfigureAwait(false);
        await At(RepositoryEffectExecutionCheckpointKind.AfterAppliedObserved).ConfigureAwait(false);
        return await ValidateIfRequested(record, classification, validate, checkpoint, cancellationToken, mutationPerformed: true).ConfigureAwait(false);

        async ValueTask At(RepositoryEffectExecutionCheckpointKind kind, int? index = null, CancellationToken? token = null)
        {
            if (checkpoint is not null) await checkpoint(new(kind, index), token ?? cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<RepositoryEffectExecutionResult> ValidateIfRequested(RepositoryEffectRecord record,
        RepositoryEffectClassification classification,
        Func<RepositoryStateSnapshot, CancellationToken, ValueTask<RepositoryEffectValidation>>? validate,
        Func<RepositoryEffectExecutionCheckpoint, CancellationToken, ValueTask>? checkpoint,
        CancellationToken ct, bool mutationPerformed = false)
    {
        if (record.Stage == RepositoryEffectStage.Blocked)
            return new(record, classification, mutationPerformed, "repository-effect.blocked");
        if (record.Stage is (RepositoryEffectStage.ValidationPassed or RepositoryEffectStage.ValidationFailed) &&
            record.Validation is { } priorValidation && priorValidation.TargetStateDigest == record.ObservedState?.StateDigest)
            return new(record, classification, mutationPerformed, null);
        if (validate is null) return new(record, classification, mutationPerformed, null);
        // The caller holds the repository lock through both observations and validation.
        var before = repository.ObserveUnderLock();
        if (before.StateDigest != record.ObservedState?.StateDigest)
        {
            record = await Block(record, before, RepositoryEffectClassification.Unresolved, ct).ConfigureAwait(false);
            return new(record, RepositoryEffectClassification.Unresolved, mutationPerformed, "repository-effect.validation-state-stale");
        }
        if (checkpoint is not null) await checkpoint(new(RepositoryEffectExecutionCheckpointKind.BeforeValidation), ct).ConfigureAwait(false);
        var validation = await validate(before, ct).ConfigureAwait(false);
        var after = repository.ObserveUnderLock();
        if (after.StateDigest != before.StateDigest || validation.TargetStateDigest != before.StateDigest)
        {
            record = await Block(record, after, RepositoryEffectClassification.Unresolved, ct).ConfigureAwait(false);
            return new(record, RepositoryEffectClassification.Unresolved, mutationPerformed, "repository-effect.validation-state-changed");
        }
        record = await journal.TransitionAsync(record.Intent.EffectId,
            validation.Passed ? RepositoryEffectStage.ValidationPassed : RepositoryEffectStage.ValidationFailed,
            RepositoryEffectClassification.DefinitelyApplied, after, validation, ct).ConfigureAwait(false);
        if (checkpoint is not null) await checkpoint(new(RepositoryEffectExecutionCheckpointKind.AfterValidation), ct).ConfigureAwait(false);
        return new(record, RepositoryEffectClassification.DefinitelyApplied, mutationPerformed, null);
    }

    private async ValueTask<RepositoryEffectAuthorityDecision> AuthorizeEveryTarget(RepositoryEffectIntent intent,
        AuthenticatedAuthorityContext context, CancellationToken ct)
    {
        var evidence = new List<RepositoryEffectAuthorizationEvidence>();
        foreach (var file in intent.Files)
        {
            var decision = await authority.AuthorizeAsync(intent, context, file.Path, requireCurrentRevision: true,
                cancellationToken: ct).ConfigureAwait(false);
            if (!decision.Allowed) return decision;
            if (decision.Evidence is not null) evidence.Add(decision.Evidence);
        }
        return new(true, "repository-effect.permitted", intent.IntentDigest, intent.Files[0].Path,
            intent.IntentDigest, null, null, EvidenceSet: evidence);
    }

    private async Task<RepositoryEffectRecord> RecordAppliedIfNeeded(RepositoryEffectRecord record,
        RepositoryStateSnapshot state, RepositoryEffectClassification classification, CancellationToken ct)
    {
        if (record.Stage == RepositoryEffectStage.AppliedObserved) return record with { Classification = classification, ObservedState = state };
        if (record.Stage is RepositoryEffectStage.IntentCommitted or RepositoryEffectStage.Applying)
            return await journal.TransitionAsync(record.Intent.EffectId, RepositoryEffectStage.AppliedObserved,
                classification, state, cancellationToken: ct).ConfigureAwait(false);
        if (record.Stage == RepositoryEffectStage.Blocked &&
            (record.Classification != classification || record.ObservedState?.StateDigest != state.StateDigest))
            return await journal.TransitionAsync(record.Intent.EffectId, RepositoryEffectStage.Blocked,
                classification, state, cancellationToken: ct).ConfigureAwait(false);
        return record;
    }

    private async Task<RepositoryEffectRecord> Block(RepositoryEffectRecord record, RepositoryStateSnapshot observed,
        RepositoryEffectClassification classification, CancellationToken ct)
    {
        if (record.Stage == RepositoryEffectStage.Blocked && record.Classification == classification &&
            record.ObservedState?.StateDigest == observed.StateDigest) return record;
        return await journal.TransitionAsync(record.Intent.EffectId, RepositoryEffectStage.Blocked,
            classification, observed, cancellationToken: ct).ConfigureAwait(false);
    }

    private static bool HasAppliedLineageToCurrent(RepositoryEffectRecord origin,
        IReadOnlyList<RepositoryEffectRecord> records, string observedDigest)
    {
        var digest = origin.ObservedState?.StateDigest ?? origin.Intent.ExpectedPostState.StateDigest;
        if (digest == observedDigest) return true;
        var visited = new HashSet<string>(StringComparer.Ordinal) { digest };
        for (var depth = 0; depth < 64; depth++)
        {
            var successors = records.Where(record => record.Intent.BaseState.StateDigest == digest &&
                record.Intent.RepositoryId == origin.Intent.RepositoryId &&
                record.Stage is (RepositoryEffectStage.AppliedObserved or RepositoryEffectStage.ValidationPassed or RepositoryEffectStage.ValidationFailed) &&
                record.ObservedState?.StateDigest == record.Intent.ExpectedPostState.StateDigest)
                .OrderBy(record => record.Intent.CreatedAt).ToArray();
            if (successors.Length != 1) return false;
            digest = successors[0].Intent.ExpectedPostState.StateDigest;
            if (digest == observedDigest) return true;
            if (!visited.Add(digest)) return false;
        }
        return false;
    }
}
