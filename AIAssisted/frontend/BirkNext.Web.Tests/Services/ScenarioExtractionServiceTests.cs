using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit.Abstractions;

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
    // Stage 7 — Enhanced near-duplicate deduplication (normalized key)
    // =========================================================================

    [Fact]
    public async Task Dedup_punctuation_normalized()
    {
        // Terminal punctuation stripped from key — "validate input." and "validate input" same key.
        var input = "- Must validate input.\n- Must validate input";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task Dedup_leading_article_normalized()
    {
        // "The system must validate input" and "A system must validate input" both reduce to
        // "validate input" after article + subject+modal stripping.
        var input = "- The system must validate input\n- A system must validate input";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task Dedup_subject_modal_phrase_normalized()
    {
        // "System must" and "Application should" are both subject+modal prefixes that normalize away.
        // RFC 2119 signal on first item gives it a higher quality score → survives.
        var input = "- System must validate credentials\n- Application should validate credentials";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle();
        result.Candidates[0].Title.Should().Contain("System must",
            "the RFC 2119 candidate should survive the quality comparison");
    }

    [Fact]
    public async Task Dedup_leading_modal_normalized()
    {
        // Both reduce to key "validate credentials" after leading modal strip.
        var input = "- Must validate credentials\n- Should validate credentials";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task Dedup_subject_and_modal_both_stripped()
    {
        // "System should validate user input" → key "validate user input"
        // "Validate user input" → key "validate user input"
        var input = "- System should validate user input\n- Validate user input";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle();
    }

    [Fact]
    public async Task Dedup_distinct_objects_preserved()
    {
        // "validate the title" and "validate the description" differ in their object → preserved.
        var input = "- System must validate the title\n- System must validate the description";

        var result = await ExtractAsync(input);

        result.Candidates.Should().HaveCount(2,
            "different objects (title vs description) produce different keys");
    }

    [Fact]
    public async Task Dedup_distinct_subjects_preserved()
    {
        // "Admin" and "User" are not in the subject-stripping list; keys remain different.
        var input = "- Admin must delete records\n- User must delete records";

        var result = await ExtractAsync(input);

        result.Candidates.Should().HaveCount(2,
            "different actors (admin vs user) are not normalized away");
    }

    [Fact]
    public async Task Dedup_quality_higher_score_survives()
    {
        // Short fragment appears first; longer explicit RFC requirement appears second.
        // Quality score prefers the longer RFC candidate even though it comes later.
        var input = "- Validate credentials\n- System must validate credentials";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Title.Should().Contain("System must",
                "the longer RFC 2119 candidate outscores the fragment");
    }

    [Fact]
    public async Task Dedup_stable_ordering_winners_in_doc_order()
    {
        // A is unique; B normalizes to same key as A (lower quality → dropped);
        // C is unique; D normalizes to same key as C (higher quality → C replaced by D at its position).
        // Output order must reflect original document positions.
        const string input = """
            - System must log all events
            - Should log all events
            - System must validate the user token
            - Application must validate the user token
            """;

        var result = await ExtractAsync(input);

        // "log all events" group: item 1 (System must, RFC) vs item 2 (Should, RequirementLang)
        // → item 1 wins (higher score). "validate the user token" group: both RFC, equal length
        // → item 3 wins (first occurrence).
        result.Candidates.Should().HaveCount(2);
        result.Candidates[0].Title.Should().Contain("log");
        result.Candidates[1].Title.Should().Contain("validate");
    }

    [Fact]
    public async Task Dedup_word_order_differences_not_normalized()
    {
        // "Validation failures should be logged" vs "System should log validation failures":
        // these have different keys (word-order changes are NOT normalized — by design).
        var input = "- Validation failures should be logged\n- System should log validation failures";

        var result = await ExtractAsync(input);

        result.Candidates.Should().HaveCount(2,
            "word-order differences are not normalized; deterministic dedup only covers structural noise");
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

    // =========================================================================
    // Stage 5.5 — Default IgnorePrefixes: metadata and section-heading filtering
    // =========================================================================

    [Theory]
    [InlineData("Feature Branch: main")]
    [InlineData("Feature Branch")]
    [InlineData("Created: 2026-01-01")]
    [InlineData("Status: Draft")]
    [InlineData("Priority: P1")]
    [InlineData("Author: Alice")]
    [InlineData("Version: 1.0")]
    [InlineData("Updated: 2026-05-01")]
    [InlineData("Tags: auth, api")]
    public async Task Metadata_lines_are_filtered_by_default_ignore_prefixes(string line)
    {
        // Metadata lines match default IgnorePrefixes and are discarded at Stage 5.5.
        var result = await ExtractAsync($"- {line}");

        result.Status.Should().Be(PipelineStatus.NoResults,
            $"metadata line \"{line}\" should be filtered before classification");
    }

    [Theory]
    [InlineData("Acceptance Scenarios")]
    [InlineData("Acceptance Scenarios:")]
    [InlineData("Key Entities")]
    [InlineData("Observability")]
    [InlineData("Measurable Outcomes")]
    [InlineData("Edge Cases")]
    [InlineData("Functional Requirements")]
    [InlineData("Non-Goals")]
    [InlineData("Independent Test: Can be fully tested by submitting the form")]
    public async Task Section_heading_labels_are_filtered_by_default_ignore_prefixes(string line)
    {
        // Standalone section-heading labels match default IgnorePrefixes.
        var result = await ExtractAsync($"- {line}");

        result.Status.Should().Be(PipelineStatus.NoResults,
            $"section heading label \"{line}\" should be filtered before classification");
    }

    // =========================================================================
    // Stage 5 — Bold/italic stripping in StripMarkdown
    // =========================================================================

    [Fact]
    public async Task Bold_formatted_Given_When_Then_classifies_as_Test()
    {
        // **Given**/**When**/**Then** bold markers are stripped before classification.
        var input = "1. **Given** a user submits the form, **When** the form is valid, **Then** the scenario is saved.";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.Test);
    }

    [Fact]
    public async Task Bold_formatted_requirement_classifies_as_Requirement()
    {
        var input = "- **The system MUST validate** the input before saving";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.Requirement);
    }

    [Fact]
    public async Task Bold_metadata_line_is_filtered_after_bold_strip()
    {
        // **Feature Branch**: value → "Feature Branch: value" after bold strip → filtered by IgnorePrefix.
        var input = "**Feature Branch**: `001-create-scenario`";

        var result = await ExtractAsync(input);

        result.Status.Should().Be(PipelineStatus.NoResults,
            "bold-formatted metadata line should be stripped and then filtered");
    }

    [Fact]
    public async Task Bold_stripped_title_has_no_asterisks()
    {
        var input = "- **The system MUST handle** requests";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Title.Should().NotContain("**");
    }

    // =========================================================================
    // Stage 6 — RequirementLanguage classification (should / can)
    // =========================================================================

    [Theory]
    [InlineData("- Validation failures should be logged")]
    [InlineData("- Successful scenario creation should be logged")]
    [InlineData("- Response time for scenario creation should be measurable")]
    [InlineData("- Technical failures should be logged with correlation context")]
    public async Task Should_language_lines_classify_as_Requirement(string input)
    {
        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.Requirement);
    }

    [Theory]
    [InlineData("- The feature can be enabled by the administrator")]
    [InlineData("- Users can opt out of notifications")]
    public async Task Can_language_lines_classify_as_Requirement(string input)
    {
        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.Requirement);
    }

    [Fact]
    public async Task Should_question_remains_NeedsClarification()
    {
        // "should" in a question-terminated line → QuestionTerminator (30) > RequirementLanguage (15).
        var input = "- What should happen when the session expires?";

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.NeedsClarification);
    }

    // =========================================================================
    // Stage 6 — ClarificationSignal classification (strong ambiguity phrases)
    // =========================================================================

    [Theory]
    [InlineData("- How should we handle double-submit?")]
    [InlineData("- Should we implement pagination for the initial version?")]
    [InlineData("- What happens if the backend is unavailable?")]
    [InlineData("- This behavior is unresolved and needs further discussion")]
    [InlineData("- Needs decision on the retry policy before implementation")]
    [InlineData("- Please clarify the expected timeout behavior")]
    public async Task Strong_clarification_signals_classify_as_NeedsClarification(string input)
    {
        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.NeedsClarification);
    }

    // =========================================================================
    // Stage 5.3 — BDD grouping: orphaned And/But are dropped
    // =========================================================================

    [Theory]
    [InlineData("- And the user sees a confirmation message")]
    [InlineData("- But the scenario is not saved when validation fails")]
    public async Task Gherkin_And_But_orphaned_produce_no_results(string input)
    {
        // A standalone And/But line with no preceding Given/When/Then is an orphaned
        // continuation — GroupBddSteps drops it and no candidates are produced.
        var result = await ExtractAsync(input);

        result.Status.Should().Be(PipelineStatus.NoResults,
            "standalone And/But without a preceding Given/When/Then are dropped as orphaned continuers");
    }

    // =========================================================================
    // Stage 5.3 — BDD grouping: adjacent steps merged into single TEST candidate
    // =========================================================================

    [Fact]
    public async Task BddGrouping_adjacent_Given_When_Then_merged_into_single_test()
    {
        const string input = """
            - Given a user is on the login page
            - When they enter valid credentials
            - Then they are redirected to the dashboard
            """;

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.Test);
        result.Candidates[0].Title.Should().Contain("Given")
            .And.Contain("When")
            .And.Contain("Then");
    }

    [Fact]
    public async Task BddGrouping_And_continuation_merged_into_group()
    {
        const string input = """
            - Given a user submits the form
            - When the input is valid
            - Then the scenario is saved
            - And a confirmation message is shown
            """;

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.Test);
        result.Candidates[0].Title.Should().Contain("And a confirmation message is shown");
    }

    [Fact]
    public async Task BddGrouping_But_continuation_merged_into_group()
    {
        const string input = """
            - Given a user is logged in
            - When they try an invalid action
            - But the system rejects the request
            """;

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.Test);
        result.Candidates[0].Title.Should().Contain("But the system rejects the request");
    }

    [Fact]
    public async Task BddGrouping_orphaned_And_is_dropped()
    {
        var result = await ExtractAsync("- And the title is not empty");

        result.Status.Should().Be(PipelineStatus.NoResults);
    }

    [Fact]
    public async Task BddGrouping_orphaned_But_is_dropped()
    {
        var result = await ExtractAsync("- But the form fails");

        result.Status.Should().Be(PipelineStatus.NoResults);
    }

    [Fact]
    public async Task BddGrouping_non_bdd_line_between_steps_breaks_group()
    {
        // A non-BDD line interrupts the group; Given is a standalone test,
        // When+Then form a separate merged test.
        const string input = """
            - Given the system is running
            - The database is available
            - When a request arrives
            - Then a response is returned
            """;

        var result = await ExtractAsync(input);

        result.TestCount.Should().Be(2,
            "Given is a standalone TEST; When+Then merge into a second TEST");
    }

    [Fact]
    public async Task BddGrouping_second_Given_starts_new_group()
    {
        const string input = """
            - Given user A logs in
            - When they view the dashboard
            - Then they see their projects
            - Given user B logs in
            - When they view the dashboard
            - Then they see a different set of projects
            """;

        var result = await ExtractAsync(input);

        result.TestCount.Should().Be(2,
            "each Given keyword starts a new scenario group");
    }

    [Fact]
    public async Task BddGrouping_When_Then_without_Given_merged()
    {
        // When/Then without a preceding Given form a valid group.
        const string input = """
            - When the session expires
            - Then the user is redirected to the login page
            """;

        var result = await ExtractAsync(input);

        result.Candidates.Should().ContainSingle()
            .Which.Classification.Should().Be(ScenarioKind.Test);
        result.Candidates[0].Title.Should().Contain("When").And.Contain("Then");
    }

    // =========================================================================
    // Stage 5.5 — BDD label IgnorePrefixes (Given:, When:, Then:, And:, But:)
    // =========================================================================

    [Theory]
    [InlineData("- Given:")]
    [InlineData("- Given: setup step")]
    public async Task BddLabel_Given_colon_filtered(string input)
    {
        var result = await ExtractAsync(input);

        result.Status.Should().Be(PipelineStatus.NoResults,
            "bare 'Given:' label lines are filtered by the default IgnorePrefixes");
    }

    [Theory]
    [InlineData("- When:")]
    [InlineData("- When: the user clicks submit")]
    public async Task BddLabel_When_colon_filtered(string input)
    {
        var result = await ExtractAsync(input);

        result.Status.Should().Be(PipelineStatus.NoResults,
            "bare 'When:' label lines are filtered by the default IgnorePrefixes");
    }

    [Theory]
    [InlineData("- Then:")]
    [InlineData("- Then: the result is shown")]
    public async Task BddLabel_Then_colon_filtered(string input)
    {
        var result = await ExtractAsync(input);

        result.Status.Should().Be(PipelineStatus.NoResults,
            "bare 'Then:' label lines are filtered by the default IgnorePrefixes");
    }

    // =========================================================================
    // Spec.md import quality — fewer noisy NeedsClarification results
    // =========================================================================

    [Fact]
    public async Task SpecLike_document_NeedsClarification_does_not_exceed_explicit_question_count()
    {
        // A representative spec document: headings, bold metadata, BDD acceptance criteria,
        // FR requirements, observability bullets, and explicit open questions.
        // After improvements, NeedsClarification count should not exceed the number of lines
        // that contain genuine clarification signals (question marks or strong phrases).
        const string specLike = """
            # Feature Specification: Login

            **Feature Branch**: `feature/login`
            **Created**: 2026-01-01
            **Status**: Draft

            ---

            ## Functional Requirements

            - FR-001: System MUST validate credentials before granting access.
            - FR-002: System MUST lock account after 5 failed attempts.
            - FR-003: System MUST log all authentication events.

            ## Observability

            - Successful logins should be logged with user identifier.
            - Failed login attempts should be logged with reason code.
            - Authentication latency should be measurable per request.

            ## Acceptance Scenarios

            1. **Given** a user with valid credentials, **When** they submit the login form, **Then** access is granted.
            2. **Given** a user with invalid credentials, **When** they submit the login form, **Then** an error is shown.

            ## Edge Cases

            - What happens if the identity provider is unavailable?
            - How should we handle expired tokens?

            ---
            """;

        var result = await ExtractAsync(specLike);

        result.Status.Should().Be(PipelineStatus.Success);

        // Explicit clarification signals: lines with "?" or strong clarification phrases.
        int genuineClarifications = result.Candidates
            .Count(c => c.Title.EndsWith('?')
                || c.Title.Contains("what happens if", StringComparison.OrdinalIgnoreCase)
                || c.Title.Contains("how should", StringComparison.OrdinalIgnoreCase));

        result.NeedsClarificationCount.Should().BeLessThanOrEqualTo(genuineClarifications + 1,
            "NeedsClarification should be limited to lines with genuine clarification signals");

        result.RequirementCount.Should().BeGreaterThan(0,
            "requirement-language lines (should be logged, MUST, FR-) must be classified as REQUIREMENT");

        result.TestCount.Should().BeGreaterThan(0,
            "BDD acceptance-criteria lines must be classified as TEST");
    }
}

