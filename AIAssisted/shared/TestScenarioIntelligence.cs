using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BirkNext.DeliveryReporting;

// ── Test scenario intelligence (rule set scenarios/1) ─────────────────────────────────────────────────────────────────────
// DRAFT test scenarios for acceptance criteria a functional scope holds and no test automates yet. Candidates only: never run, never
// published, never counted as coverage. Existing coverage is checked first (explicit > candidate). Priority is explainable from evidence
// the item carries (story priority, quality category, coverage) — complexity or length alone never makes a scenario High.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioPriority { High, Medium, Low }

public sealed record TestScenarioDraft(
    string Key, string Scenario, string Purpose, string? Given, string? When, string Then, string Coverage, ScenarioPriority Priority, string PriorityReason,
    string DerivedFrom, string Module, string FeatureKey, string? DuplicateOf)
{
    public const string Status = "Draft · not run · not coverage";
}

public static partial class TestScenarioIntelligence
{
    public const string RuleSetVersion = "scenarios/1";

    /// <summary>One draft per acceptance criterion without automated coverage, in scope order; same feature + same expected outcome = duplicate.</summary>
    public static IReadOnlyList<TestScenarioDraft> Drafts(ScopeResolution resolution)
    {
        var stories = resolution.Items.Where(i => i.Type is ScopeArtifactType.UserStory or ScopeArtifactType.Scenario).ToDictionary(i => i.Key, StringComparer.Ordinal);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var drafts = new List<TestScenarioDraft>();
        foreach (var ac in resolution.Items.Where(i => i.Type == ScopeArtifactType.AcceptanceCriterion && !i.Unavailable && i.Coverage != ScopeCoverage.AutomatedCoverage))
        {
            var story = ac.ParentKey is { } parent ? stories.GetValueOrDefault(parent) : null;
            var (given, when, then) = Split(ac.Title);
            var (priority, reason) = Priority(ac, story);
            var duplicateKey = $"{ac.FeatureKey}|{Normalize(then)}";
            var duplicateOf = seen.GetValueOrDefault(duplicateKey);
            if (duplicateOf is null) seen[duplicateKey] = ac.Key;
            drafts.Add(new TestScenarioDraft(
                Key: "draft:" + ac.Key,
                Scenario: $"{(story is null ? ac.FeatureTitle : story.Title)} — {ac.DisplayId}",
                Purpose: $"Verify {ac.DisplayId} of {(story is null ? "the feature" : story.DisplayId)} ({FunctionalQualityClassifier.Label(ac.Category)}).",
                Given: given, When: when, Then: then,
                Coverage: ac.Coverage == ScopeCoverage.CandidateCoverageOnly ? "Candidate coverage only — confirm or replace" : "No known coverage",
                Priority: priority, PriorityReason: reason,
                DerivedFrom: $"{ac.Module} / {ac.FeatureKey} / {(story?.DisplayId ?? "feature")} / {ac.DisplayId}",
                Module: ac.Module, FeatureKey: ac.FeatureKey, DuplicateOf: duplicateOf is null ? null : "draft:" + duplicateOf));
        }
        return drafts;
    }

    /// <summary>Explainable priority from item evidence only.</summary>
    public static (ScenarioPriority, string) Priority(ScopeItem ac, ScopeItem? story)
    {
        var uncovered = ac.Coverage != ScopeCoverage.CandidateCoverageOnly;
        var p = story?.Priority?.Trim().ToUpperInvariant();
        if (ac.Category is QualityCategory.Security or QualityCategory.Privacy && uncovered)
            return (ScenarioPriority.High, $"{FunctionalQualityClassifier.Label(ac.Category)} criterion with no known coverage");
        if (p == "P1" && uncovered) return (ScenarioPriority.High, "P1 user story with no known coverage");
        if (p is "P1" or "P2") return (ScenarioPriority.Medium, p == "P1" ? "P1 user story; a candidate test may already cover it" : "P2 user story");
        if (ac.Category is QualityCategory.Reliability or QualityCategory.DataQuality or QualityCategory.Compliance)
            return (ScenarioPriority.Medium, $"{FunctionalQualityClassifier.Label(ac.Category)} criterion");
        return (ScenarioPriority.Low, p is null ? "No story priority stated" : $"{p} user story");
    }

    /// <summary>Given / When / Then as the criterion states them (Markdown emphasis removed); otherwise the whole text is the expected outcome.</summary>
    public static (string? Given, string? When, string Then) Split(string text)
    {
        var plain = text.Replace("**", "").Replace("__", "").Trim();
        var m = GwtRe().Match(plain);
        return m.Success ? (m.Groups[1].Value.Trim().TrimEnd(','), m.Groups[2].Value.Trim().TrimEnd(','), m.Groups[3].Value.Trim()) : (null, null, plain);
    }

    private static string Normalize(string text) => WsRe().Replace(text.ToLowerInvariant(), " ").Trim().TrimEnd('.');

    [GeneratedRegex(@"(?is)^\s*given\s+(.+?)\s*,?\s*when\s+(.+?)\s*,?\s*then\s+(.+)$")]
    private static partial Regex GwtRe();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WsRe();
}
