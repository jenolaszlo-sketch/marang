using Marang.Hufu;

/// <summary>
/// Test-only resource gates. They are NOT production code and must not be
/// shipped; production hosts compose a real Hufu resource gate. They exist so
/// behavior tests unrelated to AE-5B keep exercising their paths while every
/// tool/handler construction still supplies an explicit gate, and so coverage
/// probes can prove every protected operation is wired (deny-all denies all).
/// </summary>
internal sealed class TestResourceGate : IMarangResourceAuthorizationGate
{
    private readonly MarangResourceAuthorizationOutcome _outcome;

    public static TestResourceGate AllowAll() => new(new MarangResourceAuthorizationOutcome(
        MarangResourceAuthorizationStatus.Permitted, "test.permitted"));

    public static TestResourceGate DenyAll() => new(new MarangResourceAuthorizationOutcome(
        MarangResourceAuthorizationStatus.Denied, "test.denied"));

    public static TestResourceGate Unavailable() => new(new MarangResourceAuthorizationOutcome(
        MarangResourceAuthorizationStatus.Unavailable, "test.unavailable"));

    private TestResourceGate(MarangResourceAuthorizationOutcome outcome) => _outcome = outcome;

    public ValueTask<MarangResourceAuthorizationOutcome> AuthorizeAsync(
        MarangResourceAuthorizationInput input, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_outcome);
}
