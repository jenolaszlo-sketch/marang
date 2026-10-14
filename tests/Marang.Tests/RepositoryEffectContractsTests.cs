using System.Diagnostics;
using FluentAssertions;
using Marang.Runs.Effects;

namespace Marang.Tests;

public sealed class RepositoryEffectContractsTests
{
    [Fact]
    public async Task Checkout_snapshot_binds_head_index_and_all_regular_working_files()
    {
        using var repo = Checkout.Create();
        var controlled = ControlledRepository.Open(repo.Root, ["src/a.txt", "src/b.txt"]);
        ControlledRepository.Open(repo.Root + Path.DirectorySeparatorChar, ["src/a.txt", "src/b.txt"]).RepositoryId.Should().Be(controlled.RepositoryId);
        var first = await controlled.ObserveAsync(TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(repo.Root, "src", "a.txt"), "changed");
        var second = await controlled.ObserveAsync(TestContext.Current.CancellationToken);
        first.StateDigest.Should().NotBe(second.StateDigest);
        first.HeadIdentity.Should().Be(second.HeadIdentity);
        first.IndexSha256.Should().Be(second.IndexSha256);
        second.Files.Should().Contain(f => f.Path == "src/a.txt");
    }

    [Fact]
    public async Task Clean_initial_gate_accepts_seed_then_rejects_dirty_working_tree()
    {
        using var repo = Checkout.Create();
        var controlled = ControlledRepository.Open(repo.Root, ["src/a.txt"]);
        var initial = await controlled.RequireCleanInitialAsync(TestContext.Current.CancellationToken);
        initial.HeadIdentity.Should().NotBeNullOrWhiteSpace();
        File.WriteAllText(Path.Combine(repo.Root, "src", "a.txt"), "later effect");
        Func<Task> dirty = async () => await controlled.RequireCleanInitialAsync(TestContext.Current.CancellationToken);
        await dirty.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Reconciler_classifies_base_post_partial_and_unrelated_state()
    {
        using var repo = Checkout.Create();
        var controlled = ControlledRepository.Open(repo.Root, ["src/a.txt", "src/b.txt"]);
        var basis = await controlled.ObserveAsync(TestContext.Current.CancellationToken);
        var intent = RepositoryEffectIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "r1", "fix", Guid.NewGuid(), "attempt-1",
            basis.RepositoryId, "src", basis,
            [RepositoryPatchFile.Create("src/a.txt", "old-a"u8, "new-a"u8), RepositoryPatchFile.Create("src/b.txt", "old-b"u8, "new-b"u8)],
            DateTimeOffset.UtcNow);
        RepositoryEffectReconciler.Classify(intent, basis).Should().Be(RepositoryEffectClassification.DefinitelyNotApplied);
        RepositoryEffectReconciler.Classify(intent, intent.ExpectedPostState).Should().Be(RepositoryEffectClassification.DefinitelyApplied);

        var partialFiles = basis.Files.Select(f => f.Path == "src/a.txt"
            ? new RepositoryFileState(f.Path, intent.Files[0].AfterSha256) : f).ToArray();
        var partial = RepositoryStateSnapshot.Create(basis.RepositoryId, basis.HeadIdentity, basis.IndexSha256, partialFiles);
        RepositoryEffectReconciler.Classify(intent, partial).Should().Be(RepositoryEffectClassification.PartiallyApplied);

        var unrelated = RepositoryStateSnapshot.Create(basis.RepositoryId, basis.HeadIdentity, basis.IndexSha256,
            basis.Files.Select(f => f.Path == "src/a.txt" ? new RepositoryFileState(f.Path, new string('0', 64)) : f));
        RepositoryEffectReconciler.Classify(intent, unrelated).Should().Be(RepositoryEffectClassification.Unresolved);
        var changedHead = RepositoryStateSnapshot.Create(basis.RepositoryId, new string('0', 40), basis.IndexSha256, basis.Files);
        RepositoryEffectReconciler.Classify(intent, changedHead).Should().Be(RepositoryEffectClassification.Unresolved);
        var changedIndex = RepositoryStateSnapshot.Create(basis.RepositoryId, basis.HeadIdentity, new string('0', 64), basis.Files);
        RepositoryEffectReconciler.Classify(intent, changedIndex).Should().Be(RepositoryEffectClassification.Unresolved);

        File.WriteAllText(Path.Combine(repo.Root, "unexpected.txt"), "outside effect");
        var withUnrelatedUntracked = await controlled.ObserveAsync(TestContext.Current.CancellationToken);
        RepositoryEffectReconciler.Classify(intent, withUnrelatedUntracked).Should().Be(RepositoryEffectClassification.Unresolved);
    }

