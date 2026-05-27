using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Service-level tests for the Speckit extraction profile.
/// Verifies heading-aware classification, metadata filtering, identifier prefixes,
/// priority ordering between Speckit rules and base rules, and profile isolation.
/// </summary>
public sealed class SpeckitProfileTests
{
    // ── helpers ─────────────────────────────────────────────────────────────────

    private static ScenarioExtractionService BuildService()
        => new(new ExtractionConfiguration
        {
            MaxInputLengthChars = 50_000,
            MinCandidateLengthChars = 3,
            MaxLineLengthForPatternMatching = 2_000,
        });

    private static async Task<ExtractionPipelineResult> ExtractDefault(string input)
        => await BuildService().ExtractAsync(input, ExtractionProfile.Default);

    private static async Task<ExtractionPipelineResult> ExtractSpeckit(string input)
        => await BuildService().ExtractAsync(input, ExtractionProfile.Speckit);

    // ── profile metadata ────────────────────────────────────────────────────────

    [Fact]
    public async Task Default_profile_result_has_Default_profile_property()
    {
        var result = await ExtractDefault("- The system MUST validate input.");
        result.Profile.Should().Be(ExtractionProfile.Default);
    }

    [Fact]
    public async Task Speckit_profile_result_has_Speckit_profile_property()
    {
        var result = await ExtractSpeckit("- The system MUST validate input.");
        result.Profile.Should().Be(ExtractionProfile.Speckit);
    }

    // ── Default profile behavior preserved ──────────────────────────────────────

    [Fact]
    public async Task Default_profile_behavior_unchanged_for_RFC2119_lines()
    {
        const string input = "- The system MUST validate credentials.";
        var defaultResult = await ExtractDefault(input);
        var speckitResult = await ExtractSpeckit(input);

        // Both profiles should classify MUST as REQUIREMENT with Rfc2119Uppercase signal
        defaultResult.Candidates.Should().HaveCount(1);
        speckitResult.Candidates.Should().HaveCount(1);
        defaultResult.Candidates[0].Classification.Should().Be(ScenarioKind.Requirement);
        speckitResult.Candidates[0].Classification.Should().Be(ScenarioKind.Requirement);
        defaultResult.Candidates[0].ClassificationSignal.Should().Be(ClassificationSignal.Rfc2119Uppercase);
        speckitResult.Candidates[0].ClassificationSignal.Should().Be(ClassificationSignal.Rfc2119Uppercase);
    }

    // ── Speckit IgnorePrefixes ───────────────────────────────────────────────────

    [Fact]
    public async Task Speckit_filters_Input_metadata_line()
    {
        const string input = "- Input: some value here";
        var result = await ExtractSpeckit(input);
        result.Candidates.Should().BeEmpty("Input: metadata lines must be filtered in Speckit profile");
    }

    [Fact]
    public async Task Default_does_not_filter_Input_metadata_line()
    {
        const string input = "- Input: some value here that is long enough";
        var result = await ExtractDefault(input);
        result.Candidates.Should().HaveCount(1, "Input: is not filtered in Default profile");
    }

    // ── NFR/SC prefix rules ──────────────────────────────────────────────────────

    [Fact]
    public async Task Speckit_classifies_NFR_prefix_as_requirement()
    {
        const string input = "- NFR-001 The system must handle 100 requests per second.";
        var result = await ExtractSpeckit(input);

        result.Candidates.Should().HaveCount(1);
        result.Candidates[0].Classification.Should().Be(ScenarioKind.Requirement);
    }

    [Fact]
    public async Task Speckit_classifies_SC_prefix_as_requirement()
    {
        const string input = "- SC-002 Users are constrained to their own organisation data.";
        var result = await ExtractSpeckit(input);

        result.Candidates.Should().HaveCount(1);
        result.Candidates[0].Classification.Should().Be(ScenarioKind.Requirement);
    }

    // ── Heading context rules ────────────────────────────────────────────────────

