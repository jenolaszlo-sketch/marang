using Marang.Hufu;

/// <summary>Test-only permissive resource gate for the restart probe. Not production code.</summary>
internal sealed class TestResourceGate : IMarangResourceAuthorizationGate
{
    public static TestResourceGate AllowAll() => new();

    private TestResourceGate()
    {
    }

    public ValueTask<MarangResourceAuthorizationOutcome> AuthorizeAsync(
        MarangResourceAuthorizationInput input, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new MarangResourceAuthorizationOutcome(
            MarangResourceAuthorizationStatus.Permitted, "test.permitted"));
}
