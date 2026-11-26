using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace BirkNext.TestPlanning;

// ── Test level and hierarchical test plan structure (2026-11-26) ────────────────────────────────────────────────────────────────────────────
// Test level is a third independent dimension (beside the scenario type and Manual/Technical). The plan structure turns a selection into saved
// suites (business data), built by ONE rule set used for the browser's preview and the server's creation, so the preview is exactly what is saved.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioTestLevel { Component, Integration, EndToEnd, Unclassified }

public sealed record TestLevelResult(ScenarioTestLevel Level, bool Candidate, string Explanation);

public static class TestLevelClassifier
{
    public const string RuleVersion = "test-level/1";

    public static string Label(ScenarioTestLevel l) => l switch
    {
        ScenarioTestLevel.Component => "Komponent",
        ScenarioTestLevel.Integration => "Integrasjon",
        ScenarioTestLevel.EndToEnd => "Ende til ende",
        _ => "Uavklart",
    };

    public static readonly ScenarioTestLevel[] Order = [ScenarioTestLevel.EndToEnd, ScenarioTestLevel.Integration, ScenarioTestLevel.Component, ScenarioTestLevel.Unclassified];

    /// <summary>
    /// The level the discovery evidence states for a scenario (its candidate type), never its title, folder or number of services:
    /// <list type="bullet">
    /// <item>BusinessE2E (a documented business flow across components) → «Ende til ende», marked as a candidate: the flow is documented, the
    /// executable end-to-end test is not proven.</item>
    /// <item>Integration (a documented message or system integration) → «Integrasjon».</item>
    /// <item>ApiContract, UiFunctional, UiSmoke (one service's operation or one application's page) → «Komponent».</item>
    /// <item>An acceptance scenario of a specification, performance and security candidates → «Uavklart»: the source does not state a boundary.</item>
    /// </list>
    /// </summary>
    public static TestLevelResult Of(ScenarioCandidateType? type) => type switch
    {
        ScenarioCandidateType.BusinessE2E => new(ScenarioTestLevel.EndToEnd, true, "E2E-kandidat: en dokumentert forretningsflyt på tvers av komponenter. Testgrensen må gjennomgås."),
        ScenarioCandidateType.Integration => new(ScenarioTestLevel.Integration, false, "En dokumentert integrasjon eller meldingsflyt mellom systemer."),
        ScenarioCandidateType.ApiContract => new(ScenarioTestLevel.Component, false, "En operasjon i én tjenestes API-kontrakt."),
        ScenarioCandidateType.UiFunctional or ScenarioCandidateType.UiSmoke => new(ScenarioTestLevel.Component, false, "En side eller funksjon i én applikasjon."),
        _ => new(ScenarioTestLevel.Unclassified, false, "Kilden oppgir ikke testgrensen (hvilke systemer som er med)."),
    };
}

/// <summary>What a plan structure needs to know about one case (from its scenario; never guessed).</summary>
public sealed record PlanStructureFacts(string CaseId, ScenarioTestLevel Level, string? Area, string? Module, string? Story, ScenarioRequirementType? Type);

public sealed record PlanStructureResult(List<TestSuiteDraft> Suites, Dictionary<string, string> Assignment);

/// <summary>The saved grouping templates of new plans (System Settings → Manuell testing → Testplanstruktur).</summary>
public static class PlanStructureTemplates
{
    public const string LevelArea = "level-area", ModuleStory = "module-story", TypeLevel = "type-level", None = "none";
    public const string Default = LevelArea;
    public const string NoArea = "Uten angitt område", NoModule = "Uten modul", NoStory = "Uten brukerhistorie";

    public static readonly IReadOnlyList<(string Key, string Label, string Description)> All =
    [
        (LevelArea, "Testnivå → Funksjonelt område", "Ende til ende, integrasjon og komponent øverst, med funksjonelle områder under. Anbefalt."),
        (ModuleStory, "Modul → Brukerhistorie", "Én gruppe per modul, med brukerhistoriene under."),
        (TypeLevel, "Scenariotype → Testnivå", "Funksjonelle og ikke-funksjonelle scenarioer hver for seg, med testnivå under."),
        (None, "Ingen automatisk gruppering", "Testtilfellene legges i områdegrupper slik testplaner ble laget før."),
    ];