    [Fact]
    public async Task Speckit_Acceptance_Criteria_heading_classifies_plain_line_as_test()
    {
        // "User can log in" has RequirementLanguage signal in Default → REQUIREMENT.
        // Under Acceptance Criteria heading in Speckit → TEST (TestSection:16 > RequirementLanguage:15).
        const string input = """
## Acceptance Criteria

- User can log in with valid credentials.
""";
        var defaultResult = await ExtractDefault(input);
        var speckitResult = await ExtractSpeckit(input);

        defaultResult.Candidates[0].Classification.Should().Be(ScenarioKind.Requirement,
            "Default profile has no heading context rule; RequirementLanguage wins");
        speckitResult.Candidates[0].Classification.Should().Be(ScenarioKind.Test,
            "Speckit TestSection:16 rule overrides RequirementLanguage:15 under Acceptance Criteria heading");
    }

    [Fact]
    public async Task Speckit_Open_Questions_heading_classifies_plain_line_as_needs_clarification()
    {
        const string input = """
## Open Questions

- Session timeout policy is TBD.
""";
        var result = await ExtractSpeckit(input);

        result.Candidates.Should().HaveCount(1);
        result.Candidates[0].Classification.Should().Be(ScenarioKind.NeedsClarification);
        result.Candidates[0].ClassificationSignal.Should().Be(ClassificationSignal.DeferralMarker,
            "TBD keyword wins at priority 20 over ClarificationSection at priority 16");
    }

    [Fact]
    public async Task Speckit_Functional_Requirements_heading_lifts_plain_line_to_requirement()
    {
        // A plain statement with no RFC2119 or RequirementLanguage keyword.
        // In Default → NeedsClarification (Default fallback).
        // In Speckit under Functional Requirements heading → REQUIREMENT (RequirementSection:10).
        const string input = """
## Functional Requirements

- Log all user authentication events.
""";
        var defaultResult = await ExtractDefault(input);
        var speckitResult = await ExtractSpeckit(input);

        defaultResult.Candidates[0].Classification.Should().Be(ScenarioKind.NeedsClarification,
            "Default profile has no heading context rule for this plain statement");
        speckitResult.Candidates[0].Classification.Should().Be(ScenarioKind.Requirement,
            "Speckit RequirementSection:10 lifts plain statement under Functional Requirements heading");
    }

    [Fact]
    public async Task Speckit_Observability_heading_lifts_plain_line_to_requirement()
    {
        const string input = """
## Observability

- Emit structured logs for all database queries.
""";
        var result = await ExtractSpeckit(input);

        result.Candidates.Should().HaveCount(1);
        result.Candidates[0].Classification.Should().Be(ScenarioKind.Requirement);
        result.Candidates[0].ClassificationSignal.Should().Be(ClassificationSignal.HeadingContext);
    }

    // ── Priority ordering ────────────────────────────────────────────────────────

    [Fact]
    public async Task Speckit_QuestionTerminator_overrides_Functional_Requirements_section()
    {
        // QuestionTerminator (30) > RequirementSection (10) — question under a requirement heading stays NC.
        const string input = """
## Functional Requirements

- Should we support OAuth2?
""";
        var result = await ExtractSpeckit(input);

        result.Candidates.Should().HaveCount(1);
        result.Candidates[0].Classification.Should().Be(ScenarioKind.NeedsClarification,
            "QuestionTerminator (30) beats RequirementSection (10)");
    }

    [Fact]
    public async Task Speckit_Rfc2119Uppercase_overrides_Acceptance_Criteria_section()
    {
        // Rfc2119Uppercase (60) > TestSection (16) — MUST under Acceptance Criteria → REQUIREMENT.
        const string input = """
## Acceptance Criteria

- The system MUST reject invalid tokens.
""";
        var result = await ExtractSpeckit(input);

        result.Candidates.Should().HaveCount(1);
        result.Candidates[0].Classification.Should().Be(ScenarioKind.Requirement,
            "Rfc2119Uppercase (60) beats TestSection (16)");
    }

