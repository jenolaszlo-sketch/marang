namespace Marang;

/// <summary>Stable identity of the external agent selected for one operation.</summary>
public sealed record ExternalAgentReference
{
    public ExternalAgentReference(string provider, string identifier, string protocolVersion)
    {
        Provider = IdentityText.Require(provider, nameof(provider), 128);
        Identifier = IdentityText.Require(identifier, nameof(identifier), 512);
        ProtocolVersion = ArtifactContracts.Version(protocolVersion, nameof(protocolVersion));
    }

    public string Provider { get; }
    public string Identifier { get; }
    public string ProtocolVersion { get; }

    public void Validate()
    {
        IdentityText.Require(Provider, nameof(Provider), 128);
        IdentityText.Require(Identifier, nameof(Identifier), 512);
        ArtifactContracts.Version(ProtocolVersion, nameof(ProtocolVersion));
    }
}

/// <summary>Provider-issued stable task identity. This is not a transient connection id.</summary>
public sealed record ExternalTaskReference
{
    public ExternalTaskReference(string provider, string identifier)
    {
        Provider = IdentityText.Require(provider, nameof(provider), 128);
        Identifier = IdentityText.Require(identifier, nameof(identifier), 2_048);
    }

    public string Provider { get; }
    public string Identifier { get; }

    public void Validate()
    {
        IdentityText.Require(Provider, nameof(Provider), 128);
        IdentityText.Require(Identifier, nameof(Identifier), 2_048);
    }
}

/// <summary>
/// Correlates a provider operation with the exact Marang execution identity.
/// The attempt id remains stable across reconnect and retry of observations;
/// semantic re-execution must create a new node generation and attempt.
/// </summary>
public sealed record ExternalOperationCorrelation
{
    public ExternalOperationCorrelation(
        DelegationId delegationId,
        WorkflowRunExecutionReference workflowRun,
        StructuralNodeReference structuralNode,
        NodeGenerationId nodeGeneration,
        string executionAttemptId,
        ExternalAgentReference agent,
        ExternalTaskReference? task = null)
    {
        ArtifactContracts.RequireDelegation(delegationId, nameof(delegationId));
        ArgumentNullException.ThrowIfNull(workflowRun);
        workflowRun.Validate();
        structuralNode.Validate();
        nodeGeneration.Validate();
        ExecutionAttemptId = IdentityText.Require(executionAttemptId, nameof(executionAttemptId), 512);
        ArgumentNullException.ThrowIfNull(agent);
        agent.Validate();
        task?.Validate();
        if (task is not null && !string.Equals(task.Provider, agent.Provider, StringComparison.Ordinal))
        {
            throw new ArgumentException("An external task must belong to the correlated external agent provider.", nameof(task));
        }

        DelegationId = delegationId;
        WorkflowRun = workflowRun;
        StructuralNode = structuralNode;
        NodeGeneration = nodeGeneration;
        Agent = agent;
        Task = task;
    }

    public DelegationId DelegationId { get; }
    public WorkflowRunExecutionReference WorkflowRun { get; }
    public StructuralNodeReference StructuralNode { get; }
    public NodeGenerationId NodeGeneration { get; }
    public string ExecutionAttemptId { get; }
    public ExternalAgentReference Agent { get; }
    public ExternalTaskReference? Task { get; }

    public void Validate()
    {
        ArtifactContracts.RequireDelegation(DelegationId, nameof(DelegationId));
        WorkflowRun.Validate();
        StructuralNode.Validate();
        NodeGeneration.Validate();
        IdentityText.Require(ExecutionAttemptId, nameof(ExecutionAttemptId), 512);
        Agent.Validate();
        Task?.Validate();
        if (Task is not null && !string.Equals(Task.Provider, Agent.Provider, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An external task must belong to the correlated external agent provider.");
        }
    }

    public void EnsureTaskCaptured()
    {
        if (Task is null)
        {
            throw new InvalidOperationException(
                "A durable external operation requires a provider-issued task identity before reconnecting.");
        }
    }
}

/// <summary>
/// Idempotency identity for a start request. The semantic fingerprint covers
/// all start inputs but deliberately excludes the caller's transport details.
/// </summary>
public sealed record ExternalOperationStartIdentity
{
    public ExternalOperationStartIdentity(
        DelegationId delegationId,
        WorkflowRunExecutionReference workflowRun,
        StructuralNodeReference structuralNode,
        NodeGenerationId nodeGeneration,
        string executionAttemptId,
        string idempotencyKey,
        string semanticFingerprint)
    {
        ArtifactContracts.RequireDelegation(delegationId, nameof(delegationId));
        ArgumentNullException.ThrowIfNull(workflowRun);
        workflowRun.Validate();
        structuralNode.Validate();
        nodeGeneration.Validate();
        ExecutionAttemptId = IdentityText.Require(executionAttemptId, nameof(executionAttemptId), 512);
        IdempotencyKey = IdentityText.Require(idempotencyKey, nameof(idempotencyKey), 512);
        SemanticFingerprint = IdentityText.RequireSha256(semanticFingerprint, nameof(semanticFingerprint));
        DelegationId = delegationId;
        WorkflowRun = workflowRun;
        StructuralNode = structuralNode;
        NodeGeneration = nodeGeneration;
    }

