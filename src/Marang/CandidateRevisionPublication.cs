namespace Marang;

/// <summary>Small atomic proof of idempotent candidate publication semantics.</summary>
public sealed class InMemoryCandidateRevisionPublicationRegistry : ICandidateRevisionPublicationRegistry
{
    private readonly object gate = new();
    private readonly Dictionary<CandidateKey, CandidateRevisionReference> candidates = new();

    public ValueTask<CandidateRevisionPublication> PublishAsync(CandidateRevisionReference candidate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(candidate);
        var key = new CandidateKey(candidate.DelegationId, candidate.CandidateId, candidate.Revision);
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!candidates.TryGetValue(key, out var existing))
            {
                candidates.Add(key, candidate);
                return ValueTask.FromResult(new CandidateRevisionPublication(candidate, true));
            }

            if (CandidateRevisionIdentity.SemanticallyEqual(existing, candidate))
            {
                return ValueTask.FromResult(new CandidateRevisionPublication(existing, false));
            }

            throw new CandidateRevisionConflictException("The candidate identity is already bound to different immutable content.");
        }
    }

    private readonly record struct CandidateKey(DelegationId DelegationId, CandidateId CandidateId, int Revision);
}
