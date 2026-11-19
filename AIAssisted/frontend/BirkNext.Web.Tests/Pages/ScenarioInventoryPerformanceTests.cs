using System.Diagnostics;
using BirkNext.TestPlanning;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit.Abstractions;
using P = BirkNext.Web.Services.TestPlanSuggestionsPresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Plan &amp; prepare with large candidate sets (2026-11-18): the inventory is classified once per discovery result, a view switch, filter or
/// search never re-runs analysis or calls the backend, and the list renders a bounded page of rows. Timings are written to the test output;
/// the limits are generous ceilings for a shared CI machine, not targets.
/// </summary>
public sealed class ScenarioInventoryPerformanceTests(ITestOutputHelper output) : BunitContext
{
    /// <summary>An M2LB-shaped mix: ~30 % business / guide / UI behaviour, the rest API operations (copies of schemas), checks and journeys.</summary>
    internal static List<DiscoveredScenario> Inventory(int count) => [.. Enumerable.Range(0, count).Select(i => (i % 10) switch
    {
        0 or 1 => Candidate(i, "specification", ScenarioCandidateType.BusinessFunctional, "Specification", $"scenario:m|{i}"),
        2 => Candidate(i, "ui-source", i % 20 == 2 ? ScenarioCandidateType.UiSmoke : ScenarioCandidateType.UiFunctional, "UI source", $"ui:{i}"),
        9 => Candidate(i, "integration-journeys", ScenarioCandidateType.Integration, "Integration journey", $"journey:pack:j{i % 3}:s{i}"),
        _ => Candidate(i, "api-contract", i % 7 == 0 ? ScenarioCandidateType.SecurityAuthorization : ScenarioCandidateType.ApiContract, "API contract", $"contract-op:f{i % 4}.graphql#op{i / 4}") with
        {
            Template = [new("operation", "Operation", $"query op{i / 40}"), new("input", "Input", null), new("expected-response", "Expected response", "Returns X")],
            Evidence = [new("contract-operation", $"Svc{i % 4}/schema.graphql:1#query op{i / 40}", "GraphQlSchema S")],
        },
    })];

    private static DiscoveredScenario Candidate(int i, string capability, ScenarioCandidateType type, string source, string key) => new()
    {
        CandidateId = $"tsc-{i:00000}", ProjectKey = "perf", CapabilityId = capability, SourceCategory = source, Area = $"Area {i % 25}", Title = $"Candidate {i:00000}",
        Purpose = $"Purpose {i}", Type = type, Confidence = CandidateConfidence.SourceDerived, ExpectedBehaviour = i % 9 == 0 ? null : "Expected", CanonicalKey = key,
        RequirementIds = i % 5 == 0 ? [$"FR-{i % 50:000}"] : [], Engine = "test", RuleVersion = "tsd-1",
    };

    [Theory]
    [InlineData(50)]
    [InlineData(500)]
    [InlineData(1000)]
    [InlineData(5000)]
    public void Large_inventories_stay_responsive_and_render_a_bounded_page(int count)
    {
        var candidates = Inventory(count);
        var watch = Stopwatch.StartNew();
        var inventory = P.Classify(candidates);
        var classify = watch.Elapsed.TotalMilliseconds;
        (inventory.Manual + inventory.Technical + inventory.Unclassified).Should().Be(count, "every candidate belongs to one workspace or to review");

        var api = new Mock<ITestPlanSuggestionsApiService>();
        api.Setup(a => a.ProjectAsync("perf", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanWorkspace { ProjectKey = "perf", GuideKey = "project:perf", CanWrite = true, RuleVersion = "tps-1" }, (string?)null));
        api.Setup(a => a.LastDiscoveryAsync("perf", It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ScenarioDiscoveryResult { DiscoveryId = "d", ProjectKey = "perf", RuleVersion = "tsd-1", DiscoveredAt = DateTimeOffset.UtcNow, Candidates = candidates }, (string?)null));
        Services.AddSingleton(api.Object);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "perf", "Perf")).Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.GetRequiredService<NavigationManager>().NavigateTo("test-plan-suggestions");

        watch.Restart();
        var cut = Render<TestPlanSuggestions>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tps]").GetAttribute("aria-busy").Should().Be("false"));
        var initial = watch.Elapsed.TotalMilliseconds;
        cut.FindAll("li[data-testid^=tps-candidate-]").Count.Should().BeLessThanOrEqualTo(P.PageSize, "rows are rendered a page at a time");

        watch.Restart();
        cut.Find("[data-testid=tps-view-all]").Click();
        var viewSwitch = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        cut.Find("[data-testid=tps-search]").Input("Candidate 0001");
        var search = watch.Elapsed.TotalMilliseconds;

        watch.Restart();
        cut.Find("[data-testid=tps-select-shown]").Click();
        var select = watch.Elapsed.TotalMilliseconds;
        cut.Find("[data-testid=tps-search]").Input("");

        watch.Restart();
        cut.Find("[data-testid=tps-prepare]").Click();
        var preview = watch.Elapsed.TotalMilliseconds;

        var groups = cut.FindAll("[data-testid^=tps-group-toggle-]");
        watch.Restart();
        if (groups.Count > 1) groups[1].Click();
        var expand = watch.Elapsed.TotalMilliseconds;

        var first = cut.FindAll("[data-testid^=tps-details-]")[0];
        watch.Restart();
        first.Click();
        var details = watch.Elapsed.TotalMilliseconds;

        var memory = GC.GetTotalMemory(forceFullCollection: false) / 1024 / 1024;
        output.WriteLine($"PERF n={count} classify={classify:F1}ms initial={initial:F0}ms view={viewSwitch:F0}ms search={search:F0}ms select={select:F0}ms preview={preview:F0}ms expand={expand:F0}ms details={details:F0}ms heap={memory}MB");
        api.Verify(a => a.DiscoverAsync(It.IsAny<ScenarioDiscoveryRequest>(), It.IsAny<CancellationToken>()), Times.Never(), "views, filters and search never re-run discovery");
        api.Verify(a => a.LastDiscoveryAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once(), "no per-row or per-filter backend request");
        classify.Should().BeLessThan(2000);
        new[] { viewSwitch, search, select, preview, expand, details }.Should().OnlyContain(ms => ms < 5000);
    }
}
