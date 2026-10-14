using Marang.Hufu;
using Marang.Mcp;
using Marang.Runs;
using Penghou.Hufu;
using Penghou.Qingniao;
using Penghou.Zhinu;

namespace Marang.Http;

/// <summary>
/// HTTP adapter over the Qingniao supervision operations already exposed
/// through MCP (<c>marang_wait</c>, <c>marang_intervene</c>,
/// <c>marang_inspect</c>). No supervisor semantics are invented here:
/// caller scoping, checkpoint fencing, and intervention validation all live
/// in the runtime and the acceptance catalog. Handlers return a status code
/// plus a body; the host maps them to HTTP results. Unknown and forbidden
/// delegations both report 404 so callers cannot enumerate each other.
///
/// Historical reads survive restart without a live runtime: when the
/// in-memory execution handle is gone but the caller holds a live visibility
/// binding, detail and evidence are reconstructed from durable sources
/// (accepted envelope, Zhinu run/output, session linkage) with unavailable
/// runtime-only fields reported as explicit nulls rather than synthesized.
/// </summary>
public static class SupervisionHttpEndpoints
{
    /// <summary>
    /// Reads one caller-scoped delegation with an optional waiting summary.
    /// Without a live runtime handle, an authorized caller receives a
    /// reconstructed history view when a durable run source is supplied;
    /// without one the delegation reports 404 exactly as before.
    /// </summary>
    public static async Task<(int StatusCode, object? Body)> GetDetailAsync(
        DelegationRuntime runtime,
        MarangDelegationCatalog catalog,
        string caller,
        Guid id,
        CancellationToken cancellationToken = default,
        IRunProjectionSource? runs = null,
        IMarangResourceAuthorizationGate? gate = null,
        MarangDelegationCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);

        var delegationId = new DelegationId(id);
        var record = await ReattachedAsync(catalog, caller, delegationId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return (404, null);
        }

        // The waiting card names a checkpoint id only after reading status, so
        // the supervision resource authorizes this read; checkpoint exactness
        // is enforced downstream by fencing, not by authority.
        if (!await AuthorizeAsync(gate, caller, AuthorityAction.MarangReadDelegation,
                id, record.Workspace, $"delegation/{id:D}/supervision", credential, cancellationToken).ConfigureAwait(false))
        {
            return (404, null);
        }

        var progress = await runtime.GetStatusAsync(delegationId, cancellationToken).ConfigureAwait(false);
        if (progress is null)
        {
            // No live execution handle: reconstruct the history view from
            // durable sources when the host supplied a run source. Without
            // one, preserve the historical 404.
            return await ReconstructedDetailAsync(record, delegationId, runs, cancellationToken).ConfigureAwait(false);
        }

