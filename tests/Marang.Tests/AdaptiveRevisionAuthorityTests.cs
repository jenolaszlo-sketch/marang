using FluentAssertions;
using Marang.Hufu;
using Penghou.Hufu;
using Penghou.Hufu.Cedar;
using Penghou.Hufu.Sqlite;

namespace Marang.Tests;

public sealed class AdaptiveRevisionAuthorityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedAuthorityContext Context =
        new("tenant", "planner", "workflow-1", "revision-1", "fence-1");
    private static readonly AuthorityStoreActor StoreActor = new("tenant", "host", "session-1");
    private const string Workspace = "repo";
    private const string Path = "workflow/plan.json";
    private const string ParentStateDigest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ProposalDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Automatic_decision_uses_current_scoped_Hufu_grant_and_exact_proposal_digest()
    {
        using var fixture = await AuthorityFixture.CreateAsync();
        var adapter = fixture.CreateAdapter();

        var result = await adapter.EvaluateAsync(Request(), TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(AdaptiveRevisionAuthorityOutcome.Automatic);
        result.HufuStatus.Should().Be(AuthorityStatus.Permit);
        result.HufuDecision.Should().NotBeNull();
        result.HufuRequestIdentity.Should().Be(ProposalDigest);
        result.HufuDecision!.SnapshotIdentity.Should().Be(fixture.SnapshotIdentity);
    }

    [Fact]
    public async Task Required_approval_is_bound_to_exact_workflow_parent_state_and_proposal()
    {
        using var fixture = await AuthorityFixture.CreateAsync();
        var verifier = new ApprovalVerifier();
        var adapter = fixture.CreateAdapter(verifier);
        var request = Request(requiresApproval: true);

        var pending = await adapter.EvaluateAsync(request, TestContext.Current.CancellationToken);
        pending.Outcome.Should().Be(AdaptiveRevisionAuthorityOutcome.ApprovalRequired);

        verifier.Evidence = new(
            "approval-1", "tenant", "approver", "workflow-1", "revision-1",
            ParentStateDigest, "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            Now.AddMinutes(-1), Now.AddHours(1));
        var mismatched = await adapter.EvaluateAsync(
            Request(requiresApproval: true, receipt: new("approval-1")),
            TestContext.Current.CancellationToken);
        mismatched.Outcome.Should().Be(AdaptiveRevisionAuthorityOutcome.Denied);
        mismatched.ReasonCode.Should().Be("adaptive-revision.approval-invalid");

        verifier.Evidence = verifier.Evidence with { ProposalDigest = ProposalDigest };
        var approved = await adapter.EvaluateAsync(
            Request(requiresApproval: true, receipt: new("approval-1")),
            TestContext.Current.CancellationToken);
        approved.Outcome.Should().Be(AdaptiveRevisionAuthorityOutcome.Automatic);
        approved.ApprovalEvidence!.ActorId.Should().Be("approver");
        approved.ApprovalEvidence.ExpiresAt.Should().Be(Now.AddHours(1));
        verifier.LastRequest!.ProposalDigest.Should().Be(ProposalDigest);
        verifier.LastRequest.ParentStateDigest.Should().Be(ParentStateDigest);
    }

    [Fact]
    public async Task Expired_or_foreign_approval_evidence_is_rejected()
    {
        using var fixture = await AuthorityFixture.CreateAsync();
        var verifier = new ApprovalVerifier();
        var adapter = fixture.CreateAdapter(verifier);
        var request = Request(requiresApproval: true, receipt: new("approval-1"));

        verifier.Evidence = new(
            "approval-1", "tenant", "approver", "workflow-1", "revision-1",
            ParentStateDigest, ProposalDigest, Now.AddMinutes(-2), Now.AddMinutes(-1));
        var expired = await adapter.EvaluateAsync(request, TestContext.Current.CancellationToken);
        expired.Outcome.Should().Be(AdaptiveRevisionAuthorityOutcome.Denied);
        expired.ReasonCode.Should().Be("adaptive-revision.approval-invalid");

        verifier.Evidence = new(
            "approval-1", "tenant", "foreign-actor", "workflow-1", "revision-1",
            ParentStateDigest, ProposalDigest, Now.AddMinutes(-1), Now.AddHours(1));
        var foreignActor = await adapter.EvaluateAsync(request, TestContext.Current.CancellationToken);
        foreignActor.Outcome.Should().Be(AdaptiveRevisionAuthorityOutcome.Denied);
        foreignActor.ReasonCode.Should().Be("adaptive-revision.approval-invalid");
    }

    [Fact]
    public async Task Commit_time_evaluation_denies_after_current_Hufu_grant_is_revoked()
    {
        using var fixture = await AuthorityFixture.CreateAsync();
        var adapter = fixture.CreateAdapter();

        (await adapter.EvaluateAsync(Request(), TestContext.Current.CancellationToken))
            .Outcome.Should().Be(AdaptiveRevisionAuthorityOutcome.Automatic);

        var revoked = await fixture.Store.RevokeAsync(
            new AuthorityRevokeCommand("revoke-1", StoreActor, Context, 1, "test.revoked"),
            TestContext.Current.CancellationToken);
        revoked.Status.Should().Be(AuthorityMutationStatus.Applied);

        var commitCheck = await adapter.EvaluateAsync(Request(), TestContext.Current.CancellationToken);
        commitCheck.Outcome.Should().Be(AdaptiveRevisionAuthorityOutcome.Denied);
        commitCheck.ReasonCode.Should().Be("adaptive-revision.hufu-unavailable");
        commitCheck.HufuStatus.Should().Be(AuthorityStatus.Unavailable);
    }

    [Fact]
    public async Task Invalid_context_and_unknown_capability_fail_closed_before_Hufu()
    {
        var authorizer = new CountingAuthorizer();
        var adapter = new AdaptiveRevisionAuthority(authorizer, clock: new FixedTimeProvider(Now));

        var contextMismatch = await adapter.EvaluateAsync(
            Request() with { AuthorityContext = Context with { RevisionId = "other" } },
            TestContext.Current.CancellationToken);
        contextMismatch.Outcome.Should().Be(AdaptiveRevisionAuthorityOutcome.Denied);
        contextMismatch.ReasonCode.Should().Be("adaptive-revision.binding-invalid");

        var unknownCapability = await adapter.EvaluateAsync(
            Request() with { Capability = "workflow-revision" },
            TestContext.Current.CancellationToken);
        unknownCapability.Outcome.Should().Be(AdaptiveRevisionAuthorityOutcome.Denied);
        unknownCapability.ReasonCode.Should().Be("adaptive-revision.capability-unsupported");
        authorizer.Calls.Should().Be(0);
    }

    private static AdaptiveRevisionAuthorityRequest Request(
        bool requiresApproval = false,
        AdaptiveRevisionApprovalReceipt? receipt = null) => new(
            "workflow-1", "revision-1", ParentStateDigest, ProposalDigest, Context,
            "write-file", Workspace, Path, requiresApproval, receipt);

    private sealed class AuthorityFixture : IDisposable
    {
        private AuthorityFixture(TemporaryDatabase database, SqliteAuthorityStore store, string snapshotIdentity)
        {
            Database = database;
            Store = store;
            SnapshotIdentity = snapshotIdentity;
        }

        private TemporaryDatabase Database { get; }
        public SqliteAuthorityStore Store { get; }
        public string SnapshotIdentity { get; }

        public static async Task<AuthorityFixture> CreateAsync()
        {
            var database = new TemporaryDatabase();
            var time = new FixedTimeProvider(Now);
            var store = new SqliteAuthorityStore(database.DatabasePath, new AllowStorePolicy(), time);
            var snapshot = new AuthoritySnapshot(
                Context,
                "grant-version-1",
                [new AuthorityLayer("current", [new AuthorityGrant(
                    "grant-1", [AuthorityAction.WriteFile],
                    new AuthorityScope(Workspace, "workflow", AuthorityScopeKind.Subtree), [],
                    Now.AddMinutes(-1), Now.AddDays(1))])],
                [],
                Now.AddDays(1));
            var publish = await store.PublishAsync(
                new AuthorityPublishCommand("publish-1", StoreActor, snapshot, 0),
                TestContext.Current.CancellationToken);
            publish.Status.Should().Be(AuthorityMutationStatus.Applied);
            return new(database, store, snapshot.Identity);
        }

        public AdaptiveRevisionAuthority CreateAdapter(IAdaptiveRevisionApprovalVerifier? approvals = null)
        {
            var source = new AuthorityStoreSnapshotSource(Store, StoreActor);
            var current = new CurrentAuthorityRequestAuthorizer(
                source,
                new CedarAuthorityEvaluator(),
                new AllowDecisionRecorder(),
                new FixedTimeProvider(Now));
            return new AdaptiveRevisionAuthority(current, approvals, new FixedTimeProvider(Now));
        }

        public void Dispose() => Database.Dispose();
    }

    private sealed class AllowStorePolicy : IAuthorityStoreAuthorizer
    {
        public ValueTask<AuthorityStoreAuthorization> AuthorizeAsync(
            AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default) =>
            new(new AuthorityStoreAuthorization(AuthorityStatus.Permit, request.Actor));
    }

    private sealed class AllowDecisionRecorder : IAuthorityDecisionRecorder
    {
        public ValueTask<bool> RecordAsync(
            AuthorityRequest request,
            AuthorityDecision decision,
            CancellationToken cancellationToken = default) => new(true);
    }

    private sealed class ApprovalVerifier : IAdaptiveRevisionApprovalVerifier
    {
        public AdaptiveRevisionApprovalEvidence? Evidence { get; set; }
        public AdaptiveRevisionAuthorityRequest? LastRequest { get; private set; }

        public ValueTask<AdaptiveRevisionApprovalEvidence?> ResolveAsync(
            AdaptiveRevisionAuthorityRequest request,
            AdaptiveRevisionApprovalReceipt receipt,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return new(Evidence?.ActorId == "approver" ? Evidence : null);
        }
    }

    private sealed class CountingAuthorizer : IAuthorityRequestAuthorizer
    {
        public int Calls { get; private set; }

        public ValueTask<AuthorityRequestAuthorization> AuthorizeAsync(
            AuthorityRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return new(new AuthorityRequestAuthorization(request, AuthorityStatus.Deny));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "marang-adaptive-hufu-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath => System.IO.Path.Combine(_directory, "authority.db");

        public TemporaryDatabase() => Directory.CreateDirectory(_directory);

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