    [Fact]
    public async Task Speckit_BDD_pattern_overrides_requirements_section()
    {
        // BddPattern (70) > RequirementSection (10).
        const string input = """
## Functional Requirements

- Given a valid user When they log in Then they are redirected to the dashboard.
""";
        var result = await ExtractSpeckit(input);

        result.Candidates.Should().HaveCount(1);
        result.Candidates[0].Classification.Should().Be(ScenarioKind.Test,
            "BddPattern (70) beats RequirementSection (10)");
    }

    // ── Context heading preserved ────────────────────────────────────────────────

    [Theory]
    [InlineData("Why this priority")]
    [InlineData("Business value")]
    [InlineData("Goals")]
    [InlineData("Summary")]
    [InlineData("Future evolution")]
    [InlineData("Non-goals")]
    [InlineData("Background")]
    [InlineData("Context")]
    [InlineData("Assumptions")]
    [InlineData("Scope")]
    [InlineData("User Workflow Impact")]
    [InlineData("Determinism Guarantees")]
    [InlineData("Configuration Boundaries")]
    [InlineData("Fallback and Default Behavior")]
    public async Task Speckit_narrative_labels_under_acceptance_heading_are_not_test(string label)
    {
        var input = $"""
## Acceptance Criteria

- {label}
""";

        var result = await ExtractSpeckit(input);

        result.Candidates.Should().ContainSingle();
        result.Candidates[0].Classification.Should().Be(ScenarioKind.NeedsClarification,
            "narrative labels are documentation structure, not executable scenarios");
        result.Candidates[0].Classification.Should().NotBe(ScenarioKind.Test);
    }

    [Fact]
    public async Task Speckit_Why_this_priority_never_becomes_test_under_test_section()
    {
        const string input = """
## Tests

- Why this priority
""";

        var result = await ExtractSpeckit(input);

        result.Candidates.Should().ContainSingle();
        result.Candidates[0].Classification.Should().Be(ScenarioKind.NeedsClarification);
        result.Candidates[0].Title.Should().Be("Why this priority");
    }

    [Fact]
    public async Task Speckit_generic_scenarios_heading_does_not_classify_plain_narrative_as_test()
    {
        const string input = """
## Scenarios

- Summary of the rollout approach.
""";

        var result = await ExtractSpeckit(input);

        result.Candidates.Should().ContainSingle();
        result.Candidates[0].Classification.Should().Be(ScenarioKind.NeedsClarification,
            "generic Scenarios headings are not enough to infer an executable test");
    }

    [Fact]
    public async Task Speckit_BDD_under_generic_scenarios_heading_remains_test()
    {
        const string input = """
## Scenarios

- Given a valid user When they submit credentials Then they see the dashboard.
""";

        var result = await ExtractSpeckit(input);

        result.Candidates.Should().ContainSingle();
        result.Candidates[0].Classification.Should().Be(ScenarioKind.Test,
            "Given/When/Then remains a strong TEST signal independent of heading context");
    }

    [Fact]
    public async Task Speckit_preserves_context_heading_on_extracted_candidate()
    {
        const string input = """
## Acceptance Criteria

- User can log in with valid credentials.
""";
        var result = await ExtractSpeckit(input);

        result.Candidates.Should().HaveCount(1);
        result.Candidates[0].ContextHeading.Should().Be("Acceptance Criteria");
    }

    // ── Profile isolation ────────────────────────────────────────────────────────

    [Fact]
    public async Task Profile_change_does_not_mutate_Default_extraction_candidates()
    {
        const string input = """
## Acceptance Criteria

- User can log in with valid credentials.
""";
        var service = BuildService();

        // Run Default first, then Speckit — verify Default result is unchanged.
        var defaultResult = await service.ExtractAsync(input, ExtractionProfile.Default);
        var speckitResult = await service.ExtractAsync(input, ExtractionProfile.Speckit);

        defaultResult.Candidates[0].Classification.Should().Be(ScenarioKind.Requirement,
            "Default profile must not be mutated by a subsequent Speckit extraction");
        speckitResult.Candidates[0].Classification.Should().Be(ScenarioKind.Test,
            "Speckit profile must classify correctly after Default was run first");
    }
}
