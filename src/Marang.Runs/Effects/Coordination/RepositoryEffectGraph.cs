using System.Collections.Immutable;
using System.Text;

namespace Marang.Runs.Effects;

public sealed record RepositoryEffectDependency(Guid EffectId, string CausalDigest, string ResultStateDigest);

public sealed record RepositoryCausalEffect(RepositoryEffectIntent Intent,
    ImmutableArray<RepositoryEffectDependency> Prerequisites, string RequirementEvidence, string CausalDigest)
{
    public static RepositoryCausalEffect Create(RepositoryEffectIntent intent,
        IEnumerable<RepositoryEffectDependency> prerequisites, string requirementEvidence)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var deps = prerequisites.OrderBy(d => d.EffectId).ToImmutableArray();
        ValidateText(requirementEvidence, nameof(requirementEvidence));
        if (!intent.HasValidDigest()) throw new ArgumentException("Effect intent digest is invalid.", nameof(intent));
        if (deps.Select(d => d.EffectId).Distinct().Count() != deps.Length) throw new ArgumentException("Prerequisite effect ids must be unique.", nameof(prerequisites));
        if (deps.Any(d => d.EffectId == Guid.Empty || !RepositoryStateSnapshot.IsSha256(d.CausalDigest) || !RepositoryStateSnapshot.IsSha256(d.ResultStateDigest)))
            throw new ArgumentException("Prerequisite ids and digests must be valid.", nameof(prerequisites));
        var digest = ComputeCausalDigest(intent.IntentDigest, deps, requirementEvidence);
        return new(intent, deps, requirementEvidence, digest);
    }

    public bool HasValidDigest()
    {
        try
        {
            return Intent is not null && Intent.HasValidDigest() && !Prerequisites.IsDefault &&
            Prerequisites.Select(d => d.EffectId).Distinct().Count() == Prerequisites.Length &&
            Prerequisites.All(d => d.EffectId != Guid.Empty && RepositoryStateSnapshot.IsSha256(d.CausalDigest) && RepositoryStateSnapshot.IsSha256(d.ResultStateDigest)) &&
            IsValidText(RequirementEvidence) && ComputeCausalDigest(Intent.IntentDigest, Prerequisites, RequirementEvidence) == CausalDigest;
        }
        catch { return false; }
    }

    internal static string ComputeCausalDigest(string intentDigest, ImmutableArray<RepositoryEffectDependency> deps, string evidence)
    {
        var b = new StringBuilder("marang-repository-causal-effect-v1\n").Append(intentDigest).Append('\n').Append(evidence).Append('\n');
        foreach (var d in deps.OrderBy(d => d.EffectId)) b.Append(d.EffectId.ToString("D")).Append('\0').Append(d.CausalDigest).Append('\0').Append(d.ResultStateDigest).Append('\n');
        return RepositoryStateSnapshot.Hash(b.ToString());
    }
    private static void ValidateText(string value, string name)
    { if (!IsValidText(value)) throw new ArgumentException("A canonical evidence value is required.", name); }
    private static bool IsValidText(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 4096 && !value.Any(char.IsControl) && value == value.Trim();
}

public sealed record RepositoryEffectSelection(string LogicalId, RepositoryCausalEffect Effect, bool Reuse, string Rationale, Guid? ReplacesEffectId = null);
public enum RepositoryEffectDispositionKind { Preserved, AcceptedAsBase, Superseded, Invalidated, RequiresReconciliation, RequiresReplacement }
public sealed record RepositoryEffectDisposition(Guid EffectId, string LogicalId,
    ImmutableArray<RepositoryEffectDispositionKind> Kinds, Guid? ReplacementEffectId, string Reason);

