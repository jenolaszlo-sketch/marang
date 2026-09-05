namespace Marang;

/// <summary>Identifies the kind of plan revision selected for a delegation.</summary>
public enum WorkflowPlanReferenceKind
{
    BuiltInPreset = 0,
    FuwenDefinition = 1,
}

/// <summary>
/// A workflow plan selection identity. The value does not prove authorization,
/// verification, or binding; the host policy/resolver must establish those
/// properties before execution. This is not a Fuwen runtime type.
/// </summary>
public sealed record WorkflowPlanRevisionReference
{
    public WorkflowPlanRevisionReference(
        WorkflowPlanReferenceKind kind,
        string identifier,
        string revision,
        string? canonicalFingerprint)
    {
        Kind = kind;
        Identifier = IdentityText.Require(identifier, nameof(identifier), 512);
        Revision = IdentityText.Require(revision, nameof(revision), 256);
        CanonicalFingerprint = canonicalFingerprint is null
            ? null
            : IdentityText.RequireSha256(canonicalFingerprint, nameof(canonicalFingerprint));

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown workflow plan reference kind.");
        }

        if (kind == WorkflowPlanReferenceKind.BuiltInPreset && CanonicalFingerprint is not null)
        {
            throw new ArgumentException("A built-in preset does not accept a caller-supplied content fingerprint.", nameof(canonicalFingerprint));
        }

        if (kind == WorkflowPlanReferenceKind.FuwenDefinition && CanonicalFingerprint is null)
        {
            throw new ArgumentException("A Fuwen definition reference requires a canonical fingerprint.", nameof(canonicalFingerprint));
        }
    }

    public WorkflowPlanReferenceKind Kind { get; }
    public string Identifier { get; }
    public string Revision { get; }
    public string? CanonicalFingerprint { get; }

    public static WorkflowPlanRevisionReference BuiltInPreset(string identifier, string version) =>
        new(WorkflowPlanReferenceKind.BuiltInPreset, identifier, version, null);

    public static WorkflowPlanRevisionReference FuwenDefinition(
        string definitionIdentifier,
        string revision,
        string canonicalFingerprint) =>
        new(WorkflowPlanReferenceKind.FuwenDefinition, definitionIdentifier, revision, canonicalFingerprint);

    public void Validate()
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown workflow plan reference kind.");
        }

        IdentityText.Require(Identifier, nameof(Identifier), 512);
        IdentityText.Require(Revision, nameof(Revision), 256);
        if (Kind == WorkflowPlanReferenceKind.FuwenDefinition)
        {
            IdentityText.RequireSha256(CanonicalFingerprint, nameof(CanonicalFingerprint));
        }
        else if (CanonicalFingerprint is not null)
        {
            throw new ArgumentException("A built-in preset does not accept a content fingerprint.", nameof(CanonicalFingerprint));
        }
    }
}

/// <summary>Host-supplied identity of the Hongxian session that contains this work.</summary>
public readonly record struct HongxianSessionReference
{
    public HongxianSessionReference(string identifier)
    {
        Identifier = IdentityText.Require(identifier, nameof(identifier), 512);
    }

    public string Identifier { get; }

    public void Validate() => IdentityText.Require(Identifier, nameof(Identifier), 512);
}

/// <summary>Provider-neutral workflow run and execution epoch identity.</summary>
public sealed record WorkflowRunExecutionReference
{
    public WorkflowRunExecutionReference(string provider, string workflowRunId, string executionEpoch)
    {
        Provider = IdentityText.Require(provider, nameof(provider), 128);
        WorkflowRunId = IdentityText.Require(workflowRunId, nameof(workflowRunId), 512);
        ExecutionEpoch = IdentityText.Require(executionEpoch, nameof(executionEpoch), 256);
    }

    public string Provider { get; }
    public string WorkflowRunId { get; }
    public string ExecutionEpoch { get; }

    public void Validate()
    {
        IdentityText.Require(Provider, nameof(Provider), 128);
        IdentityText.Require(WorkflowRunId, nameof(WorkflowRunId), 512);
        IdentityText.Require(ExecutionEpoch, nameof(ExecutionEpoch), 256);
    }
}

/// <summary>Stable structural node identity, distinct from runtime generations.</summary>
public readonly record struct StructuralNodeReference
{
    public StructuralNodeReference(string identifier)
    {
        Identifier = IdentityText.Require(identifier, nameof(identifier), 512);
    }

    public string Identifier { get; }

    public void Validate() => IdentityText.Require(Identifier, nameof(Identifier), 512);
}

/// <summary>Runtime generation of one structural node.</summary>
public readonly record struct NodeGenerationId
{
    public NodeGenerationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A node generation identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public void Validate() => IdentityText.RequireNonEmpty(Value, nameof(Value));
}

/// <summary>One provider attempt/handle within a node generation.</summary>
public sealed record ProviderExecutionAttemptReference
{
    public ProviderExecutionAttemptReference(string provider, string attemptId, string handle)
    {
        Provider = IdentityText.Require(provider, nameof(provider), 128);
        AttemptId = IdentityText.Require(attemptId, nameof(attemptId), 512);
        Handle = IdentityText.Require(handle, nameof(handle), 4_096);
    }

    public string Provider { get; }
    public string AttemptId { get; }
    public string Handle { get; }

