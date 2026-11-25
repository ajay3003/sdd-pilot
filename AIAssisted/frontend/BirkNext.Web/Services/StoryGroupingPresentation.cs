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

    /// <summary>The default display grouping (2026-11-25): always «Brukerhistorier». Functional areas and business groups stay available in «Grupper
    /// etter»; this is display only — area assignments and reviewed grouping decisions are not changed.</summary>
    public static Mode DefaultMode(StoryGroupingDocument? doc, RequirementSectionMap sections) => Mode.Stories;

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

    /// <param name="Key">A stable node key: the business group id, "area:{title}", or <see cref="UngroupedKey"/>.</param>
    /// <param name="Kind">"area", "business", "stories" or "system" (the permanent «Uten gruppe»).</param>
    public sealed record GroupNode(string Key, string Title, string Kind, IReadOnlyList<StoryGroupingProfile> Stories, string? Detail);

    /// <summary>The permanent system view «Uten gruppe» (2026-11-25): derived from grouping state, never a stored group; it cannot be renamed, deleted or
    /// merged, and it is present in every grouped mode, also when empty.</summary>
    public const string UngroupedKey = "system:ungrouped";
    public const string UngroupedTitle = "Uten gruppe";

    /// <summary>
    /// The navigation tree of a grouping mode. Every story appears exactly once: under its primary area (functional mode) or business group (business
    /// mode) when it has one — a person's placement, or a clear source suggestion — and otherwise under the permanent «Uten gruppe», with its reason.
    /// Related areas are shown in a story's details, never as a duplicate row. Nothing is ever hidden.
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
                nodes.Add(Ungrouped([.. profiles.Where(p => p.PrimaryArea is null)]));
                return nodes;
            }
            case Mode.Business:
            {
                var groups = (doc?.Groups ?? []).OrderBy(g => g.Order).ToList();
                var nodes = groups.Select(g => new GroupNode(g.Id, g.Name, "business", [.. profiles.Where(p => p.GroupId == g.Id)],
                    g.FunctionalAreas.Count > 0 ? string.Join(" · ", g.FunctionalAreas) : "Ingen funksjonsområder tilordnet")).ToList();
                nodes.Add(Ungrouped([.. profiles.Where(p => p.GroupId is null || groups.All(g => g.Id != p.GroupId))]));
                return nodes;
            }
            case Mode.Stories:
                return [new GroupNode("special:all-stories", "Brukerhistorier", "stories", [.. profiles], null)];
            default:
                return [];
        }

        static GroupNode Ungrouped(List<StoryGroupingProfile> stories) => new(UngroupedKey, UngroupedTitle, "system", stories,
            stories.Count == 0 ? "Alle brukerhistorier er gruppert." : "Brukerhistorier uten gjeldende gruppe. De kan testes som alle andre.");
    }

    /// <summary>Why a story is under «Uten gruppe» in a mode, in the tester's words (null when it is grouped in that mode).</summary>
    public static string? UngroupedReason(StoryGroupingProfile p, Mode mode, StoryGroupingDocument? doc)
    {
        var grouped = mode == Mode.Business ? p.GroupId is { } g && (doc?.Groups ?? []).Any(x => x.Id == g) : p.PrimaryArea is not null;
        if (grouped) return null;
        if (p.KeptUngrouped) return "Bevisst uten gruppe";
        if (p.MissingGroupId is not null) return "Gruppen finnes ikke lenger";
        return p.State switch
        {
            GroupingState.NoRequirements => "Ingen tilknyttede funksjonelle krav",
            GroupingState.OnlySuggestions => "Bare foreslåtte kravkoblinger",
            GroupingState.NeedsGrouping => "Flere mulige grupper",
            GroupingState.NeedsReview => p.Candidates.Count == 0 ? "Kravene står utenfor funksjonsdelene" : "Trenger gjennomgang",
            _ when mode == Mode.Business => p.PrimaryArea is { } area ? $"Ikke i en forretningsgruppe (område «{area}»)" : "Ikke i en forretningsgruppe",
            _ => "Uten funksjonsområde",
        };
    }

    /// <summary>The story's grouping status for testers: «Gruppert», «Uten gruppe», «Trenger gjennomgang» or «Flere mulige grupper».</summary>
    public static string StatusLabel(StoryGroupingProfile p) => p.KeptUngrouped ? "Uten gruppe" : p.State switch
    {
        GroupingState.Confirmed or GroupingState.Suggested => "Gruppert",
        GroupingState.NeedsReview => "Trenger gjennomgang",
        GroupingState.NeedsGrouping => "Flere mulige grupper",
        _ => "Uten gruppe",
    };

    /// <summary>How a grouped story got its group (never presents a suggestion as confirmed).</summary>
    public static string? StatusBasis(StoryGroupingProfile p) => p.KeptUngrouped ? "besluttet av en person" : p.State switch
    {
        GroupingState.Confirmed => "bekreftet av en person",
        GroupingState.Suggested => "foreslått fra kilden",
        _ => null,
    };

    public static string StatusTone(StoryGroupingProfile p) => p.KeptUngrouped ? "muted" : p.State switch
    {
        GroupingState.Confirmed => "ok",
        GroupingState.Suggested => "info",
        GroupingState.NoRequirements or GroupingState.OnlySuggestions => "muted",
        _ => "warn",
    };

    /// <summary>
    /// «Gjennomgå gruppering»: the stories that need a person — no current group for an open reason (not a deliberate «Uten gruppe»), several
    /// possible groups, a thin or cross-cutting candidate, a placement naming a deleted group, or a confirmed placement whose suggestion changed.
    /// </summary>
    public static List<StoryGroupingProfile> ReviewQueue(IReadOnlyList<StoryGroupingProfile> profiles) =>
        [.. profiles.Where(p => !p.KeptUngrouped && (p.NeedsReviewFlag || p.MissingGroupId is not null
            || p.State is GroupingState.NeedsReview or GroupingState.NeedsGrouping or GroupingState.OnlySuggestions or GroupingState.NoRequirements))];

    /// <summary>
    /// What the page does with the stored grouping it read (2026-11-25): a document is used (and editable); a failed read is «Gruppering feilet» —
    /// never an empty document, which would silently turn confirmed placements back into suggestions; a host without grouping storage
    /// (<paramref name="notAvailable"/>) is no failure: suggestions only, nothing to edit.
    /// </summary>
    public static (StoryGroupingDocument? Document, string? Error, bool Writable) LoadOutcome(StoryGroupingDocument? doc, string? error, string notAvailable) =>
        doc is not null ? (doc, null, true)
        : error == notAvailable ? (null, null, false)
        : (null, error ?? "Grupperingen kunne ikke leses.", false);

    /// <summary>«Kilde mangler»: there are stories but the specification text (and so its FR sections) is not available.</summary>
    public static bool SourceMissing(bool hasStories, string? specificationText) => hasStories && string.IsNullOrWhiteSpace(specificationText);

    /// <summary>A change's identity across retries: the revision it starts from and its operations (the same retry gives the same id; FNV-1a 64).</summary>
    public static string RequestId(int revision, IReadOnlyList<StoryGroupingOperation> ops)
    {
        var text = revision + "|" + System.Text.Json.JsonSerializer.Serialize(ops);
        var hash = 14695981039346656037UL;
        foreach (var c in text) { hash ^= c; hash *= 1099511628211UL; }
        return $"r{revision}-{hash:x16}";
    }

    /// <summary>
    /// Navigation invariants (2026-11-25), checked by tests and safe to check at runtime: every story of the profiles is in the tree exactly once; every
    /// story that has scenarios has a profile; scenarios are not duplicated. Returns the violations (empty = sound).
    /// </summary>
    public static List<string> NavigationViolations(IReadOnlyList<S.Item> items, IReadOnlyList<StoryGroupingProfile> profiles, IReadOnlyList<GroupNode> tree, Mode mode)
    {
        var problems = new List<string>();
        if (mode is Mode.Functional or Mode.Business)
        {
            var seen = tree.SelectMany(n => n.Stories.Select(s => s.StoryKey)).GroupBy(k => k, StringComparer.Ordinal).ToList();
            problems.AddRange(seen.Where(g => g.Count() > 1).Select(g => $"{g.Key} står {g.Count()} ganger i navigasjonen."));
            var keys = seen.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            problems.AddRange(profiles.Where(p => !keys.Contains(p.StoryKey)).Select(p => $"{p.StoryKey} mangler i navigasjonen."));
            if (tree.Count(n => n.Key == UngroupedKey) != 1) problems.Add("«Uten gruppe» mangler.");
        }
        var profiled = profiles.Select(p => p.StoryKey).ToHashSet(StringComparer.Ordinal);
        problems.AddRange(items.Where(i => i.Candidate.Story is { } s && !profiled.Contains(s.Key)).Select(i => i.Candidate.Story!.Key).Distinct()
            .Select(k => $"{k} har scenarioer, men ingen grupperingsprofil."));
        problems.AddRange(items.GroupBy(i => i.Id, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"Scenario {g.Key} finnes flere ganger."));
        return problems;
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
        GroupingState.Suggested or GroupingState.Confirmed => "Gruppert",
        GroupingState.NeedsGrouping => "Flere mulige grupper",
        GroupingState.NeedsReview => "Trenger gjennomgang",
        _ => "Uten gruppe",
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
        foreach (var op in ops.Where(o => o.Kind == StoryGroupingOperationKind.SetLinkRole))
            lines.Add($"{(op.StoryKey is { } k && label.TryGetValue(k, out var l) ? l : op.StoryKey)}: rollen til {op.DisplayId ?? op.RequirementId} blir «{StoryGroupingEngine.RoleLabel(op.Role ?? StoryLinkRole.Unknown)}» (koblingen og kildebeviset er uendret).");
        lines.Add("Akseptansescenarioer, lagrede testtilfeller, testplaner og kjøringer endres ikke.");
        return lines;

        static string Diff(HashSet<string> b, HashSet<string> a) =>
            string.Join(", ", a.Except(b).Select(x => "+" + x).Concat(b.Except(a).Select(x => "−" + x)));
        static string? Group(StoryGroupingDocument d, string? id) => d.Groups.FirstOrDefault(g => g.Id == id)?.Name;
    }
}
