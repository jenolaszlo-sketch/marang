using System.Text.Json;
using FluentAssertions;
using Marang.Mcp;
using Penghou.Qingniao;

namespace Marang.Tests;

/// <summary>
/// Exercises the MCP delegation tools against a real composed runtime with no
/// providers registered: delegations honestly wait for supervision, reads are
/// null-safe, and cancellation is fenced.
/// </summary>
public sealed class MarangDelegationToolsTests
{
    [Fact]
    public async Task Delegate_without_providers_waits_for_supervision()
    {
        var tools = CreateTools();
        var ct = TestContext.Current.CancellationToken;

        var json = await tools.DelegateAsync(
            "tester", $"key-{Guid.NewGuid():N}", "Do the work", "no-such-provider", 10, ct);
        var document = JsonDocument.Parse(json);

        document.RootElement.GetProperty("state").GetString().Should().Be("NeedsSupervisor");
        document.RootElement.GetProperty("delegationId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Status_result_and_cancel_handle_unknown_ids()
    {
        var tools = CreateTools();
        var ct = TestContext.Current.CancellationToken;
        var unknown = Guid.NewGuid().ToString("D");

        (await tools.GetStatusAsync(unknown, ct)).Should().Be("unknown delegation");
        (await tools.GetResultAsync(unknown, ct)).Should().Be("not terminal or unknown delegation");
        (await tools.CancelAsync(unknown, ct)).Should().Be("unknown delegation");
        (await tools.GetStatusAsync("not-a-guid", ct)).Should().Be("unknown delegation id");
    }

    [Fact]
    public async Task Cancel_before_execution_terminates()
    {
        var runtime = CreateRuntime();
        var tools = new MarangDelegationTools(runtime);
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
        (await tools.GetStatusAsync(delegationId, ct)).Should().Be("Cancelled");
    }

    private static MarangDelegationTools CreateTools() => new(CreateRuntime());

    private static DelegationRuntime CreateRuntime() => new(
        new InMemoryDelegationAcceptanceRegistry(),
        new MarangAdmissionVerifier(),
        new InMemoryProviderRegistry(),
        new InMemoryExternalOperationProviderCatalog());
}