    public DelegationId DelegationId { get; }
    public WorkflowRunExecutionReference WorkflowRun { get; }
    public StructuralNodeReference StructuralNode { get; }
    public NodeGenerationId NodeGeneration { get; }
    public string ExecutionAttemptId { get; }
    public string IdempotencyKey { get; }
    public string SemanticFingerprint { get; }

    public void Validate()
    {
        ArtifactContracts.RequireDelegation(DelegationId, nameof(DelegationId));
        WorkflowRun.Validate();
        StructuralNode.Validate();
        NodeGeneration.Validate();
        IdentityText.Require(ExecutionAttemptId, nameof(ExecutionAttemptId), 512);
        IdentityText.Require(IdempotencyKey, nameof(IdempotencyKey), 512);
        IdentityText.RequireSha256(SemanticFingerprint, nameof(SemanticFingerprint));
    }
}

/// <summary>Bounded start hints. Providers may ignore unsupported hints.</summary>
public sealed record ExternalOperationBudgetHint
{
    public ExternalOperationBudgetHint(int? maximumTokens = null, TimeSpan? maximumDuration = null)
    {
        if (maximumTokens is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTokens));
        }

        if (maximumDuration is not null && maximumDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        }

        MaximumTokens = maximumTokens;
        MaximumDuration = maximumDuration;
    }

    public int? MaximumTokens { get; }
    public TimeSpan? MaximumDuration { get; }
}

/// <summary>
/// Provider-neutral request. Inputs are immutable artifact references; raw
/// prompts, transcripts, credentials, and ambient paths are not part of this
/// contract.
/// </summary>
public sealed record ExternalOperationStartRequest
{
    public ExternalOperationStartRequest(
        ExternalOperationStartIdentity identity,
        ExternalOperationCorrelation correlation,
        string capability,
        IReadOnlyList<DelegationArtifactReference> inputArtifacts,
        ExternalOperationBudgetHint? budget = null,
        DateTimeOffset? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(correlation);
        identity.Validate();
        correlation.Validate();
        ExternalOperationContracts.RequireMatchingIdentity(identity, correlation);
        Capability = ArtifactContracts.Version(capability, nameof(capability));
        ArgumentNullException.ThrowIfNull(inputArtifacts);
        if (inputArtifacts.Count > ArtifactContracts.MaximumCollectionItems)
        {
            throw new ArgumentException(
                $"An external operation cannot contain more than {ArtifactContracts.MaximumCollectionItems} input artifacts.",
                nameof(inputArtifacts));
        }

        var snapshot = inputArtifacts.ToArray();
        var identities = new HashSet<(string Provider, string Repository, string ArtifactId)>();
        foreach (var artifact in snapshot)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            ArtifactContracts.ValidateArtifact(artifact, identity.DelegationId);
            if (!identities.Add((artifact.Provider, artifact.Repository, artifact.ArtifactId)))
            {
                throw new ArgumentException("Input artifacts cannot contain duplicate identities.", nameof(inputArtifacts));
            }
        }

        Identity = identity;
        Correlation = correlation;
        InputArtifacts = Array.AsReadOnly(snapshot);
        Budget = budget;
        Deadline = deadline;
    }

    public ExternalOperationStartIdentity Identity { get; }
    public ExternalOperationCorrelation Correlation { get; }
    public string Capability { get; }
    public IReadOnlyList<DelegationArtifactReference> InputArtifacts { get; }
    public ExternalOperationBudgetHint? Budget { get; }
    public DateTimeOffset? Deadline { get; }
}

