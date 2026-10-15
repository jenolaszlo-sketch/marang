#if NET10_0_OR_GREATER
using System.Text.Json;
using FluentAssertions;
using Marang.Tests.LifecycleBroker;

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
        foreach (var barrier in MultiEffectScenario.ContinuingBarriers)
        {
            await StopOwnedProbeAtBarrierAsync(probe, root, directory, barrier, resume);
            resume = true;
        }

        await using var completed = OwnedProbeRun.Start(probe, root, directory, null, resume: true);
        var completion = await completed.WaitForNaturalExitAsync(TimeSpan.FromMinutes(30), directory, TestContext.Current.CancellationToken);
        completion.Cause.Should().Be(Gagamba.Runtime.RunCause.NaturalExit);
        completion.RootExitCode.Should().Be(0);
        completion.Cleanup.Should().Be(Gagamba.Runtime.CleanupStatus.Confirmed);
        completion.Output?.Status.Should().Be(Gagamba.Execution.OutputCaptureStatus.Complete);

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
        foreach (var barrier in MultiEffectScenario.ContinuingBarriers)
        {
            File.Exists(Path.Combine(directory, "barrier-used-" + barrier)).Should().BeTrue();
            File.Exists(Path.Combine(directory, "barrier-context-" + barrier + ".json")).Should().BeTrue();
            File.Exists(Path.Combine(directory, "process-id-" + barrier + ".txt")).Should().BeTrue();
            using var stop = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "lifecycle-stop-" + barrier + ".json"), TestContext.Current.CancellationToken));
            stop.RootElement.GetProperty("root").GetProperty("cause").GetString().Should().Be("ExplicitStop");
            stop.RootElement.GetProperty("root").GetProperty("cleanup").GetString().Should().Be("Confirmed");
            stop.RootElement.GetProperty("originalWorkflowProcessId").GetInt32().Should().BeGreaterThan(0);
        }
        using (var k12Stop = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "lifecycle-stop-K12.json"), TestContext.Current.CancellationToken)))
        {
            var stoppedCommands = k12Stop.RootElement.GetProperty("brokerCommands").EnumerateArray().ToArray();
            stoppedCommands.Should().NotBeEmpty("K12 must stop the active validation command through the lifecycle owner");
            stoppedCommands.Should().OnlyContain(command => command.GetProperty("cause").GetString() == "ExplicitStop"
                && command.GetProperty("cleanup").GetString() == "Confirmed");
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
        await StopOwnedProbeAtBarrierAsync(probe, root, directory, "K4", resume: false);
        await using var resumed = OwnedProbeRun.Start(probe, root, directory, null, resume: true);
        var completion = await resumed.WaitForNaturalExitAsync(TimeSpan.FromMinutes(5), directory, TestContext.Current.CancellationToken);
        completion.Cause.Should().Be(Gagamba.Runtime.RunCause.NaturalExit);
        completion.RootExitCode.Should().Be(0);
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

    [Fact]
    public async Task Multi_effect_reconciliation_refuses_to_continue_when_actual_state_diverges_from_validated_A()
    {
        var root = FindRoot();
        var directory = Path.Combine(root, "artifacts", "aaaw-m5", "process-probes",
            "multi-effect-negative-control-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(root, "tests", "Marang.RestartProbe", "bin", "Release", "net10.0", "Marang.RestartProbe.dll");
        await StopOwnedProbeAtBarrierAsync(probe, root, directory, "K3", resume: false);

        var checkout = (await File.ReadAllTextAsync(Path.Combine(directory, "checkout-path.txt"), TestContext.Current.CancellationToken)).Trim();
        var projectPath = Path.Combine(checkout, "tests", "Marang.Tests", "Marang.Tests.csproj");
        var original = await File.ReadAllBytesAsync(projectPath, TestContext.Current.CancellationToken);
        var tampered = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(original) + Environment.NewLine + "<!-- M5 actual-state negative control -->" + Environment.NewLine);
        await File.WriteAllBytesAsync(projectPath, tampered, TestContext.Current.CancellationToken);
        var tamperedDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(tampered)).ToLowerInvariant();

        foreach (var run in new[] { 1, 2 })
        {
            await using var resumed = OwnedProbeRun.Start(probe, root, directory, barrier: null, resume: true,
                phase: "multi-effect-reconcile", reconcileRun: run);
            var completion = await resumed.WaitForNaturalExitAsync(TimeSpan.FromMinutes(5), directory, TestContext.Current.CancellationToken);
            completion.Cause.Should().Be(Gagamba.Runtime.RunCause.NaturalExit);
            completion.RootExitCode.Should().Be(0);
            File.ReadAllBytes(projectPath).Should().Equal(tampered, "reconciliation must preserve the unexpected actual bytes");

            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(directory, $"multi-effect-reconcile-{run}.json"), TestContext.Current.CancellationToken));
            var json = result.RootElement;
            json.GetProperty("activeRevisionId").GetString().Should().Be("revision-2");
            json.GetProperty("observedStateDiffersFromAExpected").GetBoolean().Should().BeTrue();
            json.GetProperty("observedStateBefore").GetProperty("stateDigest").GetString()
                .Should().Be(json.GetProperty("observedStateAfter").GetProperty("stateDigest").GetString());
            json.GetProperty("persistedAValidation").GetProperty("beforeStage").GetString().Should().Be("ValidationPassed");
            json.GetProperty("persistedAValidation").GetProperty("beforePassed").GetBoolean().Should().BeTrue();
            json.GetProperty("persistedAValidation").GetProperty("afterStage").GetString().Should().Be("ValidationPassed");
            json.GetProperty("persistedAValidation").GetProperty("afterPassed").GetBoolean().Should().BeTrue();
            json.GetProperty("persistedAValidation").GetProperty("beforeTargetStateDigest").GetString()
                .Should().Be(json.GetProperty("persistedAValidation").GetProperty("afterTargetStateDigest").GetString());
            json.GetProperty("allowlistedProjectObservedAsStale").GetBoolean().Should().BeTrue();
            json.GetProperty("bRemainedPrepared").GetBoolean().Should().BeTrue();
            json.GetProperty("cRemainedPrepared").GetBoolean().Should().BeTrue();
            json.GetProperty("noBOrCMutation").GetBoolean().Should().BeTrue();
            json.GetProperty("mutationBlocked").GetBoolean().Should().BeTrue();
            json.GetProperty("validatorCalled").GetBoolean().Should().BeFalse();
            json.GetProperty("checkpointCalled").GetBoolean().Should().BeFalse();
            json.GetProperty("bAttempt").GetProperty("mutationPerformed").GetBoolean().Should().BeFalse();
            json.GetProperty("cAttempt").GetProperty("mutationPerformed").GetBoolean().Should().BeFalse();
            json.GetProperty("bAttempt").GetProperty("reasonCode").GetString().Should().Contain("m4.prerequisite-A-output-stale");
            json.GetProperty("cAttempt").GetProperty("reasonCode").GetString().Should().Contain("m4.prerequisite-A-output-stale");
            json.GetProperty("bAttempt").GetProperty("stage").GetString().Should().Be("Prepared");
            json.GetProperty("cAttempt").GetProperty("stage").GetString().Should().Be("Prepared");
            json.GetProperty("supervisorView").GetProperty("actualCheckoutState").GetProperty("stateDigest").GetString()
                .Should().Be(json.GetProperty("observedStateAfter").GetProperty("stateDigest").GetString());
            json.GetProperty("physicalApplyLog").EnumerateArray().Select(line => line.GetString()).Should().Equal("A");
            var identities = json.GetProperty("effectIdentities");
            identities.GetProperty("aId").GetGuid().Should().Be(Guid.Parse("bf668e4e-721b-4611-aae6-e2712dc5a881"));
            identities.GetProperty("bId").GetGuid().Should().Be(Guid.Parse("bb288558-11d7-4a4d-91e2-a4332cecedc8"));
            identities.GetProperty("cId").GetGuid().Should().Be(Guid.Parse("912081ef-5f01-4d72-890a-75a381f6abdd"));
            json.GetProperty("actualProjectFileSha256").GetString().Should().Be(tamperedDigest);
            var lineage = json.GetProperty("effectLineage").EnumerateArray().ToArray();
            lineage.Select(effect => effect.GetProperty("effectId").GetGuid()).Should().Equal(
                Guid.Parse("bf668e4e-721b-4611-aae6-e2712dc5a881"),
                Guid.Parse("bb288558-11d7-4a4d-91e2-a4332cecedc8"),
                Guid.Parse("912081ef-5f01-4d72-890a-75a381f6abdd"));
            lineage.Select(effect => effect.GetProperty("revisionId").GetString()).Should().OnlyContain(revision => revision == "revision-2");
            if (run == 1)
                json.GetProperty("observedStateAfter").GetProperty("stateDigest").GetString().Should().NotBeNullOrWhiteSpace();
        }

        using var first = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "multi-effect-reconcile-1.json"), TestContext.Current.CancellationToken));
        using var second = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "multi-effect-reconcile-2.json"), TestContext.Current.CancellationToken));
        second.RootElement.GetProperty("graphDigest").GetString().Should().Be(first.RootElement.GetProperty("graphDigest").GetString());
        second.RootElement.GetProperty("observedStateAfter").GetProperty("stateDigest").GetString()
            .Should().Be(first.RootElement.GetProperty("observedStateAfter").GetProperty("stateDigest").GetString());
        second.RootElement.GetProperty("effectLineage").GetRawText().Should().Be(first.RootElement.GetProperty("effectLineage").GetRawText());
    }

    private static async Task StopOwnedProbeAtBarrierAsync(string probe, string root, string directory, string barrier, bool resume)
    {
        await using var run = OwnedProbeRun.Start(probe, root, directory, barrier, resume);
        var ready = Path.Combine(directory, "barrier-ready-" + barrier);
        var until = DateTimeOffset.UtcNow.AddMinutes(10);
        var cleanupComplete = false;
        try
        {
            while (!File.Exists(ready))
            {
                if (run.RootTask.IsCompleted)
                {
                    var receipt = await run.RootTask;
                    await run.StopAfterFailureAsync(directory, "before-" + barrier);
                    cleanupComplete = true;
                    throw new InvalidOperationException("Probe exited before " + barrier + ": " + OwnedProbeRun.Describe(receipt));
                }
                if (DateTimeOffset.UtcNow >= until)
                {
                    await run.StopAfterFailureAsync(directory, "timeout-" + barrier);
                    cleanupComplete = true;
                    throw new TimeoutException("Probe did not reach " + barrier + " within ten minutes.");
                }
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }
            if (run.RootTask.IsCompleted)
                throw new InvalidOperationException("Probe root receipt completed at barrier " + barrier + " before owner stop.");
            var pidPath = Path.Combine(directory, "workflow-process-id.txt");
            if (!File.Exists(pidPath)) throw new InvalidOperationException("Probe did not persist workflow-process-id.txt before " + barrier);
            var originalProcessId = int.Parse((await File.ReadAllTextAsync(pidPath, TestContext.Current.CancellationToken)).Trim());
            await File.WriteAllTextAsync(Path.Combine(directory, "process-id-" + barrier + ".txt"),
                originalProcessId.ToString(), TestContext.Current.CancellationToken);
            await run.StopAtBarrierAsync(directory, barrier, originalProcessId,
                requireActive: barrier == "K12", cancellationToken: TestContext.Current.CancellationToken);
            cleanupComplete = true;
            await File.WriteAllTextAsync(Path.Combine(directory, "barrier-used-" + barrier), "owned-stop-confirmed", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "killed-" + barrier + ".log"),
                await File.ReadAllTextAsync(Path.Combine(directory, "lifecycle-stop-" + barrier + ".json"), TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken);
        }
        catch
        {
            if (!cleanupComplete)
            {
                try { await run.StopAfterFailureAsync(directory, "failure-" + barrier); }
                catch { /* Preserve the original barrier failure; disposal still attempts owner cleanup. */ }
            }
            throw;
        }
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
