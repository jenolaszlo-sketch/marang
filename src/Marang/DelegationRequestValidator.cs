namespace Marang;

public static class DelegationRequestValidator
{
    public static void Validate(DelegationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        RequireText(request.RequestKey, nameof(request.RequestKey), 256);
        RequireText(request.Objective, nameof(request.Objective), 16_384);

        ArgumentNullException.ThrowIfNull(request.Workspace);
        RequireText(request.Workspace.Provider, nameof(request.Workspace.Provider), 128);
        RequireText(request.Workspace.Identifier, nameof(request.Workspace.Identifier), 2_048);

        ValidateTextList(request.AcceptanceCriteria, nameof(request.AcceptanceCriteria), required: true);
        ValidateTextList(request.Constraints, nameof(request.Constraints), required: false);

        ArgumentNullException.ThrowIfNull(request.Budget);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Budget.MaximumWorkerCalls, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Budget.MaximumRetries, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Budget.MaximumParallelWorkers, 1);

        if (request.Budget.MaximumDuration is { } duration && duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.Budget.MaximumDuration),
                duration,
                "Maximum duration must be positive when supplied.");
        }

        if (request.Strategy != DelegationStrategy.Implement)
        {
            throw new NotSupportedException(
                $"Delegation strategy '{request.Strategy}' is not supported by the initial runtime.");
        }
    }

    private static void ValidateTextList(
        IReadOnlyList<string>? values,
        string parameterName,
        bool required)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);

        if (required && values.Count == 0)
        {
            throw new ArgumentException("At least one value is required.", parameterName);
        }

        for (var index = 0; index < values.Count; index++)
        {
            RequireText(values[index], $"{parameterName}[{index}]", 4_096);
        }
    }

    private static void RequireText(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        if (value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"The value cannot exceed {maximumLength} characters.",
                parameterName);
        }
    }
}
