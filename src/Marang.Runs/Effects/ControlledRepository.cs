using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Marang.Runs.Effects;

/// <summary>Repository access restricted to regular files in one controlled Git checkout.</summary>
public sealed class ControlledRepository
{
    private const int MaximumPatchBytes = 1_048_576;
    private readonly string _root;
    private readonly HashSet<string> _allowedPaths;

    private ControlledRepository(string root, HashSet<string> allowedPaths)
    {
        _root = root;
        _allowedPaths = allowedPaths;
    }

    public string RepositoryId => RepositoryStateSnapshot.Hash("marang-repository-root-v1\n" +
        (OperatingSystem.IsWindows() ? _root.ToUpperInvariant() : _root));

    public static ControlledRepository Open(string root, IEnumerable<string> allowedTargetPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(allowedTargetPaths);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        ValidateNoReparseAncestors(full);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in allowedTargetPaths)
        {
            ValidateRelativePath(path);
            if (!allowed.Add(path)) throw new ArgumentException("Allowlisted paths must be unique ignoring case.", nameof(allowedTargetPaths));
        }
        if (allowed.Count is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(allowedTargetPaths), "Allowlist one to four existing tracked files.");
        var repository = new ControlledRepository(full, allowed);
        var top = repository.Git("rev-parse", "--show-toplevel").TrimEnd('\r', '\n');
        if (!PathEquals(full, Path.GetFullPath(top))) throw new InvalidOperationException("The selected directory must be the Git checkout root.");
        return repository;
    }

    public async ValueTask<RepositoryStateSnapshot> ObserveAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        return ObserveUnderLock();
    }

    /// <summary>Verifies the required clean starting checkout and captures its initial state under one exclusive lease.</summary>
    public async ValueTask<RepositoryStateSnapshot> RequireCleanInitialAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        RequireCleanInitialUnderLock();
        return ObserveUnderLock();
    }

    internal async ValueTask<RepositoryCheckoutLease> AcquireLockAsync(CancellationToken cancellationToken = default)
    {
        var lockRoot = Path.Combine(Path.GetTempPath(), "marang-repository-locks");
        Directory.CreateDirectory(lockRoot);
        var lockPath = Path.Combine(lockRoot, RepositoryId + ".lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new RepositoryCheckoutLease(new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough));
            }
            catch (IOException) { await Task.Delay(50, cancellationToken).ConfigureAwait(false); }
        }
    }

    public RepositoryStateSnapshot ObserveUnderLock()
    {
        ValidateNoReparseAncestors(_root);
        var head = Git("rev-parse", "HEAD").Trim();
        var indexEntries = Git("ls-files", "--stage", "-z");
        var indexHash = Hash(Encoding.UTF8.GetBytes(indexEntries));
        var tracked = new List<string>();
        foreach (var entry in indexEntries.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('\t');
            if (separator < 0) throw new IOException("Git returned a malformed index entry.");
            var metadata = entry[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length != 3 || metadata[0] is not ("100644" or "100755") || metadata[2] != "0")
                throw new IOException("The controlled repository profile supports only stage-zero regular tracked files.");
            tracked.Add(entry[(separator + 1)..]);
        }
        var untracked = GitNulList("ls-files", "--others", "--exclude-standard");
        var allPaths = tracked.Concat(untracked).ToArray();
        if (allPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != allPaths.Length)
            throw new IOException("Checkout contains paths that collide under portable case-insensitive path rules.");
        var paths = allPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new List<RepositoryFileState>(paths.Count);
        foreach (var path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            ValidateRelativePath(path);
            var full = Resolve(path);
            ValidateNoReparseAncestors(full);
            if (!File.Exists(full)) throw new IOException($"Repository file '{path}' is missing or is not a regular file.");
            var attributes = File.GetAttributes(full);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) != 0)
                throw new IOException($"Repository target '{path}' is not a regular file.");
            files.Add(new(path, Hash(File.ReadAllBytes(full))));
        }
        return RepositoryStateSnapshot.Create(RepositoryId, head, indexHash, files);
    }

    /// <summary>Enforces the qualified clean-checkout starting point. Later effect bases may be dirty when fully observed and authorized.</summary>
    internal void RequireCleanInitialUnderLock()
    {
        if (Git("status", "--porcelain=v1", "--untracked-files=all").Length != 0)
            throw new InvalidOperationException("The initial repository state must have a clean working tree and no meaningful untracked files.");
    }

    internal async ValueTask<RepositoryStateSnapshot> ApplyAsync(RepositoryEffectIntent intent,
        Func<RepositoryEffectIntent, CancellationToken, ValueTask<bool>> durableIntentExists,
        Func<int, CancellationToken, ValueTask>? checkpoint = null, CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        return await ApplyUnderLockAsync(intent, durableIntentExists, checkpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies while the caller holds this repository's checkout lease across reconciliation and admission.</summary>
    internal async ValueTask<RepositoryStateSnapshot> ApplyUnderLockAsync(RepositoryEffectIntent intent,
        Func<RepositoryEffectIntent, CancellationToken, ValueTask<bool>> durableIntentExists,
        Func<int, CancellationToken, ValueTask>? checkpoint = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(durableIntentExists);
        ValidateIntent(intent);
        if (!await durableIntentExists(intent, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Repository mutation requires proof that the exact intent is durable.");
        var observed = ObserveUnderLock();
        if (observed.StateDigest != intent.BaseState.StateDigest)
            throw new InvalidOperationException("Repository state changed since the immutable effect intent was created.");
        if (intent.Files.Sum(f => (long)f.BeforeBytes.Length + f.AfterBytes.Length) > MaximumPatchBytes)
            throw new InvalidOperationException("Patch exceeds the bounded repository profile.");
        var written = 0;
        foreach (var file in intent.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Resolve(file.Path);
            ValidateNoReparseAncestors(target);
            if (!File.Exists(target) || !Hash(File.ReadAllBytes(target)).Equals(file.BeforeSha256, StringComparison.Ordinal))
                throw new InvalidOperationException($"Target '{file.Path}' no longer has the intended before content.");
            var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".marang-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(file.AfterBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, target, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            written++;
            if (checkpoint is not null) await checkpoint(written, cancellationToken).ConfigureAwait(false);
        }
        var after = ObserveUnderLock();
        if (after.StateDigest != intent.ExpectedPostState.StateDigest)
            throw new IOException("The observed result does not match the immutable expected post-state.");
        return after;
    }

    private void ValidateIntent(RepositoryEffectIntent intent)
    {
        if (intent.RepositoryId != RepositoryId || intent.BaseState.RepositoryId != RepositoryId)
            throw new InvalidOperationException("Effect intent is bound to a different repository.");
        if (intent.Files.Length is < 1 or > 4) throw new InvalidOperationException("Invalid patch size.");
        if (!intent.HasValidDigest())
            throw new InvalidOperationException("Effect intent content or identity was changed after creation.");
        var tracked = new HashSet<string>(GitNulList("ls-files", "--cached"), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in intent.Files)
        {
            ValidateRelativePath(file.Path);
            if (!seen.Add(file.Path) || !_allowedPaths.Contains(file.Path)) throw new InvalidOperationException($"Target '{file.Path}' is not uniquely allowlisted.");
            if (!tracked.Contains(file.Path)) throw new InvalidOperationException($"Target '{file.Path}' is not an existing tracked file.");
            if (!intent.BaseState.Files.Any(s => string.Equals(s.Path, file.Path, StringComparison.OrdinalIgnoreCase) && s.Sha256 == file.BeforeSha256))
                throw new InvalidOperationException($"Target '{file.Path}' is not bound to its before content.");
        }
    }

    internal static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains(':') || path.Contains('\\') || path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Paths must be canonical checkout-relative portable paths.", nameof(path));
        var parts = path.Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.Any(char.IsControl)))
            throw new ArgumentException("Path contains an alias, traversal, or non-portable segment.", nameof(path));
        if (parts.Any(p => IsWindowsDeviceName(p.Split('.')[0]))) throw new ArgumentException("Windows device names are not valid targets.", nameof(path));
        if (parts.Any(p => p.Equals(".git", StringComparison.OrdinalIgnoreCase) || p.Equals(".marang", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Git and executor bookkeeping are outside the target scope.", nameof(path));
    }

    private static bool IsWindowsDeviceName(string value) =>
        new[] { "CON", "PRN", "AUX", "NUL" }.Contains(value, StringComparer.OrdinalIgnoreCase) ||
        (value.Length == 4 && (value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && value[3] is >= '1' and <= '9');

    private static void ValidateNoReparseAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Checkout path traverses a symbolic link or reparse point: '{current}'.");
            }
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent ?? string.Empty;
        }
    }

    private string Resolve(string relative) => Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
    private static bool PathEquals(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private List<string> GitNulList(params string[] args) => Git(args.Append("-z").ToArray()).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"Git {args[0]} failed: {stderr.Trim()}");
        return stdout;
    }
}

internal sealed class RepositoryCheckoutLease : IAsyncDisposable
{
    private readonly FileStream _stream;
    internal RepositoryCheckoutLease(FileStream stream) => _stream = stream;
    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
