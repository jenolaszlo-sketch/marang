namespace Marang;

[Flags]
public enum SupervisorContextFacet
{
    None = 0,
    Status = 1,
    Summary = 2,
    Artifacts = 4,
    Correlations = 8,
    PrimitiveReferences = 16,
}

public enum ContextFacetAvailability
{
    Included = 0,
    Truncated = 1,
    Omitted = 2,
}

/// <summary>Explicitly records whether a requested context facet was included.</summary>
public sealed record ContextFacetOutcome
{
    public ContextFacetOutcome(
        SupervisorContextFacet facet,
        ContextFacetAvailability availability,
        int itemCount,
        string? reason = null)
    {
        ContextContracts.RequireSingleFacet(facet, nameof(facet));
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        if (availability is not (ContextFacetAvailability.Included
            or ContextFacetAvailability.Truncated
            or ContextFacetAvailability.Omitted))
        {
            throw new ArgumentOutOfRangeException(nameof(availability), availability, "Unknown context facet availability.");
        }

        if (availability == ContextFacetAvailability.Omitted && itemCount != 0)
        {
            throw new ArgumentException("An omitted facet cannot report included items.", nameof(itemCount));
        }

        if (availability != ContextFacetAvailability.Included && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Truncation and omission require an explicit reason.", nameof(reason));
        }

        Facet = facet;
        Availability = availability;
        ItemCount = itemCount;
        Reason = reason is null ? null : ContextContracts.Prose(reason, nameof(reason), 1_024);
    }

    public SupervisorContextFacet Facet { get; }
    public ContextFacetAvailability Availability { get; }
    public int ItemCount { get; }
    public string? Reason { get; }
}

/// <summary>Hard limits applied to one context response.</summary>
public sealed record SupervisorContextLimits
{
    public SupervisorContextLimits(int maxItems, int maxInlineSummaryBytes)
    {
        if (maxItems is < 1 or > ContextContracts.MaximumItems)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems), maxItems, $"maxItems must be between 1 and {ContextContracts.MaximumItems}.");
        }

        if (maxInlineSummaryBytes is < 1 or > ContextContracts.MaximumInlineSummaryBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInlineSummaryBytes), maxInlineSummaryBytes, $"maxInlineSummaryBytes must be between 1 and {ContextContracts.MaximumInlineSummaryBytes}.");
        }

        MaxItems = maxItems;
        MaxInlineSummaryBytes = maxInlineSummaryBytes;
    }

    public int MaxItems { get; }
    public int MaxInlineSummaryBytes { get; }
}

/// <summary>Bounded, explicit request for checkpoint re-entry context.</summary>
public sealed record SupervisorContextRequest
{
    public SupervisorContextRequest(
        DelegationId delegationId,
        SupervisorCheckpointId checkpointId,
        long expectedRevision,
        SupervisorContextFacet requestedFacets,
        SupervisorContextLimits limits)
    {
        ContextContracts.RequireDelegation(delegationId, nameof(delegationId));
        checkpointId.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        ContextContracts.RequireFacets(requestedFacets, nameof(requestedFacets));
        ArgumentNullException.ThrowIfNull(limits);
        DelegationId = delegationId;
        CheckpointId = checkpointId;
        ExpectedRevision = expectedRevision;
        RequestedFacets = requestedFacets;
        Limits = limits;
    }

    public DelegationId DelegationId { get; }
    public SupervisorCheckpointId CheckpointId { get; }
    public long ExpectedRevision { get; }
    public SupervisorContextFacet RequestedFacets { get; }
    public SupervisorContextLimits Limits { get; }

