using BirkNext.Sdd;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using S = BirkNext.Web.Services.ScenarioBrowserPresentation;

namespace BirkNext.Web.Services;

/// <summary>
/// Finn og forstå's grouping (2026-11-24): user stories under their source functional areas (FR sections), under business groups people made, or
/// flat. Pure: builds the inputs of <see cref="StoryGroupingEngine"/> from what the page already holds (discovery stories, the browser's own story
/// evidence with tasks.md links, scenarios, the saved-case trace, the specification text) and the navigation tree from its profiles. Grouping is
/// navigation only: it never changes a scenario, a requirement link, a plan or a case.
/// </summary>
public static class StoryGroupingPresentation
{
    public enum Mode { Business, Functional, Stories, None }

    public static string ModeLabel(Mode m) => m switch
    {
        Mode.Business => "Forretningsområder", Mode.Functional => "Funksjonelle områder", Mode.Stories => "Brukerhistorier", _ => "Ingen gruppering",
    };

    /// <summary>The default: business groups when people have made some, else functional areas when the specification has FR sections, else stories.</summary>
    public static Mode DefaultMode(StoryGroupingDocument? doc, RequirementSectionMap sections) =>
        doc is { Groups.Count: > 0 } ? Mode.Business : sections.Sections.Count > 0 ? Mode.Functional : Mode.Stories;

