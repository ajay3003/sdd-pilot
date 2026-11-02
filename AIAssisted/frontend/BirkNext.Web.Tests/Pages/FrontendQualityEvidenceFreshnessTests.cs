using BirkNext.BrowserCompanion;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// FQR loads its own browser evidence (never via Browser Discovery being opened), keeps live and historical pages apart with freshness on every
/// page (live first), treats historical as usable-with-a-limitation unless live evidence is explicitly required, never shows a verdict while the
/// evidence loads, states access/authentication semantics truthfully, and freezes the evidence a result used ("Newer browser evidence available").
/// </summary>
public sealed class FrontendQualityEvidenceFreshnessTests : BunitContext
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
    private readonly BrowserCompanionRuntime _runtime;
    private readonly RuntimeReviewSessionService _sessions = new();
    private string? _livePath = "/admin/page-0";
    private IEndpointDiscoveryService? _discoveryOverride;

    public FrontendQualityEvidenceFreshnessTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new Mock<IBrowserCompanionApiService>();
        api.Setup(a => a.StatusAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(() => new BrowserCompanionStatus
        {
            ProfileId = "dev", State = BrowserCompanionState.Connected, ApprovedOrigins = [Origin],
            Live = _livePath is null ? BrowserCompanionLiveSession.Disconnected : new BrowserCompanionLiveSession
            {
                ProfileId = "dev", ExtensionConnected = true,
                LivePages = [new BrowserCompanionLivePage { PageId = "p1", Origin = Origin, Route = _livePath, RegisteredAt = Now, LastSeenAt = Now }],
            },
        });
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(() => new FrontendAnalysisContext
        {
            ActiveProfile = _profile, TargetUrl = Origin, FeatureToggles = _profile.Features, EngineRequirements = _profile.EngineRequirements,
            ReviewEngineSelection = _profile.ReviewEngineSelection, AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId, AuthenticationConfiguration = "Attended",
        });
        var status = new Mock<IFrontendQualityEngineStatusApiService>();
        status.Setup(s => s.GetStatusAsync(It.IsAny<ReviewAuthenticationModeDto>(), It.IsAny<ReviewEngineSelectionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FrontendQualityEngineStatusReportDto());
        _runtime = new BrowserCompanionRuntime(api.Object, _discovery, JSInterop.JSRuntime);
        Services.AddSingleton<IEndpointDiscoveryService>(_ => _discoveryOverride ?? _discovery);
        Services.AddSingleton(api.Object);
        Services.AddSingleton(_runtime);
        Services.AddSingleton(factory.Object);
        Services.AddSingleton(status.Object);
        Services.AddSingleton(Mock.Of<IFrontendQualityReviewOrchestrator>());
        Services.AddSingleton(_sessions);
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton(Mock.Of<IAuthenticatedBrowserSessionService>());
    }

    private void Seed(int count, int nodeCount = 812, TimeSpan? age = null)
    {
        var snapshot = _discovery.GetSnapshot("dev");
        snapshot.Pages.Clear();
        for (var i = 0; i < count; i++)
        {
            var path = $"/admin/page-{i}";
            snapshot.Pages.Add(new()
            {
                PageOrigin = Origin, PagePath = path,
                BrowserEvidence = new()
                {
                    PageOrigin = Origin, PagePath = path, CapturedAt = Now - (age ?? TimeSpan.FromHours(2)) - TimeSpan.FromMinutes(i), Dom = new() { NodeCount = nodeCount },
                    Performance = new() { ObservationType = "initial-load", LcpMs = 1234 }, Accessibility = new() { Checks = [new() { CheckId = "a11y-page-title", Outcome = "Pass", Tested = 1 }] },
                },
            });
        }
    }

    [Fact]
    public void Opened_directly_FQR_loads_the_evidence_itself_and_ranks_the_live_page_first_with_freshness_on_every_page()
    {
        Seed(3);
        var page = Render<FrontendQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=fbe-pages]").TextContent.Should().Be("3"));
        page.Find("[data-testid=fbe-live]").TextContent.Should().Be("1");
        page.Find("[data-testid=fbe-historical]").TextContent.Should().Be("2");
        var rows = page.FindAll("[data-testid=fqr-evidence-page]");
        rows[0].GetAttribute("data-live").Should().Be("true", "the live page is the strongest runtime evidence and comes first");
        rows[0].QuerySelector("[data-testid=fqr-evidence-badge]")!.TextContent.Should().Be("Live evidence · strongest runtime evidence");
        rows[1].QuerySelector("[data-testid=fqr-evidence-badge]")!.TextContent.Should().StartWith("Historical evidence · last observed").And.EndWith("not revalidated in current session");
        page.Find("[data-testid=fqr-browser-evidence-limitations]").TextContent.Should().Contain("2 pages use historical evidence");
        page.Find("[data-testid=fqr-run]").HasAttribute("disabled").Should().BeFalse("historical evidence is a limitation, not a blocker");
    }

    [Fact]
    public void Access_and_attended_authentication_are_stated_truthfully()
    {
        Seed(1);
        var page = Render<FrontendQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-browser-evidence-access]"));
        page.Find("[data-testid=fqr-browser-evidence-access]").TextContent.Should().Contain("BirkNext performed no sign-in")
            .And.Contain("whether these pages required sign-in was not recorded").And.Contain("Configured (Attended) — not exercised by this browser evidence");
    }

    [Fact]
    public void While_the_evidence_loads_the_page_is_assessing_never_a_verdict()
    {
        var pending = new TaskCompletionSource();
        var slow = new Mock<IEndpointDiscoveryService>();
        slow.Setup(d => d.LoadAsync(It.IsAny<IJSRuntime>())).Returns(pending.Task);
        slow.Setup(d => d.GetSnapshot(It.IsAny<string>())).Returns(new EndpointDiscoverySnapshot());
        _discoveryOverride = slow.Object;
        var page = Render<FrontendQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-readiness-assessing]").TextContent.Should().Contain("Assessing frontend review readiness"));
        page.FindAll("[data-testid=fqr-readiness]").Should().BeEmpty();
        page.Find("[data-testid=fqr-run]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=fqr-run-disabled-reason]").TextContent.Should().StartWith("Assessing frontend review readiness");
        pending.SetResult();
        page.WaitForAssertion(() => page.FindAll("[data-testid=fqr-readiness-assessing]").Should().BeEmpty());
    }

    [Fact]
    public void Requiring_live_evidence_turns_historical_only_pages_into_the_blocker()
    {
        Seed(3);
        var page = Render<FrontendQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-require-live-input]"));
        page.Find("[data-testid=fqr-require-live-input]").Change(true);
        page.Find("[data-testid=fqr-require-live-blocker]").TextContent.Should().Contain("2 of 3 pages have historical evidence only");
        page.Find("[data-testid=fqr-run]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=fqr-run-disabled-reason]").TextContent.Should().StartWith("Current-runtime review requires live evidence");
    }

    [Fact]
    public void A_result_keeps_the_evidence_it_used_and_newer_captures_raise_a_warning_without_rewriting_it()
    {
        Seed(2);
        var used = FrontendBrowserEvidences.Used(FrontendBrowserEvidences.From(_discovery.GetSnapshot("dev"), null, [Origin], Now));
        Seed(2, nodeCount: 900);   // Browser Discovery captured a new DOM for both pages after the review
        _sessions.SaveQualityResult(new FrontendQualityReviewReport { TargetUrl = Origin, BrowserEvidence = used }, new FrontendAnalysisContext { ActiveProfile = _profile, TargetUrl = Origin });

        var page = Render<FrontendQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=fqr-newer-evidence]"));
        page.Find("[data-testid=fqr-newer-evidence]").TextContent.Should().Contain("Newer browser evidence available").And.Contain("2 pages changed since this review")
            .And.Contain("keeps the evidence it was reviewed with");
        page.Find("[data-testid=fqr-newer-evidence-review]").Click();
        page.Find("[data-testid=fqr-newer-evidence-list]").TextContent.Should().Contain("DOM evidence changed");
        page.Find("[data-testid=fqr-evidence-used]").TextContent.Should().Contain(used[0].Fingerprint, "the frozen version is what the result shows");
    }

    [Theory]
    [InlineData(2, PageEvidenceFreshness.Recent)]
    [InlineData(24 * 5, PageEvidenceFreshness.Historical)]
    [InlineData(24 * 60, PageEvidenceFreshness.Stale)]
    public void Historical_freshness_is_graded_not_simply_bad(int hoursAgo, PageEvidenceFreshness expected) =>
        FrontendBrowserEvidences.Freshness(false, Now.AddHours(-hoursAgo), Now).Should().Be(expected);

    [Fact]
    public void Unchanged_evidence_is_not_newer_even_when_read_again()
    {
        Seed(2);
        var first = FrontendBrowserEvidences.From(_discovery.GetSnapshot("dev"), null, [Origin], Now);
        var again = FrontendBrowserEvidences.From(_discovery.GetSnapshot("dev"), null, [Origin], Now.AddHours(1));
        FrontendBrowserEvidences.NewerThan(FrontendBrowserEvidences.Used(first), again).Should().BeEmpty("identity is the evidence fingerprint, not the read time");
    }
}
