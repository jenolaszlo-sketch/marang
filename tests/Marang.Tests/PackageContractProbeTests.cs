using FluentAssertions;
using Penghou.Fuwen;
using Penghou.Fuwen.Zhinu;

namespace Marang.Tests;

/// <summary>
/// M5.1 consumer-side package checkpoint: proves from Marang, strictly
/// against published NuGet packages, that a real <see cref="WorkflowPlan"/>
/// produces a <see cref="FuwenZhinuStepMap"/>, that plan identity is
/// available, and that opaque step keys classify via
/// <see cref="FuwenZhinuStepMapper.TryMatchStepKey"/> with no consumer-side
/// key parsing. If this probe fails, M5.3 must stop.
/// </summary>
public sealed class PackageContractProbeTests
{
    [Fact]
    public void Published_mapper_contract_is_usable_from_Marang()
    {
        var plan = CreatePlan();
        var map = FuwenZhinuStepMapper.Map(plan);

        map.SchemaVersion.Should().Be(FuwenZhinuStepMapper.CurrentSchemaVersion);
        map.PlanRevision.Should().Be("1");
        map.ExecutionFingerprint.Should().NotBeNullOrWhiteSpace();
        map.Steps.Should().NotBeEmpty();

        // Declared node: pass the opaque structural path straight through.
        // No parsing, no suffix knowledge, no loop awareness in this test.
        var activityPath = StructuralNodeIdentity.Create("echo", "echo");
        FuwenZhinuStepMapper.TryMatchStepKey(map, activityPath, out var activityMatch).Should().BeTrue();
        activityMatch.Descriptor.Kind.Should().Be(FuwenZhinuStepKind.Activity);
        activityMatch.Descriptor.Origin.Should().Be(FuwenZhinuStepOrigin.Declared);
        activityMatch.Descriptor.DeclaredNodePath.Should().Be(activityPath);
        activityMatch.Scope.Should().BeEmpty();

        // Declared-but-unexecuted nodes are represented even with no run.
        map.Steps.Select(descriptor => descriptor.DeclaredNodePath)
            .Should().Contain(activityPath);

        // Unknown keys fail closed rather than guessing.
        FuwenZhinuStepMapper.TryMatchStepKey(map, "no/such/step", out _).Should().BeFalse();
    }

    private static WorkflowPlan CreatePlan()
    {
        var text = new PrimitiveType(FuwenPrimitiveKind.String);
        var activity = new DescriptorReference(
            DescriptorKind.Activity,
            "sample.echo",
            "1",
            new ContentDigest("sha256", "descriptor/v1", new string('a', 64)));
        var activityPath = StructuralNodeIdentity.Create("echo", "echo");
        var returnPath = StructuralNodeIdentity.Create("echo", "return_result");
        return new WorkflowPlan(
            FuwenContracts.IrVersion,
            "fuwen-language/v1",
            FuwenContracts.CompilerSemanticVersion,
            FuwenContracts.CanonicalJsonVersion,
            FuwenContracts.ExecutionFingerprintVersion,
            "echo",
            "1",
            text,
            text,
            "routing/1",
            [],
            [activity],
            new CapabilityManifest([]),
            [
                new ActivityNode("echo", activityPath, activity, [new ArgumentBinding("value", new InputBinding([]))], text),
                new ReturnNode("return_result", returnPath, new NodeOutputBinding(activityPath, [])),
            ],
            new WorkflowExecutionOrder([
                new WorkflowExecutionRegion("echo", [
                    new WorkflowExecutionPhase([activityPath]),
                    new WorkflowExecutionPhase([returnPath]),
                ]),
            ]));
    }
}
