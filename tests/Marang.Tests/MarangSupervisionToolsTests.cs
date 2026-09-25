using System.Text.Json;
using FluentAssertions;
using Marang.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Penghou.Qingniao;

namespace Marang.Tests;

/// <summary>
/// Supervisor-gated tools: fencing validation is reported, never retried, and
/// the supervisor identity always comes from the authenticated context.
/// Positive waiting-checkpoint flows need a pausing provider and stay live
/// integration concerns; these tests pin the deterministic surface.
/// </summary>
public sealed class MarangSupervisionToolsTests
{
    [Fact]
    public async Task Wait_returns_terminal_state_immediately()
    {
        var (supervision, delegates) = CreateTools("tester");
        var ct = TestContext.Current.CancellationToken;
        var delegationId = await DelegateAsync(delegates, ct);

        var json = await supervision.WaitAsync(delegationId, 5, ct);

        JsonDocument.Parse(json).RootElement.GetProperty("state").GetString()
            .Should().Be("NeedsSupervisor");
    }

    [Fact]
    public async Task Wait_rejects_unknown_ids()
    {
        var (supervision, delegates) = CreateTools("tester");
        var ct = TestContext.Current.CancellationToken;

        var json = await supervision.WaitAsync(Guid.NewGuid().ToString("D"), 1, ct);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()
            .Should().Contain("unknown delegation");
    }

    [Fact]
    public async Task Intervene_validates_inputs_before_touching_state()
    {
        var (supervision, delegates) = CreateTools("tester");
        var ct = TestContext.Current.CancellationToken;
        var delegationId = await DelegateAsync(delegates, ct);

        var badAction = await supervision.InterveneAsync(delegationId, "explode", Guid.NewGuid().ToString("D"), 0, "x", null, ct);
        JsonDocument.Parse(badAction).RootElement.GetProperty("error").GetString()
            .Should().Contain("unsupported action");

        var badCheckpoint = await supervision.InterveneAsync(delegationId, "approve", "not-a-guid", 0, "", null, ct);
        JsonDocument.Parse(badCheckpoint).RootElement.GetProperty("error").GetString()
            .Should().Contain("unknown checkpoint");

        var missingReason = await supervision.InterveneAsync(
            delegationId, "reject", Guid.NewGuid().ToString("D"), 0, "", null, ct);
        JsonDocument.Parse(missingReason).RootElement.GetProperty("error").GetString()
            .Should().Contain("unsupported action");
    }

    [Fact]
    public async Task Intervene_on_non_waiting_delegation_reports_rejection()
    {
        var (supervision, delegates) = CreateTools("tester");
        var ct = TestContext.Current.CancellationToken;
        var delegationId = await DelegateAsync(delegates, ct);

        var json = await supervision.InterveneAsync(
            delegationId, "approve", Guid.NewGuid().ToString("D"), 0, "looks good", null, ct);

        // No waiting checkpoint exists: the runtime rejects, the tool reports.
        JsonDocument.Parse(json).RootElement.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Inspect_validates_fencing_and_facets()
    {
        var (supervision, delegates) = CreateTools("tester");
        var ct = TestContext.Current.CancellationToken;
        var delegationId = await DelegateAsync(delegates, ct);
        var checkpoint = Guid.NewGuid().ToString("D");

        var badFacet = await supervision.InspectAsync(delegationId, checkpoint, 0, "bogus", 50, 8192, ct);
        JsonDocument.Parse(badFacet).RootElement.GetProperty("error").GetString()
            .Should().Contain("unknown facet");

        var nonWaiting = await supervision.InspectAsync(delegationId, checkpoint, 0, "status", 50, 8192, ct);
        JsonDocument.Parse(nonWaiting).RootElement.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetArtifact_reports_missing_and_nonterminal()
    {
        var (supervision, delegates) = CreateTools("tester");
        var ct = TestContext.Current.CancellationToken;
        var delegationId = await DelegateAsync(delegates, ct);

        // NeedsSupervisor is terminal but carries no result artifacts here.
        var missing = await supervision.GetArtifactAsync(delegationId, "nope", ct);
        JsonDocument.Parse(missing).RootElement.TryGetProperty("error", out _).Should().BeTrue();

        var unknown = await supervision.GetArtifactAsync(Guid.NewGuid().ToString("D"), "nope", ct);
        JsonDocument.Parse(unknown).RootElement.GetProperty("error").GetString()
            .Should().Contain("unknown delegation");
    }

    private static async Task<string> DelegateAsync(MarangDelegationTools delegation, CancellationToken ct)
    {
        var json = await delegation.DelegateAsync(
            $"key-{Guid.NewGuid():N}", "Do the work", "no-such-provider", "workspace", 10, ct);
        return JsonDocument.Parse(json).RootElement.GetProperty("delegationId").GetString()!;
    }

    private static (MarangSupervisionTools Supervision, MarangDelegationTools Delegation) CreateTools(string caller)
    {
        var runtime = new DelegationRuntime(
            new InMemoryDelegationAcceptanceRegistry(),
            new MarangAdmissionVerifier(),
            new InMemoryProviderRegistry(),
            new InMemoryExternalOperationProviderCatalog());
        var context = new DefaultHttpContext();
        context.Items[MarangHttpContextKeys.CallerIdentity] = caller;
        var accessor = new HttpContextAccessor { HttpContext = context };
        var options = Options.Create(new MarangAuthenticationOptions
        {
            AllowedWorkspaceRoots = new Dictionary<string, string[]>
            {
                [caller] = ["workspace"],
            },
        });
        return (new MarangSupervisionTools(runtime, accessor, options),
            new MarangDelegationTools(runtime, accessor, options));
    }
}
