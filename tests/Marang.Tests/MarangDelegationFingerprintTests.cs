using FluentAssertions;

namespace Marang.Tests;

public sealed class MarangDelegationFingerprintTests
{
    private static readonly Guid Id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

    [Fact]
    public void Compute_is_deterministic_lowercase_hex()
    {
        var first = MarangDelegationFingerprint.Compute(
            Id, "alice", "Review the changes", "provider-one", "workspace");
        var repeat = MarangDelegationFingerprint.Compute(
            Id, "alice", "Review the changes", "provider-one", "workspace");

        first.Should().Be(repeat);
        first.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Compute_is_sensitive_to_every_committed_field()
    {
        var baseline = MarangDelegationFingerprint.Compute(
            Id, "alice", "Review the changes", "provider-one", "workspace");

        MarangDelegationFingerprint.Compute(
                Guid.NewGuid(), "alice", "Review the changes", "provider-one", "workspace")
            .Should().NotBe(baseline);
        MarangDelegationFingerprint.Compute(
                Id, "bob", "Review the changes", "provider-one", "workspace")
            .Should().NotBe(baseline);
        MarangDelegationFingerprint.Compute(
                Id, "alice", "Write the changes", "provider-one", "workspace")
            .Should().NotBe(baseline);
        MarangDelegationFingerprint.Compute(
                Id, "alice", "Review the changes", "provider-two", "workspace")
            .Should().NotBe(baseline);
        MarangDelegationFingerprint.Compute(
                Id, "alice", "Review the changes", "provider-one", "other")
            .Should().NotBe(baseline);
    }

    [Fact]
    public void Verify_accepts_matching_content_and_rejects_tampering()
    {
        var fingerprint = MarangDelegationFingerprint.Compute(
            Id, "alice", "Review the changes", "provider-one", "workspace");

        MarangDelegationFingerprint.Verify(
                Id, "alice", "Review the changes", "provider-one", "workspace", fingerprint)
            .Should().BeTrue();
        MarangDelegationFingerprint.Verify(
                Id, "alice", "Review the CHANGESET", "provider-one", "workspace", fingerprint)
            .Should().BeFalse();
        MarangDelegationFingerprint.Verify(
                Id, "alice", "Review the changes", "provider-one", "workspace", new string('0', 64))
            .Should().BeFalse();
        MarangDelegationFingerprint.Verify(
                Id, "alice", "Review the changes", "provider-one", "workspace", "not-hex")
            .Should().BeFalse();
        MarangDelegationFingerprint.Verify(
                Id, "alice", "Review the changes", "provider-one", "workspace", null)
            .Should().BeFalse();
    }
}
