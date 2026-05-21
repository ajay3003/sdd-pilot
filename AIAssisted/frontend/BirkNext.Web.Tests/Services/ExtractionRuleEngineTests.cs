using System.Text.RegularExpressions;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class ExtractionRuleEngineTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static TextBlock MakeBlock(string rawText, BlockType blockType)
        => new(rawText, blockType, 0, null);

    private static ExtractionRuleEngine DefaultEngine()
        => new(ExtractionRuleSet.Default(), new ExtractionConfiguration());

    private static ExtractionConfiguration ConfigWith(int maxLineLength)
        => new() { MaxLineLengthForPatternMatching = maxLineLength };

    // Minimal valid rule set: one unconditional Default rule only.
    private static ExtractionRuleSet MinimalRuleSet()
        => new(
            Array.Empty<FilterRule>(),
            [new ClassificationRule("Classify:Default", 0,
                new UnconditionalCondition(),
                new ClassificationOutcome(ScenarioKind.NeedsClarification, ClassificationSignal.Default))]);

    // =========================================================================
    // T103 — Evaluation logic with custom rule sets
    // =========================================================================

    [Fact]
    public void Filter_matching_blocktype_returns_IsFiltered_true()
    {
        var engine = DefaultEngine();
        var block = MakeBlock("# Heading text", BlockType.Heading);

        var result = engine.Evaluate(block, string.Empty);

        result.IsFiltered.Should().BeTrue();
        result.Classification.Should().BeNull();
        result.Signal.Should().BeNull();
        result.WinningRuleName.Should().BeNull();
    }

    [Fact]
    public void Filter_non_matching_blocktype_returns_IsFiltered_false()
    {
        var engine = DefaultEngine();
        var block = MakeBlock("some list item", BlockType.UnorderedListItem);

        var result = engine.Evaluate(block, "some list item");

        result.IsFiltered.Should().BeFalse();
    }

    [Fact]
    public void Filter_shortcircuit_classification_and_signal_are_null_when_filtered()
    {
        var engine = DefaultEngine();
        var block = MakeBlock("```code```", BlockType.FencedCodeBlock);

        var result = engine.Evaluate(block, string.Empty);

        result.IsFiltered.Should().BeTrue();
        result.Classification.Should().BeNull();
        result.Signal.Should().BeNull();
        result.WinningRuleName.Should().BeNull();
    }

    [Fact]
    public void BddPattern_opener_classifies_as_Test_BddPattern()
    {
        var engine = DefaultEngine();
        var block = MakeBlock("Given a user is logged in", BlockType.ParagraphLine);

        var result = engine.Evaluate(block, "Given a user is logged in");

        result.IsFiltered.Should().BeFalse();
        result.Classification.Should().Be(ScenarioKind.Test);
        result.Signal.Should().Be(ClassificationSignal.BddPattern);
        result.WinningRuleName.Should().Be("Classify:BddPattern");
    }

    [Fact]
    public void Rfc2119Uppercase_classifies_as_Requirement_Rfc2119Uppercase()
    {
        var engine = DefaultEngine();
        var block = MakeBlock("The system MUST validate input.", BlockType.ParagraphLine);

        var result = engine.Evaluate(block, "The system MUST validate input.");

        result.IsFiltered.Should().BeFalse();
        result.Classification.Should().Be(ScenarioKind.Requirement);
        result.Signal.Should().Be(ClassificationSignal.Rfc2119Uppercase);
        result.WinningRuleName.Should().Be("Classify:Rfc2119Uppercase");
    }

    [Fact]
    public void Plain_text_classifies_as_NeedsClarification_Default()
    {
        var engine = DefaultEngine();
        var block = MakeBlock("Some random statement about performance.", BlockType.ParagraphLine);

        var result = engine.Evaluate(block, "Some random statement about performance.");

        result.IsFiltered.Should().BeFalse();
        result.Classification.Should().Be(ScenarioKind.NeedsClarification);
        result.Signal.Should().Be(ClassificationSignal.Default);
        result.WinningRuleName.Should().Be("Classify:Default");
    }

    [Fact]
    public void Conflict_BddPattern_beats_Rfc2119Uppercase_due_to_higher_priority()
    {
        // "Given that the system MUST" matches both BddPattern (p70) and Rfc2119Uppercase (p60).
        // BddPattern wins because priority 70 > 60.
        var engine = DefaultEngine();
        var block = MakeBlock("Given that the system MUST process requests quickly", BlockType.ParagraphLine);
        const string stripped = "Given that the system MUST process requests quickly";

        var result = engine.Evaluate(block, stripped);

        result.Classification.Should().Be(ScenarioKind.Test);
        result.Signal.Should().Be(ClassificationSignal.BddPattern);
        result.WinningRuleName.Should().Be("Classify:BddPattern");
    }

    [Fact]
    public void Tiebreak_first_registered_rule_wins_on_equal_priority()
    {
        // Two classification rules at priority 10, same condition type.
        // First-registered ("First") must win.
        var pattern = new Regex("foo", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var ruleSet = new ExtractionRuleSet(
            Array.Empty<FilterRule>(),
            [
                new ClassificationRule("First", 10,
                    new PatternMatchCondition(pattern),
                    new ClassificationOutcome(ScenarioKind.Test, ClassificationSignal.BddPattern)),
                new ClassificationRule("Second", 10,
                    new PatternMatchCondition(pattern),
                    new ClassificationOutcome(ScenarioKind.Requirement, ClassificationSignal.Rfc2119Uppercase)),
                new ClassificationRule("Classify:Default", 0,
                    new UnconditionalCondition(),
                    new ClassificationOutcome(ScenarioKind.NeedsClarification, ClassificationSignal.Default)),
            ]);
        var engine = new ExtractionRuleEngine(ruleSet, new ExtractionConfiguration());

        var result = engine.Evaluate(MakeBlock("foo bar", BlockType.ParagraphLine), "foo bar");

        result.WinningRuleName.Should().Be("First");
        result.Classification.Should().Be(ScenarioKind.Test);
    }

    [Fact]
    public void ApplicableBlockTypes_scoped_rule_skipped_for_non_applicable_block()
    {
        // A rule scoped to UnorderedListItem should be skipped for ParagraphLine.
        var pattern = new Regex(".*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var ruleSet = new ExtractionRuleSet(
            Array.Empty<FilterRule>(),
            [
                new ClassificationRule("Scoped:ListOnly", 50,
                    new PatternMatchCondition(pattern),
                    new ClassificationOutcome(ScenarioKind.Requirement, ClassificationSignal.Rfc2119Uppercase),
                    applicableBlockTypes: [BlockType.UnorderedListItem]),
                new ClassificationRule("Classify:Default", 0,
                    new UnconditionalCondition(),
                    new ClassificationOutcome(ScenarioKind.NeedsClarification, ClassificationSignal.Default)),
            ]);
        var engine = new ExtractionRuleEngine(ruleSet, new ExtractionConfiguration());
        var block = MakeBlock("some text", BlockType.ParagraphLine);

        var result = engine.Evaluate(block, "some text");

        // Scoped:ListOnly should have been skipped; Default wins.
        result.WinningRuleName.Should().Be("Classify:Default");
        result.Classification.Should().Be(ScenarioKind.NeedsClarification);
    }

    [Fact]
    public void ApplicableBlockTypes_scoped_rule_fires_for_applicable_block()
    {
        var pattern = new Regex(".*", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var ruleSet = new ExtractionRuleSet(
            Array.Empty<FilterRule>(),
            [
                new ClassificationRule("Scoped:ListOnly", 50,
                    new PatternMatchCondition(pattern),
                    new ClassificationOutcome(ScenarioKind.Requirement, ClassificationSignal.Rfc2119Uppercase),
                    applicableBlockTypes: [BlockType.UnorderedListItem]),
                new ClassificationRule("Classify:Default", 0,
                    new UnconditionalCondition(),
                    new ClassificationOutcome(ScenarioKind.NeedsClarification, ClassificationSignal.Default)),
            ]);
        var engine = new ExtractionRuleEngine(ruleSet, new ExtractionConfiguration());
        var block = MakeBlock("some text", BlockType.UnorderedListItem);

        var result = engine.Evaluate(block, "some text");

        result.WinningRuleName.Should().Be("Scoped:ListOnly");
        result.Classification.Should().Be(ScenarioKind.Requirement);
    }

    [Fact]
    public void EvaluatedRuleCount_is_at_least_one_for_filtered_block()
    {
        var engine = DefaultEngine();
        var block = MakeBlock("# Heading", BlockType.Heading);

        var result = engine.Evaluate(block, string.Empty);

        result.EvaluatedRuleCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void EvaluatedRuleCount_is_at_least_one_for_classified_block()
    {
        var engine = DefaultEngine();
        var block = MakeBlock("plain text", BlockType.ParagraphLine);

        var result = engine.Evaluate(block, "plain text");

        result.EvaluatedRuleCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void WinningRuleName_equals_expected_rule_name()
    {
        var engine = DefaultEngine();
        var block = MakeBlock("The system MUST NOT fail.", BlockType.ParagraphLine);

        var result = engine.Evaluate(block, "The system MUST NOT fail.");

        result.WinningRuleName.Should().Be("Classify:Rfc2119Uppercase");
    }

    [Fact]
    public void RuleNames_contains_all_filter_then_classification_rules()
    {
        var engine = DefaultEngine();

        // 9 filter rules + 7 classification rules = 16 total
        engine.RuleNames.Should().HaveCount(16);
        engine.RuleNames.First().Should().StartWith("Filter:");
        engine.RuleNames.Last().Should().Be("Classify:Default");
    }

    // =========================================================================
    // T105 — Default rule set correctness
    // =========================================================================

    [Fact]
    public void Default_Rfc2119Uppercase_MUST_classifies_as_Requirement()
    {
        var engine = DefaultEngine();
        const string text = "The system MUST validate credentials";

        var result = engine.Evaluate(MakeBlock(text, BlockType.ParagraphLine), text);

        result.Classification.Should().Be(ScenarioKind.Requirement);
        result.Signal.Should().Be(ClassificationSignal.Rfc2119Uppercase);
    }

    [Fact]
    public void Default_BddPattern_triple_classifies_as_Test()
    {
        var engine = DefaultEngine();
        const string text = "Given login When valid Then redirect";

        var result = engine.Evaluate(MakeBlock(text, BlockType.ParagraphLine), text);

        result.Classification.Should().Be(ScenarioKind.Test);
        result.Signal.Should().Be(ClassificationSignal.BddPattern);
    }

    [Fact]
    public void Default_QuestionTerminator_classifies_as_NeedsClarification()
    {
        var engine = DefaultEngine();
        const string text = "Session timeout policy?";

        var result = engine.Evaluate(MakeBlock(text, BlockType.ParagraphLine), text);

        result.Classification.Should().Be(ScenarioKind.NeedsClarification);
        result.Signal.Should().Be(ClassificationSignal.QuestionTerminator);
    }

    [Fact]
    public void Default_DeferralMarker_TBD_classifies_as_NeedsClarification()
    {
        var engine = DefaultEngine();
        const string text = "TBD — performance target not yet set";

        var result = engine.Evaluate(MakeBlock(text, BlockType.ParagraphLine), text);

        result.Classification.Should().Be(ScenarioKind.NeedsClarification);
        result.Signal.Should().Be(ClassificationSignal.DeferralMarker);
    }

    [Fact]
    public void Default_FrPrefix_with_lowercase_shall_classifies_as_Rfc2119Lowercase_priority_beats_FrPrefix()
    {
        // "FR-001: the system shall authenticate" matches both FrPrefix (p40) and Rfc2119Lowercase "shall" (p50).
        // Rfc2119Lowercase wins because priority 50 > 40.
        var engine = DefaultEngine();
        const string text = "FR-001: the system shall authenticate";

        var result = engine.Evaluate(MakeBlock(text, BlockType.ParagraphLine), text);

        result.Classification.Should().Be(ScenarioKind.Requirement);
        result.Signal.Should().Be(ClassificationSignal.Rfc2119Lowercase);
        result.WinningRuleName.Should().Be("Classify:Rfc2119Lowercase");
    }

    [Fact]
    public void Default_Rfc2119Lowercase_must_classifies_as_Requirement()
    {
        var engine = DefaultEngine();
        const string text = "the system must be available 99.9% of the time";

        var result = engine.Evaluate(MakeBlock(text, BlockType.ParagraphLine), text);

        result.Classification.Should().Be(ScenarioKind.Requirement);
        result.Signal.Should().Be(ClassificationSignal.Rfc2119Lowercase);
    }

    [Fact]
    public void Default_BddPattern_beats_Rfc2119Uppercase_in_combined_text()
    {
        var engine = DefaultEngine();
        const string text = "Given that the system MUST process requests quickly";

        var result = engine.Evaluate(MakeBlock(text, BlockType.ParagraphLine), text);

        result.Classification.Should().Be(ScenarioKind.Test);
        result.Signal.Should().Be(ClassificationSignal.BddPattern);
    }

    [Fact]
    public void Default_plain_statement_classifies_as_NeedsClarification_Default()
    {
        var engine = DefaultEngine();
        const string text = "Random plain statement about performance";

        var result = engine.Evaluate(MakeBlock(text, BlockType.ParagraphLine), text);

        result.Classification.Should().Be(ScenarioKind.NeedsClarification);
        result.Signal.Should().Be(ClassificationSignal.Default);
        result.WinningRuleName.Should().Be("Classify:Default");
    }

    [Fact]
    public void Default_over_limit_text_pattern_matching_bypassed_classifies_as_Default()
    {
        // strippedText longer than MaxLineLengthForPatternMatching → pattern matching bypassed → Default.
        var engine = new ExtractionRuleEngine(ExtractionRuleSet.Default(), ConfigWith(maxLineLength: 2000));
        var longText = new string('x', 2001);

        var result = engine.Evaluate(MakeBlock(longText, BlockType.ParagraphLine), longText);

        result.Classification.Should().Be(ScenarioKind.NeedsClarification);
        result.Signal.Should().Be(ClassificationSignal.Default);
        result.WinningRuleName.Should().Be("Classify:Default");
    }

    [Theory]
    [InlineData(BlockType.Heading)]
    [InlineData(BlockType.FencedCodeBlock)]
    [InlineData(BlockType.Blockquote)]
    [InlineData(BlockType.HorizontalRule)]
    [InlineData(BlockType.HtmlComment)]
    [InlineData(BlockType.YamlFrontMatter)]
    [InlineData(BlockType.Empty)]
    [InlineData(BlockType.TableHeaderRow)]
    [InlineData(BlockType.TableSeparatorRow)]
    public void Default_all_filtered_block_types_return_IsFiltered_true(BlockType blockType)
    {
        var engine = DefaultEngine();
        var block = MakeBlock("content", blockType);

        var result = engine.Evaluate(block, string.Empty);

        result.IsFiltered.Should().BeTrue();
    }

    [Theory]
    [InlineData(BlockType.UnorderedListItem)]
    [InlineData(BlockType.ParagraphLine)]
    public void Default_non_filtered_block_types_return_IsFiltered_false(BlockType blockType)
    {
        var engine = DefaultEngine();
        var block = MakeBlock("plain content", blockType);

        var result = engine.Evaluate(block, "plain content");

        result.IsFiltered.Should().BeFalse();
    }
}
