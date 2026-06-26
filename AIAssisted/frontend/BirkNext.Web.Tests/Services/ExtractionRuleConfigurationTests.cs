using BirkNext.Web.GraphQL;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Web.Tests.Services;

// =========================================================================
// T123 — ExtractionRuleSetCompiler validation and fallback behaviour
//
// Every test that expects a validation failure asserts BeSameAs(baseSet):
// the compiler must return the *exact* baseSet reference on any failure,
// confirming no partial application occurred and baseSet was not mutated.
// =========================================================================
public sealed class ExtractionRuleConfigurationTests
{
    private static ExtractionRuleSetCompiler Compiler()
        => new(NullLogger<ExtractionRuleSetCompiler>.Instance);

    // -------------------------------------------------------------------------
    // Empty / null config — no_configuration fallback
    // -------------------------------------------------------------------------

    [Fact]
    public void Empty_config_returns_exact_baseSet_reference_no_configuration()
    {
        var baseSet = ExtractionRuleSet.Default();

        var result = Compiler().Compile(baseSet, new ExtractionRuleConfiguration());

        result.Should().BeSameAs(baseSet);
    }

    [Fact]
    public void Null_config_returns_exact_baseSet_reference_no_configuration()
    {
        var baseSet = ExtractionRuleSet.Default();

        var result = Compiler().Compile(baseSet, null);

        result.Should().BeSameAs(baseSet);
    }

    // -------------------------------------------------------------------------
    // Valid config — successful compilation (not a fallback)
    // -------------------------------------------------------------------------

    [Fact]
    public void Valid_single_bdd_addition_returns_new_set_not_baseSet()
    {
        var baseSet = ExtractionRuleSet.Default();
        var config = new ExtractionRuleConfiguration { BddKeywordAdditions = ["Scenario"] };

        var result = Compiler().Compile(baseSet, config);

        result.Should().NotBeSameAs(baseSet);
        // Rule count unchanged: keyword extension replaces the existing BDD rule, does not add one
        result.ClassificationRules.Should().HaveCount(baseSet.ClassificationRules.Count);
    }

    // -------------------------------------------------------------------------
    // Check 1 — Array length limits (too_many_entries)
    // -------------------------------------------------------------------------

    [Fact]
    public void BddKeywordAdditions_51_entries_returns_baseSet_too_many_entries()
    {
        var baseSet = ExtractionRuleSet.Default();
        var config = new ExtractionRuleConfiguration
        {
            BddKeywordAdditions = [.. Enumerable.Range(0, 51).Select(i => $"Word{i}")]
        };

        Compiler().Compile(baseSet, config).Should().BeSameAs(baseSet);
    }

    // -------------------------------------------------------------------------
    // Check 2 — String value constraints
    // -------------------------------------------------------------------------

    [Fact]
    public void Empty_string_in_keyword_array_returns_baseSet_empty_value()
    {
        var baseSet = ExtractionRuleSet.Default();
        var config = new ExtractionRuleConfiguration { DeferralMarkerAdditions = [""] };

        Compiler().Compile(baseSet, config).Should().BeSameAs(baseSet);
    }

    [Fact]
    public void Whitespace_only_string_in_keyword_array_returns_baseSet_empty_value()
    {
        var baseSet = ExtractionRuleSet.Default();
        var config = new ExtractionRuleConfiguration { Rfc2119LowercaseAdditions = ["   "] };

        Compiler().Compile(baseSet, config).Should().BeSameAs(baseSet);
    }

    [Fact]
    public void String_exceeding_200_chars_returns_baseSet_value_too_long()
    {
        var baseSet = ExtractionRuleSet.Default();
        var config = new ExtractionRuleConfiguration
        {
            BddKeywordAdditions = [new string('A', 201)]
        };

        Compiler().Compile(baseSet, config).Should().BeSameAs(baseSet);
    }

    [Fact]
    public void Non_ASCII_character_in_keyword_returns_baseSet_non_ascii_characters()
    {
        var baseSet = ExtractionRuleSet.Default();
        var config = new ExtractionRuleConfiguration { BddKeywordAdditions = ["Scén"] };

        Compiler().Compile(baseSet, config).Should().BeSameAs(baseSet);
    }

    [Fact]
    public void Regex_metacharacter_in_keyword_returns_baseSet_regex_metacharacter()
    {
        var baseSet = ExtractionRuleSet.Default();
        var config = new ExtractionRuleConfiguration { BddKeywordAdditions = ["Given+"] };

        Compiler().Compile(baseSet, config).Should().BeSameAs(baseSet);
    }

    // -------------------------------------------------------------------------
    // Check 3 — PrefixRuleEntry constraints
    // -------------------------------------------------------------------------