    public static bool Known(string? key) => key is not null && All.Any(t => t.Key == key);
    public static string Label(string key) => All.FirstOrDefault(t => t.Key == key).Label ?? key;
}

/// <summary>
/// Builds a plan's suites from a selection: only groups that hold at least one case (no empty suites), stable ids derived from the group path
/// (the same selection gives the same ids; two groups with one name under different parents are different suites), at most two levels. When
/// every case has the same test level, the level layer is left out (an E2E-only plan is «Rapportering», «CDC» …) and the level is kept on the
/// suites and on every case. Deterministic and pure.
/// </summary>
public static class PlanStructure
{
    public static PlanStructureResult Build(string template, IReadOnlyList<PlanStructureFacts> facts)
    {
        var suites = new List<TestSuiteDraft>();
        var assignment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (facts.Count == 0) return new(suites, assignment);
        var oneLevel = facts.Select(f => f.Level).Distinct().Count() == 1;
        (string? Outer, ScenarioTestLevel? OuterLevel, string Inner, ScenarioTestLevel? InnerLevel) Path(PlanStructureFacts f) => template switch
        {
            PlanStructureTemplates.ModuleStory => (f.Module ?? PlanStructureTemplates.NoModule, null, f.Story ?? PlanStructureTemplates.NoStory, null),
            PlanStructureTemplates.TypeLevel => (f.Type is { } t ? ScenarioTypeClassifier.GroupLabel(t) : "Ikke akseptansescenarioer", null, TestLevelClassifier.Label(f.Level), f.Level),
            PlanStructureTemplates.None => (null, null, f.Area ?? PlanStructureTemplates.NoArea, null),
            _ => oneLevel ? (null, f.Level, f.Area ?? PlanStructureTemplates.NoArea, f.Level) : (TestLevelClassifier.Label(f.Level), f.Level, f.Area ?? PlanStructureTemplates.NoArea, f.Level),
        };
        var ordered = facts.Select(f => (Facts: f, Path: Path(f))).OrderBy(x => OuterRank(template, x.Path.Outer, x.Path.OuterLevel)).ThenBy(x => x.Path.Outer, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Path.Inner == PlanStructureTemplates.NoArea || x.Path.Inner == PlanStructureTemplates.NoStory ? 1 : 0).ThenBy(x => x.Path.Inner, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var (f, path) in ordered)
        {
            string? parent = null;
            if (path.Outer is { } outer)
            {
                parent = SuiteId(outer);
                if (suites.All(s => s.Id != parent))
                    suites.Add(new TestSuiteDraft(parent, outer, "structure", null) { Order = suites.Count(s => s.ParentId is null), TestLevel = path.OuterLevel });
            }
            var leaf = SuiteId(path.Outer is null ? path.Inner : $"{path.Outer}/{path.Inner}");
            if (suites.All(s => s.Id != leaf))
                suites.Add(new TestSuiteDraft(leaf, path.Inner, "structure", null) { ParentId = parent, Order = suites.Count(s => s.ParentId == parent), TestLevel = path.InnerLevel });
            assignment[f.CaseId] = leaf;
        }
        return new(suites, assignment);
    }

    private static int OuterRank(string template, string? outer, ScenarioTestLevel? level) =>
        level is { } l ? Array.IndexOf(TestLevelClassifier.Order, l)
        : template == PlanStructureTemplates.TypeLevel && outer is not null ? Array.IndexOf(ScenarioTypeClassifier.Order.Select(ScenarioTypeClassifier.GroupLabel).ToArray(), outer) is var i and >= 0 ? i : 99
        : outer is PlanStructureTemplates.NoModule ? 99 : 0;

