namespace Marang;

/// <summary>
/// An explicit content-hash contract. Marang preserves unknown future
/// contracts without interpreting them; SHA-256 contracts require exact
/// lowercase hexadecimal values.
/// </summary>
public readonly record struct ArtifactContentIdentity
{
    public const string Sha256BytesV1 = "sha256-bytes-v1";

    public ArtifactContentIdentity(string contractVersion, string hash)
    {
        ContractVersion = ArtifactContracts.Version(contractVersion, nameof(contractVersion));
        Hash = ArtifactContracts.Hash(hash, nameof(hash), ContractVersion.StartsWith("sha256-", StringComparison.Ordinal));
    }

    public string ContractVersion { get; }
    public string Hash { get; }

    public void Validate()
    {
        ArtifactContracts.Version(ContractVersion, nameof(ContractVersion));
        ArtifactContracts.Hash(Hash, nameof(Hash), ContractVersion.StartsWith("sha256-", StringComparison.Ordinal));
    }

    public static ArtifactContentIdentity Sha256Bytes(string hash) => new(Sha256BytesV1, hash);
}

public readonly record struct CandidateId
{
    public CandidateId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("A candidate identifier cannot be empty.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public void Validate() => ArtifactContracts.RequireGuid(Value, nameof(Value));
    public override string ToString() => Value.ToString("D");
}

public readonly record struct DelegationResultId
{
    public DelegationResultId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("A result identifier cannot be empty.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public void Validate() => ArtifactContracts.RequireGuid(Value, nameof(Value));
    public override string ToString() => Value.ToString("D");
}

/// <summary>Immutable candidate revision containing references, never payloads.</summary>
public sealed record CandidateRevisionReference
{
    public CandidateRevisionReference(
        DelegationId delegationId,
        StructuralNodeReference structuralNode,
        NodeGenerationId nodeGeneration,
        CandidateId candidateId,
        int revision,
        ArtifactContentIdentity contentIdentity,
        IReadOnlyList<DelegationArtifactReference> artifacts)
    {
        ArtifactContracts.RequireDelegation(delegationId, nameof(delegationId));
        structuralNode.Validate();
        nodeGeneration.Validate();
        candidateId.Validate();
        CandidateId = candidateId;
        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        contentIdentity.Validate();
        Artifacts = ArtifactContracts.Snapshot(artifacts, nameof(artifacts));
        if (Artifacts.Count == 0)
        {
            throw new ArgumentException("A candidate revision must reference at least one artifact.", nameof(artifacts));
        }

        var identities = new HashSet<ArtifactContracts.ArtifactIdentityKey>();
        foreach (var artifact in Artifacts)
        {
            ArtifactContracts.ValidateArtifact(artifact, delegationId, structuralNode, nodeGeneration);
            if (!identities.Add(ArtifactContracts.IdentityKey(artifact)))
            {
                throw new ArgumentException("A candidate revision cannot contain duplicate artifact identities.", nameof(artifacts));
            }
        }

        DelegationId = delegationId;
        StructuralNode = structuralNode;
        NodeGeneration = nodeGeneration;
        Revision = revision;
        ContentIdentity = contentIdentity;
    }

    public DelegationId DelegationId { get; }
    public StructuralNodeReference StructuralNode { get; }
    public NodeGenerationId NodeGeneration { get; }
    public CandidateId CandidateId { get; }
    public int Revision { get; }
    public ArtifactContentIdentity ContentIdentity { get; }
    public IReadOnlyList<DelegationArtifactReference> Artifacts { get; }
}

/// <summary>Aggregate result identity that references a candidate and artifacts.</summary>
public sealed record DelegationResultReference
{
    public DelegationResultReference(
        DelegationId delegationId,
        DelegationResultId resultId,
        CandidateRevisionReference candidate,
        IReadOnlyList<DelegationArtifactReference> artifacts)
    {
        ArtifactContracts.RequireDelegation(delegationId, nameof(delegationId));
        resultId.Validate();
        ResultId = resultId;
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.DelegationId != delegationId)
        {
            throw new ArgumentException("The candidate must belong to the result delegation.", nameof(candidate));
        }

        Candidate = candidate;
        Artifacts = ArtifactContracts.Snapshot(artifacts, nameof(artifacts));
        var identities = new HashSet<ArtifactContracts.ArtifactIdentityKey>();
        foreach (var artifact in Artifacts)
        {
            ArtifactContracts.ValidateArtifact(artifact, delegationId);
            if (!identities.Add(ArtifactContracts.IdentityKey(artifact)))
            {
                throw new ArgumentException("A result cannot contain duplicate artifact identities.", nameof(artifacts));
            }
        }

        DelegationId = delegationId;
    }

    public DelegationId DelegationId { get; }
    public DelegationResultId ResultId { get; }
    public CandidateRevisionReference Candidate { get; }
    public IReadOnlyList<DelegationArtifactReference> Artifacts { get; }
}

public sealed record CandidateRevisionPublication(CandidateRevisionReference Candidate, bool IsNew);

public sealed class CandidateRevisionConflictException : InvalidOperationException
{
    public CandidateRevisionConflictException(string message) : base(message)
    {
    }
}

public interface ICandidateRevisionPublicationRegistry
{
    ValueTask<CandidateRevisionPublication> PublishAsync(CandidateRevisionReference candidate, CancellationToken cancellationToken = default);
}

public static class CandidateRevisionIdentity
{
    public static bool SemanticallyEqual(CandidateRevisionReference left, CandidateRevisionReference right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.DelegationId == right.DelegationId
            && left.StructuralNode == right.StructuralNode
            && left.NodeGeneration == right.NodeGeneration
            && left.CandidateId == right.CandidateId
            && left.Revision == right.Revision
            && left.ContentIdentity == right.ContentIdentity
            && left.Artifacts.SequenceEqual(right.Artifacts);
    }
}

internal static class ArtifactContracts
{
    public const int MaximumCollectionItems = 128;

    public static string Identity(string? value, string parameterName, int maximumLength) => IdentityText.Require(value, parameterName, maximumLength);

    public static ArtifactIdentityKey IdentityKey(DelegationArtifactReference artifact) =>
        new(artifact.Provider, artifact.Repository, artifact.ArtifactId);

    public static void RequireDelegation(DelegationId value, string parameterName) => IdentityText.RequireNonEmpty(value.Value, parameterName);
    public static void RequireGuid(Guid value, string parameterName) => IdentityText.RequireNonEmpty(value, parameterName);

    public static string Version(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 32
            || !IsAsciiLowercaseLetterOrDigit(value[0])
            || value.Any(character => !IsAsciiLowercaseLetterOrDigit(character) && character is not ('.' or '-' or '_')))
        {
            throw new ArgumentException("A bounded lowercase ASCII content-hash contract is required.", parameterName);
        }

        return value;
    }

    public static string Hash(string? value, string parameterName, bool sha256)
    {
        if (sha256)
        {
            return IdentityText.RequireSha256(value, parameterName);
        }

        return IdentityText.Require(value, parameterName, 512);
    }

    public static IReadOnlyList<T> Snapshot<T>(IReadOnlyList<T> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > MaximumCollectionItems)
        {
            throw new ArgumentException($"A collection cannot contain more than {MaximumCollectionItems} values.", parameterName);
        }

        return Array.AsReadOnly(values.ToArray());
    }

    public static void ValidateArtifact(DelegationArtifactReference artifact, DelegationId expectedDelegation, StructuralNodeReference? expectedNode = null, NodeGenerationId? expectedGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        artifact.StructuralNode.Validate();
        artifact.NodeGeneration.Validate();
        Identity(artifact.Provider, nameof(artifact.Provider), 256);
        Identity(artifact.Repository, nameof(artifact.Repository), 1_024);
        Identity(artifact.ArtifactId, nameof(artifact.ArtifactId), 1_024);
        Identity(artifact.Kind, nameof(artifact.Kind), 256);
        if (artifact.SchemaVersion < 1) throw new ArgumentOutOfRangeException(nameof(artifact.SchemaVersion));
        Identity(artifact.Location, nameof(artifact.Location), 4_096);
        if (artifact.DelegationId != expectedDelegation) throw new ArgumentException("Artifact ownership does not match the expected delegation.", nameof(artifact));
        if (expectedNode is { } node && artifact.StructuralNode != node) throw new ArgumentException("Artifact ownership does not match the expected structural node.", nameof(artifact));
        if (expectedGeneration is { } generation && artifact.NodeGeneration != generation) throw new ArgumentException("Artifact ownership does not match the expected node generation.", nameof(artifact));
        artifact.ContentIdentity.Validate();
    }

    internal readonly record struct ArtifactIdentityKey(string Provider, string Repository, string ArtifactId);

    private static bool IsAsciiLowercaseLetterOrDigit(char value) => value is >= 'a' and <= 'z' or >= '0' and <= '9';
}
