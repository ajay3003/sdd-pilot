using BirkNext.TestPlanning;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Technical Testing › Discover &amp; assess shows the technical share of the project's one scenario inventory per specialist module (2026-11-18):
/// counts and links to the module that owns the work, read from the last stored discovery (never a new run, never another project's result).
/// </summary>
public sealed class TechnicalCandidateSummaryTests : BunitContext
{
    private Mock<ITestPlanSuggestionsApiService> Api(ScenarioDiscoveryResult? last)
    {
        var api = new Mock<ITestPlanSuggestionsApiService>();
        api.Setup(a => a.LastDiscoveryAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync((last, (string?)null));
        Services.AddSingleton(api.Object);
        return api;
    }

    [Fact]
    public void Technical_candidates_are_counted_per_specialist_module_with_links_and_manual_ones_are_left_out()
    {
        var candidates = ScenarioInventoryPerformanceTestsAccess.Inventory(100).Select(c => c with { ProjectKey = "m2lb" }).ToList();
        var api = Api(new ScenarioDiscoveryResult { DiscoveryId = "d", ProjectKey = "m2lb", DiscoveredAt = DateTimeOffset.UtcNow, Candidates = candidates });
        var cut = Render<TechnicalCandidateSummary>(p => p.Add(x => x.ProjectKey, "m2lb"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=tcs-share]").Should().NotBeEmpty());
        var shares = cut.FindAll("[data-testid=tcs-share]").ToDictionary(s => s.GetAttribute("data-area")!, s => s);
        shares.Keys.Should().BeEquivalentTo(["ContractTesting", "ApiQualityReview", "CriticalE2E", "TestPackJourney"], "manual candidates are not technical work");
        shares["ContractTesting"].QuerySelector("a")!.GetAttribute("href").Should().Be("api-quality-review/contracts");
        shares["ContractTesting"].TextContent.Should().Contain("distinct");
        shares["TestPackJourney"].QuerySelector("a")!.GetAttribute("href").Should().Be(IntegrationTestingPresentation.HomeRoute, "the synthetic pack has no page of its own");
        cut.Find("[data-testid=tcs-browse]").GetAttribute("href").Should().Be("test-plan-suggestions?area=choose&view=technical");
        api.Verify(a => a.DiscoverAsync(It.IsAny<ScenarioDiscoveryRequest>(), It.IsAny<CancellationToken>()), Times.Never(), "nothing is discovered from Technical Testing");
    }

    [Fact]
    public void Without_a_discovery_or_with_another_projects_result_it_says_where_discovery_runs()
    {
        Api(new ScenarioDiscoveryResult { DiscoveryId = "d", ProjectKey = "other", Candidates = ScenarioInventoryPerformanceTestsAccess.Inventory(10) });
        var cut = Render<TechnicalCandidateSummary>(p => p.Add(x => x.ProjectKey, "m2lb"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=tcs-none]").TextContent.Should().Contain("No scenario discovery has run for this project yet"));
        cut.Markup.Should().NotContain("Contract Testing");
    }
}

/// <summary>Access to the shared synthetic inventory of the performance tests.</summary>
internal static class ScenarioInventoryPerformanceTestsAccess
{
    public static List<DiscoveredScenario> Inventory(int count) => BirkNext.Web.Tests.Pages.ScenarioInventoryPerformanceTests.Inventory(count);
}