public sealed record RepositoryEffectGraph(Guid WorkflowId, string RevisionId, string? ParentRevisionId, string RepositoryId,
    RepositoryStateSnapshot PhysicalBaseState, ImmutableArray<RepositoryEffectSelection> Effects,
    ImmutableArray<RepositoryEffectDisposition> PriorEffects, string GraphDigest)
{
    public static RepositoryEffectGraph Create(Guid workflowId, string revisionId, string? parentRevisionId, string repositoryId,
        RepositoryStateSnapshot physicalBaseState, IEnumerable<RepositoryEffectSelection> effects,
        IEnumerable<RepositoryEffectDisposition>? priorEffects = null)
    {
        if (workflowId == Guid.Empty) throw new ArgumentException("Workflow id is required.", nameof(workflowId));
        RequireText(revisionId, nameof(revisionId)); RequireText(repositoryId, nameof(repositoryId));
        if (parentRevisionId is not null) RequireText(parentRevisionId, nameof(parentRevisionId));
        ArgumentNullException.ThrowIfNull(physicalBaseState);
        var selected = effects.ToImmutableArray(); var prior = (priorEffects ?? []).ToImmutableArray();
        var graph = new RepositoryEffectGraph(workflowId, revisionId, parentRevisionId, repositoryId, physicalBaseState, selected, prior, "");
        graph.ValidateContents();
        return graph with { GraphDigest = graph.ComputeDigest() };
    }

    public bool Verify()
    {
        try { ValidateContents(); return GraphDigest == ComputeDigest(); }
        catch { return false; }
    }

    private void ValidateContents()
    {
        if (WorkflowId == Guid.Empty || !ValidText(RevisionId) || !ValidText(RepositoryId) || ParentRevisionId is { } parent && !ValidText(parent) ||
            !PhysicalBaseState.HasValidDigest() || PhysicalBaseState.RepositoryId != RepositoryId || Effects.IsDefault || Effects.Length is < 1 or > 8 || PriorEffects.IsDefault)
            throw new InvalidOperationException("Graph envelope is incomplete or invalid.");
        if (Effects.Any(e => e is null || !ValidText(e.LogicalId) || !ValidText(e.Rationale) || !e.Effect.HasValidDigest()))
            throw new InvalidOperationException("Graph contains an invalid selection.");
        var ids = Effects.Select(e => e.Effect.Intent.EffectId).ToHashSet();
        if (ids.Count != Effects.Length || Effects.Select(e => e.LogicalId).Distinct(StringComparer.Ordinal).Count() != Effects.Length)
            throw new InvalidOperationException("Effect and logical ids must be unique within a graph.");
        var replacementIds = Effects.Where(e => e.ReplacesEffectId.HasValue).Select(e => e.ReplacesEffectId!.Value).ToArray();
        if (replacementIds.Any(id => id == Guid.Empty || ids.Contains(id)) || replacementIds.Distinct().Count() != replacementIds.Length ||
            Effects.Any(e => e.Reuse && e.ReplacesEffectId.HasValue))
            throw new InvalidOperationException("Replacement mappings must be unique, fresh, and point to a different prior effect identity.");
        foreach (var s in Effects)
        {
            var i = s.Effect.Intent;
            if (i.WorkflowId != WorkflowId || i.RepositoryId != RepositoryId || i.LogicalStepId != s.LogicalId)
                throw new InvalidOperationException("Selected intent identity does not match the graph.");
            if (!s.Reuse && i.RevisionId != RevisionId) throw new InvalidOperationException("Fresh intents must originate in this revision.");
            if (s.Reuse && i.RevisionId == RevisionId) throw new InvalidOperationException("Reuse must retain an earlier intent revision.");
            foreach (var d in s.Effect.Prerequisites)
            {
                if (d.EffectId == Guid.Empty || !RepositoryStateSnapshot.IsSha256(d.CausalDigest) || !RepositoryStateSnapshot.IsSha256(d.ResultStateDigest))
                    throw new InvalidOperationException("Prerequisite identity and digests must be complete.");
                var dep = Effects.SingleOrDefault(e => e.Effect.Intent.EffectId == d.EffectId) ?? throw new InvalidOperationException("Prerequisite is not selected in this graph.");
                if (dep.Effect.CausalDigest != d.CausalDigest || dep.Effect.Intent.ExpectedPostState.StateDigest != d.ResultStateDigest)
                    throw new InvalidOperationException("Prerequisite binding does not match its causal and result digests.");
            }
        }
        // Detect cycles from explicit edges only. Physical sequence is separately derived from state digests.
        var remaining = Effects.ToDictionary(e => e.Effect.Intent.EffectId);
        var ready = new HashSet<Guid>(remaining.Keys.Where(id => remaining[id].Effect.Prerequisites.Length == 0));
        var visited = 0;
        while (ready.Count > 0)
        {
            var id = ready.OrderBy(x => x).First(); ready.Remove(id); var s = remaining[id];
            remaining.Remove(id); visited++;
            foreach (var child in remaining.Values.Where(x => x.Effect.Prerequisites.Any(p => p.EffectId == id)))
                if (child.Effect.Prerequisites.All(p => !remaining.ContainsKey(p.EffectId))) ready.Add(child.Effect.Intent.EffectId);
        }
        if (visited != Effects.Length) throw new InvalidOperationException("Effect prerequisites contain a cycle or disconnected ordering.");
        var physical = Effects.Where(e => !e.Reuse).ToList(); var expectedBase = PhysicalBaseState.StateDigest;
        while (physical.Count != 0)
        {
            var matching = physical.Where(e => e.Effect.Intent.BaseState.StateDigest == expectedBase).ToArray();
            if (matching.Length != 1) throw new InvalidOperationException("Fresh effect physical states do not form one serial lineage from the checkout base.");
            expectedBase = matching[0].Effect.Intent.ExpectedPostState.StateDigest;
            physical.Remove(matching[0]);
        }
        if (PriorEffects.Select(d => d.EffectId).Distinct().Count() != PriorEffects.Length)
            throw new InvalidOperationException("A prior effect can have only one disposition row.");
        foreach (var d in PriorEffects)
            if (d.EffectId == Guid.Empty || !ValidText(d.LogicalId) || d.Kinds.IsDefaultOrEmpty || d.Kinds.Distinct().Count() != d.Kinds.Length ||
                d.Kinds.Any(k => !Enum.IsDefined(k)) || !ValidText(d.Reason))
                throw new InvalidOperationException("Prior effect disposition is incomplete.");
    }

    private string ComputeDigest()
    {
        var b = new StringBuilder("marang-repository-effect-graph-v1\n").Append(WorkflowId.ToString("D")).Append('\n').Append(RevisionId).Append('\n')
            .Append(ParentRevisionId).Append('\n').Append(RepositoryId).Append('\n').Append(PhysicalBaseState.StateDigest).Append('\n');
        foreach (var e in Effects.OrderBy(e => e.LogicalId, StringComparer.Ordinal))
            b.Append(e.LogicalId).Append('\0').Append(e.Effect.Intent.EffectId.ToString("D")).Append('\0').Append(e.Effect.CausalDigest).Append('\0').Append(e.Reuse).Append('\0').Append(e.Rationale).Append('\0').Append(e.ReplacesEffectId?.ToString("D")).Append('\n');
        foreach (var d in PriorEffects.OrderBy(d => d.EffectId))
            b.Append(d.EffectId.ToString("D")).Append('\0').Append(d.LogicalId).Append('\0').Append(string.Join(',', d.Kinds.Order())).Append('\0').Append(d.ReplacementEffectId).Append('\0').Append(d.Reason).Append('\n');
        return RepositoryStateSnapshot.Hash(b.ToString());
    }
    private static bool ValidText(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl) && value == value.Trim();
    private static void RequireText(string value, string name) { if (!ValidText(value)) throw new ArgumentException("Canonical value required.", name); }
}

