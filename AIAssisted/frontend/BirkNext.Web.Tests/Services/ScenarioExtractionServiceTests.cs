using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class ScenarioExtractionServiceTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ScenarioExtractionService Build(
        int maxInput = 50_000,
        int minCandidate = 3,
        int maxLineLength = 2_000)
        => new(new ExtractionConfiguration
        {
            MaxInputLengthChars = maxInput,
            MinCandidateLengthChars = minCandidate,
            MaxLineLengthForPatternMatching = maxLineLength,
        });

    private static async Task<ExtractionPipelineResult> ExtractAsync(
        string input,
        int maxInput = 50_000,
        int minCandidate = 3,
        int maxLineLength = 2_000)
        => await Build(maxInput, minCandidate, maxLineLength).ExtractAsync(input);

    // =========================================================================
    // Stage 1: Input Validation Gate
    // =========================================================================

    [Fact]
    public async Task Empty_string_returns_EmptyInput()
    {
        var result = await ExtractAsync(string.Empty);
        result.Status.Should().Be(PipelineStatus.EmptyInput);
        result.Candidates.Should().BeEmpty();
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("   \n  \n  ")]
    public async Task Whitespace_only_returns_EmptyInput(string input)
    {
        var result = await ExtractAsync(input);
        result.Status.Should().Be(PipelineStatus.EmptyInput);
        result.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Input_at_exactly_MaxInputLengthChars_succeeds()
    {
        // "- System MUST comply" = 20 chars; pad to exactly 200 with empty lines
        const int max = 200;
        var bullet = "- System MUST comply";
        var input = bullet + new string('\n', max - bullet.Length);
        input.Length.Should().Be(max);

        var result = await ExtractAsync(input, maxInput: max);

        result.Status.Should().Be(PipelineStatus.Success);
        result.Candidates.Should().HaveCount(1);
    }

    [Fact]
    public async Task Input_one_char_over_MaxInputLengthChars_returns_InputTooLarge()
    {
        const int max = 200;
        var bullet = "- System MUST comply";
        var input = bullet + new string('\n', max - bullet.Length + 1); // max + 1 chars
        input.Length.Should().Be(max + 1);

        var result = await ExtractAsync(input, maxInput: max);

        result.Status.Should().Be(PipelineStatus.InputTooLarge);
        result.Candidates.Should().BeEmpty();
    }

    // =========================================================================
    // Stage 2: Normalization
    // =========================================================================

    [Fact]
    public async Task Windows_line_endings_are_normalized()
    {
        var input = "- System MUST validate input\r\n- System MUST log errors";

        var result = await ExtractAsync(input);

        result.Status.Should().Be(PipelineStatus.Success);
        result.Candidates.Should().HaveCount(2);
        result.Candidates.Should().AllSatisfy(c =>
            c.Classification.Should().Be(ScenarioKind.Requirement));
    }

    [Fact]
    public async Task Utf8_bom_is_stripped_before_processing()
    {
        var input = "﻿- System MUST handle BOM";

        var result = await ExtractAsync(input);

        result.Status.Should().Be(PipelineStatus.Success);
        result.Candidates.Should().ContainSingle()
            .Which.Title.Should().Be("System MUST handle BOM");
    }

    // =========================================================================
    // Stage 3 + Stage 4: Block partitioning and filtering
    // =========================================================================

    [Fact]
    public async Task Heading_only_input_returns_NoResults()
    {
        var input = "# Heading One\n## Heading Two\n### Heading Three";

        var result = await ExtractAsync(input);

        result.Status.Should().Be(PipelineStatus.NoResults);
        result.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Fenced_code_block_content_is_not_extracted()
    {
        var input = "```\n- System MUST be secure\n- FR-001: validate input\n```";

        var result = await ExtractAsync(input);

        result.Status.Should().Be(PipelineStatus.NoResults);
    }

    [Fact]
    public async Task Heading_text_is_propagated_as_ContextHeading()
    {
        var input = "# Authentication\n- The system MUST validate credentials";

        var result = await ExtractAsync(input);

        result.Status.Should().Be(PipelineStatus.Success);
        result.Candidates.Should().ContainSingle()
            .Which.ContextHeading.Should().Be("Authentication");
    }

    [Fact]
    public async Task ContextHeading_is_null_when_no_heading_precedes_candidate()
    {
        var input = "- The system MUST comply with FR-001";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.ContextHeading.Should().BeNull();
    }

    // =========================================================================
    // Stage 5: Content Extraction
    // =========================================================================

    [Fact]
    public async Task Blank_bullet_with_no_text_is_discarded()
    {
        // "- " strips to "" which is below MinCandidateLengthChars
        var input = "- \n- The system MUST log all errors";

        var result = await ExtractAsync(input);

        result.Status.Should().Be(PipelineStatus.Success);
        result.Candidates.Should().ContainSingle()
            .Which.Title.Should().Be("The system MUST log all errors");
    }

    [Fact]
    public async Task Unordered_bullet_list_marker_is_stripped()
    {
        var input = "- The system MUST handle requests\n* The system SHALL return errors\n+ The system SHOULD log";

        var result = await ExtractAsync(input);

        result.Status.Should().Be(PipelineStatus.Success);
        result.Candidates.Select(c => c.Title).Should().BeEquivalentTo(
            "The system MUST handle requests",
            "The system SHALL return errors",
            "The system SHOULD log");
    }

    [Fact]
    public async Task Ordered_list_marker_is_stripped()
    {
        var input = "1. The system MUST authenticate users\n2. The system SHALL store credentials";

        var result = await ExtractAsync(input);

        result.Candidates.Select(c => c.Title).Should().BeEquivalentTo(
            "The system MUST authenticate users",
            "The system SHALL store credentials");
    }

    [Fact]
    public async Task Inline_code_backticks_are_stripped_retaining_inner_text()
    {
        var input = "- The system MUST handle `null` values";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Title.Should().Be("The system MUST handle null values");
    }

    [Fact]
    public async Task Link_syntax_is_replaced_by_display_text()
    {
        var input = "- The system MUST comply with [RFC 2119](https://example.com)";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Title.Should().Be("The system MUST comply with RFC 2119");
    }

    [Fact]
    public async Task Image_syntax_is_stripped_entirely()
    {
        var input = "- The system MUST display ![diagram](img.png) correctly";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Title.Should().Be("The system MUST display  correctly");
    }

    // =========================================================================
    // Stage 6: Classification
    // =========================================================================

    [Fact]
    public async Task BDD_triple_Given_When_Then_is_classified_as_Test()
    {
        var input = "- Given a valid user When they login Then they see the dashboard";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.Test);
    }

    [Fact]
    public async Task BDD_opener_Given_at_start_is_classified_as_Test()
    {
        var input = "- Given a valid user submits the form";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.Test);
    }

    [Theory]
    [InlineData("- The system MUST validate input")]
    [InlineData("- The service SHALL return a response")]
    [InlineData("- Access SHOULD be restricted")]
    [InlineData("- Users MAY opt out of notifications")]
    [InlineData("- The system MUST NOT store plain-text passwords")]
    [InlineData("- The service SHALL NOT expose internal errors")]
    public async Task RFC2119_uppercase_keyword_is_classified_as_Requirement(string input)
    {
        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.Requirement);
    }

    [Theory]
    [InlineData("- The system must validate the token")]
    [InlineData("- Authentication is required to proceed")]
    [InlineData("- Access is required to view reports")]
    public async Task RFC2119_lowercase_keyword_is_classified_as_Requirement(string input)
    {
        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.Requirement);
    }

    [Fact]
    public async Task FR_prefix_is_classified_as_Requirement()
    {
        var input = "- FR-001: The system shall store the user profile";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.Requirement);
    }

    [Fact]
    public async Task Question_mark_terminator_is_classified_as_NeedsClarification()
    {
        var input = "- What happens when the session expires?";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.NeedsClarification);
    }

    [Theory]
    [InlineData("- The retry logic is TBD")]
    [InlineData("- TODO: define the error format")]
    [InlineData("- Rate limit policy is TBC")]
    [InlineData("- Caching strategy is to be defined")]
    public async Task Deferral_marker_is_classified_as_NeedsClarification(string input)
    {
        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.NeedsClarification);
    }

    [Fact]
    public async Task Default_fallback_is_NeedsClarification()
    {
        // No RFC 2119, no BDD, no question, no deferral marker
        var input = "- Consider adding a retry mechanism";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.NeedsClarification);
    }

    [Fact]
    public async Task BDD_takes_priority_over_RFC2119_uppercase_on_same_line()
    {
        // Given...When...Then AND MUST on same line → BDD wins (higher priority)
        var input = "- Given a request When MUST succeed Then it returns 200";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.Test);
    }

    [Fact]
    public async Task Lines_exceeding_MaxLineLengthForPatternMatching_become_NeedsClarification()
    {
        // A line longer than the cap skips pattern matching → Default → NeedsClarification
        var longBullet = "- " + new string('x', 2_001); // exceeds 2000 cap

        var result = await ExtractAsync(longBullet, maxLineLength: 2_000);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.NeedsClarification);
    }

    // =========================================================================
    // Stage 7: Deduplication
    // =========================================================================

    [Fact]
    public async Task Duplicate_bullets_produce_single_candidate()
    {
        var input = "- The system MUST validate input\n- The system MUST validate input";

        var result = await ExtractAsync(input);

        result.Status.Should().Be(PipelineStatus.Success);
        result.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task Deduplication_is_case_insensitive()
    {
        var input = "- The system MUST validate input\n- the system MUST validate input";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task Deduplication_keeps_first_occurrence()
    {
        var input = "- The system MUST validate input\n- the system MUST validate input";

        var result = await ExtractAsync(input);

        result.Candidates[0].Title.Should().Be("The system MUST validate input");
    }

    // =========================================================================
    // Stage 8: Result Assembly and invariants
    // =========================================================================

    [Fact]
    public async Task RequirementCount_plus_TestCount_plus_NeedsClarificationCount_equals_CandidatesCount()
    {
        var input = string.Join('\n',
            "- The system MUST validate tokens",
            "- Given a user When they log in Then they see the home page",
            "- What is the retry policy?",
            "- The system MUST log all errors",
            "- TBD: define rate limits");

        var result = await ExtractAsync(input);

        result.Status.Should().Be(PipelineStatus.Success);
        (result.RequirementCount + result.TestCount + result.NeedsClarificationCount)
            .Should().Be(result.Candidates.Count);
    }

    [Fact]
    public async Task Classification_counts_match_candidate_classifications()
    {
        var input = string.Join('\n',
            "- The system MUST validate tokens",
            "- The system SHALL log access",
            "- Given a user When logged in Then see dashboard",
            "- What is the session timeout?",
            "- Retry logic is TBD");

        var result = await ExtractAsync(input);

        result.RequirementCount.Should().Be(
            result.Candidates.Count(c => c.Classification == ScenarioKind.Requirement));
        result.TestCount.Should().Be(
            result.Candidates.Count(c => c.Classification == ScenarioKind.Test));
        result.NeedsClarificationCount.Should().Be(
            result.Candidates.Count(c => c.Classification == ScenarioKind.NeedsClarification));
    }

    [Fact]
    public async Task NonSuccess_result_has_zero_counts_and_empty_candidates()
    {
        var result = await ExtractAsync("# Only a heading");

        result.Status.Should().Be(PipelineStatus.NoResults);
        result.Candidates.Should().BeEmpty();
        result.RequirementCount.Should().Be(0);
        result.TestCount.Should().Be(0);
        result.NeedsClarificationCount.Should().Be(0);
    }

    [Fact]
    public async Task DurationMs_is_non_negative_on_Success()
    {
        var input = string.Join('\n', Enumerable.Range(1, 20)
            .Select(i => $"- FR-{i:000}: The system MUST handle case {i}"));

        var result = await ExtractAsync(input);

        result.Status.Should().Be(PipelineStatus.Success);
        result.DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task InputLengthChars_reflects_raw_input_length()
    {
        var input = "- System MUST comply\n";

        var result = await ExtractAsync(input);

        result.InputLengthChars.Should().Be(input.Length);
    }

    [Fact]
    public async Task InputLineCount_reflects_normalized_line_count()
    {
        var input = "- System MUST comply\n- System SHALL log";
        // After split on \n: ["- System MUST comply", "- System SHALL log"] = 2 lines

        var result = await ExtractAsync(input);

        result.InputLineCount.Should().Be(2);
    }

    [Fact]
    public async Task Each_candidate_has_unique_CandidateId()
    {
        var input = string.Join('\n',
            "- The system MUST validate input",
            "- The system SHALL log errors",
            "- Given a user When active Then session created");

        var result = await ExtractAsync(input);

        result.Candidates.Select(c => c.CandidateId)
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Candidates_default_IsSelected_false_and_SaveState_Pending()
    {
        var input = "- The system MUST handle requests";

        var result = await ExtractAsync(input);

        var candidate = result.Candidates.Should().ContainSingle().Subject;
        candidate.IsSelected.Should().BeFalse();
        candidate.SaveState.Should().Be(CandidateSaveState.Pending);
        candidate.SaveError.Should().BeNull();
        candidate.SavedScenarioId.Should().BeNull();
    }

    // =========================================================================
    // Mixed scenarios
    // =========================================================================

    [Fact]
    public async Task Input_with_no_extractable_content_after_filtering_returns_NoResults()
    {
        // All filtered: headings, fenced code, horizontal rule, empty lines
        var input = "# Section\n## Subsection\n---\n```\ncode\n```\n";

        var result = await ExtractAsync(input);

        result.Status.Should().Be(PipelineStatus.NoResults);
    }

    [Fact]
    public async Task Mixed_list_types_are_all_extracted()
    {
        var input = "- Unordered MUST item\n1. Ordered MUST item";

        var result = await ExtractAsync(input);

        result.Candidates.Should().HaveCount(2);
        result.Candidates.Should().AllSatisfy(c =>
            c.Classification.Should().Be(ScenarioKind.Requirement));
    }

    [Fact]
    public async Task ContextHeading_updates_as_document_sections_change()
    {
        var input = string.Join('\n',
            "# Authentication",
            "- The system MUST validate the token",
            "# Logging",
            "- The system SHALL record all events");

        var result = await ExtractAsync(input);

        result.Candidates.Should().HaveCount(2);
        result.Candidates[0].ContextHeading.Should().Be("Authentication");
        result.Candidates[1].ContextHeading.Should().Be("Logging");
    }
}
