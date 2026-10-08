using Marang.Mcp;
using Penghou.Qingniao;

namespace Marang.Http;

/// <summary>
/// HTTP adapter over the Qingniao supervision operations already exposed
/// through MCP (<c>marang_wait</c>, <c>marang_intervene</c>,
/// <c>marang_inspect</c>). No supervisor semantics are invented here:
/// caller scoping, checkpoint fencing, and intervention validation all live
/// in the runtime and the acceptance catalog. Handlers return a status code
/// plus a body; the host maps them to HTTP results. Unknown and forbidden
/// delegations both report 404 so callers cannot enumerate each other.
/// </summary>
public static class SupervisionHttpEndpoints
{
    /// <summary>Reads one caller-scoped delegation with an optional waiting summary.</summary>
    public static async Task<(int StatusCode, object? Body)> GetDetailAsync(
        DelegationRuntime runtime,
        MarangDelegationCatalog catalog,
        string caller,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);

        var delegationId = new DelegationId(id);
        var record = catalog.Find(delegationId, caller);
        if (record is null)
        {
            return (404, null);
        }

        var progress = await runtime.GetStatusAsync(delegationId, cancellationToken).ConfigureAwait(false);
        if (progress is null)
        {
            return (404, null);
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);

        var delegationId = new DelegationId(id);
        if (catalog.Find(delegationId, caller) is null)
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);

        var delegationId = new DelegationId(id);
        if (catalog.Find(delegationId, caller) is null)
        {
            return (404, null);
        }

        try
        {
            await runtime.CancelAsync(delegationId, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return (404, null);
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
        CancellationToken cancellationToken = default)
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
        if (catalog.Find(delegationId, caller) is null)
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