        var result = await runtime.GetResultAsync(delegationId, cancellationToken).ConfigureAwait(false);
        return (200, new
        {
            id = record.Id.Value.ToString("D"),
            objective = record.Objective,
            provider = record.Provider,
            workspace = record.Workspace,
            state = progress.State.ToString(),
            updatedAt = progress.UpdatedAt,
            revision = progress.Revision,
            currentSteps = progress.CurrentSteps,
            completedSteps = progress.CompletedSteps,
            workerCalls = progress.WorkerCalls,
            retries = progress.Retries,
            resultSummary = result?.Summary,
            unresolvedConcerns = result?.UnresolvedConcerns ?? [],
            waiting = await WaitingSummaryAsync(runtime, progress, cancellationToken).ConfigureAwait(false),
            canCancel = !DelegationLifecycle.IsTerminal(progress.State),
        });
    }

    /// <summary>Reads bounded checkpoint context for a waiting delegation.</summary>
    public static async Task<(int StatusCode, object? Body)> GetWaitingAsync(
        DelegationRuntime runtime,
        MarangDelegationCatalog catalog,
        string caller,
        Guid id,
        CancellationToken cancellationToken = default,
        IMarangResourceAuthorizationGate? gate = null,
        MarangDelegationCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);

        var delegationId = new DelegationId(id);
        var record = await ReattachedAsync(catalog, caller, delegationId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return (404, null);
        }

        if (!await AuthorizeAsync(gate, caller, AuthorityAction.MarangReadDelegation,
                id, record.Workspace, $"delegation/{id:D}/supervision", credential, cancellationToken).ConfigureAwait(false))
        {
            return (404, null);
        }

        var progress = await runtime.GetStatusAsync(delegationId, cancellationToken).ConfigureAwait(false);
        if (progress is null
            || progress.State != DelegationState.WaitingForSupervisor
            || progress.Checkpoint is null)
        {
            return (404, null);
        }

        // The checkpoint summary comes from the waiting progress and its wake
        // hint, never from a context provider: production configures none, and
        // the card needs reason + action, not facet inspection (which stays on
        // the MCP inspect path where a provider exists).
        var hint = await runtime.GetWakeHintAsync(delegationId, cancellationToken).ConfigureAwait(false);
        return (200, new
        {
            checkpointId = progress.Checkpoint.CheckpointId.Value.ToString("D"),
            revision = progress.Revision,
            reason = "Needs your approval",
            summary = hint?.Reason ?? "The delegation is waiting for a supervisor decision.",
            requestedAction = "Continue this step?",
            canIntervene = true,
        });
    }

    /// <summary>
    /// Requests durable cancellation over the existing fenced
    /// <c>runtime.CancelAsync</c>. No second cancellation implementation:
    /// unknown or forbidden delegations report 404, and an already-terminal
    /// delegation is an idempotent success reporting its current state.
    /// </summary>
    public static async Task<(int StatusCode, object? Body)> PostCancelAsync(
        DelegationRuntime runtime,
        MarangDelegationCatalog catalog,
        string caller,
        Guid id,
        CancellationToken cancellationToken = default,
        IMarangResourceAuthorizationGate? gate = null,
        MarangDelegationCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);

        var delegationId = new DelegationId(id);
        var record = await ReattachedAsync(catalog, caller, delegationId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return (404, null);
        }

        if (!await AuthorizeAsync(gate, caller, AuthorityAction.MarangCancel,
                id, record.Workspace, $"delegation/{id:D}/supervision", credential, cancellationToken).ConfigureAwait(false))
        {
            return (404, null);
        }

        try
        {
            await runtime.CancelAsync(delegationId, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Known delegation without a live execution handle (notably after
            // a restart): fail closed as unavailable rather than unknown, so
            // operators can distinguish "no such delegation" (404) from "no
            // controllable execution here" (409). No work is started.
            var live = await runtime.GetStatusAsync(delegationId, cancellationToken).ConfigureAwait(false);
            return live is null
                ? (409, new { error = "control unavailable: the delegation has no live execution handle" })
                : (404, null);
        }

        var current = await runtime.GetStatusAsync(delegationId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return (404, null);
        }

        return (200, new
        {
            state = current.State.ToString(),
            revision = current.Revision,
        });
    }

    /// <summary>
    /// Reads caller-owned evidence for one delegation: compact result
    /// counters, flattened normalized findings, and artifact descriptors.
    /// Metadata only — artifact bytes stay with the provider. A delegation
    /// without a terminal result truthfully reports no evidence yet.
    /// Without a live runtime handle, an authorized caller receives the
    /// durable absence shape (plus any durable terminal receipts) when a run
    /// source is supplied; without one the delegation reports 404 as before.
    /// </summary>
    public static async Task<(int StatusCode, object? Body)> GetEvidenceAsync(
        DelegationRuntime runtime,
        MarangDelegationCatalog catalog,
        string caller,
        Guid id,
        CancellationToken cancellationToken = default,
        IRunProjectionSource? runs = null,
        IMarangResourceAuthorizationGate? gate = null,
        MarangDelegationCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);

        var delegationId = new DelegationId(id);
        var evidenceRecord = await ReattachedAsync(catalog, caller, delegationId, cancellationToken).ConfigureAwait(false);
        if (evidenceRecord is null)
        {
            return (404, null);
        }

        if (!await AuthorizeAsync(gate, caller, AuthorityAction.MarangReadResult,
                id, evidenceRecord.Workspace, $"delegation/{id:D}/result", credential, cancellationToken).ConfigureAwait(false))
        {
            return (404, null);
        }

        var progress = await runtime.GetStatusAsync(delegationId, cancellationToken).ConfigureAwait(false);
        if (progress is null)
        {
            // No live Qingniao result survives restart. Prefer durable
            // receipts (Zhinu terminal output/error, session linkage) and
            // report the Qingniao result as absent rather than 404ing an
            // otherwise authorized delegation. The entire Qingniao result is
            // never persisted merely to serve this endpoint.
            return await ReconstructedEvidenceAsync(delegationId, runs, cancellationToken).ConfigureAwait(false);
        }

        var result = await runtime.GetResultAsync(delegationId, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return (200, new
            {
                hasResult = false,
                state = progress.State.ToString(),
                evidence = (object?)null,
                findings = Array.Empty<object>(),
                artifacts = Array.Empty<object>(),
            });
        }

        return (200, new
        {
            hasResult = true,
            state = result.State.ToString(),
            evidence = new
            {
                testsPassed = result.Evidence.TestsPassed,
                testsFailed = result.Evidence.TestsFailed,
                reviewApproved = result.Evidence.ReviewApproved,
                reviewFindingsResolved = result.Evidence.ReviewFindingsResolved,
            },
            findings = EvidenceHttpMapper.FlattenFindings(result.NormalizedEvidence),
            artifacts = result.Artifacts.Select(DescribeArtifact).ToArray(),
        });
    }

    /// <summary>
    /// Reads one caller-owned artifact descriptor from a terminal result.
    /// Mirrors <c>marang_get_artifact</c> exactly: identity, location, and
    /// content identity only — never artifact bytes. Unknown, forbidden,
    /// non-terminal, and unknown-artifact all report 404 without distinction.
    /// </summary>
    public static async Task<(int StatusCode, object? Body)> GetArtifactAsync(
        DelegationRuntime runtime,
        MarangDelegationCatalog catalog,
        string caller,
        Guid id,
        string artifactId,
        CancellationToken cancellationToken = default,
        IMarangResourceAuthorizationGate? gate = null,
        MarangDelegationCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);

        var delegationId = new DelegationId(id);
        var artifactRecord = await ReattachedAsync(catalog, caller, delegationId, cancellationToken).ConfigureAwait(false);
        if (artifactRecord is null)
        {
            return (404, null);
        }

        // Caller-supplied artifact identity: the gate rejects non-canonical
        // segments rather than normalizing them.
        if (!await AuthorizeAsync(gate, caller, AuthorityAction.MarangReadResult,
                id, artifactRecord.Workspace, $"delegation/{id:D}/artifact/{artifactId}", credential, cancellationToken).ConfigureAwait(false))
        {
            return (404, null);
        }

        var result = await runtime.GetResultAsync(delegationId, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return (404, null);
        }

        var artifact = result.Artifacts.FirstOrDefault(candidate =>
            string.Equals(candidate.ArtifactId, artifactId, StringComparison.Ordinal));
        if (artifact is null)
        {
            return (404, null);
        }

        return (200, DescribeArtifact(artifact));
    }

    private static object DescribeArtifact(DelegationArtifactReference artifact) => new
    {
        provider = artifact.Provider,
        repository = artifact.Repository,
        artifactId = artifact.ArtifactId,
        kind = artifact.Kind,
        location = artifact.Location,
        schemaVersion = artifact.SchemaVersion,
    };

    /// <summary>
    /// Durable evidence absence shape: no Qingniao result is retained, but
    /// terminal Zhinu receipts (output, error) and the session linkage are
    /// reported when they exist. Zero-artifact success stays valid: an empty
    /// artifact set on an absent result means "no Qingniao-result artifacts",
    /// never a claim about the Zhinu artifact store.
    /// </summary>
    private static async Task<(int StatusCode, object? Body)> ReconstructedEvidenceAsync(
        DelegationId delegationId,
        IRunProjectionSource? runs,
        CancellationToken cancellationToken)
    {
        if (runs is null)
        {
            return (404, null);
        }

        var runId = await runs.GetRunIdByDelegationAsync(delegationId, cancellationToken).ConfigureAwait(false);
        if (runId is null)
        {
            return (200, HistoryEvidence(null, null, null));
        }

        var run = await runs.GetRunAsync(runId.Value, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return (404, null);
        }

        var correlation = RunCorrelation.TryParseMetadataJson(run.MetadataJson, run.Id);
        if (correlation is null || correlation.DelegationId != delegationId)
        {
            return (404, null);
        }

        return (200, HistoryEvidence(runId.Value, run, correlation.SessionId));
    }

    private static object HistoryEvidence(Guid? runId, WorkflowRun? run, string? sessionId)
    {
        var terminal = run is not null && IsTerminal(run.Status);
        return new
        {
            hasResult = false,
            state = (string?)null,
            runtimeAvailable = false,
            runId = runId?.ToString("D"),
            runStatus = run?.Status.ToString(),
            sessionId,
            terminalOutput = terminal ? run!.OutputJson : null,
            terminalError = terminal ? run!.Error?.Message : null,
            evidence = (object?)null,
            findings = Array.Empty<object>(),
            artifacts = Array.Empty<object>(),
        };
    }

    /// <summary>
    /// Applies one approve/resume intervention. Only <c>approve</c> is offered
    /// here; the remaining supervisor actions stay on the MCP path until they
    /// have clearly defined product semantics.
    /// </summary>
    public static async Task<(int StatusCode, object? Body)> PostInterventionAsync(
        DelegationRuntime runtime,
        MarangDelegationCatalog catalog,
        string caller,
        Guid id,
        InterventionHttpRequest request,
        CancellationToken cancellationToken = default,
        IMarangResourceAuthorizationGate? gate = null,
        MarangDelegationCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.Action, "approve", StringComparison.OrdinalIgnoreCase))
        {
            return (400, new { error = "Only 'approve' is supported here." });
        }

        if (!Guid.TryParse(request.CheckpointId, out var checkpoint)
            || request.ExpectedRevision is null)
        {
            return (400, new { error = "A checkpoint id and expected revision are required." });
        }

        var delegationId = new DelegationId(id);
        var interventionRecord = await ReattachedAsync(catalog, caller, delegationId, cancellationToken).ConfigureAwait(false);
        if (interventionRecord is null)
        {
            return (404, null);
        }

        // One authorization covers the activate-then-accept protocol below:
        // both runtime calls use the same bound delegation/checkpoint ids.
        if (!await AuthorizeAsync(gate, caller, AuthorityAction.MarangIntervene,
                id, interventionRecord.Workspace, $"delegation/{id:D}/checkpoint/{checkpoint:D}/intervention",
                credential, cancellationToken).ConfigureAwait(false))
        {
            return (404, null);
        }

        // One operator action maps to the backend's activate-then-accept
        // protocol: approving a checkpoint authorizes this supervisor for it
        // and immediately records the approval. There is no separate UI step.
        var supervisor = new SupervisorIdentity("marang", caller);
        try
        {
            await runtime.ActivateCheckpointAsync(delegationId, supervisor, cancellationToken)
                .ConfigureAwait(false);
            var snapshot = await runtime.ApplyInterventionAsync(
                delegationId,
                supervisor,
                new SupervisorIntervention(
                    delegationId,
                    new SupervisorCheckpointId(checkpoint),
                    string.IsNullOrWhiteSpace(request.InterventionKey)
                        ? $"http:{id:D}:{request.ExpectedRevision}:approve"
                        : request.InterventionKey,
                    request.ExpectedRevision.Value,
                    new SupervisorAction.Approve(
                        string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason)),
                cancellationToken).ConfigureAwait(false);
            return (200, new
            {
                state = snapshot.Progress.State.ToString(),
                revision = snapshot.Progress.Revision,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return await Conflict(runtime, delegationId, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<MarangDelegationRecord?> ReattachedAsync(
        MarangDelegationCatalog catalog,
        string caller,
        DelegationId delegationId,
        CancellationToken cancellationToken)
    {
        var cached = catalog.Find(delegationId, caller);
        if (cached is not null)
        {
            return cached;
        }

        if (!await catalog.ReattachAsync(caller, delegationId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return catalog.Find(delegationId, caller);
    }

    /// <summary>
    /// Evaluates the mandatory Hufu resource gate before a protected side
    /// effect. A null gate is an unconfigured host: fail closed (never a
    /// bypass). Denial and unavailability both return false; callers map that
    /// to the same 404 they use for unknown/forbidden delegations, so
    /// authorization never becomes an existence oracle.
    /// </summary>
    private static async Task<bool> AuthorizeAsync(
        IMarangResourceAuthorizationGate? gate,
        string caller,
        AuthorityAction action,
        Guid id,
        string workspace,
        string path,
        MarangDelegationCredential? credential,
        CancellationToken cancellationToken)
    {
        if (gate is null)
        {
            return false;
        }

        var outcome = await gate.AuthorizeAsync(
            new MarangResourceAuthorizationInput(caller, action, id.ToString("D"), workspace, path, credential),
            cancellationToken).ConfigureAwait(false);
        return outcome.Status == MarangResourceAuthorizationStatus.Permitted;
    }

    /// <summary>
    /// Reconstructs one delegation's history view from durable sources when
    /// no live Qingniao handle exists. Every field states its provenance:
    /// envelope identity from the accepted record, run linkage/output from
    /// Zhinu, session linkage from the run correlation. Qingniao pump state
    /// (state, revision, steps, counters, waiting) is reported as explicit
    /// null rather than synthesized; control is reported unavailable.
    /// Without a run source, or when no durable run exists yet, the record is
    /// an authorized envelope with no execution history to show.
    /// </summary>
    private static async Task<(int StatusCode, object? Body)> ReconstructedDetailAsync(
        MarangDelegationRecord record,
        DelegationId delegationId,
        IRunProjectionSource? runs,
        CancellationToken cancellationToken)
    {
        if (runs is null)
        {
            return (404, null);
        }

        var runId = await runs.GetRunIdByDelegationAsync(delegationId, cancellationToken).ConfigureAwait(false);
        if (runId is null)
        {
            return (200, HistoryDetail(record, null, null, null));
        }

        var run = await runs.GetRunAsync(runId.Value, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return (404, null);
        }

        var correlation = RunCorrelation.TryParseMetadataJson(run.MetadataJson, run.Id);
        if (correlation is null || correlation.DelegationId != delegationId)
        {
            return (404, null);
        }

        return (200, HistoryDetail(record, runId.Value, run, correlation.SessionId));
    }

    private static object HistoryDetail(
        MarangDelegationRecord record,
        Guid? runId,
        WorkflowRun? run,
        string? sessionId)
    {
        var terminal = run is not null && IsTerminal(run.Status);
        return new
        {
            id = record.Id.Value.ToString("D"),
            objective = record.Objective,
            provider = record.Provider,
            workspace = record.Workspace,
            state = (string?)null,
            runtimeAvailable = false,
            runId = runId?.ToString("D"),
            runStatus = run?.Status.ToString(),
            sessionId,
            updatedAt = (DateTimeOffset?)run?.UpdatedAt,
            revision = (long?)null,
            currentSteps = (IReadOnlyList<string>?)null,
            completedSteps = (IReadOnlyList<string>?)null,
            workerCalls = (int?)null,
            retries = (int?)null,
            resultSummary = terminal ? run!.OutputJson : null,
            unresolvedConcerns = (IReadOnlyList<string>?)null,
            waiting = (object?)null,
            canCancel = false,
        };
    }

    private static bool IsTerminal(WorkflowStatus status) =>
        status is WorkflowStatus.Completed or WorkflowStatus.Failed
            or WorkflowStatus.Cancelled or WorkflowStatus.Compensated;

    private static async Task<object?> WaitingSummaryAsync(
        DelegationRuntime runtime,
        DelegationProgress progress,
        CancellationToken cancellationToken)
    {
        if (progress.State != DelegationState.WaitingForSupervisor || progress.Checkpoint is null)
        {
            return null;
        }

        var hint = await runtime.GetWakeHintAsync(progress.DelegationId, cancellationToken).ConfigureAwait(false);
        return new
        {
            checkpointId = progress.Checkpoint.CheckpointId.Value.ToString("D"),
            reason = "Needs your approval",
            summary = hint?.Reason ?? "The delegation is waiting for a supervisor decision.",
            requestedAction = "Continue this step?",
            canIntervene = true,
            expectedRevision = progress.Revision,
        };
    }

    private static async Task<(int StatusCode, object? Body)> Conflict(
        DelegationRuntime runtime,
        DelegationId delegationId,
        string message,
        CancellationToken cancellationToken)
    {
        // Stale fence, stale checkpoint, or no-longer-waiting: report the
        // current state so the caller refreshes instead of retrying blindly.
        // Never resubmit; the runtime already rejected the duplicate.
        var current = await runtime.GetStatusAsync(delegationId, cancellationToken).ConfigureAwait(false);
        return (409, new
        {
            error = message,
            state = current?.State.ToString(),
            revision = current?.Revision,
        });
    }
}

/// <summary>HTTP request body for one approve/resume intervention.</summary>
public sealed record InterventionHttpRequest(
    string? Action,
    string? CheckpointId,
    long? ExpectedRevision,
    string? Reason,
    string? InterventionKey);
