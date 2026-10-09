using System.Text;
using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// HTML export of one journey pack for one environment: journeys, per-step evidence status, prerequisites, scenarios, architecture rule
/// findings and run history. Built only from readiness/run contracts, which carry no payload, credential, token or personal data.
/// </summary>
public static class IntegrationJourneyExport
{
    public static string Build(IntegrationJourneyPackView pack, ArchitectureRuleReport? rules, IReadOnlyList<IntegrationJourneyRunSummary> history, string? projectName,
        Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge, Func<string?, string> esc, Func<string, string?, string?, string, string> buildHtml)
    {
        var sb = new StringBuilder();
        sb.Append($"<section class=\"block\"><h2>{esc(pack.DisplayName)}</h2><p>{esc(pack.Description)}</p>");
        sb.Append(table(["Fact", "Value"], new[]
        {
            new[] { "Environment", pack.EnvironmentId }, ["Environment trust", pack.EnvironmentDetail], ["Pack", $"{pack.PackId} v{pack.PackVersion}"],
        }.Select(row => row.Select(esc).ToArray())));
        sb.Append(table(["Journey", "Configuration", "Source evidence", "Execution readiness", "Last run", "Verification"], pack.Journeys.Select(view => new[]
        {
            esc(view.Journey.DisplayName), esc(IntegrationJourneyPresentation.Configuration(view)), esc(IntegrationJourneyPresentation.Source(view)),
            badge(IntegrationJourneyPresentation.Readiness(view.Readiness)), esc(view.LastRun is { } last ? $"{last.StartedAt:yyyy-MM-dd HH:mm} UTC" : "No run"),
            esc(IntegrationJourneyPresentation.Verification(view)),
        })));
        sb.Append("<p>Configured ≠ source verified ≠ executable ≠ runtime verified. Accepted or observed is never a pass; a journey is Completed only when every mandatory step is verified.</p></section>\n");

        foreach (var view in pack.Journeys)
        {
            sb.Append($"<section class=\"block\"><h2>{esc(view.Journey.DisplayName)}</h2><p>{esc(view.Journey.Description)}</p>");
            sb.Append($"<p>Readiness: {badge(IntegrationJourneyPresentation.Readiness(view.Readiness))} — {esc(view.ReadinessSummary)} {esc(view.Maturity.Detail)}</p>");
            var status = view.StepStatus.ToDictionary(item => item.StepId);
            sb.Append(table(["Step", "Owner", "Boundary", "Configured", "Evidence source", "State", "Limitation"], view.Journey.Steps.Select(step =>
            {
                var current = status.GetValueOrDefault(step.StepId);
                return new[]
                {
                    esc(step.Label), esc(step.Owner), esc(IntegrationJourneyPresentation.Kind(step.Kind)), esc(current?.Configured == true ? "Yes" : "No"),
                    esc(current?.Observable == true ? "Available" : "None"), badge(IntegrationJourneyPresentation.Step(current?.State ?? JourneyStepState.NotAssessed)), esc(current?.Limitation),
                };
            })));
            sb.Append(table(["Prerequisite", "Category", "State", "Detail"], view.Prerequisites.Select(item => new[]
            {
                esc(item.Label), esc(IntegrationJourneyPresentation.Category(item.Category)), badge(IntegrationJourneyPresentation.Readiness(item.State)), esc(item.Detail),
            })));
            sb.Append(table(["Scenario", "Support", "Reason"], view.Journey.Scenarios.Select(scenario => new[]
            {
                esc(scenario.DisplayName), esc(scenario.Support.ToString()), esc(scenario.NotAssessedBecause ?? scenario.Description),
            })));
            if (view.Journey.Limitations.Count > 0) sb.Append("<ul>" + string.Concat(view.Journey.Limitations.Select(item => $"<li>{esc(item)}</li>")) + "</ul>");
            sb.Append($"<p>Evidence basis: {esc(view.Journey.EvidenceBasis)}</p></section>\n");
        }

        sb.Append("<section class=\"block\"><h2>Architecture rules</h2>");
        if (rules is null) sb.Append("<p>Architecture rules were not loaded for this export.</p>");
        else
        {
            sb.Append($"<p>Source: {esc(rules.SourceArchive ?? "no Source Analysis snapshot")}</p>");
            sb.Append(table(["Rule", "Expectation", "Outcome", "Detail", "Evidence"], rules.Findings.Select(finding => new[]
            {
                esc(finding.Title), esc($"{finding.SubjectLabel} {IntegrationJourneyPresentation.Expectation(finding.Expectation).ToLowerInvariant()} {finding.TargetLabel}"),
                badge(IntegrationJourneyPresentation.Rule(finding.Outcome)), esc(finding.Detail),
                esc(finding.Evidence.Count == 0 ? "—" : string.Join("; ", finding.Evidence.Select(item => $"{item.File}:{item.Line} {item.Symbol}"))),
            })));
            if (rules.Limitations.Count > 0) sb.Append("<ul>" + string.Concat(rules.Limitations.Select(item => $"<li>{esc(item)}</li>")) + "</ul>");
        }
        sb.Append("</section>\n");

        sb.Append("<section class=\"block\"><h2>Run history</h2>");
        sb.Append(history.Count == 0 ? "<p>No journey runs are recorded (or history was not available without sign-in).</p>"
            : table(["Journey", "Scenario", "Started", "Result", "Verified steps", "Limitations"], history.Select(run => new[]
            {
                esc(run.JourneyId), esc(run.ScenarioId), esc($"{run.StartedAt:yyyy-MM-dd HH:mm} UTC"), badge(IntegrationJourneyPresentation.Run(run.State)),
                esc($"{run.VerifiedSteps} of {run.TotalSteps}"), esc(run.LimitationCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            })));
        sb.Append("</section>\n");
        if (pack.Limitations.Count > 0)
            sb.Append("<section class=\"block\"><h2>Limitations</h2><ul>" + string.Concat(pack.Limitations.Select(item => $"<li>{esc(item)}</li>")) + "</ul></section>\n");
        return buildHtml(pack.DisplayName + " — Integration journeys", projectName, $"Environment: {pack.EnvironmentId} · No payloads, credentials or personal data are included.", sb.ToString());
    }
}
