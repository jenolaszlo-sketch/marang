using System.Text.Json;
using FluentAssertions;
using Marang.Runs;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Qingniao;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Marang.Tests;

/// <summary>
/// M5.3 run-projection behavior: declared Fuwen topology joined to Zhinu
/// execution state through the port-owned step map, with durable watermark,
/// fingerprint gating, drift detection, and caller isolation. Synthetic steps
/// never become topology nodes; unmatched keys fail closed.
/// </summary>
public sealed class MarangRunProjectionTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private const string Caller = "operator-1";

    [Fact]
    public async Task Projection_covers_declared_topology_with_execution_overlay_and_watermark()
    {
        var plan = CreateMainPlan();
        var map = FuwenZhinuStepMapper.Map(plan);
        var runId = Guid.NewGuid();
        var steps = new[]
        {
            Step(runId, "demo/fetch", StepStatus.Completed),
            Step(runId, "demo/decide", StepStatus.Completed),
            Step(runId, "demo/decide/$then/accept", StepStatus.Completed),
            Step(runId, "demo/decide/$merge", StepStatus.Completed),
            Step(runId, "demo/saved", StepStatus.Completed),
            Step(runId, "demo/approval", StepStatus.Waiting),
        };
        var (catalog, delegationId) = await AcceptAsync("proj-topology");
        var correlation = Correlation(delegationId, runId, map);
        var run = Run(runId, WorkflowStatus.Running, map, correlation);
        var source = new FakeSource(plan, run, steps, throughSequence: 42);

        var (status, body) = await RunProjectionEndpoints.GetRunAsync(
            catalog, Caller, runId, source, () => Start.AddMinutes(2), TestContext.Current.CancellationToken);

        status.Should().Be(200);
        var projection = Json(body);
        Str(projection, "status").Should().Be("running");
        Str(projection, "durableSequence").Should().Be("42");
        Str(projection, "attention").Should().Be("Needs your approval.");

        var nodes = projection.GetProperty("nodes");
        var byId = nodes.EnumerateArray().ToDictionary(node => Str(node, "id"));
        // Declared nodes present, executed and otherwise.
        byId.Should().ContainKey("demo/fetch");
        byId.Should().ContainKey("demo/decide/$else/repair");
        byId.Should().ContainKey("demo/return_result");
        Str(byId["demo/fetch"], "status").Should().Be("completed");
        Str(byId["demo/decide/$else/repair"], "status").Should().Be("pending");
        Str(byId["demo/return_result"], "status").Should().Be("pending");
        Str(byId["demo/approval"], "status").Should().Be("waiting");

        // Edges come from the declared plan, never from event order.
        var edges = projection.GetProperty("edges");
        edges.EnumerateArray().Select(edge => Str(edge, "id")).Should()
            .Contain("demo/fetch->demo/decide")
            .And.Contain("demo/decide->demo/decide/$then/accept");

        // Synthetic merge is execution detail, not topology.
        var executionSteps = projection.GetProperty("executionSteps");
        executionSteps.EnumerateArray().Select(step => Str(step, "stepKey")).Should()
            .Contain("demo/decide/$merge");
        var checkpoints = projection.GetProperty("checkpoints");
        checkpoints.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Repeat_projection_covers_multiple_iterations_without_rederiving_paths()
    {
        var plan = CreateRepeatPlan();
        var map = FuwenZhinuStepMapper.Map(plan);
        var runId = Guid.NewGuid();
        var steps = new[]
        {
            Step(runId, "$loop/loop1/1/body/step", StepStatus.Completed),
            Step(runId, "$loop/loop1/1/commit", StepStatus.Completed),
            Step(runId, "$loop/loop1/2/body/step", StepStatus.Running),
            Step(runId, "$loop/loop1/2/condition", StepStatus.Running),
            Step(runId, "loop1", StepStatus.Running),
        };
        var (catalog, delegationId) = await AcceptAsync("proj-repeat");        var correlation = Correlation(delegationId, runId, map);        var run = Run(runId, WorkflowStatus.Running, map, correlation);
        var source = new FakeSource(plan, run, steps, throughSequence: 17);

        var (status, body) = await RunProjectionEndpoints.GetRunAsync(
            catalog, Caller, runId, source, () => Start.AddMinutes(3), TestContext.Current.CancellationToken);

        status.Should().Be(200);
        var nodes = Json(body).GetProperty("nodes");
        var byId = nodes.EnumerateArray().ToDictionary(node => Str(node, "id"));
        // The declared repeat body is covered by its iterations (one completed, one running).
        byId.Should().ContainKey("demo/loop1/$body/step");
        Str(byId["demo/loop1/$body/step"], "status").Should().BeOneOf("completed", "running");
    }

    [Fact]
    public async Task FanOut_projection_respects_item_coverage()
    {
        var plan = CreateFanOutPlan();
        var map = FuwenZhinuStepMapper.Map(plan);
        var runId = Guid.NewGuid();
        var fanOutPath = "batch/process";
        var steps = new[]
        {
            Step(runId, fanOutPath, StepStatus.Running),
            Step(runId, $"{fanOutPath}/$item/sha256-{new string('a', 64)}", StepStatus.Completed),
            Step(runId, $"{fanOutPath}/$item/sha256-{new string('b', 64)}", StepStatus.Running),
        };
        var (catalog, delegationId) = await AcceptAsync("proj-fanout");        var correlation = Correlation(delegationId, runId, map);        var run = Run(runId, WorkflowStatus.Running, map, correlation);
        var source = new FakeSource(plan, run, steps, throughSequence: 9);

        var (status, body) = await RunProjectionEndpoints.GetRunAsync(
            catalog, Caller, runId, source, () => Start.AddMinutes(1), TestContext.Current.CancellationToken);

        status.Should().Be(200);
        var projection = Json(body);
        var executionSteps = projection.GetProperty("executionSteps");
        executionSteps.EnumerateArray().Select(step => Str(step, "stepKey")).Should()
            .Contain($"{fanOutPath}/$item/sha256-{new string('a', 64)}");
        // Fan-out body nodes are declared topology (not execution steps), covered by items.
        var nodes = projection.GetProperty("nodes");
        nodes.EnumerateArray().Select(node => Str(node, "id")).Should()
            .Contain("batch/process/$body/uppercase");
    }

    [Fact]
    public async Task Unmatched_step_key_fails_closed_as_drift()
    {
        var plan = CreateMainPlan();
        var map = FuwenZhinuStepMapper.Map(plan);
        var runId = Guid.NewGuid();
        var steps = new[] { Step(runId, "demo/fetch", StepStatus.Completed), Step(runId, "bogus/step", StepStatus.Running) };
        var (catalog, delegationId) = await AcceptAsync("proj-drift");        var correlation = Correlation(delegationId, runId, map);        var run = Run(runId, WorkflowStatus.Running, map, correlation);
        var source = new FakeSource(plan, run, steps, throughSequence: 3);

        var (status, _) = await RunProjectionEndpoints.GetRunAsync(
            catalog, Caller, runId, source, () => Start, TestContext.Current.CancellationToken);

        status.Should().Be(500);
    }

    [Fact]
    public async Task Fingerprint_mismatch_fails_closed_as_incompatible()
    {
        var plan = CreateMainPlan();
        var map = FuwenZhinuStepMapper.Map(plan);
        var runId = Guid.NewGuid();
        var steps = new[] { Step(runId, "demo/fetch", StepStatus.Completed) };
        var catalog = new MarangDelegationCatalog();
        var acceptance = await catalog.AcceptAsync(
            new DelegationCallerScope(Caller),
            new DelegationRequest(
                "proj-mismatch",
                "Do the work",
                "test-provider",
                new WorkspaceReference("local", "workspace", "revision"),
                ["Done"],
                [],
                new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2)),
            TestContext.Current.CancellationToken);
        var wrong = new RunCorrelation(
            acceptance.DelegationId, "gen", runId, map.PlanRevision, "wrong-fingerprint");
        var run = Run(runId, WorkflowStatus.Running, map, wrong);
        var source = new FakeSource(plan, run, steps, throughSequence: 3);

        var (status, _) = await RunProjectionEndpoints.GetRunAsync(
            catalog, Caller, runId, source, () => Start, TestContext.Current.CancellationToken);

        status.Should().Be(409);
    }

    [Fact]
    public async Task Delegation_run_link_resolves_without_disclosing_foreign_runs()
    {
        var plan = CreateMainPlan();
        var map = FuwenZhinuStepMapper.Map(plan);
        var runId = Guid.NewGuid();
        var steps = new[] { Step(runId, "demo/fetch", StepStatus.Completed) };
        var (catalog, delegationId) = await AcceptAsync("proj-link");
        var correlation = Correlation(delegationId, runId, map);
        var run = Run(runId, WorkflowStatus.Running, map, correlation);
        var source = new FakeSource(plan, run, steps, throughSequence: 5);
        var ct = TestContext.Current.CancellationToken;

        var (status, body) = await RunProjectionEndpoints.GetDelegationRunAsync(
            catalog, Caller, delegationId.Value, source, ct);
        status.Should().Be(200);
        Str(Json(body), "runId").Should().Be(runId.ToString("D"));

        (await RunProjectionEndpoints.GetDelegationRunAsync(
            catalog, "someone-else", delegationId.Value, source, ct)).StatusCode.Should().Be(404);
        (await RunProjectionEndpoints.GetDelegationRunAsync(
            catalog, Caller, Guid.NewGuid(), source, ct)).StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Foreign_caller_cannot_read_a_run()
    {
        var plan = CreateMainPlan();
        var map = FuwenZhinuStepMapper.Map(plan);
        var runId = Guid.NewGuid();
        var steps = new[] { Step(runId, "demo/fetch", StepStatus.Completed) };
        var (catalog, delegationId) = await AcceptAsync("proj-foreign");        var correlation = Correlation(delegationId, runId, map);        var run = Run(runId, WorkflowStatus.Running, map, correlation);
        var source = new FakeSource(plan, run, steps, throughSequence: 3);

        (await RunProjectionEndpoints.GetRunAsync(
            catalog, "someone-else", runId, source, () => Start, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Durable_inputs_survive_close_and_reopen()
    {
        var plan = CreateMainPlan();
        var map = FuwenZhinuStepMapper.Map(plan);
        var runId = Guid.NewGuid();
        var (preCatalog, delegationId) = await AcceptAsync("proj-reopen");
        var correlation = Correlation(delegationId, runId, map);
        var directory = Path.Combine(Path.GetTempPath(), "marang-runs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "zhinu.db");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var options = new ZhinuSqliteOptions { DatabasePath = databasePath };
            var store = new SqliteWorkflowStore(options);
            await store.CreateRunAsync(
                new WorkflowRun
                {
                    Id = runId,
                    WorkflowName = "demo",
                    WorkflowVersion = "1",
                    Status = WorkflowStatus.Running,
                    CreatedAt = Start,
                    UpdatedAt = Start,
                    DefinitionFingerprint = map.ExecutionFingerprint,
                    MetadataJson = correlation.ToMetadataJson(),
                }, ct);
            await store.AppendEventAsync(runId, "test.step", null, cancellationToken: ct);
            await store.AppendEventAsync(runId, "test.step", null, cancellationToken: ct);

            var reopened = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = databasePath });
            {
                var run = await reopened.GetRunAsync(runId, ct);
                run.Should().NotBeNull();
                var events = await reopened.GetEventsAsync(runId, 0, 100, ct);
                var throughSequence = events.Count == 0 ? 0 : events.Max(@event => @event.Sequence);

                var steps = new[] { Step(runId, "demo/fetch", StepStatus.Completed) };
                var source = new FakeSource(plan, run!, steps, throughSequence);
                var (status, body) = await RunProjectionEndpoints.GetRunAsync(
                    preCatalog, Caller, runId, source, () => Start.AddMinutes(1), ct);

                status.Should().Be(200);
                Str(Json(body), "durableSequence").Should().Be(throughSequence.ToString());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            for (var attempt = 0; Directory.Exists(directory) && attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(50 * (attempt + 1), TestContext.Current.CancellationToken);
                }
            }
        }
    }

    private static WorkflowPlan CreateMainPlan()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var activityDesc = new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1", new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var fetchPath = StructuralNodeIdentity.Create("demo", "fetch");
        var condPath = StructuralNodeIdentity.Create("demo", "decide");
        var acceptPath = condPath + "/$then/accept";
        var repairPath = condPath + "/$else/repair";
        var savedPath = StructuralNodeIdentity.Create("demo", "saved");
        var approvalPath = StructuralNodeIdentity.Create("demo", "approval");
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        return new WorkflowPlan(
            FuwenContracts.IrVersion,
            "fuwen-language/v1",
            FuwenContracts.CompilerSemanticVersion,
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersion,
            "demo",
            "1",
            str,
            str,
            "routing/1",
            [],
            [activityDesc],
            new CapabilityManifest([]),
            [
                new ActivityNode("fetch", fetchPath, activityDesc, [new ArgumentBinding("value", new InputBinding([]))], str),
                new ConditionalNode(
                    "decide", condPath,
                    new ConditionExpression(ConditionOperator.Equal, new InputBinding([]), new LiteralBinding(JsonDocument.Parse("\"go\"").RootElement.Clone())),
                    [new ActivityNode("accept", acceptPath, activityDesc, [new ArgumentBinding("value", new InputBinding([]))], str)],
                    [new ActivityNode("repair", repairPath, activityDesc, [new ArgumentBinding("value", new InputBinding([]))], str)],
                    new ConditionalMerge(new NodeOutputBinding(acceptPath, []), new NodeOutputBinding(repairPath, []), str)),
                new CheckpointNode("saved", savedPath, new InputBinding([]), str),
                new WaitNode("approval", approvalPath, "approval_request", str),
                new ReturnNode("return_result", returnPath, new NodeOutputBinding(approvalPath, [])),
            ],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [
                    new WorkflowExecutionPhase([fetchPath]),
                    new WorkflowExecutionPhase([condPath]),
                    new WorkflowExecutionPhase([savedPath]),
                    new WorkflowExecutionPhase([approvalPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion("demo/decide/$then", [new WorkflowExecutionPhase([acceptPath])]),
                new WorkflowExecutionRegion("demo/decide/$else", [new WorkflowExecutionPhase([repairPath])]),
            ]));
    }

    private static WorkflowPlan CreateRepeatPlan()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var activityDesc = new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1", new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var loopPath = StructuralNodeIdentity.Create("demo", "loop1");
        var stepPath = loopPath + "/$body/step";
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        return new WorkflowPlan(
            FuwenContracts.IrVersion,
            "fuwen-language/v1",
            FuwenContracts.CompilerSemanticVersion,
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersion,
            "demo",
            "1",
            str,
            str,
            "routing/1",
            [],
            [activityDesc],
            new CapabilityManifest([]),
            [
                new RepeatNode(
                    "loop1", loopPath, 5, str,
                    new InputBinding([]),
                    [new ActivityNode("step", stepPath, activityDesc, [new ArgumentBinding("value", new LoopStateBinding([]))], str)],
                    new NodeOutputBinding(stepPath, []),
                    new ConditionExpression(ConditionOperator.Equal, new LoopIterationBinding([]), new LiteralBinding(JsonDocument.Parse("3").RootElement.Clone())),
                    str),
                new ReturnNode("return_result", returnPath, new NodeOutputBinding(loopPath, [])),
            ],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [new WorkflowExecutionPhase([loopPath]), new WorkflowExecutionPhase([returnPath])]),
                new WorkflowExecutionRegion("demo/loop1/$body", [new WorkflowExecutionPhase([stepPath])]),
            ]));
    }

    private static WorkflowPlan CreateFanOutPlan()
    {
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        var list = new ListType(text, 3);
        var activity = new DescriptorReference(DescriptorKind.Activity, "sample.uppercase", "1", new ContentDigest("sha256", "descriptor/v1", new string('e', 64)));
        var fanOutPath = StructuralNodeIdentity.Create("batch", "process");
        var bodyPath = $"{fanOutPath}/$body/uppercase";
        var returnPath = StructuralNodeIdentity.Create("batch", "return_result");
        return new WorkflowPlan(
            FuwenContracts.IrVersion,
            "fuwen-language/v1",
            FuwenContracts.CompilerSemanticVersion,
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersion,
            "batch",
            "1",
            list,
            list,
            "routing/1",
            [],
            [activity],
            new CapabilityManifest([]),
            [
                new FanOutNode(
                    "process", fanOutPath,
                    new InputBinding([]),
                    new FanOutItemBinding("item", text),
                    new FanOutItemValueBinding([]),
                    [new ActivityNode("uppercase", bodyPath, activity,
                        [new ArgumentBinding("value", new FanOutItemValueBinding([]))], text)],
                    new NodeOutputBinding(bodyPath, []),
                    list,
                    MaximumItems: 3,
                    MaximumConcurrency: 2),
                new ReturnNode("return_result", returnPath, new NodeOutputBinding(fanOutPath, [])),
            ],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("batch", [
                    new WorkflowExecutionPhase([fanOutPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
                new WorkflowExecutionRegion($"{fanOutPath}/$body", [new WorkflowExecutionPhase([bodyPath])]),
            ]));
    }

    private static WorkflowRun Run(Guid runId, WorkflowStatus status, FuwenZhinuStepMap map, RunCorrelation? correlation) =>
        new()
        {
            Id = runId,
            WorkflowName = "demo",
            WorkflowVersion = "1",
            Status = status,
            CreatedAt = Start,
            UpdatedAt = Start.AddMinutes(1),
            DefinitionFingerprint = map.ExecutionFingerprint,
            MetadataJson = correlation?.ToMetadataJson(),
        };

    private static WorkflowStepRun Step(Guid runId, string stepKey, StepStatus status) =>
        new()
        {
            Id = Guid.NewGuid(),
            WorkflowRunId = runId,
            StepKey = stepKey,
            Status = status,
            Attempt = 1,
            CreatedAt = Start,
        };

    private static RunCorrelation Correlation(DelegationId delegationId, Guid runId, FuwenZhinuStepMap map) =>
        new(delegationId, delegationId.Value.ToString("D"), runId, map.PlanRevision, map.ExecutionFingerprint);

    private static async Task<(MarangDelegationCatalog Catalog, DelegationId DelegationId)> AcceptAsync(string requestKey)
    {
        var catalog = new MarangDelegationCatalog();
        var acceptance = await catalog.AcceptAsync(
            new DelegationCallerScope(Caller),
            new DelegationRequest(
                requestKey,
                "Do the work",
                "test-provider",
                new WorkspaceReference("local", "workspace", "revision"),
                ["Done"],
                [],
                new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2)),
            TestContext.Current.CancellationToken);
        return (catalog, acceptance.DelegationId);
    }

    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

    private static JsonElement Json(object? body) =>
        JsonDocument.Parse(JsonSerializer.Serialize(body, CamelCase)).RootElement;

    private static string Str(JsonElement element, string name) =>
        element.GetProperty(name).GetString()!;

    private sealed class FakeSource(
        WorkflowPlan plan,
        WorkflowRun run,
        IReadOnlyList<WorkflowStepRun> steps,
        long throughSequence) : IRunProjectionSource
    {
        public ValueTask<WorkflowPlan?> GetPlanAsync(string planRevision, CancellationToken cancellationToken = default) =>
            new(planRevision == plan.Revision ? plan : null);

        public ValueTask<WorkflowRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
            new(runId == run.Id ? run : null);

        public ValueTask<IReadOnlyList<WorkflowStepRun>> GetStepsAsync(Guid runId, CancellationToken cancellationToken = default) =>
            new(runId == run.Id ? steps : (IReadOnlyList<WorkflowStepRun>)[]);

        public ValueTask<long> GetThroughSequenceAsync(Guid runId, CancellationToken cancellationToken = default) =>
            new(runId == run.Id ? throughSequence : 0);

        public ValueTask<Guid?> GetRunIdByDelegationAsync(DelegationId delegationId, CancellationToken cancellationToken = default)
        {
            var correlation = RunCorrelation.TryParseMetadataJson(run.MetadataJson, run.Id);
            return new(correlation is not null && correlation.DelegationId == delegationId ? run.Id : (Guid?)null);
        }
    }
}
