using System.Text.Json;
using FluentAssertions;
using Marang.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Penghou.Qingniao;

namespace Marang.Tests;

/// <summary>
/// Exercises the MCP delegation tools against a real composed runtime with no
/// providers registered: delegations honestly wait for supervision, reads are
/// null-safe, and cancellation is fenced. The caller identity always comes
/// from the HTTP context set by the boundary middleware, never from a client
/// argument.
/// </summary>
public sealed class MarangDelegationToolsTests
{
    [Fact]
    public async Task Delegate_without_providers_waits_for_supervision()
    {
        var tools = CreateTools("tester");
        var ct = TestContext.Current.CancellationToken;

        var json = await tools.DelegateAsync(
            $"key-{Guid.NewGuid():N}", "Do the work", "no-such-provider", "workspace", 10, ct);
        var document = JsonDocument.Parse(json);

        document.RootElement.GetProperty("state").GetString().Should().Be("NeedsSupervisor");
        document.RootElement.GetProperty("caller").GetString().Should().Be("tester");
        document.RootElement.GetProperty("delegationId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Delegate_rejects_unauthorized_workspace()
    {
        var tools = CreateTools("tester");
        var ct = TestContext.Current.CancellationToken;

        var json = await tools.DelegateAsync(
            $"key-{Guid.NewGuid():N}", "Do the work", "no-such-provider", "elsewhere", 10, ct);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()
            .Should().Contain("not authorized");
    }

    [Fact]
    public async Task Missing_context_falls_back_to_local_operator()
    {
        var runtime = CreateRuntime();
        var accessor = new HttpContextAccessor();
        var tools = new MarangDelegationTools(
            runtime, accessor, Options.Create(new MarangAuthenticationOptions()));
        var ct = TestContext.Current.CancellationToken;

        var json = await tools.DelegateAsync(
            $"key-{Guid.NewGuid():N}", "Do the work", "no-such-provider", "workspace", 1, ct);

        JsonDocument.Parse(json).RootElement.GetProperty("caller").GetString()
            .Should().Be("local-operator");
    }

    [Fact]
    public async Task Status_result_and_cancel_handle_unknown_ids()
    {
        var tools = CreateTools("tester");
        var ct = TestContext.Current.CancellationToken;
        var unknown = Guid.NewGuid().ToString("D");

        JsonDocument.Parse(await tools.GetStatusAsync(unknown, ct))
            .RootElement.GetProperty("error").GetString().Should().Contain("unknown delegation");
        JsonDocument.Parse(await tools.GetResultAsync(unknown, ct))
            .RootElement.GetProperty("error").GetString().Should().Contain("not terminal");
        JsonDocument.Parse(await tools.CancelAsync(unknown, ct))
            .RootElement.GetProperty("error").GetString().Should().Contain("unknown delegation");
        JsonDocument.Parse(await tools.GetStatusAsync("not-a-guid", ct))
            .RootElement.GetProperty("error").GetString().Should().Contain("unknown delegation id");
    }

    [Fact]
    public async Task Cancel_before_execution_terminates()
    {
        var runtime = CreateRuntime();
        var tools = new MarangDelegationTools(
            runtime, Context("tester"), Options.Create(new MarangAuthenticationOptions()));
        var ct = TestContext.Current.CancellationToken;
        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope("tester"),
            new DelegationRequest(
                $"key-{Guid.NewGuid():N}",
                "Do the work",
                "no-such-provider",
                new WorkspaceReference("local", "workspace", null),
                ["Done"],
                [],
                new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2)),
            ct);

        var delegationId = handle.DelegationId.Value.ToString("D");
        (await tools.CancelAsync(delegationId, ct)).Should().Be("cancellation requested");
        JsonDocument.Parse(await tools.GetStatusAsync(delegationId, ct))
            .RootElement.GetProperty("state").GetString().Should().Be("Cancelled");
    }

    private static MarangDelegationTools CreateTools(string caller)
    {
        var options = new MarangAuthenticationOptions
        {
            AllowedWorkspaceRoots = new Dictionary<string, string[]>
            {
                [caller] = ["workspace"],
            },
        };
        return new MarangDelegationTools(CreateRuntime(), Context(caller), Options.Create(options));
    }

    private static IHttpContextAccessor Context(string caller)
    {
        var context = new DefaultHttpContext();
        context.Items[MarangHttpContextKeys.CallerIdentity] = caller;
        return new HttpContextAccessor { HttpContext = context };
    }

    private static DelegationRuntime CreateRuntime() => new(
        new InMemoryDelegationAcceptanceRegistry(),
        new MarangAdmissionVerifier(),
        new InMemoryProviderRegistry(),
        new InMemoryExternalOperationProviderCatalog());
}
