using System.Security.Cryptography;
using System.Text;
using Penghou.Qingniao;

namespace Marang;

/// <summary>
/// API-key authentication policy for the Marang HTTP/MCP boundary.
/// Keys live exclusively in host configuration (environment); validation is
/// constant-time; the caller identity derived from the presented key is what
/// reaches <see cref="DelegationCallerScope"/> and diagnostics — never a
/// client-supplied caller string.
/// </summary>
public sealed record MarangAuthenticationOptions
{
    /// <summary>Caller identity by API key, bound from configuration.</summary>
    public Dictionary<string, string> ApiKeys { get; init; } = [];

    /// <summary>Workspace identifiers each caller may use, by caller identity.</summary>
    /// <remarks>
    /// The loopback development identity is pre-authorized for the default
    /// workspace so local development works with no configuration. Every
    /// remote caller starts denied and must be configured explicitly.
    /// </remarks>
    public Dictionary<string, string[]> AllowedWorkspaceRoots { get; init; } = new()
    {
        ["local-operator"] = ["workspace"],
    };

    /// <summary>Loopback requests skip authentication for local development.</summary>
    public bool BypassLoopback { get; init; } = true;

    /// <summary>Caller identity used when authentication is bypassed.</summary>
    public string LocalCallerIdentity { get; init; } = "local-operator";
}

/// <summary>
/// Validates an HTTP `Authorization` header against configured API keys.
/// Only the `Bearer` scheme is accepted; keys never appear in query strings.
/// </summary>
public sealed class MarangApiKeyAuthenticator(IReadOnlyDictionary<string, string> apiKeys)
{
    /// <summary>Returns the caller identity for a valid key, else null.</summary>
    public string? Authenticate(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }

        const string scheme = "Bearer ";
        if (!authorizationHeader.StartsWith(scheme, StringComparison.Ordinal)
            || authorizationHeader.Length <= scheme.Length)
        {
            return null;
        }

        var presented = Encoding.UTF8.GetBytes(authorizationHeader[scheme.Length..].Trim());
        foreach (var (caller, key) in apiKeys)
        {
            var expected = Encoding.UTF8.GetBytes(key);
            if (presented.Length == expected.Length
                && CryptographicOperations.FixedTimeEquals(presented, expected))
            {
                return caller;
            }
        }

        return null;
    }

    /// <summary>Authorizes a workspace identifier against a caller's roots.</summary>
    public static bool IsWorkspaceAuthorized(
        IReadOnlyDictionary<string, string[]> allowedRoots,
        string caller,
        string workspace)
    {
        if (!allowedRoots.TryGetValue(caller, out var roots))
        {
            return false;
        }

        return roots.Any(root =>
            workspace.Equals(root, StringComparison.Ordinal) ||
            workspace.StartsWith(root + "/", StringComparison.Ordinal));
    }
}

/// <summary>HttpContext.Items key carrying the authenticated caller identity.</summary>
public static class MarangHttpContextKeys
{
    /// <summary>Gets the items key for the caller identity.</summary>
    public const string CallerIdentity = "Marang.CallerIdentity";
}
