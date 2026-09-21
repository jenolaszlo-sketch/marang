using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Marang.Mcp;

/// <summary>
/// Connectivity probe. Proves the MCP transport serves tools; delegation
/// tools land here as the Marang runtime is wired up.
/// </summary>
[McpServerToolType]
public static class MarangConnectivityTools
{
    /// <summary>Echoes the message back to the MCP client.</summary>
    [McpServerTool(Name = "marang_ping")]
    [Description("Connectivity probe. Echoes the message back to verify the Marang MCP transport serves tools.")]
    public static string Ping(
        [Description("Message to echo back.")] string message) => $"marang: {message}";
}
