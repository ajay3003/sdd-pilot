using BirkNext.Api.Services.IntegrationQuality;

namespace BirkNext.Api.Services.ContractAnalysis;

/// <summary>
/// Phase 3 Checkpoint 4: maps contract compatibility and drift results onto the existing
/// integration finding model. Findings are raised only for deterministic evidence; a comparison
/// that did not occur produces no finding rather than a failure.
///
/// Severity mapping:
///   compatibility Breaking          -> High
///   compatibility Warning           -> Low
///   compatibility Compatible        -> no finding
///   compatibility NotComparable     -> no finding
///   drift BreakingChange            -> High
///   drift NonBreakingChange         -> Info
///   drift NoChange                  -> no finding
///   drift BaselineUnavailable       -> no finding
///   drift NotComparable             -> no finding
/// </summary>
public static class ContractFindingMapper
{
    public static IntegrationFinding? ForCompatibility(
        ContractCompatibilityResult result,
        string integrationId,
        string integrationName)
    {
        if (result.Status is ContractCompatibilityStatus.Compatible
            or ContractCompatibilityStatus.NotComparable
            or ContractCompatibilityStatus.NotReady
            or ContractCompatibilityStatus.Unsupported
            or ContractCompatibilityStatus.Error)
            return null;

        var breaking = result.Differences
            .Where(d => d.Severity == ContractDifferenceSeverity.Breaking)
            .ToList();

        if (breaking.Count > 0)
            return new IntegrationFinding
            {
                Id = $"contract-compat-{integrationId}",
                IntegrationId = integrationId,
                IntegrationName = integrationName,
                Title = $"Producer does not satisfy consumer contract ({breaking.Count} breaking)",
                Description =
                    $"Comparing producer '{result.Producer}' against consumer '{result.Consumer}' for contract "
                    + $"'{result.Contract}' found {breaking.Count} breaking incompatibility/ies.",
                Recommendation =
                    "Align the producer contract with what the consumer expects, or coordinate a versioned rollout before deploying.",
                Severity = IntegrationFindingSeverity.High,
                Evidence = Evidence(breaking)
            };

        if (result.Differences.Count == 0)
            return null;

        return new IntegrationFinding
        {
            Id = $"contract-compat-{integrationId}",
            IntegrationId = integrationId,
            IntegrationName = integrationName,
            Title = $"Non-breaking contract differences ({result.Differences.Count})",
            Description =
                $"Producer '{result.Producer}' and consumer '{result.Consumer}' differ for contract "
                + $"'{result.Contract}', but no difference is breaking.",
            Recommendation = "Review the differences to confirm they are intentional.",
            Severity = IntegrationFindingSeverity.Low,
            Evidence = Evidence(result.Differences)
        };
    }

    public static IntegrationFinding? ForDrift(
        ContractDriftResult result,
        string integrationId,
        string integrationName)
    {
        switch (result.State)
        {
            case ContractDriftState.BreakingChange:
            {
                var breaking = result.Differences
                    .Where(d => d.Severity == ContractDifferenceSeverity.Breaking)
                    .ToList();

                return new IntegrationFinding
                {
                    Id = $"contract-drift-{integrationId}",
                    IntegrationId = integrationId,
                    IntegrationName = integrationName,
                    Title = $"Breaking contract change since previous baseline ({breaking.Count})",
                    Description =
                        $"Contract '{result.Contract}' changed in a breaking way since the previous baseline"
                        + (result.BaselineCapturedAt.HasValue
                            ? $" captured {result.BaselineCapturedAt.Value:yyyy-MM-dd HH:mm} UTC."
                            : "."),
                    Recommendation =
                        "Confirm the change is intended and that consumers have been migrated before release.",
                    Severity = IntegrationFindingSeverity.High,
                    Evidence = Evidence(breaking)
                };
            }

            case ContractDriftState.NonBreakingChange:
                return new IntegrationFinding
                {
                    Id = $"contract-drift-{integrationId}",
                    IntegrationId = integrationId,
                    IntegrationName = integrationName,
                    Title = $"Contract changed since previous baseline ({result.Differences.Count})",
                    Description =
                        $"Contract '{result.Contract}' changed since the previous baseline. "
                        + "No change is breaking.",
                    Recommendation = "No action required. Recorded for traceability.",
                    Severity = IntegrationFindingSeverity.Info,
                    Evidence = Evidence(result.Differences)
                };

            default:
                // NoChange, BaselineUnavailable and NotComparable are not findings.
                return null;
        }
    }

    /// <summary>
    /// Difference explanations carry schema and property names only. Producer/consumer source
    /// locations are redacted upstream by the comparer and are deliberately not repeated here.
    /// </summary>
    private static List<string> Evidence(IEnumerable<ContractDifference> differences) =>
        differences
            .OrderBy(d => d.Path, StringComparer.Ordinal)
            .ThenBy(d => d.Property, StringComparer.Ordinal)
            .Select(d => d.Explanation)
            .ToList();
}
