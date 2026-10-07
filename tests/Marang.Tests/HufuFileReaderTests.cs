using FluentAssertions;
using Marang.Hufu;
using Penghou.Hufu;

namespace Marang.Tests;

/// <summary>
/// Hufu admission slice: one filesystem read tool behind a fresh
/// <see cref="IAuthorityRequestAuthorizer"/> decision per call. No Hufu
/// policy internals are reproduced here; the fake evaluator below stands in
/// for a real evaluator with explicit grant-coverage rules.
/// </summary>
public sealed class HufuFileReaderTests
{
    private static readonly AuthenticatedAuthorityContext Context = new("tenant", "subject", "run", "rev", "fence");
    private const string Workspace = "ws-local";

    [Fact]
    public async Task Allowed_read_reads_authorized_resource_exactly_once()
    {
        var harness = new Harness(PermitScope(""));
        var reader = harness.Reader();

        var result = await reader.ReadFileAsync("src/a.txt", TestContext.Current.CancellationToken);

        var allowed = result.Should().BeOfType<FileReadAllowed>().Subject;
        allowed.Content.Should().Be("hello");
        allowed.WorkspaceId.Should().Be(Workspace);
        allowed.RelativePath.Should().Be("src/a.txt");
        harness.Reads.Should().Be(1);
        harness.Evaluator.Evaluations.Should().Be(1);
    }

    [Fact]
    public async Task Denied_read_never_touches_filesystem()
    {
        var harness = new Harness(PermitScope("src"));
        var reader = harness.Reader();

        var result = await reader.ReadFileAsync("secrets/x.txt", TestContext.Current.CancellationToken);

        var denied = result.Should().BeOfType<FileReadDenied>().Subject;
        denied.ReasonCode.Should().Be("test.layer-denied");
        denied.WorkspaceId.Should().Be(Workspace);
        denied.RelativePath.Should().Be("secrets/x.txt");
        harness.Reads.Should().Be(0);
        harness.Evaluator.Evaluations.Should().Be(1);
    }

    [Fact]
    public async Task Traversal_path_is_rejected_before_admission()
    {
        var harness = new Harness(PermitScope(""));
        var reader = harness.Reader();

        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadFileAsync("../escape.txt", TestContext.Current.CancellationToken).AsTask());

