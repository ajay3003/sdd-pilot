using System.Globalization;

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

    /// <summary>
    /// One-line history summary for the review. Reads backend state verbatim; the change count
    /// is never derived from findings.
    /// </summary>
    public static string History(IntegrationQualityReport report)
    {
        if (!report.BaselineAvailable)
            return "No previous baseline";

        // Invariant culture: report wording must not vary with the host machine locale.
        var when = report.PreviousSnapshotCapturedAt?.UtcDateTime
            .ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) ?? "unknown date";

        return report.HistoricalChangeCount == 0
            ? $"Previous review: {when} · no changes since"
            : $"Previous review: {when} · {report.HistoricalChangeCount} change{(report.HistoricalChangeCount == 1 ? "" : "s")} since";
    }

    /// <summary>
    /// Whether this review could be recorded for future comparison. A failure is stated plainly
    /// rather than implied to have succeeded.
    /// </summary>
    public static string? HistoryPersistenceNote(IntegrationQualityReport report) =>
        report.SnapshotPersistenceState switch
        {
            SnapshotPersistenceState.Failed =>
                "This review could not be recorded, so future reviews will not be able to compare against it.",
            SnapshotPersistenceState.SkippedIncompleteReview =>
                "This review was not recorded as a baseline because it assessed no integrations.",
            _ => null
        };

    public static string HistoricalChangeLabel(IntegrationHistoricalChangeType type) => type switch
    {
        IntegrationHistoricalChangeType.IntegrationAdded => "Integration added",
        IntegrationHistoricalChangeType.IntegrationRemoved => "Integration removed",
        IntegrationHistoricalChangeType.ProducerChanged => "Producer changed",
        IntegrationHistoricalChangeType.ConsumerChanged => "Consumer changed",
        IntegrationHistoricalChangeType.RelationshipSourceChanged => "Relationship source changed",
        IntegrationHistoricalChangeType.AuthenticationRequiredChanged => "Authentication requirement changed",
        IntegrationHistoricalChangeType.AuthenticatedCapabilityChanged => "Authenticated capability changed",
        IntegrationHistoricalChangeType.RuntimeEvidenceStateChanged => "Runtime evidence changed",
        _ => "Changed"
    };
}
