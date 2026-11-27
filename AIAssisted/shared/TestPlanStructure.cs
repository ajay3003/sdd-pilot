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
        ScenarioCandidateType.BusinessE2E => new(ScenarioTestLevel.EndToEnd, true, "E2E-scenarioforslag: en dokumentert forretningsflyt på tvers av komponenter. Testgrensen må gjennomgås."),
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

    public static bool Known(string? key) => key is not null && (All.Any(t => t.Key == key) || ScenarioGroupAxes.TryParse(key, out _, out _));
    public static string Label(string key) =>
        ScenarioGroupAxes.TryParse(key, out var p, out var sec) ? ScenarioGroupAxes.Describe(p, sec) : All.FirstOrDefault(t => t.Key == key).Label ?? key;
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
        if (ScenarioGroupAxes.TryParse(template, out var primaryAxis, out var secondaryAxis)) return BuildByAxes(primaryAxis, secondaryAxis, facts);
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

    /// <summary>
    /// «Bruk gruppering som testplanstruktur» (2026-11-26): the suites follow the grouping chosen in Finn og forstå (primary, optional secondary),
    /// with the same labels and order as the list. Only groups that hold a case; stable ids from the group path; a test-level group keeps its level.
    /// </summary>
    private static PlanStructureResult BuildByAxes(ScenarioGroupAxis primary, ScenarioGroupAxis secondary, IReadOnlyList<PlanStructureFacts> facts)
    {
        var suites = new List<TestSuiteDraft>();
        var assignment = new Dictionary<string, string>(StringComparer.Ordinal);
        var two = secondary != ScenarioGroupAxis.None;
        var ordered = facts.Select(f => (Facts: f, Outer: ScenarioGroupAxes.ValueOf(f, primary), Inner: two ? ScenarioGroupAxes.ValueOf(f, secondary) : default))
            .OrderBy(x => x.Outer.Rank).ThenBy(x => x.Outer.Label, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => two ? x.Inner.Rank : 0).ThenBy(x => two ? x.Inner.Label : "", StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var (f, outer, inner) in ordered)
        {
            var outerPath = $"{primary}:{outer.Label}";
            var parent = SuiteId(outerPath);
            if (suites.All(s => s.Id != parent))
                suites.Add(new TestSuiteDraft(parent, outer.Label, "structure", null) { Order = suites.Count(s => s.ParentId is null), TestLevel = outer.Level });
            var leaf = parent;
            if (two)
            {
                leaf = SuiteId($"{outerPath}/{secondary}:{inner.Label}");
                if (suites.All(s => s.Id != leaf))
                    suites.Add(new TestSuiteDraft(leaf, inner.Label, "structure", null) { ParentId = parent, Order = suites.Count(s => s.ParentId == parent), TestLevel = inner.Level ?? outer.Level });
            }
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
    /// <remarks>AC-level classification (2026-11-26): the type is the acceptance scenario's OWN classification — a reviewer's decision for this
    /// scenario when given (<paramref name="decision"/>, server) or already resolved (<paramref name="resolvedType"/>, the browser's overview),
    /// else the proposal from the scenario's own text. Never the story's: a story is context, not a classification unit.</remarks>
    public static PlanStructureFacts FactsOf(string caseId, DiscoveredScenario s, string? fallbackArea = null, ScenarioTypeDecision? decision = null, ScenarioRequirementType? resolvedType = null) =>
        new(caseId, TestLevelClassifier.Of(s.Type).Level, string.IsNullOrWhiteSpace(s.Area) ? fallbackArea : s.Area,
            s.Story?.SpecificationPath is { Length: > 0 } spec ? ModuleOf(spec) : s.CapabilityId == "test-specification" && s.Area is { Length: > 0 } docModule ? docModule : null,
            s.Story is { } story ? $"{story.StoryId} {story.Title}".Trim() : null,
            ScenarioTypeClassifier.IsClassifiable(s) ? resolvedType ?? ScenarioTypeClassifier.Classify(s, decision).Type : null);

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

// ── Scenario grouping axes (2026-11-26) ──────────────────────────────────────────────────────────────────────────────────────────────────────
// Independent ways to organise scenarios in Finn og forstå (presentation) and, when the tester asks for it, the suites of a NEW plan. Test level,
// scenario type and test method are separate dimensions and never merged. An unknown value has its own honest group; nothing is reclassified.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioGroupAxis { None, Story, TestLevel, ScenarioType, Area, Module, Method, Specification }

public static class ScenarioGroupAxes
{
    public const string Prefix = "axes:";
    public const string NoStory = "Scenarioer uten brukerhistorie", NoArea = "Uavklart område", NoModule = "Uten modultilknytning", NoType = "Ikke akseptansescenarioer",
        NoSpecification = "Uten spesifikasjon";

    /// <summary>V12 (2026-11-27, approved): the groupings Finn og forstå offers — Brukerhistorie, Spesifikasjon, Modul, Ingen gruppering. The
    /// rail, the overview and the list follow this one choice.</summary>
    public static readonly ScenarioGroupAxis[] PrimaryChoices =
        [ScenarioGroupAxis.Story, ScenarioGroupAxis.Specification, ScenarioGroupAxis.Module, ScenarioGroupAxis.None];

    /// <summary>Further breakdowns that stay available (as a second level, or under «Andre inndelinger»): they classify scenarios, they do not
    /// organise them, so they are not among the approved groupings.</summary>
    public static readonly ScenarioGroupAxis[] OtherChoices =
        [ScenarioGroupAxis.TestLevel, ScenarioGroupAxis.ScenarioType, ScenarioGroupAxis.Area, ScenarioGroupAxis.Method];

    public static readonly ScenarioGroupAxis[] Choices =
        [ScenarioGroupAxis.Story, ScenarioGroupAxis.TestLevel, ScenarioGroupAxis.ScenarioType, ScenarioGroupAxis.Area, ScenarioGroupAxis.Module, ScenarioGroupAxis.Method, ScenarioGroupAxis.None];

    public static string Label(ScenarioGroupAxis a) => a switch
    {
        ScenarioGroupAxis.Story => "Brukerhistorie",
        ScenarioGroupAxis.TestLevel => "Testnivå",
        ScenarioGroupAxis.ScenarioType => "Scenariotype",
        ScenarioGroupAxis.Area => "Funksjonsområde",
        ScenarioGroupAxis.Module => "Modul",
        ScenarioGroupAxis.Method => "Testmetode",
        ScenarioGroupAxis.Specification => "Spesifikasjon",
        _ => "Ingen gruppering",
    };

    public static string LevelLabel(ScenarioTestLevel l) => l switch
    {
        ScenarioTestLevel.EndToEnd => "Ende-til-ende-tester",
        ScenarioTestLevel.Integration => "Integrasjonstester",
        ScenarioTestLevel.Component => "Komponenttester",
        _ => "Uavklart testnivå",
    };

    public static string TypeLabel(ScenarioRequirementType t) => t switch
    {
        ScenarioRequirementType.Functional => "Funksjonelle",
        ScenarioRequirementType.NonFunctional => "Ikke-funksjonelle",
        ScenarioRequirementType.Mixed => "Blandede",
        _ => "Uavklarte",
    };

    /// <summary>«3 funksjonelle», «1 ikke-funksjonell», «2 blandede», «1 uavklart» — a count of acceptance scenarios of one type.</summary>
    public static string TypeCount(ScenarioRequirementType t, int n) => $"{n} " + (t, n == 1) switch
    {
        (ScenarioRequirementType.Functional, true) => "funksjonell",
        (ScenarioRequirementType.Functional, false) => "funksjonelle",
        (ScenarioRequirementType.NonFunctional, true) => "ikke-funksjonell",
        (ScenarioRequirementType.NonFunctional, false) => "ikke-funksjonelle",
        (ScenarioRequirementType.Mixed, true) => "blandet",
        (ScenarioRequirementType.Mixed, false) => "blandede",
        (_, true) => "uavklart",
        _ => "uavklarte",
    };

    public static int TypeRank(ScenarioRequirementType? t) => t is { } x ? Array.IndexOf(ScenarioTypeClassifier.Order, x) : 50;

    /// <summary>Whether a plan can use this grouping as its suites: test method is a decision about a scenario, not a fact a case carries.</summary>
    public static bool UsableForPlan(ScenarioGroupAxis primary, ScenarioGroupAxis secondary) =>
        primary is not (ScenarioGroupAxis.None or ScenarioGroupAxis.Method or ScenarioGroupAxis.Specification)
        && secondary is not (ScenarioGroupAxis.Method or ScenarioGroupAxis.Specification) && secondary != primary;

    public static string? StructureKey(ScenarioGroupAxis primary, ScenarioGroupAxis secondary) =>
        UsableForPlan(primary, secondary) ? Prefix + primary + (secondary == ScenarioGroupAxis.None ? "" : ">" + secondary) : null;

    public static bool TryParse(string? key, out ScenarioGroupAxis primary, out ScenarioGroupAxis secondary)
    {
        primary = secondary = ScenarioGroupAxis.None;
        if (key is null || !key.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var parts = key[Prefix.Length..].Split('>');
        if (parts.Length > 2 || !Enum.TryParse(parts[0], out primary) || !Enum.IsDefined(primary)) return false;
        if (parts.Length == 2 && (!Enum.TryParse(parts[1], out secondary) || !Enum.IsDefined(secondary))) return false;
        return UsableForPlan(primary, secondary);
    }

    public static string Describe(ScenarioGroupAxis primary, ScenarioGroupAxis secondary) =>
        secondary == ScenarioGroupAxis.None ? Label(primary) : $"{Label(primary)} → {Label(secondary)}";

    /// <summary>A case's group on one axis, from the structure facts (the same values the list shows for its scenario).</summary>
    public static (string Label, int Rank, ScenarioTestLevel? Level) ValueOf(PlanStructureFacts f, ScenarioGroupAxis axis) => axis switch
    {
        ScenarioGroupAxis.TestLevel => (LevelLabel(f.Level), Array.IndexOf(TestLevelClassifier.Order, f.Level), f.Level),
        ScenarioGroupAxis.ScenarioType => (f.Type is { } t ? TypeLabel(t) : NoType, TypeRank(f.Type), null),
        ScenarioGroupAxis.Story => (f.Story ?? NoStory, f.Story is null ? 99 : 0, null),
        ScenarioGroupAxis.Area => (f.Area ?? NoArea, f.Area is null ? 99 : 0, null),
        ScenarioGroupAxis.Module => (f.Module ?? NoModule, f.Module is null ? 99 : 0, null),
        _ => ("Alle", 0, null),
    };
}

/// <summary>The one definition of a plan's scenario occurrences (2026-11-27, FUN-015): execution, Testplaner and Planlegg count the same thing.</summary>
public static class PlanOccurrences
{
    /// <summary>A plan's scenario occurrences: each scenario once (its cases grouped under it), each scenario-only item, and each hand-written case
    /// without a scenario on its own. Excluded and superseded cases are not executed.</summary>
    public static List<ScenarioOccurrence> Of(TestPlanDraft plan)
    {
        var result = new List<ScenarioOccurrence>();
        string? Path(string? suite) => suite is not null && plan.Suites.Any(s => s.Id == suite) ? PlanStructure.PathOf(plan.Suites, suite) : null;
        // FUN-011 (2026-11-27): a scenario's membership is its planned item when it has one; its cases may sit in another suite (written before
        // the fix, or grouped by hand) and never move the scenario. Only a scenario without a planned item takes its first case's suite.
        var plannedItem = (plan.PlannedScenarios ?? []).GroupBy(p => p.ScenarioId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var plannedSuite = plannedItem.ToDictionary(kv => kv.Key, kv => kv.Value.SuiteId, StringComparer.Ordinal);
        foreach (var g in plan.Cases.Where(c => c.Review is not (TestCaseReviewState.Superseded or TestCaseReviewState.Excluded))
                     .GroupBy(c => c.ParentScenario is { } sid ? "scenario:" + sid : "case:" + c.Source.ScenarioId, StringComparer.Ordinal))
        {
            var first = g.First();
            var suiteId = first.ParentScenario is { } psid && plannedSuite.TryGetValue(psid, out var ps) && plan.Suites.Any(s => s.Id == ps) ? ps : first.SuiteId;
            result.Add(new ScenarioOccurrence
            {
                Key = g.Key, ScenarioId = first.ParentScenario, Title = first.Source.Kind == ScenarioSourceKind.Manual && first.ParentScenario is null ? first.EffectiveTitle : first.Generated.Title,
                StoryId = first.Generated.Story?.StoryId ?? (first.ParentScenario is { } ps2 && plannedItem.TryGetValue(ps2, out var pi) ? pi.StoryId : null),
                NumberInStory = first.Generated.NumberInStory ?? (first.ParentScenario is { } ps3 && plannedItem.TryGetValue(ps3, out var pj) ? pj.NumberInStory : null), ExpectedBehaviour = first.Generated.ExpectedResult ?? first.EffectiveExpected,
                SuiteId = suiteId, SuitePath = Path(suiteId), CaseIds = [.. g.Select(c => c.Source.ScenarioId)],
            });
        }
        foreach (var item in plan.PlannedScenarios ?? [])
        {
            if (result.Any(o => o.Key == "scenario:" + item.ScenarioId)) continue;
            result.Add(new ScenarioOccurrence
            {
                Key = "scenario:" + item.ScenarioId, ScenarioId = item.ScenarioId, Title = item.Title, StoryId = item.StoryId, NumberInStory = item.NumberInStory,
                ExpectedBehaviour = item.ExpectedBehaviour, SuiteId = item.SuiteId, SuitePath = Path(item.SuiteId), ScenarioOnly = true,
            });
        }
        return result;
    }
}