/// <summary>Provider-issued reconnectable operation handle.</summary>
public sealed record ExternalOperationHandle
{
    public ExternalOperationHandle(
        string provider,
        string value,
        string protocolVersion,
        ExternalOperationCorrelation correlation)
    {
        Provider = IdentityText.Require(provider, nameof(provider), 128);
        Value = IdentityText.Require(value, nameof(value), 4_096);
        ProtocolVersion = ArtifactContracts.Version(protocolVersion, nameof(protocolVersion));
        ArgumentNullException.ThrowIfNull(correlation);
        correlation.Validate();
        correlation.EnsureTaskCaptured();
        if (!string.Equals(Provider, correlation.Agent.Provider, StringComparison.Ordinal))
        {
            throw new ArgumentException("An external handle provider must match the correlated agent provider.", nameof(provider));
        }

        if (!string.Equals(Provider, correlation.Task!.Provider, StringComparison.Ordinal))
        {
            throw new ArgumentException("An external handle provider must match the captured task provider.", nameof(provider));
        }

        if (!string.Equals(ProtocolVersion, correlation.Agent.ProtocolVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("An external handle protocol version must match the correlated agent protocol.", nameof(protocolVersion));
        }
        Correlation = correlation;
    }

    public string Provider { get; }
    public string Value { get; }
    public string ProtocolVersion { get; }
    public ExternalOperationCorrelation Correlation { get; }

    public void Validate()
    {
        IdentityText.Require(Provider, nameof(Provider), 128);
        IdentityText.Require(Value, nameof(Value), 4_096);
        ArtifactContracts.Version(ProtocolVersion, nameof(ProtocolVersion));
        Correlation.Validate();
        Correlation.EnsureTaskCaptured();
        if (!string.Equals(Provider, Correlation.Agent.Provider, StringComparison.Ordinal)
            || !string.Equals(Provider, Correlation.Task!.Provider, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An external handle provider must match its agent and task providers.");
        }

        if (!string.Equals(ProtocolVersion, Correlation.Agent.ProtocolVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("An external handle protocol version must match its agent protocol.");
        }
    }

    public ProviderExecutionAttemptReference ToProviderAttemptReference() =>
        new(Provider, Correlation.ExecutionAttemptId, Value);
}

/// <summary>Durable receipt of the earliest provider handle capture.</summary>
public sealed record ExternalOperationHandleCapture
{
    public ExternalOperationHandleCapture(ExternalOperationHandle handle, DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        if (capturedAt == default)
        {
            throw new ArgumentException("A handle capture must have a timestamp.", nameof(capturedAt));
        }

        Handle = handle;
        CapturedAt = capturedAt;
    }

    public ExternalOperationHandle Handle { get; }
    public DateTimeOffset CapturedAt { get; }
}

/// <summary>
/// Durable sink invoked as soon as a provider reveals a handle. Implementations
/// must persist it atomically and treat exact replays as idempotent.
/// </summary>
public interface IExternalOperationHandleCaptureSink
{
    ValueTask CaptureAsync(ExternalOperationHandleCapture capture, CancellationToken cancellationToken = default);
}

public enum ExternalOperationStartDisposition
{
    Created = 0,
    Existing = 1,
}

public sealed record ExternalOperationStartReceipt
{
    public ExternalOperationStartReceipt(
        ExternalOperationStartIdentity identity,
        ExternalOperationHandle handle,
        ExternalOperationStartDisposition disposition,
        ExternalOperationState state,
        DateTimeOffset acceptedAt)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(handle);
        identity.Validate();
        handle.Validate();
        ExternalOperationContracts.RequireMatchingIdentity(identity, handle.Correlation);
        ExternalOperationContracts.RequireKnownState(state);
        if (acceptedAt == default)
        {
            throw new ArgumentException("A start receipt must have an acceptance timestamp.", nameof(acceptedAt));
        }

        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        Identity = identity;
        Handle = handle;
        Disposition = disposition;
        State = state;
        AcceptedAt = acceptedAt;
    }

    public ExternalOperationStartIdentity Identity { get; }
    public ExternalOperationHandle Handle { get; }
    public ExternalOperationStartDisposition Disposition { get; }
    public ExternalOperationState State { get; }
    public DateTimeOffset AcceptedAt { get; }
}

public enum ExternalOperationState
{
    Accepted = 0,
    Running = 1,
    Waiting = 2,
    Succeeded = 3,
    Failed = 4,
    CancellationRequested = 5,
    Cancelled = 6,
    TimedOut = 7,
    Rejected = 8,
    /// <summary>The provider response is ambiguous; the operation may still be running.</summary>
    Unknown = 9,
}

public enum ExternalOperationFailureKind
{
    Transport = 0,
    Remote = 1,
    Cancellation = 2,
    Timeout = 3,
    Rejection = 4,
    ResultValidation = 5,
}

/// <summary>Stable, policy-readable classification of an external failure.</summary>
public sealed record ExternalOperationFailure
{
    public ExternalOperationFailure(
        ExternalOperationFailureKind kind,
        string code,
        string summary,
        bool retryable,
        string? providerCode = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        Code = ArtifactContracts.Version(code, nameof(code));
        Summary = IdentityText.RequireProse(summary, nameof(summary), 8_192);
        ProviderCode = providerCode is null ? null : IdentityText.Require(providerCode, nameof(providerCode), 512);
        Retryable = retryable;
    }

    public ExternalOperationFailureKind Kind { get; }
    public string Code { get; }
    public string Summary { get; }
    public bool Retryable { get; }
    public string? ProviderCode { get; }
}

public sealed record ExternalOperationObservation
{
    public ExternalOperationObservation(
        ExternalOperationHandle handle,
        long revision,
        ExternalOperationState state,
        DateTimeOffset observedAt,
        string? providerStatus = null,
        ExternalOperationFailure? failure = null,
        bool resultAvailable = false)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ExternalOperationContracts.RequireKnownState(state);
        if (observedAt == default)
        {
            throw new ArgumentException("An observation must have a timestamp.", nameof(observedAt));
        }

        if (providerStatus is not null)
        {
            IdentityText.Require(providerStatus, nameof(providerStatus), 512);
        }

        if (state == ExternalOperationState.Succeeded && failure is not null)
        {
            throw new ArgumentException("A successful observation cannot contain a failure.", nameof(failure));
        }

        if (failure is null && state is (ExternalOperationState.Failed
            or ExternalOperationState.TimedOut
            or ExternalOperationState.Rejected))
        {
            throw new ArgumentException("Failed, timed-out, and rejected observations require failure evidence.", nameof(failure));
        }

        if (failure is not null)
        {
            ExternalOperationContracts.RequireFailureMatchesState(state, failure);
        }

        if (resultAvailable && state is not (ExternalOperationState.Succeeded
            or ExternalOperationState.Failed
            or ExternalOperationState.Cancelled
            or ExternalOperationState.TimedOut
            or ExternalOperationState.Rejected))
        {
            throw new ArgumentException("A result cannot be available for a non-terminal observation.", nameof(resultAvailable));
        }

        Handle = handle;
        Revision = revision;
        State = state;
        ObservedAt = observedAt;
        ProviderStatus = providerStatus;
        Failure = failure;
        ResultAvailable = resultAvailable;
    }

    public ExternalOperationHandle Handle { get; }
    public long Revision { get; }
    public ExternalOperationState State { get; }
    public DateTimeOffset ObservedAt { get; }
    public string? ProviderStatus { get; }
    public ExternalOperationFailure? Failure { get; }
    public bool ResultAvailable { get; }
}

public static class ExternalOperationObservationRules
{
    public static void ValidateProgression(ExternalOperationObservation previous, ExternalOperationObservation current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        previous.Handle.Validate();
        current.Handle.Validate();
        if (previous.Handle != current.Handle)
        {
            throw new InvalidOperationException("Observations must belong to the same external handle.");
        }

        if (current.Revision < previous.Revision)
        {
            throw new InvalidOperationException("An observation revision cannot move backwards.");
        }

        if (current.ObservedAt < previous.ObservedAt)
        {
            throw new InvalidOperationException("An observation timestamp cannot move backwards.");
        }

        if (ExternalOperationContracts.IsTerminal(previous.State)
            && (current.State != previous.State || current.Revision != previous.Revision))
        {
            throw new InvalidOperationException("A terminal external operation observation cannot be changed.");
        }

        if (current.Revision == previous.Revision && !Equals(previous, current))
        {
            throw new InvalidOperationException("A changed external observation requires a greater revision.");
        }

        if (current.Revision > previous.Revision
            && !ExternalOperationContracts.CanTransition(previous.State, current.State))
        {
            throw new InvalidOperationException(
                $"An external operation cannot transition from '{previous.State}' to '{current.State}'.");
        }
    }
}

public sealed record ExternalOperationResult
{
    public ExternalOperationResult(
        ExternalOperationHandle handle,
        ExternalOperationState state,
        DateTimeOffset completedAt,
        string summary,
        IReadOnlyList<DelegationArtifactReference> artifacts,
        ExternalOperationProvenanceSnapshot? provenance = null,
        ExternalOperationFailure? failure = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        if (!ExternalOperationContracts.IsTerminal(state))
        {
            throw new ArgumentException("An external result must use a terminal state.", nameof(state));
        }

        if (completedAt == default)
        {
            throw new ArgumentException("An external result must have a completion timestamp.", nameof(completedAt));
        }

        Summary = IdentityText.RequireProse(summary, nameof(summary), 8_192);
        ArgumentNullException.ThrowIfNull(artifacts);
        if (artifacts.Count > ArtifactContracts.MaximumCollectionItems)
        {
            throw new ArgumentException("An external result contains too many artifacts.", nameof(artifacts));
        }

        var snapshot = artifacts.ToArray();
        var identities = new HashSet<(string Provider, string Repository, string ArtifactId)>();
        foreach (var artifact in snapshot)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            ArtifactContracts.ValidateArtifact(artifact, handle.Correlation.DelegationId);
            if (!identities.Add((artifact.Provider, artifact.Repository, artifact.ArtifactId)))
            {
                throw new ArgumentException("An external result cannot contain duplicate artifacts.", nameof(artifacts));
            }
        }

