using BirkNext.AzureEnvironment;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;

/// <summary>
/// Integration Quality Review's use of the shared OBSERVED evidence (Azure Environment Analysis): configured catalog values looked up in the
/// newest stored Azure snapshot of the Target Environment. Adds observed lines beside configured and declared; it never changes a domain's
/// state, a check's outcome or the catalog — the control plane shows a resource exists, not that the integration works.
/// </summary>
public static class IqrObservedAzureReview
{
    public static IntegrationReviewResult Augment(IntegrationReviewResult result, AzureEnvironmentSnapshot snapshot)
    {
        var comparisons = IqrInfrastructureComparison.CompareObserved(result.ConfigurationSnapshot, snapshot);
        if (comparisons.Count == 0) return result;
        return result with
        {
            ObservedAzureComparisons = comparisons,
            Domains = result.Domains.Select(d =>
            {
                var (observed, missing) = IqrInfrastructureComparison.ObservedDomain(d.Domain, comparisons);
                return observed.Count == 0 ? d : d with { Observed = [.. d.Observed, .. observed], Missing = [.. d.Missing, .. missing] };
            }).ToList(),
            WhatWasTested = [.. result.WhatWasTested,
                $"Observed evidence read: Azure Environment Analysis snapshot of {snapshot.CapturedAt:yyyy-MM-dd HH:mm} UTC ({snapshot.Resources.Count} resources, {snapshot.Status}); not re-queried by this run"],
        };
    }
}
