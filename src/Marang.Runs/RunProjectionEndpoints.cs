using Marang.Hufu;
using Penghou.Fuwen.Zhinu;
using Penghou.Hufu;
using Penghou.Qingniao;

namespace Marang.Runs;

/// <summary>
/// HTTP adapter for the run projection. Caller scoping reuses the delegation
/// catalog via the run's host correlation; unknown, uncorrelated, forbidden,
/// and unknown-plan runs all report 404 without distinction. Fingerprint
/// mismatch reports 409 (refresh, do not retry blindly); unclassified steps
/// report 500 (contract drift, never silent).
/// </summary>
public static class RunProjectionEndpoints
{
    /// <summary>
    /// Resolves the run correlated to one delegation for navigation. Caller
    /// scoping reuses the delegation catalog; unknown, uncorrelated, and
    /// forbidden delegations all report 404.
    /// </summary>
    public static async Task<(int StatusCode, object? Body)> GetDelegationRunAsync(
        MarangDelegationCatalog catalog,
        string caller,
        Guid delegationGuid,
        IRunProjectionSource source,
        CancellationToken cancellationToken = default,
        IMarangResourceAuthorizationGate? gate = null,
        MarangDelegationCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);
        ArgumentNullException.ThrowIfNull(source);

        var delegationId = new DelegationId(delegationGuid);
        var record = await ReattachedAsync(catalog, caller, delegationId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return (404, null);
        }

        if (!await AuthorizeAsync(gate, caller, delegationGuid, record.Workspace, credential, cancellationToken).ConfigureAwait(false))
        {
            return (404, null);
        }

        var runId = await source.GetRunIdByDelegationAsync(delegationId, cancellationToken).ConfigureAwait(false);
        if (runId is null)
        {
            return (404, null);
        }

        return (200, new { runId = runId.Value.ToString("D") });
    }

    /// <summary>Reads one caller-scoped run projection.</summary>
    public static async Task<(int StatusCode, object? Body)> GetRunAsync(
        MarangDelegationCatalog catalog,
        string caller,
        Guid runId,
        IRunProjectionSource source,
        Func<DateTimeOffset> now,
        CancellationToken cancellationToken = default,
        IMarangResourceAuthorizationGate? gate = null,
        MarangDelegationCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(now);

        var run = await source.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return (404, null);
        }

        var correlation = RunCorrelation.TryParseMetadataJson(run.MetadataJson, runId);
        if (correlation is null)
        {
            return (404, null);
        }

        var record = await ReattachedAsync(catalog, caller, correlation.DelegationId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return (404, null);
        }

        if (!await AuthorizeAsync(gate, caller, correlation.DelegationId.Value,
                record.Workspace, credential, cancellationToken, $"run/{runId:D}").ConfigureAwait(false))
        {
            return (404, null);
        }

        var plan = await source.GetPlanAsync(correlation.PlanRevision, cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return (404, null);
        }

        FuwenZhinuStepMap map;
        try
        {
            map = FuwenZhinuStepMapper.Map(plan);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return (500, new { error = $"The plan cannot be mapped: {exception.Message}" });
        }

        var steps = await source.GetStepsAsync(runId, cancellationToken).ConfigureAwait(false);
        var throughSequence = await source.GetThroughSequenceAsync(runId, cancellationToken).ConfigureAwait(false);

        try
        {
            var projection = RunProjectionBuilder.Build(
                plan, map, run, steps, throughSequence, correlation,
                record.Workspace, record.Objective, now());
            return (200, projection);
        }
        catch (IncompatibleProjectionException exception)
        {
            return (409, new { error = exception.Message });
        }
        catch (ProjectionContractDriftException exception)
        {
            return (500, new { error = exception.Message });
        }
    }

    /// <summary>
    /// Caller-scoped delegation lookup with post-restart reattachment: a
    /// process-local miss is re-resolved through the durable acceptance
    /// descriptor plus visibility authority (verify-only, never repaired).
    /// Volatile catalogs reattach to nothing and behave exactly as before.
    /// </summary>
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
    /// Evaluates the mandatory Hufu resource gate before a protected run
    /// read. A null gate is an unconfigured host: fail closed. Denial and
    /// unavailability both map to the caller's existing 404.
    /// </summary>
    private static async Task<bool> AuthorizeAsync(
        IMarangResourceAuthorizationGate? gate,
        string caller,
        Guid delegationId,
        string workspace,
        MarangDelegationCredential? credential,
        CancellationToken cancellationToken,
        string? path = null)
    {
        if (gate is null)
        {
            return false;
        }

        var resolved = path ?? $"delegation/{delegationId:D}/run";
        var outcome = await gate.AuthorizeAsync(
            new MarangResourceAuthorizationInput(caller, AuthorityAction.MarangReadResult,
                delegationId.ToString("D"), workspace, resolved, credential),
            cancellationToken).ConfigureAwait(false);
        return outcome.Status == MarangResourceAuthorizationStatus.Permitted;
    }
}
