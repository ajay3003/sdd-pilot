using System.Text;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Web.Tests.Services;

// =========================================================================
// T129 / T130 — Phase 28 stabilization: performance and FR-US4-010 gate
// =========================================================================
public sealed class ExtractionPipelineCompilerIntegrationTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ExtractionRuleSetCompiler Compiler()
        => new(NullLogger<ExtractionRuleSetCompiler>.Instance);

    private static IExtractionConfiguration DefaultExtractionConfig()
        => new ExtractionConfiguration();

    private static ScenarioExtractionService BuildService(ExtractionRuleSet ruleSet)
        => new(DefaultExtractionConfig(),
               new ExtractionRuleEngine(ruleSet, DefaultExtractionConfig()),
               NullLogger<ScenarioExtractionService>.Instance);

    // =========================================================================
    // T129 — Performance with maximally configured rule set
    // =========================================================================

    [Fact]
    public async Task MaxConfig_50_entries_per_group_pipeline_on_10k_input_completes_under_200ms()
    {
        // Build a maximally loaded configuration (50 entries in each array/collection).
        var keywords50 = Enumerable.Range(0, 50).Select(i => $"Keyword{i:D2}").ToArray();
        var config = new ExtractionRuleConfiguration
        {
            BddKeywordAdditions = keywords50,
            Rfc2119UppercaseAdditions = Enumerable.Range(0, 50).Select(i => $"VERB{i:D2}").ToArray(),
            Rfc2119LowercaseAdditions = Enumerable.Range(0, 50).Select(i => $"verb{i:D2}").ToArray(),
            DeferralMarkerAdditions = Enumerable.Range(0, 50).Select(i => $"DEFER{i:D2}").ToArray(),
            IgnorePrefixes = Enumerable.Range(0, 50).Select(i => $"IGN{i:D2}").ToArray(),
            PrefixRules =
            [
                .. Enumerable.Range(0, 50).Select(i => new PrefixRuleEntry
                {
                    Prefix = $"PFX{i:D2}-",
                    Classification = (ScenarioKind)(i % 3),
                    Priority = (i % 49) + 1,
                }),
            ],
        };

        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), config);
        var service = BuildService(compiled);

        // Build a 10 000+ character input: 170 lines × ~60 chars each = ~10 200 chars.
        var sb = new StringBuilder(11_000);
        for (int i = 0; i < 170; i++)
            sb.AppendLine($"The system MUST implement feature {i:D3} to enable user workflow automation.");
        var input = sb.ToString();

        input.Length.Should().BeGreaterThanOrEqualTo(10_000);

        var result = await service.ExtractAsync(input);

        // Measured on development hardware: ~2 ms (regex compiled at startup, not per call).
        result.DurationMs.Should().BeLessThan(200,
            "regex compilation happens at Compile() startup, not during pipeline execution");
    }

    // =========================================================================
    // T130 — FR-US4-010: Compile(Default(), empty config) ≡ Default() pipeline
    // =========================================================================

    [Fact]
    public async Task Empty_config_pipeline_results_identical_to_Default_for_requirement_input()
    {
        const string input =
            "The system MUST validate user input before processing.\n" +
            "The system SHALL log all failed authentication attempts.";

        var defaultResult = await BuildService(ExtractionRuleSet.Default()).ExtractAsync(input);
        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), new ExtractionRuleConfiguration());
        var compiledResult = await BuildService(compiled).ExtractAsync(input);

        AssertIdenticalResults(defaultResult, compiledResult);
    }

    [Fact]
    public async Task Empty_config_pipeline_results_identical_to_Default_for_test_input()
    {
        const string input =
            "Given a user is logged in\n" +
            "When the user submits the form\n" +
            "Then the system saves the record";

        var defaultResult = await BuildService(ExtractionRuleSet.Default()).ExtractAsync(input);
        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), new ExtractionRuleConfiguration());
        var compiledResult = await BuildService(compiled).ExtractAsync(input);

        AssertIdenticalResults(defaultResult, compiledResult);
    }

    [Fact]
    public async Task Empty_config_pipeline_results_identical_to_Default_for_needs_clarification_input()
    {
        const string input =
            "What should happen when the session expires?\n" +
            "TBD the notification subsystem design";

        var defaultResult = await BuildService(ExtractionRuleSet.Default()).ExtractAsync(input);
        var compiled = Compiler().Compile(ExtractionRuleSet.Default(), new ExtractionRuleConfiguration());
        var compiledResult = await BuildService(compiled).ExtractAsync(input);

        AssertIdenticalResults(defaultResult, compiledResult);
    }

    private static void AssertIdenticalResults(
        ExtractionPipelineResult expected, ExtractionPipelineResult actual)
    {
        actual.Status.Should().Be(expected.Status);
        actual.Candidates.Should().HaveCount(expected.Candidates.Count);
        for (int i = 0; i < expected.Candidates.Count; i++)
        {
            actual.Candidates[i].Title.Should().Be(expected.Candidates[i].Title);
            actual.Candidates[i].Classification.Should().Be(expected.Candidates[i].Classification);
            actual.Candidates[i].ClassificationSignal.Should().Be(expected.Candidates[i].ClassificationSignal);
        }
    }
}
