using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Web.Tests.Services;

// =========================================================================
// T124 — ExtractionRuleSetCompiler behavior tests
//
// Covers: structural output, keyword extension, prefix rules, IgnorePrefixes,
// DisabledRuleNames, PriorityOverrides, non-mutability, FR-US4-010 compliance,
// and compiled-set compatibility with ExtractionRuleEngine startup validation.
// =========================================================================
public sealed class ExtractionRuleSetCompilerTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ExtractionRuleSetCompiler Compiler()
        => new(NullLogger<ExtractionRuleSetCompiler>.Instance);

    private static ExtractionRuleEngine EngineFor(ExtractionRuleSet ruleSet)
        => new(ruleSet, new ExtractionConfiguration());

    private static RuleEvaluationResult Eval(ExtractionRuleSet ruleSet, string text)
    {
        var block = new TextBlock(text, BlockType.ParagraphLine, 0, null);
        return EngineFor(ruleSet).Evaluate(block, text);
    }

    // =========================================================================
    // Structural equivalence — empty config
    // =========================================================================

    [Fact]
    public void Empty_config_returns_set_with_same_rule_counts_as_Default()
    {
        var baseSet = ExtractionRuleSet.Default();

        var compiled = Compiler().Compile(baseSet, new ExtractionRuleConfiguration());

        // IsEffectivelyEmpty path: returns exact same reference
        compiled.Should().BeSameAs(baseSet);
        compiled.ClassificationRules.Should().HaveCount(baseSet.ClassificationRules.Count);
        compiled.FilterRules.Should().HaveCount(baseSet.FilterRules.Count);
        // IgnorePrefixes reflect Default()'s built-in metadata/section-heading prefixes.
        compiled.IgnorePrefixes.Should().BeEquivalentTo(baseSet.IgnorePrefixes);
    }

    // =========================================================================
    // Keyword extension — behavioral verification
    // =========================================================================

    [Fact]
    public void BddKeywordAddition_new_keyword_matches_in_engine_evaluation()
    {
        // "Scenario the user logs in" does NOT match the original BDD pattern
        // (no Given/When/Then opener) but DOES match after "Scenario" is added.
        var config = new ExtractionRuleConfiguration { BddKeywordAdditions = ["Scenario"] };
        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), config);
        const string input = "Scenario the user logs in";

        // Verify the original Default() does not classify this as BddPattern
        Eval(ExtractionRuleSet.Default(), input).Signal.Should().NotBe(ClassificationSignal.BddPattern);

        // Verify the compiled set does classify it as BddPattern
        var result = Eval(compiled, input);
        result.Signal.Should().Be(ClassificationSignal.BddPattern);
        result.Classification.Should().Be(ScenarioKind.Test);
        result.WinningRuleName.Should().Be("Classify:BddPattern");
    }

    [Fact]
    public void Rfc2119UppercaseAddition_new_keyword_classifies_as_Requirement()
    {
        // "PERMITTED" does not appear in the original RFC 2119 uppercase set.
        var config = new ExtractionRuleConfiguration { Rfc2119UppercaseAdditions = ["PERMITTED"] };
        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), config);
        const string input = "Users PERMITTED to access the report";

        Eval(ExtractionRuleSet.Default(), input).Signal.Should().NotBe(ClassificationSignal.Rfc2119Uppercase);

        var result = Eval(compiled, input);
        result.Signal.Should().Be(ClassificationSignal.Rfc2119Uppercase);
        result.Classification.Should().Be(ScenarioKind.Requirement);
    }

    [Fact]
    public void DeferralMarkerAddition_new_keyword_classifies_as_NeedsClarification()
    {
        // "PENDING" does not appear in the original deferral marker set.
        var config = new ExtractionRuleConfiguration { DeferralMarkerAdditions = ["PENDING"] };
        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), config);
        const string input = "PENDING implementation of the login feature";

        Eval(ExtractionRuleSet.Default(), input).Signal.Should().NotBe(ClassificationSignal.DeferralMarker);

        var result = Eval(compiled, input);
        result.Signal.Should().Be(ClassificationSignal.DeferralMarker);
        result.Classification.Should().Be(ScenarioKind.NeedsClarification);
    }

    // =========================================================================
    // Prefix classification rules
    // =========================================================================

    [Fact]
    public void Prefix_rule_match_produces_ConfiguredPrefix_signal()
    {
        var config = new ExtractionRuleConfiguration
        {
            PrefixRules = [new PrefixRuleEntry { Prefix = "AC-", Classification = ScenarioKind.Test }]
        };
        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), config);
        // "AC-001 The login form displays an error message" — no other rule matches
        const string input = "AC-001 The login form displays an error message";

        var result = Eval(compiled, input);

        result.Signal.Should().Be(ClassificationSignal.ConfiguredPrefix);
        result.Classification.Should().Be(ScenarioKind.Test);
    }

    [Fact]
    public void Prefix_rule_with_explicit_name_preserves_rule_name()
    {
        const string customName = "Custom:AcceptanceCriteria";
        var config = new ExtractionRuleConfiguration
        {
            PrefixRules = [new PrefixRuleEntry { Name = customName, Prefix = "AC-", Classification = ScenarioKind.Test }]
        };

        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), config);

        compiled.ClassificationRules.Should().Contain(r => r.Name == customName);
    }

    [Fact]
    public void Prefix_rule_with_null_name_generates_Configure_Prefix_index()
    {
        var config = new ExtractionRuleConfiguration
        {
            PrefixRules = [new PrefixRuleEntry { Name = null, Prefix = "AC-", Classification = ScenarioKind.Test }]
        };

        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), config);

        compiled.ClassificationRules.Should().Contain(r => r.Name == "Configure:Prefix:0");
    }

    [Fact]
    public void Multiple_prefix_rules_all_present_with_correct_priorities()
    {
        var config = new ExtractionRuleConfiguration
        {
            PrefixRules =
            [
                new PrefixRuleEntry { Prefix = "AC-",  Classification = ScenarioKind.Test,        Priority = 15 },
                new PrefixRuleEntry { Prefix = "SEC-", Classification = ScenarioKind.Requirement, Priority = 25 },
            ]
        };

        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), config);

        var rule0 = compiled.ClassificationRules.Should().Contain(r => r.Name == "Configure:Prefix:0").Which;
        var rule1 = compiled.ClassificationRules.Should().Contain(r => r.Name == "Configure:Prefix:1").Which;
        rule0.Priority.Should().Be(15);
        rule1.Priority.Should().Be(25);
    }

    // =========================================================================
    // IgnorePrefixes
    // =========================================================================

    [Fact]
    public void IgnorePrefixes_populated_in_compiled_set()
    {
        var config = new ExtractionRuleConfiguration
        {
            IgnorePrefixes = ["IGNORE:", "SKIP-"]
        };

        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), config);

        // User-configured prefixes are merged with Default()'s built-in prefixes.
        compiled.IgnorePrefixes.Should().Contain("IGNORE:").And.Contain("SKIP-");
    }

    // =========================================================================
    // PrefixMatchCondition case-insensitivity
    // =========================================================================

    [Fact]
    public void PrefixMatchCondition_is_case_insensitive_lower_prefix_matches_upper_input()
    {
        // Prefix "fr-" configured; input "FR-001 ..." (uppercase) must still match.
        var config = new ExtractionRuleConfiguration
        {
            PrefixRules = [new PrefixRuleEntry { Prefix = "fr-", Classification = ScenarioKind.Requirement, Priority = 45 }]
        };
        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), config);
        const string input = "FR-001 The system displays the login page";

        var result = Eval(compiled, input);

        result.Signal.Should().Be(ClassificationSignal.ConfiguredPrefix);
        result.Classification.Should().Be(ScenarioKind.Requirement);
    }

    [Fact]
    public void PrefixMatchCondition_is_case_insensitive_upper_prefix_matches_lower_input()
    {
        // Confirms symmetry: "FR-" prefix configured; input "fr-001 ..." must match.
        var config = new ExtractionRuleConfiguration
        {
            PrefixRules = [new PrefixRuleEntry { Prefix = "FR-", Classification = ScenarioKind.Requirement, Priority = 45 }]
        };
        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), config);
        const string input = "fr-001 The system displays the login page";

        var result = Eval(compiled, input);

        result.Signal.Should().Be(ClassificationSignal.ConfiguredPrefix);
    }

    // =========================================================================
    // DisabledRuleNames
    // =========================================================================

    [Fact]
    public void DisabledRuleName_rule_is_absent_from_compiled_set()
    {
        var config = new ExtractionRuleConfiguration
        {
            DisabledRuleNames = ["Classify:DeferralMarker"]
        };

        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), config);

        compiled.ClassificationRules.Should().NotContain(r => r.Name == "Classify:DeferralMarker");
    }

    [Fact]
    public void DisabledRule_deferral_marker_input_falls_back_to_Default_signal()
    {
        // With DeferralMarker disabled, "TBD something" no longer matches that rule.
        // No other rule matches, so Default wins.
        var config = new ExtractionRuleConfiguration
        {
            DisabledRuleNames = ["Classify:DeferralMarker"]
        };
        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), config);
        const string input = "TBD the notification subsystem";

        var result = Eval(compiled, input);

        result.Signal.Should().Be(ClassificationSignal.Default);
        result.WinningRuleName.Should().Be("Classify:Default");
    }

    // =========================================================================
    // PriorityOverrides
    // =========================================================================

    [Fact]
    public void PriorityOverride_rule_has_updated_priority_in_compiled_set()
    {
        var config = new ExtractionRuleConfiguration
        {
            PriorityOverrides = { ["Classify:FrPrefix"] = 80 }
        };

        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), config);

        var rule = compiled.ClassificationRules.Should().Contain(r => r.Name == "Classify:FrPrefix").Which;
        rule.Priority.Should().Be(80);
    }

    [Fact]
    public void PriorityOverride_elevated_FrPrefix_beats_BddPattern_on_conflicting_input()
    {
        // Without override: BddPattern (70) beats FrPrefix (40) on "Given FR-001 is tested".
        // With override to 80: FrPrefix (80) beats BddPattern (70).
        var config = new ExtractionRuleConfiguration
        {
            PriorityOverrides = { ["Classify:FrPrefix"] = 80 }
        };
        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), config);
        const string input = "Given FR-001 is tested";

        // Baseline: BddPattern wins by default
        Eval(ExtractionRuleSet.Default(), input).WinningRuleName.Should().Be("Classify:BddPattern");

        // After override: FrPrefix wins
        var result = Eval(compiled, input);
        result.WinningRuleName.Should().Be("Classify:FrPrefix");
        result.Signal.Should().Be(ClassificationSignal.FrPrefix);
    }

    // =========================================================================
    // Non-mutability
    // =========================================================================

    [Fact]
    public void BaseSet_is_unchanged_after_successful_compilation()
    {
        var baseSet = ExtractionRuleSet.Default();
        int originalClassificationCount = baseSet.ClassificationRules.Count;
        int originalFilterCount = baseSet.FilterRules.Count;

        var config = new ExtractionRuleConfiguration
        {
            BddKeywordAdditions = ["Scenario"],
            PrefixRules = [new PrefixRuleEntry { Prefix = "AC-", Classification = ScenarioKind.Test }],
            DisabledRuleNames = ["Classify:DeferralMarker"],
        };
        Compiler().Compile(baseSet, config);

        baseSet.ClassificationRules.Should().HaveCount(originalClassificationCount);
        baseSet.FilterRules.Should().HaveCount(originalFilterCount);
    }

    [Fact]
    public void Default_factory_returns_original_rule_count_after_compilation()
    {
        int originalCount = ExtractionRuleSet.Default().ClassificationRules.Count;

        var config = new ExtractionRuleConfiguration
        {
            PrefixRules = [new PrefixRuleEntry { Prefix = "AC-", Classification = ScenarioKind.Test }],
            DisabledRuleNames = ["Classify:DeferralMarker"],
        };
        Compiler().Compile(ExtractionRuleSet.Default(), config);

        // Default() is a factory: each call produces a fresh set; the factory is unaffected
        ExtractionRuleSet.Default().ClassificationRules.Should().HaveCount(originalCount);
    }

    // =========================================================================
    // FR-US4-010 — Default config reproduces Default() extraction behavior
    // =========================================================================

    [Fact]
    public void Compile_with_empty_config_produces_identical_extraction_to_Default_for_three_inputs()
    {
        // Empty config triggers IsEffectivelyEmpty → returns baseSet directly,
        // so results are guaranteed identical. This test documents the behavioral contract.
        var emptyCompiled = Compiler().Compile(ExtractionRuleSet.Default(), new ExtractionRuleConfiguration());

        // Input 1: Requirement (RFC 2119 uppercase)
        const string req = "The system MUST validate user input before processing.";
        var defaultReq = Eval(ExtractionRuleSet.Default(), req);
        var compiledReq = Eval(emptyCompiled, req);
        compiledReq.Classification.Should().Be(defaultReq.Classification);
        compiledReq.Signal.Should().Be(defaultReq.Signal);
        compiledReq.WinningRuleName.Should().Be(defaultReq.WinningRuleName);

        // Input 2: Test (BDD opener)
        const string bdd = "Given a user is logged in";
        var defaultBdd = Eval(ExtractionRuleSet.Default(), bdd);
        var compiledBdd = Eval(emptyCompiled, bdd);
        compiledBdd.Classification.Should().Be(defaultBdd.Classification);
        compiledBdd.Signal.Should().Be(defaultBdd.Signal);
        compiledBdd.WinningRuleName.Should().Be(defaultBdd.WinningRuleName);

        // Input 3: NeedsClarification (question terminator)
        const string nc = "What should happen when the session expires?";
        var defaultNc = Eval(ExtractionRuleSet.Default(), nc);
        var compiledNc = Eval(emptyCompiled, nc);
        compiledNc.Classification.Should().Be(defaultNc.Classification);
        compiledNc.Signal.Should().Be(defaultNc.Signal);
        compiledNc.WinningRuleName.Should().Be(defaultNc.WinningRuleName);
    }

    // =========================================================================
    // Engine startup validation compatibility
    // =========================================================================

    [Fact]
    public void Compiled_set_with_prefix_rules_passes_ExtractionRuleEngine_startup_validation()
    {
        var config = new ExtractionRuleConfiguration
        {
            BddKeywordAdditions = ["Scenario"],
            PrefixRules =
            [
                new PrefixRuleEntry { Prefix = "AC-",  Classification = ScenarioKind.Test,        Priority = 15 },
                new PrefixRuleEntry { Prefix = "SEC-", Classification = ScenarioKind.Requirement, Priority = 25 },
            ],
            IgnorePrefixes = ["SKIP-"],
            DisabledRuleNames = ["Classify:DeferralMarker"],
            PriorityOverrides = { ["Classify:FrPrefix"] = 55 },
        };

        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), config);
        var act = () => EngineFor(compiled);

        act.Should().NotThrow("the compiler must produce a rule set that satisfies ExtractionRuleEngine startup validation");
    }

    // =========================================================================
    // Observability — OBS-US4-005 log event compliance (T127)
    // =========================================================================

    [Fact]
    public void Valid_config_logs_ExtractionRuleConfigurationLoaded_with_correct_counts()
    {
        var logger = new CapturingLogger<ExtractionRuleSetCompiler>();
        var compiler = new ExtractionRuleSetCompiler(logger);
        var config = new ExtractionRuleConfiguration
        {
            BddKeywordAdditions = ["Scenario"],
            PrefixRules = [new PrefixRuleEntry { Prefix = "AC-", Classification = ScenarioKind.Test }],
        };

        compiler.Compile(ExtractionRuleSet.Default(), config);

        var loaded = logger.Entries
            .Should().ContainSingle(e =>
                e.Level == LogLevel.Information && e.Message.Contains("ExtractionRuleConfigurationLoaded"))
            .Which;
        loaded.Message.Should().Contain("bddKeywordAdditionCount=1");
        loaded.Message.Should().Contain("prefixRuleCount=1");
        logger.Entries.Should().NotContain(e => e.Message.Contains("ExtractionRuleConfigurationFailed"));
    }

    [Fact]
    public void Invalid_config_logs_ExtractionRuleConfigurationFailed_without_value_content()
    {
        var logger = new CapturingLogger<ExtractionRuleSetCompiler>();
        var compiler = new ExtractionRuleSetCompiler(logger);
        const string invalidKeyword = "Given+"; // regex metacharacter — must NOT appear in log
        var config = new ExtractionRuleConfiguration { BddKeywordAdditions = [invalidKeyword] };

        compiler.Compile(ExtractionRuleSet.Default(), config);

        var failed = logger.Entries
            .Should().ContainSingle(e =>
                e.Level == LogLevel.Warning && e.Message.Contains("ExtractionRuleConfigurationFailed"))
            .Which;
        failed.Message.Should().Contain("fieldName=BddKeywordAdditions");
        failed.Message.Should().Contain("violationType=regex_metacharacter");
        failed.Message.Should().Contain("fallbackApplied=True");
        failed.Message.Should().NotContain(invalidKeyword); // OBS-US4-005: no field value content

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("ExtractionRuleConfigurationFallback") &&
            e.Message.Contains("reason=validation_failure"));
    }

    [Fact]
    public void Empty_config_logs_ExtractionRuleConfigurationLoaded_zeros_and_no_configuration_fallback()
    {
        var logger = new CapturingLogger<ExtractionRuleSetCompiler>();
        var compiler = new ExtractionRuleSetCompiler(logger);

        compiler.Compile(ExtractionRuleSet.Default(), new ExtractionRuleConfiguration());

        var loaded = logger.Entries
            .Should().ContainSingle(e =>
                e.Level == LogLevel.Information && e.Message.Contains("ExtractionRuleConfigurationLoaded"))
            .Which;
        loaded.Message.Should().Contain("bddKeywordAdditionCount=0");
        loaded.Message.Should().Contain("prefixRuleCount=0");

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("ExtractionRuleConfigurationFallback") &&
            e.Message.Contains("reason=no_configuration"));
    }

    // =========================================================================
    // Helpers — private
    // =========================================================================

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
