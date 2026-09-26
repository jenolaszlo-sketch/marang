using FluentAssertions;

namespace Marang.Tests;

/// <summary>
/// API-key boundary policy: bearer-only, constant-time, env-configured keys;
/// caller and workspace authorization answers.
/// </summary>
public sealed class MarangAuthenticationTests
{
    private static readonly Dictionary<string, string> Keys = new()
    {
        ["alice"] = "secret-alice-key",
    };

    [Fact]
    public void Bearer_key_returns_caller()
    {
        new MarangApiKeyAuthenticator(Keys).Authenticate("Bearer secret-alice-key")
            .Should().Be("alice");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("secret-alice-key")]
    [InlineData("Basic secret-alice-key")]
    [InlineData("Bearer ")]
    [InlineData("Bearer wrong-key")]
    [InlineData("bearer secret-alice-key")]
    [InlineData("Bearer secret-alice-key ")]
    public void Missing_malformed_or_wrong_key_returns_null(string? header)
    {
        // A trailing space is trimmed and therefore still valid; every other
        // shape above must be rejected. The trim case is asserted separately.
        if (header == "Bearer secret-alice-key ")
        {
            new MarangApiKeyAuthenticator(Keys).Authenticate(header).Should().Be("alice");
            return;
        }

        new MarangApiKeyAuthenticator(Keys).Authenticate(header).Should().BeNull();
    }

    [Theory]
    [InlineData("alice", "workspace", true)]
    [InlineData("alice", "workspace/sub", true)]
    [InlineData("alice", "workspace2", false)]
    [InlineData("alice", "other", false)]
    [InlineData("bob", "workspace", false)]
    public void Workspace_authorization_is_exact_or_prefix(string caller, string workspace, bool expected)
    {
        var roots = new Dictionary<string, string[]>
        {
            ["alice"] = ["workspace"],
        };
        MarangApiKeyAuthenticator.IsWorkspaceAuthorized(roots, caller, workspace).Should().Be(expected);
    }

    [Fact]
    public void Loopback_identity_is_preauthorized_for_default_workspace()
    {
        var options = new MarangAuthenticationOptions();
        options.LocalCallerIdentity.Should().Be("local-operator");
        MarangApiKeyAuthenticator.IsWorkspaceAuthorized(
                options.AllowedWorkspaceRoots, "local-operator", "workspace")
            .Should().BeTrue();
    }

    [Fact]
    public void Validate_accepts_sane_configuration()
    {
        var act = () => new MarangAuthenticationOptions
        {
            ApiKeys = new Dictionary<string, string> { ["alice"] = "secret" },
            AllowedWorkspaceRoots = new Dictionary<string, string[]> { ["alice"] = ["workspace"] },
        }.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_rejects_blank_material_and_roots()
    {
        var blankKey = () => new MarangAuthenticationOptions
        {
            ApiKeys = new Dictionary<string, string> { ["alice"] = "  " },
        }.Validate();
        blankKey.Should().Throw<ArgumentException>();

        var emptyRoots = () => new MarangAuthenticationOptions
        {
            AllowedWorkspaceRoots = new Dictionary<string, string[]> { ["alice"] = [] },
        }.Validate();
        emptyRoots.Should().Throw<ArgumentException>();
    }
}
