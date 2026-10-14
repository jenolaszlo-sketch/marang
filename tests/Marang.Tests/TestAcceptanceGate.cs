using Marang.Hufu;

/// <summary>
/// Test-only permissive acceptance gate. It is NOT production code and must not
/// be shipped; production hosts compose a real Hufu acceptance gate. It exists so
/// existing acceptance tests unrelated to AE-5A.2 keep exercising their behavior
/// while every catalog construction still supplies an explicit gate.
/// </summary>
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
