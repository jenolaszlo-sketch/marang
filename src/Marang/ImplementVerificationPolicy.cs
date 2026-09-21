using Penghou.Qingniao;

namespace Marang;

/// <summary>
/// Marang's Implement-preset verification policy for the Qingniao runtime:
/// deterministic validation must pass and independent review must approve;
/// a first-round failure earns exactly one checkpoint-local re-execution.
/// This is Marang product policy — pass criteria, review standards, and the
/// single-repair budget live here, never in Qingniao core.
/// </summary>
public sealed class ImplementVerificationPolicy : ICandidateVerificationPolicy
{
    /// <summary>Gets the two verification rounds (initial plus one re-execution).</summary>
    public int MaxVerificationRounds => 2;

    /// <summary>Decides one verification round outcome from its evidence.</summary>
    public CandidateVerificationDecision Decide(CandidateVerificationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Round == 1
            && input.ValidationFailure is null
            && input.ReviewFailure is null)
        {
            var failing = input.Validation is null
                || input.Review is null
                || !IsPass(input.Validation.Outcome)
                || !IsApprove(input.Review.Outcome);
            if (failing)
            {
                return CandidateVerificationDecision.RequestLocalReexecution();
            }
        }

        var validationPass = input.Validation is not null
            && input.ValidationFailure is null
            && IsPass(input.Validation.Outcome);
        var reviewApprove = input.Review is not null
            && input.ReviewFailure is null
            && IsApprove(input.Review.Outcome);
        if (input.ValidationFailure is not null || input.Validation is null || !validationPass)
        {
            return CandidateVerificationDecision.Reject();
        }

        if (!reviewApprove)
        {
            return CandidateVerificationDecision.ContinueWithConstraint("Independent review rejected the candidate.");
        }

        return CandidateVerificationDecision.Accept();
    }

    private static bool IsPass(string outcome) =>
        string.Equals(outcome, "passed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, "pass", StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, "success", StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, "approved", StringComparison.OrdinalIgnoreCase);

    private static bool IsApprove(string outcome) =>
        string.Equals(outcome, "approved", StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, "approve", StringComparison.OrdinalIgnoreCase)
        || string.Equals(outcome, "passed", StringComparison.OrdinalIgnoreCase);
}
