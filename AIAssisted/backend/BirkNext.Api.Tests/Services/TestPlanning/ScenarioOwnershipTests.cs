using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Scenario ownership (2026-11-18): every candidate of the one discovery inventory is classified from what its producer recorded (capability and
/// candidate type), never from a title or a file name; it says which workspace a tester normally uses and which specialist module owns the technical
/// work. Classification changes no candidate (identity, content and fingerprint stay as discovered) and never hides one.
/// </summary>
public sealed class ScenarioOwnershipTests
{
    private const string Project = "import:m2lb-synthetic";

    private static SourceContract GraphQl(string file) => new()
    {
        Id = "graphql:" + file, Type = SourceContractType.GraphQlSchema, Name = "Authorization", File = file, Line = 1,
        Operations = [new ContractOperation("mutation activateEmergencyAccess", "mutation", "activateEmergencyAccess", ["childId"]) { Detailed = true, ResultType = "EmergencyAccess" }],
    };

    private static SourceContract Rest(string file, string title = "Service API") => new()
    {
        Id = "openapi:" + file, Type = SourceContractType.OpenApi, Name = title, Version = "1.0", File = file, Line = 1,
        Operations = [new ContractOperation("GET /health/live", "GET", "/health/live", []) { Detailed = true, ResponseCodes = ["200"] },
            new ContractOperation("POST /reports", "POST", "/reports", []) { Detailed = true, ResponseCodes = ["202", "401"], Security = ["bearer"] }],
    };

    [Fact]
    public void Each_producer_is_classified_by_its_own_evidence_kind()
    {
        var spec = D.FromSpecification(Project, new CoverageWorkspaceEvidence
        {
            SpecificationAvailable = true, SpecificationArtifacts = [new("a", "specs/001/spec.md", null, "fp")],
            Requirements = [new("FR-001", "The system MUST hold a report for an unknown child.", null), new("FR-002", "The system MUST lock the period.", null)],
            AcceptanceScenarios = [new QaTestCandidate { Id = "US1-1", Title = "Unknown child", Given = "a report for an unknown child", When = "it arrives", Then = "it is held", RequirementIds = ["FR-001"] }],
        }).Candidates;
        var scenario = ScenarioOwnership.Of(spec.Single(c => c.CanonicalKey.StartsWith("scenario:")));
        scenario.Should().Match<ScenarioOwnershipInfo>(o => o.Purpose == ScenarioPurpose.BusinessFunctional && o.Primary == TestingWorkspace.ManualTesting && o.SourceKind == "Acceptance scenario");
        ScenarioOwnership.Of(spec.Single(c => c.CanonicalKey.StartsWith("requirement:"))).SourceKind.Should().Be("Requirement without a scenario");

        var ui = D.FromUi(Project,
        [
            new TestSuggestion { SuggestionId = "s1", Kind = SuggestionKind.PageOpens, Category = SuggestionCategory.BusinessE2E, Route = "/admin", Feature = "Admin", Title = "Admin opens", RuleVersion = "v" },
            new TestSuggestion { SuggestionId = "s2", Kind = SuggestionKind.Search, Category = SuggestionCategory.UiFunctional, Route = "/children", Feature = "Children", Title = "Search finds a child", RuleVersion = "v" },
        ]);
        var smoke = ScenarioOwnership.Of(ui.Single(c => c.Title == "Admin opens"));
        smoke.Should().Match<ScenarioOwnershipInfo>(o => o.Purpose == ScenarioPurpose.FrontendTechnical && o.Primary == TestingWorkspace.TechnicalTesting
            && o.Secondary == TestingWorkspace.ManualTesting && o.Specialist == SpecialistArea.CriticalE2E);
        ScenarioOwnership.Of(ui.Single(c => c.Title == "Search finds a child")).Should().Match<ScenarioOwnershipInfo>(o =>
            o.Purpose == ScenarioPurpose.UiFunctional && o.Primary == TestingWorkspace.ManualTesting && o.Specialist == SpecialistArea.FrontendQualityReview);

        var api = D.FromContracts(Project, [Rest("ReportService/autodoc/openapi.yaml")]);
        ScenarioOwnership.Of(api.First(c => c.Type == ScenarioCandidateType.ApiContract)).Should().Match<ScenarioOwnershipInfo>(o =>
            o.Purpose == ScenarioPurpose.ApiContractTechnical && o.Primary == TestingWorkspace.TechnicalTesting && o.Secondary == TestingWorkspace.ManualTesting && o.Specialist == SpecialistArea.ContractTesting);
        ScenarioOwnership.Of(api.Single(c => c.Type == ScenarioCandidateType.SecurityAuthorization)).Should().Match<ScenarioOwnershipInfo>(o =>
            o.Purpose == ScenarioPurpose.SecurityTechnical && o.Specialist == SpecialistArea.ApiQualityReview && o.SourceKind == "Authorization check");

        var declared = D.FromDeclaredJourneys(Project, [new DeclaredIntegrationJourney { ProjectKey = Project, JourneyId = "report-intake", DisplayName = "Report intake", Components = ["Altinn", "MM", "SkoleService"] }]).Single();
        var journey = ScenarioOwnership.Of(declared);
        journey.Should().Match<ScenarioOwnershipInfo>(o => o.Purpose == ScenarioPurpose.IntegrationFunctional && o.Primary == TestingWorkspace.TechnicalTesting && o.Specialist == SpecialistArea.IntegrationTesting);
        ScenarioOwnership.IsPackJourney(declared).Should().BeFalse("a journey a person declared for the project is not a test-pack scenario");
        ScenarioOwnership.JourneyParts(declared).Should().Be(($"project.{Project}", "report-intake", (string?)null));
    }

    [Fact]
    public void A_guide_scenario_through_several_systems_is_business_end_to_end_with_integration_testing_as_secondary()
    {
        var e2e = new DiscoveredScenario { CapabilityId = ScenarioOwnership.SystemGuide, Type = ScenarioCandidateType.BusinessE2E, CanonicalKey = "guide:g/valid" };
        ScenarioOwnership.Of(e2e).Should().Match<ScenarioOwnershipInfo>(o => o.Purpose == ScenarioPurpose.BusinessEndToEnd && o.Primary == TestingWorkspace.ManualTesting
            && o.Secondary == TestingWorkspace.TechnicalTesting && o.Specialist == SpecialistArea.IntegrationTesting);
        ScenarioOwnership.Of(e2e with { Type = ScenarioCandidateType.BusinessFunctional }).Should().Match<ScenarioOwnershipInfo>(o => o.Primary == TestingWorkspace.ManualTesting && o.Secondary == null);
    }

    [Fact]
    public void A_test_pack_scenario_is_a_pack_journey_and_an_unknown_producer_is_left_for_review()
    {
        var pack = new DiscoveredScenario { CapabilityId = ScenarioOwnership.IntegrationJourneys, Type = ScenarioCandidateType.Integration, CanonicalKey = "journey:orders:intake:two-buyers" };
        ScenarioOwnership.Of(pack).Specialist.Should().Be(SpecialistArea.TestPackJourney);
        ScenarioOwnership.JourneyParts(pack).Should().Be(("orders", "intake", (string?)"two-buyers"));

        var unknown = ScenarioOwnership.Of(new DiscoveredScenario { CapabilityId = "future-analyzer", Type = ScenarioCandidateType.BusinessFunctional, Title = "Looks like business" });
        unknown.Should().Match<ScenarioOwnershipInfo>(o => o.Purpose == ScenarioPurpose.Unclassified && o.Primary == null && o.NeedsReview, "the title is never used to guess");
        ScenarioOwnership.Of(new DiscoveredScenario { CapabilityId = "future-analyzer", Type = ScenarioCandidateType.Performance }).Specialist.Should().Be(SpecialistArea.PerformanceTestReview);
    }

    [Fact]
    public void Copies_of_one_GraphQL_schema_share_an_operation_identity_but_the_same_REST_path_in_two_services_does_not()
    {
        var copies = D.FromContracts(Project, [GraphQl("AuthService/autodoc/schema.graphql"), GraphQl("Frontend/GraphQL/Authorization/schema.graphql"),
            GraphQl("AuthService/tests/Contract/schema.snapshot.graphql")]);
        copies.Should().HaveCount(3, "each file stays its own candidate (nothing is merged)");
        copies.Select(ScenarioOwnership.OperationIdentity).Distinct().Should().ContainSingle().Which.Should().StartWith("graphql|mutation activateEmergencyAccess");
        copies.Select(c => c.CandidateId).Distinct().Should().HaveCount(3);

        // REST: the contract's own title tells documents apart, not a path folder (archives differ in their root folder).
        var health = D.FromContracts(Project, [Rest("Hub/AuthService/autodoc/openapi.yaml", "Auth API"), Rest("Hub/PersonService/autodoc/openapi.yaml", "Person API")])
            .Where(c => c.Type == ScenarioCandidateType.ApiContract && c.Title.StartsWith("GET /health")).ToList();
        health.Select(ScenarioOwnership.OperationIdentity).Distinct().Should().HaveCount(2, "two services' health endpoints are two endpoints, even under one root folder");
        ScenarioOwnership.ContractFile(health[0]).Should().Be("Hub/AuthService/autodoc/openapi.yaml");
        var copied = D.FromContracts(Project, [Rest("Hub/AuthService/autodoc/openapi.yaml", "Auth API"), Rest("Hub/AuthService/docs/openapi.copy.yaml", "Auth API")])
            .Where(c => c.Type == ScenarioCandidateType.ApiContract && c.Title.StartsWith("GET /health")).ToList();
        copied.Select(ScenarioOwnership.OperationIdentity).Distinct().Should().ContainSingle("two copies of one OpenAPI document declare the same operation");
        ScenarioOwnership.OperationIdentity(D.FromContracts(Project, [Rest("A/openapi.yaml")]).Single(c => c.Type == ScenarioCandidateType.SecurityAuthorization))
            .Should().BeNull("an authorization check is not an operation entry of its own");
    }

    [Fact]
    public void Classifying_changes_no_candidate_and_every_candidate_belongs_to_exactly_one_workspace_or_to_review()
    {
        var all = D.FromContracts(Project, [Rest("Svc/openapi.yaml"), GraphQl("Svc/schema.graphql")]).ToList();
        var before = all.Select(c => (c.CandidateId, c.Fingerprint, c.Title)).ToList();
        var owners = all.Select(ScenarioOwnership.Of).ToList();
        all.Select(c => (c.CandidateId, c.Fingerprint, c.Title)).Should().Equal(before);
        owners.Should().OnlyContain(o => o.ManualRelevant ^ o.Technical || o.NeedsReview);
    }
}
