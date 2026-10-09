using System.Security.Cryptography;
using System.Text;

namespace Marang.DelegationAuthority;

/// <summary>
/// Stable acceptance identity for one caller-scoped request key.
///
/// The acceptance key depends only on <c>(tenant, subject, requestKey)</c>:
/// content determines whether a replay is <em>compatible</em>, never which
/// acceptance identity it addresses. A conflicting retry (same key, different
/// content) therefore targets the same durable slot and is rejected there,
/// even after the process-local acceptance registry is gone.
/// </summary>
public static class AcceptanceIdentity
{
    /// <summary>Domain separator for the acceptance-key hash.</summary>
    public const string AcceptanceKeyDomain = "marang-acceptance/v1";

    /// <summary>Domain separator for the deterministic delegation-ID derivation.</summary>
    public const string DelegationIdDomain = "marang-delegation-id/v1";

    /// <summary>Generation bound at acceptance time. Later generations get their own rows.</summary>
    public const string InitialGeneration = "g0";

    /// <summary>
    /// Derives the stable acceptance key. Pure function of tenant, subject,
    /// and request key; content-independent by construction.
    /// </summary>
    public static string AcceptanceKey(string tenantId, string subjectId, string requestKey)
    {
        RequireToken(tenantId, nameof(tenantId));
        RequireToken(subjectId, nameof(subjectId));
        if (string.IsNullOrWhiteSpace(requestKey) || requestKey.Length > 256)
        {
            throw new ArgumentException("A bounded non-empty request key is required.", nameof(requestKey));
        }

        return Hex(Hash(AcceptanceKeyDomain, tenantId, subjectId, requestKey));
    }

    /// <summary>
    /// Derives the generation-0 delegation ID deterministically from the
    /// acceptance key, so exact retries converge on one delegation without a
    /// shared transaction or a surviving in-memory registry.
    /// </summary>
    public static Guid DelegationIdFor(string acceptanceKey)
    {
        if (string.IsNullOrWhiteSpace(acceptanceKey) || acceptanceKey.Length != 64)
        {
            throw new ArgumentException("A 64-character acceptance key is required.", nameof(acceptanceKey));
        }

        var bytes = Hash(DelegationIdDomain, acceptanceKey);
        var guidBytes = new byte[16];
        Array.Copy(bytes, guidBytes, 16);

        // Mark as name-based (RFC 9562 version 5 layout with SHA-256
        // truncated to 128 bits): version nibble 0101, variant 10xx.
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    /// <summary>
    /// Derives the Hufu visibility command identity. Fingerprint-insensitive:
    /// the same acceptance plus the same operation/epoch always yields the
    /// same command, so exact replays return the original receipt and
    /// conflicting content is rejected at the addressed slot.
    /// </summary>
    public static string VisibilityCommandId(string acceptanceKey, string generation)
    {
        if (string.IsNullOrWhiteSpace(acceptanceKey) || acceptanceKey.Length != 64)
        {
            throw new ArgumentException("A 64-character acceptance key is required.", nameof(acceptanceKey));
        }

        RequireToken(generation, nameof(generation));
        return $"visibility-accept/{acceptanceKey}/{generation}";
    }

    private static byte[] Hash(params string[] fields)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        writer.Flush();
        return SHA256.HashData(stream.ToArray());
    }

    private static string Hex(byte[] bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();

    private static void RequireToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded non-empty token is required.", parameterName);
        }
    }
}
