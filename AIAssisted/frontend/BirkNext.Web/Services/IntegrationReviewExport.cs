using System.Text;
using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// HTML export of an Integration Quality Review result, from the result and its own configuration snapshot only. Keeps "Not
/// assessed" with its reason, evidence provenance, limitations and manual follow-up; carries no secret (the catalog has none).
/// </summary>
public static class IntegrationReviewExport
{
    public static string Build(IntegrationReviewResult result, string? projectName, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge, Func<string?, string> esc,
        Func<string, string?, string?, string, string> buildHtml)
    {
        var sb = new StringBuilder();
        var headline = IntegrationReviewResultPresentation.Headline(result);
        sb.Append("<section class=\"block\"><h2>Summary</h2><dl>");
        sb.Append($"<dt>Target environment</dt><dd>{esc(result.EnvironmentName)} ({esc(result.EnvironmentId)})</dd>");
        sb.Append($"<dt>Outcome</dt><dd>{esc(headline.Outcome)}</dd>");
        sb.Append($"<dt>Integration systems reviewed</dt><dd>{headline.Systems}</dd><dt>Topics reviewed</dt><dd>{headline.Topics}</dd>");
        sb.Append($"<dt>Domains assessed</dt><dd>{headline.DomainsAssessed} of {headline.DomainsTotal}</dd><dt>Findings</dt><dd>{headline.Findings}</dd>");
        sb.Append($"<dt>Checks not assessed</dt><dd>{headline.NotAssessedChecks}</dd><dt>Evidence freshness</dt><dd>{esc(headline.Freshness)}</dd>");
        sb.Append($"<dt>Evidence sources</dt><dd>{esc(headline.Sources)}</dd><dt>Completed</dt><dd>{result.CompletedAt:u}</dd></dl></section>\n");

        foreach (var platform in result.ConfigurationSnapshot.Platforms)
        {
            sb.Append($"<section class=\"block\"><h2>Platform · {esc(platform.Name)}</h2><dl>");
            sb.Append($"<dt>Namespace</dt><dd>{esc(platform.Namespace ?? "Not configured")} ({esc(platform.NamespaceFqdn ?? "—")})</dd>");
            sb.Append($"<dt>Resource group</dt><dd>{esc(platform.ResourceGroup ?? "Not configured")}</dd>");
            sb.Append($"<dt>Producer</dt><dd>{esc(platform.ProducerTechnology ?? "Not configured")} · auth {esc(IntegrationConfigurationRules.AuthLabel(platform.ProducerAuthentication))}</dd>");
            sb.Append($"<dt>Default consumer auth</dt><dd>{esc(IntegrationConfigurationRules.AuthLabel(platform.DefaultConsumerAuthentication))}</dd>");
            sb.Append($"<dt>Monitoring</dt><dd>{esc(platform.MonitoringProvider ?? "Not configured")} · dashboard {esc(platform.MonitoringUrl ?? "Not configured")}</dd>");
            sb.Append($"<dt>Technical topics (not reviewed)</dt><dd>{esc(string.Join(", ", platform.TechnicalTopics.Select(t => t.Name)))}</dd></dl></section>\n");
        }

        sb.Append("<section class=\"block\"><h2>Selected topics</h2>");
        sb.Append(table(["Integration", "Topic", "Consumer", "Mapping", "Consumer group", "Configuration"], result.ConfigurationSnapshot.Integrations.Where(i => i.Enabled).Select(i => new[]
        {
            esc(i.DisplayName), esc(i.EndpointOrTopic ?? "Not configured"), esc(i.Consumer.DisplayName ?? "Needs confirmation"),
            esc(IntegrationConfigurationRules.MappingLabel(i.Consumer.MappingState)), esc(i.ConsumerGroup ?? "Unknown / not configured"),
            esc(IntegrationConfigurationRules.Label(IntegrationConfigurationRules.Evaluate(i, result.ConfigurationSnapshot.Platforms.FirstOrDefault(p => p.Id == i.PlatformId)).State)),
        })));
        sb.Append("</section>\n");

        sb.Append("<section class=\"block\"><h2>Domain coverage</h2>");
        sb.Append(table(["Domain", "State", "Coverage", "Findings", "Key limitation"], result.Domains.Select(d => new[]
        {
            esc(IntegrationReviewLabels.Domain(d.Domain)), esc(d.StateLabel), esc(IntegrationReviewResultPresentation.Coverage(d)), d.Findings.ToString(), esc(d.KeyLimitation ?? ""),
        })));
        sb.Append("</section>\n");

        foreach (var domain in result.Domains.Select(d => d.Domain))
        {
            var rows = IntegrationReviewResultPresentation.Rows(result, domain);
            if (rows.Count == 0) continue;
            sb.Append($"<section class=\"block\"><h2>{esc(IntegrationReviewLabels.Domain(domain))} checks</h2>");
            sb.Append(table(["Subject", "Check", "Status", "Evidence", "Explanation", "Provenance"], rows.Select(r => new[]
            {
                esc(r.Subject), esc(r.Check.Title), badge(IntegrationReviewLabels.Status(r.Check.Status)), esc(r.Check.Evidence), esc(r.Check.Explanation),
                esc($"{IntegrationReviewLabels.Source(r.Check.Provenance)} · {r.Check.CapturedAt:u}"),
            })));
            sb.Append("</section>\n");
        }

        sb.Append("<section class=\"block\"><h2>Findings</h2>");
        sb.Append(result.Findings.Count == 0 ? "<p>No findings on the assessed checks. Not-assessed checks are listed above, never as passes.</p>" :
            table(["Severity", "Domain", "Subject", "Finding", "Evidence", "Recommendation", "Affected"], result.Findings.Select(f => new[]
            {
                badge(f.Severity.ToString()), esc(IntegrationReviewLabels.Domain(f.Domain)), esc(f.Subject), esc(f.Title), esc(string.Join("; ", f.Evidence)), esc(f.Recommendation),
                f.AffectedIntegrations.Count.ToString(),
            })));
        sb.Append("</section>\n");

        sb.Append("<section class=\"block\"><h2>Manual follow-up</h2><ul>");
        foreach (var item in result.ManualFollowUp) sb.Append($"<li><strong>{esc(item.Title)}</strong> ({item.AffectedCount}) — {esc(item.Detail)}</li>");
        sb.Append("</ul></section>\n<section class=\"block\"><h2>Limitations</h2><ul>");
        foreach (var limitation in result.Limitations) sb.Append($"<li>{esc(limitation)}</li>");
        sb.Append("</ul></section>\n");

        return buildHtml("Integration Quality Review", projectName, $"Environment: {result.EnvironmentName}  Completed: {result.CompletedAt:yyyy-MM-dd HH:mm} UTC", sb.ToString());
    }
}