    public void Validate()
    {
        IdentityText.Require(Provider, nameof(Provider), 128);
        IdentityText.Require(AttemptId, nameof(AttemptId), 512);
        IdentityText.Require(Handle, nameof(Handle), 4_096);
    }
}

/// <summary>Stable address of a supervisory checkpoint.</summary>
public readonly record struct SupervisorCheckpointId
{
    public SupervisorCheckpointId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A supervisor checkpoint identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public void Validate() => IdentityText.RequireNonEmpty(Value, nameof(Value));
}

/// <summary>
/// Immutable information needed to resume or inspect a waiting checkpoint.
/// It does not authorize an intervention and does not implement one.
/// </summary>
public sealed record SupervisorCheckpointDescriptor
{
    public SupervisorCheckpointDescriptor(
        SupervisorCheckpointId checkpointId,
        HongxianSessionReference session,
        DelegationId delegationId,
        WorkflowPlanRevisionReference planRevision,
        WorkflowRunExecutionReference workflowRun,
        StructuralNodeReference structuralNode,
        NodeGenerationId nodeGeneration,
        long expectedObservableRevision,
        bool dependentProgressGated)
    {
        CheckpointId = checkpointId;
        Session = session;
        DelegationId = delegationId;
        PlanRevision = planRevision ?? throw new ArgumentNullException(nameof(planRevision));
        WorkflowRun = workflowRun ?? throw new ArgumentNullException(nameof(workflowRun));
        StructuralNode = structuralNode;
        NodeGeneration = nodeGeneration;
        ExpectedObservableRevision = expectedObservableRevision;
        DependentProgressGated = dependentProgressGated;
        if (!dependentProgressGated)
        {
            throw new ArgumentException(
                "A supervisor checkpoint must gate dependent progress; nonblocking attention is a wake hint.",
                nameof(dependentProgressGated));
        }

        Validate();
    }

    public SupervisorCheckpointId CheckpointId { get; }
    public HongxianSessionReference Session { get; }
    public DelegationId DelegationId { get; }
    public WorkflowPlanRevisionReference PlanRevision { get; }
    public WorkflowRunExecutionReference WorkflowRun { get; }
    public StructuralNodeReference StructuralNode { get; }
    public NodeGenerationId NodeGeneration { get; }
    public long ExpectedObservableRevision { get; }
    public bool DependentProgressGated { get; }

    public void Validate()
    {
        CheckpointId.Validate();
        Session.Validate();
        if (DelegationId.Value == Guid.Empty)
        {
            throw new ArgumentException("A checkpoint delegation identifier cannot be empty.", nameof(DelegationId));
        }

        PlanRevision.Validate();
        WorkflowRun.Validate();
        StructuralNode.Validate();
        NodeGeneration.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(ExpectedObservableRevision);
    }
}

/// <summary>Rules that preserve the distinction between attempts and generations.</summary>
public static class ExecutionIdentityRules
{
    public static void EnsureRetryOrReconnectSameGeneration(
        NodeGenerationId original,
        NodeGenerationId candidate)
    {
        original.Validate();
        candidate.Validate();
        if (original != candidate)
        {
            throw new InvalidOperationException("A retry or reconnect must remain in the same node generation.");
        }
    }

    public static void EnsureSemanticReexecutionNewGeneration(
        NodeGenerationId previous,
        NodeGenerationId candidate)
    {
        previous.Validate();
        candidate.Validate();
        if (previous == candidate)
        {
            throw new InvalidOperationException("Semantic node re-execution requires a new node generation.");
        }
    }

    public static void EnsureReopenedWorkNewRunAndEpoch(
        WorkflowRunExecutionReference previous,
        WorkflowRunExecutionReference candidate)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(candidate);
        previous.Validate();
        candidate.Validate();
        if (string.Equals(previous.WorkflowRunId, candidate.WorkflowRunId, StringComparison.Ordinal)
            || string.Equals(previous.ExecutionEpoch, candidate.ExecutionEpoch, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Reopening completed work requires a new linked workflow run and execution epoch.");
        }
    }
}

internal static class IdentityText
{
    public static string Require(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty identity value is required.", parameterName);
        }

        if (value.Length > maximumLength)
        {
            throw new ArgumentException($"The identity value cannot exceed {maximumLength} characters.", parameterName);
        }

        if (value.Normalize(System.Text.NormalizationForm.FormC) != value
            || value != value.Trim()
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("Identity values must already be in canonical form.", parameterName);
        }

        return value;
    }

    public static string RequireProse(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Non-empty prose is required.", parameterName);
        }

        if (value.Length > maximumLength)
        {
            throw new ArgumentException($"Prose cannot exceed {maximumLength} characters.", parameterName);
        }

        if (value.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        {
            throw new ArgumentException("Prose cannot contain non-whitespace control characters.", parameterName);
        }

        return value.Normalize(System.Text.NormalizationForm.FormC)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    public static string RequireSha256(string? value, string parameterName)
    {
        Require(value, parameterName, 64);
        if (value!.Length != 64 || value.Any(character => !Uri.IsHexDigit(character) || char.IsUpper(character)))
        {
            throw new ArgumentException("A canonical SHA-256 fingerprint must contain 64 lowercase hexadecimal characters.", parameterName);
        }

        return value;
    }

    public static void RequireNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("The identifier cannot be empty.", parameterName);
        }
    }
}