    /// <summary>A stable, plan-scoped id from the group path (never the name alone: «Rapportering» under two parents is two suites).</summary>
    public static string SuiteId(string path) =>
        "suite:t:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.Trim().ToLowerInvariant())))[..12].ToLowerInvariant();

    /// <summary>The module of a specification path: «SkoleService» for «SkoleService/specs/001-…/spec.md», «Prosjektnivå» at the root.</summary>
    public static string ModuleOf(string specificationPath)
    {
        var parts = specificationPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && parts[1].Equals("specs", StringComparison.OrdinalIgnoreCase) ? parts[0] : "Prosjektnivå";
    }

    /// <summary>What the structure reads about one scenario (the same for the browser's preview and the server's creation).</summary>
    public static PlanStructureFacts FactsOf(string caseId, DiscoveredScenario s, string? fallbackArea = null) =>
        new(caseId, TestLevelClassifier.Of(s.Type).Level, string.IsNullOrWhiteSpace(s.Area) ? fallbackArea : s.Area,
            s.Story?.SpecificationPath is { Length: > 0 } spec ? ModuleOf(spec) : null,
            s.Story is { } story ? $"{story.StoryId} {story.Title}".Trim() : null,
            s.CapabilityId == "specification" && s.CanonicalKey.StartsWith("scenario:", StringComparison.Ordinal) ? ScenarioTypeClassifier.Propose(s).Type : null);

    // ── Tree helpers (Planlegg og klargjør, Testplaner, export) ──

    public static IEnumerable<TestSuiteDraft> Children(IReadOnlyList<TestSuiteDraft> suites, string? parentId) =>
        suites.Select((s, i) => (s, i)).Where(x => x.s.ParentId == parentId && (parentId is null || suites.Any(p => p.Id == parentId)) || parentId is null && x.s.ParentId is not null && suites.All(p => p.Id != x.s.ParentId))
            .OrderBy(x => x.s.Order).ThenBy(x => x.i).Select(x => x.s);

    /// <summary>«Ende til ende / Rapportering»: names from the top; a suite whose parent is missing starts its own path.</summary>
    public static string PathOf(IReadOnlyList<TestSuiteDraft> suites, string suiteId)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var s = suites.FirstOrDefault(x => x.Id == suiteId); s is not null && seen.Add(s.Id); s = s.ParentId is null ? null : suites.FirstOrDefault(x => x.Id == s.ParentId))
            names.Insert(0, s.Name);
        return string.Join(" / ", names);
    }

    /// <summary>Every suite in tree order (parents before children), with its depth.</summary>
    public static List<(TestSuiteDraft Suite, int Depth)> Flatten(IReadOnlyList<TestSuiteDraft> suites)
    {
        var result = new List<(TestSuiteDraft, int)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(string? parent, int depth)
        {
            foreach (var s in Children(suites, parent).Where(s => seen.Add(s.Id))) { result.Add((s, depth)); Walk(s.Id, depth + 1); }
        }
        Walk(null, 0);
        return result;
    }

    /// <summary>Null when the hierarchy is valid; else why not (unknown parent, a cycle, a suite under itself, duplicate ids).</summary>
    public static string? Validate(IReadOnlyList<TestSuiteDraft> suites)
    {
        if (suites.GroupBy(s => s.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } dup) return $"Testgruppen «{dup.First().Name}» finnes to ganger.";
        foreach (var s in suites.Where(s => s.ParentId is not null))
        {
            if (suites.All(p => p.Id != s.ParentId)) return $"Testgruppen «{s.Name}» ligger under en gruppe som ikke finnes.";
            var seen = new HashSet<string>(StringComparer.Ordinal) { s.Id };
            for (var p = suites.FirstOrDefault(x => x.Id == s.ParentId); p is not null; p = p.ParentId is null ? null : suites.FirstOrDefault(x => x.Id == p.ParentId))
                if (!seen.Add(p.Id)) return $"Testgruppen «{s.Name}» kan ikke ligge under seg selv.";
        }
        return null;
    }
}
