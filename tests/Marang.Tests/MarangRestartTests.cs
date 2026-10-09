#if NET10_0_OR_GREATER
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Marang.Http;
using Marang.Runs;
using Marang.SessionJournal;
using Penghou.Fuwen.Zhinu;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;
using Penghou.Qingniao;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Marang.Tests;

/// <summary>
/// M5.5 restart proof across REAL OS processes. A probe process sets up
/// correlated durable state and runs a bounded workflow to a parked wait,
/// then dies. A FRESH probe process reopens the same files, verifies,
/// resumes to terminal, and proves no duplicate work. This driver then
/// verifies Marang's stateless reconstruction, caller isolation, and the
/// production composition against the post-restart stores.
/// </summary>
public sealed class MarangRestartTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private const string Caller = "restart-probe";

    [Fact]
    public async Task Restart_preserves_durable_state_without_duplicate_work()
    {
        var directory = Path.Combine(Path.GetTempPath(), "marang-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await RunProbeAsync(directory, "1", ct);
            var manifest = ReadJson(Path.Combine(directory, "phase1.json"));
            await RunProbeAsync(directory, "2", ct);
            var result = ReadJson(Path.Combine(directory, "phase2.json"));

            // Cross-process identity continuity.
            result["runId"].Should().Be(manifest["runId"]);
            result["sameRun"].Should().Be("pass");
            result["sameSession"].Should().Be("pass");
            result["sameFingerprint"].Should().Be("pass");
            result["terminal"].Should().Be("pass");
            result["invocationCount"].Should().Be("pass");
            result["sequenceMonotonicAfter"].Should().Be("pass");
            result["zeroArtifacts"].Should().Be("pass");

            // Restart-safe observation through durable authority.
            result["preReattachHidden"].Should().Be("pass");
            result["reattach"].Should().Be("pass");
            result["detailEnvelope"].Should().Be("pass");
            result["delegationRun"].Should().Be("pass");
            result["delegationRunId"].Should().Be("pass");
            result["runProjection"].Should().Be("pass");
            result["journalEndpoint"].Should().Be("pass");
            result["journalAvailableEndpoint"].Should().Be("pass");
            result["volatileHidden"].Should().Be("pass");
            result["volatileDenied"].Should().Be("pass");
            result["malloryDenied"].Should().Be("pass");
            result["malloryJournalDenied"].Should().Be("pass");
            result["malloryReattachDenied"].Should().Be("pass");
            result["conflictRejected"].Should().Be("pass");
            result["revoked"].Should().Be("pass");
            result["revokeBlocked"].Should().Be("pass");
            result["expiredBlocked"].Should().Be("pass");

            await VerifyMarangPostRestartAsync(directory, manifest, ct);
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

    private static async Task VerifyMarangPostRestartAsync(
        string directory, Dictionary<string, string> manifest, CancellationToken ct)
    {
        var runId = Guid.Parse(manifest["runId"]);
        var sources = RunStoreComposition.Create(
            Path.Combine(directory, "zhinu.db"),
            Path.Combine(directory, "hongxian"),
            directory);
        try
        {
            // Production run source reconstructs the DTO from durable truth.
            var run = await sources.GetRunAsync(runId, ct);
            run.Should().NotBeNull();
            var steps = await sources.GetStepsAsync(runId, ct);
            steps.Should().Contain(s => s.StepKey == "demo/fetch" && s.Status == StepStatus.Completed);
            var plan = await sources.GetPlanAsync("1", ct);
            plan.Should().NotBeNull();
            var map = FuwenZhinuStepMapper.Map(plan!);
            map.ExecutionFingerprint.Should().Be(manifest["executionFingerprint"]);
            foreach (var step in steps)
            {
                FuwenZhinuStepMapper.TryMatchStepKey(map, step.StepKey, out _).Should().BeTrue();
            }

            // Caller isolation fail-closed on a fresh catalog (no pre-restart
            // acceptance survives; the run must not leak).
            var fresh = new MarangDelegationCatalog();
            (await RunProjectionEndpoints.GetRunAsync(
                fresh, Caller, runId, sources, () => Start, ct)).StatusCode.Should().Be(404);
            (await SessionJournalEndpoints.GetJournalAsync(
                fresh, Caller, runId, sources, sources, ct)).StatusCode.Should().Be(404);
            (await RunProjectionEndpoints.GetRunAsync(
                fresh, "mallory", runId, sources, () => Start, ct)).StatusCode.Should().Be(404);

            // Restart-safe reattachment through durable authority: the same
            // subject re-establishes visibility on a fresh in-memory cache and
            // reads the same delegation/run/session through public endpoints.
            var durable = new MarangDelegationCatalog(
                new Marang.DelegationAuthority.SqliteAcceptedDelegationStore(Path.Combine(directory, "authority.db")),
                new Marang.DelegationAuthority.SqliteDelegationVisibilityStore(Path.Combine(directory, "authority.db")));
            (await durable.ReattachAsync(Caller, new Penghou.Qingniao.DelegationId(Guid.Parse(manifest["delegationId"])), ct))
                .Should().BeTrue();
            (await RunProjectionEndpoints.GetRunAsync(
                durable, Caller, runId, sources, () => Start, ct)).StatusCode.Should().Be(200);
            var (reattachedDelegationRunStatus, reattachedDelegationRunBody) =
                await RunProjectionEndpoints.GetDelegationRunAsync(
                    durable, Caller, Guid.Parse(manifest["delegationId"]), sources, ct);
            reattachedDelegationRunStatus.Should().Be(200);
            reattachedDelegationRunBody.Should().NotBeNull();
            var (reattachedJournalStatus, reattachedJournalBody) = await SessionJournalEndpoints.GetJournalAsync(
                durable, Caller, runId, sources, sources, ct);
            reattachedJournalStatus.Should().Be(200);
            reattachedJournalBody.Should().BeOfType<SessionJournalView>().Which.Available.Should().BeTrue();
            (await RunProjectionEndpoints.GetRunAsync(
                durable, "mallory", runId, sources, () => Start, ct)).StatusCode.Should().Be(404);

            // Control without a live execution handle is unavailable (409),
            // never success and never silent work; strangers stay 404.
            var bareRuntime = new Penghou.Qingniao.DelegationRuntime(
                new MarangDelegationCatalog(), null,
                new Penghou.Qingniao.InMemoryProviderRegistry(),
                new Penghou.Qingniao.InMemoryExternalOperationProviderCatalog());
            (await Marang.Http.SupervisionHttpEndpoints.PostCancelAsync(
                bareRuntime, durable, Caller, Guid.Parse(manifest["delegationId"]), ct)).StatusCode.Should().Be(409);
            (await Marang.Http.SupervisionHttpEndpoints.PostCancelAsync(
                bareRuntime, durable, "mallory", Guid.Parse(manifest["delegationId"]), ct)).StatusCode.Should().Be(404);

            // Terminal revocation on the main binding: restoring access needs
            // a new acceptance. Nothing after this needs the binding.
            var visibility = new Marang.DelegationAuthority.SqliteDelegationVisibilityStore(Path.Combine(directory, "authority.db"));
            (await visibility.RevokeAsync(
                "marang", Caller, Guid.Parse(manifest["delegationId"]),
                Marang.DelegationAuthority.AcceptanceIdentity.InitialGeneration,
                "driver-revoke-1", "driver.revoked", ct))
                .Should().Be(Marang.DelegationAuthority.DelegationVisibilityRevokeStatus.Revoked);
            var afterRevoke = new MarangDelegationCatalog(
                new Marang.DelegationAuthority.SqliteAcceptedDelegationStore(Path.Combine(directory, "authority.db")),
                new Marang.DelegationAuthority.SqliteDelegationVisibilityStore(Path.Combine(directory, "authority.db")));
            (await afterRevoke.ReattachAsync(Caller, new Penghou.Qingniao.DelegationId(Guid.Parse(manifest["delegationId"])), ct))
                .Should().BeFalse();
            (await RunProjectionEndpoints.GetRunAsync(
                afterRevoke, Caller, runId, sources, () => Start, ct)).StatusCode.Should().Be(404);

            // The mechanism is live: a fresh delegation gets normal behavior.
            var acceptance = await fresh.AcceptAsync(
                new DelegationCallerScope(Caller), DelegationRequest(), ct);
            (await RunProjectionEndpoints.GetRunAsync(
                fresh, Caller, Guid.NewGuid(), sources, () => Start, ct)).StatusCode.Should().Be(404);
            acceptance.DelegationId.Should().NotBeNull();

            // Positive post-restart path on a fresh correlated run.
            await using var hongxian = new HongxianSqliteStoreSet(new HongxianSqliteOptions
            {
                RootPath = Path.Combine(directory, "hongxian"),
                Pooling = false,
            });
            var session = await hongxian.SessionStore.CreateAsync("ctx2", "resource/2", cancellationToken: ct);
            var zhinu = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(directory, "zhinu.db"),
            });
            var correlation = new RunCorrelation(
                acceptance.DelegationId, acceptance.DelegationId.Value.ToString("D"),
                Guid.NewGuid(), map.PlanRevision, map.ExecutionFingerprint,
                session.Id.Value.ToString("D"));
            var runId2 = correlation.WorkflowRunId;
            await zhinu.CreateRunAsync(
                new WorkflowRun
                {
                    Id = runId2, WorkflowName = "demo", WorkflowVersion = "1",
                    Status = WorkflowStatus.Running, CreatedAt = Start, UpdatedAt = Start,
                    MetadataJson = correlation.ToMetadataJson(),
                }, ct);
            await hongxian.EventStore.AppendAsync(
                new SessionEventRequest(
                    session.Id,
                    SessionParticipantAttribution.System("restart-probe", "marang"),
                    SessionEventTypes.UserMessage,
                    Start), ct);

            var (runStatus, _) = await RunProjectionEndpoints.GetRunAsync(
                fresh, Caller, runId2, sources, () => Start, ct);
            runStatus.Should().Be(200);
            var (journalStatus, journalBody) = await SessionJournalEndpoints.GetJournalAsync(
                fresh, Caller, runId2, sources, sources, ct);
            journalStatus.Should().Be(200);
            var journal = JsonDocument.Parse(JsonSerializer.Serialize(
                journalBody, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;
            journal.GetProperty("available").GetBoolean().Should().BeTrue();
            (await RunProjectionEndpoints.GetRunAsync(
                fresh, "mallory", runId2, sources, () => Start, ct)).StatusCode.Should().Be(404);
            (await SessionJournalEndpoints.GetJournalAsync(
                fresh, "mallory", runId2, sources, sources, ct)).StatusCode.Should().Be(404);

            if (sources is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    private static DelegationRequest DelegationRequest() => new(
        "restart-driver",
        "Driver verification",
        "test-provider",
        new WorkspaceReference("local", "workspace", "revision"),
        ["Done"],
        [],
        new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2));

    private static async Task RunProbeAsync(string directory, string phase, CancellationToken ct)
    {
        var config = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var probe = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Marang.RestartProbe", "bin", config, "net10.0", "Marang.RestartProbe.dll"));
        File.Exists(probe).Should().BeTrue($"Restart probe was not built at '{probe}'.");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", $"\"{probe}\" --phase {phase} --dir \"{directory}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        process.ExitCode.Should().Be(
            0,
            $"Restart probe phase {phase} failed.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
    }

    private static Dictionary<string, string> ReadJson(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
        ?? throw new InvalidOperationException($"Cannot read '{path}'.");
}
#endif