    /// <summary>
    /// The stories with everything the engine reads. The discovery story is enriched with the browser's own evidence of the same story (its tasks.md
    /// and plan links); confirmed links come from plans (a story link a tester saved on a plan) and from this document; suggestions come from the
    /// story's own text against the specification's requirements (never grouping evidence on their own).
    /// </summary>
    public static List<StoryGroupingEngine.StoryInputs> Inputs(IReadOnlyList<S.Item> items, CoverageWorkspaceEvidence? evidence, IReadOnlyCollection<ProjectRequirementOption>? catalog,
        IEnumerable<StoryRequirementLink>? planStoryLinks = null)
    {
        var browser = (evidence?.Stories ?? []).ToDictionary(s => s.Key, StringComparer.Ordinal);
        var planLinks = (planStoryLinks ?? []).GroupBy(l => l.StoryKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        return [.. items.Where(i => i.Candidate.Story is not null).GroupBy(i => i.Candidate.Story!.Key, StringComparer.Ordinal).Select(g =>
        {
            var story = g.First().Candidate.Story!;
            if (browser.TryGetValue(story.Key, out var own))
                story = story with
                {
                    NamedRequirements = [.. story.NamedRequirements.Concat(own.NamedRequirements).Distinct(StringComparer.OrdinalIgnoreCase)],
                    TaskRequirements = [.. story.TaskRequirements.Concat(own.TaskRequirements).DistinctBy(t => (t.RequirementId.ToUpperInvariant(), t.TasksPath, t.Line))],
                };
            var scenarioLinks = g.SelectMany(i => i.Candidate.RequirementIds.Select(id => (id, $"{(i.Candidate.NumberInStory is { } n ? $"scenario {n}" : i.Title)}"))).ToList();
            var confirmed = planLinks.TryGetValue(story.Key, out var links) ? links.Select(l => (l.Link.RequirementId, l.Link.LinkedBy ?? "testplan")).ToList() : [];
            var suggestions = catalog is { Count: > 0 }
                ? ScenarioPlanning.SuggestRequirements($"{story.Title}\n{story.Description}\n{story.IndependentTest}", story.NamedRequirements, [], catalog, maxSemantic: 3, maxWeak: 0,
                        scopePath: story.SpecificationPath)
                    .Where(s => s.Level is RequirementSuggestionLevel.Structural or RequirementSuggestionLevel.Semantic).Select(s => (s.RequirementId, s.Reason)).ToList()
                : [];
            return new StoryGroupingEngine.StoryInputs(story, scenarioLinks, confirmed, suggestions);
        })];
    }

    /// <param name="Key">A stable node key: the business group id, "area:{title}", or a fixed key for the special groups.</param>
    public sealed record GroupNode(string Key, string Title, string Kind, IReadOnlyList<StoryGroupingProfile> Stories, string? Detail);

    public const string NeedsGroupingKey = "special:needs-grouping";
    public const string UngroupedKey = "special:ungrouped";
    public const string NoRequirementsKey = "special:no-requirements";

    /// <summary>
    /// The navigation tree of a grouping mode. Every story appears exactly once (its primary placement); a story with several areas lists the others as
    /// secondary in its details, never as a duplicate row. Stories that cannot be placed are never hidden: they are under «Trenger gruppering»,
    /// «Uten krav» or «Uten gruppe».
    /// </summary>
    public static List<GroupNode> Tree(Mode mode, IReadOnlyList<StoryGroupingProfile> profiles, StoryGroupingDocument? doc, RequirementSectionMap sections)
    {
        switch (mode)
        {
            case Mode.Functional:
            {
                var order = sections.Sections.Select((s, i) => (s.Title, i)).GroupBy(x => x.Title).ToDictionary(g => g.Key, g => g.Min(x => x.i), StringComparer.Ordinal);
                var nodes = profiles.Where(p => p.PrimaryArea is not null).GroupBy(p => p.PrimaryArea!)
                    .OrderBy(g => order.GetValueOrDefault(g.Key, int.MaxValue)).ThenBy(g => g.Key, StringComparer.CurrentCulture)
                    .Select(g => new GroupNode("area:" + g.Key, g.Key, "area", [.. g], SectionDetail(sections, g.Key))).ToList();
                AddSpecial(nodes, profiles.Where(p => p.PrimaryArea is null && p.State is GroupingState.NeedsGrouping or GroupingState.OnlySuggestions).ToList(),
                    NeedsGroupingKey, "Trenger gruppering", "Kravene peker mot flere områder, eller bare forslag finnes.");
                AddSpecial(nodes, profiles.Where(p => p.PrimaryArea is null && p.State == GroupingState.NoRequirements).ToList(), NoRequirementsKey, "Uten krav", "Ingen krav er knyttet til disse brukerhistoriene ennå.");
                return nodes;
            }
            case Mode.Business:
            {
                var groups = (doc?.Groups ?? []).OrderBy(g => g.Order).ToList();
                var nodes = groups.Select(g => new GroupNode(g.Id, g.Name, "business", [.. profiles.Where(p => p.GroupId == g.Id)],
                    g.FunctionalAreas.Count > 0 ? string.Join(" · ", g.FunctionalAreas) : "Ingen funksjonsområder tilordnet")).ToList();
                AddSpecial(nodes, profiles.Where(p => p.GroupId is null || groups.All(g => g.Id != p.GroupId)).ToList(), UngroupedKey, "Uten gruppe", "Ikke plassert i en forretningsgruppe.");
                return nodes;
            }
            case Mode.Stories:
                return [new GroupNode("special:all-stories", "Brukerhistorier", "stories", [.. profiles], null)];
            default:
                return [];
        }

        static void AddSpecial(List<GroupNode> nodes, List<StoryGroupingProfile> stories, string key, string title, string detail)
        {
            if (stories.Count > 0) nodes.Add(new GroupNode(key, title, "special", stories, detail));
        }
    }

    private static string? SectionDetail(RequirementSectionMap sections, string area) =>
        sections.Sections.FirstOrDefault(s => s.Title == area) is { } s ? $"{s.RequirementIds.Count} krav i denne delen av spesifikasjonen" : null;

    public static string BasisLabel(StoryLinkBasis b) => b switch
    {
        StoryLinkBasis.SourceExplicit => "Nevnt i brukerhistorien",
        StoryLinkBasis.HumanConfirmed => "Bekreftet av en person",
        StoryLinkBasis.ScenarioExplicit => "Nevnt i et akseptansescenario",
        StoryLinkBasis.Structural => "Fra oppgavene (tasks/plan)",
        _ => "Foreslått",
    };

    public static string BasisTone(StoryLinkBasis b) => b switch
    {
        StoryLinkBasis.SourceExplicit or StoryLinkBasis.HumanConfirmed => "ok",
        StoryLinkBasis.ScenarioExplicit or StoryLinkBasis.Structural => "info",
        _ => "muted",
    };

    public static string StateLabel(GroupingState s) => s switch
    {
        GroupingState.Suggested => "Foreslått gruppering",
        GroupingState.Confirmed => "Bekreftet gruppering",
        GroupingState.NeedsGrouping => "Trenger gruppering",
        GroupingState.OnlySuggestions => "Trenger gjennomgang",
        _ => "Uten krav",
    };

    /// <summary>What a change would do, computed by applying it to a copy and comparing the profiles before and after (shown before saving; nothing is
    /// claimed as changed until the server accepted it).</summary>
    public static List<string> Preview(IReadOnlyList<StoryGroupingEngine.StoryInputs> inputs, RequirementSectionMap sections, StoryGroupingDocument doc,
        IReadOnlyList<StoryGroupingOperation> ops, Func<string, bool> knownRequirement)
    {
        StoryGroupingDocument after;
        try { after = StoryGroupingEngine.Apply(doc, ops, "forhåndsvisning", DateTimeOffset.UtcNow, knownRequirement); }
        catch (StoryGroupingEngine.GroupingRefusal refusal) { return [$"Kan ikke lagres: {refusal.Message}"]; }
        var before = StoryGroupingEngine.Profiles(inputs, sections, doc).ToDictionary(p => p.StoryKey);
        var now = StoryGroupingEngine.Profiles(inputs, sections, after).ToDictionary(p => p.StoryKey);
        var label = inputs.ToDictionary(i => i.Story.Key, i => ScenarioWorkspacePresentation.StoryLabel(i.Story));
        var lines = new List<string>();
        foreach (var (key, b) in before)
        {
            var a = now[key];
            var reqBefore = b.Requirements.Where(r => r.Established).Select(r => r.DisplayId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reqAfter = a.Requirements.Where(r => r.Established).Select(r => r.DisplayId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!reqBefore.SetEquals(reqAfter))
                lines.Add($"{label[key]}: kravkoblinger {Diff(reqBefore, reqAfter)} (redigerbar sporbarhet; kilden er uendret).");
            if (!string.Equals(b.SuggestedArea, a.SuggestedArea, StringComparison.Ordinal))
                lines.Add($"{label[key]}: grupperingsforslaget endres fra «{b.SuggestedArea ?? "ingen"}» til «{a.SuggestedArea ?? "ingen"}».");
            if (!string.Equals(b.GroupId, a.GroupId, StringComparison.Ordinal) || !string.Equals(b.PrimaryArea, a.PrimaryArea, StringComparison.Ordinal))
                lines.Add($"{label[key]}: plassering endres ({Group(doc, b.GroupId) ?? b.PrimaryArea ?? "ingen"} → {Group(after, a.GroupId) ?? a.PrimaryArea ?? "ingen"}).");
            if (b.State == GroupingState.Confirmed && a.State == GroupingState.Confirmed && b.GroupId == a.GroupId && !string.Equals(b.SuggestedArea, a.SuggestedArea, StringComparison.Ordinal))
                lines.Add($"{label[key]}: bekreftet plassering beholdes til noen ser på det nye forslaget.");
        }
        foreach (var g in after.Groups.Where(g => doc.Groups.All(x => x.Id != g.Id))) lines.Add($"Ny gruppe «{g.Name}».");
        foreach (var g in doc.Groups.Where(g => after.Groups.All(x => x.Id != g.Id))) lines.Add($"Gruppen «{g.Name}» fjernes (bare grupperingen).");
        foreach (var g in after.Groups) if (doc.Groups.FirstOrDefault(x => x.Id == g.Id) is { } old && old.Name != g.Name) lines.Add($"«{old.Name}» får navnet «{g.Name}».");
        lines.Add("Akseptansescenarioer, lagrede testtilfeller, testplaner og kjøringer endres ikke.");
        return lines;

        static string Diff(HashSet<string> b, HashSet<string> a) =>
            string.Join(", ", a.Except(b).Select(x => "+" + x).Concat(b.Except(a).Select(x => "−" + x)));
        static string? Group(StoryGroupingDocument d, string? id) => d.Groups.FirstOrDefault(g => g.Id == id)?.Name;
    }
}
