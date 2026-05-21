using System.Collections.Immutable;
using System.Text.RegularExpressions;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;

// Regex authoring constraints for PatternMatchCondition patterns in this file:
//   - Use \b word-boundary assertions for keyword patterns (prevents MUSTARD matching MUST)
//   - No nested quantifiers: avoid (a+)+, (a|a)*, etc. — all quantifiers must be flat
//   - No backreferences
//   - All patterns constructed with RegexOptions.Compiled | RegexOptions.CultureInvariant
//   - Case-sensitive vs case-insensitive matches must mirror the original Stage 6 logic exactly
//   - Patterns compile at application startup via ExtractionRuleSet.Default() construction

namespace BirkNext.Web.Services;

public sealed class ExtractionRuleSet
{
    public IReadOnlyList<FilterRule> FilterRules { get; }
    public IReadOnlyList<ClassificationRule> ClassificationRules { get; }
    // Plain-text prefixes whose matching content items are discarded at Stage 5.5 (US4).
    // Empty by default; populated by ExtractionRuleSetCompiler from ExtractionRuleConfiguration.
    public IReadOnlyList<string> IgnorePrefixes { get; }

    public ExtractionRuleSet(
        IReadOnlyList<FilterRule> filterRules,
        IReadOnlyList<ClassificationRule> classificationRules,
        IReadOnlyList<string>? ignorePrefixes = null)
    {
        ArgumentNullException.ThrowIfNull(filterRules);
        ArgumentNullException.ThrowIfNull(classificationRules);
        // Stable sort: equal priorities preserve registration order.
        FilterRules = [.. filterRules.OrderByDescending(r => r.Priority)];
        ClassificationRules = [.. classificationRules.OrderByDescending(r => r.Priority)];
        IgnorePrefixes = ignorePrefixes ?? ImmutableArray<string>.Empty;
    }

    public static ExtractionRuleSet Default()
    {
        var filterRules = new List<FilterRule>
        {
            new("Filter:Heading",           100, new BlockTypeMatchCondition(BlockType.Heading)),
            new("Filter:FencedCodeBlock",   100, new BlockTypeMatchCondition(BlockType.FencedCodeBlock)),
            new("Filter:Blockquote",        100, new BlockTypeMatchCondition(BlockType.Blockquote)),
            new("Filter:HorizontalRule",    100, new BlockTypeMatchCondition(BlockType.HorizontalRule)),
            new("Filter:HtmlComment",       100, new BlockTypeMatchCondition(BlockType.HtmlComment)),
            new("Filter:YamlFrontMatter",   100, new BlockTypeMatchCondition(BlockType.YamlFrontMatter)),
            new("Filter:Empty",             100, new BlockTypeMatchCondition(BlockType.Empty)),
            new("Filter:TableHeaderRow",    100, new BlockTypeMatchCondition(BlockType.TableHeaderRow)),
            new("Filter:TableSeparatorRow", 100, new BlockTypeMatchCondition(BlockType.TableSeparatorRow)),
        };

        var classificationRules = new List<ClassificationRule>
        {
            // Priority 70: BDD triple (Given...When...Then in order) or BDD line opener.
            // Mirrors IsBddPattern(): OrdinalIgnoreCase triple check + StartsWith check.
            new("Classify:BddPattern", 70,
                new PatternMatchCondition(BddPattern),
                new ClassificationOutcome(ScenarioKind.Test, ClassificationSignal.BddPattern)),

            // Priority 60: RFC 2119 uppercase modal verbs (case-sensitive).
            // Mirrors Rfc2119UpperPattern: MUST NOT/SHALL NOT precede MUST/SHALL in alternation.
            new("Classify:Rfc2119Uppercase", 60,
                new PatternMatchCondition(Rfc2119UpperPattern),
                new ClassificationOutcome(ScenarioKind.Requirement, ClassificationSignal.Rfc2119Uppercase)),

            // Priority 50: RFC 2119 lowercase modal verbs/phrases (case-insensitive).
            // Mirrors Rfc2119LowerPattern: longer phrases precede their component words.
            new("Classify:Rfc2119Lowercase", 50,
                new PatternMatchCondition(Rfc2119LowerPattern),
                new ClassificationOutcome(ScenarioKind.Requirement, ClassificationSignal.Rfc2119Lowercase)),

            // Priority 40: Functional requirement prefix FR-NNN (case-sensitive).
            new("Classify:FrPrefix", 40,
                new PatternMatchCondition(FrPrefixPattern),
                new ClassificationOutcome(ScenarioKind.Requirement, ClassificationSignal.FrPrefix)),

            // Priority 30: Question terminator — stripped text ends with '?'.
            // Mirrors: text.TrimEnd().EndsWith('?'); strippedText is already trimmed by Stage 5.
            new("Classify:QuestionTerminator", 30,
                new PatternMatchCondition(QuestionTerminatorPattern),
                new ClassificationOutcome(ScenarioKind.NeedsClarification, ClassificationSignal.QuestionTerminator)),

            // Priority 20: Deferral marker keywords (case-insensitive).
            new("Classify:DeferralMarker", 20,
                new PatternMatchCondition(DeferralPattern),
                new ClassificationOutcome(ScenarioKind.NeedsClarification, ClassificationSignal.DeferralMarker)),

            // Priority 0: Unconditional default fallback. Exactly one per rule set.
            new("Classify:Default", 0,
                new UnconditionalCondition(),
                new ClassificationOutcome(ScenarioKind.NeedsClarification, ClassificationSignal.Default)),
        };

        return new ExtractionRuleSet(filterRules, classificationRules, ImmutableArray<string>.Empty);
    }

    // BDD: triple Given/When/Then appearing in document order anywhere on the line,
    // or a BDD section opener at the very start of the line.
    // Mirrors IsBddPattern(): IndexOf triple + StartsWith, OrdinalIgnoreCase.
    private static readonly Regex BddPattern = new(
        @"Given .*When .*Then |^(?:Given|When|Then) ",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // RFC 2119 uppercase modal verbs (case-sensitive).
    // MUST NOT / SHALL NOT must precede MUST / SHALL to prevent partial matches.
    private static readonly Regex Rfc2119UpperPattern = new(
        @"\b(MUST NOT|SHALL NOT|MUST|SHALL|SHOULD|MAY)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // RFC 2119 lowercase modal verbs / phrases (case-insensitive).
    // Longer phrases precede their component words to prevent partial matches.
    private static readonly Regex Rfc2119LowerPattern = new(
        @"\b(must not|shall not|is required to|must|shall|required)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Functional requirement prefix FR-NNN (case-sensitive).
    private static readonly Regex FrPrefixPattern = new(
        @"\bFR-\d+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Question terminator: stripped text ends with '?'.
    // strippedText is already Trim()ed by Stage 5 (StripMarkdown returns text.Trim()).
    private static readonly Regex QuestionTerminatorPattern = new(
        @"\?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Deferral marker keywords (case-insensitive).
    private static readonly Regex DeferralPattern = new(
        @"\b(TBD|TODO|TBC|open question|to be defined|to be decided)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
