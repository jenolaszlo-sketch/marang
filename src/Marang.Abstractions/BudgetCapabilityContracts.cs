namespace Marang;

/// <summary>Bounded, integer-only units understood by the Marang budget contract.</summary>
public enum BudgetQuantityKind
{
    Count = 0,
    Milliseconds = 1,
    Tokens = 2,
    MicroCredits = 3,
    MinorCurrencyUnits = 4,
    TimeSpanTicks = 5,
}

/// <summary>
/// An exact budget quantity. No floating-point value crosses the contract:
/// time is an integer duration unit (milliseconds or exact TimeSpan ticks),
/// token usage is a count, and money is an integer in a declared currency unit
/// (or micro-credits for a provider-neutral estimate).
/// </summary>
public readonly record struct BudgetQuantity
{
    public const long MaximumValue = 9_000_000_000_000_000;

    public BudgetQuantity(BudgetQuantityKind kind, long value, string? currency = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown budget quantity kind.");
        }

        if (value < 0 || value > MaximumValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Budget quantities must be between zero and {MaximumValue}.");
        }

        var requiresCurrency = kind == BudgetQuantityKind.MinorCurrencyUnits;
        if (requiresCurrency != (currency is not null))
        {
            throw new ArgumentException(
                requiresCurrency
                    ? "Minor currency units require a three-letter currency code."
                    : "Only minor currency units accept a currency code.",
                nameof(currency));
        }

        Kind = kind;
        Value = value;
        Currency = currency is null ? null : RequireCurrency(currency);
    }

    public BudgetQuantityKind Kind { get; }
    public long Value { get; }
    public string? Currency { get; }

    public static BudgetQuantity Count(long value) => new(BudgetQuantityKind.Count, value);
    public static BudgetQuantity Milliseconds(long value) => new(BudgetQuantityKind.Milliseconds, value);
    public static BudgetQuantity Tokens(long value) => new(BudgetQuantityKind.Tokens, value);
    public static BudgetQuantity MicroCredits(long value) => new(BudgetQuantityKind.MicroCredits, value);
    public static BudgetQuantity Ticks(long value) => new(BudgetQuantityKind.TimeSpanTicks, value);
    public static BudgetQuantity MinorCurrencyUnits(string currency, long value) =>
        new(BudgetQuantityKind.MinorCurrencyUnits, value, currency);

    public void Validate()
    {
        _ = new BudgetQuantity(Kind, Value, Currency);
    }

    internal BudgetQuantity Add(BudgetQuantity other)
    {
        EnsureCompatible(other);
        var value = checked(Value + other.Value);
        if (value > MaximumValue)
        {
            throw new OverflowException("The accumulated budget quantity exceeds the contract bound.");
        }

        return new BudgetQuantity(Kind, value, Currency);
    }

    internal void EnsureCompatible(BudgetQuantity other)
    {
        if (Kind != other.Kind || !string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new ArgumentException("Budget quantities must use the same kind and currency.", nameof(other));
        }
    }

    private static string RequireCurrency(string value)
    {
        if (value.Length != 3 || value.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ArgumentException("Currency codes must contain exactly three uppercase ASCII letters.", nameof(value));
        }

        return value;
    }
}

/// <summary>One named, version-neutral budget ceiling.</summary>
public sealed record BudgetLimit
{
    public BudgetLimit(string dimension, BudgetQuantity maximum)
    {
        Dimension = ArtifactContracts.Version(dimension, nameof(dimension));
        maximum.Validate();
        if (maximum.Value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum), "A budget limit must be positive.");
        }

        Maximum = maximum;
    }

    public string Dimension { get; }
    public BudgetQuantity Maximum { get; }
}

/// <summary>Immutable versioned ceilings accepted by one delegation.</summary>
public sealed record BudgetDefinition
{
    public const string CurrentVersion = "budget-v1";
    public const int MaximumLimits = 32;

    public BudgetDefinition(string version, IReadOnlyList<BudgetLimit> limits)
    {
        Version = ArtifactContracts.Version(version, nameof(version));
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.Count == 0 || limits.Count > MaximumLimits)
        {
            throw new ArgumentException($"A budget definition must contain 1 to {MaximumLimits} limits.", nameof(limits));
        }

