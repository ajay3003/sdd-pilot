using BirkNext.BrowserCompanion;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// "Review current page only": one run narrowed to the page open in the paired browser — its evidence alone for the page evidence engines, its
/// address for the URL engines, the narrowing stated as a limitation. Available only when exactly one page is open (never guessed among
/// several); the page is read again at run time. The evidence panel is summary first: live page, one count line, page list collapsed.
/// </summary>
public sealed class FrontendQualityCurrentPageTests : BunitContext
{
    private const string Origin = "https://app.example.test";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private readonly FrontendAnalysisProfile _profile = new()
    {
        Id = "dev", Name = "DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Origin,
        Features = { EnableBrowserQualityEngine = true, EnablePerformanceQualityEngine = true, EnableBrowserRuntimeEngine = false, EnableAccessibilityEngine = false,
            EnableLighthouseEngine = false, EnablePassiveSecurityEngine = false },
    };
    private readonly EndpointDiscoveryService _discovery = new();
    private readonly Mock<IFrontendQualityReviewOrchestrator> _orchestrator = new();
    private List<string> _livePaths = ["/admin/page-0"];

    public FrontendQualityCurrentPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new Mock<IBrowserCompanionApiService>();
        api.Setup(a => a.StatusAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(() => new BrowserCompanionStatus
        {
            ProfileId = "dev", State = BrowserCompanionState.Connected, ApprovedOrigins = [Origin],
            Live = new BrowserCompanionLiveSession
            {
                ProfileId = "dev", ExtensionConnected = true,
                LivePages = [.. _livePaths.Select((path, i) => new BrowserCompanionLivePage { PageId = $"t{i}-a", Origin = Origin, Route = path, RegisteredAt = Now, LastSeenAt = Now })],
            },
        });
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(() => new FrontendAnalysisContext
        {
            ActiveProfile = _profile, TargetUrl = Origin, FeatureToggles = _profile.Features, EngineRequirements = _profile.EngineRequirements,
            ReviewEngineSelection = _profile.ReviewEngineSelection,
        });
        var status = new Mock<IFrontendQualityEngineStatusApiService>();
        status.Setup(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineStatusReportDto());
        _orchestrator.Setup(o => o.RunAsync(It.IsAny<string>(), It.IsAny<FrontendAnalysisContext>(), It.IsAny<FrontendQualityEngineExecutionSnapshot?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityReviewOrchestrationResult(PreflightBlockReason: "stub"));
        Services.AddSingleton<IEndpointDiscoveryService>(_discovery);
        Services.AddSingleton(api.Object);
        Services.AddSingleton(new BrowserCompanionRuntime(api.Object, _discovery, JSInterop.JSRuntime));
        Services.AddSingleton(factory.Object);
        Services.AddSingleton(status.Object);
        Services.AddSingleton(_orchestrator.Object);
        Services.AddSingleton(new RuntimeReviewSessionService());
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());
    }

    private void Seed(int count)
    {
        var snapshot = _discovery.GetSnapshot("dev");
        snapshot.Pages.Clear();
        for (var i = 0; i < count; i++)
        {
            var path = $"/admin/page-{i}";
            snapshot.Pages.Add(new()
            {
                PageOrigin = Origin, PagePath = path,
                BrowserEvidence = new() { PageOrigin = Origin, PagePath = path, CapturedAt = Now - TimeSpan.FromHours(2) - TimeSpan.FromMinutes(i), Dom = new() { NodeCount = 100 } },
            });
        }
    }

    [Fact]
    public void The_evidence_panel_is_summary_first_with_the_live_page_leading_and_the_page_list_collapsed()
    {
        Seed(3);
        var page = Render<FrontendQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-current-page]"));
        page.Find("[data-testid=fqr-current-page]").TextContent.Should().Contain("Live now").And.Contain("/admin/page-0");
        page.Find("[data-testid=fqr-browser-evidence-counts]").TextContent.Should().Contain("3 pages").And.Contain("1 live").And.Contain("2 historical");
        page.Find("[data-testid=fqr-browser-evidence-detail]").HasAttribute("open").Should().BeFalse("the page list and access detail are collapsed");
    }

    [Fact]
    public void Reviewing_the_current_page_only_runs_against_that_page_and_states_the_narrowing()
    {
        Seed(3);
        var page = Render<FrontendQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-page-scope-current-input]").HasAttribute("disabled").Should().BeFalse());
        page.Find("[data-testid=fqr-page-scope-current]").TextContent.Should().Contain("Current page only — /admin/page-0");

        page.Find("[data-testid=fqr-page-scope-current-input]").Change(true);
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-run]").TextContent.Should().Contain("Review current page"));
        page.Find("[data-testid=fqr-run]").Click();

        page.WaitForAssertion(() => _orchestrator.Verify(o => o.RunAsync($"{Origin}/admin/page-0",
            It.Is<FrontendAnalysisContext>(c => c.ReviewPageIdentity == $"{Origin}/admin/page-0"), It.IsAny<FrontendQualityEngineExecutionSnapshot?>(), It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public void All_captured_pages_stay_the_default_and_review_the_frontend_url()
    {
        Seed(2);
        var page = Render<FrontendQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-run]").HasAttribute("disabled").Should().BeFalse());
        page.Find("[data-testid=fqr-run]").Click();
        page.WaitForAssertion(() => _orchestrator.Verify(o => o.RunAsync(Origin, It.Is<FrontendAnalysisContext>(c => c.ReviewPageIdentity == null),
            It.IsAny<FrontendQualityEngineExecutionSnapshot?>(), It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public void Several_open_pages_make_the_current_page_unknown_never_guessed()
    {
        _livePaths = ["/admin/page-0", "/admin/page-1"];
        Seed(2);
        var page = Render<FrontendQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-page-scope-help]").TextContent.Should().Contain("2 pages are open in the paired browser"));
        page.Find("[data-testid=fqr-page-scope-current-input]").HasAttribute("disabled").Should().BeTrue();
    }

    [Theory]
    [InlineData("https://app.example.test/admin/", "https://app.example.test/admin", true)]
    [InlineData("https://APP.example.test/admin", "https://app.example.test/admin", true)]
    [InlineData("https://app.example.test/other", "https://app.example.test/admin", false)]
    [InlineData("https://app.example.test/other", null, true)]
    public void A_page_is_in_scope_when_it_is_the_current_page_or_no_page_is_chosen(string identity, string? current, bool expected) =>
        FrontendQualityPageScope.Includes(new FrontendAnalysisContext { ReviewPageIdentity = current }, identity).Should().Be(expected);

    [Fact]
    public void The_review_url_is_the_current_page_and_the_limitation_names_it()
    {
        var context = new FrontendAnalysisContext { TargetUrl = Origin, ReviewPageIdentity = $"{Origin}/admin/page-0" };
        FrontendQualityPageScope.ReviewUrl(context).Should().Be($"{Origin}/admin/page-0");
        FrontendQualityPageScope.Limitation(context).Should().StartWith("Scope: current page only (https://app.example.test/admin/page-0)");
        FrontendQualityPageScope.ReviewUrl(new FrontendAnalysisContext { TargetUrl = Origin }).Should().Be(Origin);
        FrontendQualityPageScope.Limitation(new FrontendAnalysisContext()).Should().BeNull();
    }
}
