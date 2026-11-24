using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// The test-design benchmark (2026-11-24): 54 acceptance scenarios quoted verbatim from AIAssisted/testdata/M2LB.zip across 11 services
/// (Fixtures/test-design-benchmark.json). Each scenario goes through the real pipeline (AcceptanceScenarioInterpreter → case content →
/// TestDesignAdvisor / ScenarioPlanning.Variants). The quality gates are checked against the SOURCE TEXT, never against model output:
/// no invented references, no definitive expected result the source does not state, no lost negative assertion, no duplicate proposal, every
/// technique grounded in its trigger words. Classification accuracy is NOT claimed: no label in the fixture has been reviewed by a person.
/// Set BIRKNEXT_WRITE_BENCHMARK=1 to (re)write the reviewable results next to the fixture.
/// </summary>
public sealed class TestDesignBenchmarkTests(ITestOutputHelper output)
{
    private sealed record Story(string Id, string Title, string? Priority);
    private sealed record Review(string Status, string? Reviewer, List<string>? ExpectedApproaches, string? Notes);
    private sealed record Scenario(string Id, string Service, string SpecificationPath, string SourceVersion, Story Story, int ScenarioNumber,
        string Given, string When, string Then, List<string> ExplicitRequirementIds, Review HumanReview);
    private sealed record Fixture(string Source, string Note, List<Scenario> Scenarios);

