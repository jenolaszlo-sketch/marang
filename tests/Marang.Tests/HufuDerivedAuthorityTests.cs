using FluentAssertions;
using Marang.Hufu;
using Penghou.Hufu;
using Penghou.Hufu.Sqlite;

namespace Marang.Tests;

/// <summary>
/// Cross-package derived-authority proof: the Marang fs.read adapter consumes a
/// Hufu-derived child grant and never inspects lineage. A read inside the
/// child's attenuated scope succeeds, a read outside it is denied, and once the
/// parent authority is revoked the next read fails closed as ancestor-revoked —
/// without the filesystem accessor ever running on a denial.
/// </summary>
public sealed class HufuDerivedAuthorityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedAuthorityContext Supervisor =
        new("tenant", "supervisor", "run-sup", "rev", "fence");
    private static readonly AuthenticatedAuthorityContext Child =
        new("tenant", "delegation", "run-child", "rev", "fence");
    private static readonly AuthorityStoreActor Actor = new("tenant", "host", "session-1");
    private const string Workspace = "workspace";

    [Fact]
    public async Task Marang_read_uses_derived_child_and_fails_closed_when_ancestor_revoked()
    {
        using var db = new TemporaryDatabase();
        var time = new FixedTimeProvider(Now);
        await PublishParentAsync(db.DatabasePath, time);

        var requested = new RequestedAuthority(
            [AuthorityAction.ReadFile],
            new AuthorityScope(Workspace, "src/service", AuthorityScopeKind.Subtree),
            [],
            Now.AddMinutes(-1),
            Now.AddDays(1));
        var approval = new DerivedAuthorityApproval(
            "grant-parent", "delegation-1", "gen-1",
            AuthorityDerivation.RequestedAuthorityHash(requested));
        var store = new SqliteAuthorityStore(
            db.DatabasePath,
            new BoundedAuthorityIssuanceAuthorizer(new TrustSource(Actor, approval), new AllowAllPolicy(), time),
            time);

        var derived = await store.DeriveAsync(
            new AuthorityDerivationCommand(
                Actor, Supervisor, "grant-parent", Child, "delegation-1", "gen-1", requested, "req-1"),
            TestContext.Current.CancellationToken);
        derived.Status.Should().Be(AuthorityStatus.Permit);
        derived.Grant.Should().NotBeNull();
        derived.Grant!.ParentGrantId.Should().Be("grant-parent");

        var reads = 0;
        var reader = new HufuFileReader(
            Admission(store, time),
            Child,
            Workspace,
            (_, _) => { reads++; return new ValueTask<string>("content"); });

        (await reader.ReadFileAsync("src/service/a.cs", TestContext.Current.CancellationToken))
            .Should().BeOfType<FileReadAllowed>();
        reads.Should().Be(1);

        // Attenuation: the child grant is narrower than the parent, so a
        // parent-covered path outside the child's scope is denied.
        (await reader.ReadFileAsync("src/other/b.cs", TestContext.Current.CancellationToken))
            .Should().BeOfType<FileReadDenied>();
        reads.Should().Be(1);

        var revoked = await store.RevokeAsync(
            new AuthorityRevokeCommand("revoke-1", Actor, Supervisor, 1, "test.revoked"),
            TestContext.Current.CancellationToken);
        revoked.Status.Should().Be(AuthorityMutationStatus.Applied);

        var after = await reader.ReadFileAsync("src/service/a.cs", TestContext.Current.CancellationToken);
        after.Should().BeOfType<FileReadDenied>()
            .Which.ReasonCode.Should().Be("authority.ancestor-revoked");
        reads.Should().Be(1);

        // The child remains historically issued; only its effectiveness changed.
        (await store.GetLineageAsync(derived.Grant.Id, TestContext.Current.CancellationToken))
            .Should().NotBeNull();
    }

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
                    "grant-parent",
                    [AuthorityAction.ReadFile],
                    new AuthorityScope(Workspace, "src", AuthorityScopeKind.Subtree),
                    [],
                    Now.AddMinutes(-1),
                    Now.AddDays(1))])],
            [],
            Now.AddDays(1));
        var published = await store.PublishAsync(
            new AuthorityPublishCommand("publish-parent", Actor, snapshot, 0),
            TestContext.Current.CancellationToken);
        published.Status.Should().Be(AuthorityMutationStatus.Applied);
    }

    private sealed class TrustSource(AuthorityStoreActor actor, DerivedAuthorityApproval approval) : IAuthorityIssuanceTrustSource
    {
        public ValueTask<AuthorityIssuancePrincipal?> AuthenticateAsync(
            AuthorityStoreActor presentedActor,
            AuthorityStoreAccessRequest request,
            CancellationToken cancellationToken = default) =>
            new(new AuthorityIssuancePrincipal(actor, ApprovalValidUntil: Now.AddHours(1))
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
            Path.Combine(Path.GetTempPath(), "marang-hufu-derived-" + Guid.NewGuid().ToString("N"));

        public string DatabasePath => Path.Combine(DirectoryPath, "authority.db");

        public TemporaryDatabase() => Directory.CreateDirectory(DirectoryPath);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