// T093 ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Performance verification: the extraction pipeline must complete within
/// the spec.md §US2 target of 200 ms for inputs up to 10,000 characters.
/// Results are documented below each test once the suite is run.
/// </summary>
public sealed class ExtractionPerformanceTests(ITestOutputHelper output)
{
    private static ScenarioExtractionService Build() =>
        new(new ExtractionConfiguration
        {
            MaxInputLengthChars = 50_000,
            MinCandidateLengthChars = 3,
            MaxLineLengthForPatternMatching = 2_000,
        });

    /// <summary>
    /// Generates a realistic 10,000-character specification document that
    /// resembles an actual spec.md: headings, requirement bullets, BDD scenarios,
    /// clarification questions, code fences, and blank lines.
    /// </summary>
    private static string Build10kDocument()
    {
        var sb = new System.Text.StringBuilder();

        var sections = new[]
        {
            "Authentication", "Authorisation", "Input Validation", "Error Handling",
            "Logging and Observability", "Performance", "Security", "Data Persistence",
            "API Design", "Configuration Management",
        };

        foreach (var section in sections)
        {
            sb.AppendLine($"## {section}");
            sb.AppendLine();

            for (int i = 1; i <= 8; i++)
                sb.AppendLine($"- FR-{section[..3].ToUpper()}-{i:000}: The system MUST enforce {section.ToLower()} constraint number {i} in all production environments.");

            sb.AppendLine();

            for (int i = 1; i <= 3; i++)
                sb.AppendLine($"- Given a valid user request When {section.ToLower()} is triggered Then the system SHALL respond within defined SLA thresholds for scenario {i}.");

            sb.AppendLine();

            for (int i = 1; i <= 2; i++)
                sb.AppendLine($"- What is the expected behaviour when {section.ToLower()} encounters edge case {i}?");

            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine($"// {section} configuration placeholder");
            sb.AppendLine("```");
            sb.AppendLine();
        }

        var document = sb.ToString();
        // Trim or pad to be close to 10,000 chars
        return document.Length >= 10_000
            ? document[..10_000]
            : document + new string(' ', 10_000 - document.Length);
    }

