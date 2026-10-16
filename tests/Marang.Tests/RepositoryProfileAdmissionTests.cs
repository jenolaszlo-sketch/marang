using FluentAssertions;
using Marang.Runs.Integrity;

namespace Marang.Tests;

[Collection("RepositoryRecoveryIntegrity")]
public sealed class RepositoryProfileAdmissionTests
{
    [Theory]
    [InlineData("marker-only")]
    [InlineData("column-only")]
    [InlineData("empty-marker")]
    [InlineData("future-profile")]
    [InlineData("unknown-contract")]
    [InlineData("unknown-owner")]
    [InlineData("future-record")]
    [InlineData("duplicate-marker")]
    public async Task Unsupported_or_partial_profile_cannot_be_admitted_as_legacy(string damage)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        var column = damage == "marker-only" ? "" :
            "ALTER TABLE repository_effects ADD COLUMN record_format INTEGER NOT NULL DEFAULT 1;";
        var marker = damage == "column-only" ? "" : """
            CREATE TABLE marang_repository_profile (
              id INTEGER PRIMARY KEY, owner TEXT NOT NULL, format_version INTEGER NOT NULL,
              contract_digest TEXT NOT NULL);
            """;
        await fixture.MutateAsync(column + marker, ct);
        if (damage is not ("column-only" or "empty-marker"))
        {
            const string identity = "marang/bounded-repository/m6.1@5dce00ef8d7ca962e3de42f4a28b1e0d1e52fb29;zhinu-schema=6;effect-json=web-v1;profile=1;effect-record=1";
            var digest = Marang.Runs.Effects.RepositoryStateSnapshot.Hash(identity);
            await fixture.MutateAsync($"INSERT INTO marang_repository_profile VALUES(1,'marang.bounded-repository',1,'{digest}')", ct);
        }
        var mutation = damage switch
        {
            "future-profile" => "UPDATE marang_repository_profile SET format_version=2",
            "unknown-contract" => "UPDATE marang_repository_profile SET contract_digest='unknown'",
            "unknown-owner" => "UPDATE marang_repository_profile SET owner='unknown'",
            "future-record" => "UPDATE repository_effects SET record_format=2",
            "duplicate-marker" => "INSERT INTO marang_repository_profile SELECT 2,owner,format_version,contract_digest FROM marang_repository_profile",
            _ => null
        };
        if (mutation is not null) await fixture.MutateAsync(mutation, ct);
        var before = await File.ReadAllBytesAsync(fixture.DatabasePath, ct);
        var first = await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);
        first.CanRecover.Should().BeFalse("unknown or incomplete format evidence cannot select the legacy decoder");
        var second = await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct);
        second.Should().Be(first);
        (await File.ReadAllBytesAsync(fixture.DatabasePath, ct)).Should().Equal(before);
    }
}
