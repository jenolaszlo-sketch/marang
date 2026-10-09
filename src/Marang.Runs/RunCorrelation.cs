using System.Text.Json;
using Penghou.Qingniao;

namespace Marang.Runs;

/// <summary>
/// Host-owned correlation joining a Qingniao delegation to a Zhinu workflow
/// run, its Fuwen plan revision, and its Hongxian session. The session is
/// carried as an opaque string; only the Hongxian layer interprets it.
/// Persisted inside the Zhinu run's opaque <c>MetadataJson</c> (an existing
/// durable carrier), never in a second Marang identity store.
/// </summary>
public sealed record RunCorrelation(
    DelegationId DelegationId,
    string Generation,
    Guid WorkflowRunId,
    string PlanRevision,
    string ExecutionFingerprint,
    string? SessionId = null)
{
    private const string DelegationKey = "marang.delegationId";
    private const string GenerationKey = "marang.generation";
    private const string PlanRevisionKey = "marang.planRevision";
    private const string FingerprintKey = "marang.executionFingerprint";
    private const string SessionKey = "marang.sessionId";

    /// <summary>Serializes the correlation for a Zhinu run's MetadataJson.</summary>
    public string ToMetadataJson()
    {
        var fields = new Dictionary<string, string>
        {
            [DelegationKey] = DelegationId.Value.ToString("D"),
            [GenerationKey] = Generation,
            [PlanRevisionKey] = PlanRevision,
            [FingerprintKey] = ExecutionFingerprint,
        };
        if (!string.IsNullOrWhiteSpace(SessionId))
        {
            fields[SessionKey] = SessionId;
        }

        return JsonSerializer.Serialize(fields);
    }

    /// <summary>
    /// Reads the correlation back from a Zhinu run's MetadataJson. Returns
    /// null when absent or malformed; the caller treats that as uncorrelated.
    /// A malformed session id fails the whole correlation closed rather than
    /// joining a wrong session.
    /// </summary>
    public static RunCorrelation? TryParseMetadataJson(string? metadataJson, Guid workflowRunId)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(DelegationKey, out var delegationElement)
                || !root.TryGetProperty(GenerationKey, out var generationElement)
                || !root.TryGetProperty(PlanRevisionKey, out var revisionElement)
                || !root.TryGetProperty(FingerprintKey, out var fingerprintElement))
            {
                return null;
            }

            var delegationText = delegationElement.GetString();
            var generation = generationElement.GetString();
            var planRevision = revisionElement.GetString();
            var fingerprint = fingerprintElement.GetString();
            if (string.IsNullOrWhiteSpace(delegationText)
                || string.IsNullOrWhiteSpace(generation)
                || string.IsNullOrWhiteSpace(planRevision)
                || string.IsNullOrWhiteSpace(fingerprint)
                || !Guid.TryParse(delegationText, out var delegationGuid))
            {
                return null;
            }

            string? sessionId = null;
            if (root.TryGetProperty(SessionKey, out var sessionElement))
            {
                var sessionText = sessionElement.GetString();
                if (string.IsNullOrWhiteSpace(sessionText) || !Guid.TryParse(sessionText, out _))
                {
                    return null;
                }

                sessionId = sessionText;
            }

            return new RunCorrelation(
                new DelegationId(delegationGuid),
                generation,
                workflowRunId,
                planRevision,
                fingerprint,
                sessionId);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
