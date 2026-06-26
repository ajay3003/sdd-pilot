using System.Text.RegularExpressions;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class ExtractionRuleSetValidationTests
{
    private static ExtractionConfiguration Config() => new();

    private static ClassificationRule DefaultRule()
        => new("Classify:Default", 0,
            new UnconditionalCondition(),
            new ClassificationOutcome(ScenarioKind.NeedsClarification, ClassificationSignal.Default));

    // =========================================================================
    // T104 — Engine startup validation
    // =========================================================================

    [Fact]
    public void Engine_throws_when_classification_rules_empty()
    {
        var ruleSet = new ExtractionRuleSet(Array.Empty<FilterRule>(), Array.Empty<ClassificationRule>());

        var act = () => new ExtractionRuleEngine(ruleSet, Config());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*at least one ClassificationRule*");
    }

    [Fact]
    public void Engine_throws_when_no_unconditional_default_rule()
    {
        var pattern = new Regex("foo", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var ruleSet = new ExtractionRuleSet(
            Array.Empty<FilterRule>(),
            [new ClassificationRule("Classify:SomeRule", 10,
                new PatternMatchCondition(pattern),
                new ClassificationOutcome(ScenarioKind.Requirement, ClassificationSignal.Rfc2119Uppercase))]);

        var act = () => new ExtractionRuleEngine(ruleSet, Config());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*exactly one unconditional Default rule*");
    }

    [Fact]
    public void Engine_throws_when_duplicate_rule_names_across_filter_and_classification()
    {
        const string duplicateName = "Duplicate:Rule";
        var ruleSet = new ExtractionRuleSet(
            [new FilterRule(duplicateName, 10, new BlockTypeMatchCondition(BlockType.Heading))],
            [
                new ClassificationRule(duplicateName, 10,
                    new PatternMatchCondition(new Regex("x", RegexOptions.Compiled | RegexOptions.CultureInvariant)),
                    new ClassificationOutcome(ScenarioKind.Requirement, ClassificationSignal.Rfc2119Uppercase)),
                DefaultRule(),
            ]);

        var act = () => new ExtractionRuleEngine(ruleSet, Config());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{duplicateName}*");
    }

    [Fact]
    public void Engine_positive_case_default_rule_set_does_not_throw()
    {
        var act = () => new ExtractionRuleEngine(ExtractionRuleSet.Default(), Config());

        act.Should().NotThrow();
    }

    // =========================================================================
    // T111 — Startup validation fires at construction, not deferred to Evaluate
    // =========================================================================

    [Fact]
    public void StartupValidation_ThrowsSynchronouslyAtConstruction_NotDeferredToEvaluate()
    {
        // A deliberately broken rule set — no classification rules at all.
        var brokenRuleSet = new ExtractionRuleSet(Array.Empty<FilterRule>(), Array.Empty<ClassificationRule>());
        var config = new ExtractionConfiguration();

        // The critical requirement: validation failure must surface at engine construction time.
        // If deferred to the first Evaluate() call, constructing would succeed here and this test would fail.
        var act = () => _ = new ExtractionRuleEngine(brokenRuleSet, config);

        act.Should().Throw<InvalidOperationException>(
            "startup validation must surface at engine construction time, not at the first Evaluate() call");
    }

    // =========================================================================
    // Constructor-level validation (ClassificationRule, FilterRule, conditions)
    // =========================================================================

    [Fact]
    public void ClassificationRule_priority_zero_with_pattern_condition_throws()
    {
        var pattern = new Regex("x", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var outcome = new ClassificationOutcome(ScenarioKind.Requirement, ClassificationSignal.Rfc2119Uppercase);

        var act = () => new ClassificationRule("Bad", 0, new PatternMatchCondition(pattern), outcome);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Priority 0*reserved*");
    }

    [Fact]
    public void PatternMatchCondition_null_pattern_throws()
    {
        var act = () => new PatternMatchCondition(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FilterRule_priority_zero_throws()
    {
        var act = () => new FilterRule("Bad", 0, new BlockTypeMatchCondition(BlockType.Heading));

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*Priority must be > 0*");
    }
}
