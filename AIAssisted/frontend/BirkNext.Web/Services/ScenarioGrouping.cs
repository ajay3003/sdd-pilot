using BirkNext.TestPlanning;
using S = BirkNext.Web.Services.ScenarioBrowserPresentation;

namespace BirkNext.Web.Services;

/// <summary>
/// Scenario grouping in Finn og forstå (2026-11-26): a presentation of the scenarios that are ALREADY scoped, searched and filtered, organised by
/// one or two independent axes (<see cref="ScenarioGroupAxis"/>). Each scenario lands in exactly one group per level (one value per axis), so a
/// tree never shows a scenario twice and group counts are unique scenario ids. Nothing here classifies: the values are the scenario's own test
/// level, type (with reviewers' decisions), story, functional area, module and test method; an unknown value gets its own honest group.
/// Pure and deterministic; group keys include the axis, so a changed grouping never reuses unrelated expansion state.
/// </summary>
public static class ScenarioGrouping
{
    public sealed record Value(string Key, string Label, int Rank);

    public sealed record Group(string Path, ScenarioGroupAxis Axis, string Label, IReadOnlyList<S.Item> Items, IReadOnlyList<Group> Children, int Depth)
    {
        /// <summary>E2E candidates (a documented flow proposes the level; not reviewed) — shown on test-level groups so a candidate is never mistaken for a confirmed E2E test.</summary>
        public int Candidates => Axis == ScenarioGroupAxis.TestLevel ? Items.Count(i => i.Level.Candidate) : 0;
    }

    /// <summary>The scenario's group on one axis.</summary>
    public static Value ValueOf(S.Item i, ScenarioGroupAxis axis)
    {
        switch (axis)
        {
            case ScenarioGroupAxis.TestLevel:
                var level = i.Level.Level;
                return new(level.ToString(), ScenarioGroupAxes.LevelLabel(level), Array.IndexOf(TestLevelClassifier.Order, level));
            case ScenarioGroupAxis.ScenarioType:
                return i.TypeResult is { } t
                    ? new(t.Type.ToString(), ScenarioGroupAxes.TypeLabel(t.Type), ScenarioGroupAxes.TypeRank(t.Type))
                    : new(i.TypeKey, S.TypeGroupLabel(i.TypeKey), i.TypeKey == S.RequirementGroup ? 60 : 70);
            case ScenarioGroupAxis.Story:
                // Specification-scoped: «US1» of two specifications are two groups.
                return i.Candidate.Story is { } st
                    ? new($"{st.SpecificationPath}#{st.Key}", $"{st.StoryId} — {st.Title}".Trim(' ', '—'), 0)
                    : new("~none", ScenarioGroupAxes.NoStory, 99);
            case ScenarioGroupAxis.Area:
                return string.IsNullOrWhiteSpace(i.Area) ? new("~none", ScenarioGroupAxes.NoArea, 99) : new(i.Area, i.Area, 0);
            case ScenarioGroupAxis.Module:
                return ModuleOf(i) is { } m ? new(m, m, 0) : new("~none", ScenarioGroupAxes.NoModule, 99);
            case ScenarioGroupAxis.Method:
                // The kind a reviewer chose, else the suggestion; «Uavklart metode» only when the ownership itself is unclassified.
                return i.Owner.NeedsReview && !i.KindDecided ? new("Unclassified", "Uavklart metode", 3)
                    : i.Relevance switch
                    {
                        S.Relevance.Manual => new("Manual", "Manuell", 0),
                        S.Relevance.Technical => new("Technical", "Teknisk", 1),
                        _ => new("Both", "Manuell og teknisk", 2),
                    };
            default:
                return new("all", "Alle", 0);
        }
    }

