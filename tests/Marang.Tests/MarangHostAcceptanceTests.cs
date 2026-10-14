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
/// HC-1 qualification: the default host composes a live Hufu current-authority
/// source plus real Biscuit credential trust, end to end through the MCP tool
/// boundary. No stubs stand in for the authority source or the trust check:
/// snapshots live in a real SQLite store and credentials carry real Ed25519
/// signatures verified against host-pinned public keys. Each test arranges
/// authority explicitly with <see cref="HostFixture.EnsureSnapshotAsync"/>
/// before acting, mirroring the operator publishing authority out of band.
/// </summary>
public sealed class MarangHostAcceptanceTests
{
    private const string Tenant = "marang";
    private const string Caller = "tester";
    private const string Workspace = "workspace";
    private const string Provider = "no-such-provider";
    private const string Realm = "marang-realm";
    private const string KeyId = "key-1";

    [Fact]
    public async Task Live_accept_returns_deterministic_delegation_with_evidence()
    {
        await using var f = await HostFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var requestKey = f.RequestKey();
        await f.EnsureSnapshotAsync(requestKey);

        var json = await f.Tools.DelegateAsync(requestKey, "Do the work", Provider, Workspace, 1, ct);
        var document = JsonDocument.Parse(json);

        var expectedId = AcceptanceIdentity.DelegationIdFor(f.AcceptanceKey(requestKey)).ToString("D");
        document.RootElement.GetProperty("delegationId").GetString().Should().Be(expectedId);
        document.RootElement.GetProperty("caller").GetString().Should().Be(Caller);
        f.Composition.Evidence.Should().ContainSingle()
            .Which.Decision.Status.Should().Be(AuthorityStatus.Permit);
        f.Catalog.List(Caller).Should().ContainSingle();
    }

    [Fact]
    public async Task Missing_credential_is_denied_with_no_state_and_no_evidence()
    {
        await using var f = await HostFixture.CreateAsync(includeCredential: false);
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var requestKey = f.RequestKey();
        await f.EnsureSnapshotAsync(requestKey);

        var json = await f.Tools.DelegateAsync(requestKey, "Do the work", Provider, Workspace, 1, ct);
        var document = JsonDocument.Parse(json);

        document.RootElement.GetProperty("error").GetString().Should().Be("delegation not accepted");
        document.RootElement.GetProperty("status").GetString().Should().Be("Denied");
        document.RootElement.GetProperty("reason").GetString().Should().Be("marang.delegation-credential-missing");
        f.Catalog.List(Caller).Should().BeEmpty();
        f.Composition.Evidence.Should().BeEmpty();
    }

    [Fact]
    public async Task Tampered_credential_is_rejected_before_the_hufu_decision()
    {
        await using var f = await HostFixture.CreateAsync(tamperCredential: true);
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var requestKey = f.RequestKey();
        await f.EnsureSnapshotAsync(requestKey);

        var json = await f.Tools.DelegateAsync(requestKey, "Do the work", Provider, Workspace, 1, ct);

        JsonDocument.Parse(json).RootElement.GetProperty("reason").GetString()
            .Should().Be("marang.delegation-credential-invalid");
        f.Catalog.List(Caller).Should().BeEmpty();
        f.Composition.Evidence.Should().BeEmpty();
    }

    [Fact]
    public async Task Untrusted_issuer_is_rejected_despite_a_valid_signature()
    {
        await using var f = await HostFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var requestKey = f.RequestKey();
        await f.EnsureSnapshotAsync(requestKey);
        // A genuinely signed token from an unpinned key: signature math passes,
        // trust does not.
        f.SetCredential(await f.SignedCredentialAsync("key-2"));

        var json = await f.Tools.DelegateAsync(requestKey, "Do the work", Provider, Workspace, 1, ct);

        JsonDocument.Parse(json).RootElement.GetProperty("reason").GetString()
            .Should().Be("marang.delegation-credential-invalid");
        f.Catalog.List(Caller).Should().BeEmpty();
    }

    [Fact]
    public async Task Mislabelled_credential_is_rejected()
    {
        await using var f = await HostFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var requestKey = f.RequestKey();
        await f.EnsureSnapshotAsync(requestKey);
        // Bytes signed by the pinned key but labelled with another key id: the
        // label, not just the math, must bind to trust.
        f.SetCredential(f.Credential("key-2", tamper: false));

        var json = await f.Tools.DelegateAsync(requestKey, "Do the work", Provider, Workspace, 1, ct);

        JsonDocument.Parse(json).RootElement.GetProperty("reason").GetString()
            .Should().Be("marang.delegation-credential-invalid");
        f.Catalog.List(Caller).Should().BeEmpty();
    }

