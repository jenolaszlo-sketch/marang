using Penghou.Qingniao;

namespace Marang.Http;

/// <summary>
/// Flattens normalized validation/review findings into a compact,
/// operator-readable list. Only code, severity, summary, resolution, and
/// origin are carried; finding detail maps and invocation transcripts stay
/// out. No synthesis: entries are copied verbatim from the bundle.
/// </summary>
public static class EvidenceHttpMapper
{
    /// <summary>Flattens bundle findings; a missing bundle yields an empty list.</summary>
    public static IReadOnlyList<object> FlattenFindings(EvidenceBundle? bundle)
    {
        if (bundle is null)
        {
            return [];
        }

        var flattened = new List<object>();
        foreach (var validation in bundle.Validations)
        {
            foreach (var finding in validation.Findings)
            {
                flattened.Add(Describe(finding, "validation"));
            }
        }

        foreach (var review in bundle.Reviews)
        {
            foreach (var finding in review.Findings)
            {
                flattened.Add(Describe(finding, "review"));
            }
        }

        return flattened;
    }

    private static object Describe(EvidenceFinding finding, string source) => new
    {
        code = finding.Code,
        severity = finding.Severity,
        summary = finding.Summary,
        resolved = finding.Resolved,
        source,
    };
}
