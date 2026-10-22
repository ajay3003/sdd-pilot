using System.Text.RegularExpressions;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.TestData;
using BirkNext.TestEvidence;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationJourneys;

/// <summary>
/// Journey readiness as domain orchestration over shared evidence: one state per dimension (never one score), Test Data missing as a journey
/// prerequisite, declared infrastructure never "Ready", source-ready but execution-blocked, executed ≠ verified, contracts and tests in bound
/// sources needing review rather than name-confirmed, optional dimensions never blocking, and generic composition code naming no domain.
/// Verification keeps ambiguous runs unresolved and provider conflicts as Review required.
/// </summary>
public sealed class JourneyReadinessTests
{
    private static readonly IntegrationJourneyDefinition Journey = new()
    {
        PackId = "pack", JourneyId = "intake", DisplayName = "Intake", Flow = "A → B", ExecutionMode = JourneyExecutionMode.JourneyRunner,
        Integrations = [new("bus", "Bus", IntegrationKind.ServiceBus, "A", "B", "events")],
        Steps = [new("publish", "Publish", JourneyStepKind.Publication, "A", "published", IntegrationKey: "bus"), new("consume", "Consume", JourneyStepKind.ConsumerProcessing, "B", "consumed")],
        Scenarios = [new("happy", "Happy path", "valid message", JourneyScenarioSupport.Supported)],
    };

    private static JourneyPrerequisite Pre(string key, JourneyPrerequisiteCategory category, JourneyReadinessState state, bool mandatory = true) =>
        new(key, key, category, state, $"{key} detail", mandatory);

    private static IqrSourceSnapshot Snapshot(SourceTestInventory? tests = null, params InfrastructureResource[] resources) => new()
    {
        Id = Guid.NewGuid(), Archive = new("app.zip", "abc", 1), AnalyzedAt = DateTimeOffset.Parse("2026-10-22T08:00:00Z"), TestInventory = tests,
        EvidenceDomains = new SourceEvidenceDomainsSnapshot { Infrastructure = new InfrastructureEvidence { Domain = SourceEvidenceDomain.Infrastructure, Resources = [.. resources] } },
    };

    private static TestDataResolutionResult Resolution(params TestDataRoleResolution[] roles) => new([.. roles], [], null);

    private static IReadOnlyList<JourneyReadinessDimension> Compose(IReadOnlyList<JourneyPrerequisite> prerequisites, IReadOnlyList<string>? runnable = null,
        IReadOnlyList<IqrSourceSnapshot>? bound = null, TestDataResolutionResult? testData = null, IReadOnlyList<JourneyStepStatus>? steps = null) =>
        JourneyReadinessComposer.Compose(new JourneyReadinessInputs(Journey, prerequisites, steps ?? [], runnable ?? [], bound ?? [],
            testData is null ? [] : [new TestDataRequirement { Role = "Payload" }], testData));

    private static JourneyReadinessDimension D(IReadOnlyList<JourneyReadinessDimension> all, JourneyDimension d) => all.Single(x => x.Dimension == d);

    [Fact]
    public void Every_dimension_is_reported_separately_with_an_owner()
    {
        var all = Compose([Pre("integration:bus", JourneyPrerequisiteCategory.Integration, JourneyReadinessState.Ready)]);
        all.Select(d => d.Dimension).Should().BeEquivalentTo(Enum.GetValues<JourneyDimension>()).And.OnlyHaveUniqueItems();
        all.Should().OnlyContain(d => d.Owner.Length > 0 && d.OwnerKey.Length > 0 && d.Evidence.Length > 0);
        D(all, JourneyDimension.Delivery).Optional.Should().BeTrue("delivery evidence never blocks a journey");
    }

