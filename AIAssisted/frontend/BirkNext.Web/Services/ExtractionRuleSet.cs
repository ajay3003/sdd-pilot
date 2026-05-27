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
    // Plain-text prefixes whose matching content items are discarded at Stage 5.5.
    // Default() includes built-in metadata and section-heading prefixes; the compiler
    // merges user-configured additions on top of these.
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
            // Priority 70: BDD triple (Given…When…Then in order) or BDD line opener (Given/When/Then).
            new("Classify:BddPattern", 70,
                new PatternMatchCondition(BddPattern),
                new ClassificationOutcome(ScenarioKind.Test, ClassificationSignal.BddPattern)),

            // Priority 60: RFC 2119 uppercase modal verbs (case-sensitive).
            // MUST NOT/SHALL NOT precede MUST/SHALL in alternation to prevent partial matches.
            new("Classify:Rfc2119Uppercase", 60,
                new PatternMatchCondition(Rfc2119UpperPattern),
                new ClassificationOutcome(ScenarioKind.Requirement, ClassificationSignal.Rfc2119Uppercase)),

            // Priority 55: Strong ambiguity signals — phrases that explicitly indicate a decision
            // or clarification is needed. Placed above Rfc2119Lowercase so "how should we handle X"
            // becomes NeedsClarification rather than REQUIREMENT via "should".
            new("Classify:ClarificationSignal", 55,
                new PatternMatchCondition(ClarificationPattern),
                new ClassificationOutcome(ScenarioKind.NeedsClarification, ClassificationSignal.ClarificationSignal)),

            // Priority 50: RFC 2119 lowercase modal verbs/phrases (case-insensitive).
            // Longer phrases precede their component words to prevent partial matches.
            new("Classify:Rfc2119Lowercase", 50,
                new PatternMatchCondition(Rfc2119LowerPattern),
                new ClassificationOutcome(ScenarioKind.Requirement, ClassificationSignal.Rfc2119Lowercase)),

            // Priority 40: Functional requirement prefix FR-NNN (case-sensitive).
            new("Classify:FrPrefix", 40,
                new PatternMatchCondition(FrPrefixPattern),
                new ClassificationOutcome(ScenarioKind.Requirement, ClassificationSignal.FrPrefix)),

            // Priority 30: Question terminator — stripped text ends with '?'.
            // strippedText is already Trim()ed by Stage 5 (StripMarkdown returns text.Trim()).
            new("Classify:QuestionTerminator", 30,
                new PatternMatchCondition(QuestionTerminatorPattern),
                new ClassificationOutcome(ScenarioKind.NeedsClarification, ClassificationSignal.QuestionTerminator)),

            // Priority 20: Deferral marker keywords (case-insensitive).
            new("Classify:DeferralMarker", 20,
                new PatternMatchCondition(DeferralPattern),
                new ClassificationOutcome(ScenarioKind.NeedsClarification, ClassificationSignal.DeferralMarker)),

            // Priority 15: Requirement-language words ("should", "can") that did not match a
            // stronger signal. Lower than QuestionTerminator (30) and DeferralMarker (20) so
            // "Should we implement X?" and "TBD should be decided" remain NeedsClarification.
            new("Classify:RequirementLanguage", 15,
                new PatternMatchCondition(RequirementLanguagePattern),
                new ClassificationOutcome(ScenarioKind.Requirement, ClassificationSignal.RequirementLanguage)),

            // Priority 0: Unconditional default fallback. Exactly one per rule set.
            new("Classify:Default", 0,
                new UnconditionalCondition(),
                new ClassificationOutcome(ScenarioKind.NeedsClarification, ClassificationSignal.Default)),
        };

        // Built-in ignore prefixes: structural metadata and standalone section-heading labels
        // that appear in specification documents but are not meaningful extraction candidates.
        // Applied at Stage 5.5 after bold/italic formatting is stripped from candidate text.
        // The compiler merges user-configured IgnorePrefixes on top of these defaults.
        var defaultIgnorePrefixes = ImmutableArray.Create(
            // Metadata fields (typically formatted as **Key**: value in spec documents)
            "Feature Branch",
            "Created:",
            "Status:",
            "Priority:",
            "Author:",
            "Version:",
            "Updated:",
            "Tags:",
            // Standalone section-heading labels (not ATX headings; appear as bold paragraph text)
            "Acceptance Scenarios",
            "Key Entities",
            "Observability",
            "Measurable Outcomes",
            "Edge Cases",
            "Functional Requirements",
            "Non-Goals",
            "Independent Test:",
            // Bare BDD step labels (e.g. "Given: setup" used as section markers, not step content)
            "Given:",
            "When:",
            "Then:",
            "And:",
            "But:"
        );

        return new ExtractionRuleSet(filterRules, classificationRules, defaultIgnorePrefixes);
    }

    /// <summary>
    /// Speckit extraction profile — extends Default() with heading-aware rules tuned for
    /// structured spec.md files generated by Spec Kit. No user-config (ExtractionRuleSetCompiler)
    /// is applied to this profile; it is a code-defined, closed rule set.
    /// </summary>
    public static ExtractionRuleSet Speckit()
    {
        var defaultSet = Default();
        var classificationRules = defaultSet.ClassificationRules.ToList();

        // Priority 45: NFR-NNN identifier (case-sensitive) → REQUIREMENT.
        // Higher than FrPrefix (40) but still below ClarificationSignal (55) and Rfc2119 (50/60).
        classificationRules.Add(new ClassificationRule("Speckit:NfrPrefix", 45,
            new PatternMatchCondition(SpeckitNfrPrefixPattern),
            new ClassificationOutcome(ScenarioKind.Requirement, ClassificationSignal.FrPrefix)));

        // Priority 44: SC-NNN identifier (Scenario/Constraint) → REQUIREMENT.
        classificationRules.Add(new ClassificationRule("Speckit:ScPrefix", 44,
            new PatternMatchCondition(SpeckitScPrefixPattern),
            new ClassificationOutcome(ScenarioKind.Requirement, ClassificationSignal.FrPrefix)));

        // Priority 16: Acceptance Criteria / Test heading → TEST.
        // Above RequirementLanguage (15) so "User can log in" under an Acceptance Criteria heading
        // becomes TEST; below ClarificationSignal (55) so strong ambiguity still wins.
        classificationRules.Add(new ClassificationRule("Speckit:TestSection", 16,
            new HeadingContextCondition(SpeckitTestHeadingPattern),
            new ClassificationOutcome(ScenarioKind.Test, ClassificationSignal.HeadingContext)));

        // Priority 16: Open Questions / Clarifications heading → NEEDS_CLARIFICATION.
        classificationRules.Add(new ClassificationRule("Speckit:ClarificationSection", 16,
            new HeadingContextCondition(SpeckitClarificationHeadingPattern),
            new ClassificationOutcome(ScenarioKind.NeedsClarification, ClassificationSignal.HeadingContext)));

        // Priority 10: Functional/Non-Functional Requirements / Observability heading → REQUIREMENT.
        // Below RequirementLanguage (15) so "X should be done" already classified as REQUIREMENT
        // by RequirementLanguage doesn't need the heading boost; but plain statements without
        // requirement keywords get lifted to REQUIREMENT by their section context.
        classificationRules.Add(new ClassificationRule("Speckit:RequirementSection", 10,
            new HeadingContextCondition(SpeckitRequirementHeadingPattern),
            new ClassificationOutcome(ScenarioKind.Requirement, ClassificationSignal.HeadingContext)));

        // Extend IgnorePrefixes with Speckit-specific metadata lines.
        // "Input:" appears as a metadata label in some Speckit spec.md files.
        // "Non-Functional Requirements" appears as a bold inline label (not an ATX heading) in some docs.
        var ignorePrefixes = ImmutableArray.CreateRange(
            defaultSet.IgnorePrefixes.Concat(["Input:", "Non-Functional Requirements"]));

        return new ExtractionRuleSet(defaultSet.FilterRules.ToList(), classificationRules, ignorePrefixes);
    }

    // BDD: word-boundary triple Given/When/Then appearing in document order anywhere on the line,
    // or a BDD section opener (Given/When/Then) at the very start of the line.
    // Word-boundary triple handles bold-formatted variants: **Given** ... **When** ... **Then** ...
    // after the StripMarkdown bold-stripping pass reduces them to plain keywords.
    // And/But are grouping continuers handled at Stage 5.3 (GroupBddSteps); they do not appear
    // here to avoid classifying orphaned continuation lines as standalone TEST candidates.
    private static readonly Regex BddPattern = new(
        @"\bGiven\b.*\bWhen\b.*\bThen\b|^(?:Given|When|Then) ",
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
    private static readonly Regex QuestionTerminatorPattern = new(
        @"\?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Deferral marker keywords (case-insensitive).
    private static readonly Regex DeferralPattern = new(
        @"\b(TBD|TODO|TBC|open question|to be defined|to be decided)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Strong clarification signals: phrases that unambiguously indicate a question or open decision.
    // Priority 55 — above Rfc2119Lowercase (50) so "how should we handle X" resolves to
    // NeedsClarification rather than REQUIREMENT via the "should" keyword.
    private static readonly Regex ClarificationPattern = new(
        @"\b(clarify|unresolved|needs decision|what happens if|how should|should we)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Requirement-language words ("should", "can") that signal probable intent without a
    // stronger classification match. Priority 15 — below QuestionTerminator (30) and
    // DeferralMarker (20) so "Should we implement X?" and "TBD should be decided" remain
    // NeedsClarification while "Validation failures should be logged" becomes REQUIREMENT.
    private static readonly Regex RequirementLanguagePattern = new(
        @"\b(should|can)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ── Speckit profile patterns ─────────────────────────────────────────────

    // NFR-NNN: Non-Functional Requirement identifier.
    private static readonly Regex SpeckitNfrPrefixPattern = new(
        @"\bNFR-\d+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // SC-NNN: Scenario/Constraint identifier.
    private static readonly Regex SpeckitScPrefixPattern = new(
        @"\bSC-\d+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Heading context → TEST: Acceptance Criteria, Test Cases, Scenarios, etc.
    private static readonly Regex SpeckitTestHeadingPattern = new(
        @"\b(?:Acceptance Criteria|Acceptance Scenarios?|Test Cases?|Tests?|Scenarios?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Heading context → NEEDS_CLARIFICATION: Open Questions, Risks, Unknowns, etc.
    private static readonly Regex SpeckitClarificationHeadingPattern = new(
        @"\b(?:Open Questions?|Clarifications?|Unknowns?|Risks?|TBD Items?|Unresolved)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Heading context → REQUIREMENT: Functional/Non-Functional Requirements, Observability, etc.
    private static readonly Regex SpeckitRequirementHeadingPattern = new(
        @"\b(?:Functional Requirements?|Non-Functional Requirements?|Observability|Security|Performance|Measurable Outcomes?|Key Entities|Business Rules?|Constraints?|System Requirements?|Background)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
