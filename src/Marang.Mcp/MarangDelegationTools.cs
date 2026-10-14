using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Marang.Hufu;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Penghou.Hufu;
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
///
/// Every protected operation evaluates the mandatory Hufu resource gate
/// before its first runtime effect. Denial reports the same shape as an
/// unknown delegation: the catalog is caller-scoped, so a distinct denial
/// shape would confirm a delegation's existence to an unauthorized caller.
/// Acceptance (`marang_delegate`) is deliberately NOT resource-gated: a
/// resource permit must never substitute for acceptance nor vice versa.
/// </summary>
[McpServerToolType]
public sealed class MarangDelegationTools(
    DelegationRuntime runtime,
    MarangDelegationCatalog catalog,
    IHttpContextAccessor httpContext,
    IOptions<MarangAuthenticationOptions> authentication,
    IMarangResourceAuthorizationGate resourceGate)
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
        // Acceptance only: no resource gate applies here by design (§17). A
        // resource permit must never substitute for acceptance, so acceptance
        // consults only the acceptance gate even when a resource gate exists.
        try
        {
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
        catch (MarangDelegationAcceptanceException exception)
        {
            // The mandatory Hufu acceptance gate did not accept: surface the
            // coarse outcome (status + bounded reason code, never credential
            // material) instead of an untyped failure. No acceptance state exists.
            return JsonSerializer.Serialize(new
            {
                error = "delegation not accepted",
                status = exception.Outcome.Status.ToString(),
                reason = exception.Outcome.ReasonCode,
                caller,
            });
        }
    }

    /// <summary>Reads the current delegation state.</summary>
    [McpServerTool(Name = "marang_status")]
    [Description("Read the current state, revision, and waiting checkpoint of one delegation.")]
    public async Task<string> GetStatusAsync(
        [Description("Delegation id.")] string delegationId,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(delegationId, out var id))
        {
            return Error("unknown delegation id");
        }

        var record = catalog.Find(new DelegationId(id), CurrentCaller());
        if (record is null)
        {
            return Error("unknown delegation");
        }

        var denied = await AuthorizeResourceAsync(AuthorityAction.MarangReadDelegation,
            id, record.Workspace, $"delegation/{id:D}/supervision", cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return denied;
        }

        var status = await runtime.GetStatusAsync(new DelegationId(id), cancellationToken).ConfigureAwait(false);
        if (status is null)
        {
            return Error("unknown delegation");
        }

        return JsonSerializer.Serialize(new
        {
            state = status.State.ToString(),
            revision = status.Revision,
            checkpoint = status.Checkpoint?.CheckpointId.Value.ToString("D"),
        });
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
            return Error("unknown delegation id");
        }

        var record = catalog.Find(new DelegationId(id), CurrentCaller());
        if (record is null)
        {
            return Error("unknown delegation");
        }

        var denied = await AuthorizeResourceAsync(AuthorityAction.MarangReadResult,
            id, record.Workspace, $"delegation/{id:D}/result", cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return denied;
        }

        var result = await runtime.GetResultAsync(new DelegationId(id), cancellationToken).ConfigureAwait(false);
        return result is null ? Error("not terminal or unknown delegation") : result.Summary;
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

        var record = catalog.Find(new DelegationId(id), CurrentCaller());
        if (record is null)
        {
            return Error("unknown delegation");
        }

        var denied = await AuthorizeResourceAsync(AuthorityAction.MarangCancel,
            id, record.Workspace, $"delegation/{id:D}/supervision", cancellationToken).ConfigureAwait(false);
        if (denied is not null)
        {
            return denied;
        }

        try
        {
            await runtime.CancelAsync(new DelegationId(id), cancellationToken).ConfigureAwait(false);
            return "cancellation requested";
        }
        catch (InvalidOperationException)
        {
            return Error("unknown delegation");
        }
    }

    private string CurrentCaller() =>
        httpContext.HttpContext?.Items[MarangHttpContextKeys.CallerIdentity] as string
        ?? authentication.Value.LocalCallerIdentity;

    private MarangDelegationCredential? CurrentCredential() =>
        httpContext.HttpContext?.Items[MarangHttpContextKeys.DelegationCredential] as MarangDelegationCredential;

    private async Task<string?> AuthorizeResourceAsync(AuthorityAction action, Guid id,
        string workspace, string path, CancellationToken cancellationToken)
    {
        var outcome = await resourceGate.AuthorizeAsync(
            new MarangResourceAuthorizationInput(
                CurrentCaller(), action, id.ToString("D"), workspace, path, CurrentCredential()),
            cancellationToken).ConfigureAwait(false);
        return outcome.Status == MarangResourceAuthorizationStatus.Permitted ? null : Error("unknown delegation");
    }

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { error = message });
}
