#if NET10_0_OR_GREATER
using System.Text.Json;
using FluentAssertions;
using Marang.Codex;
using Marang.Runs;
using Marang.SessionJournal;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Hongxian;
using Penghou.Hongxian.Sqlite;
using Penghou.Hufu;
using Penghou.Hufu.Sqlite;
using Penghou.Qingniao;
using Penghou.Qingniao.Codex;
using Penghou.Qingniao.Hufu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite;

namespace Marang.Tests;

/// <summary>
/// M5 provider-backed projection proof: the real published
/// <see cref="CodexExecAdapter"/> is registered through the normal
/// provider-selection mechanism, a Zhinu workflow activity executes a full
/// delegation through it to a terminal outcome, the durable Zhinu run is
/// rebuilt by the existing run projection, and the projection carries the
/// authoritative snapshot watermark. No fake provider, no shadow execution
/// model, no provider-specific projection branches.
/// </summary>
public sealed class MarangProviderProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NotBefore = Now.AddMinutes(-1);
    private static readonly DateTimeOffset ExpiresAt = Now.AddDays(1);
    private static readonly AuthorityStoreActor Actor = new("tenant", "host", "session-1");
    private static readonly AuthenticatedAuthorityContext ParentContext =
        new("tenant", "supervisor", "run-parent", "rev", "fence");
    private const string Workspace = "workspace";
    private const string ParentGrantId = "grant-codex-parent";
    private const string Caller = "operator-1";
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Provider_backed_execution_projects_with_snapshot_watermark()
    {
        var directory = Path.Combine(Path.GetTempPath(), "marang-provider-proj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ct = TestContext.Current.CancellationToken;
        var files = new TemporaryWorkspace(Path.Combine(directory, "work"));
        var db = new TemporaryDatabase(Path.Combine(directory, "hufu"));
        var time = new FixedTimeProvider(Now);
        await PublishParentAsync(db.DatabasePath, time);

        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            SuccessTranscript("codex-thread-1"), exitCode: 0));
        var preflight = new HufuDelegationAuthorityPreflight(
            DerivationStore(db.DatabasePath, time),
            Actor,
            ParentContext,
            (delegationId, generation) => new AuthenticatedAuthorityContext(
                "tenant", "delegation", $"run-{delegationId}", "rev", generation));
        var catalog = new MarangDelegationCatalog(TestAcceptanceGate.AllowAll());
        var providers = new InMemoryProviderRegistry();
        var adapters = new InMemoryExternalOperationProviderCatalog();
        var adapter = CodexProviderRegistration.Register(
            providers,
            adapters,
            new CodexExecOptions("Run one bounded task", files.Workspace, files.Root),
            factory,
            clock: () => Now);
        adapter.Should().NotBeNull();
        var runtime = new DelegationRuntime(
            catalog,
            new MarangAdmissionVerifier(),
            providers,
            adapters,
            now: () => Now,
            authorityPreflight: preflight);

        var delegated = new DelegationCapture();
        var zhinuPath = Path.Combine(directory, "zhinu.db");
        var zhinu = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = zhinuPath });
        await using var engine = new WorkflowEngine(
            zhinu,
            new WorkflowRegistry().Register("task", "1", new DelegatingWorkflow(runtime, delegated)),
            new ZhinuOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(5),
                LeaseDuration = TimeSpan.FromMinutes(5),
            });
        var runId = await engine.StartAsync("task", "1", "in", cancellationToken: ct);
        await engine.ExecuteAsync(runId, ct);
        var result = await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
        result.Should().Be("delegated-ok");
        factory.Spawns.Should().Be(1);
        delegated.Id.Should().NotBe(Guid.Empty);

        // Host correlation: the Zhinu run records which delegation it executed.
        var plan = CreatePlan();
        var map = FuwenZhinuStepMapper.Map(plan);
        var correlation = new RunCorrelation(
            delegated.Id,
            delegated.Id.Value.ToString("D"),
            runId,
            map.PlanRevision,
            map.ExecutionFingerprint);
        await zhinu.UpdateRunMetadataAsync(runId, correlation.ToMetadataJson(), ct);

        await using var hongxian = new HongxianSqliteStoreSet(new HongxianSqliteOptions
        {
            RootPath = Path.Combine(directory, "hongxian"),
            Pooling = false,
        });
        try
        {
            var source = new ConfiguredRunProjectionSource(
                zhinu, zhinu, zhinu,
                (_, _) => ValueTask.FromResult<WorkflowPlan?>(plan),
                hongxian);
            var (status, body) = await RunProjectionEndpoints.GetRunAsync(
                catalog, Caller, runId, source, () => DateTimeOffset.UtcNow, ct, gate: TestResourceGate.AllowAll());

            status.Should().Be(200);
            var projection = Json(body);
            Str(projection, "status").Should().Be("completed");
            var nodes = projection.GetProperty("nodes");
            var byId = nodes.EnumerateArray().ToDictionary(node => Str(node, "id"));
            Str(byId["task/execute"], "status").Should().Be("completed");

            // The projection carries the authoritative snapshot watermark, not
            // an independent max-sequence guess.
            var expected = await zhinu.ReadRunSnapshotAsync(runId, new RunSnapshotOptions(), ct);
            expected.Should().NotBeNull();
            Str(projection, "durableSequence").Should().Be(expected!.ThroughDurableSequence.ToString());

            // The generic contract carries no provider-specific fields.
            JsonSerializer.Serialize(body).Should().NotContain("codex");

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
    public async Task Completed_provider_work_survives_teardown_without_respawn()
    {
        // Completed-pre-crash proof: provider-backed work finishes, then every
        // process-local component is torn down. Two successive reopens rebuild
        // the identical projection from durable state alone, the session
        // journal stays available, and no provider process ever spawns again.
        // This does NOT prove recovery of an in-flight provider operation.
        var directory = Path.Combine(Path.GetTempPath(), "marang-provider-reopen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ct = TestContext.Current.CancellationToken;
        var files = new TemporaryWorkspace(Path.Combine(directory, "work"));
        var db = new TemporaryDatabase(Path.Combine(directory, "hufu"));
        var time = new FixedTimeProvider(Now);
        await PublishParentAsync(db.DatabasePath, time);

        var factory1 = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            SuccessTranscript("codex-thread-1"), exitCode: 0));
        var preflight = new HufuDelegationAuthorityPreflight(
            DerivationStore(db.DatabasePath, time),
            Actor,
            ParentContext,
            (delegationId, generation) => new AuthenticatedAuthorityContext(
                "tenant", "delegation", $"run-{delegationId}", "rev", generation));
        var catalog = new MarangDelegationCatalog(TestAcceptanceGate.AllowAll());
        var providers = new InMemoryProviderRegistry();
        var adapters = new InMemoryExternalOperationProviderCatalog();
        CodexProviderRegistration.Register(
            providers,
            adapters,
            new CodexExecOptions("Run one bounded task", files.Workspace, files.Root),
            factory1,
            clock: () => Now);
        var runtime = new DelegationRuntime(
            catalog,
            new MarangAdmissionVerifier(),
            providers,
            adapters,
            now: () => Now,
            authorityPreflight: preflight);

        var delegated = new DelegationCapture();
        var zhinuPath = Path.Combine(directory, "zhinu.db");
        var plan = CreatePlan();
        var map = FuwenZhinuStepMapper.Map(plan);
        var hongxianRoot = Path.Combine(directory, "hongxian");
        Guid runId;
        string beforeSequence;
        Dictionary<string, string> beforeNodes;
        // Phase 1: live execution to terminal through the real provider.
        {
            var zhinu = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = zhinuPath });
            await using var engine = new WorkflowEngine(
                zhinu,
                new WorkflowRegistry().Register("task", "1", new DelegatingWorkflow(runtime, delegated)),
                new ZhinuOptions { PollInterval = TimeSpan.FromMilliseconds(5) });
            runId = await engine.StartAsync("task", "1", "in", cancellationToken: ct);
            await engine.ExecuteAsync(runId, ct);
            await engine.WaitForCompletionAsync<string>(runId, cancellationToken: ct);
            factory1.Spawns.Should().Be(1);
            var terminal = await runtime.GetAsync(delegated.Id, ct);
            DelegationLifecycle.IsTerminal(terminal.Progress.State).Should().BeTrue();
            terminal.Progress.State.Should().Be(DelegationState.Completed);

            await using var hongxian = new HongxianSqliteStoreSet(new HongxianSqliteOptions
            {
                RootPath = hongxianRoot,
                Pooling = false,
            });
            var session = await hongxian.SessionStore.CreateAsync("provider-proof", "resource/1", cancellationToken: ct);
            await hongxian.EventStore.AppendAsync(
                new SessionEventRequest(
                    session.Id,
                    SessionParticipantAttribution.System("provider-proof", "marang"),
                    SessionEventTypes.UserMessage,
                    Start),
                ct);
            var correlation = new RunCorrelation(
                delegated.Id,
                delegated.Id.Value.ToString("D"),
                runId,
                map.PlanRevision,
                map.ExecutionFingerprint,
                session.Id.Value.ToString("D"));
            await zhinu.UpdateRunMetadataAsync(runId, correlation.ToMetadataJson(), ct);
            var source = new ConfiguredRunProjectionSource(
                zhinu, zhinu, zhinu,
                (_, _) => ValueTask.FromResult<WorkflowPlan?>(plan),
                hongxian);
            var (status, body) = await RunProjectionEndpoints.GetRunAsync(
                catalog, Caller, runId, source, () => DateTimeOffset.UtcNow, ct, gate: TestResourceGate.AllowAll());
            status.Should().Be(200);
            var projection = Json(body);
            Str(projection, "status").Should().Be("completed");
            beforeSequence = Str(projection, "durableSequence");
            beforeNodes = projection.GetProperty("nodes").EnumerateArray()
                .ToDictionary(node => Str(node, "id"), node => Str(node, "status"));
            var (journalStatus, journalBody) = await SessionJournalEndpoints.GetJournalAsync(
                catalog, Caller, runId, source, source, ct, gate: TestResourceGate.AllowAll());
            journalStatus.Should().Be(200);
            Json(journalBody).GetProperty("available").GetBoolean().Should().BeTrue();

            if (source is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Phase 2: fresh components, same files, no execution anywhere. A new
        // scripted factory proves no second spawn: had anything re-entered
        // provider execution, it would have succeeded and been counted.
        var factory2 = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            SuccessTranscript("codex-thread-1"), exitCode: 0));
        await using (var hongxian2 = new HongxianSqliteStoreSet(new HongxianSqliteOptions
        {
            RootPath = hongxianRoot,
            Pooling = false,
        }))
        {
            var reopened = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = zhinuPath });
            var source = new ConfiguredRunProjectionSource(
                reopened, reopened, reopened,
                (_, _) => ValueTask.FromResult<WorkflowPlan?>(plan),
                hongxian2);
            var (status, body) = await RunProjectionEndpoints.GetRunAsync(
                catalog, Caller, runId, source, () => DateTimeOffset.UtcNow, ct, gate: TestResourceGate.AllowAll());

            status.Should().Be(200);
            var projection = Json(body);
            Str(projection, "status").Should().Be("completed");
            Str(projection, "durableSequence").Should().Be(beforeSequence);
            projection.GetProperty("nodes").EnumerateArray()
                .ToDictionary(node => Str(node, "id"), node => Str(node, "status"))
                .Should().BeEquivalentTo(beforeNodes);

            var reread = await reopened.GetRunAsync(runId, ct);
            reread.Should().NotBeNull();
            var recorrelation = RunCorrelation.TryParseMetadataJson(reread!.MetadataJson, runId);
            recorrelation.Should().NotBeNull();
            recorrelation!.DelegationId.Should().Be(delegated.Id);
            recorrelation.SessionId.Should().NotBeNullOrWhiteSpace();

            var (journalStatus, journalBody) = await SessionJournalEndpoints.GetJournalAsync(
                catalog, Caller, runId, source, source, ct, gate: TestResourceGate.AllowAll());
            journalStatus.Should().Be(200);
            Json(journalBody).GetProperty("available").GetBoolean().Should().BeTrue();

            if (source is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        factory2.Spawns.Should().Be(0);

        // Phase 3: reopen once more; the result stays stable.
        await using (var hongxian3 = new HongxianSqliteStoreSet(new HongxianSqliteOptions
        {
            RootPath = hongxianRoot,
            Pooling = false,
        }))
        {
            try
            {
                var reopened = new SqliteWorkflowStore(new ZhinuSqliteOptions { DatabasePath = zhinuPath });
                var source = new ConfiguredRunProjectionSource(
                    reopened, reopened, reopened,
                    (_, _) => ValueTask.FromResult<WorkflowPlan?>(plan),
                    hongxian3);
                var (status, body) = await RunProjectionEndpoints.GetRunAsync(
                    catalog, Caller, runId, source, () => DateTimeOffset.UtcNow, ct, gate: TestResourceGate.AllowAll());

                status.Should().Be(200);
                Str(Json(body), "durableSequence").Should().Be(beforeSequence);

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
    }

    [Fact]
    public async Task Unknown_provider_fails_clearly_without_execution()
    {
        using var files = new TemporaryWorkspace(Path.Combine(Path.GetTempPath(), "marang-provider-unknown-" + Guid.NewGuid().ToString("N")));
        using var db = new TemporaryDatabase(Path.Combine(Path.GetTempPath(), "marang-provider-unknown-hufu-" + Guid.NewGuid().ToString("N")));
        var ct = TestContext.Current.CancellationToken;
        var time = new FixedTimeProvider(Now);
        await PublishParentAsync(db.DatabasePath, time);

        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            SuccessTranscript("codex-thread-1"), exitCode: 0));
        var preflight = new HufuDelegationAuthorityPreflight(
            DerivationStore(db.DatabasePath, time),
            Actor,
            ParentContext,
            (delegationId, generation) => new AuthenticatedAuthorityContext(
                "tenant", "delegation", $"run-{delegationId}", "rev", generation));
        var catalog = new MarangDelegationCatalog(TestAcceptanceGate.AllowAll());
        var providers = new InMemoryProviderRegistry();
        var adapters = new InMemoryExternalOperationProviderCatalog();
        CodexProviderRegistration.Register(
            providers,
            adapters,
            new CodexExecOptions("Run one bounded task", files.Workspace, files.Root),
            factory,
            clock: () => Now);
        var runtime = new DelegationRuntime(
            catalog,
            new MarangAdmissionVerifier(),
            providers,
            adapters,
            now: () => Now,
            authorityPreflight: preflight);

        var request = new DelegationRequest(
            "unknown-provider",
            "Do the work",
            "no-such-provider",
            new WorkspaceReference("local", Workspace, null),
            ["Done"],
            [],
            new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2),
            parentGrantId: ParentGrantId,
            requestedAuthority: new Penghou.Qingniao.RequestedAuthority(
                [AuthorityAction.ExecuteProcess.ToString()],
                new RequestedAuthorityScope(Workspace, "src", RequestedAuthorityScopeKind.Subtree),
                [],
                NotBefore,
                ExpiresAt));

        var handle = await runtime.DelegateAsync(new DelegationCallerScope(Caller), request, ct);
        var snapshot = await runtime.GetAsync(handle.DelegationId, ct);
        for (var index = 0; index < 10 && !DelegationLifecycle.IsTerminal(snapshot.Progress.State); index++)
        {
            snapshot = await runtime.PumpAsync(handle.DelegationId, snapshot.Progress.Revision, ct);
        }

        DelegationLifecycle.IsTerminal(snapshot.Progress.State).Should().BeTrue();
        // An unregistered provider parks the delegation for supervisor review
        // instead of executing it: terminal, never completed, zero spawns.
        snapshot.Progress.State.Should().Be(DelegationState.NeedsSupervisor);
        factory.Spawns.Should().Be(0, "an unregistered provider must never execute");
    }

    private sealed class DelegationCapture
    {
        public DelegationId Id { get; private set; } = new(Guid.Empty);

        public void Capture(DelegationId id) => Id = id;
    }

    private sealed class DelegatingWorkflow(
        DelegationRuntime runtime,
        DelegationCapture capture) : IWorkflow<string, string>
    {
        public async Task<string> RunAsync(WorkflowContext context, string input, CancellationToken cancellationToken)
        {
            return await context.StepAsync(
                "task/execute",
                input,
                async (value, stepCt) =>
                {
                    var handle = await runtime.DelegateAsync(
                        new DelegationCallerScope(Caller),
                        new DelegationRequest(
                            "provider-backed-execution",
                            "Run one bounded task",
                            CodexProviderRegistration.ProviderName,
                            new WorkspaceReference("local", Workspace, null),
                            ["Done"],
                            [],
                            new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2),
                            parentGrantId: ParentGrantId,
                            requestedAuthority: new Penghou.Qingniao.RequestedAuthority(
                                [AuthorityAction.ExecuteProcess.ToString()],
                                new RequestedAuthorityScope(Workspace, "src", RequestedAuthorityScopeKind.Subtree),
                                [],
                                NotBefore,
                                ExpiresAt)),
                        stepCt);
                    capture.Capture(handle.DelegationId);
                    var snapshot = await runtime.GetAsync(handle.DelegationId, stepCt);
                    for (var index = 0;
                        index < 10 && !DelegationLifecycle.IsTerminal(snapshot.Progress.State);
                        index++)
                    {
                        snapshot = await runtime.PumpAsync(
                            handle.DelegationId, snapshot.Progress.Revision, stepCt);
                    }

                    return snapshot.Progress.State == DelegationState.Completed
                        ? "delegated-ok"
                        : throw new InvalidOperationException(
                            $"The delegated execution did not complete: {snapshot.Progress.State}.");
                },
                cancellationToken: cancellationToken);
        }
    }

    private static string[] SuccessTranscript(string threadId) =>
    [
        """{"type":"thread.started","thread_id":"THREAD"}""".Replace("THREAD", threadId, StringComparison.Ordinal),
        """{"type":"turn.started"}""",
        """{"type":"turn.completed","usage":{"input_tokens":120,"cached_input_tokens":30,"output_tokens":45}}""",
    ];

    private static IAuthorityDerivationStore DerivationStore(string path, TimeProvider time) =>
        new SqliteAuthorityStore(
            path,
            new BoundedAuthorityIssuanceAuthorizer(new DeriveApprovalTrustSource(Actor), new AllowAllPolicy(), time),
            time);

    private static async Task PublishParentAsync(string path, TimeProvider time)
    {
        var store = new SqliteAuthorityStore(path, new AllowAllPolicy(), time);
        var snapshot = new AuthoritySnapshot(
            ParentContext,
            "v1",
            [new AuthorityLayer("run", [
                new AuthorityGrant(
                    ParentGrantId,
                    [AuthorityAction.ExecuteProcess],
                    new AuthorityScope(Workspace, "src", AuthorityScopeKind.Subtree),
                    [],
                    NotBefore,
                    ExpiresAt)])],
            [],
            ExpiresAt);
        var published = await store.PublishAsync(
            new AuthorityPublishCommand("publish-parent", Actor, snapshot, 0),
            TestContext.Current.CancellationToken);
        published.Status.Should().Be(AuthorityMutationStatus.Applied);
    }

    private static WorkflowPlan CreatePlan()
    {
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        var activity = new DescriptorReference(
            DescriptorKind.Activity, "sample.echo", "1",
            new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var activityPath = StructuralNodeIdentity.Create("task", "execute");
        var returnPath = StructuralNodeIdentity.Create("task", "return_result");
        return new WorkflowPlan(
            FuwenContracts.IrVersion,
            "fuwen-language/v1",
            FuwenContracts.CompilerSemanticVersion,
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersion,
            "task", "1", text, text, "routing/1", [], [activity],
            new CapabilityManifest([]),
            [
                new ActivityNode("execute", activityPath, activity, [new ArgumentBinding("value", new InputBinding([]))], text),
                new ReturnNode("return_result", returnPath, new NodeOutputBinding(activityPath, [])),
            ],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("task", [
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

    private sealed class TemporaryWorkspace : IDisposable
    {
        public string Root { get; }

        public string Workspace => Path.Combine(Root, "work");

        public TemporaryWorkspace(string? root = null)
        {
            Root = root ?? Path.Combine(Path.GetTempPath(), "marang-codex-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Workspace);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public string DirectoryPath { get; }

        public string DatabasePath => Path.Combine(DirectoryPath, "authority.db");

        public TemporaryDatabase(string? directoryPath = null)
        {
            DirectoryPath = directoryPath ?? Path.Combine(Path.GetTempPath(), "marang-codex-hufu-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ScriptedProcessFactory(
        Func<CodexProcessInvocation, ScriptedProcessFactory, ScriptedProcess> script) : ICodexProcessFactory
    {
        public int Spawns { get; private set; }

        public ICodexProcess Start(CodexProcessInvocation invocation)
        {
            Spawns++;
            return script(invocation, this);
        }
    }

    private sealed class ScriptedProcess(
        IReadOnlyList<string> lines,
        int exitCode) : ICodexProcess
    {
        public async IAsyncEnumerable<string> ReadLinesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var line in lines)
            {
                yield return line;
            }

            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ReadErrorLinesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(exitCode);

        public void Kill()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Test-host derivation approver: approves exactly the derivation the
    /// adapter presents (a real host would apply its own approval policy and
    /// lineage). Reads the tuple from the command so the approval can never
    /// drift from the requested delegation/generation.
    /// </summary>
    private sealed class DeriveApprovalTrustSource(AuthorityStoreActor actor) : IAuthorityIssuanceTrustSource
    {
        public ValueTask<AuthorityIssuancePrincipal?> AuthenticateAsync(
            AuthorityStoreActor presentedActor,
            AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default) =>
            request.DerivationCommand is null
                ? throw new InvalidOperationException("The access request carries no derivation command.")
                : new(new AuthorityIssuancePrincipal(actor, ApprovalValidUntil: ExpiresAt)
                {
                    ApprovedDerivation = new DerivedAuthorityApproval(
                        request.DerivationCommand.ParentGrantId,
                        request.DerivationCommand.DelegationId,
                        request.DerivationCommand.Generation,
                        AuthorityDerivation.RequestedAuthorityHash(request.DerivationCommand.Requested))
                });
    }

    private sealed class AllowAllPolicy : IAuthorityStoreAuthorizer
    {
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(
            AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default) =>
            new(new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
    }
}
#endif


