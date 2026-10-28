using BirkNext.LocalHttpsProxy;
using BirkNext.PerformanceTests;
using BirkNext.SourceDomains;
using BirkNext.TestData;
using BirkNext.Web.Components;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Smart Scenario builder: sources Recommended / OpenAPI / GraphQL / Live Discovery / Manual over the explicitly chosen snapshot's contracts, the
/// browser's observations and central Test Data. Candidates are proposals with evidence badges and reasons; adding one never runs anything.
/// </summary>
public sealed class PerformanceScenarioBuilderTests : BunitContext
{
    private static readonly Guid SnapshotId = Guid.Parse("5c000000-0000-0000-0000-000000000042");
    private const string Target = "https://m2lbdev.example.test";

    private static SourceContract Persons() => new()
    {
        Id = "persons", Type = SourceContractType.OpenApi, Name = "Persons API", File = "Persons/openapi.json",
        Operations =
        [
            new ContractOperation("GET /api/person/{personId}", "GET", "/api/person/{personId}", ["personId"])
            {
                OperationId = "getPerson", Detailed = true, ResponseCodes = ["200", "404"], Security = ["Bearer (bearer)"],
                ParameterDetails = [new ContractParameter("personId", "path", true, "string")],
            },
            new ContractOperation("POST /api/person", "POST", "/api/person", []) { OperationId = "createPerson", Detailed = true, RequestContentTypes = ["application/json"], RequestSchema = "CreatePersonRequest" },
        ],
    };

    private static SourceContract Hendelse() => new()
    {
        Id = "hendelse", Type = SourceContractType.OpenApi, Name = "Hendelse API", File = "Hendelse/openapi.json",
        Operations = [new ContractOperation("GET /api/hendelse", "GET", "/api/hendelse", []) { Detailed = true }],
    };

    private static ObservedNetworkEndpoint Seen(string path, int count = 44) => new()
    {
        Category = ObservedTrafficCategory.Rest, Host = "m2lbdev.example.test", Port = 443, Method = "GET", Path = path, Count = count, LastStatus = 200, AuthObserved = true,
        LastObservedAt = new DateTimeOffset(2026, 10, 23, 8, 54, 0, TimeSpan.Zero),
    };

    private static readonly TestDataAssetSummary PersonIds = new()
    {
        Id = "person-id-baseline", Alias = "person-id-baseline", Kind = TestDataKind.Scalar, Approval = TestDataApproval.Approved, Capabilities = ["personId"], Source = "Configured catalog",
    };

    private PerformanceTestDefinition? _changed;

    private IRenderedComponent<PerformanceScenarioBuilder> Render(params SourceContract[] contracts) => Render(contracts, [Seen("/api/person/123")], [PersonIds], authenticated: true);

    private IRenderedComponent<PerformanceScenarioBuilder> Render(SourceContract[] contracts, IReadOnlyList<ObservedNetworkEndpoint> observed, IReadOnlyList<TestDataAssetSummary> assets,
        bool authenticated, PerformanceTestDefinition? definition = null)
    {
        if (_registered) return RenderBuilder(observed, assets, authenticated, definition);
        _registered = true;
        var api = new Mock<IPerformanceTestApiService>();
        api.Setup(a => a.ScenarioSourcesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ScenarioSourceSnapshot(SnapshotId, "M2LB.zip", DateTimeOffset.Parse("2026-10-22T08:00:00Z"), contracts.Length, 0)]);
        api.Setup(a => a.ScenarioContractsAsync(It.IsAny<string>(), SnapshotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScenarioContractCatalog(SnapshotId, "M2LB.zip", DateTimeOffset.Parse("2026-10-22T08:00:00Z"), contracts));
        Services.AddSingleton(api.Object);
        return RenderBuilder(observed, assets, authenticated, definition);
    }

    private bool _registered;

    private IRenderedComponent<PerformanceScenarioBuilder> RenderBuilder(IReadOnlyList<ObservedNetworkEndpoint> observed, IReadOnlyList<TestDataAssetSummary> assets, bool authenticated,
        PerformanceTestDefinition? definition)
    {
        var d = definition ?? new PerformanceTestDefinition
        {
            Id = "def-1", Name = "Baseline", TargetOrigin = Target, EnvironmentName = "Development", EnvironmentType = "Development",
            AuthenticationReference = authenticated ? "target-environment" : null, Scenario = new PerformanceScenario { Name = "Main scenario" },
        };
        return Render<PerformanceScenarioBuilder>(p => p.Add(x => x.Definition, d).Add(x => x.EnvironmentId, "dev").Add(x => x.EnvironmentName, "Development")
            .Add(x => x.Observations, observed).Add(x => x.Assets, assets).Add(x => x.OnChange, (PerformanceTestDefinition next) => _changed = next));
    }

