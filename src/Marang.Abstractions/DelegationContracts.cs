namespace Marang;

/// <summary>Identifies a delegation independently of any workflow provider run.</summary>
public readonly record struct DelegationId(Guid Value)
{
    public static DelegationId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

/// <summary>Identifies a host-resolved workspace without granting arbitrary path access.</summary>
public sealed record WorkspaceReference(
    string Provider,
    string Identifier,
    string? Revision = null);

/// <summary>Identifies the durable workflow that executes a delegation.</summary>
public sealed record WorkflowReference(string Provider, string Identifier);

/// <summary>Identifies an independently inspectable, owned result artifact.</summary>
public sealed record DelegationArtifactReference
{
    public DelegationArtifactReference(
        DelegationId delegationId,
        StructuralNodeReference structuralNode,
        NodeGenerationId nodeGeneration,
        string provider,
        string repository,
        string artifactId,
        string kind,
        int schemaVersion,
        string location,
        ArtifactContentIdentity contentIdentity)
    {
        ArtifactContracts.RequireDelegation(delegationId, nameof(delegationId));
        structuralNode.Validate();
        nodeGeneration.Validate();
        Provider = ArtifactContracts.Identity(provider, nameof(provider), 256);
        Repository = ArtifactContracts.Identity(repository, nameof(repository), 1_024);
        ArtifactId = ArtifactContracts.Identity(artifactId, nameof(artifactId), 1_024);
        Kind = ArtifactContracts.Identity(kind, nameof(kind), 256);
        if (schemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        Location = ArtifactContracts.Identity(location, nameof(location), 4_096);
        contentIdentity.Validate();
        DelegationId = delegationId;
        StructuralNode = structuralNode;
        NodeGeneration = nodeGeneration;
        SchemaVersion = schemaVersion;
        ContentIdentity = contentIdentity;
    }

    public DelegationId DelegationId { get; }
    public StructuralNodeReference StructuralNode { get; }
    public NodeGenerationId NodeGeneration { get; }
    public string Provider { get; }
    public string Repository { get; }
    public string ArtifactId { get; }
    public string Kind { get; }
    public int SchemaVersion { get; }
    public string Location { get; }
    public ArtifactContentIdentity ContentIdentity { get; }
}

public enum DelegationStrategy
{
    Implement = 0,
    Investigate = 1,
    Review = 2,
    Fix = 3,
}

public enum DelegationState
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
    BudgetExceeded = 5,
    NeedsSupervisor = 6,
    WaitingForSupervisor = 7,
}

public sealed record DelegationBudget(
    int MaximumWorkerCalls = 8,
    int MaximumRetries = 1,
    TimeSpan? MaximumDuration = null,
    int MaximumParallelWorkers = 2);

/// <summary>
/// A host-authenticated caller namespace. This identity is supplied by the
/// host boundary and is deliberately not part of <see cref="DelegationRequest"/>.
/// </summary>
public sealed record DelegationCallerScope
{
    public DelegationCallerScope(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("A caller scope identifier is required.", nameof(identifier));
        }

        if (identifier.Length > 256)
        {
            throw new ArgumentException("A caller scope identifier cannot exceed 256 characters.", nameof(identifier));
        }

        if (identifier.Normalize(System.Text.NormalizationForm.FormC) != identifier
            || identifier != identifier.Trim()
            || identifier.Contains('\r', StringComparison.Ordinal)
            || identifier.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A caller scope identifier must already be in canonical form.",
                nameof(identifier));
        }

        Identifier = identifier;
    }

    public string Identifier { get; }
}

/// <summary>
/// A delegation request whose collection inputs are snapshotted at construction.
/// </summary>
public sealed record DelegationRequest
{
    public DelegationRequest(
        string requestKey,
        string objective,
        WorkspaceReference workspace,
        IReadOnlyList<string> acceptanceCriteria,
        IReadOnlyList<string> constraints,
        DelegationBudget budget,
        DelegationStrategy strategy = DelegationStrategy.Implement,
        WorkflowPlanRevisionReference? planRevision = null)
    {
        RequestKey = requestKey;
        Objective = objective;
        Workspace = workspace;
        AcceptanceCriteria = Snapshot(acceptanceCriteria, nameof(acceptanceCriteria));
        Constraints = Snapshot(constraints, nameof(constraints));
        Budget = budget;
        Strategy = strategy;
        PlanRevision = planRevision;
    }

    public string RequestKey { get; }
    public string Objective { get; }
    public WorkspaceReference Workspace { get; }
    public IReadOnlyList<string> AcceptanceCriteria { get; }
    public IReadOnlyList<string> Constraints { get; }
    public DelegationBudget Budget { get; }
    public DelegationStrategy Strategy { get; }
    /// <summary>
    /// The optional plan binding used by the v2 fingerprint contract. Existing
    /// v1 requests intentionally remain valid without this field.
    /// </summary>
    public WorkflowPlanRevisionReference? PlanRevision { get; }

    private static IReadOnlyList<string> Snapshot(IReadOnlyList<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > 256)
        {
            throw new ArgumentException("A list cannot contain more than 256 values.", parameterName);
        }

