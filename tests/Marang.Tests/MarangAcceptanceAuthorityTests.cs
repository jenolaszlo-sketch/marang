using FluentAssertions;
using Marang.DelegationAuthority;
using Marang.Hufu;
using Microsoft.AspNetCore.Http;
using Penghou.Hufu;
using Penghou.Hufu.Sqlite;
using Penghou.Qingniao;

namespace Marang.Tests;

/// <summary>
/// AE-5A.2 qualification: the mandatory Hufu <c>DelegationAcceptanceOperation.Accept</c>
/// gate runs at the very start of acceptance, before any durable Marang state.
/// Credential verification precedes authorization; a signature never authorizes.
/// </summary>
public sealed class MarangAcceptanceAuthorityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private const string Tenant = "marang";
    private const string Caller = "alice";
    private const string Provider = "provider-one";
    private const string Workspace = "workspace";

    private static readonly MarangDelegationAcceptancePolicy Allow = new()
    {
        AllowedCallers = new HashSet<string> { Caller },
        AllowedWorkspaces = new HashSet<string> { Workspace },
        AllowedProviders = new HashSet<string> { Provider },
    };

    [Fact]
    public async Task Accepted_delegation_proceeds_and_records_acceptance_evidence()
    {
        await using var f = new Fixture();
        var accepted = await f.Catalog.AcceptAsync(new DelegationCallerScope(Caller), f.Request("k1"), TestContext.Current.CancellationToken);
        accepted.IsNew.Should().BeTrue();
        f.Records.Should().ContainSingle();
        (await f.Descriptors.GetByAcceptanceKeyAsync(f.AcceptanceKey("k1"), TestContext.Current.CancellationToken)).Should().NotBeNull();
    }

    [Fact]
    public async Task Missing_credential_is_denied_before_any_durable_state()
    {
        await using var f = new Fixture(includeCredential: false);
        var ex = await Assert.ThrowsAsync<MarangDelegationAcceptanceException>(() =>
            f.Catalog.AcceptAsync(new DelegationCallerScope(Caller), f.Request("k1"), TestContext.Current.CancellationToken).AsTask());
        ex.Outcome.ReasonCode.Should().Be("marang.delegation-credential-missing");
        (await f.Descriptors.GetByAcceptanceKeyAsync(f.AcceptanceKey("k1"), TestContext.Current.CancellationToken)).Should().BeNull();
        f.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Tampered_credential_is_rejected_before_the_hufu_decision()
    {
        await using var f = new Fixture(verifierStatus: MarangDelegationCredentialStatus.Invalid);
        var ex = await Assert.ThrowsAsync<MarangDelegationAcceptanceException>(() =>
            f.Catalog.AcceptAsync(new DelegationCallerScope(Caller), f.Request("k1"), TestContext.Current.CancellationToken).AsTask());
        ex.Outcome.ReasonCode.Should().Be("marang.delegation-credential-invalid");
        (await f.Descriptors.GetByAcceptanceKeyAsync(f.AcceptanceKey("k1"), TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Valid_credential_but_denied_current_authority_is_denied()
    {
        var denied = Allow with { AllowedCallers = new HashSet<string>() };
        await using var f = new Fixture(policy: denied);
        var ex = await Assert.ThrowsAsync<MarangDelegationAcceptanceException>(() =>
            f.Catalog.AcceptAsync(new DelegationCallerScope(Caller), f.Request("k1"), TestContext.Current.CancellationToken).AsTask());
        ex.Outcome.Status.Should().Be(MarangDelegationAcceptanceStatus.Denied);
        (await f.Descriptors.GetByAcceptanceKeyAsync(f.AcceptanceKey("k1"), TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Parent_narrowing_denies_when_workspace_ceiling_removed()
    {
        var narrowed = Allow with { AllowedWorkspaces = new HashSet<string>() };
        await using var f = new Fixture(policy: narrowed);
        var ex = await Assert.ThrowsAsync<MarangDelegationAcceptanceException>(() =>
            f.Catalog.AcceptAsync(new DelegationCallerScope(Caller), f.Request("k1"), TestContext.Current.CancellationToken).AsTask());
        ex.Outcome.Status.Should().Be(MarangDelegationAcceptanceStatus.Denied);
    }

    [Fact]
    public async Task Unavailable_current_authority_yields_no_durable_state()
    {
        await using var f = new Fixture(authorityAvailable: false);
        var ex = await Assert.ThrowsAsync<MarangDelegationAcceptanceException>(() =>
            f.Catalog.AcceptAsync(new DelegationCallerScope(Caller), f.Request("k1"), TestContext.Current.CancellationToken).AsTask());
        ex.Outcome.Status.Should().Be(MarangDelegationAcceptanceStatus.Unavailable);
        (await f.Descriptors.GetByAcceptanceKeyAsync(f.AcceptanceKey("k1"), TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Acceptance_evidence_failure_fails_closed()
    {
        await using var f = new Fixture(recordResult: false);
        await Assert.ThrowsAsync<MarangDelegationAcceptanceException>(() =>
            f.Catalog.AcceptAsync(new DelegationCallerScope(Caller), f.Request("k1"), TestContext.Current.CancellationToken).AsTask());
        (await f.Descriptors.GetByAcceptanceKeyAsync(f.AcceptanceKey("k1"), TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task Retry_reevaluates_current_authority_each_use()
    {
        await using var f = new Fixture();
        var request = f.Request("k1");
        var first = await f.Catalog.AcceptAsync(new DelegationCallerScope(Caller), request, TestContext.Current.CancellationToken);
        first.IsNew.Should().BeTrue();

        // A newly introduced current denial must affect the retry (fresh authorization).
        f.SetPolicy(Allow with { AllowedWorkspaces = new HashSet<string>() });
        await Assert.ThrowsAsync<MarangDelegationAcceptanceException>(() =>
            f.Catalog.AcceptAsync(new DelegationCallerScope(Caller), request, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Biscuit_credential_verifier_accepts_a_well_formed_envelope()
    {
        var verifier = new BiscuitDelegationCredentialVerifier();
        (await verifier.VerifyAsync(null, Caller, TestContext.Current.CancellationToken))
            .Should().Be(MarangDelegationCredentialStatus.Missing);
        var credential = new MarangDelegationCredential(MarangDelegationCredentialKind.Biscuit, "key-1", [1, 2, 3]);
        (await verifier.VerifyAsync(credential, Caller, TestContext.Current.CancellationToken))
            .Should().Be(MarangDelegationCredentialStatus.Verified);
    }

    [Fact]
    public void Credential_header_codec_round_trips_and_rejects_malformed()
    {
        var credential = new MarangDelegationCredential(MarangDelegationCredentialKind.Biscuit, "key-1", [9, 8, 7]);
        var decoded = MarangDelegationCredential.TryDecode(credential.Encode());
        decoded.Should().NotBeNull();
        decoded!.Identity.Should().Be("key-1");
        decoded.Bytes.Should().Equal(9, 8, 7);
        MarangDelegationCredential.TryDecode("garbage").Should().BeNull();
        MarangDelegationCredential.TryDecode(null).Should().BeNull();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "marang-acceptance-" + Guid.NewGuid().ToString("N"));
        private readonly DefaultHttpContext _http = new();
        private readonly bool _recordResult;
        private readonly MarangDelegationCredentialStatus _verifierStatus;
        private readonly SqliteDelegationVisibilityStore _visibility;
        private MarangDelegationAcceptancePolicy _policy;
        public List<(DelegationAcceptanceRequest Request, AuthorityDecision Decision)> Records { get; } = [];
        public StubSource Source { get; } = new();
        public SqliteAcceptedDelegationStore Descriptors { get; }
        public MarangDelegationCatalog Catalog { get; private set; }

        public Fixture(
            MarangDelegationAcceptancePolicy? policy = null,
            bool includeCredential = true,
            bool authorityAvailable = true,
            MarangDelegationCredentialStatus verifierStatus = MarangDelegationCredentialStatus.Verified,
            bool recordResult = true)
        {
            Directory.CreateDirectory(_directory);
            var db = Path.Combine(_directory, "authority.db");
            Descriptors = new SqliteAcceptedDelegationStore(db);
            _visibility = new SqliteDelegationVisibilityStore(db);
            _policy = policy ?? Allow;
            _verifierStatus = verifierStatus;
            _recordResult = recordResult;
            Source.Available = authorityAvailable;
            if (includeCredential)
                _http.Items[MarangHttpContextKeys.DelegationCredential] =
                    new MarangDelegationCredential(MarangDelegationCredentialKind.Biscuit, "key-1", [1, 2, 3]);
            Catalog = Build();
        }

        public void SetPolicy(MarangDelegationAcceptancePolicy policy)
        {
            _policy = policy;
            Catalog = Build();
        }

        private MarangDelegationCatalog Build()
        {
            var recorder = new HufuDelegationAcceptanceRecorder((request, decision, ct) =>
            {
                if (!_recordResult) return ValueTask.FromResult(false);
                lock (Records) Records.Add((request, decision));
                return ValueTask.FromResult(true);
            });
            var authorizer = new CurrentDelegationAcceptanceAuthorizer(
                Source, new HufuDelegationAcceptanceEvaluator(_policy), recorder, new FixedClock(Now));
            var gate = new MarangDelegationAcceptanceGate(new FakeVerifier(_verifierStatus), authorizer, Tenant);
            return new MarangDelegationCatalog(gate, Descriptors, _visibility, () => Now, httpContext: new Accessor(_http));
        }

        public string AcceptanceKey(string requestKey) => AcceptanceIdentity.AcceptanceKey(Tenant, Caller, requestKey);

        public DelegationRequest Request(string requestKey) => new(
            requestKey, "Review the changes", Provider, new WorkspaceReference("local", Workspace, "revision"),
            ["Done"], [], new DelegationBudget(8, 2));

        public ValueTask DisposeAsync()
        {
            try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
            return ValueTask.CompletedTask;
        }

        private sealed class FakeVerifier(MarangDelegationCredentialStatus status) : IMarangDelegationCredentialVerifier
        {
            public ValueTask<MarangDelegationCredentialStatus> VerifyAsync(
                MarangDelegationCredential? credential, string caller, CancellationToken cancellationToken = default)
            {
                if (status != MarangDelegationCredentialStatus.Verified) return ValueTask.FromResult(status);
                return ValueTask.FromResult(credential is null
                    ? MarangDelegationCredentialStatus.Missing
                    : MarangDelegationCredentialStatus.Verified);
            }
        }

        private sealed class Accessor(DefaultHttpContext context) : IHttpContextAccessor
        {
            public HttpContext? HttpContext { get; set; } = context;
        }

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }

    private sealed class StubSource : IAuthoritySnapshotSource
    {
        public bool Available { get; set; } = true;

        public ValueTask<AuthoritySnapshot?> GetCurrentAsync(AuthenticatedAuthorityContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Available ? Build(context) : null);

        private static AuthoritySnapshot Build(AuthenticatedAuthorityContext context) =>
            new(context, "v1", [new AuthorityLayer("run", [
                new AuthorityGrant("g", [AuthorityAction.ReadFile], new AuthorityScope(Workspace, "", AuthorityScopeKind.Subtree), [], Now.AddMinutes(-1), Now.AddDays(1))])],
                [], Now.AddDays(1));
    }
}
