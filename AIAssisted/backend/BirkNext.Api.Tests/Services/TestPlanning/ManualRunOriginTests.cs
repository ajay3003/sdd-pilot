using System.Text.Json;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// End-to-end traceability specification → scenario → case → manual run: a run pins where its case came from (source, scenario fingerprint,
/// specification file and acceptance scenario, each requirement's file and basis), the same derivation Requirements Traceability uses. The case
/// fingerprint moved to shared code for the web app and must stay byte-identical to the one existing runs were pinned with.
/// </summary>
public sealed class ManualRunOriginTests
{
    private static TestCaseDraft FromSpecification() => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "import:p1", ScenarioId = "spec-us1-1", Fingerprint = "f00d" },
        Generated = new TestCaseContent
        {
            Title = "Borrow a book", Steps = ["Open the loan page", "Register the loan"], StepExpected = ["Shown", "Registered"], ExpectedResult = "The loan is listed",
            Capability = TestScenarioDiscoveryEngine.Specification,
            Requirements = [new("FR-001", "Borrow", null), new("FR-002", "List loans", null)],
            Evidence = [new("specification", "specs/001-loans/spec.md#US1-1"), new("requirement", "specs/001-loans/spec.md#FR-001"), new("requirement", "specs/002-list/spec.md#FR-002")],
        },
        RequirementLinks = [new ManualRequirementLink { RequirementId = "FR-009", SpecificationPath = "specs/001-loans/spec.md", LinkedBy = "Kari", LinkedAt = DateTimeOffset.UtcNow }],
    };

    [Fact]
    public void A_run_pins_the_specification_scenario_and_each_requirements_file_and_basis()
    {
        var origin = ManualCaseIdentity.Origin(FromSpecification());

        origin.SourceKind.Should().Be(ScenarioSourceKind.ProjectDiscovery);
        origin.ScenarioFingerprint.Should().Be("f00d");
        origin.SpecificationFile.Should().Be("specs/001-loans/spec.md");
        origin.SpecificationScenarioId.Should().Be("US1-1");
        origin.Requirements.Should().Equal(
            new ManualRunRequirementSource("FR-001", "specs/001-loans/spec.md", "Specification evidence"),
            new ManualRunRequirementSource("FR-002", "specs/002-list/spec.md", "Specification evidence"),
            new ManualRunRequirementSource("FR-009", "specs/001-loans/spec.md", "Manual link", "Kari"));
    }

    [Fact]
    public void A_case_written_by_hand_has_no_specification_origin_but_keeps_its_manual_links()
    {
        var manual = FromSpecification() with
        {
            Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, Owner = "import:p1", ScenarioId = "manual-1" },
            Generated = FromSpecification().Generated with { Capability = null },
        };
        var origin = ManualCaseIdentity.Origin(manual);

        (origin.SourceKind, origin.SpecificationFile, origin.SpecificationScenarioId).Should().Be((ScenarioSourceKind.Manual, (string?)null, (string?)null));
        origin.Requirements.Should().ContainSingle().Which.Basis.Should().Be("Manual link");
    }

    [Fact]
    public void The_shared_case_fingerprint_is_byte_identical_to_the_one_existing_runs_were_pinned_with()
    {
        var c = FromSpecification();
        var old = TestPlanSuggestionEngine.Sha(JsonSerializer.Serialize(
            new { c.EffectiveTitle, c.EffectivePreconditions, Steps = c.EffectiveSteps, Expected = c.EffectiveStepExpected, c.EffectiveExpected }, TestPlanSuggestionEngine.Json));

        ManualRunRules.CaseFingerprint(c).Should().Be(old);
        ManualCaseIdentity.Fingerprint(c).Should().Be(old);
    }
}
