using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Penghou.Qingniao;

namespace Marang.Mcp;

/// <summary>
/// Supervisor-gated tools over the composed Qingniao runtime. Every tool
/// derives the supervisor from the authenticated HTTP context, and every
/// checkpoint-scoped call carries the exact checkpoint id plus the expected
/// revision: stale or competing actions are rejected by the runtime, and this
/// layer reports the rejection instead of retrying it.
/// </summary>
[McpServerToolType]
public sealed class MarangSupervisionTools(
    DelegationRuntime runtime,
    IHttpContextAccessor httpContext,
    IOptions<MarangAuthenticationOptions> authentication)
{
    /// <summary>Polls one delegation until terminal or bounded timeout.</summary>
    [McpServerTool(Name = "marang_wait")]
    [Description("Poll one delegation until it reaches a terminal state or the timeout elapses. Returns the final state.")]
    public async Task<string> WaitAsync(
        [Description("Delegation id.")] string delegationId,
        [Description("Timeout in seconds (1-300).")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(delegationId, out var id))
        {
            return Error("unknown delegation id");
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 1, 300));
        while (true)
        {
            var status = await runtime.GetStatusAsync(new DelegationId(id), cancellationToken).ConfigureAwait(false);
            if (status is null)
            {
                return Error("unknown delegation");
            }

            if (DelegationLifecycle.IsTerminal(status.State) || DateTimeOffset.UtcNow >= deadline)
            {
                return JsonSerializer.Serialize(new
                {
                    state = status.State.ToString(),
                    revision = status.Revision,
                });
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Applies one fenced supervisor intervention.</summary>
    [McpServerTool(Name = "marang_intervene")]
    [Description("Apply one supervisor intervention (approve, reject, retry, escalate, cancel) against an exact checkpoint and revision. Stale actions are rejected, never retried.")]
    public async Task<string> InterveneAsync(
        [Description("Delegation id.")] string delegationId,
        [Description("Action: approve, reject, retry, escalate, or cancel.")] string action,
        [Description("Checkpoint id of the waiting checkpoint.")] string checkpointId,
        [Description("Expected progress revision (fencing).")] long expectedRevision,
        [Description("Reason or rationale.")] string reason = "",
        [Description("Idempotency key. Defaults to a deterministic key.")] string? interventionKey = null,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(delegationId, out var id))
        {
            return Error("unknown delegation id");
        }

        if (!Guid.TryParse(checkpointId, out var checkpoint))
        {
            return Error("unknown checkpoint id");
        }

        SupervisorAction? act = action.ToLowerInvariant() switch
        {
            "approve" => new SupervisorAction.Approve(string.IsNullOrWhiteSpace(reason) ? null : reason),
            "reject" => string.IsNullOrWhiteSpace(reason) ? null : new SupervisorAction.Reject(reason),
            "retry" => string.IsNullOrWhiteSpace(reason) ? null : new SupervisorAction.Retry(reason),
            "escalate" => string.IsNullOrWhiteSpace(reason) ? null : new SupervisorAction.Escalate(reason),
            "cancel" => string.IsNullOrWhiteSpace(reason) ? null : new SupervisorAction.Cancel(reason),
            _ => null,
        };
        if (act is null)
        {
            return Error("unsupported action (approve, reject, retry, escalate, cancel); reason is required except for approve");
        }

        try
        {
            var delegation = new DelegationId(id);
            var snapshot = await runtime.ApplyInterventionAsync(
                delegation,
                Supervisor(),
                new SupervisorIntervention(
                    delegation,
                    new SupervisorCheckpointId(checkpoint),
                    interventionKey ?? $"mcp:{id:D}:{expectedRevision}:{action.ToLowerInvariant()}",
                    expectedRevision,
                    act),
                cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                state = snapshot.Progress.State.ToString(),
                revision = snapshot.Progress.Revision,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return Error(exception.Message);
        }
    }

    /// <summary>Reads bounded context for one waiting checkpoint.</summary>
    [McpServerTool(Name = "marang_inspect")]
    [Description("Read bounded re-entry context for an exact waiting checkpoint. Reports included, truncated, and omitted facets explicitly.")]
    public async Task<string> InspectAsync(
        [Description("Delegation id.")] string delegationId,
        [Description("Checkpoint id of the waiting checkpoint.")] string checkpointId,
        [Description("Expected progress revision (fencing).")] long expectedRevision,
        [Description("Comma-separated facets: status, summary, artifacts, correlations, primitiveReferences.")] string facets = "status,summary",
        [Description("Maximum items.")] int maxItems = 50,
        [Description("Maximum inline summary bytes.")] int maxBytes = 8192,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(delegationId, out var id))
        {
            return Error("unknown delegation id");
        }

        if (!Guid.TryParse(checkpointId, out var checkpoint))
        {
            return Error("unknown checkpoint id");
        }

        SupervisorContextFacet requested = SupervisorContextFacet.None;
        foreach (var name in facets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<SupervisorContextFacet>(name, ignoreCase: true, out var facet)
                || facet == SupervisorContextFacet.None)
            {
                return Error($"unknown facet '{name}' (status, summary, artifacts, correlations, primitiveReferences)");
            }

            requested |= facet;
        }

        try
        {
            var package = await runtime.GetContextAsync(
                Supervisor(),
                new SupervisorContextRequest(
                    new DelegationId(id),
                    new SupervisorCheckpointId(checkpoint),
                    expectedRevision,
                    requested,
                    new SupervisorContextLimits(
                        Math.Clamp(maxItems, 1, 500),
                        Math.Clamp(maxBytes, 1, 131_072))),
                cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                revision = package.Revision,
                facets = package.FacetOutcomes.Select(outcome => new
                {
                    facet = outcome.Facet.ToString(),
                    availability = outcome.Availability.ToString(),
                    itemCount = outcome.ItemCount,
                    reason = outcome.Reason,
                }).ToArray(),
                artifacts = package.Artifacts.Select(artifact => new
                {
                    provider = artifact.Provider,
                    repository = artifact.Repository,
                    artifactId = artifact.ArtifactId,
                    kind = artifact.Kind,
                    location = artifact.Location,
                }).ToArray(),
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return Error(exception.Message);
        }
    }

    /// <summary>Reads one artifact descriptor from a terminal result.</summary>
    [McpServerTool(Name = "marang_get_artifact")]
    [Description("Read one artifact descriptor from a terminal delegation result. Artifact bytes stay with the provider; this returns identity, location, and content identity.")]
    public async Task<string> GetArtifactAsync(
        [Description("Delegation id.")] string delegationId,
        [Description("Artifact id.")] string artifactId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(delegationId, out var id))
        {
            return Error("unknown delegation id");
        }

        var result = await runtime.GetResultAsync(new DelegationId(id), cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return Error("not terminal or unknown delegation");
        }

        var artifact = result.Artifacts.FirstOrDefault(candidate =>
            string.Equals(candidate.ArtifactId, artifactId, StringComparison.Ordinal));
        if (artifact is null)
        {
            return Error($"artifact '{artifactId}' not in the terminal result");
        }

        return JsonSerializer.Serialize(new
        {
            provider = artifact.Provider,
            repository = artifact.Repository,
            artifactId = artifact.ArtifactId,
            kind = artifact.Kind,
            location = artifact.Location,
            schemaVersion = artifact.SchemaVersion,
        });
    }

    private SupervisorIdentity Supervisor() =>
        new("marang", CurrentCaller());

    private string CurrentCaller() =>
        httpContext.HttpContext?.Items[MarangHttpContextKeys.CallerIdentity] as string
        ?? authentication.Value.LocalCallerIdentity;

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message });
}
