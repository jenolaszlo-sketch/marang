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

/// <summary>Identifies an independently inspectable result artifact.</summary>
public sealed record DelegationArtifactReference(
    string Kind,
    string Identifier,
    int SchemaVersion,
    string? ContentHash = null);

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
}

public sealed record DelegationBudget(
    int MaximumWorkerCalls = 8,
    int MaximumRetries = 1,
    TimeSpan? MaximumDuration = null,
    int MaximumParallelWorkers = 2);

public sealed record DelegationRequest(
    string RequestKey,
    string Objective,
    WorkspaceReference Workspace,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> Constraints,
    DelegationBudget Budget,
    DelegationStrategy Strategy = DelegationStrategy.Implement);

public sealed record DelegationHandle(
    DelegationId DelegationId,
    WorkflowReference Workflow,
    DelegationState State);

public sealed record DelegationProgress(
    DelegationId DelegationId,
    DelegationState State,
    long Revision,
    IReadOnlyList<string> CurrentSteps,
    IReadOnlyList<string> CompletedSteps,
    int WorkerCalls,
    int Retries,
    DateTimeOffset UpdatedAt);

public sealed record DelegationEvidence(
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> Commands,
    int TestsPassed,
    int TestsFailed,
    bool? ReviewApproved,
    int ReviewFindingsResolved);

public sealed record DelegationResult(
    DelegationId DelegationId,
    DelegationState State,
    string Summary,
    DelegationEvidence Evidence,
    IReadOnlyList<DelegationArtifactReference> Artifacts,
    IReadOnlyList<string> UnresolvedConcerns,
    DateTimeOffset CompletedAt);

public interface IDelegationService
{
    Task<DelegationHandle> DelegateAsync(
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