        harness.Reads.Should().Be(0);
        harness.Evaluator.Evaluations.Should().Be(0);
    }

    [Fact]
    public async Task Missing_snapshot_fails_closed_without_touching_filesystem()
    {
        var harness = new Harness(PermitScope(""));
        harness.Current = null;
        var reader = harness.Reader();

        var result = await reader.ReadFileAsync("src/a.txt", TestContext.Current.CancellationToken);

        result.Should().BeOfType<FileReadUnavailable>();
        harness.Reads.Should().Be(0);
    }

    [Fact]
    public async Task Mismatched_snapshot_context_fails_closed()
    {
        var harness = new Harness(PermitScope(""));
        harness.Current = SnapshotFor(
            new AuthenticatedAuthorityContext("tenant", "other", "run", "rev", "fence"),
            [new AuthorityScope(Workspace, "", AuthorityScopeKind.Subtree)]);
        var reader = harness.Reader();

        var result = await reader.ReadFileAsync("src/a.txt", TestContext.Current.CancellationToken);

        result.Should().BeOfType<FileReadUnavailable>();
        harness.Reads.Should().Be(0);
    }

    [Fact]
    public async Task Revocation_takes_effect_on_next_invocation_with_fresh_admission()
    {
        var harness = new Harness(PermitScope(""));
        var reader = harness.Reader();

        (await reader.ReadFileAsync("src/a.txt", TestContext.Current.CancellationToken)).Should().BeOfType<FileReadAllowed>();
        harness.SourceFetches.Should().Be(1);

        harness.Current = null;

        (await reader.ReadFileAsync("src/a.txt", TestContext.Current.CancellationToken)).Should().BeOfType<FileReadUnavailable>();
        harness.SourceFetches.Should().Be(2);
        harness.Reads.Should().Be(1);
    }

    [Fact]
    public async Task Prior_allow_does_not_authorize_later_call()
    {
        var harness = new Harness(PermitScope(""));
        var reader = harness.Reader();

        var first = await reader.ReadFileAsync("src/a.txt", TestContext.Current.CancellationToken);
        first.Should().BeOfType<FileReadAllowed>();

        harness.Current = SnapshotFor(Context, [new AuthorityScope(Workspace, "other", AuthorityScopeKind.Subtree)]);
        var second = await reader.ReadFileAsync("src/a.txt", TestContext.Current.CancellationToken);

        second.Should().BeOfType<FileReadDenied>();
        harness.Evaluator.Evaluations.Should().Be(2);
        harness.Evaluator.Seen.Select(request => request.RequestIdentity).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Output_conveys_no_authority()
    {
        var harness = new Harness(PermitScope(""));
        var reader = harness.Reader();

        var result = await reader.ReadFileAsync("src/a.txt", TestContext.Current.CancellationToken);

        foreach (var type in new[] { typeof(FileReadAllowed), typeof(FileReadDenied), typeof(FileReadUnavailable) })
        {
            type.GetProperties().Select(property => property.Name).Should().NotContain(
                name => name.Contains("Grant", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Credential", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Bearer", StringComparison.OrdinalIgnoreCase));
        }

        result.Should().BeOfType<FileReadAllowed>().Which.Content.Should().Be("hello");
    }

    private static AuthoritySnapshot PermitScope(string relativePath) =>
        SnapshotFor(Context, [new AuthorityScope(Workspace, relativePath, AuthorityScopeKind.Subtree)]);

    private static AuthoritySnapshot SnapshotFor(
        AuthenticatedAuthorityContext context,
        IReadOnlyList<AuthorityScope> grantScopes)
    {
        var now = DateTimeOffset.UtcNow;
        var grants = grantScopes.Select((scope, index) => new AuthorityGrant(
                $"grant-{index}",
                [AuthorityAction.ReadFile],
                scope,
                [],
                now.AddHours(-1),
                now.AddHours(1))).ToArray();
        return new AuthoritySnapshot(
            context,
            "v3",
            [new AuthorityLayer("layer-1", grants)],
            [],
            now.AddHours(1));
    }

    private sealed class Harness(AuthoritySnapshot? initial)
    {
        public AuthoritySnapshot? Current = initial;
        public int SourceFetches;
        public int Reads;
        public readonly GrantEvaluator Evaluator = new();
        private readonly Dictionary<string, string> _files = new()
        {
            ["src/a.txt"] = "hello",
        };

        public HufuFileReader Reader() =>
            new(
                new CurrentAuthorityRequestAuthorizer(
                    new StubSnapshotSource(this),
                    Evaluator,
                    new AllowRecorder()),
                Context,
                Workspace,
                (path, _) =>
                {
                    Reads++;
                    return new ValueTask<string>(_files[path]);
                });

        private sealed class StubSnapshotSource(Harness harness) : IAuthoritySnapshotSource
        {
            public ValueTask<AuthoritySnapshot?> GetCurrentAsync(
                AuthenticatedAuthorityContext context,
                CancellationToken cancellationToken = default)
            {
                harness.SourceFetches++;
                return new(harness.Current);
            }
        }

        private sealed class AllowRecorder : IAuthorityDecisionRecorder
        {
            public ValueTask<bool> RecordAsync(
                AuthorityRequest request,
                AuthorityDecision decision,
                CancellationToken cancellationToken = default) => new(true);
        }
    }

    private sealed class GrantEvaluator : IAuthorityEvaluator
    {
        public int Evaluations;
        public readonly List<AuthorityRequest> Seen = [];

        public AuthorityDecision Evaluate(
            AuthoritySnapshot snapshot,
            AuthorityRequest request,
            DateTimeOffset now)
        {
            Evaluations++;
            Seen.Add(request);
            var scope = new AuthorityScope(request.WorkspaceId, request.RelativePath, AuthorityScopeKind.Exact);
            var permitted = snapshot.Layers
                .SelectMany(layer => layer.Grants)
                .Any(grant => grant.Actions.Contains(AuthorityAction.ReadFile) &&
                    grant.NotBefore <= now && now < grant.ExpiresAt &&
                    AuthorityValidation.Contains(grant.Scope, scope) &&
                    !grant.Exclusions.Any(exclusion => AuthorityValidation.Contains(exclusion, scope)) &&
                    !snapshot.MandatoryDenials.Any(denial => AuthorityValidation.Contains(denial, scope)));
            return new AuthorityDecision(
                permitted ? AuthorityStatus.Permit : AuthorityStatus.Deny,
                permitted ? "test.permitted" : "test.layer-denied",
                snapshot.Version,
                "test-evaluator-v1",
                snapshot.Identity);
        }
    }
}
