namespace BirkNext.Web.Models;

/// <summary>
/// Single source of display wording for contract compatibility and drift, shared by the
/// Integration Quality Review page and the export so the two can never diverge.
///
/// Wording is deliberately non-absolute: the review compares contract evidence, it does not
/// certify that an integration works. State is read from the backend verbatim and is never
/// inferred from finding counts.
/// </summary>
public static class ContractStatePresenter
{
    public static string Compatibility(IntegrationStatus status) => status.CompatibilityState switch
    {
        null => "Not captured in this report version",
        ContractCompatibilityStatus.Compatible => "No breaking incompatibility detected",
        ContractCompatibilityStatus.Warning =>
            $"Non-breaking differences ({status.CompatibilityDifferenceCount ?? 0})",
        ContractCompatibilityStatus.Breaking =>
            $"Breaking incompatibility ({status.CompatibilityBreakingCount ?? 0})",
        ContractCompatibilityStatus.NotComparable => "Compatibility not assessed",
        ContractCompatibilityStatus.NotReady => "Compatibility not assessed",
        ContractCompatibilityStatus.Unsupported => "Contract analysis not supported",
        ContractCompatibilityStatus.Error => "Contract comparison could not be completed",
        _ => "Compatibility not assessed"
    };

    public static string CompatibilityDetail(IntegrationStatus status) => status.CompatibilityState switch
    {
        null => "This report was produced before contract comparison was recorded.",
        ContractCompatibilityStatus.NotComparable or ContractCompatibilityStatus.NotReady =>
            string.IsNullOrWhiteSpace(status.CompatibilityReason)
                ? "Producer/consumer contract evidence is incomplete."
                : status.CompatibilityReason!,
        _ => status.CompatibilityReason ?? ""
    };

    public static string Drift(IntegrationStatus status) => status.DriftState switch
    {
        null => "Not captured in this report version",
        ContractDriftState.NoChange => "No contract change detected",
        ContractDriftState.NonBreakingChange =>
            $"Contract changed since previous baseline ({status.DriftDifferenceCount ?? 0})",
        ContractDriftState.BreakingChange =>
            $"{status.DriftDifferenceCount ?? 0} changes since previous baseline, {status.DriftBreakingCount ?? 0} breaking",
        ContractDriftState.BaselineUnavailable => "Baseline unavailable",
        ContractDriftState.NotComparable => "Drift not assessed",
        _ => "Drift not assessed"
    };

    public static string SeverityLabel(ContractDifferenceSeverity severity) => severity switch
    {
        ContractDifferenceSeverity.Breaking => "Breaking",
        ContractDifferenceSeverity.Warning => "Non-breaking",
        _ => "Informational"
    };
}
