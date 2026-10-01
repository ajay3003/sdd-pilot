using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>IQR pre-run presentation: compact scope, action-oriented Needs attention, compact runtime evidence, domain actions.
/// The backend's headline, domain readiness and Run gate are shown as they are — never recomputed.</summary>
public sealed class IntegrationQualityReviewPrerunTests : BunitContext
{
    private const string Tiltak = "dev:eventhub:birk-cdc:dbo.Tiltak";
    private readonly FakeIntegrationCatalogApi _api = new() { Catalog = M2lbFixture.Catalog(), Readiness = M2lbFixture.Readiness(), Result = M2lbFixture.Result() };
    private readonly IntegrationMappingEvidenceSession _session = new();

    public IntegrationQualityReviewPrerunTests()
    {
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext
        {
            ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.bufetat.no/" },
        });
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        Services.AddSingleton(_session);
        Services.AddSingleton(context.Object);
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton<IReportExportService, ReportExportService>();
        Services.AddSingleton<RuntimeReviewSessionService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<IntegrationQualityReview> Open() => Render<IntegrationQualityReview>();

    [Fact]
    public void TopSummaryIsCompactAndSaysRunWithLimitations()
    {
        var cut = Open();
        cut.Find("[data-testid=iqr-summary-target]").TextContent.Should().Be("Dev");
        cut.Find("[data-testid=iqr-summary-systems]").TextContent.Should().Be("1");
        cut.Find("[data-testid=iqr-summary-topics]").TextContent.Should().Be("16");
        cut.Find("[data-testid=iqr-summary-enabled]").TextContent.Should().Be("16 of 16");
        cut.Find("[data-testid=iqr-summary-readiness]").TextContent.Should().Be("Run with limitations");
        cut.Find("[data-testid=iqr-readiness-coverage]").TextContent.Should().StartWith("4 of 10 domains are currently assessable or partially assessable.");
    }

    [Fact]
    public void NeedsAttentionIsCompactAndEveryFixDeepLinksToIntegrations()
    {
        var cut = Open();
        cut.Find("[data-testid=iqr-readiness] h3").TextContent.Should().Be("Needs attention");
        var items = cut.FindAll("[data-testid=iqr-attention-item]");
        items.Select(i => i.GetAttribute("data-key")).Should().Equal("mappings", "runtime", "contracts", "groups");
        items[0].TextContent.Should().Contain("15 need confirmation");
        items[1].TextContent.Should().Contain("0 of 4 available");
        items[2].TextContent.Should().Contain("Not configured");
        items[3].TextContent.Should().Contain("Not configured for 16 topics");
        cut.Find("[data-testid=iqr-action-mappings]").GetAttribute("href").Should().Contain("tab=integrations").And.Contain("profile=dev").And.EndWith("focus=mappings");
        cut.Find("[data-testid=iqr-action-runtime]").GetAttribute("href").Should().EndWith("focus=runtime");
        cut.Find("[data-testid=iqr-action-contracts]").GetAttribute("href").Should().EndWith("focus=contracts");
        cut.Find("[data-testid=iqr-run]").TextContent.Trim().Should().Be("Run with limitations");
        cut.Find("[data-testid=iqr-run]").HasAttribute("disabled").Should().BeFalse();
        cut.Find("#iqr-run-note").TextContent.Should().Be("Read-only review. No messages are published, consumed or changed.");
        // The full backend reason list is still available, but collapsed — not a wall of bullets before the Run action.
        cut.Find("[data-testid=iqr-limitation-details] .disclosure-body").HasAttribute("hidden").Should().BeTrue();
        cut.Find("[data-testid=iqr-readiness]").QuerySelectorAll(":scope > ul.iqr-reasons").Should().BeEmpty();
    }

    [Fact]
    public void RuntimeEvidenceStatesTheSharedAzureReasonOnceWithDetailsUnderDisclosure()
    {
        var cut = Open();
        cut.Find("[data-testid=iqr-evidence-count]").TextContent.Should().Contain("0 of 4 available");
        var sources = cut.FindAll("[data-testid=iqr-evidence-source]");
        sources.Select(s => System.Text.RegularExpressions.Regex.Replace(s.TextContent, @"\s+", " ").Trim()).Should().Equal(
            "Not configured Event Hub metadata", "Not configured Consumer groups", "Not configured Checkpoints", "Not configured Application Insights");
        var section = cut.Find("[data-testid=iqr-evidence-sources]");
        var visible = section.TextContent.Replace(cut.Find("[data-testid=iqr-evidence-details]").TextContent, "");
        visible.Split(M2lbFixture.AzureDisabled).Length.Should().Be(2, "the common reason appears once in the compact view");
        cut.Find("[data-testid=iqr-configure-runtime]").GetAttribute("href").Should().EndWith("focus=runtime");
        cut.Find("[data-testid=iqr-evidence-details] button").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("[data-testid=iqr-evidence-details] button").Click();
        cut.FindAll("[data-testid=iqr-evidence-detail-row]").Should().HaveCount(4).And.OnlyContain(r => r.TextContent.Contains("IntegrationReview:Azure:Enabled"));
    }

