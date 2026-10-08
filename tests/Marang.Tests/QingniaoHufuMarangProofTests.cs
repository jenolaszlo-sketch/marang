using FluentAssertions;
using Marang.Hufu;
using Penghou.Hufu;
using Penghou.Hufu.Sqlite;
using Penghou.Qingniao;
using Penghou.Qingniao.Hufu;

namespace Marang.Tests;

/// <summary>
/// Full Qingniao → Hufu → Marang proof for the host-owned authority preflight.
/// A parent grant P covers <c>src/**</c>; delegation D requests child C for
/// <c>src/service/**</c>. The Hufu adapter derives C, Qingniao records the
/// opaque attachment with the generation and delivers it on the start request,
/// and the Marang file reader enforces it: inside-C reads succeed once,
/// outside-C reads are denied without touching the filesystem, and revoking P
/// fails the next use closed as ancestor-revoked. Same-generation retries
/// converge on the same child; a new generation issues a distinct one. A
/// foreign kind or malformed value never resolves and never runs.
/// </summary>
public sealed class QingniaoHufuMarangProofTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NotBefore = Now.AddMinutes(-1);
    private static readonly DateTimeOffset ExpiresAt = Now.AddDays(1);
    private static readonly AuthorityStoreActor Actor = new("tenant", "host", "session-1");
    private static readonly AuthenticatedAuthorityContext Supervisor =
        new("tenant", "supervisor", "run-parent", "rev", "fence");
    private const string Workspace = "workspace";
    private const string ParentGrantId = "grant-parent";

    [Fact]
    public async Task Full_lifecycle_permit_reads_deny_and_ancestor_revoked()
    {
        using var db = new TemporaryDatabase();
        var time = new FixedTimeProvider(Now);
        await PublishParentAsync(db.DatabasePath, time);
        var ct = TestContext.Current.CancellationToken;

        var delegationGuid = Guid.NewGuid();
        var delegationD = delegationGuid.ToString("D");
        var hufuRequested = ChildRequested();
        var approval = new DerivedAuthorityApproval(
            ParentGrantId, delegationD, delegationD,
            AuthorityDerivation.RequestedAuthorityHash(hufuRequested));
        var store = Store(db.DatabasePath, approval, time);
        var preflight = Preflight(store);

        var provider = new CapturingProvider();
        var runtime = CreateRuntime(
            provider,
            preflight,
            new FixedAcceptanceRegistry(new DelegationId(delegationGuid)));

        var handle = await runtime.DelegateAsync(
            new DelegationCallerScope("caller"), CreateRequest("proof-lifecycle"), ct);

        var queued = await runtime.GetAsync(handle.DelegationId, ct);
        queued.ExecutionAttachment.Should().NotBeNull();
        queued.ExecutionAttachment!.Kind.Should().Be(HufuExecutionAttachment.Kind);
        HufuExecutionAttachment.TryResolve(queued.ExecutionAttachment, out var childContext).Should().BeTrue();

        var terminal = await PumpToTerminalAsync(runtime, handle.DelegationId, ct);
        terminal.Progress.State.Should().Be(DelegationState.Completed);
        terminal.ExecutionAttachment.Should().Be(queued.ExecutionAttachment);
        provider.StartRequest.Should().NotBeNull();
        provider.StartRequest!.ExecutionAttachment.Should().Be(queued.ExecutionAttachment);

        var reads = 0;
        var reader = new HufuFileReader(
            Admission(store, time),
            childContext!,
            Workspace,
            (_, _) => { reads++; return new ValueTask<string>("content"); });

        (await reader.ReadFileAsync("src/service/a.cs", ct)).Should().BeOfType<FileReadAllowed>();
        reads.Should().Be(1);

        (await reader.ReadFileAsync("src/other/b.cs", ct)).Should().BeOfType<FileReadDenied>();
        reads.Should().Be(1);

        var revoked = await store.RevokeAsync(
            new AuthorityRevokeCommand("revoke-1", Actor, Supervisor, 1, "test.revoked"), ct);
        revoked.Status.Should().Be(AuthorityMutationStatus.Applied);

        var after = await reader.ReadFileAsync("src/service/a.cs", ct);
        after.Should().BeOfType<FileReadDenied>()
            .Which.ReasonCode.Should().Be("authority.ancestor-revoked");
        reads.Should().Be(1);
    }

    [Fact]
    public async Task Same_generation_retry_converges_on_the_same_child()
    {
        using var db = new TemporaryDatabase();
        var time = new FixedTimeProvider(Now);
        await PublishParentAsync(db.DatabasePath, time);
        var ct = TestContext.Current.CancellationToken;

        var delegationGuid = Guid.NewGuid();
        var delegationD = delegationGuid.ToString("D");
        var hufuRequested = ChildRequested();
        var approval = new DerivedAuthorityApproval(
            ParentGrantId, delegationD, delegationD,
            AuthorityDerivation.RequestedAuthorityHash(hufuRequested));
        var store = Store(db.DatabasePath, approval, time);
        var preflight = Preflight(store);
        var context = PreflightContext(delegationGuid);

        var first = await preflight.PreflightAsync(context, ct);
        var second = await preflight.PreflightAsync(context, ct);

        first.Status.Should().Be(DelegationAuthorityPreflightStatus.Permit);
        second.Status.Should().Be(DelegationAuthorityPreflightStatus.Permit);
        second.ExecutionAttachment.Should().Be(first.ExecutionAttachment);

        // Hufu-level: the same derivation tuple reuses the issued grant even
        // with a fresh transport nonce.
        var command = (string nonce) => new AuthorityDerivationCommand(
            Actor, Supervisor, ParentGrantId, ChildFor(delegationD, delegationD),
            delegationD, delegationD, hufuRequested, nonce);
        var one = await store.DeriveAsync(command("nonce-1"), ct);
        var two = await store.DeriveAsync(command("nonce-2"), ct);

        one.Status.Should().Be(AuthorityStatus.Permit);
        two.Status.Should().Be(AuthorityStatus.Permit);
        two.Grant!.Id.Should().Be(one.Grant!.Id);
    }

    [Fact]
    public async Task New_generation_issues_a_distinct_child()
    {
        using var db = new TemporaryDatabase();
        var time = new FixedTimeProvider(Now);
        await PublishParentAsync(db.DatabasePath, time);
        var ct = TestContext.Current.CancellationToken;

        var delegationGuid = Guid.NewGuid();
        var delegationD = delegationGuid.ToString("D");
        var generation2 = Guid.NewGuid().ToString("D");
        var hufuRequested = ChildRequested();
        var approval1 = new DerivedAuthorityApproval(
            ParentGrantId, delegationD, delegationD,
            AuthorityDerivation.RequestedAuthorityHash(hufuRequested));
        var approval2 = new DerivedAuthorityApproval(
            ParentGrantId, delegationD, generation2,
            AuthorityDerivation.RequestedAuthorityHash(hufuRequested));
        var store1 = Store(db.DatabasePath, approval1, time);
        var store2 = Store(db.DatabasePath, approval2, time);

        var first = await store1.DeriveAsync(
            new AuthorityDerivationCommand(
                Actor, Supervisor, ParentGrantId, ChildFor(delegationD, delegationD),
                delegationD, delegationD, hufuRequested, "nonce-1"), ct);
        var second = await store2.DeriveAsync(
            new AuthorityDerivationCommand(
                Actor, Supervisor, ParentGrantId, ChildFor(delegationD, generation2),
                delegationD, generation2, hufuRequested, "nonce-2"), ct);

        first.Status.Should().Be(AuthorityStatus.Permit);
        second.Status.Should().Be(AuthorityStatus.Permit);
        second.Grant!.Id.Should().NotBe(first.Grant!.Id);

        // Adapter-level: the same delegation at a new generation re-runs
        // preflight and attaches a distinct child execution context.
        var preflight1 = Preflight(store1);
        var preflight2 = Preflight(store2);
        var attach1 = (await preflight1.PreflightAsync(PreflightContext(delegationGuid), ct))
            .ExecutionAttachment!;
        var context2 = new DelegationAuthorityPreflightContext(
            new DelegationId(delegationGuid),
            new NodeGenerationId(Guid.Parse(generation2)),
            ParentGrantId,
            new Penghou.Qingniao.RequestedAuthority(
                ["ReadFile"],
                new RequestedAuthorityScope(Workspace, "src/service", RequestedAuthorityScopeKind.Subtree),
                [],
                NotBefore,
                ExpiresAt));
        var attach2 = (await preflight2.PreflightAsync(context2, ct)).ExecutionAttachment!;
        attach2.Value.Should().NotBe(attach1.Value);
        HufuExecutionAttachment.TryResolve(attach2, out var child2).Should().BeTrue();
        child2.Should().NotBe(childContextOf(attach1));
    }

    [Fact]
    public void Foreign_kind_or_malformed_value_never_resolves_and_never_runs()
    {
        var foreign = new DelegationExecutionAttachment(
            "other.kind", HufuExecutionAttachment.Encode(ChildFor("d", "g")));
        HufuExecutionAttachment.TryResolve(foreign, out _).Should().BeFalse();

        var malformed = new DelegationExecutionAttachment(HufuExecutionAttachment.Kind, "not-a-context");
        HufuExecutionAttachment.TryResolve(malformed, out _).Should().BeFalse();
        HufuExecutionAttachment.TryResolve(null, out _).Should().BeFalse();
    }

    private static Penghou.Hufu.RequestedAuthority ChildRequested() => new(
        [AuthorityAction.ReadFile],
        new AuthorityScope(Workspace, "src/service", AuthorityScopeKind.Subtree),
        [],
        NotBefore,
        ExpiresAt);

    private static AuthenticatedAuthorityContext ChildFor(string delegationD, string generationD) =>
        new("tenant", "delegation", $"run-{delegationD}-{generationD}", "rev", "fence");

    private static HufuDelegationAuthorityPreflight Preflight(SqliteAuthorityStore store) =>
        new(store, Actor, Supervisor, (delegationD, generationD) => ChildFor(delegationD, generationD));

    private static DelegationAuthorityPreflightContext PreflightContext(Guid delegationGuid) =>
        new(
            new DelegationId(delegationGuid),
            new NodeGenerationId(delegationGuid),
            ParentGrantId,
            new Penghou.Qingniao.RequestedAuthority(
                ["ReadFile"],
                new RequestedAuthorityScope(Workspace, "src/service", RequestedAuthorityScopeKind.Subtree),
                [],
                NotBefore,
                ExpiresAt));

    private static DelegationRequest CreateRequest(string requestKey) => new(
        requestKey,
        "Do the delegated work",
        "proof-provider",
        new WorkspaceReference("local", "workspace", "revision"),
        ["Done"],
        [],
        new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2),
        parentGrantId: ParentGrantId,
        requestedAuthority: new Penghou.Qingniao.RequestedAuthority(
            ["ReadFile"],
            new RequestedAuthorityScope(Workspace, "src/service", RequestedAuthorityScopeKind.Subtree),
            [],
            NotBefore,
            ExpiresAt));

    private static DelegationRuntime CreateRuntime(
        IExternalOperationProvider provider,
        IDelegationAuthorityPreflight preflight,
        IDelegationAcceptanceRegistry acceptanceRegistry)
    {
        var descriptor = new ProviderDescriptor("proof-provider", [new CapabilityDescriptor("agent.execute", 1)]);
        var providers = new InMemoryProviderRegistry();
        providers.Register(descriptor);
        var adapters = new InMemoryExternalOperationProviderCatalog();
        adapters.Register(descriptor, provider);

        return new DelegationRuntime(
            acceptanceRegistry,
            null,
            providers,
            adapters,
            now: () => Now,
            authorityPreflight: preflight);
    }

    private static async Task<DelegationExecutionSnapshot> PumpToTerminalAsync(
        DelegationRuntime runtime,
        DelegationId id,
        CancellationToken ct)
    {
        var snapshot = await runtime.GetAsync(id, ct);
        for (var index = 0; index < 10 && !DelegationLifecycle.IsTerminal(snapshot.Progress.State); index++)
        {
            snapshot = await runtime.PumpAsync(id, snapshot.Progress.Revision, ct);
        }

        DelegationLifecycle.IsTerminal(snapshot.Progress.State).Should().BeTrue();
        return snapshot;
    }

    private static SqliteAuthorityStore Store(
        string path,
        DerivedAuthorityApproval approval,
        TimeProvider time) =>
        new(
            path,
            new BoundedAuthorityIssuanceAuthorizer(new TrustSource(approval), new AllowAllPolicy(), time),
            time);

    private static CurrentAuthorityRequestAuthorizer Admission(SqliteAuthorityStore store, TimeProvider time)
    {
        var source = new AuthorityStoreSnapshotSource(store, Actor);
        return new CurrentAuthorityRequestAuthorizer(
            source,
            new CoverEvaluator(),
            new AllowRecorder(),
            time,
            new AuthorityDerivation.LineageAncestorLiveness(source, store));
    }

    private static async Task PublishParentAsync(string path, TimeProvider time)
    {
        var store = new SqliteAuthorityStore(path, new AllowAllPolicy(), time);
        var snapshot = new AuthoritySnapshot(
            Supervisor,
            "v1",
            [new AuthorityLayer("run", [
                new AuthorityGrant(
                    ParentGrantId,
                    [AuthorityAction.ReadFile],
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

    private sealed class FixedAcceptanceRegistry(DelegationId id) : IDelegationAcceptanceRegistry
    {
        public ValueTask<DelegationAcceptance> AcceptAsync(
            DelegationCallerScope caller,
            DelegationRequest request,
            CancellationToken cancellationToken = default) =>
            new(new DelegationAcceptance(id, DelegationRequestIdentity.Compute(request), true));
    }

    private sealed class CapturingProvider : IExternalOperationProvider
    {
        public ExternalOperationStartRequest? StartRequest { get; private set; }

        public ValueTask<ExternalOperationStartReceipt> StartAsync(
            ExternalOperationStartRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default)
        {
            StartRequest = request;
            var handle = new ExternalOperationHandle(
                request.Correlation.Agent.Provider, "proof-handle", request.Correlation.Agent.ProtocolVersion,
                new ExternalOperationCorrelation(request.Correlation.DelegationId, request.Correlation.WorkflowRun,
                    request.Correlation.StructuralNode, request.Correlation.NodeGeneration,
                    request.Correlation.ExecutionAttemptId, request.Correlation.Agent,
                    new ExternalTaskReference(request.Correlation.Agent.Provider, "proof-handle")));
            return ValueTask.FromResult(new ExternalOperationStartReceipt(
                request.Identity, handle, ExternalOperationStartDisposition.Created, ExternalOperationState.Running, Now.AddMinutes(1)));
        }

        public ValueTask<ExternalOperationObservation> ObserveAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationObservation(
                operationHandle, 1, ExternalOperationState.Succeeded, Now.AddMinutes(2), resultAvailable: true));

        public ValueTask<ExternalOperationResult> GetResultAsync(
            ExternalOperationHandle operationHandle,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExternalOperationResult(
                operationHandle, ExternalOperationState.Succeeded, Now.AddMinutes(3), "done", []));

        public ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
            ExternalOperationCancelRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ExternalOperationResumeReceipt> ResumeAsync(
            ExternalOperationResumeRequest request,
            IExternalOperationHandleCaptureSink handleSink,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TrustSource(DerivedAuthorityApproval approval) : IAuthorityIssuanceTrustSource
    {
        public ValueTask<AuthorityIssuancePrincipal?> AuthenticateAsync(
            AuthorityStoreActor presentedActor,
            AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default) =>
            new(new AuthorityIssuancePrincipal(presentedActor, ApprovalValidUntil: Now.AddHours(1))
            {
                ApprovedDerivation = approval
            });
    }

    private sealed class AllowAllPolicy : IAuthorityStoreAuthorizer
    {
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(
            AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default) =>
            new(new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
    }

    private sealed class AllowRecorder : IAuthorityDecisionRecorder
    {
        public ValueTask<bool> RecordAsync(
            AuthorityRequest request,
            AuthorityDecision decision,
            CancellationToken cancellationToken = default) => new(true);
    }

    private sealed class CoverEvaluator : IAuthorityEvaluator
    {
        public AuthorityDecision Evaluate(
            AuthoritySnapshot snapshot,
            AuthorityRequest request,
            DateTimeOffset now)
        {
            var exact = new AuthorityScope(
                request.WorkspaceId,
                AuthorityValidation.NormalizePath(request.RelativePath),
                AuthorityScopeKind.Exact);
            var covered = snapshot.Layers
                .SelectMany(layer => layer.Grants)
                .Any(grant => grant.Actions.Contains(request.Action) &&
                    grant.NotBefore <= now && now < grant.ExpiresAt &&
                    AuthorityValidation.Contains(grant.Scope, exact));
            return new AuthorityDecision(
                covered ? AuthorityStatus.Permit : AuthorityStatus.Deny,
                covered ? "test.permitted" : "test.layer-denied",
                snapshot.Version,
                "test-evaluator-v1",
                snapshot.Identity);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        public string DirectoryPath { get; } =
            Path.Combine(Path.GetTempPath(), "marang-qingniao-proof-" + Guid.NewGuid().ToString("N"));

        public string DatabasePath => Path.Combine(DirectoryPath, "authority.db");

        public TemporaryDatabase() => Directory.CreateDirectory(DirectoryPath);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
    }
    private static AuthenticatedAuthorityContext childContextOf(DelegationExecutionAttachment attachment)
    {
        HufuExecutionAttachment.TryResolve(attachment, out var context).Should().BeTrue();
        return context!;
    }
}