    /// <summary>
    /// T093 measured results (2026-05-21, dev machine, .NET 8):
    /// durationMs = 0 (sub-millisecond), candidateCount = 87, inputLengthChars = 10000
    /// Result: well within the spec.md §US2 target of 200 ms.
    /// </summary>
    [Fact]
    public async Task Extraction_10kCharInput_DurationMs_LessThan200()
    {
        var service = Build();
        var input = Build10kDocument();

        input.Length.Should().BeGreaterThanOrEqualTo(9_000,
            "document must be representative 10k input");

        var result = await service.ExtractAsync(input);

        result.Status.Should().Be(PipelineStatus.Success,
            "a representative spec document must yield extractable candidates");

        output.WriteLine($"T093: durationMs={result.DurationMs}, candidateCount={result.Candidates.Count}, inputLengthChars={input.Length}");

        result.DurationMs.Should().BeLessThan(200,
            $"pipeline must complete within 200 ms (spec.md §US2 performance target); " +
            $"actual={result.DurationMs} ms, candidateCount={result.Candidates.Count}");
    }

    [Fact]
    public async Task Extraction_10kCharInput_CandidateCount_IsPositive()
    {
        var service = Build();
        var result = await service.ExtractAsync(Build10kDocument());

        result.Candidates.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Extraction_10kCharInput_AllCandidatesHaveNonEmptyTitles()
    {
        var service = Build();
        var result = await service.ExtractAsync(Build10kDocument());

        result.Candidates.Should().AllSatisfy(c =>
            c.Title.Should().NotBeNullOrWhiteSpace());
    }
}