        if (failure is null && state is (ExternalOperationState.Failed
            or ExternalOperationState.TimedOut
            or ExternalOperationState.Rejected))
        {
            throw new ArgumentException("Failed, timed-out, and rejected results require failure evidence.", nameof(failure));
        }

        if (failure is not null)
        {
            ExternalOperationContracts.RequireFailureMatchesState(state, failure);
        }

        if (state == ExternalOperationState.Succeeded && failure is not null)
        {
            throw new ArgumentException("A successful external result cannot contain a failure.", nameof(failure));
        }

        Handle = handle;
        State = state;
        CompletedAt = completedAt;
        Artifacts = Array.AsReadOnly(snapshot);
        Provenance = provenance;
        Failure = failure;
    }

    public ExternalOperationHandle Handle { get; }
    public ExternalOperationState State { get; }
    public DateTimeOffset CompletedAt { get; }
    public string Summary { get; }
    public IReadOnlyList<DelegationArtifactReference> Artifacts { get; }
    public ExternalOperationProvenanceSnapshot? Provenance { get; }
    public ExternalOperationFailure? Failure { get; }
}

public enum ExternalOperationCancellationDisposition
{
    Requested = 0,
    ConfirmedCancelled = 1,
    AlreadyTerminal = 2,
    Rejected = 3,
    Unknown = 4,
}