    [Fact]
    public async Task Patch_identity_covers_revision_scope_and_content_and_rejects_record_tampering()
    {
        using var repo = Checkout.Create();
        var controlled = ControlledRepository.Open(repo.Root, ["src/a.txt"]);
        var basis = await controlled.ObserveAsync(TestContext.Current.CancellationToken);
        var original = Intent(basis);
        var otherRevision = RepositoryEffectIntent.Create(original.EffectId, original.WorkflowId, "r2", original.LogicalStepId,
            original.ExecutionId, original.AttemptId, original.RepositoryId, original.AuthorityScope, basis, original.Files, original.CreatedAt);
        var otherScope = RepositoryEffectIntent.Create(original.EffectId, original.WorkflowId, original.RevisionId, original.LogicalStepId,
            original.ExecutionId, original.AttemptId, original.RepositoryId, "other-scope", basis, original.Files, original.CreatedAt);
        otherRevision.PatchIdentity.Should().NotBe(original.PatchIdentity);
        otherScope.PatchIdentity.Should().NotBe(original.PatchIdentity);
        var altered = original with { Files = [RepositoryPatchFile.Create("src/a.txt", "old"u8, "tampered"u8)] };
        Func<Task> threw = async () => await controlled.ApplyAsync(altered, (_, _) => ValueTask.FromResult(true), cancellationToken: TestContext.Current.CancellationToken);
        await threw.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Apply_requires_durable_intent_and_exact_base_then_flushes_and_replaces()
    {
        using var repo = Checkout.Create();
        var controlled = ControlledRepository.Open(repo.Root, ["src/a.txt"]);
        var basis = await controlled.ObserveAsync(TestContext.Current.CancellationToken);
        var intent = Intent(basis);
        Func<Task> refused = async () => await controlled.ApplyAsync(intent, (_, _) => ValueTask.FromResult(false), cancellationToken: TestContext.Current.CancellationToken);
        await refused.Should().ThrowAsync<InvalidOperationException>();
        File.ReadAllText(Path.Combine(repo.Root, "src", "a.txt")).Should().Be("old-a");

        var checkpoints = new List<int>();
        var result = await controlled.ApplyAsync(intent, (_, _) => ValueTask.FromResult(true), (count, _) =>
        {
            checkpoints.Add(count);
            return ValueTask.CompletedTask;
        }, TestContext.Current.CancellationToken);
        File.ReadAllText(Path.Combine(repo.Root, "src", "a.txt")).Should().Be("new-a");
        result.StateDigest.Should().Be(intent.ExpectedPostState.StateDigest);
        checkpoints.Should().Equal(1);
    }

    [Fact]
    public async Task Changed_base_and_reparse_targets_are_refused()
    {
        using var repo = Checkout.Create();
        var controlled = ControlledRepository.Open(repo.Root, ["src/a.txt"]);
        var basis = await controlled.ObserveAsync(TestContext.Current.CancellationToken);
        var intent = Intent(basis);
        File.WriteAllText(Path.Combine(repo.Root, "src", "a.txt"), "outside edit");
        Func<Task> stale = async () => await controlled.ApplyAsync(intent, (_, _) => ValueTask.FromResult(true), cancellationToken: TestContext.Current.CancellationToken);
        await stale.Should().ThrowAsync<InvalidOperationException>();

        var link = Path.Combine(repo.Root, "src", "link.txt");
        try
        {
            File.CreateSymbolicLink(link, Path.Combine(repo.Root, "src", "a.txt"));
        }
        catch (UnauthorizedAccessException) { return; }
        catch (PlatformNotSupportedException) { return; }
        catch (IOException) { return; }
        var linked = ControlledRepository.Open(repo.Root, ["src/a.txt"]);
        Func<Task> observe = async () => await linked.ObserveAsync(TestContext.Current.CancellationToken);
        await observe.Should().ThrowAsync<IOException>();
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("src/../a.txt")]
    [InlineData("src/CON.txt")]
    [InlineData(".git/config")]
    [InlineData("src/name.")]
    [InlineData("C:/escape")]
    public void Portable_target_path_rejects_aliases(string path)
    {
        var act = () => ControlledRepository.ValidateRelativePath(path);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Allowlist_rejects_case_insensitive_duplicates()
    {
        using var repo = Checkout.Create();
        var act = () => ControlledRepository.Open(repo.Root, ["src/a.txt", "SRC/A.TXT"]);
        act.Should().Throw<ArgumentException>();
    }

    private static RepositoryEffectIntent Intent(RepositoryStateSnapshot basis)
    {
        var oldA = "old-a"u8;
        return RepositoryEffectIntent.Create(Guid.NewGuid(), Guid.NewGuid(), "r1", "fix", Guid.NewGuid(), "attempt-1",
            basis.RepositoryId, "src/a.txt", basis, [RepositoryPatchFile.Create("src/a.txt", oldA, "new-a"u8)], DateTimeOffset.UtcNow);
    }

    private sealed class Checkout : IDisposable
    {
        public string Root { get; }
        private Checkout(string root) => Root = root;

        public static Checkout Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "marang-effect-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "src", "a.txt"), "old-a");
            File.WriteAllText(Path.Combine(root, "src", "b.txt"), "old-b");
            Git(root, "init", "-q");
            Git(root, "config", "user.email", "test@example.invalid");
            Git(root, "config", "user.name", "Marang Test");
            Git(root, "add", "src/a.txt", "src/b.txt");
            Git(root, "commit", "-qm", "base");
            return new(root);
        }

        private static void Git(string root, params string[] args)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException(error);
        }

        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch { } }
    }
}
