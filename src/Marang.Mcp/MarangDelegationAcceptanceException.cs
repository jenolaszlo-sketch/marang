using Marang.Hufu;

namespace Marang;

/// <summary>
/// Raised when the mandatory Hufu delegation-acceptance gate does not accept a
/// request. No durable acceptance state was created or mutated before this
/// exception.
/// </summary>
public sealed class MarangDelegationAcceptanceException : InvalidOperationException
{
    public MarangDelegationAcceptanceException(MarangDelegationAcceptanceOutcome outcome)
        : base($"The delegation was not accepted ({outcome.Status}: {outcome.ReasonCode}).")
    {
        ArgumentNullException.ThrowIfNull(outcome);
        Outcome = outcome;
    }

    public MarangDelegationAcceptanceOutcome Outcome { get; }
}