        var copy = limits.ToArray();
        if (copy.Any(limit => limit is null))
        {
            throw new ArgumentException("Budget limits cannot contain null values.", nameof(limits));
        }

        if (copy.Select(limit => limit.Dimension).Distinct(StringComparer.Ordinal).Count() != copy.Length)
        {
            throw new ArgumentException("A budget definition cannot repeat a dimension.", nameof(limits));
        }

        Limits = Array.AsReadOnly(copy);
    }

    public string Version { get; }
    public IReadOnlyList<BudgetLimit> Limits { get; }

    public bool TryGetLimit(string dimension, out BudgetLimit? limit)
    {
        var canonical = ArtifactContracts.Version(dimension, nameof(dimension));
        limit = Limits.FirstOrDefault(candidate => string.Equals(candidate.Dimension, canonical, StringComparison.Ordinal));
        return limit is not null;
    }

    public void Validate()
    {
        _ = new BudgetDefinition(Version, Limits);
    }
}

/// <summary>One exact, positive charge in a consumption receipt.</summary>
public sealed record BudgetCharge
{
    public BudgetCharge(string dimension, BudgetQuantity amount)
    {
        Dimension = ArtifactContracts.Version(dimension, nameof(dimension));
        amount.Validate();
        if (amount.Value == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "A budget charge must be positive.");
        }

        Amount = amount;
    }

    public string Dimension { get; }
    public BudgetQuantity Amount { get; }
}

/// <summary>
/// An immutable idempotency-keyed receipt for one accepted budget charge set.
/// The receipt is evidence; it does not by itself authorize spending.
/// </summary>
public sealed record BudgetConsumptionReceipt
{
    public const int MaximumCharges = 32;

    public BudgetConsumptionReceipt(
        DelegationId delegationId,
        Guid receiptId,
        string definitionVersion,
        long sequence,
        DateTimeOffset recordedAt,
        IReadOnlyList<BudgetCharge> charges)
    {
        ArtifactContracts.RequireDelegation(delegationId, nameof(delegationId));
        ArtifactContracts.RequireGuid(receiptId, nameof(receiptId));
        DefinitionVersion = ArtifactContracts.Version(definitionVersion, nameof(definitionVersion));
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        if (recordedAt == default)
        {
            throw new ArgumentException("A budget receipt must have a recording timestamp.", nameof(recordedAt));
        }

        ArgumentNullException.ThrowIfNull(charges);
        if (charges.Count == 0 || charges.Count > MaximumCharges)
        {
            throw new ArgumentException($"A budget receipt must contain 1 to {MaximumCharges} charges.", nameof(charges));
        }

        var copy = charges.ToArray();
        if (copy.Any(charge => charge is null))
        {
            throw new ArgumentException("Budget receipts cannot contain null charges.", nameof(charges));
        }

        if (copy.Select(charge => charge.Dimension).Distinct(StringComparer.Ordinal).Count() != copy.Length)
        {
            throw new ArgumentException("A budget receipt cannot charge the same dimension twice.", nameof(charges));
        }

        DelegationId = delegationId;
        ReceiptId = receiptId;
        Sequence = sequence;
        RecordedAt = recordedAt;
        Charges = Array.AsReadOnly(copy);
    }

    public DelegationId DelegationId { get; }
    public Guid ReceiptId { get; }
    public string DefinitionVersion { get; }
    public long Sequence { get; }
    public DateTimeOffset RecordedAt { get; }
    public IReadOnlyList<BudgetCharge> Charges { get; }

    public void Validate() => _ = new BudgetConsumptionReceipt(DelegationId, ReceiptId, DefinitionVersion, Sequence, RecordedAt, Charges);
}

/// <summary>Immutable accumulated consumption, suitable for durable replay.</summary>
public sealed record BudgetConsumptionSnapshot
{
    public const int MaximumReceipts = 4_096;

