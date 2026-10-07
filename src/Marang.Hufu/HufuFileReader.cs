using Penghou.Hufu;

namespace Marang.Hufu;

/// <summary>
/// Host-owned filesystem read behind Hufu admission. The host binds the
/// authority context, workspace identity, and file accessor once; every call
/// builds a fresh <see cref="AuthorityRequest"/> and asks
/// <see cref="IAuthorityRequestAuthorizer"/> before touching the filesystem.
/// A prior decision is never reused, and tool output carries no authority.
/// </summary>
/// <remarks>
/// Known production limitation: the canonical path used for admission is
/// also used for filesystem access, but Hufu canonicalization lowercases
/// while case-sensitive stores distinguish e.g. <c>src/Foo.cs</c> from
/// <c>src/foo.cs</c>. Longer term, host resource resolution must produce
/// both a physical resource identity and a Hufu authority identity with the
/// guarantee that the same resolved resource is authorized and accessed.
/// Whether that means Hufu stops lowercasing or workspace providers declare
/// path-comparison semantics belongs to the resource-abstraction work, not
/// to this adapter.
/// </remarks>
public sealed class HufuFileReader
{
    private readonly IAuthorityRequestAuthorizer _authorizer;
    private readonly AuthenticatedAuthorityContext _context;
    private readonly string _workspaceId;
    private readonly Func<string, CancellationToken, ValueTask<string>> _readFile;

    public HufuFileReader(
        IAuthorityRequestAuthorizer authorizer,
        AuthenticatedAuthorityContext context,
        string workspaceId,
        Func<string, CancellationToken, ValueTask<string>> readFile)
    {
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(readFile);
        _authorizer = authorizer;
        _context = context;
        _workspaceId = workspaceId;
        _readFile = readFile;
    }

    /// <summary>
    /// Reads a workspace-relative file after a fresh admission decision.
    /// Malformed paths throw; denied and unavailable outcomes return before
    /// any filesystem access.
    /// </summary>
    public async ValueTask<HufuFileReadResult> ReadFileAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string canonical;
        try
        {
            canonical = AuthorityValidation.NormalizePath(relativePath);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "A bounded canonical workspace-relative path is required.",
                nameof(relativePath),
                exception);
        }

        var request = new AuthorityRequest(
            _context,
            AuthorityAction.ReadFile,
            _workspaceId,
            canonical,
            Guid.NewGuid().ToString("N"));
        var authorization = await _authorizer.AuthorizeAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (authorization is { IsAuthorized: true, Decision: { } decision })
        {
            var content = await _readFile(canonical, cancellationToken)
                .ConfigureAwait(false);
            return new FileReadAllowed(
                _workspaceId,
                canonical,
                content,
                request.RequestIdentity,
                decision.SnapshotIdentity,
                decision.SnapshotVersion);
        }

        if (authorization.Status == AuthorityStatus.Deny)
        {
            return new FileReadDenied(
                _workspaceId,
                canonical,
                authorization.Decision?.ReasonCode ?? "authority.denied",
                authorization.Decision?.SnapshotVersion,
                authorization.Decision?.SnapshotIdentity);
        }

        return new FileReadUnavailable(
            _workspaceId,
            canonical,
            authorization.Decision?.ReasonCode ?? "authority-unavailable");
    }
}

/// <summary>
/// The factual outcome of one file-read attempt. Plain data only: workspace
/// and path strings, content, and the admission evidence that decided it.
/// No member confers authority and none wraps a trusted resource.
/// </summary>
public abstract record HufuFileReadResult(string WorkspaceId, string RelativePath);

/// <summary>An admitted read: content plus the evidence that admitted it.</summary>
public sealed record FileReadAllowed(
    string WorkspaceId,
    string RelativePath,
    string Content,
    string RequestIdentity,
    string SnapshotIdentity,
    string SnapshotVersion) : HufuFileReadResult(WorkspaceId, RelativePath);

/// <summary>A denied read: what was asked and why Hufu refused.</summary>
public sealed record FileReadDenied(
    string WorkspaceId,
    string RelativePath,
    string ReasonCode,
    string? SnapshotVersion,
    string? SnapshotIdentity) : HufuFileReadResult(WorkspaceId, RelativePath);

/// <summary>
/// No decision was reachable: missing snapshot, recording failure, or an
/// invalid evaluation. The filesystem was not touched.
/// </summary>
public sealed record FileReadUnavailable(
    string WorkspaceId,
    string RelativePath,
    string Detail) : HufuFileReadResult(WorkspaceId, RelativePath);
