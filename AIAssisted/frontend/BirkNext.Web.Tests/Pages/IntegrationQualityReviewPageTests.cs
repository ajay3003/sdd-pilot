using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Integration Quality Review over the backend catalog: capability-specific pre-run readiness and the typed result.</summary>
public sealed class IntegrationQualityReviewPageTests : BunitContext
{
    private readonly FakeIntegrationCatalogApi _api = new() { Catalog = M2lbFixture.Catalog(), Readiness = M2lbFixture.Readiness(), Result = M2lbFixture.Result() };

    public IntegrationQualityReviewPageTests()
    {
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext
        {
            ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.bufetat.no/" },
        });
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        Services.AddSingleton(context.Object);
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton<IReportExportService, ReportExportService>();
        Services.AddSingleton<RuntimeReviewSessionService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<IntegrationQualityReview> Open() => Render<IntegrationQualityReview>();

    private IRenderedComponent<IntegrationQualityReview> Run()
    {
        var cut = Open();
        cut.Find("[data-testid=iqr-run]").Click();
        cut.WaitForElement("[data-testid=iqr-result]");
        return cut;
    }

    [Fact]
    public void PreRunSummarisesTheConfiguredSystemAndItsTopics()
    {
        var cut = Open();
        cut.Find("[data-testid=iqr-summary-systems]").TextContent.Should().Be("1");
        cut.Find("[data-testid=iqr-summary-topics]").TextContent.Should().Be("16");
        cut.Find("[data-testid=iqr-system-consumers]").TextContent.Should().Contain("1 confirmed").And.Contain("8 suggested").And.Contain("7 needing confirmation");
        cut.Find("[data-testid=iqr-system]").TextContent.Should().Contain("Not configured for 16 topic(s)", "an unknown consumer group is stated, never invented");
    }

