using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.Specifications;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Opt-in regression on the real M2LB archive (set BIRKNEXT_M2LB_MANUAL_ARCHIVE to a local M2LB ZIP; skipped otherwise, never committed data):
/// SkoleService's acceptance scenario "child past compulsory school age" (User Story 6, scenario 3 — "Scenario 42" in the parser's numbering)
/// names no requirement, so the requirement suggestions are what links it. The requirements come from the real specification through the same
/// catalog parser Project Import uses; the scenario's Given/When/Then are read from the same file. Expectations are checked against the
/// requirements' own wording, never assumed by number: the requirement that defines who is expected must be suggested (C), the operations
/// alerting requirements must not be.
/// </summary>
public sealed class RealM2lbRequirementSuggestionTests(ITestOutputHelper output)
{
    private const string Variable = "BIRKNEXT_M2LB_MANUAL_ARCHIVE";
    private const string SpecPath = "SkoleService/specs/001-skoleservice/spec.md";

    private static string? Archive => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } a && File.Exists(a) ? a : null;

    [Fact]
    public void Scenario_42_child_past_compulsory_school_age_gets_the_requirements_that_state_it()
    {
        if (Archive is not { } archive) { output.WriteLine($"Skipped: {Variable} is not set to an M2LB archive."); return; }
        using var zip = ZipFile.OpenRead(archive);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').EndsWith(SpecPath, StringComparison.Ordinal));
        entry.Should().NotBeNull($"the archive holds {SpecPath}");
        string spec;
        using (var reader = new StreamReader(entry!.Open(), Encoding.UTF8)) spec = reader.ReadToEnd();

        var (_, requirements) = SpecificationCatalogService.Read([new SpecificationCatalogService.Document(SpecPath, spec)]);
        requirements.Should().NotBeEmpty();

        // The acceptance scenario as written (its own text, multi-line), found by its Given.
        var flat = Regex.Replace(spec, @"\s+", " ");
        var m = Regex.Match(flat, @"\*\*Given\*\* (a child past compulsory school age), \*\*When\*\* (the period locks), \*\*Then\*\* (.+?)(?= \d+\. \*\*Given| ---|$)");
        m.Success.Should().BeTrue("the scenario is in the specification");
        var then = m.Groups[3].Value.Trim();
        output.WriteLine($"Scenario: Given {m.Groups[1].Value}, When {m.Groups[2].Value}, Then {then}");
        var text = $"Child past compulsory school age when the period locks\n{m.Groups[1].Value}\n{m.Groups[2].Value}\n{then}";
        Regex.IsMatch(text, @"\bFR-\d+").Should().BeFalse("the scenario names no requirement (why suggestions are needed)");

        var options = requirements.Select(r => new ProjectRequirementOption(r.RequirementId, r.Title, r.Path)).ToList();
        var suggestions = ScenarioPlanning.SuggestRequirements(text, [], [], options);
        foreach (var s in suggestions) output.WriteLine($"{s.Level,-10} {s.RequirementId,-8} {s.Score:0.000} {s.Reason}");

        var definesExpected = requirements.Where(r => Regex.IsMatch(r.Title, @"MUST be expected", RegexOptions.IgnoreCase) && r.Title.Contains("compulsory school age", StringComparison.OrdinalIgnoreCase)).ToList();
        definesExpected.Should().NotBeEmpty("the specification has a requirement defining who is expected");
        suggestions.Where(s => s.Level is RequirementSuggestionLevel.Semantic).Select(s => s.RequirementId)
            .Should().Contain(definesExpected.Select(r => r.RequirementId), "the requirement that states who is expected (compulsory school age) is the same behaviour");
        var alerting = requirements.Where(r => r.Title.Contains("alerted", StringComparison.OrdinalIgnoreCase) && !r.Title.Contains("compulsory", StringComparison.OrdinalIgnoreCase)).Select(r => r.RequirementId).ToHashSet();
        suggestions.Where(s => s.Level == RequirementSuggestionLevel.Semantic).Should().NotContain(s => alerting.Contains(s.RequirementId), "operations alerting is not this behaviour");
        suggestions.Should().OnlyContain(s => s.SpecificationPath == SpecPath && s.Reason.Length > 0);
    }
}
