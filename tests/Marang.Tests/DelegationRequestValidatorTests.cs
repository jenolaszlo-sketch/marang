using FluentAssertions;

namespace Marang.Tests;

public sealed class DelegationRequestValidatorTests
{
    [Fact]
    public void Validate_accepts_a_bounded_implement_request()
    {
        var request = CreateRequest();

        var act = () => DelegationRequestValidator.Validate(request);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_requires_an_idempotency_key()
    {
        var request = new DelegationRequest(
            " ",
            "Add deterministic cursor pagination.",
            new WorkspaceReference("local-project", "marang", "abc123"),
            ["Pagination is deterministic."],
            ["Do not change unrelated APIs."],
            new DelegationBudget(MaximumDuration: TimeSpan.FromMinutes(20)));

        var act = () => DelegationRequestValidator.Validate(request);

        act.Should().Throw<ArgumentException>()
            .WithParameterName(nameof(DelegationRequest.RequestKey));
    }

    [Fact]
    public void Validate_rejects_an_unbounded_worker_call_count()
    {
        var request = new DelegationRequest(
            "change-42",
            "Add deterministic cursor pagination.",
            new WorkspaceReference("local-project", "marang", "abc123"),
            ["Pagination is deterministic."],
            ["Do not change unrelated APIs."],
            new DelegationBudget(MaximumWorkerCalls: 0));

        var act = () => DelegationRequestValidator.Validate(request);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_rejects_unimplemented_strategies()
    {
        var request = new DelegationRequest(
            "change-42",
            "Add deterministic cursor pagination.",
            new WorkspaceReference("local-project", "marang", "abc123"),
            ["Pagination is deterministic."],
            ["Do not change unrelated APIs."],
            new DelegationBudget(MaximumDuration: TimeSpan.FromMinutes(20)),
            DelegationStrategy.Investigate);

        var act = () => DelegationRequestValidator.Validate(request);

        act.Should().Throw<NotSupportedException>();
    }

    private static DelegationRequest CreateRequest() => new(
        "change-42",
        "Add deterministic cursor pagination.",
        new WorkspaceReference("local-project", "marang", "abc123"),
        ["Pagination is deterministic."],
        ["Do not change unrelated APIs."],
        new DelegationBudget(MaximumDuration: TimeSpan.FromMinutes(20)));
}
