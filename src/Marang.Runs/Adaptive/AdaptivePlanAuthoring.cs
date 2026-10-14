using System.Text;
using System.Text.Json;
using Penghou.Fuwen;
using Penghou.Fuwen.Compiler;
using Penghou.Fuwen.Zhinu;
using Penghou.Zhinu;
using Penghou.Zhinu.Sqlite.Experimental;

namespace Marang.Runs.Adaptive;

/// <summary>Bounded M1 authoring profile: independent read/report branches, optionally followed by one join.</summary>
public sealed record AdaptiveWorkSpec(string LogicalId, string Title, string Requirement, string Input,
    string AuthorityDigest, string Provenance, string[] Dependencies, Guid ExecutionId, bool AcceptReuse);

/// <summary>Immutable Fuwen definitions retained with their canonical lineage envelope.</summary>
public sealed record AdaptivePlanArchive(string DefinitionFingerprint, string DefinitionBase64,
    string EnvelopeFingerprint, string EnvelopeBase64, Dictionary<string, string> BranchDefinitions);

public sealed record AuthoredAdaptiveRevision(AdaptiveRevision Revision, WorkflowRegistry Registry);

/// <summary>
/// Deterministic validator between proposed work and executable state. This profile partitions a Fuwen phase
/// into separate admitted Zhinu branch registrations so independent running work can survive revision.
/// Arbitrary control-flow graphs and cross-branch mutable input are outside this profile.
/// </summary>
public static class AdaptivePlanAuthoring
{
    private static readonly PrimitiveType StringType = new(FuwenPrimitiveKind.String);
    private static readonly DescriptorReference Activity = new(DescriptorKind.Activity, "aaaw.repository-review", "1",
        new ContentDigest("sha256", "descriptor/v1", AdaptiveRevisionContracts.Digest("aaaw.repository-review/read-report/v1")));

    public static async Task<AuthoredAdaptiveRevision> AuthorAsync(Guid workflowId, string revisionId, string? parent,
        string goal, string reason, string creator, AdaptiveWorkSpec[] work, FuwenZhinuExecutionPorts ports,
        WorkflowRegistry? registry = null, CancellationToken cancellationToken = default)
    {
        work = JsonSerializer.Deserialize<AdaptiveWorkSpec[]>(JsonSerializer.Serialize(work))!;
        if (work.Length is < 2 or > 16 || work.Select(s => s.LogicalId).Distinct().Count() != work.Length)
            throw new ArgumentException("M1 requires a bounded parallel plan with unique logical steps.");
        var independent = work.Where(s => s.Dependencies.Length == 0).ToArray();
        var joins = work.Where(s => s.Dependencies.Length > 0).ToArray();
        if (independent.Length < 2 || joins.Length > 1 || joins.Any(j =>
            !j.Dependencies.Order(StringComparer.Ordinal).SequenceEqual(independent.Select(s => s.LogicalId).Order(StringComparer.Ordinal))))
            throw new ArgumentException("M1 supports independent branches and a single join of all branches.");
        foreach (var spec in work)
            if (string.IsNullOrWhiteSpace(spec.Requirement) || string.IsNullOrWhiteSpace(spec.Provenance) || spec.ExecutionId == Guid.Empty)
                throw new ArgumentException("Every proposed branch needs requirements, provenance and execution identity.");

        var whole = BuildPlan("aaaw", revisionId, work);
        var admitted = await Admit(whole, cancellationToken);
        var definition = admitted.Compilation.Definition!;
        var semantics = new PlanRevisionSemantics(Digest(goal), Digest(string.Join("\n", work.Select(s => s.Requirement))), Digest("M1/contract-and-restart-probes"));
        var envelope = PlanRevisionDocument.Create(definition, revisionId, parent, semantics);
        var branches = new List<AdaptiveBranch>();
        var archivedDefinitions = new Dictionary<string, string>(StringComparer.Ordinal);
        registry ??= new WorkflowRegistry();
        foreach (var spec in work)
        {
            // Requirement and repository content are bound into execution input, not a mutable runtime plan.
            var input = JsonSerializer.Serialize(new { spec.LogicalId, spec.Requirement, spec.Input, spec.Provenance });
            var branchPlan = BuildPlan(spec.LogicalId, "branch-contract/1", [spec with { Dependencies = [] }]);
            var branchAdmission = await Admit(branchPlan, cancellationToken);
            var receipt = branchAdmission.Receipt!;
            var registration = await new FuwenZhinuWorkflowFactory(new InMemoryWorkflowDefinitionStore(),
                new FuwenZhinuProviderRuntimeIdentity(receipt.CatalogueSnapshotRevision, receipt.ResolvedDescriptorSetFingerprint), ports)
                .CreateAsync("aaaw." + spec.LogicalId, branchAdmission.Compilation.Definition!.ExecutionFingerprint, branchAdmission, cancellationToken);
            if (!registry.TryGet(registration.Name, registration.Version, out var existing)) registration.Register(registry);
            else if (existing!.DefinitionFingerprint != registration.Definition.ExecutionFingerprint)
                throw new WorkflowStateException("Registration identity collision.");
            var now = DateTimeOffset.UtcNow;
            var execution = new WorkflowRun
            {
                Id = spec.ExecutionId,
                WorkflowName = registration.Name,
                WorkflowVersion = registration.Version,
                Status = WorkflowStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
                InputJson = JsonSerializer.Serialize(input),
                InputType = "System.Text.Json.JsonElement, System.Text.Json",
                OutputType = "System.Text.Json.JsonElement, System.Text.Json",
                DefinitionFingerprint = registration.Definition.ExecutionFingerprint,
            };
            branches.Add(new AdaptiveBranch(spec.LogicalId, revisionId + "/" + spec.LogicalId, spec.Title,
                AdaptiveRevisionContracts.Digest(new { Definition = execution.DefinitionFingerprint, spec.Requirement }),
                AdaptiveRevisionContracts.Digest(input), spec.AuthorityDigest, spec.Provenance,
                spec.Dependencies.Order(StringComparer.Ordinal).ToArray(), execution, spec.AcceptReuse));
            archivedDefinitions.Add(spec.LogicalId, Convert.ToBase64String(registration.Definition.CanonicalBytes.Span));
        }
        var archive = new AdaptivePlanArchive(definition.ExecutionFingerprint, Convert.ToBase64String(definition.CanonicalBytes.Span),
            envelope.EnvelopeFingerprint, Convert.ToBase64String(envelope.CanonicalBytes.Span), archivedDefinitions);
        var revision = new AdaptiveRevision(workflowId, revisionId, parent, JsonSerializer.Serialize(archive), reason, creator, branches.ToArray());
        VerifyArchive(revision);
        return new AuthoredAdaptiveRevision(revision, registry);
    }

