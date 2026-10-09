using System.Text.Json;
using Penghou.Qingniao;

namespace Marang.Runs;

/// <summary>
/// Host-owned correlation joining a Qingniao delegation to a Zhinu workflow
/// run and its Fuwen plan revision. Persisted inside the Zhinu run's opaque
/// <c>MetadataJson</c> (an existing durable carrier), never in a second
/// Marang identity store. Marang reads it back; it never invents run, plan,
/// or delegation identity.
/// </summary>
public sealed record RunCorrelation(
    DelegationId DelegationId,
    string Generation,
    Guid WorkflowRunId,
    string PlanRevision,
    string ExecutionFingerprint)
{
    private const string DelegationKey = "marang.delegationId";
    private const string GenerationKey = "marang.generation";
    private const string PlanRevisionKey = "marang.planRevision";
    private const string FingerprintKey = "marang.executionFingerprint";

    /// <summary>Serializes the correlation for a Zhinu run's MetadataJson.</summary>
    public string ToMetadataJson() => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        [DelegationKey] = DelegationId.Value.ToString("D"),
        [GenerationKey] = Generation,
        [PlanRevisionKey] = PlanRevision,
        [FingerprintKey] = ExecutionFingerprint,
    });

    /// <summary>
    /// Reads the correlation back from a Zhinu run's MetadataJson. Returns
    /// null when absent or malformed; the caller treats that as uncorrelated.
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

            return new RunCorrelation(
                new DelegationId(delegationGuid),
                generation,
                workflowRunId,
                planRevision,
                fingerprint);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
