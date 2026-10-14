using BirkNext.Integrations;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Components;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>The journey export is built from readiness/run contracts only: journeys, steps, prerequisites, rules, history — never payloads or secrets.</summary>
public sealed class IntegrationJourneyExportTests
{
    [Fact]
    public void Export_ContainsJourneysStepsRulesAndHistory_WithoutSecretsOrPayloads()
    {
        var pack = IntegrationJourneyPanelTests.SamplePack();
        var rules = new ArchitectureRuleReport("skolenaervaer", Guid.NewGuid(), "source.zip",
            [new("mm-no-person", "MM performs no Person lookup", "MM", "Person", ArchitectureRuleExpectation.MustNotDependOn, ArchitectureRuleOutcome.NotAssessed, "Not source verified.", [], "snapshot")], []);
        IntegrationJourneyRunSummary[] history = [new(Guid.NewGuid(), "skolenaervaer", "rapportmottak", "valid-report", "dev-env", JourneyRunState.Blocked, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 3, 2)];

        var html = new ReportExportService().ExportIntegrationJourneys(pack, rules, history, "Project");

        html.Should().Contain("Skolenærvær Testing").And.Contain("Utdanningsdata").And.Contain("Rapportmottak").And.Contain("Periodeavslutning");
        html.Should().Contain("MM validates the report against the XSD").And.Contain("Authentication not configured");
        html.Should().Contain("MM performs no Person lookup").And.Contain("Not assessed");
        html.Should().Contain("0 of 3").And.Contain("Configured ≠ source verified ≠ executable ≠ runtime verified");
        html.Should().NotContainAny("Bearer ", "SharedAccessKey", "password", "\"Body\"");
    }

    [Fact]
    public void Export_ContainsTheScenarioCapability_WithExecutionAndVerificationApart_TheGapsOwnersAndLimitations()
    {
        var pack = IntegrationJourneyPanelTests.CapabilityPack();
        pack = pack with { EvidenceLimitations = [.. pack.EvidenceLimitations, new("probe", "Escaping probe", "Not verified", "<script>alert(1)</script>", "Skole team")] };

        var html = new ReportExportService().ExportIntegrationJourneys(pack, null, [], "Project");
        var section = html[html.IndexOf("<h2>Scenario capability</h2>", StringComparison.Ordinal)..];

        section.Should().Contain("Execution availability and result verification are separate");
        foreach (var header in new[] { "Status", "Execution", "Result verification", "Missing configuration", "Required configuration", "Required evidence", "Responsible team", "Next action" })
            section.Should().Contain($">{header}<", header);
        section.Should().Contain("Runnable").And.Contain("Conditionally runnable").And.Contain("Operator required").And.Contain("Not assessed").And.Contain("Blocked");
        section.Should().Contain("Available (after confirmation)").And.Contain("After configuration").And.Contain("Not by BirkNext (operator)");
        section.Should().Contain("Partial — Altinn steps only").And.Contain("MU and MM observers").And.Contain("BirkNext operator").And.Contain("Complete the configuration");
        section.Should().Contain("Altinn app contract: incomplete.", "unmet prerequisites are the missing configuration");
        section.Should().Contain("Evidence limitations").And.Contain("Blocked: ID-porten TEST configuration incomplete").And.Contain("Skole operations");
        section.Should().Contain("1 runnable · 1 conditionally runnable · 1 operator required · 1 not assessed · 1 blocked");
        html.Should().NotContain("<script>alert(1)</script>", "every value is escaped").And.NotContain("Passed");
        section.Should().Contain("<div class=\"table-scroll\" role=\"region\" tabindex=\"0\" aria-label=\"Table: Scenario, Source table, Status, …\">",
            "the wide capability table scrolls inside the shared template's labelled, keyboard-reachable region instead of the page");
        System.Text.RegularExpressions.Regex.Matches(html, "<table>").Count.Should().Be(System.Text.RegularExpressions.Regex.Matches(html, "<div class=\"table-scroll\" role=\"region\"").Count,
            "every table of the journey report is wrapped, not only the Skolenærvær ones");
        html.Should().Contain(".badge-notassessed{background:#f3f4f6;color:#4b5563}", "Not assessed badges meet WCAG AA contrast (axe flagged the former #6b7280)");
    }
}
