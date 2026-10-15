using System.Diagnostics;
using Marang.Runs.Effects;

namespace Marang.Tests;

public sealed class RepositoryPortabilityTests
{
    [Fact]
    public async Task Distinct_case_sensitive_git_roots_have_distinct_repository_identities()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var parent = Path.Combine(Path.GetTempPath(), "m5-case-" + Guid.NewGuid().ToString("N")[..12]);
        var lowerRoot = Path.Combine(parent, "repo");
        var upperRoot = Path.Combine(parent, "REPO");
        Directory.CreateDirectory(lowerRoot);
        try
        {
            var probe = Path.Combine(lowerRoot, ".case-sensitivity-probe");
            await File.WriteAllTextAsync(probe, "lower", cancellationToken);
            Directory.CreateDirectory(upperRoot);

            if (File.Exists(Path.Combine(upperRoot, ".case-sensitivity-probe")))
                Assert.Skip("The temporary filesystem aliases case-only directory names.");
            File.Delete(probe);

            Directory.CreateDirectory(Path.Combine(lowerRoot, "src"));
            Directory.CreateDirectory(Path.Combine(upperRoot, "src"));
            await File.WriteAllTextAsync(Path.Combine(lowerRoot, "src", "tracked.txt"), "lower checkout", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(upperRoot, "src", "tracked.txt"), "upper checkout", cancellationToken);
            await InitializeGitCheckoutAsync(lowerRoot, cancellationToken);
            await InitializeGitCheckoutAsync(upperRoot, cancellationToken);

            var lower = ControlledRepository.Open(lowerRoot, ["src/tracked.txt"]);
            var upper = ControlledRepository.Open(upperRoot, ["src/tracked.txt"]);
            var lowerState = await lower.ObserveAsync(cancellationToken);
            var upperState = await upper.ObserveAsync(cancellationToken);

            Assert.NotEqual(
                lowerState.Files.Single(file => file.Path == "src/tracked.txt").Sha256,
                upperState.Files.Single(file => file.Path == "src/tracked.txt").Sha256);
            Assert.NotEqual(lower.RepositoryId, upper.RepositoryId);
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    private static async Task InitializeGitCheckoutAsync(string root, CancellationToken cancellationToken)
    {
        await GitAsync(root, cancellationToken, "init", "--quiet");
        await GitAsync(root, cancellationToken, "config", "user.name", "M5 portability test");
        await GitAsync(root, cancellationToken, "config", "user.email", "m5-portability@example.invalid");
        await GitAsync(root, cancellationToken, "add", "--", "src/tracked.txt");
        await GitAsync(root, cancellationToken, "commit", "--quiet", "-m", "seed portability test checkout");
    }

    private static async Task GitAsync(string root, CancellationToken cancellationToken, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start Git.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Git {args[0]} exceeded the 10-second test bound.");
        }

        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"git {args[0]} failed ({process.ExitCode}).\n{output}\n{error}");
    }
}