public sealed record ExternalOperationCancelRequest
{
    public ExternalOperationCancelRequest(ExternalOperationHandle handle, string cancellationKey, string reason)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        Handle = handle;
        CancellationKey = IdentityText.Require(cancellationKey, nameof(cancellationKey), 512);
        Reason = IdentityText.RequireProse(reason, nameof(reason), 4_096);
    }

    public ExternalOperationHandle Handle { get; }
    public string CancellationKey { get; }
    public string Reason { get; }
}

public sealed record ExternalOperationCancellationReceipt
{
    public ExternalOperationCancellationReceipt(
        ExternalOperationHandle handle,
        ExternalOperationCancellationDisposition disposition,
        ExternalOperationState state,
        DateTimeOffset recordedAt,
        ExternalOperationFailure? failure = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        ExternalOperationContracts.RequireKnownState(state);
        if (recordedAt == default)
        {
            throw new ArgumentException("A cancellation receipt must have a timestamp.", nameof(recordedAt));
        }

        var stateMatchesDisposition = disposition switch
        {
            ExternalOperationCancellationDisposition.Requested => state == ExternalOperationState.CancellationRequested,
            ExternalOperationCancellationDisposition.ConfirmedCancelled => state == ExternalOperationState.Cancelled,
            ExternalOperationCancellationDisposition.Unknown => state == ExternalOperationState.Unknown,
            ExternalOperationCancellationDisposition.Rejected => !ExternalOperationContracts.IsTerminal(state),
            ExternalOperationCancellationDisposition.AlreadyTerminal => ExternalOperationContracts.IsTerminal(state),
            _ => false,
        };
        if (!stateMatchesDisposition)
        {
            throw new ArgumentException(
                $"Cancellation disposition '{disposition}' is inconsistent with state '{state}'.",
                nameof(state));
        }

        if (disposition is ExternalOperationCancellationDisposition.Requested
            or ExternalOperationCancellationDisposition.ConfirmedCancelled
            or ExternalOperationCancellationDisposition.AlreadyTerminal)
        {
            if (failure is not null)
            {
                throw new ArgumentException("This cancellation disposition cannot carry failure evidence.", nameof(failure));
            }
        }
        else if (failure is not null && failure.Kind != ExternalOperationFailureKind.Cancellation)
        {
            throw new ArgumentException("Cancellation receipts must classify failures as cancellation failures.", nameof(failure));
        }

        if (disposition is (ExternalOperationCancellationDisposition.Rejected
            or ExternalOperationCancellationDisposition.Unknown)
            && failure is null)
        {
            throw new ArgumentException("A rejected or unknown cancellation must carry cancellation failure evidence.", nameof(failure));
        }

        Handle = handle;
        Disposition = disposition;
        State = state;
        RecordedAt = recordedAt;
        Failure = failure;
    }

