using System.Text.Json;
using BiscuitSharp;
using FluentAssertions;
using Marang.DelegationAuthority;
using Marang.Hufu;
using Marang.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Penghou.Hufu;
using Penghou.Hufu.Biscuit;
using Penghou.Hufu.Sqlite;
using Penghou.Qingniao;

namespace Marang.Tests;

/// <summary>
/// AE-5B.3 qualification: every protected Marang operation evaluates its own
/// Hufu resource-authority decision against current authority before its first
/// side effect. Acceptance grants no resource rights; actions and resources do
/// not substitute; narrowing/revocation/expiry/unavailability and evidence
/// failure fail closed. Runs end to end through the MCP tools with a real
/// SQLite authority source and real Ed25519 Biscuit trust.
/// </summary>
public sealed class MarangResourceAuthorityTests
{
    private const string Tenant = "marang";
    private const string Caller = "tester";
    private const string Workspace = "workspace";
    private const string Provider = "no-such-provider";
    private const string Realm = "marang-realm";
    private const string KeyId = "key-1";

    // AE5B-Q01
    [Fact]
    public async Task Q01_Accepted_delegation_with_resource_permit_executes()
    {
        await using var f = await ResourceFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var id = await f.AcceptAsync("q01", ct);
        await f.PublishResourceAsync(id, [AuthorityAction.MarangReadDelegation], ct);

        var json = await f.Tools.GetStatusAsync(id.ToString("D"), ct);

        JsonDocument.Parse(json).RootElement.TryGetProperty("state", out _).Should().BeTrue();
        f.Resource.Evidence.Should().Contain(e => e.Decision.Status == AuthorityStatus.Permit);
    }

    // AE5B-Q02 / AE5B-Q03 — the primary AE-5B proof.
    [Fact]
    public async Task Q02_Q03_Acceptance_does_not_grant_a_resource_right()
    {
        await using var f = await ResourceFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        // Acceptance succeeds (its own current authority exists) ...
        var id = await f.AcceptAsync("q02", ct);
        // ... but the resource context has no grant.
        var json = await f.Tools.GetStatusAsync(id.ToString("D"), ct);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().Should().Be("unknown delegation");
        f.Resource.Evidence.Should().NotContain(e => e.Decision.Status == AuthorityStatus.Permit);
    }

    // AE5B-Q04 — resource right cannot substitute for acceptance.
    [Fact]
    public async Task Q04_Resource_permit_does_not_admit_a_delegation()
    {
        await using var f = await ResourceFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var requestKey = "q04";
        var acceptanceKey = AcceptanceIdentity.AcceptanceKey(Tenant, Caller, requestKey);
        var id = AcceptanceIdentity.DelegationIdFor(acceptanceKey);
        // A resource grant exists for the (would-be) delegation id ...
        await f.PublishResourceAsync(id, [AuthorityAction.MarangReadDelegation], ct);

        // ... but acceptance is denied by its own (absent) authority.
        var json = await f.Tools.DelegateAsync(requestKey, "Do the work", Provider, Workspace, 1, ct);
        JsonDocument.Parse(json).RootElement.GetProperty("status").GetString().Should().Be("Unavailable");
        f.Catalog.List(Caller).Should().BeEmpty();
    }

    // AE5B-Q05 — wrong action.
    [Fact]
    public async Task Q05_Wrong_action_denies()
    {
        await using var f = await ResourceFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var id = await f.AcceptAsync("q05", ct);
        // Granted: read delegation. Attempted: cancel.
        await f.PublishResourceAsync(id, [AuthorityAction.MarangReadDelegation], ct);

        var json = await f.Tools.CancelAsync(id.ToString("D"), ct);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().Should().Be("unknown delegation");
        (await f.Runtime.GetStatusAsync(new DelegationId(id), ct))!.State.Should()
            .NotBe(DelegationState.Cancelled);
    }