    [Fact]
    public void AllTenDomainsKeepTheirStateAndGetAShortReasonAndRelevantAction()
    {
        var cut = Open();
        var cards = cut.FindAll("[data-testid=iqr-domain-readiness-card]");
        cards.Should().HaveCount(10);
        // Master-detail: every domain's detail (short reason) is one click away; the row carries the same action as the detail.
        foreach (var domain in cards.Select(c => c.GetAttribute("data-domain")!).ToList())
        {
            cut.FindAll("[data-testid=iqr-domain-readiness-card]").Single(c => c.GetAttribute("data-domain") == domain).QuerySelector("[data-testid=iqr-domain-toggle]")!.Click();
            cut.Find("[data-testid=iqr-domain-detail] [data-testid=iqr-domain-reason]").TextContent.Should().NotBeNullOrWhiteSpace();
            cut.Find("[data-testid=iqr-domain-detail]").QuerySelector("[data-testid=iqr-domain-action]")?.TextContent.Should()
                .Be(cut.FindAll("[data-testid=iqr-domain-readiness-card]").Single(c => c.GetAttribute("data-domain") == domain).QuerySelector("[data-testid=iqr-domain-row-action]")!.TextContent);
        }
        cards = cut.FindAll("[data-testid=iqr-domain-readiness-card]");
        string? Action(string domain) => cards.Single(c => c.GetAttribute("data-domain") == domain).QuerySelector("[data-testid=iqr-domain-row-action]")?.TextContent;
        Action("Configuration").Should().BeNull("a Ready domain needs no corrective action");
        Action("Connectivity").Should().Be("Configure runtime evidence");
        Action("Contract").Should().Be("Manage contracts");
        Action("MessageFlow").Should().Be("Configure runtime evidence");
        Action("DataQuality").Should().Be("Manage contracts");
        cards.Select(c => c.GetAttribute("data-readiness")).Should().Equal(_api.Readiness.Domains.Select(d => d.Readiness.ToString()), "presentation never changes domain readiness");
        cards.Single(c => c.GetAttribute("data-domain") == "Connectivity").TextContent.Should().Contain("Limited");
        cards.Single(c => c.GetAttribute("data-domain") == "Performance").TextContent.Should().Contain("Not assessable");
    }

    [Fact]
    public void StrongEvidenceOnASuggestedMappingIsStillCountedAsUnconfirmed()
    {
        _session.Record("dev", new() { IntegrationId = Tiltak, OverallState = IntegrationMappingEvidenceState.StrongEvidence });
        _session.Record("dev", new() { IntegrationId = "dev:eventhub:birk-cdc:dbo.Bestilling", OverallState = IntegrationMappingEvidenceState.PartialEvidence });
        var cut = Open();
        var mappings = cut.FindAll("[data-testid=iqr-attention-item]").Single(i => i.GetAttribute("data-key") == "mappings");
        mappings.TextContent.Should().Contain("15 need confirmation");
        cut.Find("[data-testid=iqr-mapping-evidence]").TextContent.Should()
            .Contain("2 tested").And.Contain("1 strong evidence, not confirmed").And.Contain("1 partial evidence").And.Contain("13 not tested");
        cut.Find("[data-testid=iqr-system-consumers]").TextContent.Should().Contain("1 confirmed");
    }

    [Fact]
    public void WithoutMappingTestsThePreRunShowsNoEvidenceStatistics()
    {
        var cut = Open();
        cut.FindAll("[data-testid=iqr-mapping-evidence]").Should().BeEmpty();
    }

    [Fact]
    public void AMappingConfirmedAfterItsCheckIsNoLongerCountedAsTested()
    {
        _session.Record("dev", new() { IntegrationId = Tiltak, OverallState = IntegrationMappingEvidenceState.StrongEvidence });
        _api.Catalog = _api.Catalog with
        {
            Integrations = _api.Catalog.Integrations.Select(i => i.Id == Tiltak ? i with { Consumer = i.Consumer with { MappingState = ConsumerMappingState.Confirmed } } : i).ToList(),
        };
        var cut = Open();
        cut.FindAll("[data-testid=iqr-mapping-evidence]").Should().BeEmpty();
    }

    [Fact]
    public void PreviousRunsStaySecondaryAndDoNotChangeCurrentReadiness()
    {
        _api.History.Add(new(Guid.NewGuid(), new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero), IntegrationReviewOutcome.Completed, 16, 0));
        var cut = Open();
        cut.Find("[data-testid=iqr-history] .disclosure-body").HasAttribute("hidden").Should().BeTrue();
        cut.Find("[data-testid=iqr-history]").TextContent.Should().Contain("do not change the current readiness");
        cut.Find("[data-testid=iqr-summary-readiness]").TextContent.Should().Be("Run with limitations");
        cut.Find("[data-testid=iqr-history] button").Click();
        cut.Find("[data-testid=iqr-history-list]").TextContent.Should().Contain("Completed");
        cut.Find("[data-testid=iqr-summary-readiness]").TextContent.Should().Be("Run with limitations");
    }

    [Fact]
    public void ConfiguredSystemLeadsWithReviewMappings()
    {
        var cut = Open();
        cut.Find("[data-testid=iqr-system-review-mappings]").GetAttribute("href").Should().EndWith("focus=mappings");
        cut.Find("[data-testid=iqr-system-contracts]").TextContent.Should().Be("Not configured");
        cut.Find("[data-testid^=iqr-system-topics-] .disclosure-body").HasAttribute("hidden").Should().BeTrue();
    }
}
