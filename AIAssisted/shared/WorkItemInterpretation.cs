using System.Text.RegularExpressions;
using BirkNext.DeliveryReporting;

namespace BirkNext.Interpretation;

// The canonical work-item interpretation engine: one rule set relating a Spec-Kit task to its feature's requirement model. Delivery (reports,
// Overview, Stories) and Traceability both consume it; neither re-implements the rules. Pure and deterministic: the same inputs and rule set always
// give the same result, so a frozen report records the RuleSetVersion it was interpreted with and is never reclassified retroactively.

/// <summary>A story or scenario as the specification defines it (structure only, never inferred from titles).</summary>
public sealed record InterpretedStory(string Key, string Title, bool IsScenario, bool Removed);

/// <summary>The feature-level inputs the rules need: its defined stories/scenarios and whether spec.md exists.</summary>
public sealed record InterpretedFeature(IReadOnlyList<InterpretedStory> Stories, bool HasSpec);

/// <summary>A task as written: its raw [USn] tag (kept even when it names nothing) and the title of the tasks.md phase it sits in.</summary>
public sealed record InterpretedTask(string? StoryReference, string? PhaseTitle);

/// <summary>
/// The result of one rule. <see cref="RuleId"/> names the rule that fired, so every relation is explainable as rule + evidence
/// (<see cref="Note"/>); <see cref="LinkedKey"/> is set only when evidence links a defined story or scenario.
/// </summary>
public sealed record WorkItemInterpretation(
    DeliveryTaskRelationship Relationship, string? GroupKey, DeliveryMappingConfidence Confidence, string Note, string? LinkedKey, string RuleId)
{
    /// <summary>Only removed and unresolved references can be resolved by a person; shared, functional and scenario groups are classifications.</summary>
    public bool NeedsReview => Relationship is DeliveryTaskRelationship.RemovedStoryReference or DeliveryTaskRelationship.UnresolvedReference;
}

/// <summary>
/// Rule order (documented, deterministic — first match wins):
/// <list type="number">
/// <item>WI-SHARED: no tag, not in a story phase → shared &amp; foundational work (setup, foundational, polish) — not a defect.</item>
/// <item>WI-PHASE: no tag, in a phase titled for a story → story phase (linked by the phase title when the story is defined).</item>
/// <item>WI-MALFORMED: a tag that is not one story reference (ranges, lists) → unresolved reference.</item>
/// <item>WI-REMOVED: [USn] naming a story the spec marks removed → removed-story reference (needs review).</item>
/// <item>WI-EXPLICIT: [USn] naming a defined user story → explicit story.</item>
/// <item>WI-FUNCTIONAL: [USn] in a feature without stories or scenarios → functional group (never mapped to "US-1.1"-style ids by number).</item>
/// <item>WI-SCENARIO: [USn] in a scenario-based spec → scenario group, linked to a scenario only when the phase title's own name appears in that
/// scenario's title (equal numbers are never evidence).</item>
/// <item>WI-UNRESOLVED: anything else ([US6] when the spec defines US1–US5) → unresolved reference (needs review), never re-pointed.</item>
/// </list>
/// </summary>
public static partial class WorkItemInterpreter
{
    /// <summary>Bump when a rule's outcome changes; reports frozen with an older version keep their own interpretation.</summary>
    public const string RuleSetVersion = "work-items/1";

    public const string SharedRule = "WI-SHARED", PhaseRule = "WI-PHASE", MalformedRule = "WI-MALFORMED", RemovedRule = "WI-REMOVED",
        ExplicitRule = "WI-EXPLICIT", FunctionalRule = "WI-FUNCTIONAL", ScenarioRule = "WI-SCENARIO", UnresolvedRule = "WI-UNRESOLVED";

    public const string SharedGroupKey = "shared";

