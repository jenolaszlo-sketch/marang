#if NET10_0_OR_GREATER
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace Marang.Tests;

public sealed class RepositoryEffectProcessTests
{
    [Fact]
    public async Task Each_recovery_barrier_survives_fresh_process_restart()
    {
        var root = FindRoot();
        var directory = Path.Combine(root, "artifacts", "aaaw-m3", "process-probes", "all-barriers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var probe = ProbePath(root);
        var resume = false;
        foreach (var barrier in new[] { "K1", "K2", "K4", "K5", "K6", "K7", "K8", "K9", "K10" })
        {
            await KillAtAsync(probe, directory, barrier, resume);
            resume = true;
        }
        using var completed = Start(probe, directory, null, resume: true);
        var stdout = completed.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = completed.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await WaitForOwnedProcessAsync(completed, TimeSpan.FromMinutes(10), TestContext.Current.CancellationToken);
        completed.ExitCode.Should().Be(0, await stdout + await stderr);
        AssertCompleted(directory);
    }

    [Fact]
    public async Task Kill_after_first_target_reconciles_partial_and_blocks_automatic_replay()
    {
        var root = FindRoot();
        var directory = Path.Combine(root, "artifacts", "aaaw-m3", "process-probes", "K3-partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var probe = ProbePath(root);
        await KillAtAsync(probe, directory, "K3");
        var checkout = Path.Combine(directory, "controlled-checkout");
        var paths = new[] { Path.Combine(checkout, "src", "Marang", "WorkflowPlanReference.cs"), Path.Combine(checkout, "README.md") };
        var beforeRecovery = paths.Select(path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        using var recovered = Start(probe, directory, null, resume: true);
        var stderr = recovered.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await WaitForOwnedProcessAsync(recovered, TimeSpan.FromMinutes(10), TestContext.Current.CancellationToken);
        recovered.ExitCode.Should().Be(0, await stderr);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "repository-effect-partial.json")));
        result.RootElement.GetProperty("classification").GetString().Should().Be("PartiallyApplied");
        result.RootElement.GetProperty("physicalApplyCount").GetInt32().Should().Be(1);
        result.RootElement.GetProperty("automaticReplayBlocked").GetBoolean().Should().BeTrue();
        result.RootElement.GetProperty("mutationBlocked").GetBoolean().Should().BeTrue();
        result.RootElement.GetProperty("effectCatchUp").GetProperty("effects")[0].GetProperty("replayPermitted").GetBoolean().Should().BeFalse();
        result.RootElement.GetProperty("effectCatchUp").GetProperty("workflow").GetProperty("decisions").EnumerateArray()
            .Any(item => item.GetProperty("kind").GetString() == "EffectReconciliation").Should().BeTrue();
        using (var killContext = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "barrier-context-K3.json"))))
        {
            var context = killContext.RootElement;
            context.GetProperty("durableStage").GetString().Should().Be("Applying");
            var targets = context.GetProperty("physicalTargetContent").EnumerateArray().ToArray();
            var files = result.RootElement.GetProperty("intent").GetProperty("files").EnumerateArray().ToArray();
            targets[0].GetProperty("sha256").GetString().Should().Be(files[0].GetProperty("afterSha256").GetString());
            targets[1].GetProperty("sha256").GetString().Should().Be(files[1].GetProperty("beforeSha256").GetString());
        }
        paths.Select(path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).Should().Equal(beforeRecovery);
        File.Exists(Path.Combine(directory, "process-id-K3.txt")).Should().BeTrue();

        using var repeated = Start(probe, directory, null, resume: true);
        var repeatedError = repeated.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await WaitForOwnedProcessAsync(repeated, TimeSpan.FromMinutes(10), TestContext.Current.CancellationToken);
        repeated.ExitCode.Should().Be(0, await repeatedError);
        using var afterRepeated = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "repository-effect-partial.json")));
        afterRepeated.RootElement.GetProperty("physicalApplyCount").GetInt32().Should().Be(1);
        paths.Select(path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).Should().Equal(beforeRecovery);
    }

    private static async Task KillAtAsync(string probe, string directory, string barrier, bool resume = false)
    {
        using var first = Start(probe, directory, barrier, resume);
        var stdout = first.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = first.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var ready = Path.Combine(directory, "barrier-ready-" + barrier);
        var until = DateTimeOffset.UtcNow.AddSeconds(120);
        while (!File.Exists(ready))
        {
            if (first.HasExited) throw new InvalidOperationException("Probe exited before " + barrier + ": " + await stderr);
            if (DateTimeOffset.UtcNow >= until)
            {
                first.Kill(entireProcessTree: true);
                throw new TimeoutException("Probe did not reach " + barrier + ": " + await stderr);
            }
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "process-id-" + barrier + ".txt"), first.Id.ToString(), TestContext.Current.CancellationToken);
        first.Kill(entireProcessTree: true);
        await WaitForOwnedProcessAsync(first, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "barrier-used-" + barrier), "killed", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "killed-" + barrier + ".log"), await stdout + await stderr,
            TestContext.Current.CancellationToken);
    }

    private static async Task WaitForOwnedProcessAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(bounded.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(cleanup.Token);
            throw;
        }
    }

    private static void AssertCompleted(string directory)
    {
        var path = Path.Combine(directory, "repository-effect-result.json");
        File.Exists(path).Should().BeTrue("the fresh process must persist its result snapshot");
        using var result = JsonDocument.Parse(File.ReadAllText(path));
        var json = result.RootElement;
        json.GetProperty("finalRevision").GetString().Should().Be("revision-3");
        json.GetProperty("recoveryBarriers").EnumerateArray().Select(value => value.GetString()).Should().Equal(
            "K1", "K2", "K4", "K5", "K6", "K7", "K8", "K9", "K10");
        json.GetProperty("originalPhysicalApplyCount").GetInt32().Should().Be(1);
        json.GetProperty("correctivePhysicalApplyCount").GetInt32().Should().Be(1);
        json.GetProperty("originalValidationPassed").GetBoolean().Should().BeFalse();
        json.GetProperty("correctiveValidationPassed").GetBoolean().Should().BeTrue();
        json.GetProperty("originalEffectActivityResult").GetString().Should().Contain("ValidationFailed");
        json.GetProperty("revision2FinalJoinWasResultFenced").GetBoolean().Should().BeTrue();
        json.GetProperty("revision2FinalJoinStatus").GetString().Should().NotBe("Completed");
        json.GetProperty("unaffectedExecutionPreserved").GetBoolean().Should().BeTrue();
        json.GetProperty("inventoryInvocationCount").GetInt32().Should().Be(1);
        json.GetProperty("finalJoinCompleted").GetBoolean().Should().BeTrue();
        json.GetProperty("catchUpExplainsEffectLineage").GetBoolean().Should().BeTrue();
        json.GetProperty("drillDownShowsSupersededAppliedEffect").GetBoolean().Should().BeTrue();
        foreach (var barrier in new[] { "K1", "K2", "K4", "K5", "K6", "K7", "K8", "K9", "K10" })
        {
            File.Exists(Path.Combine(directory, "process-id-" + barrier + ".txt")).Should().BeTrue();
            using var context = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "barrier-context-" + barrier + ".json")));
            context.RootElement.GetProperty("barrier").GetString().Should().Be(barrier);
            context.RootElement.GetProperty("processId").GetInt32().Should().BePositive();
        }
        var validationPid = int.Parse(File.ReadAllText(Path.Combine(directory, "validation-process-K6.pid")));
        validationPid.Should().BePositive().And.NotBe(int.Parse(File.ReadAllText(Path.Combine(directory, "process-id-K6.txt"))));
        using (var validationContext = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "barrier-context-K6.json"))))
        {
            validationContext.RootElement.GetProperty("validationProcessRunning").GetBoolean().Should().BeTrue();
            validationContext.RootElement.GetProperty("checkpoint").GetString().Should().Be("RunningValidationProcess");
        }
        using (var pending = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "barrier-context-K8.json"))))
        {
            pending.RootElement.GetProperty("candidateRevisionId").GetString().Should().Be("revision-3");
            pending.RootElement.GetProperty("proposalDigest").GetString().Should().HaveLength(64);
            pending.RootElement.GetProperty("pendingStatus").GetString().Should().Be("ApprovalRequired");
            var committed = json.GetProperty("correctiveProposal");
            pending.RootElement.GetProperty("proposalId").GetGuid().Should().Be(committed.GetProperty("proposalId").GetGuid());
            pending.RootElement.GetProperty("proposalDigest").GetString().Should().Be(committed.GetProperty("proposalDigest").GetString());
        }
    }

    private static Process Start(string probe, string directory, string? barrier, bool resume)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = FindRoot(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { probe, "--phase", "repository-effect", "--dir", directory }) start.ArgumentList.Add(arg);
        if (barrier is not null) { start.ArgumentList.Add("--barrier"); start.ArgumentList.Add(barrier); }
        if (resume) { start.ArgumentList.Add("--resume"); start.ArgumentList.Add("true"); }
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start repository effect process probe.");
    }

    private static string ProbePath(string root) => Path.Combine(root, "tests", "Marang.RestartProbe", "bin", "Release", "net10.0", "Marang.RestartProbe.dll");

    private static string FindRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path is not null && !File.Exists(Path.Combine(path.FullName, "Marang.slnx"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("Missing repository root.");
    }
}
#endif
