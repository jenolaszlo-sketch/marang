using System.Collections.Immutable;

namespace Marang.Runs.Adaptive;

/// <summary>Trusted-host provenance for an operator's durable revision goal.</summary>
public enum SupervisorIntentTrigger { User, Evidence, Initial }

public enum SupervisorDiscussionStatus { Pending, Resolved, Dismissed }

/// <summary>Immutable, bounded decision metadata attached to one durable workflow revision.</summary>
public sealed record SupervisorRevisionIntent(
    Guid WorkflowId,
    string RevisionId,
    string Goal,
    string RevisionDigest,
    SupervisorIntentTrigger Trigger,
    string Rationale,
    ImmutableArray<string> EvidenceReferences,
    string RecordedBy,
    DateTimeOffset RecordedAt);

/// <summary>A durable unresolved supervisor decision fenced to its observed parent state.</summary>
public sealed record SupervisorPendingDiscussion(
    Guid WorkflowId,
    string DecisionId,
    string ParentRevisionId,
    string ParentStateDigest,
    string Summary,
    ImmutableArray<string> MissingInformation,
    ImmutableArray<string> AffectedLogicalIds,
    ImmutableArray<string> Options,
    ImmutableArray<string> EvidenceReferences,
    SupervisorDiscussionStatus Status,
    bool IsStale,
    string? Choice,
    string RecordedBy,
    DateTimeOffset RecordedAt)
{
    public string? DismissalReason { get; init; }
}

/// <summary>One authenticated operator's exact acknowledged revision and evidence watermark.</summary>
public sealed record SupervisorOperatorCheckpoint(
    Guid WorkflowId,
    string ActorId,
    string RevisionId,
    long EvidenceWatermark,
    DateTimeOffset AcknowledgedAt);