    public BudgetConsumptionSnapshot(
        DelegationId delegationId,
        string definitionVersion,
        long lastSequence,
        IReadOnlyList<BudgetCharge>? charges = null,
        int receiptCount = 0)
    {
        ArtifactContracts.RequireDelegation(delegationId, nameof(delegationId));
        DefinitionVersion = ArtifactContracts.Version(definitionVersion, nameof(definitionVersion));
        ArgumentOutOfRangeException.ThrowIfNegative(lastSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(receiptCount);
        if (receiptCount > MaximumReceipts)
        {
            throw new ArgumentOutOfRangeException(nameof(receiptCount));
        }

        if ((receiptCount == 0 && lastSequence != 0) || (receiptCount > 0 && lastSequence == 0))
        {
            throw new ArgumentException("A consumption snapshot sequence and receipt count must agree.");
        }

        var copy = charges?.ToArray() ?? [];
        if (copy.Length > BudgetDefinition.MaximumLimits || copy.Any(charge => charge is null))
        {
            throw new ArgumentException("A consumption snapshot contains too many or null charges.", nameof(charges));
        }

        if (receiptCount == 0 && copy.Length != 0)
        {
            throw new ArgumentException("An empty consumption snapshot cannot contain charges.", nameof(charges));
        }

        if (copy.Select(charge => charge.Dimension).Distinct(StringComparer.Ordinal).Count() != copy.Length)
        {
            throw new ArgumentException("A consumption snapshot cannot repeat a dimension.", nameof(charges));
        }

        DelegationId = delegationId;
        LastSequence = lastSequence;
        ReceiptCount = receiptCount;
        Charges = Array.AsReadOnly(copy);
    }

    public DelegationId DelegationId { get; }
    public string DefinitionVersion { get; }
    public long LastSequence { get; }
    public int ReceiptCount { get; }
    public IReadOnlyList<BudgetCharge> Charges { get; }

    internal BudgetQuantity? Get(string dimension) => Charges.FirstOrDefault(x => x.Dimension == dimension)?.Amount;

    internal BudgetConsumptionSnapshot Add(BudgetConsumptionReceipt receipt)
    {
        if (receipt.DelegationId != DelegationId)
        {
            throw new ArgumentException("The receipt belongs to a different delegation.", nameof(receipt));
        }

        if (receipt.DefinitionVersion != DefinitionVersion)
        {
            throw new ArgumentException("The receipt uses a different budget definition version.", nameof(receipt));
        }

        if (receipt.Sequence <= LastSequence)
        {
            throw new InvalidOperationException("Budget receipts must be applied in strictly increasing sequence order.");
        }

        if (ReceiptCount == MaximumReceipts)
        {
            throw new InvalidOperationException("The budget receipt history has reached its bound.");
        }

        var totals = Charges.ToDictionary(charge => charge.Dimension, StringComparer.Ordinal);
        foreach (var charge in receipt.Charges)
        {
            if (totals.TryGetValue(charge.Dimension, out var prior))
            {
                prior.Amount.EnsureCompatible(charge.Amount);
                totals[charge.Dimension] = new BudgetCharge(charge.Dimension, prior.Amount.Add(charge.Amount));
            }
            else
            {
                totals.Add(charge.Dimension, charge);
            }
        }

        return new BudgetConsumptionSnapshot(
            DelegationId,
            DefinitionVersion,
            receipt.Sequence,
            totals.Values.OrderBy(charge => charge.Dimension, StringComparer.Ordinal).ToArray(),
            ReceiptCount + 1);
    }

    public void Validate() => _ = new BudgetConsumptionSnapshot(DelegationId, DefinitionVersion, LastSequence, Charges, ReceiptCount);
}

/// <summary>Why a durable budget-exhaustion outcome was recorded.</summary>
public sealed record BudgetExceededOutcome
{
    public BudgetExceededOutcome(
        DelegationId delegationId,
        string definitionVersion,
        BudgetCharge charge,
        BudgetQuantity limit,
        BudgetQuantity consumed,
        Guid triggeringReceiptId,
        string reason,
        DateTimeOffset recordedAt)
    {
        ArtifactContracts.RequireDelegation(delegationId, nameof(delegationId));
        DefinitionVersion = ArtifactContracts.Version(definitionVersion, nameof(definitionVersion));
        Charge = charge ?? throw new ArgumentNullException(nameof(charge));
        Charge.Amount.Validate();
        limit.Validate();
        consumed.Validate();
        Charge.Amount.EnsureCompatible(limit);
        Charge.Amount.EnsureCompatible(consumed);
        limit.EnsureCompatible(consumed);
        if (consumed.Value <= limit.Value)
        {
            throw new ArgumentException("A budget-exceeded outcome requires consumption greater than its limit.", nameof(consumed));
        }

        ArtifactContracts.RequireGuid(triggeringReceiptId, nameof(triggeringReceiptId));
        Reason = IdentityText.RequireProse(reason, nameof(reason), 2_048);
        if (recordedAt == default)
        {
            throw new ArgumentException("A budget-exceeded outcome must have a recording timestamp.", nameof(recordedAt));
        }

        DelegationId = delegationId;
        Limit = limit;
        Consumed = consumed;
        TriggeringReceiptId = triggeringReceiptId;
        RecordedAt = recordedAt;
    }