    // AE5B-Q06 — wrong resource.
    [Fact]
    public async Task Q06_Wrong_resource_denies()
    {
        await using var f = await ResourceFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var first = await f.AcceptAsync("q06-first", ct);
        var second = await f.AcceptAsync("q06-second", ct);
        // Grant the read only for the first delegation's supervision resource.
        await f.PublishResourceAsync(first, [AuthorityAction.MarangReadDelegation], ct);

        (await f.Tools.GetStatusAsync(first.ToString("D"), ct)).Should().Contain("state");
        var denied = await f.Tools.GetStatusAsync(second.ToString("D"), ct);
        JsonDocument.Parse(denied).RootElement.GetProperty("error").GetString().Should().Be("unknown delegation");
    }

    // AE5B-Q07 / AE5B-Q13 — narrowing denies the next attempt (no cached permit).
    [Fact]
    public async Task Q07_Q13_Narrowing_denies_the_next_attempt()
    {
        await using var f = await ResourceFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var id = await f.AcceptAsync("q07", ct);
        await f.PublishResourceAsync(id, [AuthorityAction.MarangCancel], ct);

        // Attempt 1: permit.
        (await f.Tools.CancelAsync(id.ToString("D"), ct)).Should().Contain("cancellation requested");

        // Authority narrows: the cancel action is withdrawn.
        await f.PublishResourceAsync(id, [AuthorityAction.MarangReadDelegation], ct);
        var second = await f.Tools.GetStatusAsync(id.ToString("D"), ct);
        JsonDocument.Parse(second).RootElement.TryGetProperty("state", out _).Should().BeTrue();

        var denied = await f.Tools.CancelAsync(id.ToString("D"), ct);
        JsonDocument.Parse(denied).RootElement.GetProperty("error").GetString().Should().Be("unknown delegation");
    }

    // AE5B-Q08 — expired authority denies.
    [Fact]
    public async Task Q08_Expired_authority_denies()
    {
        await using var f = await ResourceFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var id = await f.AcceptAsync("q08", ct);
        // Publish a resource snapshot whose grant window is already closed.
        await f.PublishResourceAsync(id, [AuthorityAction.MarangReadDelegation], ct, expired: true);

        var json = await f.Tools.GetStatusAsync(id.ToString("D"), ct);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().Should().Be("unknown delegation");
    }

    // AE5B-Q09 — authority source unavailable fails closed.
    [Fact]
    public async Task Q09_Unavailable_authority_fails_closed()
    {
        await using var f = await ResourceFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var id = await f.AcceptAsync("q09", ct);
        // No resource snapshot is published: current authority is unavailable.
        var outcome = await f.Resource.Gate.AuthorizeAsync(new MarangResourceAuthorizationInput(
            Caller, AuthorityAction.MarangReadDelegation, id.ToString("D"), Workspace,
            $"delegation/{id:D}/supervision", f.Credential()), ct);

        outcome.Status.Should().Be(MarangResourceAuthorizationStatus.Unavailable);
        (await f.Tools.GetStatusAsync(id.ToString("D"), ct)).Should().Contain("unknown delegation");
    }

    // AE5B-Q10 — evidence failure after permit fails closed.
    [Fact]
    public async Task Q10_Evidence_failure_fails_closed()
    {
        await using var f = await ResourceFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var id = await f.AcceptAsync("q10", ct);
        await f.PublishResourceAsync(id, [AuthorityAction.MarangReadDelegation], ct);
        var failing = f.BuildGateWithRefusingRecorder();

        var outcome = await failing.AuthorizeAsync(new MarangResourceAuthorizationInput(
            Caller, AuthorityAction.MarangReadDelegation, id.ToString("D"), Workspace,
            $"delegation/{id:D}/supervision", f.Credential()), ct);

        outcome.Status.Should().Be(MarangResourceAuthorizationStatus.Unavailable);
    }