    [Fact]
    public async Task Expired_authority_is_denied_with_decision_evidence_but_no_descriptor()
    {
        await using var f = await HostFixture.CreateAsync(expired: true);
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var requestKey = f.RequestKey();
        await f.EnsureSnapshotAsync(requestKey);

        var json = await f.Tools.DelegateAsync(requestKey, "Do the work", Provider, Workspace, 1, ct);
        var document = JsonDocument.Parse(json);

        document.RootElement.GetProperty("status").GetString().Should().Be("Denied");
        f.Catalog.List(Caller).Should().BeEmpty();
        // The denial itself is recorded as decision evidence; no acceptance
        // state follows from it.
        f.Composition.Evidence.Should().ContainSingle()
            .Which.Decision.Status.Should().Be(AuthorityStatus.Deny);
    }

    [Fact]
    public async Task Absent_authority_is_unavailable_with_no_state()
    {
        await using var f = await HostFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;

        var json = await f.Tools.DelegateAsync(f.RequestKey(), "Do the work", Provider, Workspace, 1, ct);

        JsonDocument.Parse(json).RootElement.GetProperty("status").GetString()
            .Should().Be("Unavailable");
        f.Catalog.List(Caller).Should().BeEmpty();
    }

    [Fact]
    public async Task Outside_acceptance_ceiling_is_denied_by_the_hufu_policy()
    {
        await using var f = await HostFixture.CreateAsync(policyWorkspaces: ["elsewhere"]);
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var requestKey = f.RequestKey();
        await f.EnsureSnapshotAsync(requestKey);

        var json = await f.Tools.DelegateAsync(requestKey, "Do the work", Provider, Workspace, 1, ct);
        var document = JsonDocument.Parse(json);

        // The MCP workspace check passes ("workspace" is a caller root); the
        // Hufu acceptance ceiling denies.
        document.RootElement.GetProperty("status").GetString().Should().Be("Denied");
        document.RootElement.GetProperty("reason").GetString().Should().Be("marang.acceptance-denied");
        f.Catalog.List(Caller).Should().BeEmpty();
    }

    [Fact]
    public async Task Unavailable_store_fails_closed_without_throwing()
    {
        await using var f = await HostFixture.CreateAsync(missingDatabaseDirectory: true);
        f.Attach();
        var ct = TestContext.Current.CancellationToken;

        var json = await f.Tools.DelegateAsync(f.RequestKey(), "Do the work", Provider, Workspace, 1, ct);

        JsonDocument.Parse(json).RootElement.GetProperty("status").GetString()
            .Should().Be("Unavailable");
        f.Catalog.List(Caller).Should().BeEmpty();
    }

    [Fact]
    public async Task Evidence_sink_failure_fails_closed()
    {
        await using var f = await HostFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var requestKey = f.RequestKey();
        await f.EnsureSnapshotAsync(requestKey);
        // Same live source and trust, but an evidence sink that refuses: the
        // decision must not authorize.
        var failing = new MarangDelegationAcceptanceGate(
            new BiscuitTrustVerifier([new MarangHufuTrustedIssuer(Realm, KeyId, f.PublicKey)]),
            new CurrentDelegationAcceptanceAuthorizer(
                new AuthorityStoreSnapshotSource(f.PublisherStore, HostFixture.Actor),
                new HufuDelegationAcceptanceEvaluator(HostFixture.Allow),
                new HufuDelegationAcceptanceRecorder((_, _, _) => ValueTask.FromResult(false)),
                TimeProvider.System),
            Tenant);
        var catalog = new MarangDelegationCatalog(failing, httpContext: f.Accessor);
        var runtime = new DelegationRuntime(catalog,
            new MarangAdmissionVerifier(), new InMemoryProviderRegistry(), new InMemoryExternalOperationProviderCatalog());
        var tools = new MarangDelegationTools(runtime, catalog, f.Accessor, Options.Create(f.Authentication), TestResourceGate.AllowAll());

        var json = await tools.DelegateAsync(requestKey, "Do the work", Provider, Workspace, 1, ct);

        JsonDocument.Parse(json).RootElement.GetProperty("status").GetString()
            .Should().Be("Unavailable");
        catalog.List(Caller).Should().BeEmpty();
    }

