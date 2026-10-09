#if NET10_0_OR_GREATER
using System.Text.Json;
using FluentAssertions;
using Marang.Runs;
using Marang.SessionJournal;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;
using Penghou.Qingniao;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Marang.Tests;

/// <summary>
/// M5.4 session-journal behavior: reconciled Hongxian evidence mapped to an
/// operator journal with recovery states, never touching Zhinu execution
/// truth. Sqlite-backed integration proves durable reads; the pure classifier
/// covers every operator state.
/// </summary>
public sealed class MarangSessionJournalTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private const string Caller = "operator-1";

    [Fact]
    public async Task Journal_reports_reconciled_entries_and_running_recovery()
    {
        using var hosts = new Hosts();
        var ct = TestContext.Current.CancellationToken;
        var sessionId = await hosts.CreateSessionAsync(ct);
        await hosts.AppendAsync(sessionId, SessionEventTypes.UserMessage, ct);
        await hosts.AppendAsync(sessionId, SessionEventTypes.ExecutionStarted, ct);
        var runId = await hosts.CreateRunAsync(sessionId, WorkflowStatus.Running, ct);

        var (status, body) = await SessionJournalEndpoints.GetJournalAsync(
            hosts.Catalog, Caller, runId, hosts.Runs, hosts.Sessions, ct);

        status.Should().Be(200);
        var journal = Json(body);
        jsonTrue(journal, "available").Should().BeTrue();
        Str(journal, "recoveryState").Should().Be("running");
        Str(journal, "operatorState").Should().Be("Ready");
        var entries = journal.GetProperty("entries");
        entries.GetArrayLength().Should().BeGreaterThan(0);
        // Graph truth is untouched by journal reads (covered by run endpoint tests).
    }

    [Fact]
    public async Task Missing_session_reports_unavailable_journal_without_erasing_the_run()
    {
        using var hosts = new Hosts();
        var ct = TestContext.Current.CancellationToken;
        // Correlation names a session that was never created.
        var runId = await hosts.CreateRunAsync(SessionId.New(), WorkflowStatus.Running, ct);

        var (status, body) = await SessionJournalEndpoints.GetJournalAsync(
            hosts.Catalog, Caller, runId, hosts.Runs, hosts.Sessions, ct);

        status.Should().Be(200);
        var journal = Json(body);
        jsonTrue(journal, "available").Should().BeFalse();
        Str(journal, "recoveryState").Should().Be("unavailable");
    }

    [Fact]
    public async Task Foreign_caller_cannot_read_a_journal()
    {
        using var hosts = new Hosts();
        var ct = TestContext.Current.CancellationToken;
        var sessionId = await hosts.CreateSessionAsync(ct);
        var runId = await hosts.CreateRunAsync(sessionId, WorkflowStatus.Running, ct);

        (await SessionJournalEndpoints.GetJournalAsync(
            hosts.Catalog, "someone-else", runId, hosts.Runs, hosts.Sessions, ct)).StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task Journal_and_projection_reconstruct_after_reopen()
    {
        var plan = CreatePlan();
        var map = FuwenZhinuStepMapper.Map(plan);
        var directory = Path.Combine(Path.GetTempPath(), "marang-reopen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ct = TestContext.Current.CancellationToken;
        var catalog = new MarangDelegationCatalog();
        // The acceptance catalog is process-local by design (Qingniao retention
        // model); the restart below reopens only the durable Zhinu/Hongxian
        // stores, proving reconstruction from persisted state.
        var acceptance = await catalog.AcceptAsync(
            new DelegationCallerScope(Caller), JournalRequest("reopen-journal"), ct);
        var zhinuPath = Path.Combine(directory, "zhinu.db");
        var hongxianRoot = Path.Combine(directory, "hongxian");
        var zhinu = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = zhinuPath });
        await using var hongxian = new HongxianSqliteStoreSet(new HongxianSqliteOptions { RootPath = hongxianRoot, Pooling = false });
        var sessionId = (await hongxian.SessionStore.CreateAsync("ctx", "resource/1", cancellationToken: ct)).Id;
        await hongxian.EventStore.AppendAsync(
            new SessionEventRequest(sessionId, Participant(), SessionEventTypes.UserMessage, Start), cancellationToken: ct);

        var correlation = new RunCorrelation(
            acceptance.DelegationId, acceptance.DelegationId.Value.ToString("D"),
            Guid.NewGuid(), map.PlanRevision, map.ExecutionFingerprint, sessionId.Value.ToString("D"));
        var runId = correlation.WorkflowRunId;
        await zhinu.CreateRunAsync(
            new WorkflowRun
            {
                Id = runId, WorkflowName = "demo", WorkflowVersion = "1",
                Status = WorkflowStatus.Running, CreatedAt = Start, UpdatedAt = Start,
                DefinitionFingerprint = map.ExecutionFingerprint,
                MetadataJson = correlation.ToMetadataJson(),
            }, ct);
        await zhinu.AppendEventAsync(runId, "test.step", null, cancellationToken: ct);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            var zhinuReopened = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = Path.Combine(directory, "zhinu.db") });
            var runs = new ConfiguredRunProjectionSource(
                zhinuReopened,
                zhinuReopened,
                (_, _) => ValueTask.FromResult<WorkflowPlan?>(plan),
                new HongxianSqliteStoreSet(new HongxianSqliteOptions
                {
                    RootPath = Path.Combine(directory, "hongxian"),
                    Pooling = false,
                }));

            var (journalStatus, journalBody) = await SessionJournalEndpoints.GetJournalAsync(
                catalog, Caller, runId, runs, runs, ct);
            journalStatus.Should().Be(200);
            var journal = Json(journalBody);
            jsonTrue(journal, "available").Should().BeTrue();
            Str(journal, "recoveryState").Should().Be("running");

            if (runs is IDisposable disposable)
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

    [Theory]
    [InlineData(WorkflowStatus.Running, SessionOperatorState.Ready, true, true, "running")]
    [InlineData(WorkflowStatus.Running, SessionOperatorState.AwaitingInput, true, true, "waiting")]
    [InlineData(WorkflowStatus.Running, SessionOperatorState.AwaitingApproval, true, true, "waiting")]
    [InlineData(WorkflowStatus.Running, SessionOperatorState.Recovering, true, true, "recovering")]
    [InlineData(WorkflowStatus.Running, SessionOperatorState.ReconciliationRequired, true, true, "inconsistent")]
    [InlineData(WorkflowStatus.Running, SessionOperatorState.Corrupt, true, true, "inconsistent")]
    [InlineData(WorkflowStatus.Completed, SessionOperatorState.Ready, true, true, "terminal")]
    [InlineData(WorkflowStatus.Failed, SessionOperatorState.Corrupt, true, true, "terminal")]
    [InlineData(WorkflowStatus.Running, SessionOperatorState.Ready, false, true, "unavailable")]
    [InlineData(WorkflowStatus.Running, SessionOperatorState.Ready, true, false, "unavailable")]
    public void Recovery_classifier_maps_upstream_facts_without_invention(
        WorkflowStatus runStatus,
        SessionOperatorState operatorState,
        bool hasSession,
        bool readOk,
        string expected)
    {
        RecoveryStates.Classify(runStatus, operatorState, hasSession, readOk).Should().Be(expected);
    }

    private static JsonElement Json(object? body) =>
        JsonDocument.Parse(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement;

    private static string Str(JsonElement element, string name) =>
        element.GetProperty(name).GetString()!;

    private static bool jsonTrue(JsonElement element, string name) =>
        element.GetProperty(name).GetBoolean();

    private static SessionParticipantAttribution Participant() =>
        SessionParticipantAttribution.System("test", "hongxian");

    private static DelegationRequest JournalRequest(string requestKey) => new(
        requestKey,
        "Do the work",
        "test-provider",
        new WorkspaceReference("local", "workspace", "revision"),
        ["Done"],
        [],
        new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2));

    private static Penghou.Fuwen.WorkflowPlan CreatePlan()
    {
        var text = new Penghou.Fuwen.PrimitiveType(Penghou.Fuwen.FuwenPrimitiveKind.String);
        var activity = new Penghou.Fuwen.DescriptorReference(
            Penghou.Fuwen.DescriptorKind.Activity, "sample.echo", "1",
            new Penghou.Fuwen.ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var activityPath = Penghou.Fuwen.StructuralNodeIdentity.Create("echo", "echo");
        var returnPath = Penghou.Fuwen.StructuralNodeIdentity.Create("echo", "return_result");
        return new Penghou.Fuwen.WorkflowPlan(
            Penghou.Fuwen.FuwenContracts.IrVersion,
            "fuwen-language/v1",
            Penghou.Fuwen.FuwenContracts.CompilerSemanticVersion,
            Penghou.Fuwen.FuwenContracts.CanonicalJsonVersion,
            Penghou.Fuwen.FuwenContracts.ExecutionFingerprintVersion,
            "echo", "1", text, text, "routing/1", [], [activity],
            new Penghou.Fuwen.CapabilityManifest([]),
            [
                new Penghou.Fuwen.ActivityNode("echo", activityPath, activity, [new Penghou.Fuwen.ArgumentBinding("value", new Penghou.Fuwen.InputBinding([]))], text),
                new Penghou.Fuwen.ReturnNode("return_result", returnPath, new Penghou.Fuwen.NodeOutputBinding(activityPath, [])),
            ],
            new Penghou.Fuwen.WorkflowExecutionOrder([
                new Penghou.Fuwen.WorkflowExecutionRegion("echo", [
                    new Penghou.Fuwen.WorkflowExecutionPhase([activityPath]),
                    new Penghou.Fuwen.WorkflowExecutionPhase([returnPath]),
                ]),
            ]));
    }

    private sealed class Hosts : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "marang-journal-" + Guid.NewGuid().ToString("N"));
        private readonly SqliteWorkflowStore _zhinu;
        private readonly HongxianSqliteStoreSet _hongxian;

        public MarangDelegationCatalog Catalog { get; } = new();
        public ConfiguredRunProjectionSource Runs { get; }
        public ConfiguredRunProjectionSource Sessions => Runs;

        public Hosts()
        {
            Directory.CreateDirectory(_directory);
            _zhinu = new SqliteWorkflowStore(new ZhinuSqliteOptions
            {
                DatabasePath = Path.Combine(_directory, "zhinu.db"),
            });
            _hongxian = new HongxianSqliteStoreSet(new HongxianSqliteOptions
            {
                RootPath = Path.Combine(_directory, "hongxian"),
                Pooling = false,
            });
            Runs = new ConfiguredRunProjectionSource(_zhinu, _zhinu, (_, _) => ValueTask.FromResult<WorkflowPlan?>(null), _hongxian);
        }

        public async Task<SessionId> CreateSessionAsync(CancellationToken ct)
        {
            var session = await _hongxian.SessionStore.CreateAsync("ctx", "resource/1", cancellationToken: ct);
            return session.Id;
        }

        public async Task AppendAsync(SessionId sessionId, string eventType, CancellationToken ct)
        {
            await _hongxian.EventStore.AppendAsync(
                new SessionEventRequest(sessionId, Participant(), eventType, Start), cancellationToken: ct);
        }

        public async Task<Guid> CreateRunAsync(SessionId sessionId, WorkflowStatus status, CancellationToken ct)
        {
            var acceptance = await Catalog.AcceptAsync(
                new DelegationCallerScope(Caller), JournalRequest($"journal-{Guid.NewGuid():N}"), ct);
            // Plan identity is stubbed for journal-only reads (no topology join here).
            var correlation = new RunCorrelation(
                acceptance.DelegationId, acceptance.DelegationId.Value.ToString("D"),
                Guid.NewGuid(), "1", "fp", sessionId.Value.ToString("D"));
            await _zhinu.CreateRunAsync(
                new WorkflowRun
                {
                    Id = correlation.WorkflowRunId,
                    WorkflowName = "demo",
                    WorkflowVersion = "1",
                    Status = status,
                    CreatedAt = Start,
                    UpdatedAt = Start,
                    MetadataJson = correlation.ToMetadataJson(),
                }, ct);
            return correlation.WorkflowRunId;
        }

        public void Dispose()
        {
            _hongxian.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
#endif
