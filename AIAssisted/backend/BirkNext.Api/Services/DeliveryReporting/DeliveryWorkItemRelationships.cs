using System.Text.RegularExpressions;
using BirkNext.DeliveryReporting;
using BirkNext.Interpretation;

namespace BirkNext.Api.Services.DeliveryReporting;

/// <summary>A task's relationship to its feature's requirement model, with the group it belongs to and why.</summary>
public sealed record DeliveryTaskClassification(DeliveryTaskRelationship Relationship, string? GroupKey, DeliveryMappingConfidence Confidence, string Note, string? LinkedKey)
{
    /// <summary>The shared interpretation rule that produced it (WI-EXPLICIT, WI-SHARED, …).</summary>
    public string? RuleId { get; init; }
}

/// <summary>
/// The one rule that relates a task to the requirement model of its feature (report, Overview, Stories and exports all read its result). Pure and
/// deterministic, applied to stored snapshot content at comparison time, so snapshots imported before it are classified the same way.
/// <list type="bullet">
/// <item>[USn] naming a spec.md User Story → explicit story; naming a story marked removed → removed-story reference (needs review).</item>
/// <item>[USn] in a feature without spec.md user stories → functional group derived from tasks.md (never mapped to "US-1.1"-style ids by number).</item>
/// <item>[USn] in a scenario-based spec → scenario group, linked to a scenario only when the phase title's own name appears in that scenario's title
/// (equal numbers are never evidence).</item>
/// <item>No tag: in a phase titled for a story → story phase; otherwise shared &amp; foundational work (setup, foundational, polish) — not a defect.</item>
/// <item>Anything else ([US6] when the spec defines US1–US5, ranges) → unresolved reference (needs review).</item>
/// </list>
/// </summary>
public static partial class DeliveryWorkItemRelationships
{
    public const string ScenarioKind = "Scenario", UserStoryKind = "UserStory", SharedGroupKey = WorkItemInterpreter.SharedGroupKey;

    public static bool IsScenario(DeliveryContentStory s) => string.Equals(s.Kind, ScenarioKind, StringComparison.Ordinal);

    /// <summary>Delivery's adapter onto the shared <see cref="WorkItemInterpreter"/>: the stored snapshot content becomes the engine's typed inputs.</summary>
    public static DeliveryTaskClassification Classify(DeliveryContentTask task, DeliveryContentFeature feature)
    {
        var phase = task.PhaseKey is null ? null : feature.Phases.FirstOrDefault(p => p.Key == task.PhaseKey)?.Title;
        var k = WorkItemInterpreter.Classify(new InterpretedTask(task.StoryReference, phase), Interpreted(feature));
        return new(k.Relationship, k.GroupKey, k.Confidence, k.Note, k.LinkedKey) { RuleId = k.RuleId };
    }

    public static InterpretedFeature Interpreted(DeliveryContentFeature feature) =>
        new([.. feature.Stories.Select(s => new InterpretedStory(s.Key, s.Title, IsScenario(s), s.Removed))], feature.SpecPath is not null);

    /// <summary>"Scenario 1" for a scenario key S1.</summary>
    public static string ScenarioLabel(DeliveryContentStory s) => WorkItemInterpreter.ScenarioLabel(new InterpretedStory(s.Key, s.Title, true, s.Removed));

    /// <summary>The requirement model a feature is written in: what the Stories column and Overview say instead of "0 user stories".</summary>
    public static string RequirementModel(DeliveryContentFeature f)
    {
        if (f.Stories.Any(s => !IsScenario(s))) return "User stories";
        if (f.Stories.Any(IsScenario)) return "Scenarios";
        if (f.Tasks.Any(t => DeliverySnapshotParser.StoryKey(t.StoryReference) is not null)) return "Functional groups";
        if (f.SpecPath is null) return f.TasksPath is null ? "No specification or task list" : "No specification";
        return f.TasksPath is null ? "No task list" : "Specification without user stories";
    }

