using System.Text.RegularExpressions;
using BirkNext.Api.Services.TestCoverage;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Behaviour from developer integration tests (2026-11-22): for a project with no specification requirements, the developer tests that cross a
/// real boundary — a browser (E2E), the API hosted in-process, or real/in-process messaging, by the Test Coverage level rule — become one candidate
/// per test class, with the test names as readable steps. Unit, component, repository-only, contract, security and performance tests, skipped tests
/// and tests of developer/demo tools are never offered. With specification requirements this source is not used: scenarios come from the
/// specification and tests are shown on the scenarios whose requirements they name.
/// </summary>
public sealed class DeveloperIntegrationTestScenarioProvider : ITestScenarioCandidateProvider
{
    private const string Engine = "Developer integration tests";
    public const int MaxClasses = 200;
    public string CapabilityId => D.DeveloperTests;

    public Task<CandidateProviderOutput> DiscoverAsync(ScenarioDiscoveryContext context, CancellationToken ct)
    {
        if (context.Snapshot is null)
            return Done(DiscoveryCapabilityState.NotApplicable, "No source snapshot is linked to this project.");
        if (context.Workspace is not { Requirements.Count: 0 })
            return Done(DiscoveryCapabilityState.NotApplicable, context.Workspace is null
                ? "Used only for a project without a specification; this project's documents could not be read."
                : "Not used: this project has specification requirements, so scenarios come from them and developer tests are shown on the scenarios they name.");
        var facts = context.Snapshot.TestBehaviorEvidence?.Tests ?? [];
        if (facts.Count == 0)
            return Done(DiscoveryCapabilityState.NoEvidence, "No test content was analyzed in the source (re-analyze the source to read tests).");
        var chosen = facts.Where(f => !f.Skipped && !IsDeveloperTool(f) && !IsContractOrProbe(f) && Relevant(TestCoverageReviewEngine.Level(f)) is not null).ToList();
        var classes = chosen.GroupBy(f => (f.Project, f.ClassName)).OrderBy(g => g.Key.Project, StringComparer.Ordinal).ThenBy(g => g.Key.ClassName, StringComparer.Ordinal).ToList();
        var list = classes.Take(MaxClasses).Select(g => Candidate(context.ProjectKey, g.Key.Project, g.Key.ClassName, [.. g])).ToList();
        var detail = list.Count == 0
            ? $"{facts.Count} developer tests analyzed; none crosses a real boundary (browser, hosted API or messaging) — they are unit or component tests, which are not manual test cases."
            : $"{chosen.Count} of {facts.Count} developer tests cross a real boundary (browser, hosted API or messaging), in {classes.Count} test class{(classes.Count == 1 ? "" : "es")}; one candidate per class."
              + (classes.Count > MaxClasses ? $" The first {MaxClasses} classes are offered." : "");
        return Task.FromResult(new CandidateProviderOutput(D.Capability(D.DeveloperTests, list.Count == 0 ? DiscoveryCapabilityState.NoEvidence : DiscoveryCapabilityState.Available,
            list.Count, detail, Engine, ["Derived from developer test names and setup: confirm the expected behaviour with the developer before testing it manually.",
                "Only .NET tests whose content was analyzed are read."]), list));
    }

    private static Task<CandidateProviderOutput> Done(DiscoveryCapabilityState state, string detail) =>
        Task.FromResult(new CandidateProviderOutput(D.Capability(D.DeveloperTests, state, 0, detail, Engine), []));

    /// <summary>The levels a person can observe from outside: a browser journey, an API call, a message through a broker. Null = not offered.</summary>
    private static string? Relevant((TestLevel Level, EvidenceStrength Strength, string Basis) level) =>
        level.Strength is EvidenceStrength.Weak or EvidenceStrength.NotEnough ? null : level.Level switch
        {
            TestLevel.E2E => "browser",
            TestLevel.Api => "API",
            TestLevel.Integration => "messaging",
            _ => null,
        };

    private static readonly Regex TestToken = new("^(Unit|UnitTests?|Tests?|Integration|IntegrationTests?|Contract|Contracts|ContractTests?|E2E|EndToEnd|Specs?)$", RegexOptions.IgnoreCase);

    /// <summary>Contract-baseline tests (a contract test project or category) and operational probes (health, version, ping, OpenAPI document)
    /// guard the developers' build, not behaviour a tester checks.</summary>
    private static bool IsContractOrProbe(TestBehaviorFact f) =>
        f.Categories.Any(c => c.Contains("contract", StringComparison.OrdinalIgnoreCase))
        || f.Project.Split('.').Any(p => Regex.IsMatch(p, "^(Contract|Contracts|ContractTests?)$", RegexOptions.IgnoreCase))
        || Regex.IsMatch(f.ClassName.Split('.', '+').Last(), "^(Health|Liveness|Readiness|Version|Ping|Swagger|OpenApi)", RegexOptions.IgnoreCase);

    private static bool IsDeveloperTool(TestBehaviorFact f) =>
        D.IsDeveloperToolPage(new BirkNext.CriticalE2E.SourceUiPage { Module = f.Project, File = f.FilePath });

