using Penghou.Qingniao;
using Penghou.Qingniao.Codex;

namespace Marang.Codex;

/// <summary>
/// Host-owned registration of the published Codex execution adapter as a
/// Qingniao provider. Marang core stays provider-neutral: all Codex-specific
/// configuration lives here. The published adapter is single-use per
/// configured prompt (one admitted objective), so this registers exactly one
/// bounded Codex objective rather than a shared multi-objective adapter.
/// </summary>
public static class CodexProviderRegistration
{
    /// <summary>Provider identity the runtime resolves for Codex delegations.</summary>
    public const string ProviderName = "codex";

    /// <summary>Execution capability advertised by the Codex provider.</summary>
    public const string ExecutionCapability = "agent.execute";

    /// <summary>Builds the provider descriptor advertised to the runtime.</summary>
    public static ProviderDescriptor CreateDescriptor() =>
        new(ProviderName, [new CapabilityDescriptor(ExecutionCapability, 1)]);

    /// <summary>
    /// Registers the Codex provider descriptor and a single-use adapter into
    /// the host's existing provider-selection mechanism, and returns the
    /// adapter for diagnostics.
    /// </summary>
    public static CodexExecAdapter Register(
        IProviderRegistry providers,
        InMemoryExternalOperationProviderCatalog adapters,
        CodexExecOptions options,
        ICodexProcessFactory? processFactory = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(options);

        var descriptor = CreateDescriptor();
        providers.Register(descriptor);
        var adapter = new CodexExecAdapter(options, processFactory, clock);
        adapters.Register(descriptor, adapter);
        return adapter;
    }
}
