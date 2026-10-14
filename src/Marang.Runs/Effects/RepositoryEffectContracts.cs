using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Marang.Tests")]

namespace Marang.Runs.Effects;

public sealed record RepositoryFileState(string Path, string Sha256);

/// <summary>A deterministic identity of the bounded checkout profile. Git metadata and ignored build output are excluded.</summary>
public sealed record RepositoryStateSnapshot(
    string RepositoryId,
    string HeadIdentity,
    string IndexSha256,
    ImmutableArray<RepositoryFileState> Files,
    string StateDigest)
{
    public static RepositoryStateSnapshot Create(string repositoryId, string headIdentity, string indexSha256,
        IEnumerable<RepositoryFileState> files)
    {
        if (string.IsNullOrWhiteSpace(repositoryId) || !IsSha256(repositoryId)) throw new ArgumentException("Repository identity must be a SHA-256 digest.", nameof(repositoryId));
        if (headIdentity.Length is not (40 or 64) || !headIdentity.All(Uri.IsHexDigit)) throw new ArgumentException("HEAD identity must be a Git object id.", nameof(headIdentity));
        if (!IsSha256(indexSha256)) throw new ArgumentException("Index identity must be a SHA-256 digest.", nameof(indexSha256));
        var ordered = files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        if (ordered.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ordered.Length)
            throw new ArgumentException("Repository state paths must be unique ignoring case.", nameof(files));
        foreach (var file in ordered)
        {
            ControlledRepository.ValidateRelativePath(file.Path);
            if (!IsSha256(file.Sha256)) throw new ArgumentException($"File identity for '{file.Path}' must be a SHA-256 digest.", nameof(files));
        }
        var canonical = new StringBuilder("marang-repository-state-v1\n").Append(repositoryId).Append('\n')
            .Append(headIdentity).Append('\n').Append(indexSha256).Append('\n');
        foreach (var file in ordered) canonical.Append(file.Path).Append('\0').Append(file.Sha256).Append('\n');
        return new(repositoryId, headIdentity, indexSha256, ordered, Hash(canonical.ToString()));
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static bool IsSha256(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public bool HasValidDigest()
    {
        try { return Create(RepositoryId, HeadIdentity, IndexSha256, Files).StateDigest == StateDigest; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (NullReferenceException) { return false; }
    }
}

public sealed record RepositoryPatchFile(string Path, ImmutableArray<byte> BeforeBytes, ImmutableArray<byte> AfterBytes)
{
    public string BeforeSha256 => Convert.ToHexString(SHA256.HashData(BeforeBytes.AsSpan())).ToLowerInvariant();
    public string AfterSha256 => Convert.ToHexString(SHA256.HashData(AfterBytes.AsSpan())).ToLowerInvariant();

    public static RepositoryPatchFile Create(string path, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after) =>
        new(path, ImmutableArray.CreateRange(before.ToArray()), ImmutableArray.CreateRange(after.ToArray()));
}

public sealed record RepositoryEffectIntent(
    Guid EffectId,
    Guid WorkflowId,
    string RevisionId,
    string LogicalStepId,
    Guid ExecutionId,
    string AttemptId,
    string RepositoryId,
    string AuthorityScope,
    RepositoryStateSnapshot BaseState,
    ImmutableArray<RepositoryPatchFile> Files,
    RepositoryStateSnapshot ExpectedPostState,
    DateTimeOffset CreatedAt,
    string PatchIdentity,
    string IntentDigest)
{
    public static RepositoryEffectIntent Create(Guid effectId, Guid workflowId, string revisionId, string logicalStepId,
        Guid executionId, string attemptId, string repositoryId, string authorityScope, RepositoryStateSnapshot baseState,
        IEnumerable<RepositoryPatchFile> files, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(baseState);
        if (!baseState.HasValidDigest()) throw new ArgumentException("Base state snapshot digest is invalid.", nameof(baseState));
        RequireIdentity(effectId, nameof(effectId));
        RequireIdentity(workflowId, nameof(workflowId));
        RequireIdentity(executionId, nameof(executionId));
        RequireText(revisionId, nameof(revisionId));
        RequireText(logicalStepId, nameof(logicalStepId));
        RequireText(attemptId, nameof(attemptId));
        RequireText(authorityScope, nameof(authorityScope));
        if (createdAt == default) throw new ArgumentException("Intent creation time is required.", nameof(createdAt));
        var patchFiles = files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        if (patchFiles.Length is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(files), "A patch must replace one to four existing files.");
        if (patchFiles.Sum(f => (long)f.BeforeBytes.Length + f.AfterBytes.Length) > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(files), "A patch is limited to 1 MiB of before and after content.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in patchFiles)
        {
            ControlledRepository.ValidateRelativePath(file.Path);
            if (!names.Add(file.Path)) throw new ArgumentException("Patch paths must be unique ignoring case.", nameof(files));
            if (file.BeforeSha256 == file.AfterSha256) throw new ArgumentException("A replacement must change file content.", nameof(files));
            if (!baseState.Files.Any(s => string.Equals(s.Path, file.Path, StringComparison.OrdinalIgnoreCase) && s.Sha256 == file.BeforeSha256))
                throw new ArgumentException($"Base state does not contain matching file content for '{file.Path}'.", nameof(files));
        }
        if (baseState.RepositoryId != repositoryId) throw new ArgumentException("Base state belongs to a different repository.", nameof(baseState));
        var postFiles = baseState.Files.Select(f =>
        {
            var replacement = patchFiles.FirstOrDefault(p => string.Equals(p.Path, f.Path, StringComparison.OrdinalIgnoreCase));
            return replacement is null ? f : new RepositoryFileState(f.Path, replacement.AfterSha256);
        }).ToArray();
        var post = RepositoryStateSnapshot.Create(repositoryId, baseState.HeadIdentity, baseState.IndexSha256, postFiles);
        var patchCanonical = new StringBuilder("marang-repository-patch-v1\n").Append(effectId).Append('\n').Append(workflowId)
            .Append('\n').Append(revisionId).Append('\n').Append(logicalStepId).Append('\n').Append(executionId).Append('\n')
            .Append(attemptId).Append('\n').Append(repositoryId).Append('\n').Append(authorityScope).Append('\n').Append(baseState.StateDigest).Append('\n');
        foreach (var file in patchFiles)
            patchCanonical.Append(file.Path).Append('\0').Append(file.BeforeSha256).Append('\0').Append(file.AfterSha256).Append('\n');
        var patchIdentity = RepositoryStateSnapshot.Hash(patchCanonical.ToString());
        var intentCanonical = $"marang-repository-intent-v1\n{patchIdentity}\n{createdAt.ToUniversalTime():O}\n{post.StateDigest}";
        return new(effectId, workflowId, revisionId, logicalStepId, executionId, attemptId, repositoryId, authorityScope,
            baseState, patchFiles, post, createdAt, patchIdentity, RepositoryStateSnapshot.Hash(intentCanonical));
    }

    private static void RequireIdentity(Guid value, string parameter)
    {
        if (value == Guid.Empty) throw new ArgumentException("A stable non-empty identifier is required.", parameter);
    }

    private static void RequireText(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl) || value != value.Trim())
            throw new ArgumentException("A canonical non-empty value of at most 256 characters is required.", parameter);
    }

    public bool HasValidDigest()
    {
        try
        {
            var canonical = Create(EffectId, WorkflowId, RevisionId, LogicalStepId, ExecutionId, AttemptId,
                RepositoryId, AuthorityScope, BaseState, Files, CreatedAt);
            return ExpectedPostState.HasValidDigest() && canonical.PatchIdentity == PatchIdentity && canonical.IntentDigest == IntentDigest &&
                canonical.ExpectedPostState.StateDigest == ExpectedPostState.StateDigest;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (NullReferenceException) { return false; }
    }
}

public enum RepositoryEffectClassification { DefinitelyNotApplied, DefinitelyApplied, PartiallyApplied, Unresolved }

public static class RepositoryEffectReconciler
{
    public static RepositoryEffectClassification Classify(RepositoryEffectIntent intent, RepositoryStateSnapshot observed)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(observed);
        if (!intent.HasValidDigest() || !intent.BaseState.HasValidDigest() || !intent.ExpectedPostState.HasValidDigest() || !observed.HasValidDigest())
            return RepositoryEffectClassification.Unresolved;
        if (intent.RepositoryId != observed.RepositoryId) return RepositoryEffectClassification.Unresolved;
        if (intent.BaseState.StateDigest == observed.StateDigest) return RepositoryEffectClassification.DefinitelyNotApplied;
        if (intent.ExpectedPostState.StateDigest == observed.StateDigest) return RepositoryEffectClassification.DefinitelyApplied;
        if (SameEnvelope(intent.BaseState, observed) && TargetsAreOnlyBeforeOrAfter(intent, observed))
        {
            var hashes = observed.Files.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.OrdinalIgnoreCase);
            var after = intent.Files.Count(f => hashes.TryGetValue(f.Path, out var hash) && hash == f.AfterSha256);
            var before = intent.Files.Count(f => hashes.TryGetValue(f.Path, out var hash) && hash == f.BeforeSha256);
            if (after > 0 && before > 0) return RepositoryEffectClassification.PartiallyApplied;
        }
        return RepositoryEffectClassification.Unresolved;
    }

    private static bool SameEnvelope(RepositoryStateSnapshot basis, RepositoryStateSnapshot observed) =>
        basis.HeadIdentity == observed.HeadIdentity && basis.IndexSha256 == observed.IndexSha256 && basis.Files.Length == observed.Files.Length;

    private static bool TargetsAreOnlyBeforeOrAfter(RepositoryEffectIntent intent, RepositoryStateSnapshot observed)
    {
        var expected = intent.BaseState.Files.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.OrdinalIgnoreCase);
        var actual = observed.Files.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.OrdinalIgnoreCase);
        if (!expected.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(actual.Keys)) return false;
        foreach (var file in intent.Files)
        {
            if (!actual.TryGetValue(file.Path, out var hash) || (hash != file.BeforeSha256 && hash != file.AfterSha256)) return false;
            expected[file.Path] = hash;
        }
        return expected.All(pair => actual.TryGetValue(pair.Key, out var hash) && hash == pair.Value);
    }
}