    // AE5B-Q11 — unmapped action fails closed.
    [Fact]
    public async Task Q11_Unmapped_action_fails_closed()
    {
        await using var f = await ResourceFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var id = await f.AcceptAsync("q11", ct);
        await f.PublishResourceAsync(id, [AuthorityAction.MarangReadDelegation], ct);

        // A file verb is not a Marang resource meaning.
        var outcome = await f.Resource.Gate.AuthorizeAsync(new MarangResourceAuthorizationInput(
            Caller, AuthorityAction.ReadFile, id.ToString("D"), Workspace,
            $"delegation/{id:D}/supervision", f.Credential()), ct);

        outcome.Status.Should().Be(MarangResourceAuthorizationStatus.Denied);
        outcome.ReasonCode.Should().Be("marang.resource-action-unmapped");
    }

    // AE5B-Q12 — non-canonical resource identity fails closed.
    [Fact]
    public async Task Q12_Invalid_resource_identity_fails_closed()
    {
        await using var f = await ResourceFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var id = await f.AcceptAsync("q12", ct);
        await f.PublishResourceAsync(id, [AuthorityAction.MarangReadResult], ct);

        // Non-lowercase caller-supplied artifact segment: rejected, not folded.
        var outcome = await f.Resource.Gate.AuthorizeAsync(new MarangResourceAuthorizationInput(
            Caller, AuthorityAction.MarangReadResult, id.ToString("D"), Workspace,
            $"delegation/{id:D}/artifact/Artifact-B", f.Credential()), ct);

        outcome.Status.Should().Be(MarangResourceAuthorizationStatus.Denied);
        outcome.ReasonCode.Should().Be("marang.resource-invalid");
    }

    // AE5B-Q14 — tampered credential.
    [Fact]
    public async Task Q14_Tampered_credential_denies()
    {
        await using var f = await ResourceFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var id = await f.AcceptAsync("q14", ct);
        await f.PublishResourceAsync(id, [AuthorityAction.MarangReadDelegation], ct);
        f.SetCredential(f.Credential(tamper: true));

        var outcome = await f.Resource.Gate.AuthorizeAsync(new MarangResourceAuthorizationInput(
            Caller, AuthorityAction.MarangReadDelegation, id.ToString("D"), Workspace,
            $"delegation/{id:D}/supervision", f.Credential(true)), ct);

        outcome.Status.Should().Be(MarangResourceAuthorizationStatus.Denied);
        (await f.Tools.GetStatusAsync(id.ToString("D"), ct)).Should().Contain("unknown delegation");
    }

    // AE5B-Q15 — untrusted signer.
    [Fact]
    public async Task Q15_Untrusted_signer_denies()
    {
        await using var f = await ResourceFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var id = await f.AcceptAsync("q15", ct);
        await f.PublishResourceAsync(id, [AuthorityAction.MarangReadDelegation], ct);
        using var foreign = BiscuitPrivateKey.Generate();
        var foreignCredential = new MarangDelegationCredential(
            MarangDelegationCredentialKind.Biscuit, "key-2",
            await ResourceFixture.SignAsync(foreign, "key-2", ct));

        var outcome = await f.Resource.Gate.AuthorizeAsync(new MarangResourceAuthorizationInput(
            Caller, AuthorityAction.MarangReadDelegation, id.ToString("D"), Workspace,
            $"delegation/{id:D}/supervision", foreignCredential), ct);

        outcome.Status.Should().Be(MarangResourceAuthorizationStatus.Denied);
    }

    // AE5B-Q16 — workspace ceiling.
    [Fact]
    public async Task Q16_Workspace_ceiling_denies()
    {
        await using var f = await ResourceFixture.CreateAsync(policyWorkspaces: ["other-workspace"]);
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var id = await f.AcceptAsync("q16", ct);
        await f.PublishResourceAsync(id, [AuthorityAction.MarangReadDelegation], ct);

        var json = await f.Tools.GetStatusAsync(id.ToString("D"), ct);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().Should().Be("unknown delegation");
    }