    /// <summary>The scenario's module: its specification's module (the same rule as the plan structure); null when it has none.</summary>
    public static string? ModuleOf(S.Item i) =>
        i.Candidate.Story?.SpecificationPath is { Length: > 0 } spec ? PlanStructure.ModuleOf(spec)
        : i.Candidate.CapabilityId == "test-specification" && i.Candidate.Area is { Length: > 0 } docModule ? docModule
        : i.Candidate.Evidence.Select(e => e.Reference).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && p!.Contains("/specs/", StringComparison.OrdinalIgnoreCase)) is { } path
            ? PlanStructure.ModuleOf(path) : null;

    /// <summary>The group tree for the shown scenarios (list order is kept inside a group). Secondary = None or equal to primary gives one level.</summary>
    public static IReadOnlyList<Group> Build(IReadOnlyList<S.Item> shown, ScenarioGroupAxis primary, ScenarioGroupAxis secondary)
    {
        if (primary == ScenarioGroupAxis.None) return [];
        var second = secondary == primary ? ScenarioGroupAxis.None : secondary;
        return Level(shown, primary, second, "", 0);
    }

    private static List<Group> Level(IReadOnlyList<S.Item> items, ScenarioGroupAxis axis, ScenarioGroupAxis next, string parentPath, int depth) =>
        [.. items.Select(i => (Item: i, Value: ValueOf(i, axis)))
            .GroupBy(x => x.Value.Key, StringComparer.Ordinal)
            .Select(g => (First: g.First().Value, Items: g.Select(x => x.Item).DistinctBy(x => x.Id).ToList()))
            .OrderBy(g => g.First.Rank).ThenBy(g => StoryOrder(g.First, axis)).ThenBy(g => g.First.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(g =>
            {
                var path = $"{parentPath}/{axis}:{g.First.Key}";
                var children = next == ScenarioGroupAxis.None ? [] : Level(g.Items, next, ScenarioGroupAxis.None, path, depth + 1);
                return new Group(path, axis, g.First.Label, g.Items, children, depth);
            })];

    /// <summary>Stories in their own order (specification, then US number), not alphabetically («US10» after «US9»).</summary>
    private static string StoryOrder(Value v, ScenarioGroupAxis axis)
    {
        if (axis != ScenarioGroupAxis.Story || v.Key == "~none") return "";
        var hash = v.Key.IndexOf('#');
        var spec = hash < 0 ? "" : v.Key[..hash];
        var label = v.Label;
        var digits = new string(label.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return $"{spec}|{(int.TryParse(digits, out var n) ? n : 9999):D5}";
    }

    /// <summary>The default for every tester, project, module and specification (2026-11-26, AC-level classification): Brukerhistorie →
    /// Scenariotype, unless the tester saved another choice. The type groups classify the ACs inside the story, never the story itself.
    /// (The argument is kept for callers; it does not change the default.)</summary>
    public static (ScenarioGroupAxis Primary, ScenarioGroupAxis Secondary) Default(bool specificationSelected = false) => (ScenarioGroupAxis.Story, ScenarioGroupAxis.ScenarioType);

    /// <summary>A story group's computed child summary: how many of its distinct acceptance scenarios have each type (only types present, in the
    /// canonical order). A summary of the ACs — NOT a type of the story; nothing is stored.</summary>
    public static IReadOnlyList<(ScenarioRequirementType Type, int Count)> TypeSummary(IEnumerable<S.Item> items)
    {
        var typed = items.Where(i => i.TypeResult is not null).DistinctBy(i => i.Id).ToList();
        return [.. ScenarioTypeClassifier.Order.Select(t => (t, typed.Count(i => i.TypeResult!.Type == t))).Where(x => x.Item2 > 0)];
    }

    /// <summary>A stored preference «Primary>Secondary»; null when absent or unreadable.</summary>
    public static (ScenarioGroupAxis Primary, ScenarioGroupAxis Secondary)? Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        var parts = stored.Split('>');
        if (!Enum.TryParse<ScenarioGroupAxis>(parts[0], out var p) || !Enum.IsDefined(p)) return null;
        var s = parts.Length > 1 && Enum.TryParse<ScenarioGroupAxis>(parts[1], out var x) && Enum.IsDefined(x) && x != p ? x : ScenarioGroupAxis.None;
        return (p, p == ScenarioGroupAxis.None ? ScenarioGroupAxis.None : s);
    }

    public static string Format(ScenarioGroupAxis primary, ScenarioGroupAxis secondary) => $"{primary}>{secondary}";

    /// <summary>Every group path in the tree (for «Vis alle grupper» / «Skjul alle»).</summary>
    public static IEnumerable<string> Paths(IEnumerable<Group> groups) => groups.SelectMany(g => new[] { g.Path }.Concat(Paths(g.Children)));
}