    [Fact]
    public async Task Retry_converges_on_one_delegation_then_reevaluates_narrowing()
    {
        await using var f = await HostFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var requestKey = f.RequestKey();
        await f.EnsureSnapshotAsync(requestKey);

        var first = JsonDocument.Parse(
            await f.Tools.DelegateAsync(requestKey, "Do the work", Provider, Workspace, 1, ct));
        var second = JsonDocument.Parse(
            await f.Tools.DelegateAsync(requestKey, "Do the work", Provider, Workspace, 1, ct));

        second.RootElement.GetProperty("delegationId").GetString()
            .Should().Be(first.RootElement.GetProperty("delegationId").GetString());
        f.Catalog.List(Caller).Should().ContainSingle();
        // The gate runs before idempotency: every use re-evaluates.
        f.Composition.Evidence.Should().HaveCount(2);

        // A narrowed ceiling on the same authority source denies the retry.
        await using var narrowed = await HostFixture.CreateAsync(
            databasePath: f.DatabasePath, policyWorkspaces: ["elsewhere"]);
        narrowed.Attach();
        await narrowed.EnsureSnapshotAsync(requestKey);
        var retry = JsonDocument.Parse(await narrowed.Tools.DelegateAsync(
            requestKey, "Do the work", Provider, Workspace, 1, ct));
        retry.RootElement.GetProperty("status").GetString().Should().Be("Denied");
        retry.RootElement.GetProperty("reason").GetString().Should().Be("marang.acceptance-denied");
    }

    [Fact]
    public async Task Unconfigured_host_fails_closed()
    {
        var composition = MarangHufuAcceptanceCompositionFactory.Create(
            new MarangHufuAcceptanceOptions(), new MarangHufuAcceptanceStoreAuthorizer());
        composition.Gate.Should().BeOfType<FailClosedDelegationAcceptanceGate>();

        var context = new DefaultHttpContext();
        context.Items[MarangHttpContextKeys.CallerIdentity] = Caller;
        var accessor = new HttpContextAccessor { HttpContext = context };
        var catalog = new MarangDelegationCatalog(composition.Gate, httpContext: accessor);
        var runtime = new DelegationRuntime(catalog,
            new MarangAdmissionVerifier(), new InMemoryProviderRegistry(), new InMemoryExternalOperationProviderCatalog());
        var tools = new MarangDelegationTools(runtime, catalog, accessor,
            Options.Create(new MarangAuthenticationOptions
            {
                AllowedWorkspaceRoots = new Dictionary<string, string[]> { [Caller] = [Workspace] },
            }), TestResourceGate.AllowAll());
        var ct = TestContext.Current.CancellationToken;

        var json = await tools.DelegateAsync(
            "hc1-" + Guid.NewGuid().ToString("N"), "Do the work", Provider, Workspace, 1, ct);
        var document = JsonDocument.Parse(json);

        document.RootElement.GetProperty("status").GetString().Should().Be("Unavailable");
        document.RootElement.GetProperty("reason").GetString()
            .Should().Be("marang.acceptance-authority-not-configured");
        catalog.List(Caller).Should().BeEmpty();
    }