        return Array.AsReadOnly(values.ToArray());
    }
}

public sealed record DelegationHandle(
    DelegationId DelegationId,
    WorkflowReference Workflow,
    DelegationState State);

public sealed record DelegationProgress
{
    public DelegationProgress(
        DelegationId delegationId,
        DelegationState state,
        long revision,
        IReadOnlyList<string> currentSteps,
        IReadOnlyList<string> completedSteps,
        int workerCalls,
        int retries,
        DateTimeOffset updatedAt,
        SupervisorCheckpointDescriptor? checkpoint = null)
    {
        DelegationId = delegationId;
        State = state;
        Revision = revision;
        CurrentSteps = Snapshot(currentSteps, nameof(currentSteps));
        CompletedSteps = Snapshot(completedSteps, nameof(completedSteps));
        WorkerCalls = workerCalls;
        Retries = retries;
        UpdatedAt = updatedAt;
        Checkpoint = checkpoint;
    }

    public DelegationId DelegationId { get; }
    public DelegationState State { get; }
    public long Revision { get; }
    public IReadOnlyList<string> CurrentSteps { get; }
    public IReadOnlyList<string> CompletedSteps { get; }
    public int WorkerCalls { get; }
    public int Retries { get; }
    public DateTimeOffset UpdatedAt { get; }
    public SupervisorCheckpointDescriptor? Checkpoint { get; }

    private static IReadOnlyList<string> Snapshot(IReadOnlyList<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        return Array.AsReadOnly(values.ToArray());
    }
}

public sealed record DelegationEvidence
{
    public DelegationEvidence(
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<string> commands,
        int testsPassed,
        int testsFailed,
        bool? reviewApproved,
        int reviewFindingsResolved)
    {
        ChangedFiles = Snapshot(changedFiles, nameof(changedFiles));
        Commands = Snapshot(commands, nameof(commands));
        TestsPassed = testsPassed;
        TestsFailed = testsFailed;
        ReviewApproved = reviewApproved;
        ReviewFindingsResolved = reviewFindingsResolved;
    }

    public IReadOnlyList<string> ChangedFiles { get; }
    public IReadOnlyList<string> Commands { get; }
    public int TestsPassed { get; }
    public int TestsFailed { get; }
    public bool? ReviewApproved { get; }
    public int ReviewFindingsResolved { get; }

    private static IReadOnlyList<string> Snapshot(IReadOnlyList<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        return Array.AsReadOnly(values.ToArray());
    }
}

public sealed record DelegationResult
{
    public DelegationResult(
        DelegationId delegationId,
        DelegationState state,
        string summary,
        DelegationEvidence evidence,
        IReadOnlyList<DelegationArtifactReference> artifacts,
        IReadOnlyList<string> unresolvedConcerns,
        DateTimeOffset completedAt,
        EvidenceBundle? normalizedEvidence = null,
        BudgetExceededOutcome? budgetExceeded = null)
    {
        DelegationId = delegationId;
        State = state;
        Summary = summary;
        Evidence = evidence;
        Artifacts = Snapshot(artifacts, nameof(artifacts));
        UnresolvedConcerns = Snapshot(unresolvedConcerns, nameof(unresolvedConcerns));
        CompletedAt = completedAt;
        NormalizedEvidence = normalizedEvidence;
        if (budgetExceeded is not null && budgetExceeded.DelegationId != delegationId)
        {
            throw new ArgumentException("A budget-exceeded outcome must belong to the result delegation.", nameof(budgetExceeded));
        }

        BudgetExceeded = budgetExceeded;
        EvidenceContracts.ValidateBundleForDelegation(NormalizedEvidence, delegationId, nameof(normalizedEvidence));
    }

    public DelegationId DelegationId { get; }
    public DelegationState State { get; }
    public string Summary { get; }
    public DelegationEvidence Evidence { get; }
    public IReadOnlyList<DelegationArtifactReference> Artifacts { get; }
    public IReadOnlyList<string> UnresolvedConcerns { get; }
    public DateTimeOffset CompletedAt { get; }
    /// <summary>
    /// Optional bounded normalized evidence. Raw transcripts and provider
    /// payloads remain artifact references rather than result payloads.
    /// </summary>
    public EvidenceBundle? NormalizedEvidence { get; }
    /// <summary>
    /// Durable accounting evidence required when <see cref="State"/> is
    /// <see cref="DelegationState.BudgetExceeded"/>.
    /// </summary>
    public BudgetExceededOutcome? BudgetExceeded { get; }

    private static IReadOnlyList<T> Snapshot<T>(IReadOnlyList<T> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        return Array.AsReadOnly(values.ToArray());
    }
}

public interface IDelegationService
{
    Task<DelegationHandle> DelegateAsync(
        DelegationCallerScope caller,
        DelegationRequest request,
        CancellationToken cancellationToken = default);

    Task<DelegationProgress?> GetStatusAsync(
        DelegationId id,
        CancellationToken cancellationToken = default);

    Task<DelegationResult?> GetResultAsync(
        DelegationId id,
        CancellationToken cancellationToken = default);

    Task CancelAsync(
        DelegationId id,
        CancellationToken cancellationToken = default);
}
