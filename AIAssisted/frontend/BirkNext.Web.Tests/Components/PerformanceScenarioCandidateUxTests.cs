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
/// Scenario candidate UX: a normalised {id} is a dynamic parameter (a runtime value configured on the step — not a Test Data requirement), hidden
/// observed values with an explicit review bridge
/// (backend match by alias, unapproved candidate registration), the green "Added to scenario" selection state with unresolved warnings kept,
/// filters/ranked subset/technical toggle, REST vs GraphQL separation and the proposed GraphQL definition. Generic fixtures only.
/// </summary>
public sealed class PerformanceScenarioCandidateUxTests : BunitContext
{
    private const string Target = "https://app.example.test";
    private const string ObservedId = "8455120934";
    private static readonly DateTimeOffset Seen = new(2026, 10, 28, 13, 6, 0, TimeSpan.Zero);
    private readonly Mock<ITestDataApiService> _testData = new();
    private PerformanceTestDefinition? _changed;
    private int? _addedStep;
    private (bool Called, HttpPerformanceStep? Step) _graphQl;

    public PerformanceScenarioCandidateUxTests()
    {
        var api = new Mock<IPerformanceTestApiService>();
        api.Setup(a => a.ScenarioSourcesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Services.AddSingleton(api.Object);
        Services.AddSingleton(_testData.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static ObservedNetworkEndpoint Rest(string path, int count = 13, ObservedTrafficCategory category = ObservedTrafficCategory.Rest) => new()
    {
        Category = category, Scheme = "https", Host = "app.example.test", Port = 443, Method = "GET", Path = path, Count = count, LastStatus = 200, AuthObserved = true, LastObservedAt = Seen,
    };

    private static ObservedNetworkEndpoint GraphQl(string name, string? document) => new()
    {
        Category = ObservedTrafficCategory.GraphQl, Scheme = "https", Host = "app.example.test", Port = 443, Method = "POST", Path = "/graphql", Count = 7, LastStatus = 200, AuthObserved = true,
        LastObservedAt = Seen, OperationName = name, OperationType = GraphQlOperationType.Query,
        GraphQlDocuments = document is null ? [] : [new ObservedGraphQlDocument { Hash = "h", Document = document, LastObservedAt = Seen }],
    };

    private static PerformanceTestDefinition Definition(params HttpPerformanceStep[] steps) => new()
    {
        Id = "def-1", Name = "Baseline", TargetOrigin = Target, EnvironmentName = "Development", EnvironmentType = "Development",
        Scenario = new PerformanceScenario { Name = "Main scenario", Steps = [.. steps] },
    };

    private IRenderedComponent<PerformanceScenarioBuilder> Builder(IReadOnlyList<ObservedNetworkEndpoint> observed, PerformanceTestDefinition? definition = null, string source = PerformanceScenarioBuilder.ObservedKey)
    {
        var d = (definition ?? Definition()) with { Scenario = (definition ?? Definition()).Scenario with { SourceKind = source } };
        return Render<PerformanceScenarioBuilder>(p => p.Add(x => x.Definition, d).Add(x => x.EnvironmentId, "dev").Add(x => x.EnvironmentName, "Development")
            .Add(x => x.Observations, observed).Add(x => x.OnChange, (PerformanceTestDefinition next) => _changed = next)
            .Add(x => x.OnStepAdded, (int n) => _addedStep = n).Add(x => x.OnConfigureStep, (int n) => _configuredStep = n)
            .Add(x => x.OnNewGraphQlDefinition, (HttpPerformanceStep? s) => _graphQl = (true, s)));
    }

    private int? _configuredStep;

    [Fact]
    public void A_step_added_from_the_contract_is_the_same_operation_Live_Discovery_sees_so_it_is_never_added_twice()
    {
        var fromContract = new HttpPerformanceStep
        {
            Name = "getchildrenforuser", Method = "GET", RelativePath = "/api/brukere/{brukerId}/barn",
            Source = new() { OperationKey = "openapi:openapi:Autorisasjon/openapi.yaml#GET /api/brukere/{brukerId}/barn", Origin = ScenarioOperationOrigin.OpenApi },
        };
        var cut = Builder([Rest($"/api/brukere/{ObservedId}/barn")], Definition(fromContract));

        var card = cut.Find("[data-testid=psb-operation]");
        card.GetAttribute("data-added").Should().Be("true", "the observed GET /api/brukere/{id}/barn is the contract's GET /api/brukere/{brukerId}/barn");
        cut.FindAll("[data-testid=psb-add], [data-testid=psb-review-add], [data-testid=psb-select]").Should().BeEmpty("no second copy can be added");
        cut.Find("[data-testid=psb-configure-step]").Click();
        _configuredStep.Should().Be(1);
    }

    [Fact]
    public void An_operation_shows_the_authentication_it_actually_gets_and_Configure_opens_its_existing_step()
    {
        var cut = Builder([Rest($"/api/brukere/{ObservedId}/barn")]);
        cut.Find("[data-testid=psb-review-add]").Click();
        cut.Find("[data-testid=psb-add]").Click();
        var added = _changed!;
        string Auth(PerformanceTestDefinition d) => Builder([Rest($"/api/brukere/{ObservedId}/barn")], d).FindAll("[data-testid=psb-attention-item][data-kind=auth]").Single().TextContent;
        HttpPerformanceStep Step(StepAuthenticationMode mode, string? reference = null) => added.Scenario.Steps[0] with { Authentication = mode, AuthenticationReference = reference };

        Auth(added).Should().EndWith("configuration missing");
        Auth(added with { Scenario = added.Scenario with { Steps = [Step(StepAuthenticationMode.None)] }, AuthenticationReference = PerformanceScenarioRuntime.TargetEnvironmentApiCredential })
            .Should().EndWith("step set to None — authentication unresolved", "an explicit None on the step wins over a configured default — and is unresolved");
        Auth(added with { AuthenticationReference = PerformanceScenarioRuntime.TargetEnvironmentApiCredential })
            .Should().Contain("scenario default: Target Environment API credential");
        Auth(added with { Scenario = added.Scenario with { Steps = [Step(StepAuthenticationMode.Override, PerformanceScenarioRuntime.TargetEnvironmentAuthentication)] } })
            .Should().Contain("step override: Target Environment — not executable by the load generator", "attended sign-in is configured, not executable");

        var existing = Builder([Rest($"/api/brukere/{ObservedId}/barn")], added);
        existing.Find("[data-testid=psb-configure-step]").Click();
        _configuredStep.Should().Be(1, "it opens the step that already holds the operation");
        _changed!.Scenario.Steps.Should().ContainSingle("no duplicate step is created");
    }

    private static AngleSharp.Dom.IElement Card(IRenderedComponent<PerformanceScenarioBuilder> cut, string name) =>
        cut.FindAll("[data-testid=psb-operation]").Single(o => o.QuerySelector("[data-testid=psb-op-name]")!.TextContent == name);

    [Fact]
    public void LiveDiscovery_WithZeroObservations_ShowsNoCandidates_AndNeverFallsBackToContractOperations()
    {
        var cut = Builder([]);
        cut.FindAll("[data-testid=psb-operation]").Should().BeEmpty("Live Discovery lists observations only — never declared or recommended operations");
        cut.Find("[data-testid=psb-live-actions]").TextContent.Should().Contain("Use declared contracts");
        cut.FindAll("[data-testid=psb-contract-chooser]").Should().BeEmpty();
    }

    [Fact]
    public void LiveDiscovery_WithoutASnapshot_ListsObservedOperations_AndANormalisedIdNeedsTestData_ValueHidden()
    {
        var cut = Builder([Rest($"/api/autorisasjon/v1/brukere/{ObservedId}/barn")]);
        cut.Find("[data-testid=psb-live]").Should().NotBeNull();
        cut.FindAll("[data-testid=psb-snapshot]").Should().BeEmpty("Live Discovery works without a contract source; correlation is optional and only offered when one exists");
        var card = Card(cut, "GET /api/autorisasjon/v1/brukere/{id}/barn");
        card.QuerySelector("[data-testid=psb-evidence]")!.TextContent.Should().Be("Observed only");
        card.QuerySelector("[data-testid=psb-observation]")!.TextContent.Should().StartWith("13 calls · HTTP 200 · last seen");
        var need = card.QuerySelector("[data-testid=psb-need]")!;
        need.GetAttribute("data-variable").Should().Be("id");
        need.GetAttribute("data-state").Should().Be("Missing");
        need.TextContent.Should().Contain("Path parameter").And.Contain("No compatible approved Test Data (optional").And.Contain("Observed value available · hidden");
        card.QuerySelector("[data-testid=psb-normalization]")!.TextContent.Should().Contain("dynamic route segment detected").And.Contain("not a Test Data requirement");
        card.QuerySelector("[data-testid=psb-state]")!.TextContent.Should().Be("Review required", "observed only — the contract is unknown");
        card.QuerySelectorAll("[data-testid=psb-attention-item]").Select(i => i.TextContent).Should().Contain("Dynamic parameter: id")
            .And.Contain(t => t.StartsWith("Observed only")).And.Contain("Authentication: Bearer observed · configuration missing");
        card.QuerySelector("[data-testid=psb-attention-item][data-kind=dynamic]")!.ClassList.Should().Contain("psb-info-item", "a dynamic parameter alone is information, not a warning");
        card.QuerySelector("[data-testid=psb-testdata-state]")!.GetAttribute("data-state").Should().Be(ScenarioTestDataStates.Optional);
        card.QuerySelector("[data-testid=psb-auth]")!.TextContent.Should().Contain("Bearer observed").And.Contain("configuration missing").And.Contain("no token is copied");
        cut.Markup.Should().NotContain(ObservedId).And.NotContain("no Test Data needed").And.NotContain("Test Data missing");
    }

    [Fact]
    public void ReviewObservedValue_KeepsItHidden_MatchesInTheBackend_AndUsesTheAliasNeverTheValue()
    {
        _testData.Setup(t => t.MatchObservedValueAsync(It.IsAny<ObservedValueMatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ObservedValueMatchResult(true, "bruker-id-dev", "Existing approved Test Data match found: bruker-id-dev."), (string?)null));
        var cut = Builder([Rest($"/api/brukere/{ObservedId}/barn")]);
        cut.Find("[data-testid=psb-review-observed]").Click();
        var review = cut.Find("[data-testid=ovr]");
        review.GetAttribute("role").Should().Be("dialog");
        review.TextContent.Should().Contain("Path parameter").And.Contain("GET /api/brukere/{id}/barn").And.Contain("Live Discovery").And.Contain("Development").And.Contain("Yes (bearer observed");
        cut.Find("[data-testid=ovr-value]").TextContent.Should().Contain("Available · hidden");
        cut.Markup.Should().NotContain(ObservedId, "hidden until the person explicitly shows it");

        cut.Find("[data-testid=ovr-check]").Click();
        cut.Find("[data-testid=ovr-match]").TextContent.Should().Contain("Existing approved Test Data match found").And.Contain("bruker-id-dev");
        _testData.Verify(t => t.MatchObservedValueAsync(It.Is<ObservedValueMatchRequest>(r => r.Value == ObservedId && r.DataType == TestDataDataType.SimpleValues), It.IsAny<CancellationToken>()), Times.Once);
        cut.Find("[data-testid=ovr-use-match]").Click();
        cut.Find("[data-testid=psb-data-chosen]").TextContent.Should().Be("Will map to bruker-id-dev when added");
        cut.Find("[data-testid=psb-review-add]").Click();
        cut.Find("[data-testid=psb-add]").Click();
        var step = _changed!.Scenario.Steps.Single();
        step.DataMappings!.Single().Should().Match<ScenarioDataMapping>(m => m.Variable == "id" && m.AssetAliases.Single() == "bruker-id-dev" && m.FromObservedSample);
        System.Text.Json.JsonSerializer.Serialize(step).Should().NotContain(ObservedId, "the scenario stores the alias; Test Data owns the value");

        cut.Find("[data-testid=psb-review-observed]").Click();
        cut.Find("[data-testid=ovr-show]").Click();
        cut.Find("[data-testid=ovr-value]").TextContent.Should().Contain(ObservedId, "shown only after an explicit Show value");
    }

    [Fact]
    public void RegisterObservedValue_CreatesAnUnapprovedCandidate_WithoutSendingTheValue()
    {
        TestDataRegistrationRequest? sent = null;
        _testData.Setup(t => t.RegisterAsync(It.IsAny<TestDataRegistrationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<TestDataRegistrationRequest, CancellationToken>((r, _) => sent = r).ReturnsAsync(("asset-1", (string?)null));
        var cut = Builder([Rest($"/api/brukere/{ObservedId}/barn")]);
        cut.Find("[data-testid=psb-review-observed]").Click();
        cut.Find("[data-testid=ovr-register]").Click();
        sent.Should().NotBeNull();
        sent!.Alias.Should().Be("observed-id-development");
        sent.Fields.Should().Equal("id");
        System.Text.Json.JsonSerializer.Serialize(sent).Should().NotContain(ObservedId);
        cut.Find("[data-testid=ovr-message]").TextContent.Should().Contain("Needs review").And.Contain("not approved");
        cut.Find("[data-testid=ovr-register]").TextContent.Should().Be("Registered · Needs review");
    }

    [Fact]
    public void AddedCandidate_IsGreenSelected_WithWarningsKept_AndBecomesAddableAgainWhenRemoved()
    {
        var cut = Builder([Rest($"/api/brukere/{ObservedId}/barn")]);
        cut.Find("[data-testid=psb-review-add]").Click();
        cut.Find("[data-testid=psb-add-review]").TextContent.Should().Contain("Observed only").And.Contain("configured on the step");
        cut.Find("[data-testid=psb-add]").Click();
        _addedStep.Should().Be(1, "focus moves to the new step");
        cut.Find("[data-testid=psb-message]").TextContent.Should().Be("Added GET /api/brukere/{id}/barn as step 1 — configure its runtime values on the step. Nothing has run.");
        _changed!.Scenario.Steps.Single().DataMappings.Should().BeNull("discovery never configures runtime values");
        cut.Find("[data-testid=psb-message]").GetAttribute("role").Should().Be("status");

        var added = Builder([Rest($"/api/brukere/{ObservedId}/barn")], _changed);
        var card = added.Find("[data-testid=psb-operation]");
        card.ClassList.Should().Contain("psb-is-added");
        card.GetAttribute("data-added").Should().Be("true");
        card.QuerySelector("[data-testid=psb-added-badge]")!.TextContent.Should().Contain("Added to scenario");
        card.GetAttribute("data-state").Should().Be("added");
        card.QuerySelector("[data-testid=psb-state]")!.TextContent.Should().Be("Added to scenario");
        card.QuerySelectorAll("[data-testid=psb-add]").Should().BeEmpty("an added operation is never added twice");
        card.QuerySelector("[data-testid=psb-configure-step]")!.TextContent.Should().Be("Configure scenario step", "it opens the existing step instead");
        card.QuerySelector("[data-testid=psb-configure-step]")!.HasAttribute("disabled").Should().BeFalse();
        card.QuerySelectorAll("[data-testid=psb-attention-item].psb-warn-item").Should().NotBeEmpty("green means selected, not ready — unresolved warnings stay amber");
        card.QuerySelector("[data-testid=psb-level]")!.TextContent.Should().Be("Review required");

        var removed = Builder([Rest($"/api/brukere/{ObservedId}/barn")], Definition());
        removed.Find("[data-testid=psb-operation]").GetAttribute("data-added").Should().Be("false");
        removed.Find("[data-testid=psb-review-add]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Filters_HaveCounts_AndTheAddedFilterShowsOnlyAddedCandidates()
    {
        var observed = new[] { Rest("/api/orders"), Rest($"/api/brukere/{ObservedId}/barn"), GraphQl("HentBarn", null) };
        var step = new HttpPerformanceStep { Name = "orders", RelativePath = "/api/orders", Source = new ScenarioStepSource { OperationKey = "observed:GET /api/orders", Origin = ScenarioOperationOrigin.Observed, Evidence = ScenarioEvidence.ObservedOnly } };
        var cut = Builder(observed, Definition(step));
        var filters = cut.FindAll("[data-testid=psb-filter]").Select(f => f.TextContent.Trim()).ToList();
        filters.Should().Equal("Candidates (3)", "Ready (0)", "Needs review (1)", "Added to scenario (1)");
        cut.FindAll("[data-testid=psb-evidence-filter]").Select(f => f.TextContent.Trim()).Should().Equal("Any (3)", "Observed + declared (0)", "Observed only (3)", "REST (2)", "GraphQL (1)");
        cut.FindAll("[data-testid=psb-evidence-filter]").Single(f => f.GetAttribute("data-filter") == "graphql").Click();
        cut.FindAll("[data-testid=psb-operation]").Should().ContainSingle().Which.GetAttribute("data-state").Should().Be("notrunnable");
        cut.FindAll("[data-testid=psb-evidence-filter]").Single(f => f.GetAttribute("data-filter") == "all").Click();
        cut.FindAll("[data-testid=psb-filter]").Single(f => f.GetAttribute("data-filter") == "added").Click();
        cut.FindAll("[data-testid=psb-operation]").Should().ContainSingle().Which.GetAttribute("data-added").Should().Be("true");
        cut.FindAll("[data-testid=psb-filter]").Single(f => f.GetAttribute("data-filter") == "added").GetAttribute("aria-pressed").Should().Be("true");
    }

    [Fact]
    public void RecommendedFirst_IsARankingOfTheLiveList_PagedAndTechnicalTrafficOnlyOnRequest()
    {
        var observed = Enumerable.Range(1, 30).Select(i => Rest($"/api/resource{i:00}/list", count: i)).Concat(
            [Rest("/", category: ObservedTrafficCategory.OtherHttp), Rest("/appsettings.json", category: ObservedTrafficCategory.OtherHttp), Rest("/appsettings.Dev.json", category: ObservedTrafficCategory.OtherHttp), Rest("/health/ready")]).ToList();
        // A scenario saved in the old "Recommended" view opens in the default evidence mode (Live Discovery); Recommended is only an ordering now.
        var cut = Builder(observed, source: PerformanceScenarioBuilder.RecommendedKey);
        cut.Find("[data-testid=psb-source-observed]").HasAttribute("checked").Should().BeTrue();
        cut.FindAll("[data-testid=psb-source-recommended]").Should().BeEmpty("Recommended is not an evidence source");
        cut.Find("[data-testid=psb-recommended-first]").HasAttribute("checked").Should().BeTrue();
        cut.FindAll("[data-testid=psb-operation]").Should().HaveCount(10, "no massive uncontrolled first render");
        cut.Find("[data-testid=psb-count]").TextContent.Should().StartWith("31 candidates · Added to scenario: 0");
        cut.Markup.Should().NotContain("appsettings", "technical/background traffic is hidden by default");
        cut.Find("[data-testid=psb-show-technical]").ParentElement!.TextContent.Should().Contain("Show technical/background endpoints (3)");
        cut.Find("[data-testid=psb-more]").TextContent.Should().Be("Show 21 more of 21");
        cut.FindAll("[data-testid=psb-operation]").Last().GetAttribute("data-traffic").Should().Be("Application");

        cut.Find("[data-testid=psb-show-technical]").Change(true);
        cut.Find("[data-testid=psb-search]").Input("appsettings");
        cut.FindAll("[data-testid=psb-operation]").Should().HaveCount(2).And.OnlyContain(o => o.GetAttribute("data-traffic") == "TechnicalBackground");
        cut.Find("[data-testid=psb-traffic]").TextContent.Should().Be("Technical / background");
        cut.Find("[data-testid=psb-search]").Input("health");
        cut.Find("[data-testid=psb-traffic]").TextContent.Should().Be("Diagnostic / smoke");
        cut.Find("[data-testid=psb-review-add]").HasAttribute("disabled").Should().BeFalse("diagnostic endpoints stay explicitly addable (after review)");
        cut.Find("[data-testid=psb-attention-item][data-kind=traffic]").TextContent.Should().Be("Diagnostic / smoke · not a business-load baseline");
    }

    [Fact]
    public void GraphQl_OperationNameOnly_IsNotRunnable_WithUnknownTestData()
    {
        var cut = Builder([GraphQl("HentBarn", null)]);
        var card = cut.Find("[data-testid=psb-operation]");
        card.QuerySelector("[data-testid=psb-testdata-state]")!.TextContent.Trim().Should().Be("Unknown");
        card.QuerySelector("[data-testid=psb-graphql-document]")!.TextContent.Should().Contain("Not captured");
        card.QuerySelector("[data-testid=psb-why]")!.TextContent.Should().Be("The operation name was observed, but the GraphQL query document was not retained. BirkNext cannot construct an executable test safely.");
        cut.Markup.Should().NotContain("no Test Data needed");
    }

    [Fact]
    public void GraphQl_CapturedDocument_ReviewAndCreate_ProposesADefinition_WithVariablesAsPlaceholders()
    {
        var cut = Builder([GraphQl("HentBarn", "query HentBarn($id: ID!) { barn(id: $id) { navn } }")]);
        var card = cut.Find("[data-testid=psb-operation]");
        card.QuerySelector("[data-testid=psb-need]")!.TextContent.Should().Contain("id").And.Contain("GraphQL variable");
        cut.FindAll("[data-testid=psb-add]").Should().BeEmpty("a proposed GraphQL definition is never added to the current scenario");
        cut.Find("[data-testid=psb-review-graphql]").Click();
        var review = cut.Find("[data-testid=psb-graphql-review]");
        review.TextContent.Should().Contain("Captured").And.Contain("$id: ID!").And.Contain("Declared schema match").And.Contain("no token is copied");
        cut.Find("[data-testid=psb-create-graphql]").Click();
        _graphQl.Called.Should().BeTrue();
        _graphQl.Step!.GraphQlQuery.Should().StartWith("query HentBarn($id: ID!)");
        _graphQl.Step.GraphQlVariablesJson.Should().Be("""{"id":"{id}"}""");
    }

    [Fact]
    public void GraphQlCandidate_InARestScenario_IsNotAddable_AndOffersANewGraphQlDefinition()
    {
        var contract = new SourceContract
        {
            Id = "schema", Type = SourceContractType.GraphQlSchema, Name = "Barn GraphQL", File = "Barn/autodoc/schema.graphql",
            Operations = [new ContractOperation("query barn", "query", null, ["id"]) { OperationId = "barn", Detailed = true, ParameterDetails = [new ContractParameter("id", "argument", true, "ID!")], ResultType = "Barn" }],
        };
        var api = new Mock<IPerformanceTestApiService>();
        var snapshot = Guid.NewGuid();
        api.Setup(a => a.ScenarioSourcesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([new ScenarioSourceSnapshot(snapshot, "app.zip", Seen, 0, 1)]);
        api.Setup(a => a.ScenarioContractsAsync(It.IsAny<string>(), snapshot, It.IsAny<CancellationToken>())).ReturnsAsync(new ScenarioContractCatalog(snapshot, "app.zip", Seen, [contract]));
        Services.AddSingleton(api.Object);
        var rest = new HttpPerformanceStep { Name = "orders", RelativePath = "/api/orders" };
        var cut = Builder([], Definition(rest) with { Scenario = Definition(rest).Scenario with { ContractSnapshotId = snapshot, ContractIds = ["schema"] } }, PerformanceScenarioBuilder.GraphQlKey);
        cut.WaitForAssertion(() => cut.Find("[data-testid=psb-operation]"));
        cut.Find("[data-testid=psb-add]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=psb-why]").TextContent.Should().Be("This definition contains REST steps. Create a GraphQL definition to use this operation.");
        cut.Find("[data-testid=psb-new-graphql]").Click();
        _graphQl.Should().Be((true, (HttpPerformanceStep?)null));
        _changed.Should().BeNull("no mixed-mode step is created");
    }
}
