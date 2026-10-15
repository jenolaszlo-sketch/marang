#if NET10_0_OR_GREATER
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace Marang.Tests;

public sealed class AdaptiveRevisionProcessTests
{
    [Fact]
    public Task P1_P2_P3_P4_P5_P6_repository_backed_adaptation() => Run(null);

    [Fact]
    public async Task P10_kill_between_provider_start_and_handle_capture_never_respawns_unknown_work()
    {
        var root = FindRoot();
        var directory = Path.Combine(root, "artifacts", "aaaw-m1", "process-probes", "unknown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(root, "tests", "Marang.RestartProbe", "bin", "Release", "net10.0", "Marang.RestartProbe.dll");
        using var first = Start(probe, directory, "unknown-provider");
        var error = first.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var output = first.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var until = DateTimeOffset.UtcNow.AddSeconds(100);
        while (!File.Exists(Path.Combine(directory, "barrier-ready")))
        {
            if (first.HasExited) throw new InvalidOperationException(await error);
            if (DateTimeOffset.UtcNow > until) { first.Kill(entireProcessTree: true); throw new TimeoutException("Unknown-operation barrier"); }
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        first.Kill(entireProcessTree: true); await first.WaitForExitAsync(TestContext.Current.CancellationToken);
        using var recovered = Start(probe, directory, null, "adaptive-unknown");
        var recoveredError = recovered.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await recovered.WaitForExitAsync(TestContext.Current.CancellationToken);
        recovered.ExitCode.Should().Be(0, await recoveredError);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "unknown-result.json")));
        result.RootElement.GetProperty("ProviderStarts").GetInt32().Should().Be(1);
        result.RootElement.GetProperty("RequiresOperatorResolution").GetBoolean().Should().BeTrue();
        _ = await output;
    }

    [Theory]
    [InlineData("after-activation")]
    [InlineData("delegated-running")]
    [InlineData("proposal-created")]
    [InlineData("approved-before-commit")]
    [InlineData("during-fencing")]
    [InlineData("committed-before-dispatch")]
    [InlineData("replacement-running")]
    public Task P8_P9_kill_and_fresh_process_recovery(string barrier) => Run(barrier);

    private static async Task Run(string? barrier)
    {
        var root = FindRoot();
        var directory = Path.Combine(root, "artifacts", "aaaw-m1", "process-probes", (barrier ?? "basic") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(root, "tests", "Marang.RestartProbe", "bin", "Release", "net10.0", "Marang.RestartProbe.dll");
        File.Exists(probe).Should().BeTrue("the process probe must be built before consumer qualification");
        int? killedProcess = null;
        if (barrier is not null)
        {
            using var first = Start(probe, directory, barrier);
            var output = first.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = first.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            var until = DateTimeOffset.UtcNow.AddSeconds(100);
            while (!File.Exists(Path.Combine(directory, "barrier-ready")))
            {
                if (first.HasExited) throw new InvalidOperationException("Probe exited before barrier: " + await error);
                if (DateTimeOffset.UtcNow > until)
                {
                    first.Kill(entireProcessTree: true);
                    throw new TimeoutException("Probe did not reach " + barrier + ": " + await error);
                }
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            if (barrier == "replacement-running")
            {
                using var capture = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "replacement-capture.json")));
                capture.RootElement.GetProperty("HandleCaptured").GetBoolean().Should().BeTrue();
                capture.RootElement.GetProperty("DelegationId").GetGuid().Should().NotBe(Guid.Empty);
                capture.RootElement.GetProperty("ProviderHandle").GetString().Should().NotBeNullOrWhiteSpace();
            }
            killedProcess = first.Id;
            first.Kill(entireProcessTree: true);
            await first.WaitForExitAsync(TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "killed-process.log"), await output + await error, TestContext.Current.CancellationToken);
            File.WriteAllText(Path.Combine(directory, "barrier-used"), barrier);
        }
        using var recovered = Start(probe, directory, barrier);
        var stdout = recovered.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = recovered.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try { await recovered.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            recovered.Kill(entireProcessTree: true);
            throw new TimeoutException("Recovered process stalled for " + barrier + ": " + await stderr);
        }
        var log = await stdout + await stderr;
        File.WriteAllText(Path.Combine(directory, "recovered-process.log"), log);
        recovered.ExitCode.Should().Be(0, log);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "adaptive-result.json")));
        result.RootElement.GetProperty("ActiveRevision").GetString().Should().Be("revision-2");
        result.RootElement.GetProperty("RevisionCount").GetInt32().Should().Be(2);
        result.RootElement.GetProperty("ProviderStarts").GetInt32().Should().Be(2);
        result.RootElement.GetProperty("UnaffectedExecutionPreserved").GetBoolean().Should().BeTrue();
        result.RootElement.GetProperty("CompletedInventoryReused").GetBoolean().Should().BeTrue();
        result.RootElement.GetProperty("SupersededProviderHistorical").GetBoolean().Should().BeTrue();
        if (killedProcess is not null) recovered.Id.Should().NotBe(killedProcess);
        File.WriteAllText(Path.Combine(directory, "process-evidence.json"), JsonSerializer.Serialize(new
        { Barrier = barrier ?? "basic", KilledProcess = killedProcess, RecoveredProcess = recovered.Id, Passed = true }));
    }

    private static Process Start(string probe, string directory, string? barrier, string phase = "adaptive")
    {
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { probe, "--phase", phase, "--dir", directory }) start.ArgumentList.Add(arg);
        if (barrier is not null) { start.ArgumentList.Add("--barrier"); start.ArgumentList.Add(barrier); }
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start qualification process.");
    }
    private static string FindRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "Marang.slnx"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("Missing repository root.");
    }
}
#endif
