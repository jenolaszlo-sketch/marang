using Penghou.Hufu;
using Penghou.Hufu.Biscuit;

namespace Marang.Hufu;

/// <summary>Credential carrier kinds accepted at the Marang boundary.</summary>
public enum MarangDelegationCredentialKind
{
    /// <summary>A serialized Biscuit credential envelope.</summary>
    Biscuit = 0,
}

/// <summary>
/// A bounded delegation credential carried in HTTP metadata. It is host-issued
/// material, never a tool argument, and possession alone grants nothing.
/// </summary>
public sealed class MarangDelegationCredential
{
    public MarangDelegationCredential(MarangDelegationCredentialKind kind, string identity, byte[] credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (!Enum.IsDefined(kind)) throw new ArgumentException("A known credential kind is required.", nameof(kind));
        if (!AuthorityValidation.ValidToken(identity))
            throw new ArgumentException("A bounded credential identity is required.", nameof(identity));
        if (credential.Length is < 1 or > 65_536)
            throw new ArgumentException("A bounded credential payload is required.", nameof(credential));
        Kind = kind;
        Identity = identity;
        Bytes = (byte[])credential.Clone();
    }

    public MarangDelegationCredentialKind Kind { get; }
    public string Identity { get; }
    public byte[] Bytes { get; }
    public override string ToString() => $"MarangDelegationCredential({Kind}; credential redacted)";

    /// <summary>
    /// Encodes the credential as a bounded HTTP metadata value:
    /// <c>&lt;kind&gt;.&lt;identity&gt;.&lt;base64url(bytes)&gt;</c>. Never a business payload.
    /// </summary>
    public string Encode() => (int)Kind + "." + Identity + "." + Convert.ToBase64String(Bytes);

    /// <summary>Parses a header value into a credential, or returns null when absent/malformed.</summary>
    public static MarangDelegationCredential? TryDecode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 90_000) return null;
        var first = value.IndexOf('.');
        if (first <= 0) return null;
        var second = value.IndexOf('.', first + 1);
        if (second <= first + 1) return null;
        if (!int.TryParse(value.AsSpan(0, first), out var kind) || !Enum.IsDefined((MarangDelegationCredentialKind)kind)) return null;
        var identity = value[(first + 1)..second];
        try
        {
            var bytes = Convert.FromBase64String(value[(second + 1)..]);
            return new MarangDelegationCredential((MarangDelegationCredentialKind)kind, identity, bytes);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Credential-only verification outcome. Never an authorization result.</summary>
public enum MarangDelegationCredentialStatus { Missing, Invalid, Verified }

/// <summary>
/// Credential-only verification. Proves the presented credential is genuine and
/// well-formed for the caller. It does not decide whether the bounded work may
/// be accepted; that is the Hufu acceptance decision's job.
/// </summary>
public interface IMarangDelegationCredentialVerifier
{
    ValueTask<MarangDelegationCredentialStatus> VerifyAsync(
        MarangDelegationCredential? credential, string caller, CancellationToken cancellationToken = default);
}

/// <summary>
/// First-profile verifier: the credential must be a bounded Biscuit envelope.
/// Cryptographic signature/registry verification is a host obligation supplied
/// by an alternative <see cref="IMarangDelegationCredentialVerifier"/>; this
/// verifier deliberately performs no native invocation.
/// </summary>
public sealed class BiscuitDelegationCredentialVerifier : IMarangDelegationCredentialVerifier
{
    public ValueTask<MarangDelegationCredentialStatus> VerifyAsync(
        MarangDelegationCredential? credential, string caller, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (credential is null) return new(MarangDelegationCredentialStatus.Missing);
        if (!AuthorityValidation.ValidToken(caller)) return new(MarangDelegationCredentialStatus.Missing);
        if (credential.Kind != MarangDelegationCredentialKind.Biscuit) return new(MarangDelegationCredentialStatus.Invalid);
        try
        {
            _ = new BiscuitEnvelope(credential.Identity, credential.Bytes);
            return new(MarangDelegationCredentialStatus.Verified);
        }
        catch (ArgumentException)
        {
            return new(MarangDelegationCredentialStatus.Invalid);
        }
    }
}

/// <summary>Host-configured acceptance policy bounds. Never caller-supplied.</summary>
public sealed record MarangDelegationAcceptancePolicy
{
    /// <summary>Callers (stable subjects) permitted to accept delegated work.</summary>
    public IReadOnlySet<string> AllowedCallers { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Workspace ceilings on which delegated work may be accepted.</summary>
    public IReadOnlySet<string> AllowedWorkspaces { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Providers on which delegated work may be accepted.</summary>
    public IReadOnlySet<string> AllowedProviders { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Host policy for one acceptance request. It evaluates the caller's live
/// current-authority snapshot and the host-configured acceptance bounds. It does
/// not consult resource/workspace grants: acceptance authority is its own
/// semantic.
/// </summary>
public sealed class HufuDelegationAcceptanceEvaluator : IDelegationAcceptanceEvaluator
{
    private readonly MarangDelegationAcceptancePolicy _policy;

    public HufuDelegationAcceptanceEvaluator(MarangDelegationAcceptancePolicy policy) =>
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));

    public AuthorityDecision Evaluate(AuthoritySnapshot snapshot, DelegationAcceptanceRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        var subject = request.Context.SubjectId;
        var permitted = snapshot.Context == request.Context && snapshot.ValidUntil > now &&
            _policy.AllowedCallers.Contains(subject) &&
            _policy.AllowedWorkspaces.Contains(request.WorkspaceId) &&
            _policy.AllowedProviders.Contains(request.Provider);
        return new AuthorityDecision(
            permitted ? AuthorityStatus.Permit : AuthorityStatus.Deny,
            permitted ? "marang.acceptance-permitted" : "marang.acceptance-denied",
            snapshot.Version,
            "marang-acceptance-evaluator-v1",
            snapshot.Identity);
    }
}

/// <summary>
/// Acceptance evidence recorder. Records the exact acceptance request and its
/// current-authority decision through a host sink; a null/false result blocks
/// acceptance. Acceptance evidence is distinct from resource decision evidence.
/// </summary>
public sealed class HufuDelegationAcceptanceRecorder : IDelegationAcceptanceRecorder
{
    private readonly Func<DelegationAcceptanceRequest, AuthorityDecision, CancellationToken, ValueTask<bool>> _sink;

    public HufuDelegationAcceptanceRecorder(
        Func<DelegationAcceptanceRequest, AuthorityDecision, CancellationToken, ValueTask<bool>> sink) =>
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    public ValueTask<bool> RecordAsync(DelegationAcceptanceRequest request, AuthorityDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decision);
        return _sink(request, decision, cancellationToken);
    }

    /// <summary>Convenience in-memory sink for hosts and tests.</summary>
    public static HufuDelegationAcceptanceRecorder InMemory(List<(DelegationAcceptanceRequest Request, AuthorityDecision Decision)> entries) =>
        new((request, decision, _) =>
        {
            lock (entries) entries.Add((request, decision));
            return ValueTask.FromResult(true);
        });
}
