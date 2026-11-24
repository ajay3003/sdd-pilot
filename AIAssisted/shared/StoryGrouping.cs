using System.Text.Json.Serialization;
using BirkNext.Sdd;
using BirkNext.TestCoverage;

namespace BirkNext.TestPlanning;

// =====================================================================================================================================
// User-owned grouping and US → FR review decisions (2026-11-24). Source-derived facts (functional areas from the FR sections, the links the
// source states) are computed from the specification and never stored here; this document holds only what people decided. One document per
// project and specification, revisioned (a save names the revision it started from; a stale save is refused, never merged).
// =====================================================================================================================================

/// <summary>A business group a person created: a name, an order and the source functional areas it organizes (navigation only).</summary>
public sealed record BusinessGroup
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int Order { get; init; }
    /// <summary>Source functional areas (FR section titles) this group organizes; a story whose primary area is one of them is suggested here.</summary>
    public List<string> FunctionalAreas { get; init; } = [];
}

/// <summary>A person's placement of one user story: its business group and/or its primary functional area (navigation only; never a link).</summary>
public sealed record StoryPlacement
{
    public string StoryKey { get; init; } = "";
    /// <summary>The business group the story is shown under; null = follow the suggestion.</summary>
    public string? GroupId { get; init; }
    /// <summary>The primary functional area chosen by a person; null = follow the suggestion.</summary>
    public string? PrimaryArea { get; init; }
    /// <summary>The suggestion (primary area) when the person decided: a later different suggestion is reported, never applied silently.</summary>
    public string? SuggestedAtDecision { get; init; }
    public string By { get; init; } = "";
    public DateTimeOffset At { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StoryLinkDecisionKind { Confirmed, Added, Rejected, Disputed, Removed }

/// <summary>
/// A review decision on one user story → requirement relation. Decisions are append-only: the latest decision per (story, requirement) is the
/// current one, earlier ones are its history. A source-stated relation can only be disputed (its source evidence stays); only a relation a person
/// added can be removed or replaced.
/// </summary>
public sealed record StoryLinkDecision
{
    public string StoryKey { get; init; } = "";
    /// <summary>Canonical requirement id (comparison key).</summary>
    public string RequirementId { get; init; } = "";
    /// <summary>The requirement id as the source writes it (display only).</summary>
    public string DisplayId { get; init; } = "";
    public string SpecificationPath { get; init; } = "";
    public StoryLinkDecisionKind Decision { get; init; }
    public string? Reason { get; init; }
    /// <summary>For a replacement: the requirement this decision replaced (one logical operation, recorded on both sides).</summary>
    public string? ReplacementOf { get; init; }
    /// <summary>A disputed source relation is left out of grouping inference when true (it stays visible as source evidence).</summary>
    public bool ExcludeFromGrouping { get; init; }
    public string By { get; init; } = "";
    public DateTimeOffset At { get; init; }
}

public sealed record GroupingHistoryEntry(DateTimeOffset At, string By, string Action, string Detail);

public sealed record StoryGroupingDocument
{
    public string ProjectKey { get; init; } = "";
    public string SpecificationPath { get; init; } = "";
    public int Revision { get; init; }
    public List<BusinessGroup> Groups { get; init; } = [];
    public List<StoryPlacement> Placements { get; init; } = [];
    public List<StoryLinkDecision> Decisions { get; init; } = [];
    public List<GroupingHistoryEntry> History { get; init; } = [];
    /// <summary>Requirements a person classified as cross-cutting (supporting evidence, never primary grouping evidence).</summary>
    public List<string> CrossCutting { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StoryGroupingOperationKind
{
    CreateGroup, RenameGroup, MoveGroup, DeleteGroup, SetGroupAreas, MergeGroups, SplitGroup,
    PlaceStory, SetPrimaryArea, ClearPlacement, RestoreSourceNavigation,
    ConfirmLink, RejectLink, AddLink, RemoveLink, ReplaceLink, RestoreLink, DisputeLink, Undo,
}

/// <summary>One change to the grouping document. Unused fields are null. Applied together and atomically (all or nothing).</summary>
public sealed record StoryGroupingOperation
{
    public StoryGroupingOperationKind Kind { get; init; }
    public string? GroupId { get; init; }
    public string? TargetGroupId { get; init; }
    public string? Name { get; init; }
    public int? Order { get; init; }
    public List<string>? Areas { get; init; }
    public List<string>? StoryKeys { get; init; }
    public string? StoryKey { get; init; }
    public string? PrimaryArea { get; init; }
    public string? SuggestedArea { get; init; }
    public string? RequirementId { get; init; }
    public string? DisplayId { get; init; }
    public string? NewRequirementId { get; init; }
    public string? NewDisplayId { get; init; }
    public string? Reason { get; init; }
    public bool ExcludeFromGrouping { get; init; }
}

public sealed record StoryGroupingChangeRequest
{
    public int ExpectedRevision { get; init; }
    public List<StoryGroupingOperation> Operations { get; init; } = [];
    /// <summary>The project's requirement catalog (as for plan saves): a link may only name a requirement of this project's specification.</summary>
    public ProjectRequirementCatalog? RequirementCatalog { get; init; }
}

// =====================================================================================================================================
// Grouping inference (pure, deterministic, no AI): user story → requirements (with provenance) → the source's functional areas.
// =====================================================================================================================================

/// <summary>How a story → requirement relation is supported, strongest first (the order is the grouping precedence).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StoryLinkBasis
{
    /// <summary>The story's own text names the requirement.</summary>
    SourceExplicit = 1,
    /// <summary>A person confirmed a suggestion or added the link (here or on a test plan's story).</summary>
    HumanConfirmed = 2,
    /// <summary>One of the story's acceptance scenarios names the requirement.</summary>
    ScenarioExplicit = 3,
    /// <summary>A task / plan line tagged with the story names the requirement (the feature's own structure).</summary>
    Structural = 4,
    /// <summary>Proposed by the rule comparison; never grouping evidence on its own.</summary>
    Suggested = 5,
}

public sealed record StoryRequirementEvidence(string Kind, string Reference, string? Detail);

/// <summary>One requirement of a story with its strongest basis, every piece of evidence, the source section and the review state.</summary>
public sealed record StoryRequirementRelation(string RequirementId, string DisplayId, StoryLinkBasis Basis, string? Area, IReadOnlyList<StoryRequirementEvidence> Evidence,
    StoryLinkDecisionKind? Decision, bool CrossCutting, bool Disputed, bool Rejected)
{
    /// <summary>Counted as grouping evidence: established (not a suggestion), not rejected, not a disputed source link left out of grouping.</summary>
    public bool Established => Basis != StoryLinkBasis.Suggested && !Rejected && !(Disputed && ExcludedByDispute);
    internal bool ExcludedByDispute { get; init; }
}

public sealed record AreaEvidence(string Area, IReadOnlyList<string> RequirementIds, StoryLinkBasis Strongest);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GroupingState { Suggested, NeedsGrouping, NoRequirements, OnlySuggestions, Confirmed }

public sealed record StoryGroupingProfile(string StoryKey, IReadOnlyList<StoryRequirementRelation> Requirements, IReadOnlyList<AreaEvidence> Areas,
    string? SuggestedArea, IReadOnlyList<string> SecondaryAreas, GroupingState State, string Explanation, string? PrimaryArea, string? GroupId,
    string? SuggestedGroupId, bool SuggestionChanged);

public static class StoryGroupingEngine
{
    /// <summary>"Ukjent" area: a requirement the specification defines outside any section, or one it does not define.</summary>
    public const string UnknownArea = "Ukjent funksjonsområde";

    /// <summary>The inputs of one story: the requirement ids each basis names, with a reference for the evidence.</summary>
    public sealed record StoryInputs(CoverageUserStory Story, IReadOnlyList<(string RequirementId, string Reference)> ScenarioLinks,
        IReadOnlyList<(string RequirementId, string Reference)> ConfirmedElsewhere, IReadOnlyList<(string RequirementId, string Reason)> Suggestions);

    /// <summary>
    /// A requirement is cross-cutting when a person said so, or when it is established evidence for at least three stories and for at least half
    /// of the stories that have established links — and is never the only established requirement of any of them (breadth alone is not proof).
    /// Cross-cutting requirements stay listed as supporting evidence; they never decide a primary area.
    /// </summary>
    public static HashSet<string> CrossCutting(IReadOnlyList<IReadOnlyList<StoryRequirementRelation>> perStory, IEnumerable<string> declared)
    {
        var result = new HashSet<string>(declared.Select(RequirementReferenceParser.Normalize), StringComparer.OrdinalIgnoreCase);
        var linked = perStory.Where(r => r.Any(x => x.Established)).ToList();
        if (linked.Count < 3) return result;
        foreach (var g in linked.SelectMany(r => r.Where(x => x.Established).Select(x => x.RequirementId).Distinct(StringComparer.OrdinalIgnoreCase))
                     .GroupBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            var holders = linked.Where(r => r.Any(x => x.Established && string.Equals(x.RequirementId, g.Key, StringComparison.OrdinalIgnoreCase))).ToList();
            if (holders.Count >= 3 && holders.Count * 2 >= linked.Count && holders.All(r => r.Count(x => x.Established) > 1)) result.Add(g.Key);
        }
        return result;
    }

    /// <summary>The relations of one story: every basis merged per requirement (strongest first), review decisions applied, areas from the source.</summary>
    public static List<StoryRequirementRelation> Relations(StoryInputs input, RequirementSectionMap sections, StoryGroupingDocument? doc)
    {
        var story = input.Story;
        var evidence = new Dictionary<string, (StoryLinkBasis Basis, List<StoryRequirementEvidence> Evidence)>(StringComparer.OrdinalIgnoreCase);
        void Add(string rawId, StoryLinkBasis basis, StoryRequirementEvidence e)
        {
            if (string.IsNullOrWhiteSpace(rawId)) return;
            var id = RequirementReferenceParser.Normalize(rawId);
            if (!evidence.TryGetValue(id, out var cur)) evidence[id] = cur = (basis, []);
            if (basis < cur.Basis) evidence[id] = cur = (basis, cur.Evidence);
            if (!cur.Evidence.Contains(e)) cur.Evidence.Add(e);
        }
        foreach (var id in story.NamedRequirements) Add(id, StoryLinkBasis.SourceExplicit, new("story", $"{story.SpecificationPath}#{story.StoryId}", "Nevnt i brukerhistoriens tekst"));
        foreach (var (id, reference) in input.ConfirmedElsewhere) Add(id, StoryLinkBasis.HumanConfirmed, new("plan", reference, "Koblet av en tester i en testplan"));
        foreach (var (id, reference) in input.ScenarioLinks) Add(id, StoryLinkBasis.ScenarioExplicit, new("scenario", reference, "Nevnt i et akseptansescenario"));
        foreach (var t in story.TaskRequirements) Add(t.RequirementId, StoryLinkBasis.Structural, new("task", $"{t.TasksPath}:{t.Line}", t.TaskText));
        foreach (var (id, reason) in input.Suggestions) Add(id, StoryLinkBasis.Suggested, new("suggestion", "regel", reason));

        // Review decisions: the latest per requirement is current.
        var current = (doc?.Decisions ?? []).Where(d => d.StoryKey == story.Key)
            .GroupBy(d => d.RequirementId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        foreach (var d in current.Values.Where(d => d.Decision is StoryLinkDecisionKind.Confirmed or StoryLinkDecisionKind.Added))
            Add(d.RequirementId, StoryLinkBasis.HumanConfirmed, new(d.Decision == StoryLinkDecisionKind.Added ? "manual" : "confirmed", d.By, d.Reason ?? (d.Decision == StoryLinkDecisionKind.Added ? "Lagt til av en person" : "Bekreftet av en person")));

        return [.. evidence.Select(kv =>
        {
            current.TryGetValue(kv.Key, out var decision);
            var entry = sections.Find(kv.Key);
            // A removed manual link adds no evidence, so it is simply absent here unless another basis still states it (that basis stands).
            StoryLinkBasis? basis = kv.Value.Basis;
            var rejected = decision?.Decision == StoryLinkDecisionKind.Rejected && basis == StoryLinkBasis.Suggested;
            var disputed = decision?.Decision == StoryLinkDecisionKind.Disputed;
            return new StoryRequirementRelation(kv.Key, entry?.DisplayId ?? kv.Key, basis!.Value, entry?.Section ?? UnknownArea, kv.Value.Evidence,
                decision?.Decision, false, disputed, rejected) { ExcludedByDispute = disputed && decision!.ExcludeFromGrouping };
        }).OfType<StoryRequirementRelation>().OrderBy(r => r.Basis).ThenBy(r => r.RequirementId, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The grouping profile of every story. Each story is decided on its own relations only (no transitive clustering). Primary area: the area with
    /// strictly the most distinct established, non-cross-cutting requirements; a tie is "Trenger gruppering"; only suggestions is "Bare forslag";
    /// none is "Uten krav". A person's placement wins and is reported when the suggestion has changed since.
    /// </summary>
    public static List<StoryGroupingProfile> Profiles(IReadOnlyList<StoryInputs> inputs, RequirementSectionMap sections, StoryGroupingDocument? doc)
    {
        var relations = inputs.Select(i => Relations(i, sections, doc)).ToList();
        var cross = CrossCutting([.. relations], doc?.CrossCutting ?? []);
        var groups = (doc?.Groups ?? []).OrderBy(g => g.Order).ToList();
        var result = new List<StoryGroupingProfile>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var story = inputs[i].Story;
            var rel = relations[i].Select(r => cross.Contains(r.RequirementId) ? r with { CrossCutting = true } : r).ToList();
            var areas = rel.Where(r => r.Established).GroupBy(r => r.Area ?? UnknownArea)
                .Select(g => new AreaEvidence(g.Key, [.. g.Select(r => r.RequirementId)], g.Min(r => r.Basis)))
                .OrderByDescending(a => a.RequirementIds.Count).ThenBy(a => a.Strongest).ThenBy(a => a.Area, StringComparer.Ordinal).ToList();
            // Primary-area candidates: established, not cross-cutting, and in a known area.
            var deciding = rel.Where(r => r.Established && !r.CrossCutting && r.Area != UnknownArea).GroupBy(r => r.Area!)
                .Select(g => (Area: g.Key, Count: g.Select(r => r.RequirementId).Distinct(StringComparer.OrdinalIgnoreCase).Count(), Strongest: g.Min(r => r.Basis)))
                .OrderByDescending(a => a.Count).ThenBy(a => a.Strongest).ToList();
            string? suggested = null; GroupingState state; string why;
            if (rel.Count == 0) { state = GroupingState.NoRequirements; why = "Ingen krav er knyttet til brukerhistorien i kilden eller av en person."; }
            else if (!rel.Any(r => r.Established)) { state = GroupingState.OnlySuggestions; why = $"Bare foreslåtte koblinger ({string.Join(", ", rel.Select(r => r.DisplayId))}); et forslag avgjør aldri grupperingen."; }
            else if (deciding.Count == 0)
            {
                state = GroupingState.NeedsGrouping;
                why = rel.Any(r => r.Established && r.CrossCutting) ? "De tilknyttede kravene er tverrgående og avgjør ikke ett område alene." : "De tilknyttede kravene står ikke under noen funksjonsdel i spesifikasjonen.";
            }
            else if (deciding.Count > 1 && deciding[0].Count == deciding[1].Count)
            {
                state = GroupingState.NeedsGrouping;
                why = $"Like mye støtte for {string.Join(" og ", deciding.TakeWhile(d => d.Count == deciding[0].Count).Select(d => $"«{d.Area}» ({d.Count})"))}: gruppering trenger en person.";
            }
            else
            {
                suggested = deciding[0].Area; state = GroupingState.Suggested;
                var ids = rel.Where(r => r.Established && !r.CrossCutting && r.Area == suggested).Select(r => r.DisplayId);
                why = $"Foreslått under «{suggested}» fordi {string.Join(", ", ids)} står i denne delen av spesifikasjonen" +
                      (deciding.Count > 1 ? $" ({deciding[0].Count} mot {deciding[1].Count} for «{deciding[1].Area}»)." : ".");
            }
            var placement = doc?.Placements.LastOrDefault(p => p.StoryKey == story.Key);
            var primary = placement?.PrimaryArea ?? suggested;
            var suggestedGroup = primary is null ? null : groups.Where(g => g.FunctionalAreas.Contains(primary, StringComparer.OrdinalIgnoreCase)).Select(g => g.Id).FirstOrDefault();
            var changed = placement is not null && !string.Equals(placement.SuggestedAtDecision ?? "", suggested ?? "", StringComparison.Ordinal);
            if (placement is not null && (placement.GroupId is not null || placement.PrimaryArea is not null)) state = GroupingState.Confirmed;
            result.Add(new StoryGroupingProfile(story.Key, rel, areas, suggested,
                [.. areas.Select(a => a.Area).Where(a => a != primary)], state, why, primary, placement?.GroupId ?? suggestedGroup, suggestedGroup, changed));
        }
        return result;
    }

    // ── Applying review operations (server side; also used by the browser for the preview) ───────────────────────────────────────────

    public sealed class GroupingRefusal(string code, string message) : Exception(message) { public string Code { get; } = code; }

    /// <summary>
    /// Applies the operations to a copy of the document, all or nothing. Never touches source facts: a source-stated relation can only be disputed;
    /// only a relation a person added can be removed or replaced (a replacement is one recorded operation); deleting a group removes grouping
    /// metadata only. <paramref name="knownRequirement"/> decides whether a requirement exists in the project's specification (no fictional FR).
    /// </summary>
    public static StoryGroupingDocument Apply(StoryGroupingDocument doc, IReadOnlyList<StoryGroupingOperation> operations, string actor, DateTimeOffset now,
        Func<string, bool> knownRequirement, Func<string, string>? newId = null)
    {
        newId ??= _ => Guid.NewGuid().ToString("N")[..12];
        var groups = doc.Groups.ToList();
        var placements = doc.Placements.ToList();
        var decisions = doc.Decisions.ToList();
        var history = doc.History.ToList();
        void Log(string action, string detail) => history.Add(new(now, actor, action, detail));
        StoryLinkDecision? Current(string story, string req) =>
            decisions.LastOrDefault(d => d.StoryKey == story && string.Equals(d.RequirementId, req, StringComparison.OrdinalIgnoreCase));
        BusinessGroup Group(string? id) => groups.FirstOrDefault(g => g.Id == id) ?? throw new GroupingRefusal("unknown-group", "Gruppen finnes ikke (den kan være slettet av en annen).");
        string Req(string? raw, string what) => string.IsNullOrWhiteSpace(raw) ? throw new GroupingRefusal("missing-requirement", $"Mangler krav ({what}).") : RequirementReferenceParser.Normalize(raw.Trim());
        string Story(string? key) => string.IsNullOrWhiteSpace(key) ? throw new GroupingRefusal("missing-story", "Mangler brukerhistorie.") : key;
        void Decide(string story, string req, string? display, StoryLinkDecisionKind kind, string? reason, string? replacementOf = null, bool exclude = false) =>
            decisions.Add(new StoryLinkDecision { StoryKey = story, RequirementId = req, DisplayId = string.IsNullOrWhiteSpace(display) ? req : display.Trim(),
                SpecificationPath = doc.SpecificationPath, Decision = kind, Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), ReplacementOf = replacementOf,
                ExcludeFromGrouping = exclude, By = actor, At = now });

        foreach (var op in operations)
        {
            switch (op.Kind)
            {
                case StoryGroupingOperationKind.CreateGroup:
                {
                    var name = op.Name?.Trim() is { Length: > 0 } n ? n : throw new GroupingRefusal("missing-name", "Gruppen må ha et navn.");
                    if (groups.Any(g => string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase))) throw new GroupingRefusal("duplicate-group", $"Det finnes allerede en gruppe som heter «{name}».");
                    var id = op.GroupId is { Length: > 0 } given ? given : newId(name);
                    groups.Add(new BusinessGroup { Id = id, Name = name, Order = groups.Count == 0 ? 0 : groups.Max(g => g.Order) + 1, FunctionalAreas = [.. op.Areas ?? []] });
                    Log("Opprettet gruppe", name);
                    break;
                }
                case StoryGroupingOperationKind.RenameGroup:
                {
                    var g = Group(op.GroupId);
                    var name = op.Name?.Trim() is { Length: > 0 } n ? n : throw new GroupingRefusal("missing-name", "Gruppen må ha et navn.");
                    if (groups.Any(x => x.Id != g.Id && string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase))) throw new GroupingRefusal("duplicate-group", $"Det finnes allerede en gruppe som heter «{name}».");
                    groups[groups.IndexOf(g)] = g with { Name = name };
                    Log("Endret gruppenavn", $"«{g.Name}» → «{name}»");
                    break;
                }
                case StoryGroupingOperationKind.MoveGroup:
                {
                    var g = Group(op.GroupId);
                    var ordered = groups.OrderBy(x => x.Order).ToList();
                    ordered.Remove(g);
                    ordered.Insert(Math.Clamp(op.Order ?? 0, 0, ordered.Count), g);
                    groups = [.. ordered.Select((x, index) => x with { Order = index })];
                    Log("Flyttet gruppe", $"«{g.Name}» til plass {Math.Clamp(op.Order ?? 0, 0, ordered.Count - 1) + 1}");
                    break;
                }
                case StoryGroupingOperationKind.DeleteGroup:
                {
                    var g = Group(op.GroupId);
                    if (placements.Any(p => p.GroupId == g.Id)) throw new GroupingRefusal("group-not-empty", $"«{g.Name}» har brukerhistorier plassert i seg. Flytt dem eller slå sammen gruppene først.");
                    groups.Remove(g);
                    Log("Slettet gruppe", $"«{g.Name}» (bare grupperingen; ingen brukerhistorier, krav, scenarioer eller testtilfeller er berørt)");
                    break;
                }
                case StoryGroupingOperationKind.SetGroupAreas:
                {
                    var g = Group(op.GroupId);
                    var areas = (op.Areas ?? []).Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    groups[groups.IndexOf(g)] = g with { FunctionalAreas = areas };
                    Log("Endret funksjonsområder", $"«{g.Name}»: {(areas.Count == 0 ? "ingen" : string.Join(", ", areas))}");
                    break;
                }
                case StoryGroupingOperationKind.MergeGroups:
                {
                    var source = Group(op.GroupId); var into = Group(op.TargetGroupId);
                    if (source.Id == into.Id) throw new GroupingRefusal("same-group", "En gruppe kan ikke slås sammen med seg selv.");
                    groups[groups.IndexOf(into)] = into with { FunctionalAreas = [.. into.FunctionalAreas.Concat(source.FunctionalAreas).Distinct(StringComparer.OrdinalIgnoreCase)] };
                    var moved = 0;
                    for (var k = 0; k < placements.Count; k++)
                        if (placements[k].GroupId == source.Id) { placements[k] = placements[k] with { GroupId = into.Id, By = actor, At = now }; moved++; }
                    groups.RemoveAll(x => x.Id == source.Id);
                    Log("Slo sammen grupper", $"«{source.Name}» inn i «{into.Name}» ({moved} brukerhistorier flyttet)");
                    break;
                }
                case StoryGroupingOperationKind.SplitGroup:
                {
                    var source = Group(op.GroupId);
                    var name = op.Name?.Trim() is { Length: > 0 } n ? n : throw new GroupingRefusal("missing-name", "Den nye gruppen må ha et navn.");
                    var stories = op.StoryKeys ?? []; var areas = op.Areas ?? [];
                    if (stories.Count == 0 && areas.Count == 0) throw new GroupingRefusal("empty-split", "Velg brukerhistorier eller funksjonsområder som skal til den nye gruppen.");
                    if (groups.Any(g => string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase))) throw new GroupingRefusal("duplicate-group", $"Det finnes allerede en gruppe som heter «{name}».");
                    var id = op.TargetGroupId is { Length: > 0 } given ? given : newId(name);
                    groups[groups.IndexOf(source)] = source with { FunctionalAreas = [.. source.FunctionalAreas.Where(a => !areas.Contains(a, StringComparer.OrdinalIgnoreCase))] };
                    groups.Add(new BusinessGroup { Id = id, Name = name, Order = groups.Max(g => g.Order) + 1, FunctionalAreas = [.. areas] });
                    foreach (var s in stories)
                    {
                        var k = placements.FindLastIndex(p => p.StoryKey == s);
                        if (k >= 0) placements[k] = placements[k] with { GroupId = id, By = actor, At = now };
                        else placements.Add(new StoryPlacement { StoryKey = s, GroupId = id, By = actor, At = now });
                    }
                    Log("Delte gruppe", $"«{name}» fra «{source.Name}» ({stories.Count} brukerhistorier, {areas.Count} funksjonsområder)");
                    break;
                }
                case StoryGroupingOperationKind.PlaceStory:
                {
                    var s = Story(op.StoryKey);
                    var target = op.GroupId is null ? null : Group(op.GroupId);
                    var k = placements.FindLastIndex(p => p.StoryKey == s);
                    var before = k >= 0 ? groups.FirstOrDefault(g => g.Id == placements[k].GroupId)?.Name : null;
                    var placed = (k >= 0 ? placements[k] : new StoryPlacement { StoryKey = s }) with { GroupId = target?.Id, SuggestedAtDecision = op.SuggestedArea, By = actor, At = now };
                    if (k >= 0) placements[k] = placed; else placements.Add(placed);
                    Log("Flyttet brukerhistorie", $"{s}: {before ?? "forslag"} → {target?.Name ?? "forslag"} (krav og scenarioer er uendret)");
                    break;
                }
                case StoryGroupingOperationKind.SetPrimaryArea:
                {
                    var s = Story(op.StoryKey);
                    var k = placements.FindLastIndex(p => p.StoryKey == s);
                    var placed = (k >= 0 ? placements[k] : new StoryPlacement { StoryKey = s }) with { PrimaryArea = string.IsNullOrWhiteSpace(op.PrimaryArea) ? null : op.PrimaryArea.Trim(), SuggestedAtDecision = op.SuggestedArea, By = actor, At = now };
                    if (k >= 0) placements[k] = placed; else placements.Add(placed);
                    Log("Endret primært funksjonsområde", $"{s}: {placed.PrimaryArea ?? "forslaget"} (kravkoblinger og kravenes kildedeler er uendret)");
                    break;
                }
                case StoryGroupingOperationKind.ClearPlacement:
                {
                    var s = Story(op.StoryKey);
                    if (placements.RemoveAll(p => p.StoryKey == s) > 0) Log("Følger forslaget igjen", s);
                    break;
                }
                case StoryGroupingOperationKind.RestoreSourceNavigation:
                {
                    var n = placements.Count;
                    placements.Clear();
                    Log("Gjenopprettet kildebasert navigasjon", $"{n} manuelle plasseringer fjernet; grupper og kravbeslutninger er beholdt");
                    break;
                }
                case StoryGroupingOperationKind.ConfirmLink:
                {
                    var s = Story(op.StoryKey); var r = Req(op.RequirementId, "bekreft");
                    if (!knownRequirement(r)) throw new GroupingRefusal("unknown-requirement", $"{op.DisplayId ?? r} finnes ikke i spesifikasjonen.");
                    Decide(s, r, op.DisplayId, StoryLinkDecisionKind.Confirmed, op.Reason);
                    Log("Bekreftet kravkobling", $"{s} → {op.DisplayId ?? r}");
                    break;
                }
                case StoryGroupingOperationKind.RejectLink:
                {
                    var s = Story(op.StoryKey); var r = Req(op.RequirementId, "avvis");
                    Decide(s, r, op.DisplayId, StoryLinkDecisionKind.Rejected, op.Reason);
                    Log("Avviste forslag", $"{s} → {op.DisplayId ?? r}");
                    break;
                }
                case StoryGroupingOperationKind.RestoreLink:
                {
                    var s = Story(op.StoryKey); var r = Req(op.RequirementId, "gjenopprett");
                    if (Current(s, r)?.Decision is not (StoryLinkDecisionKind.Rejected or StoryLinkDecisionKind.Disputed))
                        throw new GroupingRefusal("not-rejected", $"{op.DisplayId ?? r} er ikke avvist eller omstridt.");
                    Decide(s, r, op.DisplayId, StoryLinkDecisionKind.Confirmed, op.Reason ?? "Gjenopprettet");
                    Log("Gjenopprettet kobling", $"{s} → {op.DisplayId ?? r}");
                    break;
                }
                case StoryGroupingOperationKind.AddLink:
                {
                    var s = Story(op.StoryKey); var r = Req(op.RequirementId, "legg til");
                    if (!knownRequirement(r)) throw new GroupingRefusal("unknown-requirement", $"{op.DisplayId ?? r} finnes ikke i denne spesifikasjonen. Bare eksisterende krav kan kobles.");
                    if (Current(s, r)?.Decision is StoryLinkDecisionKind.Added or StoryLinkDecisionKind.Confirmed)
                        throw new GroupingRefusal("duplicate-link", $"{op.DisplayId ?? r} er allerede koblet til brukerhistorien.");
                    Decide(s, r, op.DisplayId, StoryLinkDecisionKind.Added, op.Reason);
                    Log("La til kravkobling", $"{s} → {op.DisplayId ?? r}");
                    break;
                }
                case StoryGroupingOperationKind.RemoveLink:
                {
                    var s = Story(op.StoryKey); var r = Req(op.RequirementId, "fjern");
                    if (Current(s, r)?.Decision is not (StoryLinkDecisionKind.Added or StoryLinkDecisionKind.Confirmed))
                        throw new GroupingRefusal("not-manual", $"{op.DisplayId ?? r} er ikke en kobling en person har lagt til. En kobling kilden oppgir kan bare markeres som omstridt.");
                    Decide(s, r, op.DisplayId, StoryLinkDecisionKind.Removed, op.Reason);
                    Log("Fjernet kravkobling", $"{s} → {op.DisplayId ?? r}");
                    break;
                }
                case StoryGroupingOperationKind.ReplaceLink:
                {
                    var s = Story(op.StoryKey); var r = Req(op.RequirementId, "erstatt"); var n = Req(op.NewRequirementId, "ny");
                    if (string.Equals(r, n, StringComparison.OrdinalIgnoreCase)) throw new GroupingRefusal("same-requirement", "Det nye kravet er det samme som det gamle.");
                    if (Current(s, r)?.Decision is not (StoryLinkDecisionKind.Added or StoryLinkDecisionKind.Confirmed))
                        throw new GroupingRefusal("not-manual", $"{op.DisplayId ?? r} er ikke en kobling en person har lagt til. En kobling kilden oppgir kan bare markeres som omstridt, med en rettet kobling ved siden av.");
                    if (!knownRequirement(n)) throw new GroupingRefusal("unknown-requirement", $"{op.NewDisplayId ?? n} finnes ikke i denne spesifikasjonen.");
                    if (Current(s, n)?.Decision is StoryLinkDecisionKind.Added or StoryLinkDecisionKind.Confirmed) throw new GroupingRefusal("duplicate-link", $"{op.NewDisplayId ?? n} er allerede koblet.");
                    Decide(s, r, op.DisplayId, StoryLinkDecisionKind.Removed, op.Reason, replacementOf: null);
                    Decide(s, n, op.NewDisplayId, StoryLinkDecisionKind.Added, op.Reason, replacementOf: r);
                    Log("Erstattet kravkobling", $"{s}: {op.DisplayId ?? r} → {op.NewDisplayId ?? n}");
                    break;
                }
                case StoryGroupingOperationKind.DisputeLink:
                {
                    var s = Story(op.StoryKey); var r = Req(op.RequirementId, "omstridt");
                    if (string.IsNullOrWhiteSpace(op.Reason)) throw new GroupingRefusal("missing-reason", "Skriv hvorfor kildekoblingen er omstridt.");
                    Decide(s, r, op.DisplayId, StoryLinkDecisionKind.Disputed, op.Reason, exclude: op.ExcludeFromGrouping);
                    if (op.NewRequirementId is { Length: > 0 } corrected)
                    {
                        var n = Req(corrected, "rettet");
                        if (!knownRequirement(n)) throw new GroupingRefusal("unknown-requirement", $"{op.NewDisplayId ?? n} finnes ikke i denne spesifikasjonen.");
                        Decide(s, n, op.NewDisplayId, StoryLinkDecisionKind.Added, op.Reason, replacementOf: r);
                    }
                    Log("Markerte kildekobling som omstridt", $"{s} → {op.DisplayId ?? r}{(op.NewRequirementId is { Length: > 0 } ? $"; rettet kobling {op.NewDisplayId ?? op.NewRequirementId}" : "")} (kildebeviset står)");
                    break;
                }
                case StoryGroupingOperationKind.Undo:
                {
                    // Compensating change for the latest relation decision of a story (never an erasure of history).
                    var s = Story(op.StoryKey);
                    var last = decisions.LastOrDefault(d => d.StoryKey == s && d.By == actor) ?? throw new GroupingRefusal("nothing-to-undo", "Det er ingen kravendring å angre for brukerhistorien.");
                    var earlier = decisions.Where(d => d.StoryKey == s && string.Equals(d.RequirementId, last.RequirementId, StringComparison.OrdinalIgnoreCase) && !ReferenceEquals(d, last)).LastOrDefault();
                    if (last.ReplacementOf is { } replaced)
                    {
                        var oldDecision = decisions.Where(d => d.StoryKey == s && string.Equals(d.RequirementId, replaced, StringComparison.OrdinalIgnoreCase) && d.Decision != StoryLinkDecisionKind.Removed).LastOrDefault();
                        Decide(s, last.RequirementId, last.DisplayId, StoryLinkDecisionKind.Removed, "Angret");
                        if (oldDecision is not null) Decide(s, replaced, oldDecision.DisplayId, oldDecision.Decision, "Angret");
                    }
                    else if (earlier is not null) Decide(s, last.RequirementId, last.DisplayId, earlier.Decision, "Angret", exclude: earlier.ExcludeFromGrouping);
                    else Decide(s, last.RequirementId, last.DisplayId, StoryLinkDecisionKind.Removed, "Angret"); // back to no decision: source evidence and suggestions stand
                    Log("Angret kravendring", $"{s} → {last.DisplayId}");
                    break;
                }
            }
        }
        return doc with { Revision = doc.Revision + 1, Groups = groups, Placements = placements, Decisions = decisions, History = history };
    }
}
