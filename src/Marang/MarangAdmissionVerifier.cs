using Penghou.Qingniao;

namespace Marang;

/// <summary>
/// Marang host admission policy for the Qingniao runtime. Requests without
/// an external fence are admitted. A fence must name a preset registered in
/// Marang's plan catalog (currently only Implement/1); anything else is
/// unknown to this host. Caller authorization beyond scope validity lives
/// with the hosting boundary.
/// </summary>
public sealed class MarangAdmissionVerifier : IDelegationAdmissionVerifier
{
    private readonly IWorkflowPlanCatalog catalog = new InMemoryWorkflowPlanCatalog();

    /// <summary>Admits the exact caller and request against Marang's presets.</summary>
    public DelegationAdmissionDecision Verify(DelegationAdmissionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var fence = context.Request.AdmissionFence;
        if (fence is null)
        {
            return DelegationAdmissionDecision.Admitted();
        }

        WorkflowPlanRevisionReference reference;
        try
        {
            reference = new WorkflowPlanRevisionReference(
                WorkflowPlanReferenceKind.BuiltInPreset,
                fence.Identifier,
                fence.Revision,
                fence.Fingerprint);
        }
        catch (ArgumentException exception)
        {
            return DelegationAdmissionDecision.Reject(
                DelegationAdmissionStatus.Unknown,
                $"The admission fence is not a Marang preset reference: {exception.Message}");
        }

        if (reference.Kind != WorkflowPlanReferenceKind.BuiltInPreset
            || !string.Equals(fence.Kind, "marang-preset", StringComparison.Ordinal))
        {
            return DelegationAdmissionDecision.Reject(
                DelegationAdmissionStatus.Unknown,
                $"Unknown admission fence kind '{fence.Kind}'.");
        }

        return catalog.TryGet(reference, out var definition) && definition is not null
            ? DelegationAdmissionDecision.Admitted()
            : DelegationAdmissionDecision.Reject(
                DelegationAdmissionStatus.Unknown,
                $"Unknown preset '{reference.Identifier}' at revision '{reference.Revision}'.");
    }
}
