namespace Marang;

/// <summary>
/// Host-authenticated supervisor identity. It is supplied by the host
/// boundary, never accepted from model-controlled request content.
/// </summary>
public sealed record SupervisorIdentity
{
    public SupervisorIdentity(string authorityScope, string subject)
    {
        AuthorityScope = IdentityText.Require(authorityScope, nameof(authorityScope), 256);
        Subject = IdentityText.Require(subject, nameof(subject), 512);
    }

    public string AuthorityScope { get; }
    public string Subject { get; }

    public void Validate()
    {
        IdentityText.Require(AuthorityScope, nameof(AuthorityScope), 256);
        IdentityText.Require(Subject, nameof(Subject), 512);
    }
}

/// <summary>
/// An opaque requested executor profile to be resolved and authorized by the
/// host. It grants no authority and is not a credential, endpoint, or
/// authorization decision. Host policy must validate it before execution.
/// </summary>
public sealed record ExecutorProfileReference
{
    public ExecutorProfileReference(string provider, string profile)
    {
        Provider = IdentityText.Require(provider, nameof(provider), 128);
        Profile = IdentityText.Require(profile, nameof(profile), 256);
    }

    public string Provider { get; }
    public string Profile { get; }

    public void Validate()
    {
        IdentityText.Require(Provider, nameof(Provider), 128);
        IdentityText.Require(Profile, nameof(Profile), 256);
    }
}

/// <summary>
/// A closed, typed set of supervisor actions. Each action can carry only the
/// fields meaningful for that action; arbitrary mutation payloads are not
/// representable through this contract.
/// </summary>
public abstract record SupervisorAction
{
    private SupervisorAction()
    {
    }

    public abstract string Kind { get; }
    public abstract void Validate();

    public sealed record Respond : SupervisorAction
    {
        public Respond(string response) => Response = IdentityText.RequireProse(response, nameof(response), 16_384);
        public string Response { get; }
        public override string Kind => "respond";
        public override void Validate() => IdentityText.RequireProse(Response, nameof(Response), 16_384);
    }

    public sealed record Approve : SupervisorAction
    {
        public Approve(string? rationale = null) => Rationale = rationale is null
            ? null
            : IdentityText.RequireProse(rationale, nameof(rationale), 4_096);
        public string? Rationale { get; }
        public override string Kind => "approve";
        public override void Validate()
        {
            if (Rationale is not null)
            {
                IdentityText.RequireProse(Rationale, nameof(Rationale), 4_096);
            }
        }
    }

    public sealed record Reject : SupervisorAction
    {
        public Reject(string reason) => Reason = IdentityText.RequireProse(reason, nameof(reason), 4_096);
        public string Reason { get; }
        public override string Kind => "reject";
        public override void Validate() => IdentityText.RequireProse(Reason, nameof(Reason), 4_096);
    }

    public sealed record Retry : SupervisorAction
    {
        public Retry(string reason) => Reason = IdentityText.RequireProse(reason, nameof(reason), 1_024);
        public string Reason { get; }
        public override string Kind => "retry";
        public override void Validate() => IdentityText.RequireProse(Reason, nameof(Reason), 1_024);
    }

    public sealed record ReexecuteNode : SupervisorAction
    {
        public ReexecuteNode(StructuralNodeReference target, string reason)
        {
            Target = target;
            Target.Validate();
            Reason = IdentityText.RequireProse(reason, nameof(reason), 4_096);
        }

        public StructuralNodeReference Target { get; }
        public string Reason { get; }
        public override string Kind => "reexecute-node";
        public override void Validate()
        {
            Target.Validate();
            IdentityText.RequireProse(Reason, nameof(Reason), 4_096);
        }
    }

    public sealed record ReexecuteSubgraph : SupervisorAction
    {
        public ReexecuteSubgraph(StructuralNodeReference root, string reason)
        {
            Root = root;
            Root.Validate();
            Reason = IdentityText.RequireProse(reason, nameof(reason), 4_096);
        }

        public StructuralNodeReference Root { get; }
        public string Reason { get; }
        public override string Kind => "reexecute-subgraph";
        public override void Validate()
        {
            Root.Validate();
            IdentityText.RequireProse(Reason, nameof(Reason), 4_096);
        }
    }

    public sealed record AddConstraint : SupervisorAction
    {
        public AddConstraint(string constraint) => Constraint = IdentityText.RequireProse(constraint, nameof(constraint), 4_096);
        public string Constraint { get; }
        public override string Kind => "add-constraint";
        public override void Validate() => IdentityText.RequireProse(Constraint, nameof(Constraint), 4_096);
    }

