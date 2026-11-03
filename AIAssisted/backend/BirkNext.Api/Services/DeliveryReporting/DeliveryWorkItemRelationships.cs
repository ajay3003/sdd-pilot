using System.Text.RegularExpressions;
using BirkNext.DeliveryReporting;

namespace BirkNext.Api.Services.DeliveryReporting;

/// <summary>A task's relationship to its feature's requirement model, with the group it belongs to and why.</summary>
public sealed record DeliveryTaskClassification(DeliveryTaskRelationship Relationship, string? GroupKey, DeliveryMappingConfidence Confidence, string Note, string? LinkedKey);

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
    public const string ScenarioKind = "Scenario", UserStoryKind = "UserStory", SharedGroupKey = "shared";

    [GeneratedRegex(@"\b(?:User\s+Story|Brukerhistorie)\s+(\d+)\b|\bUS\s*-?\s*(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PhaseStoryRe();
    [GeneratedRegex(@"\b(?:User\s+Story|Brukerhistorie|US)\s*-?\s*\d+\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PhaseNameRe();
    [GeneratedRegex(@"\((?:Priority|Prioritet)\s*:[^)]*\).*$", RegexOptions.IgnoreCase)]
    private static partial Regex PriorityRe();
    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NonWordRe();

    public static bool IsScenario(DeliveryContentStory s) => string.Equals(s.Kind, ScenarioKind, StringComparison.Ordinal);

    public static DeliveryTaskClassification Classify(DeliveryContentTask task, DeliveryContentFeature feature)
    {
        var userStories = feature.Stories.Where(s => !IsScenario(s)).ToList();
        var scenarios = feature.Stories.Where(IsScenario).ToList();
        var phase = task.PhaseKey is null ? null : feature.Phases.FirstOrDefault(p => p.Key == task.PhaseKey)?.Title;
        var phaseStory = phase is not null && PhaseStoryRe().Match(phase) is { Success: true } pm ? $"US{int.Parse(pm.Groups[1].Success ? pm.Groups[1].Value : pm.Groups[2].Value)}" : null;

        if (task.StoryReference is not { Length: > 0 } raw)
        {
            if (phaseStory is null)
                return new(DeliveryTaskRelationship.SharedFoundational, SharedGroupKey, DeliveryMappingConfidence.None,
                    phase is null ? "No story tag and not in a story phase: shared or foundational work of the feature, not owned by one story."
                        : $"No story tag, in phase \"{phase}\": shared or foundational work, not owned by one story.", null);
            var story = userStories.FirstOrDefault(s => s.Key == phaseStory && !s.Removed);
            return new(DeliveryTaskRelationship.StoryPhase, phaseStory, story is null ? DeliveryMappingConfidence.GroupLevel : DeliveryMappingConfidence.PhaseTitle,
                $"No [{phaseStory}] tag; the task sits in phase \"{phase}\"{(story is null ? ", which names a story the specification does not define" : $" of {phaseStory}")}.", story?.Key);
        }

        var key = DeliverySnapshotParser.StoryKey(raw);
        if (key is null)
            return new(DeliveryTaskRelationship.UnresolvedReference, null, DeliveryMappingConfidence.None, $"[{raw}] is not a single story reference; it cannot be linked.", null);
        var named = feature.Stories.FirstOrDefault(s => s.Key == key && !IsScenario(s));
        if (named is { Removed: true })
            return new(DeliveryTaskRelationship.RemovedStoryReference, key, DeliveryMappingConfidence.Explicit,
                $"[{raw}] references {key} \"{named.Title}\", which the specification marks removed. Review whether these tasks still belong to the scope.", key);
        if (named is not null)
            return new(DeliveryTaskRelationship.ExplicitStory, key, DeliveryMappingConfidence.Explicit, $"[{raw}] references {key} in spec.md.", key);
        if (userStories.Count == 0 && scenarios.Count == 0)
            return new(DeliveryTaskRelationship.FunctionalGroup, key, DeliveryMappingConfidence.GroupLevel,
                $"Derived from [{raw}] references in tasks.md. {(feature.SpecPath is null ? "The feature has no spec.md" : "spec.md defines no user stories")}, so this is a functional group, " +
                "not a formally defined User Story; no one-to-one mapping to detailed requirements is assumed.", null);
        if (userStories.Count == 0)
        {
            var scenario = ScenarioByPhaseName(phase, scenarios);
            return scenario is not null
                ? new(DeliveryTaskRelationship.ScenarioGroup, key, DeliveryMappingConfidence.PhaseTitle,
                    $"spec.md is written as scenarios. The phase \"{phase}\" is named in {ScenarioLabel(scenario)}, so the [{raw}] group is linked to it.", scenario.Key)
                : new(DeliveryTaskRelationship.ScenarioGroup, key, DeliveryMappingConfidence.GroupLevel,
                    $"spec.md is written as scenarios, not user stories. [{raw}] groups these tasks; no scenario is linked because no phase or title evidence names one (equal numbers are not evidence).", null);
        }
        var defined = userStories.Where(s => !s.Removed).Select(s => s.Key).ToList();
        return new(DeliveryTaskRelationship.UnresolvedReference, key, DeliveryMappingConfidence.None,
            $"[{raw}] names no story in spec.md ({(defined.Count == 0 ? "it defines none" : "it defines " + string.Join(", ", defined))}). The reference is shown as written, never re-pointed.", null);
    }

    /// <summary>"Scenario 1" for a scenario key S1.</summary>
    public static string ScenarioLabel(DeliveryContentStory s) => $"Scenario {s.Key.TrimStart('S')} \"{s.Title}\"";

    /// <summary>The single scenario whose title contains the phase's own name ("Hendelsestidslinje"); null when none or several do.</summary>
    private static DeliveryContentStory? ScenarioByPhaseName(string? phase, List<DeliveryContentStory> scenarios)
    {
        if (phase is null || PhaseNameRe().Match(PriorityRe().Replace(phase, "")) is not { Success: true } m) return null;
        var name = Words(m.Groups[1].Value);
        if (name.Length < 5) return null;
        var hits = scenarios.Where(s => Words(s.Title).Contains(name, StringComparison.Ordinal)).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>Lower-case letters and digits separated by single spaces (dashes, mojibake punctuation and emoji dropped).</summary>
    private static string Words(string text) => NonWordRe().Replace(text.Replace("â€”", " ").Replace("â€“", " ").ToLowerInvariant(), " ").Trim();

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
    public static string Explanation(DeliveryTaskRelationship kind) => kind switch
    {
        DeliveryTaskRelationship.ExplicitStory => "A User Story defined in spec.md; its tasks reference it explicitly with [USn].",
        DeliveryTaskRelationship.StoryPhase => "Tasks without a story tag inside a tasks.md phase titled for a story. Linked by the phase title, not by a tag.",
        DeliveryTaskRelationship.FunctionalGroup => "Derived from stable [USn] references in tasks.md. There is no matching User Story in spec.md; tasks are grouped without claiming a one-to-one story mapping.",
        DeliveryTaskRelationship.ScenarioGroup => "The specification is written as scenarios instead of user stories. Tasks are grouped by their [USn] tag; a scenario is linked only when the phase title names it.",
        DeliveryTaskRelationship.SharedFoundational => "Setup, foundational, polish and cross-cutting work that is not owned by one story. Normal for Spec-Kit task lists — not a traceability defect.",
        DeliveryTaskRelationship.RemovedStoryReference => "Tasks still reference a story the specification marks removed. Review whether they belong to the scope.",
        _ => "Tasks reference a story the specification does not define. The reference is shown as written and never re-pointed.",
    };

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