    [Fact]
    public void Missing_test_data_is_a_journey_prerequisite_and_optional_roles_never_make_it_missing()
    {
        var missing = D(Compose([], testData: Resolution(new TestDataRoleResolution("Payload", TestDataResolutionState.Unresolved, null, [], "none", null))), JourneyDimension.TestData);
        missing.State.Should().Be(JourneyDimensionState.Missing);
        missing.Evidence.Should().Contain("not a product defect");
        missing.OwnerKey.Should().Be("test-data");

        var optional = D(Compose([], testData: Resolution(new TestDataRoleResolution("Payload", TestDataResolutionState.Resolved, "valid-1", [], "one", "Configured"),
            new TestDataRoleResolution("Duplicate", TestDataResolutionState.Unresolved, null, [], "none", null, Optional: true))), JourneyDimension.TestData);
        optional.State.Should().Be(JourneyDimensionState.Partial);
        optional.Items.Should().Contain(i => i.Contains("approved (valid-1)")).And.Contain(i => i.Contains("Duplicate: missing · optional"));
    }

    [Fact]
    public void Source_ready_but_execution_blocked_and_declared_infrastructure_is_never_ready()
    {
        var bound = Snapshot(null, new InfrastructureResource { Id = "sb", Format = InfrastructureFormat.Terraform, ResourceType = "azurerm_servicebus_topic", LogicalName = "events", CategoryDetail = "Service Bus topic" });
        var all = Compose([Pre("source:app", JourneyPrerequisiteCategory.SourceEvidence, JourneyReadinessState.Ready),
            Pre("trusted-environment", JourneyPrerequisiteCategory.TrustedEnvironment, JourneyReadinessState.NotReady)], bound: [bound]);

        D(all, JourneyDimension.SourceBindings).State.Should().Be(JourneyDimensionState.Ready);
        D(all, JourneyDimension.Execution).State.Should().Be(JourneyDimensionState.Blocked);
        D(all, JourneyDimension.EnvironmentConfiguration).State.Should().Be(JourneyDimensionState.Blocked);
        var infrastructure = D(all, JourneyDimension.Infrastructure);
        infrastructure.State.Should().Be(JourneyDimensionState.Partial);
        infrastructure.Evidence.Should().Contain("Declared in source").And.Contain("Declared ≠ deployed");
        infrastructure.Items.Should().ContainSingle().Which.Should().Contain("runtime observed: no");
    }

    [Fact]
    public void Executed_is_not_verified_and_partial_verification_stays_partial()
    {
        JourneyReadinessComposer.Assessment(null).Should().Be(JourneyAssessmentState.NotRun);
        JourneyReadinessComposer.Assessment(JourneyRunState.NotVerified).Should().Be(JourneyAssessmentState.ExecutedNotVerified);
        JourneyReadinessComposer.Assessment(JourneyRunState.Partial).Should().Be(JourneyAssessmentState.Partial);
        JourneyReadinessComposer.Assessment(JourneyRunState.Completed).Should().Be(JourneyAssessmentState.Verified);

        var verification = D(Compose([], runnable: ["happy"], steps: [new("publish", true, true, true, JourneyStepState.Configured, ""), new("consume", false, false, false, JourneyStepState.NotAssessed, "no observer")]),
            JourneyDimension.Verification);
        verification.State.Should().Be(JourneyDimensionState.Partial);
        verification.Evidence.Should().Contain("1 of 2");
    }