    public DelegationId DelegationId { get; }
    public string DefinitionVersion { get; }
    public BudgetCharge Charge { get; }
    public BudgetQuantity Limit { get; }
    public BudgetQuantity Consumed { get; }
    public Guid TriggeringReceiptId { get; }
    public string Reason { get; }
    public DateTimeOffset RecordedAt { get; }
}

/// <summary>Pure budget accounting result; persistence owns publication.</summary>
public sealed record BudgetConsumptionDecision(
    BudgetConsumptionSnapshot Snapshot,
    BudgetExceededOutcome? Exceeded)
{
    public bool Accepted => Exceeded is null;
}

public static class BudgetAccounting
{
    public static BudgetConsumptionDecision Apply(
        BudgetDefinition definition,
        BudgetConsumptionSnapshot current,
        BudgetConsumptionReceipt receipt,
        string? exceededReason = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(receipt);
        definition.Validate();
        current.Validate();
        receipt.Validate();
        if (current.DefinitionVersion != definition.Version || receipt.DefinitionVersion != definition.Version)
        {
            throw new ArgumentException("The definition, snapshot, and receipt must use the same version.");
        }

        if (current.DelegationId != receipt.DelegationId)
        {
            throw new ArgumentException("The snapshot and receipt must belong to the same delegation.", nameof(receipt));
        }

        var next = current.Add(receipt);
        BudgetExceededOutcome? exceeded = null;
        foreach (var charge in receipt.Charges)
        {
            if (!definition.TryGetLimit(charge.Dimension, out var limit))
            {
                throw new ArgumentException($"The receipt charges undefined budget dimension '{charge.Dimension}'.", nameof(receipt));
            }

            var consumed = next.Get(charge.Dimension)!.Value;
            if (consumed.Value > limit!.Maximum.Value)
            {
                exceeded = new BudgetExceededOutcome(
                    receipt.DelegationId,
                    definition.Version,
                    charge,
                    limit.Maximum,
                    consumed,
                    receipt.ReceiptId,
                    exceededReason ?? $"Budget dimension '{charge.Dimension}' was exceeded.",
                    receipt.RecordedAt);
                break;
            }
        }

        return new BudgetConsumptionDecision(next, exceeded);
    }

    public static BudgetConsumptionSnapshot Empty(BudgetDefinition definition, DelegationId delegationId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        ArtifactContracts.RequireDelegation(delegationId, nameof(delegationId));
        return new BudgetConsumptionSnapshot(delegationId, definition.Version, 0);
    }
}

/// <summary>Provider/model/profile preferences expressed as open identities.</summary>
public sealed record ProviderHints
{
    public const int MaximumPreferredProviders = 16;
    public const int MaximumPreferredModels = 16;

    public ProviderHints(
        string? provider = null,
        string? model = null,
        string? profile = null,
        IReadOnlyList<string>? preferredProviders = null,
        IReadOnlyList<string>? preferredModels = null)
    {
        Provider = Optional(provider, nameof(provider));
        Model = Optional(model, nameof(model));
        Profile = Optional(profile, nameof(profile));
        PreferredProviders = Names(preferredProviders, MaximumPreferredProviders, nameof(preferredProviders));
        PreferredModels = Names(preferredModels, MaximumPreferredModels, nameof(preferredModels));
    }

    public string? Provider { get; }
    public string? Model { get; }
    public string? Profile { get; }
    public IReadOnlyList<string> PreferredProviders { get; }
    public IReadOnlyList<string> PreferredModels { get; }

    private static string? Optional(string? value, string parameterName) =>
        value is null ? null : IdentityText.Require(value, parameterName, 512);