    public void ValidateAgainst(DelegationProgress waitingProgress)
    {
        ArgumentNullException.ThrowIfNull(waitingProgress);
        if (waitingProgress.State != DelegationState.WaitingForSupervisor
            || waitingProgress.Checkpoint is null)
        {
            throw new ArgumentException("Context can only be requested for a waiting supervisor checkpoint.", nameof(waitingProgress));
        }

        if (waitingProgress.Revision < 0
            || waitingProgress.WorkerCalls < 0
            || waitingProgress.Retries < 0
            || waitingProgress.UpdatedAt == default)
        {
            throw new ArgumentException("The waiting progress snapshot contains invalid revision, counters, or timestamp.", nameof(waitingProgress));
        }

        waitingProgress.Checkpoint.Validate();
        if (waitingProgress.Checkpoint.ExpectedObservableRevision != waitingProgress.Revision
            || waitingProgress.Checkpoint.DelegationId != waitingProgress.DelegationId)
        {
            throw new ArgumentException("The waiting progress checkpoint must match its delegation and revision.", nameof(waitingProgress));
        }

        if (waitingProgress.DelegationId != DelegationId
            || waitingProgress.Checkpoint.CheckpointId != CheckpointId
            || waitingProgress.Revision != ExpectedRevision)
        {
            throw new ArgumentException("Context request must match the current delegation, checkpoint, and observable revision.", nameof(waitingProgress));
        }
    }
}

/// <summary>A bounded human-readable status or summary item; never raw transcript content.</summary>
public sealed record SupervisorContextItem
{
    public SupervisorContextItem(SupervisorContextFacet facet, string key, string text)
    {
        ContextContracts.RequireSingleFacet(facet, nameof(facet));
        if (facet is not (SupervisorContextFacet.Status or SupervisorContextFacet.Summary))
        {
            throw new ArgumentException("Context items must be status or summary facets.", nameof(facet));
        }

        Facet = facet;
        Key = ContextContracts.Identity(key, nameof(key), 256);
        Text = ContextContracts.Prose(text, nameof(text), 16_384);
    }

    public SupervisorContextFacet Facet { get; }
    public string Key { get; }
    public string Text { get; }
    public int Utf8ByteCount => System.Text.Encoding.UTF8.GetByteCount(Text);
}

/// <summary>Identity used to correlate context with another durable system.</summary>
public sealed record ContextCorrelationReference
{
    public ContextCorrelationReference(string provider, string kind, string identifier, string? revision = null)
    {
        Provider = ContextContracts.Identity(provider, nameof(provider), 128);
        Kind = ContextContracts.Identity(kind, nameof(kind), 256);
        Identifier = ContextContracts.Identity(identifier, nameof(identifier), 1_024);
        Revision = revision is null ? null : ContextContracts.Identity(revision, nameof(revision), 512);
    }

    public string Provider { get; }
    public string Kind { get; }
    public string Identifier { get; }
    public string? Revision { get; }
}

/// <summary>
/// Provider-neutral immutable provenance identity. It conveys reproducibility,
/// not authority or validation proof, and does not embed a primitive store.
/// </summary>
public sealed record ContextProvenanceReference
{
    public ContextProvenanceReference(
        string provider,
        string kind,
        string identifier,
        string? revision = null,
        string? contentHash = null)
    {
        Provider = ContextContracts.Identity(provider, nameof(provider), 128);
        Kind = ContextContracts.Identity(kind, nameof(kind), 256);
        Identifier = ContextContracts.Identity(identifier, nameof(identifier), 1_024);
        Revision = revision is null ? null : ContextContracts.Identity(revision, nameof(revision), 512);
        ContentHash = contentHash is null
            ? null
            : IdentityText.RequireSha256(contentHash, nameof(contentHash));
    }

    public string Provider { get; }
    public string Kind { get; }
    public string Identifier { get; }
    public string? Revision { get; }
    public string? ContentHash { get; }

    public static ContextProvenanceReference CangjieSnapshot(
        string identifier,
        string? contentHash = null) => new("cangjie", "context-snapshot", identifier, null, contentHash);

    public static ContextProvenanceReference HetuIndexPublication(
        string repositoryIdentifier,
        string indexRunIdentifier,
        string? indexIdentity = null) => new("hetu", "index-publication", repositoryIdentifier, indexRunIdentifier, indexIdentity);
}

