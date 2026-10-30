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
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
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
        cut.FindAll("[data-testid=iqr-system]").Should().ContainSingle("one configured system, shown under Setup & Evidence → Advanced");
        cut.Find("[data-testid=iqr-system-topics]").TextContent.Should().Be("16");
        cut.Find("[data-testid=iqr-system-consumers]").TextContent.Should().Contain("1 confirmed").And.Contain("8 suggested").And.Contain("7 unconfirmed");
        cut.Find("[data-testid=iqr-system-groups]").TextContent.Should().Be("Not configured for 16 topics", "an unknown consumer group is stated, never invented");
    }

    [Fact]
    public void ReadinessIsCapabilitySpecificAndLimitationsDoNotBlock()
    {
        var cut = Open();
        cut.Find("[data-testid=iqr-review-readiness]").TextContent.Should().Be("Partially reviewable");
        cut.Find("[data-testid=iqr-readiness]").GetAttribute("data-headline").Should().Be("Can run with limitations");
        cut.Find("[data-testid=iqr-run]").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-testid=iqr-run]").TextContent.Trim().Should().Be("Run review");
        var cards = cut.FindAll("[data-testid=iqr-domain-readiness-card]");
        cards.Should().HaveCount(10);
        cards.Single(c => c.GetAttribute("data-domain") == "Configuration").GetAttribute("data-readiness").Should().Be("Ready");
        cards.Single(c => c.GetAttribute("data-domain") == "Performance").GetAttribute("data-readiness").Should().Be("NotAssessable");
    }

    [Theory]
    [InlineData(2, 0, 0, "disabled", "tab=integrations")]
    [InlineData(0, 1, 0, "no-integrations", "tab=integrations")]
    [InlineData(0, 0, 1, "detectable", "dev")]
    public void EmptyState_NamesTheActualCondition(int configured, int systems, int snapshots, string condition, string href)
    {
        _api.Readiness = new IntegrationReviewReadiness
        {
            Headline = "Cannot run", ConfiguredIntegrations = configured,
            Systems = [.. Enumerable.Range(0, systems).Select(i => new IntegrationSystemScope { SystemName = $"S{i}" })],
            AvailableSourceSnapshots = [.. Enumerable.Range(0, snapshots).Select(_ => new IqrSourceSnapshot())],
        };
        var cut = Open();
        cut.Find("[data-testid=iqr-zero-integrations]").GetAttribute("data-condition").Should().Be(condition);
        cut.Find("[data-testid=iqr-zero-integrations-action]").GetAttribute("href").Should().Contain(href);
        if (condition == "detectable") cut.Find("[data-testid=iqr-zero-integrations]").TextContent.Should().Contain("Imported source evidence is available").And.Contain("Source Analysis");
    }

    [Fact]
    public void NothingEnabledCannotRunAndLinksToIntegrations()
    {
        _api.Readiness = new IntegrationReviewReadiness { Headline = "Cannot run", Reasons = ["No enabled integration is configured."] };
        var cut = Open();
        cut.FindAll("[data-testid=iqr-run]").Should().BeEmpty("Cannot run never shows a (disabled) run button");
        cut.Find("[data-testid=iqr-zero-integrations]").GetAttribute("data-condition").Should().Be("nothing");
        cut.Find("[data-testid=iqr-zero-integrations]").TextContent.Should().Contain("No connected systems, integrations or imported source evidence");
        cut.Find("[data-testid=iqr-zero-integrations-action]").GetAttribute("href").Should().Contain("dev");
        cut.FindAll("[data-testid=iqr-readiness], [data-testid=iqr-attention-item]").Should().BeEmpty("an empty environment is one clear empty state, not a page of empty technical UI");
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
    public void The_result_leads_with_a_summary_runtime_status_gaps_and_findings_and_keeps_technical_evidence_collapsed_at_the_end()
    {
        var cut = Run();
        var markup = cut.Markup;
        markup.IndexOf("data-testid=\"iqr-result-summary\"", StringComparison.Ordinal).Should().BeLessThan(markup.IndexOf("data-testid=\"iqr-overview-domains\"", StringComparison.Ordinal), "summary first");
        cut.Find("[data-testid=iqr-result-runtime]").TextContent.Should().Be("Not verified", "configuration/source evidence is never runtime verification");
        cut.Find("[data-testid=iqr-overview-findings]").TextContent.Should().Contain("1 finding");
        cut.FindAll("[data-testid=iqr-gap]").Select(g => g.TextContent).Should().Contain(g => g.StartsWith("No runtime evidence"));
        cut.FindAll("[data-testid=iqr-domain-card] [data-testid=iqr-domain-observed] li, [data-testid=iqr-domain-card] [data-testid=iqr-domain-missing] li")
            .GroupBy(li => li.Closest("[data-testid=iqr-domain-card]")).Should().OnlyContain(g => g.Count() <= 4, "cards show at most two observations and two gaps");
        var details = cut.FindAll("[data-testid=iqr-domain-details]");
        details.Should().NotBeEmpty();
        details[0].Click();
        cut.Find("[data-testid=iqr-checks-table]");
        cut.FindAll("[data-testid=iqr-source-details]").Should().BeEmpty("this run has no source snapshot, so there is no technical appendix above or below");
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
