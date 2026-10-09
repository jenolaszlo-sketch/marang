namespace Marang.Runs;

/// <summary>
/// Operator-facing run projection shaped for the existing workflow-graph
/// contract (nodes/edges). Topology comes only from the declared Fuwen plan;
/// execution state is overlaid from Zhinu steps classified through
/// <c>FuwenZhinuStepMapper.TryMatchStepKey</c>. Synthetic execution steps are
/// listed separately and never become topology nodes.
/// </summary>
public sealed record RunProjection(
    string Id,
    string Title,
    string Workspace,
    string Status,
    string CurrentRevisionId,
    string DurableSequence,
    string Elapsed,
    string Summary,
    string Attention,
    IReadOnlyList<RunProjectionNode> Nodes,
    IReadOnlyList<RunProjectionEdge> Edges,
    IReadOnlyList<RunProjectionCheckpoint> Checkpoints,
    IReadOnlyList<RunProjectionExecutionStep> ExecutionSteps,
    string DelegationId,
    string Generation)
{
    /// <summary>Gets an empty journal map. Journals arrive in M5.4; the graph contract requires the field.</summary>
    public IReadOnlyDictionary<string, object> Journal { get; } = new Dictionary<string, object>();
}

/// <summary>One declared topology node with its current execution overlay.</summary>
public sealed record RunProjectionNode(
    string Id,
    string Name,
    string Kind,
    string Status,
    string Membership,
    string Detail,
    int? Attempt,
    string? StepKey);

/// <summary>One declared plan edge (sequence or containment).</summary>
public sealed record RunProjectionEdge(string Id, string Source, string Target);

/// <summary>One run-scoped waiting checkpoint.</summary>
public sealed record RunProjectionCheckpoint(string Id, string Reason);

/// <summary>One synthetic or fan-out execution step, kept out of the declared topology.</summary>
public sealed record RunProjectionExecutionStep(
    string StepKey,
    string Kind,
    string Status,
    string? DeclaredNodePath);