    [GeneratedRegex(@"\b(?:User\s+Story|Brukerhistorie)\s+(\d+)\b|\bUS\s*-?\s*(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PhaseStoryRe();
    [GeneratedRegex(@"\b(?:User\s+Story|Brukerhistorie|US)\s*-?\s*\d+\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PhaseNameRe();
    [GeneratedRegex(@"\((?:Priority|Prioritet)\s*:[^)]*\).*$", RegexOptions.IgnoreCase)]
    private static partial Regex PriorityRe();
    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NonWordRe();
    [GeneratedRegex(@"^US\s*-?\s*0*(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex StoryTagRe();

    /// <summary>"US3" for a single story tag ("US3", "US-03", "us 3"); null for ranges, lists and anything else.</summary>
    public static string? StoryKey(string? tag) => tag is not null && StoryTagRe().Match(tag.Trim()) is { Success: true } m ? $"US{int.Parse(m.Groups[1].Value)}" : null;

    /// <summary>"US2" when the phase title names a story ("Phase 4: User Story 2 — …", "US2"); null otherwise.</summary>
    public static string? PhaseStoryKey(string? phaseTitle) =>
        phaseTitle is not null && PhaseStoryRe().Match(phaseTitle) is { Success: true } pm ? $"US{int.Parse(pm.Groups[1].Success ? pm.Groups[1].Value : pm.Groups[2].Value)}" : null;

    public static WorkItemInterpretation Classify(InterpretedTask task, InterpretedFeature feature)
    {
        var userStories = feature.Stories.Where(s => !s.IsScenario).ToList();
        var scenarios = feature.Stories.Where(s => s.IsScenario).ToList();
        var phase = task.PhaseTitle;
        var phaseStory = PhaseStoryKey(phase);

        if (task.StoryReference is not { Length: > 0 } raw)
        {
            if (phaseStory is null)
                return new(DeliveryTaskRelationship.SharedFoundational, SharedGroupKey, DeliveryMappingConfidence.None,
                    phase is null ? "No story tag and not in a story phase: shared or foundational work of the feature, not owned by one story."
                        : $"No story tag, in phase \"{phase}\": shared or foundational work, not owned by one story.", null, SharedRule);
            var story = userStories.FirstOrDefault(s => s.Key == phaseStory && !s.Removed);
            return new(DeliveryTaskRelationship.StoryPhase, phaseStory, story is null ? DeliveryMappingConfidence.GroupLevel : DeliveryMappingConfidence.PhaseTitle,
                $"No [{phaseStory}] tag; the task sits in phase \"{phase}\"{(story is null ? ", which names a story the specification does not define" : $" of {phaseStory}")}.", story?.Key, PhaseRule);
        }

        var key = StoryKey(raw);
        if (key is null)
            return new(DeliveryTaskRelationship.UnresolvedReference, null, DeliveryMappingConfidence.None, $"[{raw}] is not a single story reference; it cannot be linked.", null, MalformedRule);
        var named = userStories.FirstOrDefault(s => s.Key == key);
        if (named is { Removed: true })
            return new(DeliveryTaskRelationship.RemovedStoryReference, key, DeliveryMappingConfidence.Explicit,
                $"[{raw}] references {key} \"{named.Title}\", which the specification marks removed. Review whether these tasks still belong to the scope.", key, RemovedRule);
        if (named is not null)
            return new(DeliveryTaskRelationship.ExplicitStory, key, DeliveryMappingConfidence.Explicit, $"[{raw}] references {key} in spec.md.", key, ExplicitRule);
        if (userStories.Count == 0 && scenarios.Count == 0)
            return new(DeliveryTaskRelationship.FunctionalGroup, key, DeliveryMappingConfidence.GroupLevel,
                $"Derived from [{raw}] references in tasks.md. {(feature.HasSpec ? "spec.md defines no user stories" : "The feature has no spec.md")}, so this is a functional group, " +
                "not a formally defined User Story; no one-to-one mapping to detailed requirements is assumed.", null, FunctionalRule);
        if (userStories.Count == 0)
        {
            var scenario = ScenarioByPhaseName(phase, scenarios);
            return scenario is not null
                ? new(DeliveryTaskRelationship.ScenarioGroup, key, DeliveryMappingConfidence.PhaseTitle,
                    $"spec.md is written as scenarios. The phase \"{phase}\" is named in {ScenarioLabel(scenario)}, so the [{raw}] group is linked to it.", scenario.Key, ScenarioRule)
                : new(DeliveryTaskRelationship.ScenarioGroup, key, DeliveryMappingConfidence.GroupLevel,
                    $"spec.md is written as scenarios, not user stories. [{raw}] groups these tasks; no scenario is linked because no phase or title evidence names one (equal numbers are not evidence).", null, ScenarioRule);
        }
        var defined = userStories.Where(s => !s.Removed).Select(s => s.Key).ToList();
        return new(DeliveryTaskRelationship.UnresolvedReference, key, DeliveryMappingConfidence.None,
            $"[{raw}] names no story in spec.md ({(defined.Count == 0 ? "it defines none" : "it defines " + string.Join(", ", defined))}). The reference is shown as written, never re-pointed.", null, UnresolvedRule);
    }

    /// <summary>"Scenario 1" for a scenario key S1.</summary>
    public static string ScenarioLabel(InterpretedStory s) => $"Scenario {s.Key.TrimStart('S')} \"{s.Title}\"";

    /// <summary>The badge name of a relationship — the same words in every consumer.</summary>
    public static string Label(DeliveryTaskRelationship kind) => kind switch
    {
        DeliveryTaskRelationship.ExplicitStory => "User Story",
        DeliveryTaskRelationship.StoryPhase => "Story phase",
        DeliveryTaskRelationship.FunctionalGroup => "Functional group",
        DeliveryTaskRelationship.ScenarioGroup => "Scenario group",
        DeliveryTaskRelationship.SharedFoundational => "Shared & Foundational",
        DeliveryTaskRelationship.RemovedStoryReference => "Removed story reference",
        _ => "Unresolved reference",
    };

    /// <summary>Why a group of this kind exists — the tooltip/info text of its badge.</summary>
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

    /// <summary>The single scenario whose title contains the phase's own name ("Hendelsestidslinje"); null when none or several do.</summary>
    private static InterpretedStory? ScenarioByPhaseName(string? phase, List<InterpretedStory> scenarios)
    {
        if (phase is null || PhaseNameRe().Match(PriorityRe().Replace(phase, "")) is not { Success: true } m) return null;
        var name = Words(m.Groups[1].Value);
        if (name.Length < 5) return null;
        var hits = scenarios.Where(s => Words(s.Title).Contains(name, StringComparison.Ordinal)).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>Lower-case letters and digits separated by single spaces (dashes, mojibake punctuation and emoji dropped).</summary>
    private static string Words(string text) => NonWordRe().Replace(text.Replace("â€”", " ").Replace("â€“", " ").ToLowerInvariant(), " ").Trim();
}

/// <summary>
/// How a specification names its stories (ingest rules shared by Delivery's snapshot parser and Traceability): "User Story N" / "Brukerhistorie N"
/// headings, "Scenario N" headings, and the removed markers. Removed = structure, never a word in the title ("User Deactivated When Removed from
/// Entra" is not removed): a struck-through story heading, or a separate trailing marker after the title ("— Removed 2026-10-27", "(Removed)", "[Fjernet]").
/// </summary>
public static class SpecStoryHeadings
{
    public static readonly Regex StoryHeadingRe = new(@"^\s*(?:User\s+Story|Brukerhistorie)\s+(\d+)\s*(?:[-–—:]\s*)?(.*?)\s*(?:\(\s*(?:Priority|Prioritet)\s*:\s*([A-Za-z0-9]+)\s*\))?\s*(?:🎯.*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static readonly Regex ScenarioHeadingRe = new(@"^\s*Scenario\s+(\d+)\s*(?:[-–—:]\s*)?(.*?)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static readonly Regex RemovedStrikeRe = new(@"^#{2,6}\s+~~\s*(?:User\s+Story|Brukerhistorie)\s+(\d+)\b\s*(?:[-–—:]\s*)?(.*?)~~", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static readonly Regex RemovedMarkerRe = new(@"^#{2,6}\s+(?:User\s+Story|Brukerhistorie)\s+(\d+)\b\s*(?:[-–—:]\s*)?(.*?)(?:\s+[-–—]\s*|\s*[\[(])(?:Removed|Fjernet|Withdrawn|Utgått)\b[^\])]*[\])]?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The feature's interpretation inputs from its spec.md user-story headings (already stripped of Markdown, with the parser's explicit item id when
    /// it has one) and raw content (for removed markers): the same keys Delivery stores (US n, S n), so both consumers classify a task identically.
    /// </summary>
    public static InterpretedFeature Feature(IEnumerable<(string Heading, string? ItemId)> storyHeadings, string? specContent)
    {
        var stories = new List<InterpretedStory>();
        foreach (var (raw, itemId) in storyHeadings)
        {
            var heading = Regex.Replace(Regex.Replace(Regex.Replace(raw, @"\[([^\]]*)\]\([^)]*\)", "$1"), @"(\*\*|__|`|\*|_)", ""), @"\s+", " ").Trim();
            if (StoryHeadingRe.Match(heading) is { Success: true } m) Add($"US{int.Parse(m.Groups[1].Value)}", m.Groups[2].Value.Trim(), false);
            else if (ScenarioHeadingRe.Match(heading) is { Success: true } sm) Add($"S{int.Parse(sm.Groups[1].Value)}", sm.Groups[2].Value.Trim(), true);
            else if (WorkItemInterpreter.StoryKey(itemId) is { } idKey) Add(idKey, heading, false);
        }
        foreach (var line in (specContent ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var m = RemovedStrikeRe.Match(line) is { Success: true } strike ? strike : RemovedMarkerRe.Match(line);
            if (!m.Success) continue;
            var key = $"US{int.Parse(m.Groups[1].Value)}";
            var index = stories.FindIndex(s => s.Key == key);
            if (index >= 0) stories[index] = stories[index] with { Removed = true };
            else stories.Add(new InterpretedStory(key, m.Groups[2].Value.Trim() is { Length: > 0 } t ? t : key, false, true));
        }
        return new InterpretedFeature(stories, specContent is not null);

        void Add(string key, string title, bool scenario)
        {
            if (stories.All(s => s.Key != key)) stories.Add(new InterpretedStory(key, title.Length == 0 ? key : title, scenario, false));
        }
    }
}
