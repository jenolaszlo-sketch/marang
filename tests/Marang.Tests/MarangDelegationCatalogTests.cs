using FluentAssertions;
using Penghou.Qingniao;

namespace Marang.Tests;

public sealed class MarangDelegationCatalogTests
{
    [Fact]
    public async Task Accepted_delegation_is_visible_only_to_its_caller()
    {
        var catalog = new MarangDelegationCatalog();
        var request = new DelegationRequest(
            "catalog-test", "Review the changes", "provider-one",
            new WorkspaceReference("local", "workspace"),
            ["Done"], [], new DelegationBudget(8, 2));

        var ct = TestContext.Current.CancellationToken;
        var accepted = await catalog.AcceptAsync(new DelegationCallerScope("alice"), request, ct);
        var replay = await catalog.AcceptAsync(new DelegationCallerScope("alice"), request, ct);

        replay.DelegationId.Should().Be(accepted.DelegationId);
        catalog.Find(accepted.DelegationId, "alice")?.Objective.Should().Be("Review the changes");
        catalog.Find(accepted.DelegationId, "bob").Should().BeNull();
        catalog.List("alice").Should().ContainSingle();
        catalog.List("bob").Should().BeEmpty();
    }
}