public static class RepositoryEffectGraphImpact
{
    public static ImmutableArray<RepositoryEffectDisposition> Analyze(RepositoryEffectGraph parent, RepositoryEffectGraph candidate)
    {
        if (!parent.Verify() || !candidate.Verify() || parent.WorkflowId != candidate.WorkflowId || parent.RepositoryId != candidate.RepositoryId || candidate.ParentRevisionId != parent.RevisionId)
            throw new InvalidOperationException("Impact analysis requires valid adjacent workflow graphs.");
        var nextByLogical = candidate.Effects.ToDictionary(x => x.LogicalId, StringComparer.Ordinal);
        var parentById = parent.Effects.ToDictionary(x => x.Effect.Intent.EffectId);
        var explicitReplacements = candidate.Effects.Where(e => e.ReplacesEffectId.HasValue).ToDictionary(e => e.ReplacesEffectId!.Value);
        if (explicitReplacements.Keys.Any(id => !parentById.ContainsKey(id)))
            throw new InvalidOperationException("A replacement mapping must identify an effect in the exact parent graph.");
        var result = ImmutableArray.CreateBuilder<RepositoryEffectDisposition>();
        var acceptedPrefix = new HashSet<Guid>();
        var physical = parent.Effects.Where(e => !e.Reuse).ToList();
        var state = parent.PhysicalBaseState.StateDigest;
        var reachesCandidateBase = state == candidate.PhysicalBaseState.StateDigest;
        while (!reachesCandidateBase)
        {
            var next = physical.SingleOrDefault(e => e.Effect.Intent.BaseState.StateDigest == state);
            if (next is null) break;
            acceptedPrefix.Add(next.Effect.Intent.EffectId);
            state = next.Effect.Intent.ExpectedPostState.StateDigest;
            physical.Remove(next);
            reachesCandidateBase = state == candidate.PhysicalBaseState.StateDigest;
        }
        foreach (var old in parent.Effects)
        {
            var i = old.Effect.Intent;
            var hasExplicitReplacement = explicitReplacements.TryGetValue(i.EffectId, out var mapped);
            if (hasExplicitReplacement && candidate.Effects.Any(e => e.Effect.Intent.EffectId == i.EffectId))
                throw new InvalidOperationException("A replaced effect cannot also be retained in the candidate graph.");
            var hasLogicalFallback = nextByLogical.TryGetValue(old.LogicalId, out var logicalNext);
            if (!hasExplicitReplacement && (!hasLogicalFallback || logicalNext!.ReplacesEffectId.HasValue))
            {
                result.Add(new(i.EffectId, old.LogicalId,
                [RepositoryEffectDispositionKind.Invalidated, RepositoryEffectDispositionKind.RequiresReplacement, RepositoryEffectDispositionKind.Superseded],
                null, "Logical effect was removed; its prior causal result is invalidated for downstream effects.")); continue;
            }
            var next = hasExplicitReplacement ? mapped! : logicalNext!;
            var same = old.Effect.CausalDigest == next.Effect.CausalDigest;
            if (same && next.Reuse && next.Effect.Intent.EffectId == i.EffectId)
            { result.Add(new(i.EffectId, old.LogicalId, [RepositoryEffectDispositionKind.Preserved], null, "Exact causal effect explicitly reused.")); continue; }
            if (!same && next.Effect.Intent.EffectId != i.EffectId)
            {
                result.Add(new(i.EffectId, old.LogicalId,
                    [RepositoryEffectDispositionKind.Invalidated, RepositoryEffectDispositionKind.RequiresReplacement, RepositoryEffectDispositionKind.Superseded],
                    next.Effect.Intent.EffectId, "The prior causal effect changed and is invalidated; its replacement has a new causal identity."));
                continue;
            }
            result.Add(new(i.EffectId, old.LogicalId,
                [RepositoryEffectDispositionKind.Invalidated, RepositoryEffectDispositionKind.RequiresReplacement], null,
                "The prior causal effect was not explicitly reused or replaced."));
        }
        // Any descendant of a changed node is invalidated unless its exact causal identity is explicitly reused.
        var invalid = result.Where(x => x.Kinds.Contains(RepositoryEffectDispositionKind.Invalidated)).Select(x => x.EffectId).ToHashSet();
        bool changed;
        do
        {
            changed = false; foreach (var e in parent.Effects)
                if (!invalid.Contains(e.Effect.Intent.EffectId) && e.Effect.Prerequisites.Any(d => invalid.Contains(d.EffectId))) { invalid.Add(e.Effect.Intent.EffectId); changed = true; }
        } while (changed);
        foreach (var id in invalid)
        {
            var old = parentById[id]; var index = -1;
            for (var n = 0; n < result.Count; n++) if (result[n].EffectId == id) { index = n; break; }
            var changedPredecessors = old.Effect.Prerequisites.Where(p => invalid.Contains(p.EffectId)).Select(p => p.EffectId).Distinct().Order().ToArray();
            if (index < 0) result.Add(new(id, old.LogicalId,
                [RepositoryEffectDispositionKind.Invalidated, RepositoryEffectDispositionKind.RequiresReplacement, RepositoryEffectDispositionKind.Superseded], null,
                changedPredecessors.Length == 0 ? "The prior causal effect changed and is invalidated." :
                    $"Downstream effect invalidated because prerequisite effect(s) {string.Join(", ", changedPredecessors.Select(x => x.ToString("D")))} changed or were removed."));
            else if (changedPredecessors.Length > 0) result[index] = result[index] with
            {
                Kinds = result[index].Kinds.Concat([RepositoryEffectDispositionKind.Invalidated, RepositoryEffectDispositionKind.RequiresReplacement,
                    RepositoryEffectDispositionKind.Superseded]).Distinct().ToImmutableArray(),
                Reason = $"Downstream effect invalidated because prerequisite effect(s) {string.Join(", ", changedPredecessors.Select(x => x.ToString("D")))} changed or were removed."
            };
        }
        for (var n = 0; n < result.Count; n++)
            if (reachesCandidateBase && acceptedPrefix.Contains(result[n].EffectId) && !result[n].Kinds.Contains(RepositoryEffectDispositionKind.AcceptedAsBase))
                result[n] = result[n] with { Kinds = result[n].Kinds.Add(RepositoryEffectDispositionKind.AcceptedAsBase) };
        return result.OrderBy(d => d.EffectId).ToImmutableArray();
    }
}
