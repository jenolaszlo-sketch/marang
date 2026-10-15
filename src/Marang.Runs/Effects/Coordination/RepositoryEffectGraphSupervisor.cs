using System.Collections.Immutable;
using Marang.Runs.Adaptive;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Runs.Effects;

public sealed record RepositoryGraphEffectView(
    Guid EffectId, string LogicalId, string IntentRevisionId, string CausalDigest, string IntentDigest,
    string AuthorityScope, string PatchIdentity, string BaseStateDigest, string ExpectedPostStateDigest, string? RecordedOldStateDigest,
    string ActualCheckoutStateDigest, string PhysicalClassification, RepositoryEffectStage? PhysicalStage,
    bool SelectedInInspectedRevision, bool SelectedInActiveRevision, bool AcceptedByActiveRevision, bool Reuse,
    RepositoryEffectValidation? LocalValidation, RepositoryEffectValidation? WorkflowValidation,
    ImmutableArray<RepositoryEffectAuthorizationEvidence> AuthorityEvidence, string RequirementEvidence, string SelectionRationale,
    ImmutableArray<Guid> AcceptedPrerequisites, ImmutableArray<Guid> BlockingPrerequisites,
    Guid? ReplacementEffectId, ImmutableArray<RepositoryEffectDispositionKind> Dispositions,
    string Reason, string Explanation);

public sealed record RepositoryEffectGraphCatchUp(
    SupervisorCatchUpView Workflow, string InspectedRevisionId, RepositoryStateSnapshot ActualCheckoutState,
    ImmutableArray<RepositoryGraphEffectView> Effects, bool MutationBlocked, ImmutableArray<string> Blockers,
    string Explanation);

public sealed record RepositoryEffectGraphRevisionDetail(
    RevisionTransitionDetail Workflow, RepositoryEffectGraphCatchUp Graph,
    string PhysicalRepositoryExplanation);