    // AE5B-Q17 — evidence binds subject/action/resource/snapshot.
    [Fact]
    public async Task Q17_Evidence_binds_action_resource_and_snapshot()
    {
        await using var f = await ResourceFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var id = await f.AcceptAsync("q17", ct);
        await f.PublishResourceAsync(id, [AuthorityAction.MarangReadDelegation], ct);

        await f.Tools.GetStatusAsync(id.ToString("D"), ct);

        var entry = f.Resource.Evidence.Should().ContainSingle(e => e.Decision.Status == AuthorityStatus.Permit).Subject;
        entry.Request.Context.SubjectId.Should().Be(Caller);
        entry.Request.Action.Should().Be(AuthorityAction.MarangReadDelegation);
        entry.Request.RelativePath.Should().Be($"delegation/{id:D}/supervision");
        entry.Decision.SnapshotIdentity.Should().NotBeNullOrWhiteSpace();
        entry.Decision.SnapshotVersion.Should().NotBeNullOrWhiteSpace();
    }

    // AE5B-Q18 — side-effect ordering: denial leaves no effect.
    [Fact]
    public async Task Q18_Denied_cancel_has_no_side_effect()
    {
        await using var f = await ResourceFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var id = await f.AcceptAsync("q18", ct);
        var before = (await f.Runtime.GetStatusAsync(new DelegationId(id), ct))!.State;

        // Denied (no grant): the runtime is never reached; state is unchanged.
        (await f.Tools.CancelAsync(id.ToString("D"), ct)).Should().Contain("unknown delegation");
        (await f.Runtime.GetStatusAsync(new DelegationId(id), ct))!.State.Should().Be(before);

        // Permitted: the call reaches the runtime and reports its effect. (The
        // only observable side effect of cancelling a terminal NeedsSupervisor
        // delegation is the idempotent success itself.)
        await f.PublishResourceAsync(id, [AuthorityAction.MarangCancel], ct);
        (await f.Tools.CancelAsync(id.ToString("D"), ct)).Should().Contain("cancellation requested");
    }

    // AE5B-Q19 — coverage: every protected tool is gated (deny-all denies all).
    [Fact]
    public async Task Q19_Every_protected_tool_is_gated()
    {
        await using var f = await ResourceFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var id = await f.AcceptAsync("q19", ct);
        var checkpoint = Guid.NewGuid().ToString("D");
        // Real resource authority is fully granted ...
        await f.PublishResourceAsync(id, [
            AuthorityAction.MarangReadDelegation, AuthorityAction.MarangReadResult,
            AuthorityAction.MarangIntervene, AuthorityAction.MarangCancel], ct);
        // ... but a deny-all gate must make every protected operation fail closed,
        // proving no protected tool bypasses the gate.
        var (delegation, supervision) = f.BuildToolsWith(TestResourceGate.DenyAll());
        var idText = id.ToString("D");

        (await delegation.GetStatusAsync(idText, ct)).Should().Contain("unknown delegation");
        (await delegation.GetResultAsync(idText, ct)).Should().Contain("unknown delegation");
        (await delegation.CancelAsync(idText, ct)).Should().Contain("unknown delegation");
        (await supervision.WaitAsync(idText, 1, ct)).Should().Contain("unknown delegation");
        (await supervision.InspectAsync(idText, checkpoint, 0, "status", 50, 8192, ct)).Should().Contain("unknown delegation");
        (await supervision.InterveneAsync(idText, "approve", checkpoint, 0, "ok", null, ct)).Should().Contain("unknown delegation");
        (await supervision.GetArtifactAsync(idText, "artifact-1", ct)).Should().Contain("unknown delegation");
    }