    public ExternalOperationHandle Handle { get; }
    public ExternalOperationCancellationDisposition Disposition { get; }
    public ExternalOperationState State { get; }
    public DateTimeOffset RecordedAt { get; }
    public ExternalOperationFailure? Failure { get; }
}

/// <summary>
/// Resumes an accepted operation using its durable handle. Corrections are
/// artifact references, never an unbounded prompt or transcript.
/// </summary>
public sealed record ExternalOperationResumeRequest
{
    public ExternalOperationResumeRequest(
        ExternalOperationHandle handle,
        string resumeKey,
        IReadOnlyList<DelegationArtifactReference>? correctionArtifacts = null,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        handle.Validate();
        Handle = handle;
        ResumeKey = IdentityText.Require(resumeKey, nameof(resumeKey), 512);
        var corrections = correctionArtifacts ?? Array.Empty<DelegationArtifactReference>();
        if (corrections.Count > ArtifactContracts.MaximumCollectionItems)
        {
            throw new ArgumentException("A resume request contains too many correction artifacts.", nameof(correctionArtifacts));
        }

        var snapshot = corrections.ToArray();
        var identities = new HashSet<(string Provider, string Repository, string ArtifactId)>();
        foreach (var artifact in snapshot)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            ArtifactContracts.ValidateArtifact(artifact, handle.Correlation.DelegationId);
            if (!identities.Add((artifact.Provider, artifact.Repository, artifact.ArtifactId)))
            {
                throw new ArgumentException("Correction artifacts cannot contain duplicate identities.", nameof(correctionArtifacts));
            }
        }

        CorrectionArtifacts = Array.AsReadOnly(snapshot);
        Reason = reason is null ? null : IdentityText.RequireProse(reason, nameof(reason), 4_096);
    }

    public ExternalOperationHandle Handle { get; }
    public string ResumeKey { get; }
    public IReadOnlyList<DelegationArtifactReference> CorrectionArtifacts { get; }
    public string? Reason { get; }
}

