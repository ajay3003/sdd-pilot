using BirkNext.Api.Services.TestPlanning;
using BirkNext.Integrations;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// "Behaviour from developer integration tests" (2026-11-22): only for a project without specification requirements; only tests that cross a real
/// boundary by the Test Coverage level rule (browser, hosted API, messaging); one candidate per class with readable steps; unit, contract-baseline,
/// probe and demo-tool tests never offered. Generic: no project names.
/// </summary>
public sealed class DeveloperIntegrationTestScenarioTests
{
    private static readonly TestBoundarySignal HostedApi = new(BoundaryKind.Http, BoundaryMode.InProcess, "WebApplicationFactory", "Class", 1);
    private static readonly TestBoundarySignal Mocked = new(BoundaryKind.ExternalService, BoundaryMode.Mocked, "Mock<IClient>", "Method", 1);

    private static TestBehaviorFact Fact(string project, string className, string method, params TestBoundarySignal[] signals) => new()
    {
        TestId = $"{className}.{method}", Project = project, ClassName = className, MethodName = method, FilePath = $"{project}/{className.Split('.').Last()}.cs",
        Line = method.Length, Signals = [.. signals],
    };

    private static ScenarioDiscoveryContext Context(CoverageWorkspaceEvidence? workspace, params TestBehaviorFact[] facts) =>
        new("p", "p", new IqrSourceSnapshot { TestBehaviorEvidence = new TestBehaviorSourceEvidence { Tests = [.. facts] } }, workspace, null);

    private static readonly CoverageWorkspaceEvidence NoSpecification = new();

    [Fact]
    public async Task Hosted_API_tests_become_one_candidate_per_class_with_readable_steps()
    {
        var result = await new DeveloperIntegrationTestScenarioProvider().DiscoverAsync(Context(NoSpecification,
            Fact("Shop.Orders.IntegrationTests", "Shop.Orders.OrderSubmissionTests", "Submit_WithEmptyBasket_IsRejected", HostedApi),
            Fact("Shop.Orders.IntegrationTests", "Shop.Orders.OrderSubmissionTests", "Submit_ValidOrder_Returns201AndStoresTheOrder", HostedApi),
            Fact("Shop.Orders.IntegrationTests", "Shop.Orders.OrderSubmissionTests", "A_paid_order_is_shipped_once", HostedApi),
            Fact("Shop.Orders.UnitTests", "Shop.Orders.PriceCalculatorTests", "Discount_Applies", Mocked)), default);

        result.Capability.State.Should().Be(DiscoveryCapabilityState.Available);
        var c = result.Candidates.Should().ContainSingle("unit tests are never offered").Subject;
        c.Title.Should().Be("Order submission");
        c.Area.Should().Be("Orders");
        c.CapabilityId.Should().Be(D.DeveloperTests);
        c.Confidence.Should().Be(CandidateConfidence.ExistingTest);
        c.OpenQuestions.Should().ContainSingle().Which.Should().Contain("confirm the expected behaviour with the developer");
        c.Steps.Should().BeEquivalentTo(new DiscoveredStep[] {
            new DiscoveredStep("Submit with empty basket", "Is rejected"),
            new DiscoveredStep("Submit valid order", "Returns 201 and stores the order"),
            new DiscoveredStep("A paid order is shipped once", null) });
        BirkNext.TestPlanning.ScenarioOwnership.Of(c).ManualRelevant.Should().BeTrue();
    }

    [Fact]
    public async Task With_specification_requirements_or_unreadable_documents_the_source_is_not_used()
    {
        var withSpec = new CoverageWorkspaceEvidence { Requirements = [new CoverageRequirement("FR-001", "A requirement", null)] };
        var api = Fact("Shop.IntegrationTests", "Shop.OrderTests", "Submit_IsAccepted", HostedApi);
        (await new DeveloperIntegrationTestScenarioProvider().DiscoverAsync(Context(withSpec, api), default)).Capability.State.Should().Be(DiscoveryCapabilityState.NotApplicable);
        (await new DeveloperIntegrationTestScenarioProvider().DiscoverAsync(Context(null, api), default)).Capability.State.Should().Be(DiscoveryCapabilityState.NotApplicable);
    }

    [Fact]
    public async Task Contract_baselines_health_probes_demo_tools_and_skipped_tests_are_not_offered()
    {
        var result = await new DeveloperIntegrationTestScenarioProvider().DiscoverAsync(Context(NoSpecification,
            Fact("Shop.ContractTests", "Shop.ResponseBaselineTests", "Response_EqualsTheBaseline", HostedApi),
            Fact("Shop.IntegrationTests", "Shop.HealthEndpointTests", "GetLive_Returns200", HostedApi),
            Fact("Shop.Demo.DebugPanel.Tests", "Shop.Demo.HomePageTests", "Home_Opens", HostedApi),
            Fact("Shop.IntegrationTests", "Shop.OrderTests", "Submit_IsAccepted", HostedApi) with { Skipped = true }), default);
        result.Candidates.Should().BeEmpty();
        result.Capability.State.Should().Be(DiscoveryCapabilityState.NoEvidence);
        result.Capability.Detail.Should().Contain("not manual test cases");
    }

    [Theory]
    [InlineData("ReadField_IsRefusedAtTheEndpoint_ForAnUnauthenticatedCaller", "Read field", "Is refused at the endpoint for an unauthenticated caller")]
    [InlineData("CreateClient_Fqdn_ConnectsToThatNamespace", "Create client fqdn", "Connects to that namespace")]
    [InlineData("Query_UnauthenticatedRequest_Returns401", "Query unauthenticated request", "Returns 401")]
    [InlineData("Failure_on_the_second_link_leaves_no_message", "Failure on the second link leaves no message", null)]
    [InlineData("Validates", "Validates", null)]
    public void Test_names_read_as_steps_without_invented_splits(string method, string action, string? expected) =>
        DeveloperIntegrationTestScenarioProvider.Step(method).Should().Be(new DiscoveredStep(action, expected));

    [Theory]
    [InlineData("AltinnHooksEndpoints", "Altinn hooks endpoints")]
    [InlineData("GraphQLAuthentication", "GraphQL authentication")]
    [InlineData("XMLValidator", "XML validator")]
    [InlineData("Returns201", "Returns 201")]
    public void Identifiers_become_words(string identifier, string words) => DeveloperIntegrationTestScenarioProvider.Words(identifier).Should().Be(words);
}
