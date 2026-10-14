#if NET10_0_OR_GREATER
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace Marang.Tests;

public sealed class SupervisorDelegationRecoveryProcessTests
{
    [Theory]
    [InlineData("unresolved")]
    [InlineData("recovery-approved")]
    [InlineData("recovery-fenced")]
    public async Task Actual_unknown_provider_state_can_be_resolved_by_reviewed_fenced_read_report_replacement(string barrier)
    {
        var root = FindRoot();
        var directory = Path.Combine(root, "artifacts", "aaaw-m2", "delegation-probes", barrier + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(root, "tests", "Marang.RestartProbe", "bin", "Release", "net10.0", "Marang.RestartProbe.dll");
        using (var original = Start(probe, directory, "adaptive", "unknown-provider"))
        {
            var stdout = original.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = original.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await KillAtBarrier(original, directory, "barrier-ready", stderr);
            await File.WriteAllTextAsync(Path.Combine(directory, "provider-killed-process.log"), original.Id + "\n" + await stdout + await stderr, TestContext.Current.CancellationToken);
        }
        int killedRecovery;
        using (var recovery = Start(probe, directory, "supervisor-delegation", barrier))
        {
            var stdout = recovery.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = recovery.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await KillAtBarrier(recovery, directory, "recovery-barrier-ready", stderr);
            killedRecovery = recovery.Id;
            await File.WriteAllTextAsync(Path.Combine(directory, "recovery-killed-process.log"), recovery.Id + "\n" + await stdout + await stderr, TestContext.Current.CancellationToken);
            File.WriteAllText(Path.Combine(directory, "recovery-barrier-used"), barrier);
        }
        using var resumed = Start(probe, directory, "supervisor-delegation", barrier);
        var output = resumed.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = resumed.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try { await resumed.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { resumed.Kill(entireProcessTree: true); throw new TimeoutException(await error); }
        resumed.ExitCode.Should().Be(0, await output + await error);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "supervisor-recovery-result.json"), TestContext.Current.CancellationToken));
        var json = result.RootElement;
        json.GetProperty("ProviderStarts").GetInt32().Should().Be(1);
        json.GetProperty("OldOperationMayStillExist").GetBoolean().Should().BeTrue();
        json.GetProperty("OldExecutionFenced").GetBoolean().Should().BeTrue();
        json.GetProperty("ReplacementCompleted").GetBoolean().Should().BeTrue();
        json.GetProperty("ReplacementAuthoritative").GetBoolean().Should().BeTrue();
        json.GetProperty("UnknownHistorical").GetBoolean().Should().BeTrue();
        json.GetProperty("decision").GetProperty("AuthorityOutcome").GetString().Should().Be("Automatic");
        resumed.Id.Should().NotBe(killedRecovery);
        File.WriteAllText(Path.Combine(directory, "process-evidence.json"), JsonSerializer.Serialize(new
        { Barrier = barrier, KilledRecoveryProcess = killedRecovery, ResumedProcess = resumed.Id, Passed = true }));
    }
    private static async Task KillAtBarrier(Process process, string directory, string marker, Task<string> error)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(100);
        while (!File.Exists(Path.Combine(directory, marker)))
        {
            if (process.HasExited) throw new InvalidOperationException(await error);
            if (DateTimeOffset.UtcNow >= until) { process.Kill(entireProcessTree: true); throw new TimeoutException(await error); }
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        process.Kill(entireProcessTree: true); await process.WaitForExitAsync(TestContext.Current.CancellationToken);
    }
    private static Process Start(string probe, string directory, string phase, string barrier)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { probe, "--phase", phase, "--dir", directory, "--barrier", barrier }) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("Cannot launch recovery probe.");
    }
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Marang.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Missing repository root.");
    }
}
#endif