    private static string Dir([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Fixtures");

    private static List<Scenario> Load() =>
        JsonSerializer.Deserialize<Fixture>(File.ReadAllText(Path.Combine(Dir(), "test-design-benchmark.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Scenarios;

    private sealed record Result(Scenario S, TestCaseDraft Case, ExecutionSuitability Suitability, IReadOnlyList<VerificationPoint> Points, List<TestCaseVariantProposal> Proposals,
        EndToEndProposal? E2E, IReadOnlyList<string> States);

    /// <summary>The scenario as BirkNext prepares it: the interpreter's action, the Given/Then verbatim, its open questions.</summary>
    private static TestCaseDraft Prepare(Scenario s)
    {
        var story = new CoverageUserStory { Key = s.Story.Id, StoryId = s.Story.Id, Title = s.Story.Title, Priority = s.Story.Priority, SpecificationPath = s.SpecificationPath };
        var i = AcceptanceScenarioInterpreter.Interpret(s.Given, s.When, s.Then, story);
        return new TestCaseDraft
        {
            Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "m2lb", ScenarioId = s.Id },
            Generated = new TestCaseContent
            {
                Title = $"{s.Story.Id} scenario {s.ScenarioNumber}", Purpose = s.Story.Title, Preconditions = i.Preconditions, Steps = [i.Action], StepExpected = [string.Join("\n", i.Expected)],
                ExpectedResult = s.Then, Capability = ScenarioOwnership.Specification, GroupTitle = s.Service, Story = story,
                Template = [new CandidateTemplateField("given", "Given", s.Given), new CandidateTemplateField("when", "When", s.When), new CandidateTemplateField("then", "Then", s.Then)],
                OpenQuestions = [.. i.Unresolved],
                Evidence = [new CandidateEvidenceRef("specification", $"{s.SpecificationPath}#{s.Story.Id}-{s.ScenarioNumber}")],
            },
        };
    }

    private static List<Result> Run(List<Scenario> scenarios) => [.. scenarios.Select(s =>
    {
        var c = Prepare(s);
        var f = TestDesignAdvisor.FactsOf(c);
        return new Result(s, c, TestDesignAdvisor.Suitability(f), TestDesignAdvisor.Verification(c), ScenarioPlanning.Variants(c, []), TestDesignAdvisor.EndToEnd(f), TestDesignAdvisor.StatesNamed(f.All));
    })];

    private static string Source(Scenario s) => $"{s.Given} {s.When} {s.Then}";

    [Fact]
    public void The_fixture_is_real_and_diverse()
    {
        var scenarios = Load();
        scenarios.Should().HaveCountGreaterThanOrEqualTo(40).And.HaveCountLessThanOrEqualTo(60);
        scenarios.Select(s => s.Service).Distinct().Should().HaveCountGreaterThanOrEqualTo(8);
        scenarios.Where(s => s.Service == "SkoleService").Select(s => s.Story.Id).Distinct().Should().HaveCountGreaterThanOrEqualTo(6);
        scenarios.Should().OnlyContain(s => s.HumanReview.Status == "not reviewed", "no label is claimed as reviewed");
        scenarios.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Quality_gates_hold_on_every_real_scenario()
    {
        var results = Run(Load());
        var invented = new List<string>();
        var unsupportedExpected = new List<string>();
        var lostNegatives = new List<string>();
        var ungrounded = new List<string>();
        foreach (var r in results)
        {
            var source = Source(r.S);
            // Zero invented source references: every requirement id the output names is in the scenario's own text.
            var outputText = string.Join("\n", r.Proposals.SelectMany(p => new[] { p.Title, p.Basis, p.Preconditions, p.Expected }).Concat(r.Points.Select(p => p.Assertion)).Where(x => x is not null));
            invented.AddRange(ScenarioPlanning.RequirementIdsIn(outputText).Where(id => !source.Contains(id, StringComparison.Ordinal)).Select(id => $"{r.S.Id}: {id}"));
            // Zero unsupported definitive expected results: a proposal's expected result is the source's own Then (first clause), or none.
            foreach (var p in r.Proposals.Where(p => p.Expected is not null))
                if (!r.S.Then.Contains(p.Expected!.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) && !r.S.Then.Contains(p.Expected!.TrimEnd('.')[1..], StringComparison.Ordinal))
                    unsupportedExpected.Add($"{r.S.Id}: {p.Title} → {p.Expected}");
            // Zero loss of critical negative assertions: every negated clause of the Then is a verification point, marked negative.
            foreach (var clause in Regex.Split(r.S.Then, @";\s+|,\s+and\s+").Where(c => Regex.IsMatch(c, @"\b(not|no|never|without|nothing)\b", RegexOptions.IgnoreCase)))
                if (!r.Points.Any(p => p.Negative && clause.Trim().TrimEnd('.').EndsWith(p.Assertion[Math.Min(5, p.Assertion.Length)..], StringComparison.OrdinalIgnoreCase)))
                    lostNegatives.Add($"{r.S.Id}: {clause}");
            // Every technique is grounded in its trigger words in the source.
            foreach (var p in r.Proposals)
            {
                var grounded = p.Dimension switch
                {
                    "Grenseverdi" or "Ugyldig inndata" => Regex.IsMatch(source, @"\d"),
                    "Idempotens" => Regex.IsMatch(source, @"\b(twice|again|duplicate|same message|same change|repeated|re-?sent)\b", RegexOptions.IgnoreCase),
                    "Rekkefølge" => Regex.IsMatch(source, @"\b(out of order|before the original|before its original|correction|withdraw)", RegexOptions.IgnoreCase),
                    "Samtidighet" => Regex.IsMatch(source, @"\b(concurrent|simultaneous|at the same time)", RegexOptions.IgnoreCase),
                    "Tilgang (negativ)" => Regex.IsMatch(source, "`[^`]+:[^`]+`"),
                    "Konfidensialitet" => Regex.IsMatch(source, @"Kode ?[67]|classified|security level", RegexOptions.IgnoreCase),
                    "Tilstandsforløp" => TestDesignAdvisor.StatesNamed(source).Count >= 3,
                    _ => true,
                };
                if (!grounded) ungrounded.Add($"{r.S.Id}: {p.Dimension} {p.Title}");
            }
        }

        invented.Should().BeEmpty();
        unsupportedExpected.Should().BeEmpty();
        lostNegatives.Should().BeEmpty();
        ungrounded.Should().BeEmpty();
        results.SelectMany(r => r.Proposals.GroupBy(p => p.Title, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => $"{r.S.Id}: {g.Key}")).Should().BeEmpty("no duplicate proposals");
        results.Should().OnlyContain(r => r.Proposals.Count <= TestDesignAdvisor.MaxProposals + 6, "bounded: no explosion of variations");
        results.Should().OnlyContain(r => r.E2E == null || r.E2E.Services.All(sv => Source(r.S).Contains(sv, StringComparison.OrdinalIgnoreCase) || sv == r.S.Service),
            "E2E names only services the source names");
    }

    [Fact]
    public void Not_every_scenario_is_a_manual_test_and_several_approaches_can_apply()
    {
        var results = Run(Load());
        results.Count(r => r.Suitability.Has(ExecutionApproach.Manual) && r.Suitability.Approaches.Count == 1).Should().BeLessThan(results.Count);
        results.Should().Contain(r => r.Suitability.Approaches.Count > 1, "a scenario can be manual-suitable and automation-recommended");
        results.Should().OnlyContain(r => r.Suitability.Approaches.Count > 0 && r.Suitability.Approaches.All(a => a.Reason.Length > 0), "every classification is explained");
    }

    [Fact]
    public void Benchmark_measures_time_and_memory_and_writes_the_reviewable_results_when_asked()
    {
        var scenarios = Load();
        GC.Collect(); GC.WaitForPendingFinalizers();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        var results = Run(scenarios);
        sw.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        output.WriteLine($"{results.Count} scenarios in {sw.ElapsedMilliseconds} ms, {allocated / 1024} KiB allocated");
        sw.ElapsedMilliseconds.Should().BeLessThan(10_000, "54 scenarios through the full advisor stay well under the UI budget");

        var counts = Enum.GetValues<ExecutionApproach>().ToDictionary(a => a, a => results.Count(r => r.Suitability.Has(a)));
        var md = new StringBuilder();
        md.AppendLine("# Test-design benchmark — provisional model output (NOT reviewed)");
        md.AppendLine();
        md.AppendLine($"Source: AIAssisted/testdata/M2LB.zip · {results.Count} verbatim acceptance scenarios · {results.Select(r => r.S.Service).Distinct().Count()} services · generated by `TestDesignBenchmarkTests` (set `BIRKNEXT_WRITE_BENCHMARK=1`).");
        md.AppendLine();
        md.AppendLine("**Human review status: 0 of " + results.Count + " scenarios reviewed.** Classification accuracy is therefore not established. The columns below are provisional model labels for an expert to confirm or correct in `test-design-benchmark.json` → `humanReview`.");
        md.AppendLine();
        md.AppendLine($"Run: {sw.ElapsedMilliseconds} ms, {allocated / 1024} KiB allocated for all scenarios (suitability + verification + proposals + E2E + states).");
        md.AppendLine();
        md.AppendLine("| Approach | Scenarios |");
        md.AppendLine("|---|---|");
        foreach (var (a, n) in counts) md.AppendLine($"| {TestDesignAdvisor.ApproachLabel(a)} | {n} |");
        md.AppendLine();
        md.AppendLine($"Proposals: {results.Sum(r => r.Proposals.Count)} in total ({string.Join(", ", results.SelectMany(r => r.Proposals).GroupBy(p => p.Dimension).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}); with a source-stated expected result: {results.SelectMany(r => r.Proposals).Count(p => p.Expected is not null)}.");
        md.AppendLine($"Verification points: {results.Sum(r => r.Points.Count)} ({results.Sum(r => r.Points.Count(p => p.Negative))} negative); method confirmed: {results.Sum(r => r.Points.Count(p => p.MethodConfirmed))} (no case has a person-written observation method).");
        md.AppendLine($"Cross-service E2E proposals: {results.Count(r => r.E2E is not null)}.");
        md.AppendLine();
        md.AppendLine("| Scenario | When | Approaches (provisional) | Verification surfaces | Proposals | Open |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in results)
            md.AppendLine($"| {r.S.Id} | {Esc(r.S.When)} | {string.Join(", ", r.Suitability.Approaches.Select(a => TestDesignAdvisor.ApproachLabel(a.Approach)))} | {string.Join(", ", r.Points.Select(p => p.Surface).Distinct())} | {string.Join("; ", r.Proposals.Select(p => p.Dimension).Distinct())} | {r.Case.Generated.OpenQuestions.Count} |");
        output.WriteLine(md.ToString());
        if (Environment.GetEnvironmentVariable("BIRKNEXT_WRITE_BENCHMARK") == "1")
            File.WriteAllText(Path.Combine(Dir(), "test-design-benchmark-results.md"), md.ToString().Replace("\r\n", "\n"));
    }

    private static string Esc(string s) => s.Replace("|", "\\|");
}
