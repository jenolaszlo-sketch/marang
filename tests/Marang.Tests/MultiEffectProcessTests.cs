#if NET10_0_OR_GREATER
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace Marang.Tests;

public sealed class MultiEffectProcessTests
{
    [Fact]
    public async Task Multi_effect_graph_survives_each_restart_and_revises_only_stale_downstream_effects()
    {
        var root = FindRoot();
        var directory = Path.Combine(root, "artifacts", "aaaw-m4", "process-probes",
            "multi-effect-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(root, "tests", "Marang.RestartProbe", "bin", "Release", "net10.0", "Marang.RestartProbe.dll");
        var resume = false;
        foreach (var barrier in new[] { "K1", "K2", "K3", "K5", "K6", "K7", "K8", "K9", "K10", "K11", "K12" })
        {
            await KillAtAsync(probe, root, directory, barrier, resume);
            resume = true;
        }

        using var completed = Start(probe, root, directory, null, resume: true);
        var stdout = completed.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = completed.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            deadline.CancelAfter(TimeSpan.FromMinutes(30));
            try { await completed.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                if (!completed.HasExited) completed.Kill(entireProcessTree: true);
                await completed.WaitForExitAsync(CancellationToken.None);
                throw new TimeoutException("The resumed M4 multi-effect qualification exceeded 30 minutes.\n" + await stdout + "\n" + await stderr);
            }
        }
        completed.ExitCode.Should().Be(0, await stdout + await stderr);

        var resultPath = Path.Combine(directory, "multi-effect-result.json");
        File.Exists(resultPath).Should().BeTrue("the final process persists the causal graph evidence");
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, TestContext.Current.CancellationToken));
        var json = result.RootElement;
        json.GetProperty("sourceCommit").GetString().Should().Be("a0916534d0e82e499cffb2dea526b64357eb32e2");
        json.GetProperty("acceptedLogicalEffects").EnumerateArray().Select(x => x.GetString())
            .Should().Equal("A", "B2", "C2");
        json.GetProperty("historicalPhysicalEffects").EnumerateArray().Select(x => x.GetString())
            .Should().Contain("B").And.Contain("C");
        json.GetProperty("workflowValidationPassed").GetBoolean().Should().BeTrue();
        json.GetProperty("localValidationPassed").GetBoolean().Should().BeTrue();
        json.GetProperty("oldGraphValidationPassed").GetBoolean().Should().BeFalse();
        json.GetProperty("candidateCustomTestPassed").GetBoolean().Should().BeTrue();
        json.GetProperty("independentCanonicalConformanceFailed").GetBoolean().Should().BeTrue();
        json.GetProperty("inventoryResultPreserved").GetBoolean().Should().BeTrue();
        json.GetProperty("inventoryInvocationCount").GetInt32().Should().Be(1);
        json.GetProperty("activeAcceptedEffects").EnumerateArray().Select(x => x.GetString()).Should().Equal("A", "B2", "C2");
        json.GetProperty("activeGraphMutationBlocked").GetBoolean().Should().BeFalse();
        json.GetProperty("exactStateLineage").GetBoolean().Should().BeTrue();
        json.GetProperty("oldBAndCRemainAppliedEvidence").GetBoolean().Should().BeTrue();
        json.GetProperty("aWasExplicitlyAdopted").GetBoolean().Should().BeTrue();
        json.GetProperty("finalBranchCompleted").GetBoolean().Should().BeTrue();
        json.GetProperty("noDuplicateMutation").GetBoolean().Should().BeTrue();
        var counts = json.GetProperty("effectApplyCounts").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        counts["A"].Should().Be(1);
        counts["B"].Should().Be(1);
        counts["C"].Should().Be(1);
        counts["B2"].Should().Be(1);
        counts["C2"].Should().Be(1);
        foreach (var barrier in new[] { "K1", "K2", "K3", "K5", "K6", "K7", "K8", "K9", "K10", "K11", "K12" })
        {
            File.Exists(Path.Combine(directory, "barrier-used-" + barrier)).Should().BeTrue();
            File.Exists(Path.Combine(directory, "barrier-context-" + barrier + ".json")).Should().BeTrue();
            File.Exists(Path.Combine(directory, "process-id-" + barrier + ".txt")).Should().BeTrue();
        }
        using (var k8 = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "barrier-context-K8.json"), TestContext.Current.CancellationToken)))
        {
            k8.RootElement.GetProperty("activeRevisionId").GetString().Should().Be("revision-2");
            k8.RootElement.GetProperty("pendingProposalId").GetGuid().Should().NotBe(Guid.Empty);
            k8.RootElement.GetProperty("pendingProposalDigest").GetString().Should().HaveLength(64);
        }
        using (var k12 = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "barrier-context-K12.json"), TestContext.Current.CancellationToken)))
        {
            k12.RootElement.GetProperty("activeRevisionId").GetString().Should().Be("revision-3");
            k12.RootElement.GetProperty("validationProcessRunning").GetBoolean().Should().BeTrue();
            k12.RootElement.GetProperty("validationProcessId").GetInt32().Should().BeGreaterThan(0);
        }
        await AssertEffectStageAtBarrierAsync(directory, "K1", "A", "IntentCommitted", expectedValidationPassed: null);
        await AssertEffectStageAtBarrierAsync(directory, "K3", "A", "ValidationPassed");
        await AssertEffectStageAtBarrierAsync(directory, "K5", "B", "ValidationPassed");
        await AssertEffectStageAtBarrierAsync(directory, "K10", "B2", "ValidationPassed");
        var graphArchives = json.GetProperty("graphs").EnumerateArray().Select(g => g.GetProperty("revisionId").GetString()).ToArray();
        graphArchives.Should().Contain("revision-2").And.Contain("revision-3");
        var dispositions = json.GetProperty("invalidatedR2Effects").EnumerateArray().ToDictionary(
            e => e.GetProperty("logicalId").GetString()!, e => e);
        dispositions.Keys.Should().BeEquivalentTo("B", "C");
        dispositions["B"].GetProperty("replacementEffectId").GetGuid().Should().NotBe(Guid.Empty);
        dispositions["C"].GetProperty("replacementEffectId").GetGuid().Should().NotBe(Guid.Empty);
        dispositions["C"].GetProperty("reason").GetString().Should().Contain(dispositions["B"].GetProperty("effectId").GetGuid().ToString("D"));
        json.GetProperty("prerequisiteAuthority").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Partial_B_fixture_blocks_its_dependent_effect_without_replay()
    {
        var root = FindRoot();
        var directory = Path.Combine(root, "artifacts", "aaaw-m4", "process-probes",
            "multi-effect-K4-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(root, "tests", "Marang.RestartProbe", "bin", "Release", "net10.0", "Marang.RestartProbe.dll");
        await KillAtAsync(probe, root, directory, "K4", resume: false);
        using var resumed = Start(probe, root, directory, null, resume: true);
        var stdout = resumed.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = resumed.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await WaitBoundedAsync(resumed, TimeSpan.FromMinutes(5), stdout, stderr, "K4 resumed fixture");
        resumed.ExitCode.Should().Be(0, (await stdout) + "\n" + await stderr);
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "multi-effect-K4-result.json"), TestContext.Current.CancellationToken));
        result.RootElement.GetProperty("prerequisiteClassification").GetString().Should().Be("PartiallyApplied");
        result.RootElement.GetProperty("dependentEffectBlocked").GetBoolean().Should().BeTrue();
        result.RootElement.GetProperty("dependentLogicalId").GetString().Should().Be("C");
        result.RootElement.GetProperty("dependentEffectId").GetGuid().Should().NotBe(Guid.Empty);
        result.RootElement.GetProperty("dependentBlockedByEffectIds").EnumerateArray().Select(x => x.GetGuid())
            .Should().Contain(Guid.Parse("bb288558-11d7-4a4d-91e2-a4332cecedc8"));
        result.RootElement.GetProperty("dependentStage").GetString().Should().Be("Prepared");
        result.RootElement.GetProperty("physicalApplyCount").GetInt32().Should().Be(1);
        result.RootElement.GetProperty("dependentPhysicalApplyCount").GetInt32().Should().Be(0);
        File.Exists(Path.Combine(directory, "barrier-context-K4.json")).Should().BeTrue();
        File.Exists(Path.Combine(directory, "process-id-K4.txt")).Should().BeTrue();
    }

    private static async Task KillAtAsync(string probe, string root, string directory, string barrier, bool resume)
    {
        using var process = Start(probe, root, directory, barrier, resume);
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var ready = Path.Combine(directory, "barrier-ready-" + barrier);
        var until = DateTimeOffset.UtcNow.AddMinutes(10);
        try
        {
            while (!File.Exists(ready))
            {
                if (process.HasExited) throw new InvalidOperationException("Probe exited before " + barrier + ": " + await stdout + "\n" + await stderr);
                if (DateTimeOffset.UtcNow >= until)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                    throw new TimeoutException("Probe did not reach " + barrier + ": " + await stdout + "\n" + await stderr);
                }
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (!process.HasExited) await process.WaitForExitAsync(CancellationToken.None);
            _ = await stdout;
            _ = await stderr;
            throw;
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "process-id-" + barrier + ".txt"), process.Id.ToString(), TestContext.Current.CancellationToken);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "barrier-used-" + barrier), "killed", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "killed-" + barrier + ".log"), await stdout + await stderr, TestContext.Current.CancellationToken);
    }

    private static async Task WaitBoundedAsync(Process process, TimeSpan timeout, Task<string> stdout, Task<string> stderr, string label)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException(label + " exceeded " + timeout + ".\n" + await stdout + "\n" + await stderr);
        }
    }

    private static Process Start(string probe, string root, string directory, string? barrier, bool resume)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { probe, "--phase", "multi-effect", "--dir", directory }) start.ArgumentList.Add(arg);
        if (barrier is not null) { start.ArgumentList.Add("--barrier"); start.ArgumentList.Add(barrier); }
        if (resume) { start.ArgumentList.Add("--resume"); start.ArgumentList.Add("true"); }
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start multi-effect process probe.");
    }

    private static async Task AssertEffectStageAtBarrierAsync(string directory, string barrier, string logicalId, string expectedStage,
        bool? expectedValidationPassed = true)
    {
        using var context = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(directory, "barrier-context-" + barrier + ".json"), TestContext.Current.CancellationToken));
        var effect = context.RootElement.GetProperty("effects").EnumerateArray()
            .Single(e => e.GetProperty("logicalStepId").GetString() == logicalId);
        effect.GetProperty("stage").GetString().Should().Be(expectedStage);
        if (expectedValidationPassed.HasValue)
            effect.GetProperty("validationPassed").GetBoolean().Should().Be(expectedValidationPassed.Value);
        else
            effect.GetProperty("validationPassed").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Marang.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Marang repository root.");
    }
}
#endif
