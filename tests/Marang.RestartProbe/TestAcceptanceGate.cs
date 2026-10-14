using Marang.Hufu;

/// <summary>Test-only permissive acceptance gate for the restart probe. Not production code.</summary>
internal sealed class TestAcceptanceGate : IMarangDelegationAcceptanceGate
{
    public static TestAcceptanceGate AllowAll() => new();

    private TestAcceptanceGate()
    {
    }

    public ValueTask<MarangDelegationAcceptanceOutcome> AuthorizeAsync(
        MarangDelegationAcceptanceInput input, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new MarangDelegationAcceptanceOutcome(
            MarangDelegationAcceptanceStatus.Accepted, "test.accepted"));
}
