using Penghou.Fuwen;
using Penghou.Zhinu;

namespace Marang.Runs;

/// <summary>
/// Host-owned loaders for run-projection inputs. Implementations read durable
/// upstream truth (Zhinu run/step/event rows, Fuwen plans); tests supply
/// hand-built records or Sqlite-backed rows. No new identity or ordering
/// semantics are introduced here.
/// </summary>
public interface IRunProjectionSource
{
    /// <summary>Loads one workflow run, or null when unknown.</summary>
    ValueTask<WorkflowRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Loads one compiled plan by revision, or null when unknown.</summary>
    ValueTask<WorkflowPlan?> GetPlanAsync(string planRevision, CancellationToken cancellationToken = default);

    /// <summary>Loads current step rows for one run.</summary>
    ValueTask<IReadOnlyList<WorkflowStepRun>> GetStepsAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the durable through-sequence (highest committed event sequence)
    /// for one run. Backed by Zhinu's event cursor, never a Marang counter.
    /// </summary>
    ValueTask<long> GetThroughSequenceAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the run correlated to one delegation by scanning durable run
    /// carriers (no second identity store; optimizable with an index later).
    /// </summary>
    ValueTask<Guid?> GetRunIdByDelegationAsync(Penghou.Qingniao.DelegationId delegationId, CancellationToken cancellationToken = default);
}
