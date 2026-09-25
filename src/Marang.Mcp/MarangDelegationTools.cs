using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Penghou.Qingniao;

namespace Marang.Mcp;

/// <summary>
/// Delegation lifecycle over the composed Qingniao runtime. The caller
/// identity always comes from the authenticated HTTP context (set by the
/// boundary middleware), never from a client-supplied argument. Accept
/// submits one bounded unit of work and pumps it a bounded number of steps;
/// status, result, and cancel are reads against the same runtime.
/// Intervention, wait, inspect, and artifact tools stay out until their
/// authorization/fencing tests pass.
/// </summary>
[McpServerToolType]
public sealed class MarangDelegationTools(
    DelegationRuntime runtime,
    IHttpContextAccessor httpContext,
    IOptions<MarangAuthenticationOptions> authentication)
{
    /// <summary>Accepts one bounded unit of work and pumps it bounded steps.</summary>
    [McpServerTool(Name = "marang_delegate")]
    [Description("Accept one bounded unit of delegated work and advance it up to maxSteps coordinator steps. Returns the delegation id, caller, and current state.")]
    public async Task<string> DelegateAsync(
        [Description("Caller-scoped idempotency key. Reuse returns the accepted handle.")] string requestKey,
        [Description("Work objective.")] string objective,
        [Description("Provider identity resolved by the runtime.")] string provider,
        [Description("Workspace identifier, authorized against the caller.")] string workspace = "workspace",
        [Description("Maximum coordinator pump steps.")] int maxSteps = 20,
        CancellationToken cancellationToken = default)
    {
        var caller = CurrentCaller();
        if (!MarangApiKeyAuthenticator.IsWorkspaceAuthorized(
                authentication.Value.AllowedWorkspaceRoots, caller, workspace))
        {
            return JsonSerializer.Serialize(new
            {
                error = "workspace not authorized for caller",
                caller,
            });
        }

        var request = new DelegationRequest(
            requestKey,
            objective,
            provider,
            new WorkspaceReference("local", workspace, null),
            ["Done"],
            [],
            new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2));
        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope(caller), request, cancellationToken).ConfigureAwait(false);
        var snapshot = await runtime.GetAsync(handle.DelegationId, cancellationToken).ConfigureAwait(false);
        var steps = Math.Clamp(maxSteps, 1, 50);
        for (var index = 0;
            index < steps && !DelegationLifecycle.IsTerminal(snapshot.Progress.State);
            index++)
        {
            snapshot = await runtime.PumpAsync(
                handle.DelegationId, snapshot.Progress.Revision, cancellationToken).ConfigureAwait(false);
        }

        return JsonSerializer.Serialize(new
        {
            delegationId = handle.DelegationId.Value.ToString("D"),
            caller,
            state = snapshot.Progress.State.ToString(),
        });
    }

    /// <summary>Reads the current delegation state.</summary>
    [McpServerTool(Name = "marang_status")]
    [Description("Read the current state of one delegation, or unknown when the id is not known.")]
    public async Task<string> GetStatusAsync(
        [Description("Delegation id.")] string delegationId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(delegationId, out var id))
        {
            return "unknown delegation id";
        }

        var status = await runtime.GetStatusAsync(new DelegationId(id), cancellationToken).ConfigureAwait(false);
        return status is null ? "unknown delegation" : status.State.ToString();
    }

    /// <summary>Reads the terminal result summary.</summary>
    [McpServerTool(Name = "marang_result")]
    [Description("Read the immutable terminal result summary, or a not-terminal notice.")]
    public async Task<string> GetResultAsync(
        [Description("Delegation id.")] string delegationId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(delegationId, out var id))
        {
            return "unknown delegation id";
        }

        var result = await runtime.GetResultAsync(new DelegationId(id), cancellationToken).ConfigureAwait(false);
        return result?.Summary ?? "not terminal or unknown delegation";
    }

    /// <summary>Requests durable cancellation.</summary>
    [McpServerTool(Name = "marang_cancel")]
    [Description("Request durable cancellation of one delegation.")]
    public async Task<string> CancelAsync(
        [Description("Delegation id.")] string delegationId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(delegationId, out var id))
        {
            return "unknown delegation id";
        }

        try
        {
            await runtime.CancelAsync(new DelegationId(id), cancellationToken).ConfigureAwait(false);
            return "cancellation requested";
        }
        catch (InvalidOperationException)
        {
            return "unknown delegation";
        }
    }

    private string CurrentCaller() =>
        httpContext.HttpContext?.Items[MarangHttpContextKeys.CallerIdentity] as string
        ?? authentication.Value.LocalCallerIdentity;
}
