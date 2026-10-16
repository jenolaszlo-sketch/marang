using FluentAssertions;
using Marang.Runs.Integrity;

namespace Marang.Tests;

[Collection("RepositoryRecoveryIntegrity")]
public sealed class RepositoryRecoveryAdmissionTests
{
    [Theory]
    [InlineData("DROP TABLE workflow_steps", RepositoryRecoveryStatus.MissingStateShape)]
    [InlineData("DROP TABLE workflow_events", RepositoryRecoveryStatus.MissingStateShape)]
    [InlineData("DELETE FROM zhinu_schema", RepositoryRecoveryStatus.MissingStateShape)]
    [InlineData("UPDATE zhinu_schema SET version=5", RepositoryRecoveryStatus.MissingStateShape)]
    [InlineData("UPDATE workflow_runs SET output_json='{'", RepositoryRecoveryStatus.InconsistentState)]
    [InlineData("UPDATE adaptive_evidence SET document='{'", RepositoryRecoveryStatus.InconsistentState)]
    [InlineData("INSERT INTO adaptive_execution_witness(run_id,witness_key,digest) SELECT id,'fixture','bad' FROM workflow_runs LIMIT 1", RepositoryRecoveryStatus.InconsistentState)]
    [InlineData("INSERT INTO workflow_steps(id,workflow_run_id,step_key,status,attempt,created_at,output_json) SELECT 'f174aa6b-f3f3-49b1-98a5-e811f3c24852',id,'fixture',2,1,'2026-10-16T00:00:00Z','{' FROM workflow_runs LIMIT 1", RepositoryRecoveryStatus.InconsistentState)]
    [InlineData("DELETE FROM adaptive_workflows", RepositoryRecoveryStatus.InconsistentState)]
    public async Task Recovery_cannot_initialize_missing_execution_shape_or_interpret_damaged_execution(string damageSql, RepositoryRecoveryStatus expected)
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        await fixture.MutateAsync(damageSql, ct);
        var before = await File.ReadAllBytesAsync(fixture.DatabasePath, ct);
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, cancellationToken: ct)).Status.Should().Be(expected);
        (await File.ReadAllBytesAsync(fixture.DatabasePath, ct)).Should().Equal(before);
    }

    [Fact]
    public async Task Unavailable_required_authority_store_refuses_and_is_not_initialized()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = await RecoveryIntegrityFixture.CreateAsync();
        string absent = Path.Combine(fixture.Root, "absent-authority.db");
        (await RepositoryRecoveryInspector.InspectAsync(fixture.DatabasePath, fixture.WorkflowId, [absent], ct)).Status.Should().Be(RepositoryRecoveryStatus.MissingStore);
        File.Exists(absent).Should().BeFalse();
    }

    [Fact]
    public async Task Cancellation_is_not_reported_as_corruption()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var inspect = () => RepositoryRecoveryInspector.InspectAsync("unused.db", Guid.NewGuid(), cancellationToken: cancelled.Token);
        await inspect.Should().ThrowAsync<OperationCanceledException>();
        File.Exists("unused.db").Should().BeFalse();
    }
}