    [Fact]
    public void Tests_in_bound_sources_need_review_until_explicitly_linked_to_the_journey()
    {
        var inventory = new SourceTestInventory
        {
            Status = SourceTestDiscoveryStatus.Complete,
            Projects = [new SourceTestProject { Name = "App.IntegrationTests", Path = "tests/App.IntegrationTests/App.IntegrationTests.csproj", Framework = "xUnit" }],
            Definitions = [new SourceTestDefinition { TestDefinitionId = "t1", StableIdentity = "app/App.IntegrationTests/IntakeTests.Publishes", Project = "App.IntegrationTests",
                FullyQualifiedName = "App.IntegrationTests.IntakeTests.Publishes", ClassName = "IntakeTests", MethodName = "Publishes", Kind = TestKind.Integration, FilePath = "tests/IntakeTests.cs", Line = 4 }],
        };
        var unlinked = D(Compose([], bound: [Snapshot(inventory)]), JourneyDimension.TestEvidence);
        unlinked.State.Should().Be(JourneyDimensionState.ReviewRequired, "a test whose name resembles the journey is not linked to it");

        var linkedInventory = inventory with { Definitions = [inventory.Definitions[0] with { References = [new SourceTestReference("happy", TestReferenceKind.Requirement, TestEvidenceConfidence.Confirmed, "[Trait]", 3)] }] };
        D(Compose([], bound: [Snapshot(linkedInventory)]), JourneyDimension.TestEvidence).State.Should().Be(JourneyDimensionState.Ready);
    }

    [Fact]
    public void Skolenaervaer_declares_domain_test_data_without_payloads_and_never_generates_protected_identities()
    {
        var rapport = new IntegrationJourneyDefinition { JourneyId = SkoleNaervaerJourneyPack.RapportmottakId };
        var flows = new IntegrationJourneyHarness.FakeMessageFlows();
        var kit = new BirkNext.Api.Tests.Services.AltinnTesting.AltinnTestKit();
        var requirements = ((IIntegrationJourneyPack)new SkoleNaervaerJourneyPack(flows, kit.Policy, kit.Contracts(flows))).TestDataRequirements(rapport);
        requirements.Select(r => r.Role).Should().Contain(["Reporter", "Child", "Valid report payload", "Invalid-format payload", "Duplicate-report reference", "Protected identity reference", "Expected downstream result"]);
        requirements.Single(r => r.Role == "Protected identity reference").Should().Match<TestDataRequirement>(r => r.Optional && r.Kind == TestDataKind.EntityReference);
        requirements.Where(r => r.Role is not "Reporter" and not "Child").Should().OnlyContain(r => r.Optional, "roles the executors do not consume never block");
    }

    [Fact]
    public void Generic_composition_code_names_no_domain()
    {
        var api = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BirkNext.Api");
        var file = Path.Combine(api, "Services", "IntegrationJourneys", "JourneyReadinessComposer.cs");
        if (!File.Exists(file)) return; // Source-level check runs where the repository sources are present.
        Regex.IsMatch(File.ReadAllText(file), @"Skole|Utdanning|Rapportmottak|Periodeavslutning|Altinn|TT02|Bufdata|M2LB", RegexOptions.IgnoreCase).Should().BeFalse();
    }

    [Fact]
    public void Verification_conflicts_need_review_and_partly_proven_evidence_is_never_a_pass()
    {
        MessageAssertion A(string id, MessageVerificationVerdict v) => new(id, id, "expected", "observed", v, "basis");
        SkoleNaervaerMessageVerification.Verdict([A("a", MessageVerificationVerdict.Passed), A("b", MessageVerificationVerdict.ReviewRequired)], out var conflict)
            .Should().Be(MessageVerificationVerdict.ReviewRequired);
        conflict.Should().Contain("disagree");
        SkoleNaervaerMessageVerification.Verdict([A("a", MessageVerificationVerdict.Passed), A("b", MessageVerificationVerdict.NotEvaluated)], out var partial)
            .Should().Be(MessageVerificationVerdict.NotEvaluated);
        partial.Should().Contain("does not prove b");
        SkoleNaervaerMessageVerification.Verdict([A("a", MessageVerificationVerdict.Passed), A("b", MessageVerificationVerdict.Failed)], out _).Should().Be(MessageVerificationVerdict.Failed);
        SkoleNaervaerMessageVerification.Verdict([A("a", MessageVerificationVerdict.NotEvaluated)], out _).Should().Be(MessageVerificationVerdict.NotEvaluated);
    }
}
