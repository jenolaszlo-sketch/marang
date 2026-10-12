using FluentAssertions;
using Marang.DelegationAuthority;
using Marang.Http;
using Marang.Runs;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Hufu;
using Penghou.Hufu.Sqlite;
using Penghou.Qingniao;
using Penghou.Zhinu;

namespace Marang.Tests;

/// <summary>
/// Restart-safe delegation acceptance authority: stable acceptance identity
/// (principal + request key, never content), a Marang-owned durable metadata
/// descriptor, a Hufu-owned durable visibility binding with terminal
/// revocation, and an ordered idempotent protocol whose partial crash state
/// is completed by exact retry — never repaired by reads. Reads only verify
/// and fail closed. Store-level visibility semantics are qualified in the
/// Hufu suite; these tests prove the Marang protocol behavior above them.
/// </summary>
public sealed class DelegationAuthorityTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private const string Caller = "operator-1";

    // Identity.

    [Fact]
    public void Same_principal_and_key_yield_stable_identity()
    {
        var first = AcceptanceIdentity.AcceptanceKey("marang", Caller, "key-1");
        var second = AcceptanceIdentity.AcceptanceKey("marang", Caller, "key-1");
        first.Should().Be(second);
        AcceptanceIdentity.DelegationIdFor(first).Should().Be(AcceptanceIdentity.DelegationIdFor(second));
    }

    [Fact]
    public void Different_keys_and_subjects_yield_independent_identities()
    {
        var baseKey = AcceptanceIdentity.AcceptanceKey("marang", Caller, "key-1");
        AcceptanceIdentity.AcceptanceKey("marang", Caller, "key-2").Should().NotBe(baseKey);
        AcceptanceIdentity.AcceptanceKey("marang", "someone-else", "key-1").Should().NotBe(baseKey);
        AcceptanceIdentity.DelegationIdFor(AcceptanceIdentity.AcceptanceKey("marang", Caller, "key-2"))
            .Should().NotBe(AcceptanceIdentity.DelegationIdFor(baseKey));
    }

    [Fact]
    public void Principal_mapping_is_stable_and_never_embeds_key_material()
    {
        // Key rotation changes the secret, never the subject: the mapper only
        // ever sees the authenticated caller label.
        MarangPrincipalMapper.MapCaller("alice").Should().Be(MarangPrincipalMapper.MapCaller("alice"));
        MarangPrincipalMapper.MapCaller("alice").Should().Be(new MarangPrincipal("marang", "alice"));
        MarangPrincipalMapper.MapCaller("local-operator").SubjectId.Should().Be("local-operator");
    }

    // Descriptor store.

    [Fact]
    public async Task Descriptor_ensure_replays_and_conflicts()
    {
        var directory = NewDirectory();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var store = new SqliteAcceptedDelegationStore(Path.Combine(directory, "authority.db"));
            var candidate = Descriptor("key-1", "v3", new string('a', 64));

            var (first, firstStatus) = await store.EnsureAsync(candidate, ct);
            firstStatus.Should().Be(AcceptedDelegationEnsureStatus.Created);

            var (replay, replayStatus) = await store.EnsureAsync(candidate with { }, ct);
            replayStatus.Should().Be(AcceptedDelegationEnsureStatus.Replayed);
            replay.Should().Be(first);

            var conflict = async () => await store.EnsureAsync(candidate with { FingerprintHash = new string('b', 64) }, ct);
            (await conflict.Should().ThrowAsync<AcceptedDelegationConflictException>()).Which.RequestKey.Should().Be("key-1");

            (await store.GetByAcceptanceKeyAsync(candidate.AcceptanceKey, ct)).Should().Be(first);
            (await store.GetByDelegationIdAsync(first.DelegationId, ct)).Should().Be(first);
            (await store.GetByDelegationIdAsync(Guid.NewGuid(), ct)).Should().BeNull();
        }
        finally
        {
            Delete(directory);
        }
    }

    // Visibility store semantics (publish/replay/conflict, terminal
    // revocation, check matrix) are qualified in the Hufu suite against the
    // Hufu-owned store; the protocol tests below prove Marang behavior above it.

    // Ordered protocol through the catalog.

    [Fact]
    public async Task Exact_retry_converges_on_one_delegation_across_catalog_instances()
    {
        var directory = NewDirectory();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var first = DurableCatalog(directory);
            var accepted = await first.AcceptAsync(new DelegationCallerScope(Caller), Request("proto-exact"), ct);
            var replay = await first.AcceptAsync(new DelegationCallerScope(Caller), Request("proto-exact"), ct);
            replay.DelegationId.Should().Be(accepted.DelegationId);
            replay.IsNew.Should().BeFalse();

            // A fresh process (empty memory, same files) converges identically.
            var second = DurableCatalog(directory);
            second.Find(accepted.DelegationId, Caller).Should().BeNull();
            var revived = await second.AcceptAsync(new DelegationCallerScope(Caller), Request("proto-exact"), ct);
            revived.DelegationId.Should().Be(accepted.DelegationId);
            second.Find(accepted.DelegationId, Caller).Should().NotBeNull();
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Retry_after_descriptor_only_partial_state_converges()
    {
        var directory = NewDirectory();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var request = Request("proto-partial-descriptor");
            var fingerprint = DelegationRequestIdentity.Compute(request);
            var key = AcceptanceIdentity.AcceptanceKey("marang", Caller, request.RequestKey);
            var id = AcceptanceIdentity.DelegationIdFor(key);

            var descriptors = new SqliteAcceptedDelegationStore(Path.Combine(directory, "authority.db"));
            await descriptors.EnsureAsync(new AcceptedDelegation(
                key, id, "marang", Caller, request.RequestKey,
                fingerprint.Version, fingerprint.Hash,
                request.Objective, request.Provider, request.Workspace.Identifier,
                AcceptanceIdentity.InitialGeneration, Start), ct);

            // No visibility row exists yet: exact retry completes the protocol.
            var catalog = DurableCatalog(directory);
            var accepted = await catalog.AcceptAsync(new DelegationCallerScope(Caller), request, ct);
            accepted.DelegationId.Value.Should().Be(id);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Retry_after_visibility_only_partial_state_converges()
    {
        var directory = NewDirectory();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var request = Request("proto-partial-visibility");
            var fingerprint = DelegationRequestIdentity.Compute(request);
            var key = AcceptanceIdentity.AcceptanceKey("marang", Caller, request.RequestKey);
            var id = AcceptanceIdentity.DelegationIdFor(key);

            var visibility = new SqliteDelegationVisibilityStore(Path.Combine(directory, "authority.db"));
            var (status, _) = await visibility.PublishAsync(new DelegationVisibilityBinding(
                "marang", Caller, id.ToString("D"), AcceptanceIdentity.InitialGeneration, request.RequestKey,
                fingerprint.Version, fingerprint.Hash,
                DelegationVisibilityOperation.Observe | DelegationVisibilityOperation.Cancel | DelegationVisibilityOperation.Intervene,
                Start, Start.AddDays(30),
                AcceptanceIdentity.VisibilityCommandId(key, AcceptanceIdentity.InitialGeneration),
                Revoked: false, RevokeReason: string.Empty, Start), ct);
            status.Should().Be(DelegationVisibilityPublishStatus.Published);

            var catalog = DurableCatalog(directory);
            var accepted = await catalog.AcceptAsync(new DelegationCallerScope(Caller), request, ct);
            accepted.DelegationId.Value.Should().Be(id);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Conflicting_retry_after_restart_targets_the_same_slot_and_fails_closed()
    {
        var directory = NewDirectory();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var first = DurableCatalog(directory);
            var accepted = await first.AcceptAsync(new DelegationCallerScope(Caller), Request("proto-conflict", "Objective A"), ct);

            var second = DurableCatalog(directory);
            var conflict = async () => await second.AcceptAsync(
                new DelegationCallerScope(Caller), Request("proto-conflict", "Objective B"), ct);
            (await conflict.Should().ThrowAsync<DelegationRequestKeyConflictException>()).Which.RequestKey.Should().Be("proto-conflict");

            // No second delegation was minted: the only durable descriptor is the original.
            var descriptors = new SqliteAcceptedDelegationStore(Path.Combine(directory, "authority.db"));
            (await descriptors.GetByDelegationIdAsync(accepted.DelegationId.Value, ct))!.FingerprintHash
                .Should().Be(DelegationRequestIdentity.Compute(Request("proto-conflict", "Objective A")).Hash);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Descriptor_visibility_mismatch_fails_closed_on_both_paths()
    {
        var directory = NewDirectory();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var request = Request("proto-mismatch");
            var fingerprint = DelegationRequestIdentity.Compute(request);
            var key = AcceptanceIdentity.AcceptanceKey("marang", Caller, request.RequestKey);
            var id = AcceptanceIdentity.DelegationIdFor(key);

            var descriptors = new SqliteAcceptedDelegationStore(Path.Combine(directory, "authority.db"));
            await descriptors.EnsureAsync(new AcceptedDelegation(
                key, id, "marang", Caller, request.RequestKey,
                fingerprint.Version, fingerprint.Hash,
                request.Objective, request.Provider, request.Workspace.Identifier,
                AcceptanceIdentity.InitialGeneration, Start), ct);

            var visibility = new SqliteDelegationVisibilityStore(Path.Combine(directory, "authority.db"));
            var (status, _) = await visibility.PublishAsync(new DelegationVisibilityBinding(
                "marang", Caller, id.ToString("D"), AcceptanceIdentity.InitialGeneration, request.RequestKey,
                fingerprint.Version, new string('c', 64),
                DelegationVisibilityOperation.Observe,
                Start, Start.AddDays(30),
                AcceptanceIdentity.VisibilityCommandId(key, AcceptanceIdentity.InitialGeneration),
                Revoked: false, RevokeReason: string.Empty, Start), ct);
            status.Should().Be(DelegationVisibilityPublishStatus.Published);

            // Acceptance retry carries the true fingerprint: the visibility
            // conflict fails closed without overwriting either record.
            var catalog = DurableCatalog(directory);
            var retry = async () => await catalog.AcceptAsync(new DelegationCallerScope(Caller), request, ct);
            await retry.Should().ThrowAsync<DelegationRequestKeyConflictException>();

            // Reads verify only: disagreement is never repaired into visibility.
            (await catalog.ReattachAsync(Caller, new DelegationId(id), ct)).Should().BeFalse();
            catalog.Find(new DelegationId(id), Caller).Should().BeNull();
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Same_request_key_is_scoped_per_subject()
    {
        var directory = NewDirectory();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var catalog = DurableCatalog(directory);
            var alice = await catalog.AcceptAsync(new DelegationCallerScope("alice"), Request("shared-key"), ct);
            var bob = await catalog.AcceptAsync(new DelegationCallerScope("bob"), Request("shared-key"), ct);
            alice.DelegationId.Should().NotBe(bob.DelegationId);

            catalog.Find(alice.DelegationId, "bob").Should().BeNull();
            catalog.Find(bob.DelegationId, "alice").Should().BeNull();

            var fresh = DurableCatalog(directory);
            (await fresh.ReattachAsync("bob", alice.DelegationId, ct)).Should().BeFalse();
            (await fresh.ReattachAsync("alice", alice.DelegationId, ct)).Should().BeTrue();
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Revoked_visibility_blocks_reattachment_and_run_reads()
    {
        var directory = NewDirectory();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var catalog = DurableCatalog(directory);
            var accepted = await catalog.AcceptAsync(new DelegationCallerScope(Caller), Request("proto-revoke"), ct);

            var plan = AuthorityPlan();
            var map = FuwenZhinuStepMapper.Map(plan);
            var runId = Guid.NewGuid();
            var correlation = new Marang.Runs.RunCorrelation(
                accepted.DelegationId, accepted.DelegationId.Value.ToString("D"), runId,
                map.PlanRevision, map.ExecutionFingerprint);
            var run = new WorkflowRun
            {
                Id = runId,
                WorkflowName = "demo",
                WorkflowVersion = "1",
                Status = WorkflowStatus.Running,
                CreatedAt = Start,
                UpdatedAt = Start,
                DefinitionFingerprint = map.ExecutionFingerprint,
                MetadataJson = correlation.ToMetadataJson(),
            };
            var steps = new[] { AuthorityStep(runId, "demo/fetch", StepStatus.Completed) };
            var source = new FakeAuthoritySource(plan, run, steps);

            // Sanity: the durable path reads before revocation.
            var reread = DurableCatalog(directory);
            (await RunProjectionEndpoints.GetRunAsync(
                reread, Caller, runId, source, () => Start, ct)).StatusCode.Should().Be(200);

            var visibility = new SqliteDelegationVisibilityStore(Path.Combine(directory, "authority.db"));
            (await visibility.RevokeAsync("marang", Caller, accepted.DelegationId.Value.ToString("D"),
                AcceptanceIdentity.InitialGeneration, "revoke-reattach", "operator.revoked", ct))
                .Should().Be(DelegationVisibilityRevokeStatus.Revoked);

            var after = DurableCatalog(directory);
            (await after.ReattachAsync(Caller, accepted.DelegationId, ct)).Should().BeFalse();
            (await RunProjectionEndpoints.GetRunAsync(
                after, Caller, runId, source, () => Start, ct)).StatusCode.Should().Be(404);
            (await RunProjectionEndpoints.GetDelegationRunAsync(
                after, Caller, accepted.DelegationId.Value, source, ct)).StatusCode.Should().Be(404);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Expired_visibility_blocks_reattachment_and_run_reads()
    {
        var directory = NewDirectory();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var catalog = new MarangDelegationCatalog(
                new SqliteAcceptedDelegationStore(Path.Combine(directory, "authority.db")),
                new SqliteDelegationVisibilityStore(Path.Combine(directory, "authority.db")),
                () => Start, TimeSpan.FromMinutes(1));
            var accepted = await catalog.AcceptAsync(new DelegationCallerScope(Caller), Request("proto-expire"), ct);

            var plan = AuthorityPlan();
            var map = FuwenZhinuStepMapper.Map(plan);
            var runId = Guid.NewGuid();
            var run = new WorkflowRun
            {
                Id = runId,
                WorkflowName = "demo",
                WorkflowVersion = "1",
                Status = WorkflowStatus.Running,
                CreatedAt = Start,
                UpdatedAt = Start,
                DefinitionFingerprint = map.ExecutionFingerprint,
                MetadataJson = new Marang.Runs.RunCorrelation(
                    accepted.DelegationId, accepted.DelegationId.Value.ToString("D"), runId,
                    map.PlanRevision, map.ExecutionFingerprint).ToMetadataJson(),
            };
            var source = new FakeAuthoritySource(plan, run, [AuthorityStep(runId, "demo/fetch", StepStatus.Completed)]);

            var later = new MarangDelegationCatalog(
                new SqliteAcceptedDelegationStore(Path.Combine(directory, "authority.db")),
                new SqliteDelegationVisibilityStore(Path.Combine(directory, "authority.db")),
                () => Start.AddMinutes(2));
            (await later.ReattachAsync(Caller, accepted.DelegationId, ct)).Should().BeFalse();
            (await RunProjectionEndpoints.GetRunAsync(
                later, Caller, runId, source, () => Start, ct)).StatusCode.Should().Be(404);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Control_without_a_live_handle_is_unavailable_not_unknown()
    {
        var directory = NewDirectory();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var catalog = DurableCatalog(directory);
            var accepted = await catalog.AcceptAsync(new DelegationCallerScope(Caller), Request("proto-control"), ct);

            // A fresh runtime has no execution handle for the delegation, but
            // the durable catalog knows it: cancel must report unavailable
            // (409), never success and never silent work.
            var runtime = new DelegationRuntime(
                new MarangDelegationCatalog(), null,
                new InMemoryProviderRegistry(), new InMemoryExternalOperationProviderCatalog());
            var (status, body) = await SupervisionHttpEndpoints.PostCancelAsync(
                runtime, catalog, Caller, accepted.DelegationId.Value, ct);
            status.Should().Be(409);
            body.Should().NotBeNull();

            (await SupervisionHttpEndpoints.PostCancelAsync(
                runtime, catalog, "someone-else", accepted.DelegationId.Value, ct)).StatusCode.Should().Be(404);
        }
        finally
        {
            Delete(directory);
        }
    }

    // Helpers.

    private static MarangDelegationCatalog DurableCatalog(string directory) => new(
        new SqliteAcceptedDelegationStore(Path.Combine(directory, "authority.db")),
        new SqliteDelegationVisibilityStore(Path.Combine(directory, "authority.db")),
        () => Start);

    private static DelegationRequest Request(string requestKey, string objective = "Do the delegated work") => new(
        requestKey, objective, "authority-provider",
        new WorkspaceReference("local", "workspace", "revision"),
        ["Done"], [], new DelegationBudget(MaximumWorkerCalls: 8, MaximumRetries: 2));

    private static AcceptedDelegation Descriptor(string requestKey, string fingerprintVersion, string fingerprintHash)
    {
        var key = AcceptanceIdentity.AcceptanceKey("marang", Caller, requestKey);
        return new AcceptedDelegation(
            key, AcceptanceIdentity.DelegationIdFor(key), "marang", Caller, requestKey,
            fingerprintVersion, fingerprintHash, "Do the work", "authority-provider", "workspace",
            AcceptanceIdentity.InitialGeneration, Start);
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "marang-authority-" + Guid.NewGuid().ToString("N"));
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

    private static WorkflowPlan AuthorityPlan()
    {
        var str = new PrimitiveType(FuwenPrimitiveKind.String);
        var activityDesc = new DescriptorReference(DescriptorKind.Activity, "sample.echo", "1", new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var fetchPath = StructuralNodeIdentity.Create("demo", "fetch");
        var approvalPath = StructuralNodeIdentity.Create("demo", "approval");
        var returnPath = StructuralNodeIdentity.Create("demo", "return_result");
        return new WorkflowPlan(
            FuwenContracts.IrVersion, "fuwen-language/v1", FuwenContracts.CompilerSemanticVersion,
            FuwenContracts.CanonicalJsonVersion, FuwenContracts.ExecutionFingerprintVersion,
            "demo", "1", str, str, "routing/1", [], [activityDesc], new CapabilityManifest([]),
            [
                new ActivityNode("fetch", fetchPath, activityDesc, [new ArgumentBinding("value", new InputBinding([]))], str),
                new WaitNode("approval", approvalPath, "approval_request", str),
                new ReturnNode("return_result", returnPath, new NodeOutputBinding(approvalPath, [])),
            ],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("demo", [
                    new WorkflowExecutionPhase([fetchPath]),
                    new WorkflowExecutionPhase([approvalPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]));
    }

    private static WorkflowStepRun AuthorityStep(Guid runId, string stepKey, StepStatus status) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowRunId = runId,
        StepKey = stepKey,
        Status = status,
        Attempt = 1,
        CreatedAt = Start,
    };

    private sealed class FakeAuthoritySource(WorkflowPlan plan, WorkflowRun run, IReadOnlyList<WorkflowStepRun> steps)
        : IRunProjectionSource
    {
        public ValueTask<WorkflowPlan?> GetPlanAsync(string planRevision, CancellationToken cancellationToken = default) =>
            new(planRevision == plan.Revision ? plan : null);

        public ValueTask<WorkflowRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
            new(runId == run.Id ? run : null);

        public ValueTask<IReadOnlyList<WorkflowStepRun>> GetStepsAsync(Guid runId, CancellationToken cancellationToken = default) =>
            new(runId == run.Id ? steps : (IReadOnlyList<WorkflowStepRun>)[]);

        public ValueTask<long> GetThroughSequenceAsync(Guid runId, CancellationToken cancellationToken = default) =>
            new(runId == run.Id ? 3L : 0L);

        public ValueTask<Guid?> GetRunIdByDelegationAsync(DelegationId delegationId, CancellationToken cancellationToken = default)
        {
            var correlation = Marang.Runs.RunCorrelation.TryParseMetadataJson(run.MetadataJson, run.Id);
            return new(correlation is not null && correlation.DelegationId == delegationId ? run.Id : (Guid?)null);
        }
    }
}