    private static IReadOnlyList<string> Names(IReadOnlyList<string>? values, int maximum, string parameterName)
    {
        if (values is null || values.Count == 0) return Array.Empty<string>();
        if (values.Count > maximum) throw new ArgumentException($"A provider hint list cannot contain more than {maximum} values.", parameterName);
        var result = values.Select(value => IdentityText.Require(value, parameterName, 512)).ToArray();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw new ArgumentException("Provider hint lists cannot contain duplicate values.", parameterName);
        }

        return Array.AsReadOnly(result);
    }
}

/// <summary>An open, versioned capability advertised by an authorized provider.</summary>
public sealed record CapabilityDescriptor
{
    public const int MaximumAttributes = 32;

    public CapabilityDescriptor(string name, int version, IReadOnlyDictionary<string, string>? attributes = null)
    {
        Name = ArtifactContracts.Version(name, nameof(name));
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        Version = version;
        Attributes = Properties(attributes, nameof(attributes));
    }

    public string Name { get; }
    public int Version { get; }
    public IReadOnlyDictionary<string, string> Attributes { get; }

    private static IReadOnlyDictionary<string, string> Properties(IReadOnlyDictionary<string, string>? values, string parameterName)
    {
        if (values is null || values.Count == 0)
        {
            return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
        }

        if (values.Count > MaximumAttributes) throw new ArgumentException($"A capability cannot contain more than {MaximumAttributes} attributes.", parameterName);
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            var key = ArtifactContracts.Version(pair.Key, parameterName);
            var value = IdentityText.Require(pair.Value, parameterName, 1_024);
            if (!copy.TryAdd(key, value)) throw new ArgumentException("Capability attributes cannot contain duplicate keys.", parameterName);
        }

        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(copy);
    }
}

/// <summary>One open capability requirement used by deterministic matching.</summary>
public sealed record CapabilityRequirement
{
    public CapabilityRequirement(string name, int minimumVersion, IReadOnlyDictionary<string, string>? attributes = null)
    {
        Name = ArtifactContracts.Version(name, nameof(name));
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumVersion, 1);
        MinimumVersion = minimumVersion;
        Attributes = attributes is null
            ? new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal))
            : new CapabilityDescriptor(name, minimumVersion, attributes).Attributes;
    }

    public string Name { get; }
    public int MinimumVersion { get; }
    public IReadOnlyDictionary<string, string> Attributes { get; }
}

/// <summary>Host-authorized provider metadata used by the pure selector.</summary>
public sealed record ProviderDescriptor
{
    public ProviderDescriptor(
        string provider,
        IReadOnlyList<CapabilityDescriptor> capabilities,
        int priority = 0,
        bool enabled = true,
        IReadOnlyList<string>? models = null)
    {
        Provider = IdentityText.Require(provider, nameof(provider), 512);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.Count == 0 || capabilities.Count > 128) throw new ArgumentException("A provider must advertise 1 to 128 capabilities.", nameof(capabilities));
        var copy = capabilities.ToArray();
        if (copy.Any(capability => capability is null) || copy.Select(capability => capability.Name).Distinct(StringComparer.Ordinal).Count() != copy.Length)
        {
            throw new ArgumentException("Provider capabilities must be non-null and unique by name.", nameof(capabilities));
        }

        if (priority is < -1_000_000 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(priority));
        Capabilities = Array.AsReadOnly(copy);
        Priority = priority;
        Enabled = enabled;
        string[] modelCopy = models is null
            ? []
            : models.Select(model => IdentityText.Require(model, nameof(models), 512)).ToArray();
        if (modelCopy.Distinct(StringComparer.Ordinal).Count() != modelCopy.Length)
        {
            throw new ArgumentException("A provider cannot advertise duplicate model identities.", nameof(models));
        }

        Models = Array.AsReadOnly(modelCopy);
        if (Models.Count > 128) throw new ArgumentException("A provider cannot advertise more than 128 models.", nameof(models));
    }

    public string Provider { get; }
    public IReadOnlyList<CapabilityDescriptor> Capabilities { get; }
    public int Priority { get; }
    public bool Enabled { get; }
    public IReadOnlyList<string> Models { get; }
}