    private static DiscoveredScenario Candidate(string projectKey, string project, string className, List<TestBehaviorFact> tests)
    {
        var shortName = className.Split('.', '+').Last();
        var title = Words(Regex.Replace(shortName, "(Integration|EndToEnd|E2E)?Tests?$", "", RegexOptions.IgnoreCase)) is { Length: > 0 } t ? t : Words(shortName);
        var levels = tests.Select(f => Relevant(TestCoverageReviewEngine.Level(f))).OfType<string>().Distinct().ToList();
        var steps = tests.OrderBy(f => f.Line).Select(f => Step(f.MethodName)).ToList();
        var expected = steps.Select(s => s.Expected).OfType<string>().Distinct().Take(3).ToList();
        return D.FinishCandidate(new DiscoveredScenario
        {
            CapabilityId = D.DeveloperTests, SourceCategory = "Developer integration tests", Area = Words(project.Split('.').LastOrDefault(p => !TestToken.IsMatch(p)) ?? project),
            Title = Upper(title), Purpose = $"A developer's automated {string.Join(" / ", levels)} test{(tests.Count == 1 ? "" : "s")} ({tests.Count}) cover{(tests.Count == 1 ? "s" : "")} this behaviour. Confirm with the developer what it means before testing it manually in the test environment.",
            Type = levels.Contains("messaging") ? ScenarioCandidateType.Integration : levels.Contains("browser") ? ScenarioCandidateType.BusinessE2E : ScenarioCandidateType.BusinessFunctional,
            Confidence = CandidateConfidence.ExistingTest,
            ExpectedBehaviour = expected.Count == 0 ? null : string.Join("; ", expected), ExpectedBasis = expected.Count == 0 ? null : $"Names of the developer tests in {tests[0].FilePath}",
            Steps = steps,
            Evidence = [new("developer-test", $"{project}/{className}", tests[0].FilePath)],
            CanonicalKey = $"devtest:{project}/{className}",
            Engine = Engine,
            OpenQuestions = ["Derived from a developer's automated tests: confirm the expected behaviour with the developer before testing."],
        }, projectKey);
    }

    /// <summary>
    /// A test name as a step. "Trigger_WithSourceOutsideFilter_IsRejected" (2–4 PascalCase segments: action, condition, result) →
    /// ("Trigger with source outside filter", "Is rejected"); a sentence ("A_new_report_is_stored", "Post_user_returns_201") → the sentence, with
    /// no invented split. The expected result is taken only from a final segment that reads as an outcome.
    /// </summary>
    internal static DiscoveredStep Step(string method)
    {
        var raw = method.Split('_', StringSplitOptions.RemoveEmptyEntries).ToList();
        var segments = raw.Count is >= 2 and <= 4 && raw.All(p => char.IsUpper(p[0]) || char.IsDigit(p[0]));
        if (!segments) return new(Upper(Words(string.Join(" ", raw.Select(Words)))), null);
        var parts = raw.Select(Words).ToList();
        // The first segment after the action that reads as an outcome starts the expected result ("Returns 201", "Is refused … for …").
        var at = raw.FindIndex(1, p => Outcome.IsMatch(p));
        return at > 0
            ? new(Upper(Lower(string.Join(" ", parts.Take(at)))), Upper(Lower(string.Join(" ", parts.Skip(at)))))
            : new(Upper(Lower(string.Join(" ", parts))), null);
    }

    private static readonly Regex Outcome = new("^(Is|Are|Returns?|Should|Throws?|Fails?|Succeeds?|Rejects?|Accepts?|Creates?|Stores?|Publishes|Sends?|Does|Has|Gets?|Shows?|Logs?|Keeps?|Leaves?|Answers?|Responds?|Ignores?|Blocks?|Allows?|Denies|Retries|Moves?|Marks?|Updates?|Deletes?|Produces?|Raises?|Emits?|Records?|Skips?|Connects?|Pauses?|Resumes?|Echoes|Receives?|Appears?|Never|Always|Not)", RegexOptions.IgnoreCase);

    /// <summary>"AltinnHooksEndpoints" → "Altinn hooks endpoints" (acronyms kept: "XMLValidator" → "XML validator").</summary>
    internal static string Words(string identifier)
    {
        // lower→Upper+lower ("hooksEndpoints"), acronym→word ("XMLValidator"), letters→digits ("Returns201"); "GraphQL" stays whole.
        var spaced = Regex.Replace(identifier, "(?<=[a-zæøå0-9])(?=[A-ZÆØÅ][a-zæøå])|(?<=[A-ZÆØÅ])(?=[A-ZÆØÅ][a-zæøå])|(?<=[a-zæøåA-ZÆØÅ])(?=[0-9])", " ").Replace('_', ' ').Trim();
        return string.Join(" ", spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select((w, i) => i > 0 && w.Length > 1 && w.Skip(1).All(char.IsLower) ? w.ToLowerInvariant() : w));
    }

    /// <summary>Lower-cases the first letter of every word but the first, keeping acronyms and numbers ("Returns 201 And User" → "Returns 201 and user").</summary>
    private static string Lower(string text) => string.Join(" ", text.Split(' ').Select((w, i) => i > 0 && w.Length > 1 && char.IsUpper(w[0]) && w.Skip(1).All(char.IsLower) ? w.ToLowerInvariant() : w));

    private static string Upper(string text) => text.Length > 0 && char.IsLower(text[0]) ? char.ToUpperInvariant(text[0]) + text[1..] : text;
}