    // Adversarial B — policy/workflow-surface actions stay distinct from Marang actions.
    [Fact]
    public async Task Adversarial_file_verb_grant_does_not_authorize_a_marang_action()
    {
        await using var f = await ResourceFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var id = await f.AcceptAsync("adv-file", ct);
        await f.PublishResourceAsync(id, [AuthorityAction.ReadFile, AuthorityAction.Release], ct);

        var json = await f.Tools.GetStatusAsync(id.ToString("D"), ct);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().Should().Be("unknown delegation");
    }

    private sealed class ResourceFixture : IAsyncDisposable
    {
        public static readonly AuthorityStoreActor Actor = new(Tenant, "resource-host", "session");
        private const string AcceptanceFence = "accept";
        private const string ResourceFence = "resource";
        private const string Revision = "g0";

        private readonly string _directory;
        private readonly DefaultHttpContext _http = new();
        private readonly BiscuitPrivateKey _signingKey;
        private readonly byte[] _tokenBytes;
        private readonly DateTimeOffset _now;
        private readonly MarangResourceAuthorizationPolicy _policy;
        private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);

        public string DatabasePath { get; }
        public SqliteAuthorityStore PublisherStore { get; }
        public MarangHufuAcceptanceComposition Acceptance { get; }
        public MarangHufuResourceComposition Resource { get; }
        public MarangDelegationCatalog Catalog { get; private set; } = null!;
        public DelegationRuntime Runtime { get; private set; } = null!;
        public MarangDelegationTools Tools { get; private set; } = null!;
        public MarangSupervisionTools Supervision { get; private set; } = null!;
        public IHttpContextAccessor Accessor { get; } = new HttpContextAccessor();

        private ResourceFixture(string directory, string databasePath, DateTimeOffset now,
            BiscuitPrivateKey signingKey, byte[] tokenBytes, MarangResourceAuthorizationPolicy policy,
            SqliteAuthorityStore publisherStore, MarangHufuAcceptanceComposition acceptance,
            MarangHufuResourceComposition resource, DefaultHttpContext http)
        {
            _directory = directory;
            DatabasePath = databasePath;
            _now = now;
            _signingKey = signingKey;
            _tokenBytes = tokenBytes;
            _policy = policy;
            PublisherStore = publisherStore;
            Acceptance = acceptance;
            Resource = resource;
            _http = http;
        }

