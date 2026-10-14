#if NET10_0_OR_GREATER
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace Marang.Tests;

/// <summary>Qualification of durable supervisor continuity across a process boundary.</summary>
public sealed class SupervisorContinuityProcessTests
{
    [Theory]
    [InlineData("discussion")]
    [InlineData("proposal")]
    [InlineData("approval")]
    [InlineData("between-revisions")]
    [InlineData("after-fencing")]
    [InlineData("checkpoint")]
    public async Task Successive_repository_revisions_recover_after_fresh_process(string barrier)
    {
        var root = FindRoot();
        var directory = Path.Combine(root, "artifacts", "aaaw-m2", "continuity-probes", barrier + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(root, "tests", "Marang.RestartProbe", "bin", "Release", "net10.0", "Marang.RestartProbe.dll");
        File.Exists(probe).Should().BeTrue("the continuity probe must be built before consumer qualification");

        using (var first = Start(probe, directory, "continuity", barrier))
        {
            var stdout = first.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = first.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(100);
            while (!File.Exists(Path.Combine(directory, "barrier-ready")))
            {
                if (first.HasExited) throw new InvalidOperationException("Probe exited before " + barrier + ": " + await stderr);
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    first.Kill(entireProcessTree: true);
                    throw new TimeoutException("Probe did not reach " + barrier + ": " + await stderr);
                }
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            var killedProcessId = first.Id;
            first.Kill(entireProcessTree: true);
            await first.WaitForExitAsync(TestContext.Current.CancellationToken);
            File.WriteAllText(Path.Combine(directory, "killed-process.log"), killedProcessId + Environment.NewLine + await stdout + await stderr);
            File.WriteAllText(Path.Combine(directory, "barrier-used"), barrier);
        }

        File.Delete(Path.Combine(directory, "barrier-ready"));
        using var recovered = Start(probe, directory, "continuity-resume", barrier);
        var output = recovered.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = recovered.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var deadlineToken = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadlineToken.CancelAfter(TimeSpan.FromSeconds(180));
        try { await recovered.WaitForExitAsync(deadlineToken.Token); }
        catch (OperationCanceledException)
        {
            recovered.Kill(entireProcessTree: true);
            throw new TimeoutException("Fresh continuity process stalled at " + barrier + ": " + await error);
        }
        var log = await output + await error;
        recovered.ExitCode.Should().Be(0, log);

        var resultPath = Path.Combine(directory, "continuity-result.json");
        File.Exists(resultPath).Should().BeTrue("the fresh process must publish durable qualification evidence");
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, TestContext.Current.CancellationToken));
        var json = result.RootElement;
        json.GetProperty("RevisionCount").GetInt32().Should().Be(4);
        json.GetProperty("LineageContinuous").GetBoolean().Should().BeTrue();
        json.GetProperty("InventoryExecutionStableAcrossRevisions").GetBoolean().Should().BeTrue();
        json.GetProperty("InventoryInvocationCount").GetInt32().Should().Be(1);
        json.GetProperty("CompletedInventoryExplicitlyReused").GetBoolean().Should().BeTrue();
        json.GetProperty("FencedResultsRemainHistorical").GetBoolean().Should().BeTrue();
        json.GetProperty("CurrentReportReplaced").GetBoolean().Should().BeTrue();
        json.GetProperty("RepositoryEvidencePresent").GetBoolean().Should().BeTrue();
        json.GetProperty("FreshProcessRecovered").GetBoolean().Should().BeTrue();
        json.GetProperty("FreshAuthorityReattached").GetBoolean().Should().BeTrue();
        json.GetProperty("CurrentReportCompleted").GetBoolean().Should().BeTrue();
        json.GetProperty("SupersededDelegationHasDurableResult").GetBoolean().Should().BeTrue();
        json.GetProperty("ProviderStartsAcrossM1Revisions").GetInt32().Should().Be(2);
        json.GetProperty("Pass").GetBoolean().Should().BeTrue();

        File.WriteAllText(Path.Combine(directory, "process-evidence.json"), JsonSerializer.Serialize(new
        {
            Barrier = barrier,
            KilledProcess = File.ReadAllText(Path.Combine(directory, "killed-process.log")).Split(Environment.NewLine)[0],
            RecoveredProcess = recovered.Id,
            Passed = true
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public async Task Normal_fresh_process_restart_reconstructs_catch_up_without_a_barrier()
    {
        var root = FindRoot();
        var directory = Path.Combine(root, "artifacts", "aaaw-m2", "continuity-probes", "normal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(root, "tests", "Marang.RestartProbe", "bin", "Release", "net10.0", "Marang.RestartProbe.dll");
        using (var first = Start(probe, directory, "continuity", "unused"))
        {
            var stdout = first.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = first.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            try { await first.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { first.Kill(entireProcessTree: true); throw new TimeoutException("Initial continuity qualification timed out: " + await stderr); }
            first.ExitCode.Should().Be(0, await stdout + await stderr);
        }
        using var resumed = Start(probe, directory, "continuity-resume", "unused");
        var output = resumed.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = resumed.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var resumeTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        resumeTimeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { await resumed.WaitForExitAsync(resumeTimeout.Token); }
        catch (OperationCanceledException) { resumed.Kill(entireProcessTree: true); throw new TimeoutException("Normal fresh restart timed out: " + await error); }
        resumed.ExitCode.Should().Be(0, await output + await error);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "continuity-result.json"), TestContext.Current.CancellationToken));
        result.RootElement.GetProperty("FreshProcessRecovered").GetBoolean().Should().BeTrue();
        result.RootElement.GetProperty("RevisionCount").GetInt32().Should().Be(4);
        result.RootElement.GetProperty("Pass").GetBoolean().Should().BeTrue();
    }

    private static Process Start(string probe, string directory, string phase, string barrier)
    {
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { probe, "--phase", phase, "--dir", directory, "--barrier", barrier }) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start continuity qualification process.");
    }

    private static string FindRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "Marang.slnx"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("Missing repository root.");
    }
}
#endif