    [Fact]
    public void Regex_metacharacter_in_prefix_returns_baseSet_regex_metacharacter()
    {
        var baseSet = ExtractionRuleSet.Default();
        var config = new ExtractionRuleConfiguration
        {
            PrefixRules = [new PrefixRuleEntry { Prefix = "FR-[0-9]", Classification = ScenarioKind.Requirement }]
        };

        Compiler().Compile(baseSet, config).Should().BeSameAs(baseSet);
    }

    [Fact]
    public void Invalid_classification_in_prefix_rule_returns_baseSet_invalid_classification()
    {
        var baseSet = ExtractionRuleSet.Default();
        var config = new ExtractionRuleConfiguration
        {
            PrefixRules = [new PrefixRuleEntry { Prefix = "TEST-", Classification = (ScenarioKind)999 }]
        };

        Compiler().Compile(baseSet, config).Should().BeSameAs(baseSet);
    }

    [Fact]
    public void PrefixRuleEntry_priority_zero_returns_baseSet_priority_out_of_range()
    {
        var baseSet = ExtractionRuleSet.Default();
        var config = new ExtractionRuleConfiguration
        {
            PrefixRules = [new PrefixRuleEntry { Prefix = "AC-", Classification = ScenarioKind.Test, Priority = 0 }]
        };

        Compiler().Compile(baseSet, config).Should().BeSameAs(baseSet);
    }

    [Fact]
    public void PrefixRuleEntry_priority_100_returns_baseSet_priority_out_of_range()
    {
        var baseSet = ExtractionRuleSet.Default();
        var config = new ExtractionRuleConfiguration
        {
            PrefixRules = [new PrefixRuleEntry { Prefix = "AC-", Classification = ScenarioKind.Test, Priority = 100 }]
        };

        Compiler().Compile(baseSet, config).Should().BeSameAs(baseSet);
    }

    // -------------------------------------------------------------------------
    // Check 4 — DisabledRuleNames
    // -------------------------------------------------------------------------

    [Fact]
    public void Unknown_rule_name_in_DisabledRuleNames_returns_baseSet_unknown_rule_name()
    {
        var baseSet = ExtractionRuleSet.Default();
        var config = new ExtractionRuleConfiguration
        {
            DisabledRuleNames = ["Classify:DoesNotExist"]
        };

        Compiler().Compile(baseSet, config).Should().BeSameAs(baseSet);
    }

    [Fact]
    public void ClassifyDefault_in_DisabledRuleNames_returns_baseSet_default_rule_disabled()
    {
        var baseSet = ExtractionRuleSet.Default();
        var config = new ExtractionRuleConfiguration
        {
            DisabledRuleNames = ["Classify:Default"]
        };

        Compiler().Compile(baseSet, config).Should().BeSameAs(baseSet);
    }

    // -------------------------------------------------------------------------
    // Check 5 — PriorityOverrides
    // -------------------------------------------------------------------------

    [Fact]
    public void ClassifyDefault_in_PriorityOverrides_returns_baseSet_default_priority_override()
    {
        var baseSet = ExtractionRuleSet.Default();
        var config = new ExtractionRuleConfiguration
        {
            PriorityOverrides = { ["Classify:Default"] = 5 }
        };

        Compiler().Compile(baseSet, config).Should().BeSameAs(baseSet);
    }

    // -------------------------------------------------------------------------
    // Non-mutability after fallback
    // -------------------------------------------------------------------------

    [Fact]
    public void Fallback_does_not_mutate_baseSet()
    {
        var baseSet = ExtractionRuleSet.Default();
        int originalClassificationCount = baseSet.ClassificationRules.Count;
        int originalFilterCount = baseSet.FilterRules.Count;

        // Trigger a validation_failure fallback
        var config = new ExtractionRuleConfiguration
        {
            DisabledRuleNames = ["Classify:DoesNotExist"]
        };
        var result = Compiler().Compile(baseSet, config);

        result.Should().BeSameAs(baseSet);
        baseSet.ClassificationRules.Should().HaveCount(originalClassificationCount);
        baseSet.FilterRules.Should().HaveCount(originalFilterCount);
    }

    // -------------------------------------------------------------------------
    // Idempotency
    // -------------------------------------------------------------------------

    [Fact]
    public void Compiling_same_config_twice_produces_structurally_equivalent_sets()
    {
        var config = new ExtractionRuleConfiguration
        {
            BddKeywordAdditions = ["Scenario"],
            PrefixRules = [new PrefixRuleEntry { Prefix = "AC-", Classification = ScenarioKind.Test }]
        };

        var result1 = Compiler().Compile(ExtractionRuleSet.Default(), config);
        var result2 = Compiler().Compile(ExtractionRuleSet.Default(), config);

        result1.ClassificationRules.Should().HaveCount(result2.ClassificationRules.Count);
        result1.FilterRules.Should().HaveCount(result2.FilterRules.Count);
        result1.IgnorePrefixes.Should().HaveCount(result2.IgnorePrefixes.Count);
    }
}