    [Fact]
    public void NoSnapshotIsChosenAutomatically_AndAChosenSnapshotProposesRankedOperationsWithEvidence()
    {
        var cut = Render(Persons(), Hendelse());
        cut.WaitForAssertion(() => cut.Find("[data-testid=psb-snapshot]"));
        cut.Find("[data-testid=psb-snapshot]").GetAttribute("value").Should().BeEmpty("the newest snapshot is never chosen for the user");
        cut.Find("[data-testid=psb-count-observed]").TextContent.Should().Be("1 unique runtime endpoint on this target");
        cut.Find("[data-testid=psb-observations]").TextContent.Should().Contain("a call count is never a workload");
        cut.Find("[data-testid=psb-snapshot]").Change(SnapshotId.ToString());
        cut.WaitForAssertion(() => cut.Find("[data-testid=psb-scope]").GetAttribute("data-state").Should().Be("Resolved"));
        cut.Find("[data-testid=psb-scope]").TextContent.Should().Contain("Persons API").And.NotContain("Hendelse");
        var first = cut.FindAll("[data-testid=psb-operation]")[0];
        first.GetAttribute("data-key").Should().Be("openapi:persons#GET /api/person/{personId}");
        first.GetAttribute("data-level").Should().Be("Recommended");
        first.QuerySelector("[data-testid=psb-declared]")!.TextContent.Should().Contain("Declared").And.Contain(": yes");
        first.QuerySelector("[data-testid=psb-observed]")!.TextContent.Should().Contain(": yes");
        first.QuerySelector("[data-testid=psb-observation]")!.TextContent.Should().StartWith("44 calls · HTTP 200 · last seen 2026-10-23 08:54 UTC");
        first.QuerySelector("[data-testid=psb-auth]")!.TextContent.Should().StartWith("Bearer observed").And.Contain("no token is copied");
        first.QuerySelector("[data-testid=psb-data-recommended]")!.TextContent.Should().Be("Recommended Test Data: person-id-baseline");
        first.QuerySelector("[data-testid=psb-because]")!.TextContent.Should().StartWith("Recommended because declared in OpenAPI · Persons API (M2LB.zip)");
        cut.FindAll("[data-testid=psb-operation]").Should().NotContain(o => o.GetAttribute("data-key")!.Contains("hendelse"), "unrelated contracts are not recommended");
        var post = cut.FindAll("[data-testid=psb-operation]").Single(o => o.GetAttribute("data-key")!.Contains("POST"));
        post.QuerySelector("[data-testid=psb-add]")!.HasAttribute("disabled").Should().BeTrue();
        post.QuerySelector("[data-testid=psb-why]")!.TextContent.Should().Contain("POST");

        // The chooser stays available (collapsed) once resolved; adding a contract starts from the resolved one, never dropping it.
        cut.Find("[data-testid=psb-contract-chooser]").HasAttribute("open").Should().BeFalse();
        cut.FindAll("[data-testid=psb-contract-option]").Select(o => (o.GetAttribute("data-contract"), o.HasAttribute("checked"))).Should().Equal([("Persons API", true), ("Hendelse API", false)]);
        cut.FindAll("[data-testid=psb-contract-option]")[1].Change(true);
        _changed!.Scenario.ContractIds.Should().Equal("persons", "hendelse");
    }

