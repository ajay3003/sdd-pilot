using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;

/// <summary>Source evidence augments domains without converting it into formal-schema validation or runtime Pass.</summary>
public static class IqrSourceReview
{
    private static int EvidenceCount(IntegrationReviewDomain domain, IReadOnlyList<IqrSourceSnapshot> snapshots) =>
        Rules(domain, snapshots).Count + (domain == IntegrationReviewDomain.Security ? snapshots.Sum(s => s.Dataflows.Count) : 0) + IqrPathReview.EvidenceCount(domain, snapshots)
        + IqrObservabilityReview.EvidenceCount(domain, snapshots) + IqrSourceDomainsReview.EvidenceCount(domain, snapshots);
    private static List<ImplementationRule> Rules(IntegrationReviewDomain domain, IEnumerable<IqrSourceSnapshot> snapshots) => snapshots.SelectMany(s => s.Rules)
        .Where(r => domain switch
        {
            IntegrationReviewDomain.Contract or IntegrationReviewDomain.DataQuality => r.Kind is "Field" or "Envelope/model" or "Validation",
            IntegrationReviewDomain.MessageFlow => r.Kind is "Route" or "Operation",
            IntegrationReviewDomain.Reliability => r.Kind is "Reliability" or "Checkpoint",
            IntegrationReviewDomain.ErrorHandling => r.Kind is "ErrorHandling" or "Checkpoint",
            IntegrationReviewDomain.Security => r.Kind == "Security",
            _ => false
        }).ToList();

    public static IntegrationReviewReadiness Augment(IntegrationReviewReadiness readiness, IReadOnlyList<IqrSourceSnapshot> snapshots) => readiness with
    {
        SourceSnapshots = snapshots.ToList(),
        Domains = readiness.Domains.Select(d => EvidenceCount(d.Domain, snapshots) == 0 ? d : d with
        {
            Readiness = d.Readiness == IntegrationDomainReadiness.NotAssessable ? IntegrationDomainReadiness.Partial : d.Readiness,
            Available = [.. d.Available ?? [], "Implementation source evidence available; runtime behavior not established",
                .. IqrPathReview.EvidenceCount(d.Domain, snapshots) > 0 ? new[] { $"Integration path from source: {snapshots.Sum(s => s.IntegrationPath?.Hops.Count ?? 0)} hop(s), {snapshots.Sum(s => s.IntegrationPath?.Fields.Count ?? 0)} field trace(s)" } : [],
                .. IqrObservabilityReview.EvidenceCount(d.Domain, snapshots) > 0 && IqrObservabilityReview.Available(snapshots) is { } observability ? new[] { observability } : [],
                .. IqrSourceDomainsReview.EvidenceCount(d.Domain, snapshots) > 0 && IqrSourceDomainsReview.Available(snapshots) is { } domains ? new[] { domains } : []],
            Missing = [.. d.Missing ?? [], "Source/test relationship may be partial; developer test execution unavailable"]
        }).ToList()
    };

    public static IntegrationReviewResult Augment(IntegrationReviewResult result, IReadOnlyList<IqrSourceSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return result;
        var routes = snapshots.Select(s =>
        {
            var configured = result.ConfigurationSnapshot.Integrations.FirstOrDefault(i => i.Id == s.IntegrationId);
            var tables = s.Rules.Where(r => r.Kind == "Route" && r.Table is not null).Select(r => r.Table!).Distinct().ToList();
            var expected = configured?.SourceResource;
            var comparison = string.IsNullOrWhiteSpace(expected) ? "Configured source table unknown" : tables.Contains(expected) || tables.Contains(expected.Split('.').Last())
                ? "Configured source table has a matching source branch"
                : "Configured source table not found in resolved routes; partial analysis may miss routes, so drift is not confirmed";
            return $"Integration {s.IntegrationId}: configured source {expected ?? "Unknown"}; source tables {string.Join(", ", tables)}. {comparison}. Runtime application processing and deployment/source correlation not established";
        }).ToList();
        return result with
        {
            SourceSnapshots = snapshots.ToList(),
            Domains = result.Domains.Select(d =>
            {
                var count = EvidenceCount(d.Domain, snapshots);
                var (pathObserved, pathMissing) = IqrPathReview.Domain(d.Domain, snapshots, result);
                var (observabilityObserved, observabilityMissing) = IqrObservabilityReview.Domain(d.Domain, snapshots);
                var (domainsObserved, domainsMissing) = IqrSourceDomainsReview.Domain(d.Domain, snapshots);
                return count == 0 ? d : d with
                {
                    // Source evidence never makes a domain fully assessed: runtime evidence is still missing.
                    StateLabel = d.StateLabel is "Not assessed" or "Assessed" ? "Partially assessed" : d.StateLabel,
                    Observed = [.. d.Observed, $"Source inspected: {count} implementation evidence item(s); source-defined behavior only", .. d.Domain == IntegrationReviewDomain.MessageFlow ? routes : [], .. pathObserved, .. observabilityObserved, .. domainsObserved],
                    Missing = [.. d.Missing, .. pathMissing, .. observabilityMissing, .. domainsMissing, d.Domain switch
                    {
                        IntegrationReviewDomain.Contract => "Formal schema and runtime compatibility are independent of the implementation contract",
                        IntegrationReviewDomain.DataQuality => "Mapper structure does not establish business data quality",
                        IntegrationReviewDomain.Security => "Guard presence/unit tests do not establish raw payload wiring or runtime protection",
                        IntegrationReviewDomain.MessageFlow => "Source routes associated with configured integration; source/deployment correlation not established",
                        _ => "Source-defined handling/checkpoint calls do not prove observed runtime resilience"
                    }]
                };
            }).ToList(),
            WhatWasTested = [.. result.WhatWasTested, $"Source inspected: {snapshots.Count} immutable snapshot(s); developer tests discovered: {snapshots.Sum(s => s.Tests.Count)} (not executed)",
                .. snapshots.SelectMany(s => s.IntegrationPath?.Inspected ?? []).Distinct().Select(i => $"Source inspected: {i}")],
            WhatWasNotAssessed = [.. result.WhatWasNotAssessed, "Developer test execution results unavailable", "Runtime malformed-message and checkpoint resilience not executed", "End-to-end processing not exercised", "Deployment/source correlation not established",
                .. snapshots.Any(s => s.IntegrationPath is not null) ? new[]
                {
                    "Deployed adapter and ingestion runtime not invoked by source analysis",
                    "No Event Hub event sent and no Service Bus message sent or received",
                    "No subscriber invoked; subscriber consumption not assessed",
                    "No real personal data inspected (field names and contracts only)",
                } : []],
            Limitations = [.. result.Limitations, .. snapshots.SelectMany(s => s.Limitations).Distinct()],
            EvidenceSources = result.EvidenceSources.Append(IntegrationEvidenceSource.SourceCode).Distinct().ToList()
        };
    }
}