/// <summary>Provider-neutral durable external-operation adapter seam.</summary>
public interface IExternalOperationProvider
{
    /// <remarks>
    /// The provider must invoke <paramref name="handleSink"/> immediately
    /// after learning the task handle, before waiting for final acceptance or
    /// result data. Losing the return value is therefore recoverable.
    /// </remarks>
    ValueTask<ExternalOperationStartReceipt> StartAsync(
        ExternalOperationStartRequest request,
        IExternalOperationHandleCaptureSink handleSink,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalOperationObservation> ObserveAsync(
        ExternalOperationHandle handle,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalOperationResult> GetResultAsync(
        ExternalOperationHandle handle,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalOperationCancellationReceipt> CancelAsync(
        ExternalOperationCancelRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ExternalOperationStartReceipt> ResumeAsync(
        ExternalOperationResumeRequest request,
        IExternalOperationHandleCaptureSink handleSink,
        CancellationToken cancellationToken = default);
}

/// <summary>Versioned model identity captured at invocation time.</summary>
public sealed record ModelProvenanceSnapshot
{
    public const string CurrentSchemaVersion = "v1";

    public ModelProvenanceSnapshot(
        string provider,
        string model,
        string? modelRevision = null,
        string? profile = null,
        string schemaVersion = CurrentSchemaVersion)
    {
        SchemaVersion = ArtifactContracts.Version(schemaVersion, nameof(schemaVersion));
        Provider = IdentityText.Require(provider, nameof(provider), 256);
        Model = IdentityText.Require(model, nameof(model), 512);
        ModelRevision = modelRevision is null ? null : IdentityText.Require(modelRevision, nameof(modelRevision), 512);
        Profile = profile is null ? null : IdentityText.Require(profile, nameof(profile), 512);
    }

    public string SchemaVersion { get; }
    public string Provider { get; }
    public string Model { get; }
    public string? ModelRevision { get; }
    public string? Profile { get; }
}

public sealed record ToolProvenance
{
    public ToolProvenance(string name, string? version = null, ArtifactContentIdentity? contentIdentity = null)
    {
        Name = IdentityText.Require(name, nameof(name), 512);
        Version = version is null ? null : IdentityText.Require(version, nameof(version), 256);
        contentIdentity?.Validate();
        ContentIdentity = contentIdentity;
    }

    public string Name { get; }
    public string? Version { get; }
    public ArtifactContentIdentity? ContentIdentity { get; }
}

/// <summary>Versioned immutable tool capability snapshot.</summary>
public sealed record ToolProvenanceSnapshot
{
    public const string CurrentSchemaVersion = "v1";

    public ToolProvenanceSnapshot(
        IReadOnlyList<ToolProvenance> tools,
        string schemaVersion = CurrentSchemaVersion)
    {
        SchemaVersion = ArtifactContracts.Version(schemaVersion, nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(tools);
        if (tools.Count > 64)
        {
            throw new ArgumentException("A tool provenance snapshot cannot contain more than 64 tools.", nameof(tools));
        }

        var snapshot = tools.ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in snapshot)
        {
            ArgumentNullException.ThrowIfNull(tool);
            if (!names.Add(tool.Name))
            {
                throw new ArgumentException("A tool provenance snapshot cannot contain duplicate tools.", nameof(tools));
            }
        }

        Tools = Array.AsReadOnly(snapshot);
    }

    public string SchemaVersion { get; }
    public IReadOnlyList<ToolProvenance> Tools { get; }
}

/// <summary>
/// Versioned usage measurements; values are deliberately strings to preserve
/// provider units. Values are not treated as a secret scanner: adapters and
/// redaction/retention policy remain authoritative, while known dangerous key
/// names are rejected as a bounded guardrail.
/// </summary>
public sealed record UsageProvenanceSnapshot
{
    public const string CurrentSchemaVersion = "v1";

    public UsageProvenanceSnapshot(
        IReadOnlyDictionary<string, string>? measurements = null,
        string schemaVersion = CurrentSchemaVersion)
    {
        SchemaVersion = ArtifactContracts.Version(schemaVersion, nameof(schemaVersion));
        Measurements = ExternalOperationContracts.SafeProperties(measurements, nameof(measurements));
    }

    public string SchemaVersion { get; }
    public IReadOnlyDictionary<string, string> Measurements { get; }
}

/// <summary>Atomic versioned provenance envelope attached to an operation result.</summary>
public sealed record ExternalOperationProvenanceSnapshot
{
    public const string CurrentSchemaVersion = "v1";

    public ExternalOperationProvenanceSnapshot(
        ModelProvenanceSnapshot? model,
        ToolProvenanceSnapshot tools,
        UsageProvenanceSnapshot usage,
        string schemaVersion = CurrentSchemaVersion)
    {
        SchemaVersion = ArtifactContracts.Version(schemaVersion, nameof(schemaVersion));
        Tools = tools ?? throw new ArgumentNullException(nameof(tools));
        Usage = usage ?? throw new ArgumentNullException(nameof(usage));
        Model = model;
    }

    public string SchemaVersion { get; }
    public ModelProvenanceSnapshot? Model { get; }
    public ToolProvenanceSnapshot Tools { get; }
    public UsageProvenanceSnapshot Usage { get; }
}

internal static class ExternalOperationContracts
{
    public static bool IsTerminal(ExternalOperationState state) => state is
        ExternalOperationState.Succeeded
        or ExternalOperationState.Failed
        or ExternalOperationState.Cancelled
        or ExternalOperationState.TimedOut
        or ExternalOperationState.Rejected;

    public static bool CanTransition(ExternalOperationState current, ExternalOperationState next)
    {
        RequireKnownState(current);
        RequireKnownState(next);
        if (current == next)
        {
            return true;
        }

        if (IsTerminal(current))
        {
            return false;
        }

        return current switch
        {
            ExternalOperationState.Accepted => next is ExternalOperationState.Running
                or ExternalOperationState.Waiting
                or ExternalOperationState.Succeeded
                or ExternalOperationState.Failed
                or ExternalOperationState.Cancelled
                or ExternalOperationState.TimedOut
                or ExternalOperationState.CancellationRequested
                or ExternalOperationState.Unknown
                or ExternalOperationState.Rejected,
            ExternalOperationState.Running => next is ExternalOperationState.Waiting
                or ExternalOperationState.Succeeded
                or ExternalOperationState.Failed
                or ExternalOperationState.CancellationRequested
                or ExternalOperationState.TimedOut
                or ExternalOperationState.Rejected
                or ExternalOperationState.Unknown,
            ExternalOperationState.Waiting => next is ExternalOperationState.Running
                or ExternalOperationState.Succeeded
                or ExternalOperationState.Failed
                or ExternalOperationState.CancellationRequested
                or ExternalOperationState.TimedOut
                or ExternalOperationState.Rejected
                or ExternalOperationState.Unknown,
            ExternalOperationState.CancellationRequested => next is ExternalOperationState.Running
                or ExternalOperationState.Waiting
                or ExternalOperationState.Succeeded
                or ExternalOperationState.Failed
                or ExternalOperationState.Cancelled
                or ExternalOperationState.TimedOut
                or ExternalOperationState.Rejected
                or ExternalOperationState.Unknown,
            ExternalOperationState.Unknown => true,
            _ => false,
        };
    }

    public static void RequireKnownState(ExternalOperationState state)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown external operation state.");
        }
    }

    public static void RequireMatchingIdentity(
        ExternalOperationStartIdentity identity,
        ExternalOperationCorrelation correlation)
    {
        if (identity.DelegationId != correlation.DelegationId
            || identity.WorkflowRun != correlation.WorkflowRun
            || identity.StructuralNode != correlation.StructuralNode
            || identity.NodeGeneration != correlation.NodeGeneration
            || !string.Equals(identity.ExecutionAttemptId, correlation.ExecutionAttemptId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Start identity and operation correlation must identify the same attempt.");
        }
    }

    public static void RequireFailureMatchesState(ExternalOperationState state, ExternalOperationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var valid = state switch
        {
            ExternalOperationState.Unknown => failure.Kind == ExternalOperationFailureKind.Transport,
            ExternalOperationState.Failed => failure.Kind is ExternalOperationFailureKind.Remote
                or ExternalOperationFailureKind.ResultValidation,
            ExternalOperationState.TimedOut => failure.Kind == ExternalOperationFailureKind.Timeout,
            ExternalOperationState.Rejected => failure.Kind == ExternalOperationFailureKind.Rejection,
            _ => false,
        };

        if (!valid)
        {
            throw new ArgumentException($"Failure kind '{failure.Kind}' is not valid for state '{state}'.", nameof(failure));
        }
    }

    public static IReadOnlyDictionary<string, string> SafeProperties(
        IReadOnlyDictionary<string, string>? values,
        string parameterName)
    {
        if (values is null || values.Count == 0)
        {
            return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        if (values.Count > 64)
        {
            throw new ArgumentException("A provenance map cannot contain more than 64 values.", parameterName);
        }

        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            var key = ArtifactContracts.Version(pair.Key, parameterName);
            var value = IdentityText.RequireProse(pair.Value, parameterName, 1_024);
            if (ContainsDangerousKey(key))
            {
                throw new ArgumentException(
                    "Provenance keys cannot name credentials, authorization data, or transcripts. Adapters and redaction policy remain authoritative for values.",
                    parameterName);
            }

            if (!copy.TryAdd(key, value))
            {
                throw new ArgumentException("A provenance map cannot contain duplicate keys.", parameterName);
            }
        }

        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(copy);
    }

    private static bool ContainsDangerousKey(string value)
    {
        var dangerousSegments = new HashSet<string>(StringComparer.Ordinal)
        {
            "secret",
            "secrets",
            "password",
            "passwords",
            "credential",
            "credentials",
            "authorization",
            "transcript",
            "access_token",
            "refresh_token",
        };

        var normalized = value.Replace('.', '_').Replace('-', '_');
        return normalized.Contains("access_token", StringComparison.Ordinal)
            || normalized.Contains("refresh_token", StringComparison.Ordinal)
            || dangerousSegments.Contains(normalized)
            || value.Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => dangerousSegments.Contains(segment));
    }
}
