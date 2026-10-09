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
}