    [Fact]
    public async Task Trust_verifier_checks_signature_under_the_pinned_key()
    {
        await using var f = await HostFixture.CreateAsync();
        f.Attach();
        var ct = TestContext.Current.CancellationToken;
        var verifier = new BiscuitTrustVerifier(
            [new MarangHufuTrustedIssuer(Realm, KeyId, f.PublicKey)]);

        (await verifier.VerifyAsync(f.Credential(KeyId, tamper: false), Caller, ct))
            .Should().Be(MarangDelegationCredentialStatus.Verified);
        (await verifier.VerifyAsync(f.Credential(KeyId, tamper: true), Caller, ct))
            .Should().Be(MarangDelegationCredentialStatus.Invalid);
        (await verifier.VerifyAsync(null, Caller, ct))
            .Should().Be(MarangDelegationCredentialStatus.Missing);

        Assert.Throws<ArgumentException>(() => new BiscuitTrustVerifier([]));
        Assert.Throws<ArgumentException>(() =>
            new BiscuitTrustVerifier([new MarangHufuTrustedIssuer(Realm, "", f.PublicKey)]));
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        public static readonly AuthorityStoreActor Actor = new(Tenant, "acceptance-host", "session");
        public static readonly MarangDelegationAcceptancePolicy Allow = new()
        {
            AllowedCallers = new HashSet<string> { Caller },
            AllowedWorkspaces = new HashSet<string> { Workspace },
            AllowedProviders = new HashSet<string> { Provider },
        };

        private readonly string _directory;
        private readonly DefaultHttpContext _http = new();
        private readonly BiscuitPrivateKey _signingKey;
        private readonly byte[] _tokenBytes;
        private readonly DateTimeOffset _now;

        public string DatabasePath { get; }
        public BiscuitPublicKey PublicKey => _signingKey.PublicKey;
        public SqliteAuthorityStore PublisherStore { get; }
        public MarangHufuAcceptanceComposition Composition { get; }
        public MarangDelegationCatalog Catalog { get; }
        public MarangDelegationTools Tools { get; }
        public IHttpContextAccessor Accessor { get; }
        public MarangAuthenticationOptions Authentication { get; }

        private HostFixture(
            string directory, string databasePath, DateTimeOffset now, BiscuitPrivateKey signingKey,
            byte[] tokenBytes, SqliteAuthorityStore publisherStore, MarangHufuAcceptanceComposition composition,
            MarangDelegationCatalog catalog, MarangDelegationTools tools, IHttpContextAccessor accessor,
            MarangAuthenticationOptions authentication, DefaultHttpContext http)
        {
            _directory = directory;
            DatabasePath = databasePath;
            _now = now;
            _signingKey = signingKey;
            _tokenBytes = tokenBytes;
            PublisherStore = publisherStore;
            Composition = composition;
            Catalog = catalog;
            Tools = tools;
            Accessor = accessor;
            Authentication = authentication;
            _http = http;
        }

        public static async Task<HostFixture> CreateAsync(
            bool includeCredential = true,
            bool tamperCredential = false,
            bool expired = false,
            string[]? policyWorkspaces = null,
            bool missingDatabaseDirectory = false,
            string? databasePath = null)
        {
            var ct = TestContext.Current.CancellationToken;
            var directory = Path.Combine(Path.GetTempPath(), "marang-hc1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var dbPath = databasePath ?? Path.Combine(directory, "hufu.db");
            if (missingDatabaseDirectory)
                dbPath = Path.Combine(directory, "no-such-dir", "hufu.db");

            using var key = BiscuitPrivateKey.Generate();
            var tokenBytes = await BuildTokenBytesAsync(key, KeyId, ct).ConfigureAwait(false);
            // The fixture owns its signing copy; the composition pins only the
            // public key, so no private material crosses the host boundary.
            var signingKey = BiscuitPrivateKey.Import(key.Export());

            var publisherPath = missingDatabaseDirectory ? Path.Combine(directory, "publish.db") : dbPath;
            var publisher = new SqliteAuthorityStore(publisherPath, new AllowAllStoreAuthorizer());
            var now = DateTimeOffset.UtcNow;
            var clock = expired
                ? (TimeProvider)new FixedClock(now.AddDays(2))
                : new FixedClock(now);
            var policy = Allow with
            {
                AllowedWorkspaces = new HashSet<string>(policyWorkspaces ?? [Workspace], StringComparer.Ordinal),
            };
            var authentication = new MarangAuthenticationOptions
            {
                AllowedWorkspaceRoots = new Dictionary<string, string[]> { [Caller] = [Workspace] },
            };
            var composition = MarangHufuAcceptanceCompositionFactory.Create(
                new MarangHufuAcceptanceOptions
                {
                    AuthorityDatabasePath = dbPath,
                    TenantId = Tenant,
                    TrustedIssuers = [new MarangHufuTrustedIssuer(Realm, KeyId, signingKey.PublicKey)],
                    AcceptancePolicy = policy,
                },
                new MarangHufuAcceptanceStoreAuthorizer(),
                clock);

            var http = new DefaultHttpContext();
            http.Items[MarangHttpContextKeys.CallerIdentity] = Caller;
            // The accessor slot is bound by Attach in the test's own context:
            // HttpContextAccessor is AsyncLocal, so a slot set inside this
            // async factory would not flow back to the caller.
            var accessor = new HttpContextAccessor();
            var catalog = new MarangDelegationCatalog(composition.Gate, httpContext: accessor);
            var runtime = new DelegationRuntime(catalog,
                new MarangAdmissionVerifier(), new InMemoryProviderRegistry(), new InMemoryExternalOperationProviderCatalog());
            var tools = new MarangDelegationTools(runtime, catalog, accessor, Options.Create(authentication), TestResourceGate.AllowAll());

            var fixture = new HostFixture(directory, dbPath, now, signingKey, tokenBytes,
                publisher, composition, catalog, tools, accessor, authentication, http);
            if (includeCredential)
                fixture.SetCredential(fixture.Credential(KeyId, tamperCredential));
            return fixture;
        }

        public string RequestKey() => "hc1-" + Guid.NewGuid().ToString("N");

        /// <summary>
        /// Binds the fixture's HTTP context to the test's own execution
        /// context. Must run in the test body (a synchronous call shares the
        /// caller's context; an async factory's slot would not flow back).
        /// </summary>
        public void Attach() => ((HttpContextAccessor)Accessor).HttpContext = _http;

        public string AcceptanceKey(string requestKey) =>
            AcceptanceIdentity.AcceptanceKey(Tenant, Caller, requestKey);

        public MarangDelegationCredential Credential(string keyId, bool tamper)
        {
            var bytes = (byte[])_tokenBytes.Clone();
            if (tamper) bytes[^1] ^= 0xFF;
            return new MarangDelegationCredential(MarangDelegationCredentialKind.Biscuit, keyId, bytes);
        }

        public async Task<MarangDelegationCredential> SignedCredentialAsync(string keyId)
        {
            using var other = BiscuitPrivateKey.Generate();
            var bytes = await BuildTokenBytesAsync(other, keyId, TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
            return new MarangDelegationCredential(MarangDelegationCredentialKind.Biscuit, keyId, bytes);
        }

        public void SetCredential(MarangDelegationCredential credential) =>
            _http.Items[MarangHttpContextKeys.DelegationCredential] =
                MarangDelegationCredential.TryDecode(credential.Encode());

        /// <summary>
        /// Publishes the operator's ceiling snapshot for one acceptance context:
        /// the exact context shape the gate will request. Fresh subjects start
        /// at sequence zero.
        /// </summary>
        public async Task EnsureSnapshotAsync(string requestKey)
        {
            var ct = TestContext.Current.CancellationToken;
            var context = new AuthenticatedAuthorityContext(
                Tenant, Caller, AcceptanceKey(requestKey),
                AcceptanceIdentity.InitialGeneration, "accept");
            // Snapshot versions are unique per subject: re-publishing a
            // ceiling supersedes under a new version, never by reuse.
            var snapshot = new AuthoritySnapshot(context, "v-" + Guid.NewGuid().ToString("N"),
                [new AuthorityLayer("run", [
                    new AuthorityGrant("g", [AuthorityAction.ReadFile],
                        new AuthorityScope(Workspace, "", AuthorityScopeKind.Subtree), [],
                        _now.AddMinutes(-1), _now.AddDays(1))])],
                [], _now.AddDays(1));
            // Sequences are per subject: a fresh subject starts at zero, and a
            // re-published ceiling (e.g. a second fixture sharing the database)
            // must continue the subject's sequence.
            for (var sequence = 0L; sequence < 4; sequence++)
            {
                var result = await PublisherStore.PublishAsync(
                    new AuthorityPublishCommand("publish-" + Guid.NewGuid().ToString("N"), Actor, snapshot, sequence), ct)
                    .ConfigureAwait(false);
                if (result.Status == AuthorityMutationStatus.Applied) return;
                if (result.Status != AuthorityMutationStatus.Conflict)
                    throw new InvalidOperationException($"Authority arrange failed: {result.Status}.");
            }
            throw new InvalidOperationException("Authority arrange failed: sequence Conflict.");
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

        private static async Task<byte[]> BuildTokenBytesAsync(
            BiscuitPrivateKey key, string keyId, CancellationToken ct = default)
        {
            using var scratch = new BiscuitKeyRing();
            scratch.AddSigningKey(Realm, keyId, BiscuitPrivateKey.Import(key.Export()));
            using var lease = await scratch.AcquireSigningKeyAsync(Realm, ct).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(lease);
            var builder = BiscuitTokenBuilder.Create();
            builder.AddFact("marang_test({value})",
                [KeyValuePair.Create("value", BiscuitParam.Str("yes"))]);
            return lease.Build(builder).ToBytes();
        }

        private sealed class AllowAllStoreAuthorizer : IAuthorityStoreAuthorizer
        {
            public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(AuthorityStoreAccessRequest request,
                CancellationToken cancellationToken = default) =>
                new(new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
        }

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}

