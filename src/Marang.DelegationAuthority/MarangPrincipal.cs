namespace Marang.DelegationAuthority;

/// <summary>
/// Stable host principal. The subject is the long-lived caller identity
/// (survives credential rotation); raw API-key material never enters
/// authority or acceptance records.
/// </summary>
public sealed record MarangPrincipal(string TenantId, string SubjectId)
{
    /// <summary>Single-tenant host scope. Multi-tenant hosts extend the mapper, not this shape.</summary>
    public const string DefaultTenant = "marang";

    /// <summary>Loopback development identity, resolved to an explicit stable subject.</summary>
    public const string LocalOperatorSubject = "local-operator";
}

/// <summary>
/// Maps an authenticated caller identity to its stable principal. The input
/// is the already-authenticated caller label (API-key label or loopback
/// identity), never key material: rotating a key keeps the subject stable.
/// </summary>
public static class MarangPrincipalMapper
{
    /// <summary>Resolves the stable principal for one authenticated caller identity.</summary>
    public static MarangPrincipal MapCaller(string callerIdentity)
    {
        if (string.IsNullOrWhiteSpace(callerIdentity) || callerIdentity.Length > 256 ||
            callerIdentity.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded non-empty caller identity is required.", nameof(callerIdentity));
        }

        return new MarangPrincipal(MarangPrincipal.DefaultTenant, callerIdentity.Trim());
    }
}