    /// <summary>Verify persisted canonical Fuwen bytes and exact manifest binding before admission or recovery.</summary>
    public static void VerifyArchive(AdaptiveRevision revision)
    {
        AdaptiveRevisionContracts.Validate(revision);
        if (revision.Branches.Length is < 2 or > 16)
            throw new WorkflowStateException("Candidate exceeds the bounded M1 branch count.");
        var archive = JsonSerializer.Deserialize<AdaptivePlanArchive>(revision.PlanEnvelopeJson)!;
        var definition = WorkflowDefinitionDocument.LoadVerified(archive.DefinitionFingerprint, Convert.FromBase64String(archive.DefinitionBase64));
        var envelope = PlanRevisionDocument.LoadVerified(archive.EnvelopeFingerprint, Convert.FromBase64String(archive.EnvelopeBase64)).ReadEnvelope();
        if (envelope.ExecutionFingerprint != definition.ExecutionFingerprint || envelope.PlanRevisionId != revision.RevisionId ||
            envelope.ParentRevisionId != revision.ParentRevisionId || archive.BranchDefinitions.Count != revision.Branches.Length)
            throw new WorkflowStateException("Fuwen lineage does not bind the proposed revision.");
        var expectedWork = new List<AdaptiveWorkSpec>();
        foreach (var branch in revision.Branches)
        {
            var doc = WorkflowDefinitionDocument.LoadVerified(branch.Execution.DefinitionFingerprint!, Convert.FromBase64String(archive.BranchDefinitions[branch.LogicalId]));
            var plan = doc.ReadPlan();
            if (plan.Name != branch.LogicalId || plan.Nodes.OfType<ActivityNode>().Single().Name != branch.LogicalId)
                throw new WorkflowStateException("Manifest branch differs from its admitted Fuwen definition.");
            if (plan.Nodes.Count != 2 || plan.Nodes.OfType<ActivityNode>().Single().Activity != Activity ||
                plan.Nodes.OfType<ReturnNode>().Count() != 1)
                throw new WorkflowStateException("Candidate exceeds the finite M1 read/report execution profile.");
            var input = JsonSerializer.Deserialize<string>(branch.Execution.InputJson!)!;
            using var facts = JsonDocument.Parse(input);
            if (facts.RootElement.GetProperty("LogicalId").GetString() != branch.LogicalId ||
                facts.RootElement.GetProperty("Provenance").GetString() != branch.Provenance ||
                branch.InputDigest != AdaptiveRevisionContracts.Digest(input) ||
                branch.ContractDigest != AdaptiveRevisionContracts.Digest(new
                {
                    Definition = branch.Execution.DefinitionFingerprint,
                    Requirement = facts.RootElement.GetProperty("Requirement").GetString()
                })) throw new WorkflowStateException("Claimed branch equivalence is not bound to its actual inputs.");
            if (branch.Execution.WorkflowName != "aaaw." + branch.LogicalId ||
                branch.Execution.WorkflowVersion != branch.Execution.DefinitionFingerprint)
                throw new WorkflowStateException("Execution registration differs from its verified branch identity.");
            expectedWork.Add(new AdaptiveWorkSpec(branch.LogicalId, branch.Title,
                facts.RootElement.GetProperty("Requirement").GetString()!, facts.RootElement.GetProperty("Input").GetString()!,
                branch.AuthorityDigest, branch.Provenance, branch.Dependencies, branch.Execution.Id, branch.AcceptReuse));
            if (WorkflowPlanIdentity.ComputeExecutionFingerprint(BuildPlan(branch.LogicalId, "branch-contract/1",
                [expectedWork[^1] with { Dependencies = [] }])) != doc.ExecutionFingerprint)
                throw new WorkflowStateException("Branch definition differs from the finite execution profile.");
        }
        var independent = expectedWork.Where(s => s.Dependencies.Length == 0).ToArray();
        var joins = expectedWork.Where(s => s.Dependencies.Length > 0).ToArray();
        if (independent.Length < 2 || joins.Length > 1 || joins.Any(j =>
            !j.Dependencies.Order(StringComparer.Ordinal).SequenceEqual(independent.Select(s => s.LogicalId).Order(StringComparer.Ordinal))))
            throw new WorkflowStateException("Manifest dependencies exceed the admitted phase profile.");
        var expected = BuildPlan("aaaw", revision.RevisionId, expectedWork.ToArray());
        if (WorkflowPlanIdentity.ComputeExecutionFingerprint(expected) != definition.ExecutionFingerprint)
            throw new WorkflowStateException("Runtime dependencies differ from the verified Fuwen phase plan.");
    }

