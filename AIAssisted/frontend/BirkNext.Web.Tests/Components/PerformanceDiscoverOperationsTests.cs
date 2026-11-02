using BirkNext.LocalHttpsProxy;
using BirkNext.PerformanceTests;
using BirkNext.SourceDomains;
using BirkNext.TestData;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Discover operations: Live / Declared / Manual are evidence modes (Recommended is only an ordering). Live lists observed runtime evidence only
/// and distinguishes loading, unreadable, never run and ran-with-nothing; persisted Endpoint Discovery history drives Current vs Include historical
/// with New / Observed again / Previously observed states (never "removed"). Declared groups contracts by API and keeps contract disambiguation.
/// Generic fixtures; nothing runs.
/// </summary>
public sealed class PerformanceDiscoverOperationsTests : BunitContext
{
    private static readonly Guid SnapshotId = Guid.Parse("5c000000-0000-0000-0000-000000000077");
    private const string Target = "https://app.example.test", Host = "app.example.test";
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private PerformanceTestDefinition? _changed;

    public PerformanceDiscoverOperationsTests()
    {
        var api = new Mock<IPerformanceTestApiService>();
        api.Setup(a => a.ScenarioSourcesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ScenarioSourceSnapshot(SnapshotId, "App.zip", T0.AddDays(-1), 2, 0)]);
        api.Setup(a => a.ScenarioContractsAsync(It.IsAny<string>(), SnapshotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScenarioContractCatalog(SnapshotId, "App.zip", T0.AddDays(-1), [Orders(), Reports()]));
        Services.AddSingleton(api.Object);
        Services.AddSingleton(new Mock<ITestDataApiService>().Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static SourceContract Orders() => new()
    {
        Id = "orders", Type = SourceContractType.OpenApi, Name = "Orders API", File = "Orders/openapi.json",
        Operations =
        [
            new ContractOperation("GET /api/orders/{orderId}", "GET", "/api/orders/{orderId}", ["orderId"]) { Detailed = true, ResponseCodes = ["200"] },
            new ContractOperation("POST /api/orders", "POST", "/api/orders", []) { Detailed = true },
        ],
    };

    private static SourceContract Reports() => new()
    {
        Id = "reports", Type = SourceContractType.OpenApi, Name = "Reports API", File = "Reports/openapi.json",
        Operations = [new ContractOperation("GET /api/reports", "GET", "/api/reports", []) { Detailed = true }],
    };

    private static ObservedOperationHistory Op(string method, string path, ObservedOperationState state, int count = 3) => new()
    {
        Identity = $"{method} {path}", Protocol = "REST", Host = Host, Method = method, Path = path, State = state, ObservationCount = count, LastStatus = 200,
        FirstObservedAt = T0, LastObservedAt = T0.AddMinutes(30), FirstRunId = "run-1", LastRunId = state == ObservedOperationState.PreviouslyObserved ? "run-1" : "run-2",
    };

    private static EndpointDiscoveryRunSummary RunSummary(string id, int observed, int @new = 0, int again = 0, int notSeen = 0, string status = EndpointDiscoveryRunSummary.Completed) =>
        new(id, "dev", Host, T0.AddHours(1), T0.AddHours(1).AddMinutes(5), status, observed, @new, again, notSeen);

    private static EndpointObservationHistoryView History(EndpointDiscoveryRunSummary? latest, params ObservedOperationHistory[] ops) =>
        new("dev", Host, latest is not null, latest, null, latest is null ? [] : [latest], ops);

    private IRenderedComponent<PerformanceScenarioBuilder> Builder(EndpointObservationHistoryView? history, bool loaded = true, string? source = null,
        IReadOnlyList<ObservedNetworkEndpoint>? browser = null)
    {
        var d = new PerformanceTestDefinition
        {
            Id = "def-1", Name = "Baseline", TargetOrigin = Target, EnvironmentName = "Development", EnvironmentType = "Development",
            Scenario = new PerformanceScenario { Name = "Main scenario", SourceKind = source },
        };
        return Render<PerformanceScenarioBuilder>(p => p.Add(x => x.Definition, d).Add(x => x.EnvironmentId, "dev").Add(x => x.EnvironmentName, "Development")
            .Add(x => x.Observations, browser ?? []).Add(x => x.History, history).Add(x => x.HistoryLoaded, loaded)
            .Add(x => x.EndpointDiscoveryHref, "/admin/system-settings?section=target-environments&tab=endpoint&profile=dev")
            .Add(x => x.OnChange, (PerformanceTestDefinition next) => _changed = next));
    }

    private static List<string> Names(IRenderedComponent<PerformanceScenarioBuilder> cut) =>
        [.. cut.FindAll("[data-testid=psb-operation]").Select(o => o.QuerySelector("[data-testid=psb-op-name]")!.TextContent)];

    [Fact]
    public void Live_is_the_default_mode_and_Recommended_is_an_ordering_not_a_source()
    {
        var cut = Builder(History(null));
        cut.FindAll("[data-testid^=psb-source-]").Select(r => r.GetAttribute("value")).Should().Equal("observed", "declared", "manual");
        cut.Find("[data-testid=psb-source-observed]").HasAttribute("checked").Should().BeTrue("a Target Environment exists");
    }

    [Fact]
    public void Loading_history_is_not_presented_as_zero_observations()
    {
        var cut = Builder(null, loaded: false);
        cut.Find("[data-testid=psb-live]").GetAttribute("data-state").Should().Be("loading");
        cut.Markup.Should().NotContain("0 observed operations");
        cut.FindAll("[data-testid=psb-live-actions]").Should().BeEmpty();
    }

    [Fact]
    public void Never_run_says_so_and_offers_Endpoint_Discovery_and_declared_contracts_without_listing_contracts()
    {
        var cut = Builder(History(null));
        cut.Find("[data-testid=psb-live]").GetAttribute("data-state").Should().Be("never-run");
        cut.Find("[data-testid=psb-live-never]").TextContent.Should().Contain("has not been run for this target");
        cut.Find("[data-testid=psb-open-discovery]").GetAttribute("href").Should().Contain("tab=endpoint&profile=dev");
        cut.FindAll("[data-testid=psb-operation]").Should().BeEmpty("Live never falls back to declared operations");
        cut.FindAll("[data-testid=psb-contract-chooser]").Should().BeEmpty();
    }

    [Fact]
    public void Ran_with_zero_is_different_from_never_run()
    {
        var cut = Builder(History(RunSummary("run-1", 0)));
        cut.Find("[data-testid=psb-live]").GetAttribute("data-state").Should().Be("zero");
        cut.Find("[data-testid=psb-live-zero]").TextContent.Should().Contain("completed").And.Contain("with 0 observed API operations");
        cut.FindAll("[data-testid=psb-operation]").Should().BeEmpty();
    }

    [Fact]
    public void Unreadable_history_says_so_and_uses_only_this_browsers_observations()
    {
        var browser = new ObservedNetworkEndpoint { Category = ObservedTrafficCategory.Rest, Host = Host, Port = 443, Method = "GET", Path = "/api/status", Count = 2, LastStatus = 200, LastObservedAt = T0 };
        var cut = Builder(null, browser: [browser]);
        cut.Find("[data-testid=psb-live]").GetAttribute("data-state").Should().Be("unreadable");
        cut.Find("[data-testid=psb-live-unreadable]").TextContent.Should().Contain("could not be read").And.Contain("this browser's observations");
        Names(cut).Should().Equal("GET /api/status");
    }

    [Fact]
    public void Current_vs_include_historical_with_history_states_and_never_removed()
    {
        var cut = Builder(History(RunSummary("run-2", 2, @new: 1, again: 1, notSeen: 1),
            Op("GET", "/api/status", ObservedOperationState.New), Op("GET", "/api/orders/{id}", ObservedOperationState.ObservedAgain, 5),
            Op("GET", "/api/legacy", ObservedOperationState.PreviouslyObserved)));
        cut.Find("[data-testid=psb-live-counts]").TextContent.Should().Contain("2 observed").And.Contain("1 new").And.Contain("1 observed again")
            .And.Contain("1 previously observed but not seen this run");
        cut.Find("[data-testid=psb-history-current]").ParentElement!.TextContent.Should().Contain("Current observations (2)");
        cut.Find("[data-testid=psb-history-all]").ParentElement!.TextContent.Should().Contain("Include historical (3)");
        Names(cut).Should().HaveCount(2).And.NotContain(n => n.Contains("legacy"));
        cut.FindAll("[data-testid=psb-history-state]").Select(s => s.TextContent.Trim()).Should().BeEquivalentTo(["New", "Observed again"]);

        cut.Find("[data-testid=psb-history-all]").Change(true);
        Names(cut).Should().HaveCount(3);
        cut.FindAll("[data-testid=psb-history-state]").Select(s => s.TextContent.Trim()).Should().Contain("Previously observed · not seen in latest run");
        cut.Markup.Should().NotContainEquivalentOf("removed");
    }

    [Fact]
    public void A_baseline_run_shows_observed_only_and_invents_no_new_or_not_seen_counts()
    {
        var cut = Builder(History(RunSummary("baseline", 1, status: EndpointDiscoveryRunSummary.Baseline), Op("GET", "/api/status", ObservedOperationState.Observed)));
        cut.Find("[data-testid=psb-live-latest]").TextContent.Should().Contain("baseline from earlier browser history");
        cut.Find("[data-testid=psb-live-counts]").TextContent.Trim().Should().Be("1 observed");
    }

    [Fact]
    public void Switching_mode_changes_the_list_and_Declared_groups_by_API_with_contract_counts()
    {
        var cut = Builder(History(RunSummary("run-2", 1, again: 1), Op("GET", "/api/orders/{id}", ObservedOperationState.ObservedAgain)));
        cut.WaitForAssertion(() => Names(cut).Should().ContainSingle());
        cut.FindAll("[data-testid=psb-snapshot]").Should().NotBeEmpty("correlation is offered, collapsed, when a contract source exists");
        cut.Find("[data-testid=psb-snapshot]").Change(SnapshotId.ToString());
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=psb-operation]").Single().GetAttribute("data-evidence").Should().Be("DeclaredAndObserved"));

        cut.Find("[data-testid=psb-source-declared]").Change(true);
        cut.FindAll("[data-testid=psb-live]").Should().BeEmpty();
        cut.Find("[data-testid=psb-count-rest]").TextContent.Should().Be("2 APIs");
        cut.Find("[data-testid=psb-count-graphql]").TextContent.Should().Be("0 schemas");
        var groups = cut.FindAll("[data-testid=psb-group]");
        groups.Should().NotBeEmpty();
        groups[0].HasAttribute("open").Should().BeTrue("the most relevant group is open; the rest are collapsed");
        groups.Skip(1).Should().OnlyContain(g => !g.HasAttribute("open"));
        cut.FindAll("[data-testid=psb-not-observed]").Should().NotBeEmpty("declared operations that were not observed are labelled so");
    }

    [Fact]
    public void Use_declared_contracts_switches_mode_from_an_empty_Live_list()
    {
        var cut = Builder(History(null));
        cut.Find("[data-testid=psb-use-declared]").Click();
        cut.Find("[data-testid=psb-declared]").Should().NotBeNull();
        cut.Find("[data-testid=psb-source-declared]").HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public void Manual_claims_no_evidence_and_lists_nothing()
    {
        var cut = Builder(History(RunSummary("run-2", 1, @new: 1), Op("GET", "/api/status", ObservedOperationState.New)), source: PerformanceScenarioBuilder.ManualKey);
        cut.Find("[data-testid=psb-manual]").TextContent.Should().Contain("claims no observed or declared evidence");
        cut.FindAll("[data-testid=psb-operation]").Should().BeEmpty();
    }

    [Fact]
    public void Recommended_first_can_be_turned_off_for_a_plain_path_order()
    {
        var cut = Builder(History(RunSummary("run-2", 2, @new: 2), Op("GET", "/api/zeta", ObservedOperationState.New, 50), Op("GET", "/api/alpha", ObservedOperationState.New, 1)));
        cut.Find("[data-testid=psb-recommended-first]").Change(false);
        Names(cut).Should().Equal("GET /api/alpha", "GET /api/zeta");
    }

    [Fact]
    public void A_legacy_recommended_or_openapi_source_kind_maps_to_an_evidence_mode()
    {
        Builder(History(null), source: PerformanceScenarioBuilder.OpenApiKey).Find("[data-testid=psb-source-declared]").HasAttribute("checked").Should().BeTrue();
        Builder(History(null), source: PerformanceScenarioBuilder.RecommendedKey).Find("[data-testid=psb-source-observed]").HasAttribute("checked").Should().BeTrue();
    }
}