        public static async Task<ResourceFixture> CreateAsync(
            string[]? policyWorkspaces = null)
        {
            var ct = TestContext.Current.CancellationToken;
            var directory = Path.Combine(Path.GetTempPath(), "marang-ae5b-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var dbPath = Path.Combine(directory, "hufu.db");
            var now = DateTimeOffset.UtcNow;

            using var key = BiscuitPrivateKey.Generate();
            var tokenBytes = await SignAsync(key, KeyId, ct).ConfigureAwait(false);
            var signingKey = BiscuitPrivateKey.Import(key.Export());
            var issuer = new MarangHufuTrustedIssuer(Realm, KeyId, signingKey.PublicKey);

            var publisher = new SqliteAuthorityStore(dbPath, new AllowAllStoreAuthorizer());
            var acceptance = MarangHufuAcceptanceCompositionFactory.Create(
                new MarangHufuAcceptanceOptions
                {
                    AuthorityDatabasePath = dbPath,
                    TenantId = Tenant,
                    TrustedIssuers = [issuer],
                    AcceptancePolicy = new MarangDelegationAcceptancePolicy
                    {
                        AllowedCallers = new HashSet<string> { Caller },
                        AllowedWorkspaces = new HashSet<string> { Workspace },
                        AllowedProviders = new HashSet<string> { Provider },
                    },
                },
                new MarangHufuAcceptanceStoreAuthorizer(), new FixedClock(now));

            var policy = new MarangResourceAuthorizationPolicy
            {
                AllowedCallers = new HashSet<string> { Caller },
                AllowedWorkspaces = new HashSet<string>(policyWorkspaces ?? [Workspace], StringComparer.Ordinal),
            };
            var resource = MarangHufuResourceCompositionFactory.Create(
                new MarangHufuResourceOptions
                {
                    AuthorityDatabasePath = dbPath,
                    TenantId = Tenant,
                    TrustedIssuers = [issuer],
                    ResourcePolicy = policy,
                },
                new MarangHufuAcceptanceStoreAuthorizer(), new FixedClock(now));

            var http = new DefaultHttpContext();
            http.Items[MarangHttpContextKeys.CallerIdentity] = Caller;
            var authentication = new MarangAuthenticationOptions
            {
                AllowedWorkspaceRoots = new Dictionary<string, string[]> { [Caller] = [Workspace] },
            };
            var fixture = new ResourceFixture(directory, dbPath, now, signingKey, tokenBytes, policy, publisher,
                acceptance, resource, http);
            var accessor = fixture.Accessor;
            var catalog = new MarangDelegationCatalog(acceptance.Gate, httpContext: accessor);
            var runtime = new DelegationRuntime(catalog,
                new MarangAdmissionVerifier(), new InMemoryProviderRegistry(), new InMemoryExternalOperationProviderCatalog());
            var tools = new MarangDelegationTools(runtime, catalog, accessor, Options.Create(authentication), resource.Gate);
            var supervision = new MarangSupervisionTools(runtime, catalog, accessor, Options.Create(authentication), resource.Gate);
            fixture.Catalog = catalog;
            fixture.Runtime = runtime;
            fixture.Tools = tools;
            fixture.Supervision = supervision;
            fixture.SetCredential(new MarangDelegationCredential(
                MarangDelegationCredentialKind.Biscuit, KeyId, tokenBytes));
            return fixture;
        }

        public void Attach() => ((HttpContextAccessor)Accessor).HttpContext = _http;

        public MarangDelegationCredential Credential(bool tamper = false)
        {
            var bytes = (byte[])_tokenBytes.Clone();
            if (tamper) bytes[^1] ^= 0xFF;
            return new MarangDelegationCredential(MarangDelegationCredentialKind.Biscuit, KeyId, bytes);
        }

        public void SetCredential(MarangDelegationCredential credential) =>
            _http.Items[MarangHttpContextKeys.DelegationCredential] =
                MarangDelegationCredential.TryDecode(credential.Encode());

        public async Task<Guid> AcceptAsync(string requestKey, CancellationToken ct)
        {
            var acceptanceKey = AcceptanceIdentity.AcceptanceKey(Tenant, Caller, requestKey);
            var id = AcceptanceIdentity.DelegationIdFor(acceptanceKey);
            var context = new AuthenticatedAuthorityContext(Tenant, Caller, acceptanceKey, Revision, AcceptanceFence);
            await PublishAsync(context, "acceptance",
                [new AuthorityGrant("g", [AuthorityAction.ReadFile],
                    new AuthorityScope(Workspace, "", AuthorityScopeKind.Subtree), [],
                    _now.AddMinutes(-1), _now.AddDays(1))], ct);
            var json = await Tools.DelegateAsync(requestKey, "Do the work", Provider, Workspace, 1, ct);
            JsonDocument.Parse(json).RootElement.GetProperty("delegationId").GetString()
                .Should().Be(id.ToString("D"));
            return id;
        }

        public async Task PublishResourceAsync(Guid id, IReadOnlyList<AuthorityAction> actions,
            CancellationToken ct, bool expired = false)
        {
            var context = new AuthenticatedAuthorityContext(Tenant, Caller, id.ToString("D"), Revision, ResourceFence);
            await PublishAsync(context, "resource/" + id.ToString("D"),
                [new AuthorityGrant("g", actions,
                    new AuthorityScope(Workspace, $"delegation/{id:D}", AuthorityScopeKind.Subtree), [],
                    _now.AddMinutes(-1), expired ? _now.AddSeconds(-1) : _now.AddDays(1))], ct);
        }

        private async Task PublishAsync(AuthenticatedAuthorityContext context, string keyHint,
            IReadOnlyList<AuthorityGrant> grants, CancellationToken ct)
        {
            // Snapshots are keyed by subject (the full context), so track the
            // sequence per context identity, not per logical fixture area.
            var key = string.Join("|", context.TenantId, context.SubjectId, context.RunId,
                context.RevisionId, context.FenceId);
            var snapshot = new AuthoritySnapshot(context, "v-" + Guid.NewGuid().ToString("N"),
                [new AuthorityLayer("run", grants)], [], _now.AddDays(1));
            var sequence = _sequences.TryGetValue(key, out var current) ? current : 0;
            for (var attempt = 0; attempt < 8; attempt++, sequence++)
            {
                var result = await PublisherStore.PublishAsync(
                    new AuthorityPublishCommand("publish-" + Guid.NewGuid().ToString("N"), Actor, snapshot, sequence), ct)
                    .ConfigureAwait(false);
                if (result.Status == AuthorityMutationStatus.Applied)
                {
                    _sequences[key] = sequence + 1;
                    return;
                }
                if (result.Status != AuthorityMutationStatus.Conflict)
                    throw new InvalidOperationException($"Arrange failed: {result.Status}.");
            }
            throw new InvalidOperationException($"Arrange failed: sequence conflict for {keyHint}.");
        }

        public IMarangResourceAuthorizationGate BuildGateWithRefusingRecorder()
        {
            var store = new SqliteAuthorityStore(DatabasePath, new AllowAllStoreAuthorizer());
            var actor = new AuthorityStoreActor(Tenant, "resource-host", "session");
            var source = new AuthorityStoreSnapshotSource(store, actor);
            var authorizer = new CurrentAuthorityRequestAuthorizer(
                source,
                new HufuResourceAuthorizationEvaluator(_policy),
                new RefusingRecorder(),
                new FixedClock(_now));
            return new MarangResourceAuthorizationGate(
                new BiscuitTrustVerifier([new MarangHufuTrustedIssuer(Realm, KeyId, _signingKey.PublicKey)]),
                authorizer, Tenant);
        }

        public (MarangDelegationTools Delegation, MarangSupervisionTools Supervision) BuildToolsWith(
            IMarangResourceAuthorizationGate gate)
        {
            var authentication = new MarangAuthenticationOptions
            {
                AllowedWorkspaceRoots = new Dictionary<string, string[]> { [Caller] = [Workspace] },
            };
            return (new MarangDelegationTools(Runtime, Catalog, Accessor, Options.Create(authentication), gate),
                new MarangSupervisionTools(Runtime, Catalog, Accessor, Options.Create(authentication), gate));
        }

        public static async Task<byte[]> SignAsync(BiscuitPrivateKey key, string keyId, CancellationToken ct = default)
        {
            using var scratch = new BiscuitKeyRing();
            scratch.AddSigningKey(Realm, keyId, BiscuitPrivateKey.Import(key.Export()));
            using var lease = await scratch.AcquireSigningKeyAsync(Realm, ct).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(lease);
            var builder = BiscuitTokenBuilder.Create();
            builder.AddFact("marang_test({value})", [KeyValuePair.Create("value", BiscuitParam.Str("yes"))]);
            return lease.Build(builder).ToBytes();
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
            }
            catch (IOException) { }
            _signingKey.Dispose();
            return ValueTask.CompletedTask;
        }

        private sealed class AllowAllStoreAuthorizer : IAuthorityStoreAuthorizer
        {
            public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request,
                CancellationToken cancellationToken = default) =>
                new(new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
        }

        private sealed class RefusingRecorder : IAuthorityDecisionRecorder
        {
            public ValueTask<bool> RecordAsync(AuthorityRequest request, AuthorityDecision decision,
                CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        }

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
