using System.Security.Cryptography;
using System.Text.Json;
using Penghou.Siming;

namespace Marang;

/// <summary>
/// Siming-backed semantic fingerprint for accepted delegations. The fingerprint
/// commits the delegation identity, caller scope, objective, provider, and
/// workspace through Siming's canonical JSON contract; Marang never copies
/// canonicalization logic. Fingerprints make the host-owned acceptance index
/// tamper-evident: any divergence between a record and its fingerprint fails
/// closed as an unknown delegation.
/// </summary>
public static class MarangDelegationFingerprint
{
    private const string Format = "marang-delegation-fingerprint-v1";

    /// <summary>Computes the stable fingerprint for accepted delegation content.</summary>
    public static string Compute(
        Guid delegationId,
        string caller,
        string objective,
        string provider,
        string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(objective);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            format = Format,
            delegationId = delegationId.ToString("D"),
            caller,
            objective,
            provider,
            workspace
        }));
        return CanonicalJsonPayloadSerializerV2.ComputeSha256(document.RootElement);
    }

    /// <summary>Verifies content against a fingerprint in fixed time.</summary>
    public static bool Verify(
        Guid delegationId,
        string caller,
        string objective,
        string provider,
        string workspace,
        string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length != 64)
            return false;
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(fingerprint);
        }
        catch (FormatException)
        {
            return false;
        }

        string actualText;
        try
        {
            actualText = Compute(delegationId, caller, objective, provider, workspace);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            expected, Convert.FromHexString(actualText));
    }
}
