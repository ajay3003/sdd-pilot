using System.Globalization;
using System.Text;
using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// HTML export of one message verification, built from its stored (sanitized) record through the shared report helpers: overview, journey
/// by system, assertions, correlations, evidence and what is missing. No payload, credential or personal identifier is part of the record.
/// </summary>
public static class MessageVerificationExport
{
    public static string Build(IntegrationJourneyRun run, string? projectName, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge,
        Func<string?, string> esc, Func<string, string?, string?, string, string> buildHtml)
    {
        var record = run.MessageVerification;
        var sb = new StringBuilder();
        if (record is null)
        {
            sb.Append("<section class=\"block\"><h2>Message verification</h2><p>This run is not a message verification.</p></section>");
            return buildHtml("Message verification", projectName, $"Environment: {run.EnvironmentId}", sb.ToString());
        }
        string At(DateTimeOffset? at) => at is { } value ? value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" : "—";

        sb.Append($"<section class=\"block\"><h2>{esc(record.TargetLabel)}</h2>");
        sb.Append(table(["Fact", "Value"], new[]
        {
            new[] { "Expected outcome", record.ExpectedScenarioName }, ["Verification run", run.RunId.ToString("N")[..8]], ["Environment", run.EnvironmentId],
            ["Evidence collected", At(record.EvaluatedAt)], ["Observation window", $"{record.WindowHours} h"], ["Rule set", record.RuleVersion],
        }.Select(row => row.Select(esc).ToArray())));
        sb.Append($"<p>Overall verification: {badge(MessageVerificationPresentation.Verdict(record.Verdict))} — {esc(record.VerdictReason)}</p>");
        sb.Append("<p>Evidence stages are not verdicts. HTTP 200 (Mottatt) is receipt, never a business pass, and missing evidence is never a failure by itself.</p></section>\n");

        sb.Append("<section class=\"block\"><h2>Processing journey</h2>");
        sb.Append(table(["System", "Evidence stage", "What was observed", "When", "Correlation", "Limitation"], record.Observations.Select(item => new[]
        {
            esc(item.System), esc(MessageVerificationPresentation.Stage(item.Stage)), esc(item.Summary), esc(At(item.At)), esc(item.CorrelationMethod), esc(item.Limitation),
        })));
        sb.Append("</section>\n<section class=\"block\"><h2>Test assertions</h2>");
        sb.Append(table(["Assertion", "Expected", "Observed", "Result", "Evidence basis"], record.Assertions.Select(item => new[]
        {
            esc(item.Label), esc(item.Expected), esc(item.Observed), badge(MessageVerificationPresentation.Verdict(item.Verdict)), esc(item.EvidenceBasis),
        })));
        sb.Append("</section>\n<section class=\"block\"><h2>Correlation</h2>");
        sb.Append(table(["From", "To", "Matching rule", "Certainty", "Provider", "Limitation"], record.Correlations.Select(item => new[]
        {
            esc($"{item.SourceType}: {item.SourceValue}"), esc($"{item.TargetType}: {item.TargetValue ?? "—"}"), esc(item.MatchingRule),
            esc(MessageVerificationPresentation.Certainty(item.Certainty)), esc(item.ProviderId), esc(item.Limitation),
        })));
        sb.Append("</section>\n<section class=\"block\"><h2>Evidence providers</h2>");
        sb.Append(table(["Provider", "System", "State", "Detail", "Basis", "Read at"], record.Providers.Select(item => new[]
        {
            esc(item.DisplayName), esc(item.System), esc(MessageVerificationPresentation.Provider(item.State)), esc(item.Detail), esc(item.EvidenceBasis), esc(At(item.CapturedAt)),
        })));
        sb.Append("</section>\n<section class=\"block\"><h2>Missing evidence</h2>");
        sb.Append(table(["What", "Why", "Required", "Responsible team", "Next action"], record.MissingEvidence.Select(item => new[]
        {
            esc(item.What), esc(item.Why), esc(item.Required), esc(item.ResponsibleTeam), esc(item.NextAction),
        })));
        sb.Append("</section>\n");
        return buildHtml($"Message verification — {record.TargetLabel}", projectName,
            $"Environment: {run.EnvironmentId} · Read-only evidence; no payloads, credentials or personal data are included.", sb.ToString());
    }
}
