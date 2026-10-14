using Marang.Hufu;
using Penghou.Hufu;

namespace Marang.Runs.Effects;

public interface IRepositoryEffectManifestVerifier
{
    /// <summary>Confirms the active admitted Fuwen manifest binds this exact execution, step, intent and patch.</summary>
    ValueTask<bool> IsAdmittedAsync(RepositoryEffectIntent intent, CancellationToken cancellationToken = default);
    /// <summary>Confirms the intent's workflow revision is still active and can perform a new write.</summary>
    ValueTask<bool> IsCurrentAsync(RepositoryEffectIntent intent, CancellationToken cancellationToken = default);
}

public sealed record RepositoryEffectAuthorityDecision(
    bool Allowed,
    string ReasonCode,
    string IntentDigest,
    string TargetPath,
    string RequestIdentity,
    AuthorityStatus? Status,
    AuthorityDecision? Decision,
    RepositoryEffectAuthorizationEvidence? Evidence = null,
    IReadOnlyList<RepositoryEffectAuthorizationEvidence>? EvidenceSet = null);

/// <summary>Fresh Hufu write checks bound to one exact effect digest and one target path.</summary>
public sealed class RepositoryEffectAuthority(
    IAuthorityRequestAuthorizer authorizer,
    IRepositoryEffectManifestVerifier manifestVerifier,
    string workspaceId,
    TimeProvider? clock = null)
{
    internal ValueTask<bool> IsCurrentRevisionAsync(RepositoryEffectIntent intent, CancellationToken cancellationToken = default) =>
        manifestVerifier.IsCurrentAsync(intent, cancellationToken);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public async ValueTask<RepositoryEffectAuthorityDecision> AuthorizeAsync(RepositoryEffectIntent intent,
        AuthenticatedAuthorityContext context, string targetPath, bool requireCurrentRevision = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (!intent.HasValidDigest()) return Denied(intent, targetPath, "repository-effect.intent-digest-invalid");
        if (!string.Equals(workspaceId, intent.AuthorityScope, StringComparison.Ordinal))
            return Denied(intent, targetPath, "repository-effect.scope-mismatch");
        if (context.RunId != intent.WorkflowId.ToString("D") || context.RevisionId != intent.RevisionId)
            return Denied(intent, targetPath, "repository-effect.context-mismatch");
        if (!intent.Files.Any(file => string.Equals(file.Path, targetPath, StringComparison.OrdinalIgnoreCase)))
            return Denied(intent, targetPath, "repository-effect.target-outside-intent");
        bool admitted;
        bool current;
        try
        {
            admitted = await manifestVerifier.IsAdmittedAsync(intent, cancellationToken).ConfigureAwait(false);
            current = !requireCurrentRevision || await manifestVerifier.IsCurrentAsync(intent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Denied(intent, targetPath, "repository-effect.manifest-unavailable"); }
        if (!admitted) return Denied(intent, targetPath, "repository-effect.manifest-binding-mismatch");
        if (!current) return Denied(intent, targetPath, "repository-effect.revision-superseded");

        var requestIdentity = RepositoryStateSnapshot.Hash($"marang-repository-write-authority-v1\n{intent.IntentDigest}\n{targetPath}\nReplaceTrackedFile");
        string normalizedPath;
        try { normalizedPath = AuthorityValidation.NormalizePath(targetPath); }
        catch (ArgumentException) { return Denied(intent, targetPath, "repository-effect.path-normalization-failed", requestIdentity); }
        catch { return Denied(intent, targetPath, "repository-effect.path-normalization-failed", requestIdentity); }
        var request = new AuthorityRequest(context, AuthorityAction.WriteFile, workspaceId, normalizedPath, requestIdentity);
        try
        {
            var result = await authorizer.AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
            if (result is null || result.Request != request)
                return new(false, "repository-effect.hufu-response-mismatch", intent.IntentDigest, targetPath,
                    request.RequestIdentity, result?.Status, result?.Decision);
            var evidence = new RepositoryEffectAuthorizationEvidence(intent.IntentDigest, targetPath, request.RequestIdentity,
                result.Status.ToString(), context.TenantId, context.SubjectId, context.RunId, context.RevisionId,
                result.Decision?.ReasonCode, result.Decision?.SnapshotIdentity,
                result.Decision?.SnapshotVersion, _clock.GetUtcNow());
            var permitted = result.IsAuthorized && result.Status == AuthorityStatus.Permit && result.Decision is not null;
            return new(permitted,
                permitted ? "repository-effect.permitted" : "repository-effect.hufu-denied",
                intent.IntentDigest, targetPath, request.RequestIdentity, result.Status, result.Decision, evidence);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Denied(intent, targetPath, "repository-effect.hufu-unavailable", request.RequestIdentity); }
    }

    private static RepositoryEffectAuthorityDecision Denied(RepositoryEffectIntent intent, string path, string reason,
        string? requestIdentity = null) => new(false, reason, intent.IntentDigest, path,
            requestIdentity ?? intent.IntentDigest, null, null);
}
