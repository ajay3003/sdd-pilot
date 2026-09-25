using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>One row of a domain table: a platform check, one topic's check, or one check shared identically by several topics.</summary>
public sealed record IntegrationCheckRow(string Subject, IntegrationCheck Check, int TopicCount);

public sealed record IntegrationReviewHeadline(string Outcome, int Systems, int Topics, int DomainsAssessed, int DomainsTotal, int Findings, int NotAssessedChecks, string Freshness, string Sources);

/// <summary>
/// Presentation of an Integration Quality Review result. Reads only the result (and its own configuration snapshot), never the
/// current catalog, so a historical run always shows what it reviewed. "Not assessed" is never rendered as zero.
/// </summary>
public static class IntegrationReviewResultPresentation
{
    public static readonly string[] Tabs =
        ["Overview", "Configuration", "Connectivity", "Contracts", "Message flow", "Reliability", "Error handling", "Security", "Observability", "Performance", "Data quality", "Findings"];

    public static IntegrationReviewDomain? DomainOf(string tab) => tab switch
    {
        "Configuration" => IntegrationReviewDomain.Configuration,
        "Connectivity" => IntegrationReviewDomain.Connectivity,
        "Contracts" => IntegrationReviewDomain.Contract,
        "Message flow" => IntegrationReviewDomain.MessageFlow,
        "Reliability" => IntegrationReviewDomain.Reliability,
        "Error handling" => IntegrationReviewDomain.ErrorHandling,
        "Security" => IntegrationReviewDomain.Security,
        "Observability" => IntegrationReviewDomain.Observability,
        "Performance" => IntegrationReviewDomain.Performance,
        "Data quality" => IntegrationReviewDomain.DataQuality,
        _ => null,
    };

    public static IntegrationReviewHeadline Headline(IntegrationReviewResult result)
    {
        var checks = result.AllChecks.ToList();
        return new(IntegrationReviewLabels.Outcome(result.Outcome), result.Systems.Count, result.TopicsReviewed,
            result.Domains.Count(d => d.ChecksAssessed > 0), result.Domains.Count, result.Findings.Count,
            checks.Count(c => !IntegrationReviewLabels.IsAssessed(c.Status)),
            result.Freshness switch
            {
                IntegrationEvidenceFreshness.ConfigurationOnly => "Configuration only — no runtime evidence",
                IntegrationEvidenceFreshness.Historical => "Historical",
                IntegrationEvidenceFreshness.Mixed => "Mixed",
                _ => $"Current (captured {result.CompletedAt:yyyy-MM-dd HH:mm} UTC)",
            },
            string.Join(", ", result.EvidenceSources.Select(IntegrationReviewLabels.Source)));
    }

    /// <summary>
    /// A domain's rows: platform checks once, then topic checks — where every topic of a system has the same check with the same
    /// status, evidence and explanation, one row states it for all of them.
    /// </summary>
    public static IReadOnlyList<IntegrationCheckRow> Rows(IntegrationReviewResult result, IntegrationReviewDomain domain)
    {
        var rows = new List<IntegrationCheckRow>();
        foreach (var system in result.Systems)
        {
            var platformName = result.ConfigurationSnapshot.Platforms.FirstOrDefault(p => p.Id == system.PlatformId)?.Name ?? system.SystemName;
            rows.AddRange(system.PlatformChecks.Where(c => c.Domain == domain).Select(c => new IntegrationCheckRow(platformName, c, 0)));
            var topicChecks = system.Topics.SelectMany(t => t.Checks.Where(c => c.Domain == domain).Select(c => (Topic: t, Check: c))).ToList();
            foreach (var group in topicChecks.GroupBy(x => x.Check.CheckId))
            {
                var items = group.ToList();
                var identical = items.GroupBy(x => (x.Check.Status, x.Check.Evidence, x.Check.Explanation)).ToList();
                if (items.Count > 1 && identical.Count == 1)
                    rows.Add(new IntegrationCheckRow($"All {items.Count} topics · {system.SystemName}", items[0].Check, items.Count));
                else
                    rows.AddRange(items.Select(x => new IntegrationCheckRow(x.Topic.DisplayName, x.Check, 1)));
            }
        }
        return rows;
    }

    public static string StatusTone(IntegrationCheckStatus status) => status switch
    {
        IntegrationCheckStatus.Pass => "pass",
        IntegrationCheckStatus.Fail => "fail",
        IntegrationCheckStatus.Warning => "warning",
        IntegrationCheckStatus.Observed or IntegrationCheckStatus.NoIndicatorsObserved or IntegrationCheckStatus.NoRecentEvidence => "observed",
        IntegrationCheckStatus.NeedsConfirmation => "attention",
        _ => "muted",
    };

    public static string ReadinessTone(IntegrationDomainReadiness readiness) => readiness switch
    {
        IntegrationDomainReadiness.Ready or IntegrationDomainReadiness.Available => "ready",
        IntegrationDomainReadiness.Limited => "attention",
        _ => "muted",
    };

    public static string DomainTone(IntegrationDomainResult domain) =>
        domain.Findings > 0 ? "fail" : domain.StateLabel == "Assessed" ? "pass" : domain.StateLabel == "Partially assessed" ? "attention" : "muted";

    /// <summary>"3 of 16 checks assessed" — never "0 failures" for a domain nothing could assess.</summary>
    public static string Coverage(IntegrationDomainResult domain) =>
        domain.ChecksTotal == 0 ? "No checks" : $"{domain.ChecksAssessed} of {domain.ChecksTotal} checks assessed";
}