    public static async Task<WorkflowRegistry> RestoreRegistryAsync(AdaptiveRevision[] revisions, FuwenZhinuExecutionPorts ports,
        CancellationToken cancellationToken = default)
    {
        var registry = new WorkflowRegistry();
        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var revision in revisions)
        {
            VerifyArchive(revision);
            var archive = JsonSerializer.Deserialize<AdaptivePlanArchive>(revision.PlanEnvelopeJson)!;
            foreach (var branch in revision.Branches)
            {
                if (!registered.Add(branch.Execution.WorkflowName + "@" + branch.Execution.WorkflowVersion)) continue;
                var doc = WorkflowDefinitionDocument.LoadVerified(branch.Execution.DefinitionFingerprint!, Convert.FromBase64String(archive.BranchDefinitions[branch.LogicalId]));
                var admitted = await Admit(doc.ReadPlan(), cancellationToken);
                var receipt = admitted.Receipt!;
                var registration = await new FuwenZhinuWorkflowFactory(new InMemoryWorkflowDefinitionStore(),
                    new FuwenZhinuProviderRuntimeIdentity(receipt.CatalogueSnapshotRevision, receipt.ResolvedDescriptorSetFingerprint), ports)
                    .CreateAsync(branch.Execution.WorkflowName, branch.Execution.WorkflowVersion, admitted, cancellationToken);
                registration.Register(registry);
            }
        }
        return registry;
    }

    private static ContentDigest Digest(string text) => new("sha256", "aaaw-m1/v1", AdaptiveRevisionContracts.Digest(text));
    private static async Task<WorkflowAdmissionResult> Admit(WorkflowPlan plan, CancellationToken ct)
    {
        var result = await new WorkflowAdmissionService(new WorkflowCompiler(new InMemoryTrustedCatalogue([
            new TrustedCatalogueDescriptor(Activity, callableContract: new CallableContract(
                new CallableSignature([new CallableParameter("value", StringType)], StringType),
                CallableEffect.Read, CallableIdempotency.Idempotent, CallableRetrySafety.Safe))]),
            capabilityPolicy: new CapabilityGrantPolicy("aaaw-m1/read-report/v1", []))).AdmitAsync(plan, cancellationToken: ct);
        if (!result.Succeeded) throw new WorkflowStateException("Candidate failed deterministic Fuwen admission: " + string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return result;
    }

    private static WorkflowPlan BuildPlan(string name, string revision, AdaptiveWorkSpec[] work)
    {
        var paths = work.ToDictionary(s => s.LogicalId, s => StructuralNodeIdentity.Create(name, s.LogicalId));
        var nodes = work.Select(s => (WorkflowNode)new ActivityNode(s.LogicalId, paths[s.LogicalId], Activity,
            [new ArgumentBinding("value", new InputBinding([]))], StringType)).ToList();
        var returnPath = StructuralNodeIdentity.Create(name, "return_result");
        nodes.Add(new ReturnNode("return_result", returnPath, new NodeOutputBinding(paths[work[^1].LogicalId], [])));
        var phases = new List<WorkflowExecutionPhase>();
        foreach (var group in work.GroupBy(s => s.Dependencies.Length == 0 ? 0 : 1).OrderBy(g => g.Key))
            phases.Add(new WorkflowExecutionPhase(group.Select(s => paths[s.LogicalId]).ToArray()));
        phases.Add(new WorkflowExecutionPhase([returnPath]));
        return new WorkflowPlan(FuwenContracts.IrVersion, "fuwen-language/v1", FuwenContracts.CompilerSemanticVersion,
            FuwenContracts.CanonicalJsonVersion, FuwenContracts.ExecutionFingerprintVersion, name, revision,
            StringType, StringType, "aaaw-m1/routing-v1", [], [Activity], new CapabilityManifest([]), nodes,
            new WorkflowExecutionOrder([new WorkflowExecutionRegion(name, phases)]));
    }
}
