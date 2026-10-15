using System.Text.Json;
using Marang.Hufu;
using Marang.Runs.Adaptive;
using Penghou.Hufu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Runs.Effects;

/// <summary>A deterministic write gate invoked while the M3 executor owns the physical checkout lease.</summary>
public interface IRepositoryEffectExecutionGuard
{
    ValueTask<string?> CheckBeforeWriteAsync(RepositoryEffectIntent intent, RepositoryStateSnapshot observed,
        AuthenticatedAuthorityContext context, CancellationToken cancellationToken = default);
}

/// <summary>M4 selects and validates causal prerequisites; M3 continues to own every physical replacement.</summary>
public sealed class RepositoryEffectGraphRuntime(IZhinuSqliteDatabase database, SqliteAdaptiveRevisionRepository revisions,
    SqliteRepositoryEffectGraphJournal graphs, SqliteRepositoryEffectJournal effects, ControlledRepository repository,
    IAuthorityRequestAuthorizer authorizer, string workspaceId) : IRepositoryEffectManifestVerifier, IRepositoryEffectExecutionGuard
{
    public RepositoryEffectExecutor CreateExecutor() => new(repository, effects,
        new RepositoryEffectAuthority(authorizer, this, workspaceId, database.TimeProvider), this);

    public async Task<RepositoryEffectGraph> GetActiveGraphAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        var revision = await revisions.GetActiveAsync(workflowId, cancellationToken);
        var graph = AdaptivePlanAuthoring.ReadEffectGraph(revision) ?? throw new WorkflowStateException("Active revision has no admitted causal graph.");
        if ((await graphs.GetGraphAsync(workflowId, revision.RevisionId, cancellationToken))?.GraphDigest != graph.GraphDigest)
            throw new WorkflowStateException("Active causal graph differs from its prepared immutable archive.");
        return graph;
    }

    public async Task<RepositoryEffectExecutionResult> ExecuteAsync(Guid effectId, AuthenticatedAuthorityContext context,
        Func<RepositoryStateSnapshot, CancellationToken, ValueTask<RepositoryEffectValidation>>? localValidate = null,
        Func<RepositoryEffectExecutionCheckpoint, CancellationToken, ValueTask>? checkpoint = null,
        CancellationToken cancellationToken = default)
    {
        var causal = await graphs.GetEffectAsync(effectId, cancellationToken) ?? throw new WorkflowStateException("No immutable causal effect exists.");
        var graph = await GetActiveGraphAsync(causal.Intent.WorkflowId, cancellationToken);
        RequireContext(graph, context);
        var selected = graph.Effects.SingleOrDefault(s => s.Effect.Intent.EffectId == effectId);
        var record = await effects.GetAsync(effectId, cancellationToken) ?? throw new WorkflowStateException("Effect has no prepared durable intent.");
        // Historical and already recorded effects are never routed to an unkeyed mutation. Logical acceptance is checked separately.
        await using (var lease = await repository.AcquireLockAsync(cancellationToken))
        {
            graph = await GetActiveGraphAsync(causal.Intent.WorkflowId, cancellationToken);
            RequireContext(graph, context);
            selected = graph.Effects.SingleOrDefault(s => s.Effect.Intent.EffectId == effectId);
            record = await effects.GetAsync(effectId, cancellationToken) ?? throw new WorkflowStateException("Effect intent disappeared.");
            var observed = repository.ObserveUnderLock();
            if (selected is null)
                return new(record, RecordedApplied(record) ? RepositoryEffectClassification.DefinitelyApplied : RepositoryEffectReconciler.Classify(causal.Intent, observed),
                    false, "m4.effect-superseded");
            if (selected.Effect.CausalDigest != causal.CausalDigest) throw new WorkflowStateException("Selected causal effect identity changed.");
            var prerequisiteRefusal = await CheckAcceptedPrerequisitesAsync(graph, selected, observed, cancellationToken);
            if (prerequisiteRefusal is not null)
                return new(record, RepositoryEffectReconciler.Classify(causal.Intent, observed), false, prerequisiteRefusal);
            if (RecordedApplied(record) && RecordedLineageTo(record, await effects.ListForRepositoryAsync(repository.RepositoryId, cancellationToken), observed.StateDigest))
            {
                var reason = await CheckAcceptedPrerequisitesAsync(graph, selected, observed, cancellationToken);
                if (reason is not null) return new(record, RepositoryEffectClassification.DefinitelyApplied, false, reason);
                if (!OutputsPresent(causal.Intent, observed)) return new(record, RepositoryEffectClassification.DefinitelyApplied, false, "m4.applied-output-no-longer-compatible");
                if (record.Stage == RepositoryEffectStage.ValidationPassed && record.Validation?.Passed == true)
                    return new(record, RepositoryEffectClassification.DefinitelyApplied, false, selected.Reuse ? "m4.effect-explicitly-adopted" : "m4.recorded-effect-not-replayed");
            }
        }
        if (selected.Reuse) return new(record, RepositoryEffectReconciler.Classify(causal.Intent, await repository.ObserveAsync(cancellationToken)), false, "m4.adoption-not-validated");
        return await CreateExecutor().ExecuteAsync(causal.Intent, context, localValidate, checkpoint, cancellationToken);
    }

    public async ValueTask<bool> IsAdmittedAsync(RepositoryEffectIntent intent, CancellationToken cancellationToken = default)
    {
        try
        {
            var graph = await GetActiveGraphAsync(intent.WorkflowId, cancellationToken);
            var selected = graph.Effects.SingleOrDefault(s => !s.Reuse && s.Effect.Intent.EffectId == intent.EffectId && s.Effect.Intent.IntentDigest == intent.IntentDigest);
            if (selected is null || intent.RevisionId != graph.RevisionId || !await HasExactApprovalAsync(graph, cancellationToken)) return false;
            // Prevent a premature downstream dispatch from even committing physical intent.
            return await CheckAcceptedPrerequisitesAsync(graph, selected, null, cancellationToken) is null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    public async ValueTask<bool> IsCurrentAsync(RepositoryEffectIntent intent, CancellationToken cancellationToken = default)
    {
        if (!await IsAdmittedAsync(intent, cancellationToken)) return false;
        var record = await effects.GetAsync(intent.EffectId, cancellationToken);
        return record is not null && !RecordedApplied(record) && record.Stage != RepositoryEffectStage.Blocked;
    }

    public async ValueTask<string?> CheckBeforeWriteAsync(RepositoryEffectIntent intent, RepositoryStateSnapshot observed,
        AuthenticatedAuthorityContext context, CancellationToken cancellationToken = default)
    {
        var graph = await GetActiveGraphAsync(intent.WorkflowId, cancellationToken);
        RequireContext(graph, context);
        var selected = graph.Effects.SingleOrDefault(s => !s.Reuse && s.Effect.Intent.EffectId == intent.EffectId && s.Effect.Intent.IntentDigest == intent.IntentDigest);
        if (selected is null || !await HasExactApprovalAsync(graph, cancellationToken)) return "m4.stale-effect-selection-or-approval";
        var reason = await CheckAcceptedPrerequisitesAsync(graph, selected, observed, cancellationToken);
        if (reason is not null) return reason;
        if (intent.BaseState.StateDigest != observed.StateDigest) return "m4.downstream-base-mismatch";
        var evidence = new List<RepositoryEffectAuthorizationEvidence>();
        foreach (var dependency in TransitivePrerequisites(graph, selected))
        {
            foreach (var file in dependency.Effect.Intent.Files)
            {
                var identity = RepositoryStateSnapshot.Hash($"m4.accepted-prerequisite/v1\n{graph.GraphDigest}\n{intent.EffectId:D}\n{dependency.Effect.CausalDigest}\n{observed.StateDigest}\n{file.Path}");
                var request = new AuthorityRequest(context, AuthorityAction.WriteFile, workspaceId, AuthorityValidation.NormalizePath(file.Path), identity);
                var result = await authorizer.AuthorizeAsync(request, cancellationToken);
                if (result.Request != request || !result.IsAuthorized || result.Status != AuthorityStatus.Permit || result.Decision is null)
                    return "m4.prerequisite-reuse-authority-denied";
                evidence.Add(new(dependency.Effect.Intent.IntentDigest, file.Path, identity, result.Status.ToString(), context.TenantId,
                    context.SubjectId, context.RunId, context.RevisionId, result.Decision.ReasonCode, result.Decision.SnapshotIdentity,
                    result.Decision.SnapshotVersion, database.TimeProvider.GetUtcNow(), GraphDigest: graph.GraphDigest,
                    DependentEffectId: intent.EffectId, PrerequisiteEffectId: dependency.Effect.Intent.EffectId,
                    PrerequisiteCausalDigest: dependency.Effect.CausalDigest, ObservedStateDigest: observed.StateDigest));
            }
        }
        if (evidence.Count > 0) await graphs.RecordPrerequisiteAuthorityAsync(graph, intent.EffectId, evidence, cancellationToken);
        return null;
    }

    public async Task<RepositoryEffectValidation> ValidateWorkflowAsync(Guid workflowId, AuthenticatedAuthorityContext context,
        Func<RepositoryStateSnapshot, CancellationToken, ValueTask<RepositoryEffectValidation>> validate,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await repository.AcquireLockAsync(cancellationToken);
        var graph = await GetActiveGraphAsync(workflowId, cancellationToken);
        RequireContext(graph, context);
        if (!await HasExactApprovalAsync(graph, cancellationToken)) throw new WorkflowStateException("Workflow validation lacks current exact graph approval.");
        var before = repository.ObserveUnderLock();
        if (before.StateDigest != FinalState(graph)) throw new WorkflowStateException("Full validation must target the exact terminal physical state.");
        foreach (var selection in graph.Effects)
        {
            var record = await effects.GetAsync(selection.Effect.Intent.EffectId, cancellationToken);
            if (!LocallyValidated(record) || !OutputsPresent(selection.Effect.Intent, before) ||
                await CheckAcceptedPrerequisitesAsync(graph, selection, before, cancellationToken) is not null)
                throw new WorkflowStateException("Full validation requires a coherent accepted locally validated effect set.");
        }
        var prior = await graphs.GetWorkflowValidationAsync(workflowId, graph.RevisionId, cancellationToken);
        if (prior is not null && prior.TargetStateDigest == before.StateDigest) return prior;
        var result = await validate(before, cancellationToken);
        var after = repository.ObserveUnderLock();
        if (before.StateDigest != after.StateDigest || result.TargetStateDigest != before.StateDigest)
            throw new WorkflowStateException("Workflow validation evidence belongs to a changed repository state.");
        await graphs.RecordWorkflowValidationAsync(graph, result, cancellationToken);
        return result;
    }

    internal async Task<bool> HasExactApprovalAsync(RepositoryEffectGraph graph, CancellationToken ct)
    {
        await using var c = await database.OpenAsync(ct);
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT p.document,p.digest,p.approval_actor,p.approval_evidence,p.approval_expires,r.document
            FROM adaptive_revisions r JOIN adaptive_proposals p ON r.proposal_id=p.id AND r.workflow_id=p.workflow_id
            WHERE r.workflow_id=$w AND r.revision_id=$r AND p.status='Committed'
            """;
        q.Parameters.AddWithValue("$w", graph.WorkflowId.ToString("D")); q.Parameters.AddWithValue("$r", graph.RevisionId);
        await using var reader = await q.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4) ||
            string.IsNullOrWhiteSpace(reader.GetString(2)) || string.IsNullOrWhiteSpace(reader.GetString(3))) return false;
        var proposal = JsonSerializer.Deserialize<AdaptiveProposal>(reader.GetString(0))!;
        var revision = JsonSerializer.Deserialize<AdaptiveRevision>(reader.GetString(5))!;
        return proposal.Authority == AdaptiveAuthorityDecision.ApprovalRequired && AdaptiveRevisionContracts.Digest(proposal) == reader.GetString(1) &&
            AdaptiveRevisionContracts.Digest(proposal.Candidate) == AdaptiveRevisionContracts.Digest(revision) &&
            AdaptivePlanAuthoring.ReadEffectGraph(revision)?.GraphDigest == graph.GraphDigest &&
            DateTimeOffset.Parse(reader.GetString(4)) > database.TimeProvider.GetUtcNow();
    }

    internal async Task<string?> CheckAcceptedPrerequisitesAsync(RepositoryEffectGraph graph, RepositoryEffectSelection selection,
        RepositoryStateSnapshot? observed, CancellationToken ct)
    {
        foreach (var prerequisite in TransitivePrerequisites(graph, selection))
        {
            var record = await effects.GetAsync(prerequisite.Effect.Intent.EffectId, ct);
            if (record?.Stage == RepositoryEffectStage.Blocked || record?.Classification is RepositoryEffectClassification.PartiallyApplied or RepositoryEffectClassification.Unresolved)
                return $"m4.prerequisite-{prerequisite.LogicalId}-requires-reconciliation";
            if (!LocallyValidated(record)) return $"m4.prerequisite-{prerequisite.LogicalId}-not-accepted-validated";
            if (observed is not null && !OutputsPresent(prerequisite.Effect.Intent, observed)) return $"m4.prerequisite-{prerequisite.LogicalId}-output-stale";
        }
        return null;
    }

    internal static IEnumerable<RepositoryEffectSelection> TransitivePrerequisites(RepositoryEffectGraph graph, RepositoryEffectSelection selected)
    {
        var seen = new HashSet<Guid>(); var result = new List<RepositoryEffectSelection>();
        void Visit(RepositoryEffectSelection node)
        {
            foreach (var dependency in node.Effect.Prerequisites)
            {
                if (!seen.Add(dependency.EffectId)) continue;
                var prior = graph.Effects.Single(s => s.Effect.Intent.EffectId == dependency.EffectId);
                Visit(prior); result.Add(prior);
            }
        }
        Visit(selected); return result;
    }

    internal static bool LocallyValidated(RepositoryEffectRecord? record) => record is { Stage: RepositoryEffectStage.ValidationPassed, Validation.Passed: true } &&
        RecordedApplied(record) && record.Validation.TargetStateDigest == record.Intent.ExpectedPostState.StateDigest;
    internal static bool RecordedApplied(RepositoryEffectRecord record) => record.Classification == RepositoryEffectClassification.DefinitelyApplied &&
        record.ObservedState?.StateDigest == record.Intent.ExpectedPostState.StateDigest && record.Stage is RepositoryEffectStage.AppliedObserved or RepositoryEffectStage.ValidationPassed or RepositoryEffectStage.ValidationFailed;
    internal static bool OutputsPresent(RepositoryEffectIntent intent, RepositoryStateSnapshot observed) => intent.Files.All(f =>
        observed.Files.Any(actual => actual.Path == f.Path && actual.Sha256 == f.AfterSha256));
    public static string FinalState(RepositoryEffectGraph graph)
    {
        var fresh = graph.Effects.Where(s => !s.Reuse).ToArray();
        return fresh.Length == 0 ? graph.PhysicalBaseState.StateDigest : fresh.Single(s => !fresh.Any(child => child.Effect.Intent.BaseState.StateDigest == s.Effect.Intent.ExpectedPostState.StateDigest)).Effect.Intent.ExpectedPostState.StateDigest;
    }
    internal static bool RecordedLineageTo(RepositoryEffectRecord origin, IReadOnlyList<RepositoryEffectRecord> records, string observed)
    {
        if (!RecordedApplied(origin)) return false;
        var state = origin.Intent.ExpectedPostState.StateDigest; var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < 64 && seen.Add(state); depth++)
        {
            if (state == observed) return true;
            var next = records.Where(r => r.Intent.BaseState.StateDigest == state && RecordedApplied(r)).ToArray();
            if (next.Length != 1) return false;
            state = next[0].Intent.ExpectedPostState.StateDigest;
        }
        return false;
    }
    private static void RequireContext(RepositoryEffectGraph graph, AuthenticatedAuthorityContext context)
    {
        if (context.RunId != graph.WorkflowId.ToString("D") || context.RevisionId != graph.RevisionId)
            throw new WorkflowAuthorizationException("Host context must bind the current selected graph revision.");
    }
}