    public sealed record ChangeExecutor : SupervisorAction
    {
        public ChangeExecutor(ExecutorProfileReference profile, string reason)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            Profile.Validate();
            Reason = IdentityText.RequireProse(reason, nameof(reason), 4_096);
        }

        public ExecutorProfileReference Profile { get; }
        public string Reason { get; }
        public override string Kind => "change-executor";
        public override void Validate()
        {
            Profile.Validate();
            IdentityText.RequireProse(Reason, nameof(Reason), 4_096);
        }
    }

    public sealed record SelectAlternative : SupervisorAction
    {
        public SelectAlternative(string alternativeId, string rationale)
        {
            AlternativeId = IdentityText.Require(alternativeId, nameof(alternativeId), 512);
            Rationale = IdentityText.RequireProse(rationale, nameof(rationale), 4_096);
        }

        public string AlternativeId { get; }
        public string Rationale { get; }
        public override string Kind => "select-alternative";
        public override void Validate()
        {
            IdentityText.Require(AlternativeId, nameof(AlternativeId), 512);
            IdentityText.RequireProse(Rationale, nameof(Rationale), 4_096);
        }
    }

    public sealed record Escalate : SupervisorAction
    {
        public Escalate(string reason) => Reason = IdentityText.RequireProse(reason, nameof(reason), 4_096);
        public string Reason { get; }
        public override string Kind => "escalate";
        public override void Validate() => IdentityText.RequireProse(Reason, nameof(Reason), 4_096);
    }

    public sealed record Cancel : SupervisorAction
    {
        public Cancel(string reason) => Reason = IdentityText.RequireProse(reason, nameof(reason), 4_096);
        public string Reason { get; }
        public override string Kind => "cancel";
        public override void Validate() => IdentityText.RequireProse(Reason, nameof(Reason), 4_096);
    }
}

/// <summary>
/// A host-authenticated, caller-scoped intervention request. The supervisor
/// identity is deliberately passed separately to the acceptance boundary.
/// </summary>
public sealed record SupervisorIntervention
{
    public SupervisorIntervention(
        DelegationId delegationId,
        SupervisorCheckpointId checkpointId,
        string interventionKey,
        long expectedRevision,
        SupervisorAction action)
    {
        if (delegationId.Value == Guid.Empty)
        {
            throw new ArgumentException("A delegation identifier cannot be empty.", nameof(delegationId));
        }

        DelegationId = delegationId;
        CheckpointId = checkpointId;
        CheckpointId.Validate();
        InterventionKey = IdentityText.Require(interventionKey, nameof(interventionKey), 256);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        ExpectedRevision = expectedRevision;
        Action = action ?? throw new ArgumentNullException(nameof(action));
        Action.Validate();
    }

    public DelegationId DelegationId { get; }
    public SupervisorCheckpointId CheckpointId { get; }
    public string InterventionKey { get; }
    public long ExpectedRevision { get; }
    public SupervisorAction Action { get; }

    public void Validate()
    {
        if (DelegationId.Value == Guid.Empty)
        {
            throw new ArgumentException("A delegation identifier cannot be empty.", nameof(DelegationId));
        }

        CheckpointId.Validate();
        IdentityText.Require(InterventionKey, nameof(InterventionKey), 256);
        ArgumentOutOfRangeException.ThrowIfNegative(ExpectedRevision);
        Action.Validate();
    }
}

/// <summary>Non-authorizing request to schedule or surface supervisor attention.</summary>
public sealed record WakeHint
{
    public WakeHint(
        DelegationId delegationId,
        SupervisorCheckpointId? checkpointId,
        string reason,
        long afterRevision,
        DateTimeOffset? expiresAt = null)
    {
        if (delegationId.Value == Guid.Empty)
        {
            throw new ArgumentException("A delegation identifier cannot be empty.", nameof(delegationId));
        }

        checkpointId?.Validate();
        DelegationId = delegationId;
        CheckpointId = checkpointId;
        Reason = IdentityText.RequireProse(reason, nameof(reason), 2_048);
        ArgumentOutOfRangeException.ThrowIfNegative(afterRevision);
        AfterRevision = afterRevision;
        if (expiresAt is { } value && value == default)
        {
            throw new ArgumentException("A wake-hint expiry must be a real timestamp when supplied.", nameof(expiresAt));
        }

        ExpiresAt = expiresAt;
    }

    public DelegationId DelegationId { get; }
    public SupervisorCheckpointId? CheckpointId { get; }
    public string Reason { get; }
    public long AfterRevision { get; }
    public DateTimeOffset? ExpiresAt { get; }
}
