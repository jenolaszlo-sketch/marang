using FluentAssertions;
using Marang.Runs.Adaptive;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Tests;

public sealed class AdaptivePlanAuthoringTests
{
    [Fact]
    public async Task Verified_archive_rejects_manifest_dependency_tampering()
    {
        var authored = await Author(Specs());
        var branches = authored.Revision.Branches.ToArray();
        branches[^1] = branches[^1] with { Dependencies = [] };
        var tampered = authored.Revision with { Branches = branches };
        Action verify = () => AdaptivePlanAuthoring.VerifyArchive(tampered);
        verify.Should().Throw<WorkflowStateException>();
    }

    [Fact]
    public async Task Claimed_input_equivalence_must_bind_the_actual_execution_input()
    {
        var authored = await Author(Specs());
        var branches = authored.Revision.Branches.ToArray();
        branches[0] = branches[0] with { Execution = branches[0].Execution with { InputJson = "\"different inputs\"" } };
        Action verify = () => AdaptivePlanAuthoring.VerifyArchive(authored.Revision with { Branches = branches });
        verify.Should().Throw<Exception>();
    }

    [Fact]
    public async Task Requirement_change_preserves_equivalent_branch_and_invalidates_join()
    {
        var specs = Specs();
        var before = await Author(specs);
        specs[1] = specs[1] with { Requirement = "revised report", ExecutionId = Guid.NewGuid() };
        specs[^1] = specs[^1] with { ExecutionId = Guid.NewGuid() };
        var ports = new UnusedPorts();
        var after = await AdaptivePlanAuthoring.AuthorAsync(before.Revision.WorkflowId, "r2", "r1", "goal", "change", "planner",
            specs, new FuwenZhinuExecutionPorts(ports, ports, ports), before.Registry, TestContext.Current.CancellationToken);
        var changes = AdaptiveRevisionContracts.Compare(before.Revision, after.Revision);
        changes.Single(c => c.LogicalId == "inventory").Kind.Should().Be(AdaptiveChangeKind.Preserved);
        changes.Single(c => c.LogicalId == "assessment").Kind.Should().Be(AdaptiveChangeKind.Modified);
        changes.Single(c => c.LogicalId == "outcome").Kind.Should().Be(AdaptiveChangeKind.DependencyChanged);
        before.Revision.Branches[0].Execution.Id.Should().Be(after.Revision.Branches[0].Execution.Id);
    }

    private static Task<AuthoredAdaptiveRevision> Author(AdaptiveWorkSpec[] specs)
    {
        var ports = new UnusedPorts();
        return AdaptivePlanAuthoring.AuthorAsync(Guid.NewGuid(), "r1", null, "goal", "original goal", "planner", specs,
            new FuwenZhinuExecutionPorts(ports, ports, ports), cancellationToken: TestContext.Current.CancellationToken);
    }
    private static AdaptiveWorkSpec[] Specs() =>
    [new("inventory", "Inventory", "collect inventory", "snapshot", "authority", "content-hash", [], Guid.NewGuid(), true),
     new("assessment", "Assessment", "original report", "snapshot", "authority", "content-hash", [], Guid.NewGuid(), true),
     new("outcome", "Outcome", "combine results", "snapshot", "authority", "content-hash", ["inventory", "assessment"], Guid.NewGuid(), true)];
    private sealed class UnusedPorts : IActivityExecutor, IContextProvider, IInferenceExecutor
    {
        public ValueTask<ActivityExecutionResult> ExecuteAsync(ActivityExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ContextExecutionResult> ExecuteAsync(ContextExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<InferenceExecutionResult> ExecuteAsync(InferenceExecutionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