public sealed record ProviderSelectionRequest
{
    public ProviderSelectionRequest(IReadOnlyList<CapabilityRequirement> requiredCapabilities, ProviderHints? hints = null)
    {
        ArgumentNullException.ThrowIfNull(requiredCapabilities);
        if (requiredCapabilities.Count == 0 || requiredCapabilities.Count > 32) throw new ArgumentException("A provider request must require 1 to 32 capabilities.", nameof(requiredCapabilities));
        if (requiredCapabilities.Any(requirement => requirement is null) || requiredCapabilities.Select(requirement => requirement.Name).Distinct(StringComparer.Ordinal).Count() != requiredCapabilities.Count)
        {
            throw new ArgumentException("Required capabilities must be non-null and unique by name.", nameof(requiredCapabilities));
        }

        RequiredCapabilities = Array.AsReadOnly(requiredCapabilities.ToArray());
        Hints = hints ?? new ProviderHints();
    }

    public IReadOnlyList<CapabilityRequirement> RequiredCapabilities { get; }
    public ProviderHints Hints { get; }
}

public sealed record ProviderMatch
{
    public ProviderMatch(ProviderDescriptor provider, IReadOnlyList<CapabilityDescriptor> capabilities, int hintScore)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (hintScore < 0) throw new ArgumentOutOfRangeException(nameof(hintScore));
        if (capabilities.Count == 0 || capabilities.Count > 32 || capabilities.Any(capability => capability is null)
            || capabilities.Select(capability => capability.Name).Distinct(StringComparer.Ordinal).Count() != capabilities.Count)
        {
            throw new ArgumentException("A provider match must contain 1 to 32 unique capabilities.", nameof(capabilities));
        }

        Provider = provider;
        Capabilities = Array.AsReadOnly(capabilities.ToArray());
        HintScore = hintScore;
    }

    public ProviderDescriptor Provider { get; }
    public IReadOnlyList<CapabilityDescriptor> Capabilities { get; }
    public int HintScore { get; }
}

/// <summary>Deterministic provider matching; no vendor/model enum is involved.</summary>
public static class ProviderSelection
{
    public static IReadOnlyList<ProviderMatch> Match(
        ProviderSelectionRequest request,
        IReadOnlyList<ProviderDescriptor> providers)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(providers);
        if (providers.Count > 128) throw new ArgumentException("A selection set cannot contain more than 128 providers.", nameof(providers));
        var matches = new List<ProviderMatch>();
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (!provider.Enabled) continue;
            var capabilities = new List<CapabilityDescriptor>();
            var valid = true;
            foreach (var requirement in request.RequiredCapabilities)
            {
                var capability = provider.Capabilities.FirstOrDefault(candidate =>
                    candidate.Name == requirement.Name
                    && candidate.Version >= requirement.MinimumVersion
                    && requirement.Attributes.All(pair => candidate.Attributes.TryGetValue(pair.Key, out var value) && value == pair.Value));
                if (capability is null) { valid = false; break; }
                capabilities.Add(capability);
            }

            if (!valid) continue;
            var hints = request.Hints ?? new ProviderHints();
            var hintScore = 0;
            if (hints.Provider == provider.Provider) hintScore += 1_000;
            var providerIndex = -1;
            for (var index = 0; index < hints.PreferredProviders.Count; index++)
            {
                if (string.Equals(hints.PreferredProviders[index], provider.Provider, StringComparison.Ordinal))
                {
                    providerIndex = index;
                    break;
                }
            }

            if (providerIndex >= 0) hintScore += 100 - providerIndex;
            if (hints.Model is not null && provider.Models.Contains(hints.Model, StringComparer.Ordinal)) hintScore += 100;
            hintScore += hints.PreferredModels.Count(model => provider.Models.Contains(model, StringComparer.Ordinal));
            matches.Add(new ProviderMatch(provider, capabilities, hintScore));
        }

        return matches
            .OrderByDescending(match => match.HintScore)
            .ThenByDescending(match => match.Provider.Priority)
            .ThenBy(match => match.Provider.Provider, StringComparer.Ordinal)
            .Select(match => match)
            .ToArray();
    }

    public static ProviderMatch? Select(ProviderSelectionRequest request, IReadOnlyList<ProviderDescriptor> providers) =>
        Match(request, providers).FirstOrDefault();
}
