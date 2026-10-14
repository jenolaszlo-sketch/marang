#if NET10_0_OR_GREATER
using System.Text.Json;
using FluentAssertions;
using Marang.Runs;
using Marang.SessionJournal;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Hongxian.Sqlite;
using Penghou.Qingniao;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Marang.Tests;

/// <summary>
/// M5 snapshot-watermark adoption: the run projection source reports Zhinu's
/// atomic snapshot watermark instead of an independent max-sequence scan.
/// A Durable/Durable/Advisory/Advisory event stream must report the second
/// durable sequence, not the highest raw sequence.
/// </summary>
public sealed class MarangSnapshotWatermarkTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private const string Caller = "operator-1";

    [Fact]
    public async Task ThroughSequence_reports_second_durable_not_max_raw_sequence()
    {
        var directory = Path.Combine(Path.GetTempPath(), "marang-watermark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ct = TestContext.Current.CancellationToken;
        await using var hongxian = new HongxianSqliteStoreSet(new HongxianSqliteOptions
        {
            RootPath = Path.Combine(directory, "hongxian"),
            Pooling = false,
        });
        try
        {
            var zhinu = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(directory, "zhinu.db"),
            });
            var runId = await SeedDurableDurableAdvisoryAdvisoryAsync(zhinu, ct);
            var source = new ConfiguredRunProjectionSource(
                zhinu, zhinu, zhinu,
                (_, _) => ValueTask.FromResult<WorkflowPlan?>(CreatePlan()),
                hongxian);

            var through = await source.GetThroughSequenceAsync(runId, ct);

            // Sequences are 1,2 durable then 3,4 advisory; the watermark is 2.
            through.Should().Be(2);

            var events = await zhinu.GetEventsAsync(runId, 0, 100, ct);
            events.Max(e => e.Sequence).Should().Be(4);

            if (source is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Projection_endpoint_reports_snapshot_watermark()
    {
        var directory = Path.Combine(Path.GetTempPath(), "marang-watermark-endpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ct = TestContext.Current.CancellationToken;
        await using var hongxian = new HongxianSqliteStoreSet(new HongxianSqliteOptions
        {
            RootPath = Path.Combine(directory, "hongxian"),
            Pooling = false,
        });
        try
        {
            var plan = CreatePlan();
            var map = FuwenZhinuStepMapper.Map(plan);
            var catalog = new MarangDelegationCatalog(TestAcceptanceGate.AllowAll());
            var acceptance = await catalog.AcceptAsync(
                new DelegationCallerScope(Caller),
                new DelegationRequest(
                    "proj-watermark",
                    "Do the work",
                    "test-provider",
                    new WorkspaceReference("local", "workspace", "revision"),
                    ["Done"],
                    [],
                    new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2)),
                ct);
            var runId = Guid.NewGuid();
            var correlation = new RunCorrelation(
                acceptance.DelegationId,
                acceptance.DelegationId.Value.ToString("D"),
                runId,
                map.PlanRevision,
                map.ExecutionFingerprint);
            var zhinu = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(directory, "zhinu.db"),
            });
            await zhinu.CreateRunAsync(
                new WorkflowRun
                {
                    Id = runId,
                    WorkflowName = "echo",
                    WorkflowVersion = "1",
                    Status = WorkflowStatus.Running,
                    CreatedAt = Start,
                    UpdatedAt = Start,
                    DefinitionFingerprint = map.ExecutionFingerprint,
                    MetadataJson = correlation.ToMetadataJson(),
                }, ct);
            await zhinu.AppendEventAsync(runId, WorkflowEventTypes.StepCompleted, null, cancellationToken: ct);
            await zhinu.AppendEventAsync(runId, WorkflowEventTypes.Progress, null, cancellationToken: ct);
            await zhinu.AppendEventAsync(runId, WorkflowEventTypes.Progress, null, cancellationToken: ct);
            var source = new ConfiguredRunProjectionSource(
                zhinu, zhinu, zhinu,
                (_, _) => ValueTask.FromResult<WorkflowPlan?>(plan),
                hongxian);

            var (status, body) = await RunProjectionEndpoints.GetRunAsync(
                catalog, Caller, runId, source, () => Start.AddMinutes(2), ct);

            status.Should().Be(200);
            Str(Json(body), "durableSequence").Should().Be("2");

            if (source is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Watermark_is_stable_across_reopen_and_zero_for_unknown_runs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "marang-watermark-reopen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ct = TestContext.Current.CancellationToken;
        var zhinuPath = Path.Combine(directory, "zhinu.db");
        var zhinu = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = zhinuPath });
        var runId = await SeedDurableDurableAdvisoryAdvisoryAsync(zhinu, ct);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        await using var hongxian = new HongxianSqliteStoreSet(new HongxianSqliteOptions
        {
            RootPath = Path.Combine(directory, "hongxian"),
            Pooling = false,
        });
        try
        {
            var reopened = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = zhinuPath });
            var source = new ConfiguredRunProjectionSource(
                reopened, reopened, reopened,
                (_, _) => ValueTask.FromResult<WorkflowPlan?>(null),
                hongxian);

            (await source.GetThroughSequenceAsync(runId, ct)).Should().Be(2);
            (await source.GetThroughSequenceAsync(Guid.NewGuid(), ct)).Should().Be(0);

            if (source is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task<Guid> SeedDurableDurableAdvisoryAdvisoryAsync(
        SqliteWorkflowStore zhinu, CancellationToken ct)
    {
        var runId = Guid.NewGuid();
        await zhinu.CreateRunAsync(
            new WorkflowRun
            {
                Id = runId,
                WorkflowName = "echo",
                WorkflowVersion = "1",
                Status = WorkflowStatus.Running,
                CreatedAt = Start,
                UpdatedAt = Start,
            }, ct);
        await zhinu.AppendEventAsync(runId, WorkflowEventTypes.StepCompleted, null, cancellationToken: ct);
        await zhinu.AppendEventAsync(runId, WorkflowEventTypes.Progress, null, cancellationToken: ct);
        await zhinu.AppendEventAsync(runId, WorkflowEventTypes.Progress, null, cancellationToken: ct);
        return runId;
    }

    private static WorkflowPlan CreatePlan()
    {
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        var activity = new DescriptorReference(
            DescriptorKind.Activity, "sample.echo", "1",
            new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var activityPath = StructuralNodeIdentity.Create("echo", "echo");
        var returnPath = StructuralNodeIdentity.Create("echo", "return_result");
        return new WorkflowPlan(
            FuwenContracts.IrVersion,
            "fuwen-language/v1",
            FuwenContracts.CompilerSemanticVersion,
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersion,
            "echo", "1", text, text, "routing/1", [], [activity],
            new CapabilityManifest([]),
            [
                new ActivityNode("echo", activityPath, activity, [new ArgumentBinding("value", new InputBinding([]))], text),
                new ReturnNode("return_result", returnPath, new NodeOutputBinding(activityPath, [])),
            ],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("echo", [
                    new WorkflowExecutionPhase([activityPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]));
    }

    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

    private static JsonElement Json(object? body) =>
        JsonDocument.Parse(JsonSerializer.Serialize(body, CamelCase)).RootElement;

    private static string Str(JsonElement element, string name) =>
        element.GetProperty(name).GetString()!;
}
#endif