/// <summary>Authenticated, read-only projection of immutable revision graphs and physical effect evidence.</summary>
public sealed class RepositoryEffectGraphSupervisor(
    SqliteAdaptiveRevisionRepository revisions,
    SqliteRepositoryEffectGraphJournal graphJournal,
    SqliteRepositoryEffectJournal effectJournal,
    ControlledRepository repository,
    SupervisorCatchUp supervisor)
{
    public async Task<RepositoryEffectGraphCatchUp> ReadAsync(Guid workflowId, string actor, CancellationToken cancellationToken = default)
    {
        var workflow = await supervisor.ReadAsync(workflowId, actor, cancellationToken).ConfigureAwait(false);
        var revision = await revisions.GetActiveAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (revision.RevisionId != workflow.ActiveRevisionId)
            throw new WorkflowStateException("Active revision changed during effect graph projection; retry the read.");
        var graph = AdaptivePlanAuthoring.ReadEffectGraph(revision);
        await using var lease = await repository.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var actual = repository.ObserveUnderLock();
        // Re-authenticate and re-read the active archive while holding the checkout lease so a concurrent
        // revision transition cannot cause stale graph acceptance to be presented as current.
        workflow = await supervisor.ReadAsync(workflowId, actor, cancellationToken).ConfigureAwait(false);
        revision = await revisions.GetActiveAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (revision.RevisionId != workflow.ActiveRevisionId)
            throw new WorkflowStateException("Active revision changed during effect graph projection; retry the read.");
        graph = AdaptivePlanAuthoring.ReadEffectGraph(revision);
        if (graph is null)
            return EmptyProjection(workflow, revision.RevisionId, actual,
                "The active immutable revision archive contains no repository effect graph.");
        var allGraphs = await GetCommittedGraphsAsync(workflowId, cancellationToken).ConfigureAwait(false);
        var records = await effectJournal.ListForWorkflowAsync(workflowId, cancellationToken).ConfigureAwait(false);
        return await ProjectAsync(workflow, graph, graph.RevisionId, actual, allGraphs, records, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RepositoryEffectGraphRevisionDetail> DrillDownAsync(Guid workflowId, string toRevisionId, string actor,
        CancellationToken cancellationToken = default)
    {
        var transition = await supervisor.DrillDownAsync(workflowId, toRevisionId, actor, cancellationToken).ConfigureAwait(false);
        var workflow = await supervisor.ReadAsync(workflowId, actor, cancellationToken).ConfigureAwait(false);
        var revision = (await revisions.GetHistoryAsync(workflowId, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(r => r.RevisionId == toRevisionId)
            ?? throw new WorkflowStateException("Revision does not belong to this workflow.");
        var graph = AdaptivePlanAuthoring.ReadEffectGraph(revision);
        await using var lease = await repository.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var actual = repository.ObserveUnderLock();
        workflow = await supervisor.ReadAsync(workflowId, actor, cancellationToken).ConfigureAwait(false);
        var active = await revisions.GetActiveAsync(workflowId, cancellationToken).ConfigureAwait(false);
        var activeGraph = AdaptivePlanAuthoring.ReadEffectGraph(active);
        var graphs = await GetCommittedGraphsAsync(workflowId, cancellationToken).ConfigureAwait(false);
        var records = await effectJournal.ListForWorkflowAsync(workflowId, cancellationToken).ConfigureAwait(false);
        if (graph is null)
            return new(transition, EmptyProjection(workflow, toRevisionId, actual,
                "The inspected immutable revision archive contains no repository effect graph."),
                "No graph was archived for this revision. Physical checkout state remains an independent observation.");
        var projection = await ProjectAsync(workflow, graph, toRevisionId, actual, graphs, records, cancellationToken,
            activeGraph?.RevisionId, activeGraph);
        return new(transition, projection,
            "The inspected revision is historical unless it is the active revision. Physical checkout state is observed independently; graph acceptance never claims that files were rolled back.");
    }

    public async Task<RepositoryGraphEffectView> ExplainEffectAsync(Guid workflowId, Guid effectId, string actor,
        CancellationToken cancellationToken = default)
    {
        if (effectId == Guid.Empty) throw new ArgumentException("Effect id is required.", nameof(effectId));
        var workflow = await supervisor.ReadAsync(workflowId, actor, cancellationToken).ConfigureAwait(false);
        var graphs = await GetCommittedGraphsAsync(workflowId, cancellationToken).ConfigureAwait(false);
        var owner = graphs.SelectMany(g => g.Effects.Select(e => (Graph: g, Selection: e)))
            .FirstOrDefault(x => x.Selection.Effect.Intent.EffectId == effectId);
        if (owner.Selection is null) throw new KeyNotFoundException("Effect is not present in this workflow's archived graphs.");
        var active = await revisions.GetActiveAsync(workflowId, cancellationToken).ConfigureAwait(false);
        var activeGraph = AdaptivePlanAuthoring.ReadEffectGraph(active);
        await using var lease = await repository.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var actual = repository.ObserveUnderLock();
        workflow = await supervisor.ReadAsync(workflowId, actor, cancellationToken).ConfigureAwait(false);
        active = await revisions.GetActiveAsync(workflowId, cancellationToken).ConfigureAwait(false);
        activeGraph = AdaptivePlanAuthoring.ReadEffectGraph(active);
        graphs = await GetCommittedGraphsAsync(workflowId, cancellationToken).ConfigureAwait(false);
        var records = await effectJournal.ListForWorkflowAsync(workflowId, cancellationToken).ConfigureAwait(false);
        var views = await ProjectAsync(workflow, owner.Graph, owner.Graph.RevisionId, actual, graphs, records, cancellationToken,
            activeGraph?.RevisionId, activeGraph);
        return views.Effects.Single(e => e.EffectId == effectId);
    }

    private async Task<RepositoryEffectGraphCatchUp> ProjectAsync(SupervisorCatchUpView workflow, RepositoryEffectGraph inspected,
        string inspectedRevision, RepositoryStateSnapshot actual, IReadOnlyList<RepositoryEffectGraph> graphs,
        IReadOnlyList<RepositoryEffectRecord> records, CancellationToken ct, string? activeRevision = null,
        RepositoryEffectGraph? activeGraph = null)
    {
        if (!inspected.Verify() || inspected.WorkflowId != workflow.WorkflowId || inspected.RepositoryId != repository.RepositoryId)
            throw new InvalidDataException("Archived effect graph does not bind a verified graph to this workflow and checkout.");
        activeRevision ??= workflow.ActiveRevisionId;
        activeGraph ??= inspected.RevisionId == activeRevision ? inspected : null;
        var activeSelected = activeGraph?.Effects.ToDictionary(e => e.Effect.Intent.EffectId) ?? [];
        var inspectedSelected = inspected.Effects.ToDictionary(e => e.Effect.Intent.EffectId);
        var scopeRecords = await effectJournal.ListForRepositoryAsync(repository.RepositoryId, ct).ConfigureAwait(false);
        var activeAccepted = activeGraph is null ? new HashSet<Guid>() : await ComputeAcceptedEffectsAsync(activeGraph, records, actual, ct).ConfigureAwait(false);
        var allSelections = graphs.Append(inspected).Concat(activeGraph is null ? [] : [activeGraph])
            .SelectMany(g => g.Effects.Select(e => (Graph: g, Selection: e)))
            .DistinctBy(x => x.Selection.Effect.Intent.EffectId).ToArray();
        var dispositions = graphs.SelectMany(g => g.PriorEffects).GroupBy(d => d.EffectId)
            .ToDictionary(g => g.Key, g => g.Last());
        var blockerNames = new List<string>();
        var views = ImmutableArray.CreateBuilder<RepositoryGraphEffectView>();
        foreach (var archived in allSelections)
        {
            ct.ThrowIfCancellationRequested();
            var selection = archived.Selection;
            var selectionGraph = archived.Graph;
            var causal = selection.Effect; var intent = causal.Intent;
            var record = await effectJournal.GetAsync(intent.EffectId, ct).ConfigureAwait(false);
            var physical = RepositoryEffectReconciler.Classify(intent, actual);
            var physicalLabel = physical.ToString();
            var disposition = dispositions.GetValueOrDefault(intent.EffectId);
            var exactAppliedRecord = record is { } physicalRecord &&
                physicalRecord.ObservedState?.StateDigest == intent.ExpectedPostState.StateDigest &&
                physicalRecord.Classification == RepositoryEffectClassification.DefinitelyApplied &&
                physicalRecord.Stage is RepositoryEffectStage.AppliedObserved or RepositoryEffectStage.ValidationPassed or RepositoryEffectStage.ValidationFailed;
            if (exactAppliedRecord && actual.StateDigest != intent.ExpectedPostState.StateDigest)
                physicalLabel = HasAppliedPath(intent.ExpectedPostState.StateDigest, actual.StateDigest, records)
                    ? "AppliedThenAdvanced" : "AppliedHistorical";
            var blocked = ImmutableArray.CreateBuilder<Guid>();
            var acceptedPrerequisites = ImmutableArray.CreateBuilder<Guid>();
            var selectedActive = activeSelected.TryGetValue(intent.EffectId, out var activeSelection) &&
                activeSelection.Effect.CausalDigest == causal.CausalDigest;
            foreach (var prerequisite in causal.Prerequisites)
            {
                var depSelection = activeSelected.GetValueOrDefault(prerequisite.EffectId);
                var depRecord = await effectJournal.GetAsync(prerequisite.EffectId, ct).ConfigureAwait(false);
                if (activeAccepted.Contains(prerequisite.EffectId) && depSelection is not null && depSelection.Effect.CausalDigest == prerequisite.CausalDigest)
                    acceptedPrerequisites.Add(prerequisite.EffectId);
                if (depSelection is null || depSelection.Effect.CausalDigest != prerequisite.CausalDigest ||
                    depRecord?.Intent.IntentDigest != depSelection?.Effect.Intent.IntentDigest ||
                    !RepositoryEffectGraphRuntime.LocallyValidated(depRecord) ||
                    depRecord?.ObservedState?.StateDigest != prerequisite.ResultStateDigest)
                {
                    blocked.Add(prerequisite.EffectId);
                    var logical = depSelection?.LogicalId ?? graphs.SelectMany(g => g.Effects).FirstOrDefault(e => e.Effect.Intent.EffectId == prerequisite.EffectId)?.LogicalId ?? prerequisite.EffectId.ToString("D");
                    if (selectedActive)
                        blockerNames.Add($"{selection.LogicalId} waits for prerequisite {logical} ({prerequisite.EffectId:D}) to produce a physically recorded, locally validated result matching its causal digest.");
                }
            }
            var workflowValidation = await graphJournal.GetWorkflowValidationAsync(workflow.WorkflowId, selectionGraph.RevisionId, ct).ConfigureAwait(false);
            var accepted = activeAccepted.Contains(intent.EffectId) && selectedActive;
            var displayedReuse = selectedActive ? activeSelection!.Reuse : selection.Reuse;
            var displayedRationale = selectedActive ? activeSelection!.Rationale : selection.Rationale;
            var localValidation = record?.Validation;
            var physicalStage = record?.Stage;
            var reason = disposition?.Reason ?? (accepted ? "Selected and locally validated by the active immutable revision graph." :
                selectedActive ? "Selected in the active graph; physical application, local validation, or an accepted prerequisite is still pending." :
                "Not selected by the active immutable revision graph.");
            var explanation = Describe(selectedActive, accepted, displayedReuse, physicalLabel, physicalStage, localValidation, workflowValidation, blocked.Count > 0);
            views.Add(new(intent.EffectId, selection.LogicalId, intent.RevisionId, causal.CausalDigest, intent.IntentDigest,
                intent.AuthorityScope, intent.PatchIdentity, intent.BaseState.StateDigest, intent.ExpectedPostState.StateDigest,
                record?.ObservedState?.StateDigest, actual.StateDigest, physicalLabel, physicalStage,
                inspectedSelected.ContainsKey(intent.EffectId), selectedActive, accepted, displayedReuse, localValidation, workflowValidation,
                record?.AuthorityEvidence.ToImmutableArray() ?? [], causal.RequirementEvidence, displayedRationale,
                acceptedPrerequisites.ToImmutable(), blocked.ToImmutable(), disposition?.ReplacementEffectId,
                disposition?.Kinds ?? ImmutableArray<RepositoryEffectDispositionKind>.Empty, reason, explanation));
        }
        foreach (var prior in inspected.PriorEffects)
        {
            if (views.Any(v => v.EffectId == prior.EffectId)) continue;
            var record = await effectJournal.GetAsync(prior.EffectId, ct).ConfigureAwait(false);
            var selectedActive = activeSelected.ContainsKey(prior.EffectId);
            var accepted = activeAccepted.Contains(prior.EffectId);
            var physical = record is { } foundRecord ? RepositoryEffectReconciler.Classify(foundRecord.Intent, actual) : RepositoryEffectClassification.Unresolved;
            var physicalLabel = record is { } priorRecord && priorRecord.ObservedState?.StateDigest == priorRecord.Intent.ExpectedPostState.StateDigest &&
                priorRecord.Classification == RepositoryEffectClassification.DefinitelyApplied
                ? actual.StateDigest == priorRecord.Intent.ExpectedPostState.StateDigest ? nameof(RepositoryEffectClassification.DefinitelyApplied) :
                    HasAppliedPath(priorRecord.Intent.ExpectedPostState.StateDigest, actual.StateDigest, records) ? "AppliedThenAdvanced" : "AppliedHistorical"
                : physical.ToString();
            views.Add(new(prior.EffectId, prior.LogicalId, record?.Intent.RevisionId ?? inspectedRevision,
                "", record?.Intent.IntentDigest ?? "", record?.Intent.AuthorityScope ?? "", record?.Intent.PatchIdentity ?? "", record?.Intent.BaseState.StateDigest ?? "",
                record?.Intent.ExpectedPostState.StateDigest ?? "", record?.ObservedState?.StateDigest, actual.StateDigest,
                physicalLabel, record?.Stage, false, selectedActive, accepted, false, record?.Validation, null,
                record?.AuthorityEvidence.ToImmutableArray() ?? [], "", "", [], [],
                prior.ReplacementEffectId, prior.Kinds, prior.Reason,
                ExplainDisposition(prior.Kinds, physical)));
        }
        var blockedPhysical = views.Any(v => v.BlockingPrerequisites.IsEmpty &&
            v.PhysicalClassification is nameof(RepositoryEffectClassification.PartiallyApplied) or nameof(RepositoryEffectClassification.Unresolved));
        if (blockedPhysical) blockerNames.Add("The checkout does not match an exact effect base or result. Reconciliation is required before any effect can proceed.");
        if (views.Any(v => v.SelectedInActiveRevision && v.LocalValidation is { Passed: false }))
            blockerNames.Add("An active effect failed local validation against its recorded state; review a corrective revision.");
        var parkedScopeEffect = scopeRecords.FirstOrDefault(r => r.Stage == RepositoryEffectStage.Blocked &&
            r.Classification is RepositoryEffectClassification.PartiallyApplied or RepositoryEffectClassification.Unresolved);
        if (parkedScopeEffect is not null)
            blockerNames.Add($"Checkout scope is parked by effect {parkedScopeEffect.Intent.EffectId:D}; inspect its owning workflow. A later file observation does not clear a durable reconciliation block.");
        var currentWorkflowValidation = activeGraph is null ? null : await graphJournal.GetWorkflowValidationAsync(workflow.WorkflowId, activeGraph.RevisionId, ct).ConfigureAwait(false);
        if (currentWorkflowValidation is { Passed: false })
            blockerNames.Add("The active graph's workflow validation failed; the graph's individually validated effects remain historical evidence, and final workflow acceptance is blocked.");
        var uniqueBlockers = blockerNames.Distinct(StringComparer.Ordinal).ToImmutableArray();
        return new(workflow, inspectedRevision, actual, views.ToImmutable(), blockedPhysical || uniqueBlockers.Length > 0,
            uniqueBlockers, uniqueBlockers.Length > 0
                ? "Accepted graph state, local effect evidence, workflow validation, and the currently observed checkout are shown as separate facts. Pending prerequisites block downstream interpretation and never grant replay permission."
                : "The projection binds acceptance to the active revision archive and reports physical, local validation, and workflow validation evidence independently.");
    }

    private static RepositoryEffectGraphCatchUp EmptyProjection(SupervisorCatchUpView workflow, string revisionId,
        RepositoryStateSnapshot actual, string explanation) =>
        new(workflow, revisionId, actual, [], false, [], explanation);

    private async Task<IReadOnlyList<RepositoryEffectGraph>> GetCommittedGraphsAsync(Guid workflowId, CancellationToken ct)
    {
        var history = await revisions.GetHistoryAsync(workflowId, ct).ConfigureAwait(false);
        var journalGraphs = (await graphJournal.ListGraphsAsync(workflowId, ct).ConfigureAwait(false))
            .ToDictionary(graph => graph.RevisionId, graph => graph, StringComparer.Ordinal);
        return history.Select(revision => AdaptivePlanAuthoring.ReadEffectGraph(revision))
            .Where(graph => graph is not null && graph.WorkflowId == workflowId && graph.Verify() &&
                journalGraphs.TryGetValue(graph.RevisionId, out var archived) && archived.GraphDigest == graph.GraphDigest)
            .Select(graph => graph!)
            .ToArray();
    }

    private static string Describe(bool selected, bool accepted, bool reuse, string physical, RepositoryEffectStage? stage,
        RepositoryEffectValidation? local, RepositoryEffectValidation? workflow, bool blocked)
    {
        var status = accepted ? "accepted by the active graph" : selected && local is { Passed: false }
            ? "selected by the active graph but blocked by failed local validation"
            : selected ? "selected by the active graph; acceptance is pending" : "historical or not selected by the active graph";
        var physicalText = stage is null ? "no physical journal record" : $"physical state is {physical} at stage {stage}";
        var reuseText = reuse ? " The graph explicitly reuses the prior causal effect." : "";
        var localText = local is null ? " Local validation has not been recorded." : $" Local validation {(local.Passed ? "passed" : "failed")} for state {local.TargetStateDigest}.";
        var workflowText = workflow is null ? " Workflow validation has not been recorded." : $" Workflow validation {(workflow.Passed ? "passed" : "failed")} for state {workflow.TargetStateDigest}.";
        var blockingText = blocked ? " A prerequisite is unresolved; downstream work is blocked and this view grants no replay permission." : "";
        return $"Effect is {status}; {physicalText}.{reuseText}{localText}{workflowText}{blockingText}";
    }

    private static string ExplainDisposition(ImmutableArray<RepositoryEffectDispositionKind> kinds, RepositoryEffectClassification physical) =>
        $"Archived disposition: {string.Join(", ", kinds)}. Current checkout classification is {physical}; prior physical facts are retained independently of revision acceptance.";

    private static bool HasAppliedPath(string start, string finish, IReadOnlyList<RepositoryEffectRecord> records)
    {
        if (start == finish) return true;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 32 && visited.Add(start); i++)
        {
            var next = records.Where(r => r.Intent.BaseState.StateDigest == start &&
                r.ObservedState?.StateDigest == r.Intent.ExpectedPostState.StateDigest &&
                r.Classification == RepositoryEffectClassification.DefinitelyApplied &&
                r.Stage is RepositoryEffectStage.AppliedObserved or RepositoryEffectStage.ValidationPassed or RepositoryEffectStage.ValidationFailed).ToArray();
            if (next.Length != 1) return false;
            start = next[0].Intent.ExpectedPostState.StateDigest;
            if (start == finish) return true;
        }
        return false;
    }

    private static async Task<HashSet<Guid>> ComputeAcceptedEffectsAsync(RepositoryEffectGraph graph,
        IReadOnlyList<RepositoryEffectRecord> records, RepositoryStateSnapshot actual, CancellationToken ct)
    {
        var selections = graph.Effects.ToDictionary(e => e.Effect.Intent.EffectId);
        var recordsById = records.ToDictionary(r => r.Intent.EffectId);
        var accepted = new HashSet<Guid>(); var visiting = new HashSet<Guid>();
        bool IsAccepted(RepositoryEffectSelection selection)
        {
            var id = selection.Effect.Intent.EffectId;
            if (accepted.Contains(id)) return true;
            if (!visiting.Add(id)) return false;
            var effect = selection.Effect; var intent = effect.Intent;
            var complete = recordsById.TryGetValue(id, out var record) &&
                record.Intent.IntentDigest == intent.IntentDigest &&
                record.Stage == RepositoryEffectStage.ValidationPassed && record.Classification == RepositoryEffectClassification.DefinitelyApplied &&
                record.ObservedState?.StateDigest == intent.ExpectedPostState.StateDigest && record.Validation is { Passed: true } validation &&
                validation.TargetStateDigest == intent.ExpectedPostState.StateDigest &&
                RepositoryEffectGraphRuntime.OutputsPresent(intent, actual) &&
                (actual.StateDigest == intent.ExpectedPostState.StateDigest || HasAppliedPath(intent.ExpectedPostState.StateDigest, actual.StateDigest, records)) &&
                effect.Prerequisites.All(prerequisite =>
                    selections.TryGetValue(prerequisite.EffectId, out var dep) && dep.Effect.CausalDigest == prerequisite.CausalDigest && IsAccepted(dep));
            visiting.Remove(id);
            if (complete) accepted.Add(id);
            return complete;
        }
        foreach (var selection in graph.Effects)
        {
            ct.ThrowIfCancellationRequested();
            _ = IsAccepted(selection);
        }
        await Task.CompletedTask.ConfigureAwait(false);
        return accepted;
    }
}
