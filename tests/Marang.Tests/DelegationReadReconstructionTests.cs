using System.Text.Json;
using FluentAssertions;
using Marang.DelegationAuthority;
using Marang.Http;
using Marang.Runs;
using Penghou.Hufu.Sqlite;
using Penghou.Qingniao;
using Penghou.Zhinu;

namespace Marang.Tests;

/// <summary>
/// Post-restart read reconstruction: without a live Qingniao runtime handle,
/// an authorized caller still inspects the same logical delegation. Detail
/// and evidence are rebuilt from durable sources (accepted envelope, Zhinu
/// run/output, session linkage); runtime-only fields are explicit nulls,
/// never synthesized values. Unknown and foreign delegations stay 404.
/// </summary>
public sealed class DelegationReadReconstructionTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private const string Caller = "operator-1";

    [Fact]
    public async Task Detail_reconstructs_from_durable_run_without_live_runtime()
    {
        var directory = NewDirectory();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var catalog = DurableCatalog(directory);
            var accepted = await catalog.AcceptAsync(new DelegationCallerScope(Caller), Request("recon-detail"), ct);
            var runId = Guid.NewGuid();
            var source = new FakeRunSource(Run(runId, WorkflowStatus.Running, accepted.DelegationId, null));

            // Fresh process: empty memory, same files, no live runtime entry.
            var restarted = DurableCatalog(directory);
            var runtime = BareRuntime();
            var (status, body) = await SupervisionHttpEndpoints.GetDetailAsync(
                runtime, restarted, Caller, accepted.DelegationId.Value, ct, runs: source);

            status.Should().Be(200);
            var detail = Json(body);
            Str(detail, "objective").Should().Be("Do the delegated work");
            Str(detail, "provider").Should().Be("recon-provider");
            Str(detail, "workspace").Should().Be("workspace");
            detail.GetProperty("state").ValueKind.Should().Be(JsonValueKind.Null);
            detail.GetProperty("runtimeAvailable").GetBoolean().Should().BeFalse();
            Str(detail, "runId").Should().Be(runId.ToString("D"));
            Str(detail, "runStatus").Should().Be("Running");
            Str(detail, "sessionId").Should().NotBeNullOrEmpty();
            detail.GetProperty("waiting").ValueKind.Should().Be(JsonValueKind.Null);
            detail.GetProperty("canCancel").GetBoolean().Should().BeFalse();
            detail.GetProperty("resultSummary").ValueKind.Should().Be(JsonValueKind.Null);
            detail.GetProperty("revision").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Detail_terminal_run_carries_durable_output()
    {
        var directory = NewDirectory();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var catalog = DurableCatalog(directory);
            var accepted = await catalog.AcceptAsync(new DelegationCallerScope(Caller), Request("recon-terminal"), ct);
            var runId = Guid.NewGuid();
            var source = new FakeRunSource(Run(runId, WorkflowStatus.Completed, accepted.DelegationId, "\"approved-output\""));

            var restarted = DurableCatalog(directory);
            var (status, body) = await SupervisionHttpEndpoints.GetDetailAsync(
                runtime: BareRuntime(), restarted, Caller, accepted.DelegationId.Value, ct, runs: source);

            status.Should().Be(200);
            var detail = Json(body);
            Str(detail, "runStatus").Should().Be("Completed");
            Str(detail, "resultSummary").Should().Contain("approved-output");
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Detail_unknown_foreign_and_sourceless_reads_fail_closed()
    {
        var directory = NewDirectory();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var catalog = DurableCatalog(directory);
            var accepted = await catalog.AcceptAsync(new DelegationCallerScope(Caller), Request("recon-closed"), ct);
            var runId = Guid.NewGuid();
            var source = new FakeRunSource(Run(runId, WorkflowStatus.Running, accepted.DelegationId, null));
            var restarted = DurableCatalog(directory);
            var runtime = BareRuntime();

            (await SupervisionHttpEndpoints.GetDetailAsync(
                runtime, restarted, Caller, Guid.NewGuid(), ct, runs: source)).StatusCode.Should().Be(404);
            (await SupervisionHttpEndpoints.GetDetailAsync(
                runtime, restarted, "someone-else", accepted.DelegationId.Value, ct, runs: source)).StatusCode.Should().Be(404);
            // Without a run source the historical behavior (404) is preserved.
            (await SupervisionHttpEndpoints.GetDetailAsync(
                runtime, restarted, Caller, accepted.DelegationId.Value, ct)).StatusCode.Should().Be(404);
            // A delegation with no durable run yet reports its authorized
            // envelope with null execution fields rather than 404.
            var (emptyStatus, emptyBody) = await SupervisionHttpEndpoints.GetDetailAsync(
                runtime, restarted, Caller, accepted.DelegationId.Value, ct, runs: new FakeRunSource(null));
            emptyStatus.Should().Be(200);
            var empty = Json(emptyBody);
            Str(empty, "objective").Should().Be("Do the delegated work");
            empty.GetProperty("runId").ValueKind.Should().Be(JsonValueKind.Null);
            empty.GetProperty("runtimeAvailable").GetBoolean().Should().BeFalse();
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Evidence_prefers_durable_receipts_without_live_result()
    {
        var directory = NewDirectory();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var catalog = DurableCatalog(directory);
            var accepted = await catalog.AcceptAsync(new DelegationCallerScope(Caller), Request("recon-evidence"), ct);
            var runId = Guid.NewGuid();
            var source = new FakeRunSource(Run(runId, WorkflowStatus.Completed, accepted.DelegationId, "\"evidence-output\""));

            var restarted = DurableCatalog(directory);
            var (status, body) = await SupervisionHttpEndpoints.GetEvidenceAsync(
                BareRuntime(), restarted, Caller, accepted.DelegationId.Value, ct, runs: source);

            status.Should().Be(200);
            var evidence = Json(body);
            evidence.GetProperty("hasResult").GetBoolean().Should().BeFalse();
            evidence.GetProperty("runtimeAvailable").GetBoolean().Should().BeFalse();
            Str(evidence, "runStatus").Should().Be("Completed");
            Str(evidence, "terminalOutput").Should().Contain("evidence-output");
            Str(evidence, "sessionId").Should().NotBeNullOrEmpty();
            evidence.GetProperty("findings").GetArrayLength().Should().Be(0);
            evidence.GetProperty("artifacts").GetArrayLength().Should().Be(0);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Evidence_running_run_reports_absence_not_404()
    {
        var directory = NewDirectory();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var catalog = DurableCatalog(directory);
            var accepted = await catalog.AcceptAsync(new DelegationCallerScope(Caller), Request("recon-running"), ct);
            var runId = Guid.NewGuid();
            var source = new FakeRunSource(Run(runId, WorkflowStatus.Running, accepted.DelegationId, null));

            var restarted = DurableCatalog(directory);
            var (status, body) = await SupervisionHttpEndpoints.GetEvidenceAsync(
                BareRuntime(), restarted, Caller, accepted.DelegationId.Value, ct, runs: source);

            status.Should().Be(200);
            var evidence = Json(body);
            evidence.GetProperty("hasResult").GetBoolean().Should().BeFalse();
            evidence.GetProperty("terminalOutput").ValueKind.Should().Be(JsonValueKind.Null);

            (await SupervisionHttpEndpoints.GetEvidenceAsync(
                BareRuntime(), restarted, "someone-else", accepted.DelegationId.Value, ct, runs: source)).StatusCode.Should().Be(404);
            (await SupervisionHttpEndpoints.GetEvidenceAsync(
                BareRuntime(), restarted, Caller, accepted.DelegationId.Value, ct)).StatusCode.Should().Be(404);
        }
        finally
        {
            Delete(directory);
        }
    }

    private static MarangDelegationCatalog DurableCatalog(string directory) => new(
        TestAcceptanceGate.AllowAll(),
        new SqliteAcceptedDelegationStore(Path.Combine(directory, "authority.db")),
        new SqliteDelegationVisibilityStore(Path.Combine(directory, "authority.db")),
        () => Start);

    private static DelegationRuntime BareRuntime() => new(
        new MarangDelegationCatalog(TestAcceptanceGate.AllowAll()), null,
        new InMemoryProviderRegistry(), new InMemoryExternalOperationProviderCatalog());

    private static DelegationRequest Request(string requestKey) => new(
        requestKey, "Do the delegated work", "recon-provider",
        new WorkspaceReference("local", "workspace", "revision"),
        ["Done"], [], new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2));

    private static WorkflowRun Run(Guid runId, WorkflowStatus status, DelegationId delegationId, string? outputJson)
    {
        var sessionId = Guid.NewGuid();
        return new WorkflowRun
        {
            Id = runId,
            WorkflowName = "demo",
            WorkflowVersion = "1",
            Status = status,
            CreatedAt = Start,
            UpdatedAt = Start.AddMinutes(1),
            CompletedAt = status == WorkflowStatus.Running ? null : Start.AddMinutes(2),
            OutputJson = outputJson,
            MetadataJson = new RunCorrelation(
                delegationId, delegationId.Value.ToString("D"), runId, "1", "fingerprint/recon",
                sessionId.ToString("D")).ToMetadataJson(),
        };
    }

    private static JsonElement Json(object? body) =>
        JsonDocument.Parse(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;

    private static string Str(JsonElement element, string name) =>
        element.GetProperty(name).GetString()!;

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "marang-recon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Delete(string directory)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        catch
        {
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FakeRunSource(WorkflowRun? run) : IRunProjectionSource
    {
        public ValueTask<Penghou.Fuwen.WorkflowPlan?> GetPlanAsync(string planRevision, CancellationToken cancellationToken = default) =>
            new((Penghou.Fuwen.WorkflowPlan?)null);

        public ValueTask<WorkflowRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
            new(run is not null && runId == run.Id ? run : null);

        public ValueTask<IReadOnlyList<WorkflowStepRun>> GetStepsAsync(Guid runId, CancellationToken cancellationToken = default) =>
            new((IReadOnlyList<WorkflowStepRun>)[]);

        public ValueTask<long> GetThroughSequenceAsync(Guid runId, CancellationToken cancellationToken = default) =>
            new(0L);

        public ValueTask<Guid?> GetRunIdByDelegationAsync(DelegationId delegationId, CancellationToken cancellationToken = default)
        {
            if (run is null)
            {
                return new((Guid?)null);
            }

            var correlation = RunCorrelation.TryParseMetadataJson(run.MetadataJson, run.Id);
            return new(correlation is not null && correlation.DelegationId == delegationId ? run.Id : (Guid?)null);
        }
    }
}


