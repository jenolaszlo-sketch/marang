namespace Marang.Tests.LifecycleBroker;

/// <summary>One unchanged scenario and restart sequence for every native profile.</summary>
internal static class MultiEffectScenario
{
    public const string SourceCommit = "a0916534d0e82e499cffb2dea526b64357eb32e2";
    public static IReadOnlyList<string> ContinuingBarriers { get; } = Array.AsReadOnly(
        new[] { "K1", "K2", "K3", "K5", "K6", "K7", "K8", "K9", "K10", "K11", "K12" });
}