    [Fact]
    public void ReadinessIsCapabilitySpecificAndLimitationsDoNotBlock()
    {
        var cut = Open();
        cut.Find("[data-testid=iqr-readiness] h2").TextContent.Should().Be("Can run with limitations");
        cut.Find("[data-testid=iqr-run]").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-testid=iqr-run]").TextContent.Trim().Should().Be("Run with limitations");
        var cards = cut.FindAll("[data-testid=iqr-domain-readiness-card]");
        cards.Should().HaveCount(10);
        cards.Single(c => c.GetAttribute("data-domain") == "Configuration").GetAttribute("data-readiness").Should().Be("Ready");
        cards.Single(c => c.GetAttribute("data-domain") == "Performance").GetAttribute("data-readiness").Should().Be("NotAssessable");
    }

    [Fact]
    public void NothingEnabledCannotRunAndLinksToIntegrations()
    {
        _api.Readiness = new IntegrationReviewReadiness { Headline = "Cannot run", Reasons = ["No enabled integration is configured."] };
        var cut = Open();
        cut.Find("[data-testid=iqr-run]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=iqr-readiness-reasons]").TextContent.Should().Contain("No enabled integration");
        cut.Find("[data-testid=iqr-open-integrations]").GetAttribute("href").Should().Contain("tab=integrations").And.Contain("profile=dev");
    }

    [Fact]
    public void TopicListsStartCollapsed()
    {
        var cut = Open();
        cut.Find("[data-testid^=iqr-system-topics-] .disclosure-body").HasAttribute("hidden").Should().BeTrue();
        cut.Find("[data-testid^=iqr-system-topics-] button").Click();
        cut.Find("[data-testid^=iqr-system-topics-] .disclosure-body").HasAttribute("hidden").Should().BeFalse();
        cut.FindAll(".iqr-topic-list li").Should().HaveCount(16);
    }

    [Fact]
    public void ABackendOutageIsStatedWithoutAFakeResult()
    {
        _api.LoadFailure = new HttpRequestException("refused");
        var cut = Open();
        cut.Find("[data-testid=iqr-error]").TextContent.Should().Contain("backend");
        cut.FindAll("[data-testid=iqr-run]").Should().BeEmpty();
        cut.Markup.Should().NotContain("Loading configured integrations");
    }

    [Fact]
    public void ResultHeadlineCountsDomainsAndNeverShowsNotAssessedAsZero()
    {
        var cut = Run();
        cut.Find("[data-testid=iqr-outcome]").TextContent.Should().NotBeNullOrWhiteSpace();
        cut.Find("[data-testid=iqr-result-topics]").TextContent.Should().Be("16");
        cut.Find("[data-testid=iqr-result-domains]").TextContent.Should().Be("2 of 10", "only Configuration and Connectivity had assessed checks");
        cut.Find("[data-testid=iqr-result-findings]").TextContent.Should().Be("1", "the namespace problem is one finding, not sixteen");
        cut.Find("[data-testid=iqr-result-not-assessed]").TextContent.Should().Be("32");
        var cards = cut.FindAll("[data-testid=iqr-domain-card]");
        cards.Single(c => c.GetAttribute("data-domain") == "Performance").QuerySelector("[data-testid=iqr-domain-state]")!.TextContent.Should().Be("Not assessed");
        cards.Single(c => c.GetAttribute("data-domain") == "ErrorHandling").QuerySelector("[data-testid=iqr-domain-coverage]")!.TextContent.Should().Be("0 of 16 checks assessed");
        cut.Markup.Should().NotContain("0 failures");
    }

    [Fact]
    public void TabsAreAnAccessibleTablistWithArrowKeys()
    {
        var cut = Run();
        var tabs = cut.FindAll("[role=tab]");
        tabs.Select(t => t.TextContent.Trim()).Should().Equal("Overview", "Configuration", "Connectivity", "Contracts", "Message flow", "Reliability", "Error handling", "Security", "Observability", "Performance", "Data quality", "Findings (1)");
        tabs[0].GetAttribute("aria-selected").Should().Be("true");
        tabs[0].KeyDown(new KeyboardEventArgs { Key = "ArrowRight" });
        cut.Find("[data-testid=iqr-tabpanel]").GetAttribute("data-tab").Should().Be("Configuration");
        cut.FindAll("[role=tab]")[1].KeyDown(new KeyboardEventArgs { Key = "ArrowLeft" });
        cut.Find("[data-testid=iqr-tabpanel]").GetAttribute("data-tab").Should().Be("Overview");
        cut.FindAll("[role=tab]")[0].KeyDown(new KeyboardEventArgs { Key = "End" });
        cut.Find("[data-testid=iqr-tabpanel]").GetAttribute("data-tab").Should().Be("Findings");
    }

    [Fact]
    public void IdenticalTopicChecksCollapseIntoOneRow()
    {
        var cut = Run();
        cut.Find("[data-testid=iqr-tab-message-flow]").Click();
        var rows = cut.FindAll("[data-testid=iqr-check-row]");
        rows.Should().ContainSingle();
        rows[0].TextContent.Should().Contain("All 16 topics").And.Contain("Not assessed");
    }

    [Fact]
    public void ErrorHandlingIsNamedAsChecksNotErrors()
    {
        var cut = Run();
        cut.Find("[data-testid=iqr-tab-error-handling]").Click();
        cut.Find("[data-testid=iqr-checks-heading]").TextContent.Should().StartWith("Error-handling checks");
    }

    [Fact]
    public void FindingsTableShowsTheGroupedPlatformFinding()
    {
        var cut = Run();
        cut.Find("[data-testid=iqr-tab-findings]").Click();
        var row = cut.Find("[data-testid=iqr-finding-row]");
        row.GetAttribute("data-rule").Should().Be("namespace-unreachable");
        row.TextContent.Should().Contain("affects 16 topics");
    }

    [Fact]
    public void OverviewSeparatesManualFollowUpFromLimitations()
    {
        var cut = Run();
        cut.Find("[data-testid=iqr-manual-item]").TextContent.Should().Contain("Confirm consumer mappings");
        cut.Find("[data-testid=iqr-limitation]").TextContent.Should().Contain("No runtime evidence source");
    }

    [Fact]
    public void ExportDownloadsTheResultAndBackReturnsToSetup()
    {
        var cut = Run();
        cut.Find("[data-testid=iqr-export]").Click();
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "downloadHtmlFile");
        cut.Find("[data-testid=iqr-back]").Click();
        cut.WaitForElement("[data-testid=iqr-readiness]");
        _api.Calls.Count(c => c == "run").Should().Be(1, "going back never starts a review");
    }
}