    /// <summary>The feature's groups (stories first, then functional/scenario groups, shared work, removed and unresolved references) with their live tasks.</summary>
    public static List<DeliveryWorkGroupRow> Groups(DeliveryContentFeature feature, IReadOnlyList<DeliveryTaskRow> tasks)
    {
        var live = tasks.Where(t => t.Change != DeliveryTaskChange.Removed && t.Relationship is not null).ToList();
        var groups = new List<DeliveryWorkGroupRow>();
        foreach (var g in live.GroupBy(t => (t.Relationship!.Value, t.GroupKey)).OrderBy(g => Order(g.Key.Value)).ThenBy(g => NaturalKey(g.Key.GroupKey)))
        {
            var (kind, key) = g.Key;
            var sample = g.First();
            var linked = sample.MappingConfidence is DeliveryMappingConfidence.Explicit or DeliveryMappingConfidence.PhaseTitle
                ? feature.Stories.FirstOrDefault(s => s.Key == (kind == DeliveryTaskRelationship.ScenarioGroup ? ScenarioLink(g) : key)) : null;
            var title = kind switch
            {
                DeliveryTaskRelationship.ExplicitStory => $"{key} — {feature.Stories.FirstOrDefault(s => s.Key == key)?.Title}",
                DeliveryTaskRelationship.StoryPhase => $"{key} — story phase (untagged tasks)",
                DeliveryTaskRelationship.FunctionalGroup => $"{key} — Functional group",
                DeliveryTaskRelationship.ScenarioGroup => linked is null ? $"{key} — Scenario group (not linked)" : $"{key} — Scenario group · {ScenarioLabel(linked)}",
                DeliveryTaskRelationship.SharedFoundational => "Shared & Foundational Tasks",
                DeliveryTaskRelationship.RemovedStoryReference => $"{key} — References removed story",
                _ => key is null ? "Unresolved references" : $"{key} — Unresolved story reference",
            };
            groups.Add(new DeliveryWorkGroupRow(key ?? kind.ToString(), kind, title)
            {
                Explanation = Explanation(kind),
                Provenance = kind switch
                {
                    DeliveryTaskRelationship.ExplicitStory => $"spec.md User Story {key?.TrimStart('U', 'S')} + [{key}] tags in tasks.md",
                    DeliveryTaskRelationship.SharedFoundational or DeliveryTaskRelationship.StoryPhase => "tasks.md phases",
                    _ => $"[{key}] tags in tasks.md",
                },
                Confidence = sample.MappingConfidence ?? DeliveryMappingConfidence.None,
                LinkedKey = linked?.Key,
                TaskTotal = g.Count(), TaskCompleted = g.Count(t => t.Completed),
                NeedsReview = kind is DeliveryTaskRelationship.RemovedStoryReference or DeliveryTaskRelationship.UnresolvedReference,
            });
        }
        return groups;

        static string? ScenarioLink(IGrouping<(DeliveryTaskRelationship, string?), DeliveryTaskRow> g) =>
            g.Select(t => t.RelationshipNote).FirstOrDefault(n => n is not null && n.Contains("is named in", StringComparison.Ordinal)) is { } note
                ? Regex.Match(note, @"Scenario (\d+)") is { Success: true } m ? $"S{m.Groups[1].Value}" : null : null;
    }

    /// <summary>Why BirkNext created a group of this kind — the tooltip/info text of its badge.</summary>
    public static string Explanation(DeliveryTaskRelationship kind) => WorkItemInterpreter.Explanation(kind);

    private static int Order(DeliveryTaskRelationship kind) => kind switch
    {
        DeliveryTaskRelationship.ExplicitStory or DeliveryTaskRelationship.StoryPhase => 0,
        DeliveryTaskRelationship.FunctionalGroup or DeliveryTaskRelationship.ScenarioGroup => 1,
        DeliveryTaskRelationship.SharedFoundational => 2,
        DeliveryTaskRelationship.RemovedStoryReference => 3,
        _ => 4,
    };

    private static string NaturalKey(string? key) => key is null ? "" : Regex.Replace(key, @"\d+", m => m.Value.PadLeft(6, '0'));
}
