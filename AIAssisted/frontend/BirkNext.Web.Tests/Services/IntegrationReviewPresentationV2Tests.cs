using BirkNext.Integrations;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class IntegrationReviewPresentationV2Tests
{
    [Fact]
    public void PaneRowsShortenTopicsAndStateSuggestedConsumers()
    {
        var groups = IntegrationsPanePresentation.Groups(M2lbFixture.Catalog(), "", null);
        groups.Should().ContainSingle();
        var tiltak = groups[0].Rows.Single(r => r.Definition.Id.EndsWith("dbo.Tiltak"));
        tiltak.TopicShort.Should().Be("…dbo.Tiltak");
        tiltak.Consumer.Should().Be("Tjeneste API (suggested)");
    }

    [Fact]
    public void PaneFilterBySearchAndState()
    {
        IntegrationsPanePresentation.Groups(M2lbFixture.Catalog(), "romning", null).SelectMany(g => g.Rows).Should().HaveCount(2);
        IntegrationsPanePresentation.Groups(M2lbFixture.Catalog(), "", IntegrationConfigurationState.Ready).SelectMany(g => g.Rows).Should().ContainSingle();
    }

    [Theory]
    [InlineData(" ", "Consumer group cannot be blank")]
    public void ValidationRejectsABlankConsumerGroup(string group, string message)
    {
        var definition = M2lbFixture.Topic("Person") with { ConsumerGroup = group };
        IntegrationsPanePresentation.Validate(definition).Should().Contain(e => e.StartsWith(message));
    }

    [Fact]
    public void ValidationRejectsRelativeUrls()
    {
        IntegrationsPanePresentation.Validate(M2lbFixture.Topic("Person") with { HealthUrl = "/health" }).Should().Contain("Health URL must be an absolute URL.");
    }

    [Fact]
    public void RowsKeepDifferingTopicChecksApart()
    {
        var rows = IntegrationReviewResultPresentation.Rows(M2lbFixture.Result(), IntegrationReviewDomain.Configuration);
        rows.Should().Contain(r => r.Subject == "All 16 topics · BIRK CDC / Debezium" && r.Check.CheckId == "cfg-topic");
        rows.Where(r => r.Check.CheckId == "cfg-consumer").Should().HaveCount(16, "confirmed and unconfirmed consumers differ, so each topic keeps its row");
    }

    [Fact]
    public void CoverageNeverReadsAsZeroFailures()
    {
        var domain = M2lbFixture.Result().Domains.Single(d => d.Domain == IntegrationReviewDomain.Performance);
        IntegrationReviewResultPresentation.Coverage(domain).Should().Be("No checks");
        IntegrationReviewResultPresentation.DomainTone(domain).Should().Be("muted");
    }

    [Fact]
    public void ExportCarriesProvenanceLimitationsAndNoSecret()
    {
        var html = new ReportExportService().ExportIntegrationReview(M2lbFixture.Result(), "BirkNext");
        html.Should().Contain("Integration Quality Review").And.Contain("evhns-m2lb-dev-nwe-001").And.Contain("Network probe")
            .And.Contain("No runtime evidence source").And.Contain("Confirm consumer mappings").And.Contain("Not assessed");
        html.Should().Contain("Unknown / not configured").And.NotContain("$Default");
        foreach (var forbidden in new[] { "SharedAccessKey", "Endpoint=sb://", "client_secret", "password" }) html.Should().NotContainEquivalentOf(forbidden);
        html.Should().Contain("connect-offsets", "technical topics are listed as not reviewed");
    }
}