    [Fact]
    public void AddingAnOperation_CreatesAProposedStep_WithAliasMappingAndSuggestedName_NothingRuns()
    {
        var cut = Render(Persons());
        cut.WaitForAssertion(() => cut.Find("[data-testid=psb-snapshot]"));
        cut.Find("[data-testid=psb-snapshot]").Change(SnapshotId.ToString());
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=psb-add]").Should().NotBeEmpty());
        cut.FindAll("[data-testid=psb-add]")[0].Click();
        _changed.Should().NotBeNull();
        var step = _changed!.Scenario.Steps.Single();
        step.Should().Match<HttpPerformanceStep>(s => s.RelativePath == "/api/person/{personId}" && s.Method == "GET");
        step.ExpectedStatusCodes.Should().Equal(200);
        step.DataMappings!.Single().AssetAliases.Should().Equal("person-id-baseline");
        step.Source!.Evidence.Should().Be(ScenarioEvidence.DeclaredAndObserved);
        _changed.Scenario.Name.Should().Be("Person lookup baseline");
        _changed.Scenario.ContractSnapshotId.Should().Be(SnapshotId);
        _changed.Scenario.SourceKind.Should().Be("recommended");
        cut.Find("[data-testid=psb-message]").TextContent.Should().EndWith("Nothing has run.");
    }

    [Fact]
    public void UnresolvedContracts_AskThePersonToChoose_AndMissingTestDataDeepLinksWithContext()
    {
        var cut = Render([Persons(), Hendelse()], [], [], authenticated: true);
        cut.WaitForAssertion(() => cut.Find("[data-testid=psb-snapshot]"));
        cut.Find("[data-testid=psb-snapshot]").Change(SnapshotId.ToString());
        cut.WaitForAssertion(() => cut.Find("[data-testid=psb-scope]").GetAttribute("data-state").Should().Be("Unresolved"));
        cut.Find("[data-testid=psb-scope]").TextContent.Should().Contain("Contract source available · target-specific contract unresolved");
        cut.FindAll("[data-testid=psb-contract-option]").Select(o => o.GetAttribute("data-contract")).Should().Equal("Persons API", "Hendelse API");
        cut.FindAll("[data-testid=psb-operation]").Should().BeEmpty("operations of every contract are never dumped as recommendations");
        cut.FindAll("[data-testid=psb-contract-option]")[0].Change(true);
        _changed!.Scenario.ContractIds.Should().Equal("persons");

        var selected = Render([Persons(), Hendelse()], [], [], authenticated: true, definition: new PerformanceTestDefinition
        {
            Id = "def-1", TargetOrigin = Target, EnvironmentName = "Development", AuthenticationReference = "te", Scenario = new PerformanceScenario { ContractIds = ["persons"] },
        });
        selected.WaitForAssertion(() => selected.Find("[data-testid=psb-snapshot]"));
        selected.Find("[data-testid=psb-snapshot]").Change(SnapshotId.ToString());
        selected.WaitForAssertion(() => selected.FindAll("[data-testid=psb-operation]").Should().NotBeEmpty());
        var get = selected.FindAll("[data-testid=psb-operation]").Single(o => o.GetAttribute("data-key")!.EndsWith("GET /api/person/{personId}"));
        get.GetAttribute("data-evidence").Should().Be("DeclaredOnly");
        get.QuerySelector("[data-testid=psb-data-missing]")!.TextContent.Should().Contain("Missing");
        get.QuerySelector("[data-testid=psb-open-test-data]")!.GetAttribute("href").Should()
            .Be("/test-data?consumer=performance-test-review&consumerId=def-1&operation=GET%20%2Fapi%2Fperson%2F%7BpersonId%7D&variable=personId&dataType=SimpleValues&in=path&contentType=string&environment=Development");
    }

    [Fact]
    public void LiveDiscoveryAndManualSources_AreAvailable_AndObservedOnlyIsReviewRequired()
    {
        var cut = Render([Persons()], [Seen("/api/person/123"), Seen("/internal/case/6b1f0a3e-4f2b-4a8e-9a77-0c1f2e3d4b5a", 3)], [PersonIds], authenticated: true);
        cut.WaitForAssertion(() => cut.Find("[data-testid=psb-source-observed]"));
        cut.Find("[data-testid=psb-source-observed]").Change("observed");
        cut.FindAll("[data-testid=psb-operation]").Should().OnlyContain(o => o.GetAttribute("data-evidence") == "ObservedOnly", "without a chosen contract nothing is declared");
        var observedOnly = cut.FindAll("[data-testid=psb-operation]").Single(o => o.QuerySelector(".psb-op-name")!.TextContent.Contains("/internal/case"));
        observedOnly.QuerySelector(".psb-op-name")!.TextContent.Should().Be("GET /internal/case/{id}", "the observed identifier is replaced by a placeholder");
        observedOnly.GetAttribute("data-level").Should().Be("ReviewRequired");
        cut.Markup.Should().NotContain("6b1f0a3e-4f2b");
        cut.Find("[data-testid=psb-source-manual]").Change("manual");
        cut.Find("[data-testid=psb-manual]").TextContent.Should().Contain("Add manual step");
        cut.FindAll("[data-testid=psb-operations]").Should().BeEmpty();
    }

    [Fact]
    public void TestDataPage_ShowsTheConsumerRequirement_AndMatchingApprovedAssets_WithoutBinding()
    {
        var api = new Mock<ITestDataApiService>();
        api.Setup(a => a.CatalogAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new TestDataCatalogView([PersonIds,
            PersonIds with { Id = "json", Alias = "person-create-valid", Kind = TestDataKind.Structured, MediaType = "application/json" }], [], []), (string?)null));
        Services.AddSingleton(api.Object);
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("/test-data?consumer=performance-test-review&operation=GET%20%2Fapi%2Fperson%2F%7BpersonId%7D&variable=personId&in=path&dataType=SimpleValues&environment=Development");
        var cut = Render<TestDataPage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=td-request]"));
        cut.Find("#td-request-heading").TextContent.Should().Be("Requested by Performance test review");
        cut.Find("[data-testid=td-request-operation]").TextContent.Should().Be("GET /api/person/{personId}");
        cut.Find("[data-testid=td-request-type]").TextContent.Should().Be("Simple values / parameters");
        cut.FindAll("[data-testid=td-request-match]").Select(m => m.TextContent).Should().ContainSingle(m => m.StartsWith("person-id-baseline"));
        cut.Find("[data-testid=td-request]").TextContent.Should().Contain("nothing is bound from here");
    }
}