/// <summary>Bounded context package for one exact waiting checkpoint.</summary>
public sealed record SupervisorContextPackage
{
    public SupervisorContextPackage(
        DelegationId delegationId,
        SupervisorCheckpointId checkpointId,
        long revision,
        SupervisorContextFacet requestedFacets,
        SupervisorContextLimits appliedLimits,
        IReadOnlyList<SupervisorContextItem> items,
        IReadOnlyList<DelegationArtifactReference> artifacts,
        IReadOnlyList<ContextCorrelationReference> correlations,
        IReadOnlyList<ContextProvenanceReference> primitiveReferences,
        IReadOnlyList<ContextFacetOutcome> facetOutcomes)
    {
        ContextContracts.RequireDelegation(delegationId, nameof(delegationId));
        checkpointId.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ContextContracts.RequireFacets(requestedFacets, nameof(requestedFacets));
        ArgumentNullException.ThrowIfNull(appliedLimits);
        Items = Snapshot(items, nameof(items));
        Artifacts = Snapshot(artifacts, nameof(artifacts));
        Correlations = Snapshot(correlations, nameof(correlations));
        PrimitiveReferences = Snapshot(primitiveReferences, nameof(primitiveReferences));
        FacetOutcomes = Snapshot(facetOutcomes, nameof(facetOutcomes));
        foreach (var item in Items) ArgumentNullException.ThrowIfNull(item);
        foreach (var artifact in Artifacts) ContextContracts.ValidateArtifact(artifact);
        foreach (var correlation in Correlations) ArgumentNullException.ThrowIfNull(correlation);
        foreach (var reference in PrimitiveReferences) ArgumentNullException.ThrowIfNull(reference);
        foreach (var outcome in FacetOutcomes) ArgumentNullException.ThrowIfNull(outcome);

        if (Items.Count + Artifacts.Count + Correlations.Count + PrimitiveReferences.Count > appliedLimits.MaxItems)
        {
            throw new ArgumentException("The context package exceeds its applied item limit.", nameof(appliedLimits));
        }

        var bytes = Items.Sum(item => item.Utf8ByteCount);
        if (bytes > appliedLimits.MaxInlineSummaryBytes)
        {
            throw new ArgumentException("The context package exceeds its applied UTF-8 summary limit.", nameof(appliedLimits));
        }

        DelegationId = delegationId;
        CheckpointId = checkpointId;
        Revision = revision;
        RequestedFacets = requestedFacets;
        AppliedLimits = appliedLimits;
        InlineSummaryUtf8Bytes = bytes;
        ValidateFacetConsistency();
    }

    public DelegationId DelegationId { get; }
    public SupervisorCheckpointId CheckpointId { get; }
    public long Revision { get; }
    public SupervisorContextFacet RequestedFacets { get; }
    public SupervisorContextLimits AppliedLimits { get; }
    public IReadOnlyList<SupervisorContextItem> Items { get; }
    public IReadOnlyList<DelegationArtifactReference> Artifacts { get; }
    public IReadOnlyList<ContextCorrelationReference> Correlations { get; }
    public IReadOnlyList<ContextProvenanceReference> PrimitiveReferences { get; }
    public IReadOnlyList<ContextFacetOutcome> FacetOutcomes { get; }
    public int InlineSummaryUtf8Bytes { get; }

    public void ValidateAgainst(SupervisorContextRequest request, DelegationProgress waitingProgress)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.ValidateAgainst(waitingProgress);
        if (DelegationId != request.DelegationId
            || CheckpointId != request.CheckpointId
            || Revision != request.ExpectedRevision)
        {
            throw new ArgumentException("Context package is not bound to the requested checkpoint fence.", nameof(request));
        }

        if (RequestedFacets != request.RequestedFacets)
        {
            throw new ArgumentException("Context package facets do not match the request.", nameof(request));
        }

        if (AppliedLimits.MaxItems > request.Limits.MaxItems
            || AppliedLimits.MaxInlineSummaryBytes > request.Limits.MaxInlineSummaryBytes)
        {
            throw new ArgumentException("Applied context limits cannot exceed the requested limits.", nameof(request));
        }

