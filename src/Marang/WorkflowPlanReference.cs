namespace Marang;

/// <summary>Identifies the kind of plan revision selected for a delegation.</summary>
public enum WorkflowPlanReferenceKind
{
    /// <summary>
    /// Identifies the BuiltInPreset enum value.
    /// </summary>
    BuiltInPreset = 0,
    /// <summary>
    /// Identifies the FuwenDefinition enum value.
    /// </summary>
    FuwenDefinition = 1,
}

/// <summary>
/// A workflow plan selection identity. The value does not prove authorization,
/// verification, or binding; the host policy/resolver must establish those
/// properties before execution. This is not a Fuwen runtime type.
/// </summary>
public sealed record WorkflowPlanRevisionReference
{
    /// <summary>
    /// Initializes a new instance of the WorkflowPlanRevisionReference type.
    /// </summary>
    public WorkflowPlanRevisionReference(
        WorkflowPlanReferenceKind kind,
        string identifier,
        string revision,
        string? canonicalFingerprint)
    {
        Kind = kind;
        Identifier = PlanIdentityText.Require(identifier, nameof(identifier), 512);
        Revision = PlanIdentityText.Require(revision, nameof(revision), 256);
        CanonicalFingerprint = canonicalFingerprint is null
            ? null
            : PlanIdentityText.RequireSha256(canonicalFingerprint, nameof(canonicalFingerprint));

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

    /// <summary>
    /// Gets the Kind value.
    /// </summary>
    public WorkflowPlanReferenceKind Kind { get; }
    /// <summary>
    /// Gets the Identifier value.
    /// </summary>
    public string Identifier { get; }
    /// <summary>
    /// Gets the Revision value.
    /// </summary>
    public string Revision { get; }
    /// <summary>
    /// Gets the CanonicalFingerprint value.
    /// </summary>
    public string? CanonicalFingerprint { get; }

    /// <summary>
    /// Performs the BuiltInPreset contract operation.
    /// </summary>
    public static WorkflowPlanRevisionReference BuiltInPreset(string identifier, string version) =>
        new(WorkflowPlanReferenceKind.BuiltInPreset, identifier, version, null);

    /// <summary>
    /// Performs the FuwenDefinition contract operation.
    /// </summary>
    public static WorkflowPlanRevisionReference FuwenDefinition(
        string definitionIdentifier,
        string revision,
        string canonicalFingerprint) =>
        new(WorkflowPlanReferenceKind.FuwenDefinition, definitionIdentifier, revision, canonicalFingerprint);

    /// <summary>
    /// Validates this contract value and throws when an invariant is violated.
    /// </summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown workflow plan reference kind.");
        }

        PlanIdentityText.Require(Identifier, nameof(Identifier), 512);
        PlanIdentityText.Require(Revision, nameof(Revision), 256);
        if (Kind == WorkflowPlanReferenceKind.FuwenDefinition)
        {
            PlanIdentityText.RequireSha256(CanonicalFingerprint, nameof(CanonicalFingerprint));
        }
        else if (CanonicalFingerprint is not null)
        {
            throw new ArgumentException("A built-in preset does not accept a content fingerprint.", nameof(CanonicalFingerprint));
        }
    }
}

internal static class PlanIdentityText
{
    internal static string Require(string? value, string parameterName, int maximumLength)
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

    internal static string RequireSha256(string? value, string parameterName)
    {
        Require(value, parameterName, 64);
        if (value!.Length != 64 || value.Any(character => !Uri.IsHexDigit(character) || char.IsUpper(character)))
        {
            throw new ArgumentException("A canonical SHA-256 fingerprint must contain 64 lowercase hexadecimal characters.", parameterName);
        }

        return value;
    }
}
