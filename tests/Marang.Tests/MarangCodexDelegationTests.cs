using System.Text.Json;
using FluentAssertions;
using Marang.Codex;
using Marang.Http;
using Penghou.Hufu;
using Penghou.Hufu.Sqlite;
using Penghou.Qingniao;
using Penghou.Qingniao.Codex;
using Penghou.Qingniao.Hufu;

namespace Marang.Tests;

/// <summary>
/// M5.2 bounded Codex proof: the real published <see cref="CodexExecAdapter"/>
/// is registered through the normal provider-selection mechanism, authorized
/// through the normal Hufu authority preflight, runs exactly one bounded
/// process to a durable terminal state, and exposes its result/evidence
/// through the existing HTTP operator surface — with no special-case
/// execution path in Marang. The process is a deterministic scripted
/// transcript (no live CLI, no network), bounded to a single spawn.
/// </summary>
public sealed class MarangCodexDelegationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NotBefore = Now.AddMinutes(-1);
    private static readonly DateTimeOffset ExpiresAt = Now.AddDays(1);
    private static readonly AuthorityStoreActor Actor = new("tenant", "host", "session-1");
    private static readonly AuthenticatedAuthorityContext ParentContext =
        new("tenant", "supervisor", "run-parent", "rev", "fence");
    private const string Workspace = "workspace";
    private const string ParentGrantId = "grant-codex-parent";
    private const string Caller = "local-operator";

    [Fact]
    public async Task Codex_delegation_runs_bounded_and_reports_terminal_evidence()
    {
        using var files = new TemporaryWorkspace();
        using var db = new TemporaryDatabase();
        var time = new FixedTimeProvider(Now);
        var ct = TestContext.Current.CancellationToken;
        await PublishParentAsync(db.DatabasePath, time);

        var factory = new ScriptedProcessFactory((_, _) => new ScriptedProcess(
            SuccessTranscript("codex-thread-1"), exitCode: 0));
        var preflight = new HufuDelegationAuthorityPreflight(
            DerivationStore(db.DatabasePath, time),
            Actor,
            ParentContext,
            (delegationId, generation) => new AuthenticatedAuthorityContext(
                "tenant", "delegation", $"run-{delegationId}", "rev", generation));

        var (runtime, catalog) = CreateRuntime(files, factory, preflight);

        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope(Caller), CreateRequest("codex-bounded"), ct);

        // The delegation was authorized through the normal Hufu preflight: the
        // permitted child authority context is recorded with the generation.
        var queued = await runtime.GetAsync(handle.DelegationId, ct);
        queued.ExecutionAttachment.Should().NotBeNull();
        queued.ExecutionAttachment!.Kind.Should().Be(HufuExecutionAttachment.Kind);

        var terminal = await PumpToTerminalAsync(runtime, handle.DelegationId, ct);
        terminal.Progress.State.Should().Be(DelegationState.Completed);

        // Exactly one bounded process: the acceptance proof cannot become an
        // open-ended coding-agent session.
        factory.Spawns.Should().Be(1);

        var (detailStatus, detailBody) = await SupervisionHttpEndpoints.GetDetailAsync(
            runtime, catalog, Caller, handle.DelegationId.Value, ct);
        detailStatus.Should().Be(200);
        Str(Json(detailBody), "state").Should().Be("Completed");
        Str(Json(detailBody), "resultSummary").Should().NotBeNullOrEmpty();

        var (evidenceStatus, evidenceBody) = await SupervisionHttpEndpoints.GetEvidenceAsync(
            runtime, catalog, Caller, handle.DelegationId.Value, ct);
        evidenceStatus.Should().Be(200);
        var evidence = Json(evidenceBody);
        evidence.GetProperty("hasResult").GetBoolean().Should().BeTrue();
        Str(evidence, "state").Should().Be("Completed");
        // The Codex adapter reports no artifact references for a successful turn.
        evidence.GetProperty("artifacts").GetArrayLength().Should().Be(0);

        // A foreign caller cannot read the Codex delegation or its evidence.
        (await SupervisionHttpEndpoints.GetDetailAsync(
            runtime, catalog, "someone-else", handle.DelegationId.Value, ct)).StatusCode.Should().Be(404);
    }

    private static string[] SuccessTranscript(string threadId) =>
    [
        """{"type":"thread.started","thread_id":"THREAD"}""".Replace("THREAD", threadId, StringComparison.Ordinal),
        """{"type":"turn.started"}""",
        """{"type":"turn.completed","usage":{"input_tokens":120,"cached_input_tokens":30,"output_tokens":45}}""",
    ];

    private static (DelegationRuntime Runtime, MarangDelegationCatalog Catalog) CreateRuntime(
        TemporaryWorkspace files,
        ICodexProcessFactory factory,
        IDelegationAuthorityPreflight preflight)
    {
        var catalog = new MarangDelegationCatalog();
        var providers = new InMemoryProviderRegistry();
        var adapters = new InMemoryExternalOperationProviderCatalog();
        CodexProviderRegistration.Register(
            providers,
            adapters,
            new CodexExecOptions("Do the bounded work", files.Workspace, files.Root),
            factory,
            clock: () => Now);

        var runtime = new DelegationRuntime(
            catalog,
            new MarangAdmissionVerifier(),
            providers,
            adapters,
            now: () => Now,
            authorityPreflight: preflight);
        return (runtime, catalog);
    }

    private static DelegationRequest CreateRequest(string requestKey) => new(
        requestKey,
        "Run one bounded Codex task",
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
            ExpiresAt));

    private static async Task<DelegationExecutionSnapshot> PumpToTerminalAsync(
        DelegationRuntime runtime, DelegationId id, CancellationToken ct)
    {
        var snapshot = await runtime.GetAsync(id, ct);
        for (var index = 0; index < 10 && !DelegationLifecycle.IsTerminal(snapshot.Progress.State); index++)
        {
            snapshot = await runtime.PumpAsync(id, snapshot.Progress.Revision, ct);
        }

        DelegationLifecycle.IsTerminal(snapshot.Progress.State).Should().BeTrue();
        return snapshot;
    }

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

    private static JsonElement Json(object? body) =>
        JsonDocument.Parse(JsonSerializer.Serialize(body)).RootElement;

    private static string Str(JsonElement element, string name) =>
        element.GetProperty(name).GetString()!;

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
            CancellationToken cancellationToken = default)
        {
            var command = request.DerivationCommand
                ?? throw new InvalidOperationException("The access request carries no derivation command.");
            var approval = new DerivedAuthorityApproval(
                command.ParentGrantId,
                command.DelegationId,
                command.Generation,
                AuthorityDerivation.RequestedAuthorityHash(command.Requested));
            return new(new AuthorityIssuancePrincipal(actor, ApprovalValidUntil: ExpiresAt)
            {
                ApprovedDerivation = approval
            });
        }
    }

    private sealed class AllowAllPolicy : IAuthorityStoreAuthorizer
    {
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(
            AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default) =>
            new(new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
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

    private sealed class TemporaryWorkspace : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "marang-codex-" + Guid.NewGuid().ToString("N"));

        public string Workspace => Path.Combine(Root, "work");

        public TemporaryWorkspace()
        {
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
        public string DirectoryPath { get; } =
            Path.Combine(Path.GetTempPath(), "marang-codex-hufu-" + Guid.NewGuid().ToString("N"));

        public string DatabasePath => Path.Combine(DirectoryPath, "authority.db");

        public TemporaryDatabase() => Directory.CreateDirectory(DirectoryPath);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