        ValidateFacetConsistency();
    }

    private static IReadOnlyList<T> Snapshot<T>(IReadOnlyList<T> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > ContextContracts.MaximumItems)
        {
            throw new ArgumentException($"A context collection cannot exceed {ContextContracts.MaximumItems} items.", parameterName);
        }

        return Array.AsReadOnly(values.ToArray());
    }

    private void ValidateFacetConsistency()
    {
        var requested = ContextContracts.EnumerateFacets(RequestedFacets).ToArray();
        var outcomes = FacetOutcomes.ToDictionary(outcome => outcome.Facet);
        if (outcomes.Count != FacetOutcomes.Count || outcomes.Count != requested.Length)
        {
            throw new ArgumentException("Context must contain exactly one outcome for every requested facet.", nameof(FacetOutcomes));
        }

        if (Items.Any(item => !requested.Contains(item.Facet))
            || (Artifacts.Count > 0 && !requested.Contains(SupervisorContextFacet.Artifacts))
            || (Correlations.Count > 0 && !requested.Contains(SupervisorContextFacet.Correlations))
            || (PrimitiveReferences.Count > 0 && !requested.Contains(SupervisorContextFacet.PrimitiveReferences)))
        {
            throw new ArgumentException("Context contains content for an unrequested facet.", nameof(RequestedFacets));
        }

        foreach (var outcome in FacetOutcomes)
        {
            var actualCount = outcome.Facet switch
            {
                SupervisorContextFacet.Status or SupervisorContextFacet.Summary => Items.Count(item => item.Facet == outcome.Facet),
                SupervisorContextFacet.Artifacts => Artifacts.Count,
                SupervisorContextFacet.Correlations => Correlations.Count,
                SupervisorContextFacet.PrimitiveReferences => PrimitiveReferences.Count,
                _ => throw new ArgumentOutOfRangeException(nameof(outcome), "Unknown context facet."),
            };

            if (!requested.Contains(outcome.Facet) || outcome.ItemCount != actualCount)
            {
                throw new ArgumentException("Context facet outcomes do not match requested facets and content.", nameof(FacetOutcomes));
            }
        }
    }
}

/// <summary>
/// Resolves authoritative waiting state, applies host authorization/redaction,
/// and returns bounded context. Implementations must not include secrets, raw
/// prompts, full conversations, or repository contents.
/// </summary>
public interface ISupervisorContextProvider
{
    ValueTask<SupervisorContextPackage> GetAsync(
        SupervisorIdentity supervisor,
        SupervisorContextRequest request,
        CancellationToken cancellationToken = default);
}

internal static class ContextContracts
{
    public const int MaximumItems = 128;
    public const int MaximumInlineSummaryBytes = 65_536;

    public static string Identity(string? value, string parameterName, int maximumLength) => IdentityText.Require(value, parameterName, maximumLength);
    public static string Prose(string? value, string parameterName, int maximumLength) => IdentityText.RequireProse(value, parameterName, maximumLength);
    public static void RequireDelegation(DelegationId value, string parameterName) => IdentityText.RequireNonEmpty(value.Value, parameterName);

    public static void ValidateArtifact(DelegationArtifactReference value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArtifactContracts.ValidateArtifact(value, value.DelegationId);
    }

    public static void RequireFacets(SupervisorContextFacet value, string parameterName)
    {
        const SupervisorContextFacet all = SupervisorContextFacet.Status | SupervisorContextFacet.Summary | SupervisorContextFacet.Artifacts | SupervisorContextFacet.Correlations | SupervisorContextFacet.PrimitiveReferences;
        if (value == SupervisorContextFacet.None || (value & ~all) != 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "At least one known context facet is required.");
        }
    }

    public static void RequireSingleFacet(SupervisorContextFacet value, string parameterName)
    {
        RequireFacets(value, parameterName);
        var bits = (int)value;
        if ((bits & (bits - 1)) != 0)
        {
            throw new ArgumentException("Exactly one context facet is required.", parameterName);
        }
    }

    public static IEnumerable<SupervisorContextFacet> EnumerateFacets(SupervisorContextFacet value)
    {
        foreach (var facet in new[] { SupervisorContextFacet.Status, SupervisorContextFacet.Summary, SupervisorContextFacet.Artifacts, SupervisorContextFacet.Correlations, SupervisorContextFacet.PrimitiveReferences })
        {
            if ((value & facet) != 0) yield return facet;
        }
    }
}
