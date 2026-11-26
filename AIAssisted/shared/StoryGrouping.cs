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
    /// <summary>A person decided the story stays under «Uten gruppe» (2026-11-25): a reviewed decision, not an open question.</summary>
    public bool KeepUngrouped { get; init; }
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
    /// <summary>Requirements (or functional areas) a person classified as cross-cutting (supporting evidence, never primary grouping evidence).</summary>
    public List<string> CrossCutting { get; init; } = [];
    /// <summary>Ids of the latest applied change requests (2026-11-25): a retried save of a change that was already stored is answered with the
    /// stored document instead of being applied twice (no duplicate group after a timeout).</summary>
    public List<string> AppliedRequests { get; init; } = [];
    /// <summary>Reviewers' role decisions per story → requirement (2026-11-25). A role never changes the link itself or its source evidence.</summary>
    public List<StoryLinkRoleDecision> Roles { get; init; } = [];
    /// <summary>Reviewers' manual/technical decisions per scenario (2026-11-25), newest last; a decision without a kind returns the scenario to
    /// the automatic suggestion. Never changes the scenario, its cases or its source.</summary>
    public List<ScenarioTestKindDecision> TestKinds { get; init; } = [];
    /// <summary>Reviewers' decisions about a source test and a scenario (2026-11-25), newest last: confirmed or rejected, and how much of the scenario
    /// the test checks. Never a test result; never changes the test, the scenario or any test case.</summary>
    public List<ScenarioTestLinkDecision> TestLinks { get; init; } = [];
    /// <summary>Reviewers' scenario-type decisions (functional / non-functional / mixed / unclassified with quality attributes), newest last
    /// (2026-11-26); a decision without a type returns the scenario to the source-derived proposal. Never changes the scenario, its test method or
    /// its test cases.</summary>
    public List<ScenarioTypeDecision> ScenarioTypes { get; init; } = [];
    /// <summary>Testers' preparation decisions per scenario and criterion (adaptive AC readiness, 2026-11-26), newest last. Never changes the
    /// scenario, its cases, an approval or an execution result.</summary>
    public List<ScenarioPreparationDecision> Preparation { get; init; } = [];
}

/// <summary>A reviewer's decision about a source test for a scenario. <see cref="Cleared"/> returns it to what the source says.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestLinkReview { Confirmed, Rejected, Cleared }

/// <summary>How much of a scenario a confirmed test checks, as a reviewer assessed it from the test's assertions. Never inferred from a link.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TechnicalCoverageExtent { NotAssessed, Partial, Full }

public sealed record ScenarioTestLinkDecision
{
    public string ScenarioId { get; init; } = "";
    public string TestId { get; init; } = "";
    public TestLinkReview Review { get; init; }
    public TechnicalCoverageExtent Extent { get; init; }
    /// <summary>The test's source fingerprint when it was reviewed: another fingerprint now means the test changed and the decision must be reviewed again.</summary>
    public string? TestFingerprint { get; init; }
    /// <summary>The scenario's content fingerprint when it was reviewed (a changed scenario also needs a new look).</summary>
    public string? ScenarioFingerprint { get; init; }
    public string? Note { get; init; }
    public string By { get; init; } = "";
    public DateTimeOffset At { get; init; }
}

/// <summary>Whether a scenario is tested by a person (manual), belongs with technical testing, or needs both (2026-11-25). <see cref="Both"/> is a
/// reviewed SCENARIO decision only («Begge»): a saved test case is always Manual or Technical. No decision at all is «Ikke vurdert».</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioTestKind { Manual, Technical, Both }

/// <summary>A reviewer's manual/technical decision for one scenario of this specification; <see cref="Kind"/> null = back to the suggestion.</summary>
public sealed record ScenarioTestKindDecision
{
    public string ScenarioId { get; init; } = "";
    public string? StoryKey { get; init; }
    public ScenarioTestKind? Kind { get; init; }
    public string? Reason { get; init; }
    public string By { get; init; } = "";
    public DateTimeOffset At { get; init; }
}

/// <summary>
/// What a requirement is FOR a user story (2026-11-25), separate from how the link is known (<see cref="StoryLinkBasis"/>, the provenance). A source
/// reference proves that a relationship exists; it does not prove primary business ownership. Only <see cref="Primary"/> (set by a reviewer) or
/// unreviewed responsibility evidence (the story's own text, its tagged tasks, its phase goal, a person's link) can decide a story's primary area.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StoryLinkRole
{
    /// <summary>The relationship is real; its role is not established by the evidence.</summary>
    Unknown,
    /// <summary>The requirement defines the story's central behaviour (reviewer decision).</summary>
    Primary,
    /// <summary>A prerequisite, constraint, exception or supporting operation (task details, plan mentions, or a reviewer).</summary>
    Supporting,
    /// <summary>A control many stories pass through (authorisation, classification, audit) — derived from breadth, or a reviewer.</summary>
    CrossCutting,
    /// <summary>Mentioned for comparison or background (reviewer decision).</summary>
    Contextual,
}

/// <summary>A reviewer's role for one story → requirement relationship; the latest per pair is current; null role = unresolved again.</summary>
public sealed record StoryLinkRoleDecision
{
    public string StoryKey { get; init; } = "";
    public string RequirementId { get; init; } = "";
    public StoryLinkRole Role { get; init; }
    public string? Reason { get; init; }
    public string By { get; init; } = "";
    public DateTimeOffset At { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StoryGroupingOperationKind
{
    CreateGroup, RenameGroup, MoveGroup, DeleteGroup, SetGroupAreas, MergeGroups, SplitGroup,
    PlaceStory, SetPrimaryArea, ClearPlacement, RestoreSourceNavigation,
    ConfirmLink, RejectLink, AddLink, RemoveLink, ReplaceLink, RestoreLink, DisputeLink, Undo,
    /// <summary>A reviewed decision to keep the story under «Uten gruppe» (2026-11-25).</summary>
    LeaveUngrouped,
    /// <summary>Sets the reviewed role of a story → requirement relationship (<see cref="StoryGroupingOperation.Role"/>); Unknown = unresolved.</summary>
    SetLinkRole,
    /// <summary>Sets a scenario's manual/technical kind (<see cref="StoryGroupingOperation.ScenarioId"/>, <see cref="StoryGroupingOperation.TestKind"/>;
    /// null = back to the automatic suggestion). 2026-11-25.</summary>
    SetScenarioTestKind,
    /// <summary>A reviewer's decision about a source test for a scenario (<see cref="StoryGroupingOperation.ScenarioId"/>, <see cref="StoryGroupingOperation.TestId"/>,
    /// <see cref="StoryGroupingOperation.LinkReview"/>, <see cref="StoryGroupingOperation.Extent"/>). 2026-11-25.</summary>
    SetScenarioTestLink,
    /// <summary>A reviewer confirms or changes a scenario's type (<see cref="StoryGroupingOperation.ScenarioId"/> = its source identity,
    /// <see cref="StoryGroupingOperation.ScenarioType"/>, <see cref="StoryGroupingOperation.Qualities"/>, <see cref="StoryGroupingOperation.ScenarioFingerprint"/>;
    /// no type = back to the proposal). 2026-11-26.</summary>
    SetScenarioType,
    /// <summary>A tester's preparation decision on one criterion of one scenario (<see cref="StoryGroupingOperation.ScenarioId"/> = its source
    /// identity, <see cref="StoryGroupingOperation.Criterion"/>, <see cref="StoryGroupingOperation.Applicability"/>, <see cref="StoryGroupingOperation.Evaluation"/>,
    /// <see cref="StoryGroupingOperation.Evidence"/>, <see cref="StoryGroupingOperation.Reason"/>). 2026-11-26.</summary>
    SetScenarioPreparation,
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
    /// <summary>DeleteGroup of a group with stories (2026-11-25): true = the stories go to «Uten gruppe»; or name <see cref="TargetGroupId"/> to move
    /// them there. Without either, deleting a non-empty group is refused. Never deletes a story, scenario, requirement or test case.</summary>
    public bool ReleaseMembers { get; init; }
    /// <summary>SetLinkRole: the role a reviewer gives the relationship.</summary>
    public StoryLinkRole? Role { get; init; }
    /// <summary>SetScenarioTestKind: the scenario (its candidate id in this specification) and the chosen kind (null = automatic).</summary>
    public string? ScenarioId { get; init; }
    public ScenarioTestKind? TestKind { get; init; }
    /// <summary>SetScenarioTestLink: the source test (its stable definition id), the decision, the assessed extent and the fingerprints it was made on.</summary>
    public string? TestId { get; init; }
    public TestLinkReview? LinkReview { get; init; }
    public TechnicalCoverageExtent? Extent { get; init; }
    public string? TestFingerprint { get; init; }
    public string? ScenarioFingerprint { get; init; }
    /// <summary>SetScenarioType: the reviewed type (null = back to the proposal) and its quality attributes.</summary>
    public ScenarioRequirementType? ScenarioType { get; init; }
    public List<QualityAttribute>? Qualities { get; init; }
    /// <summary>SetScenarioPreparation: the criterion and the parts the tester decided (null = keep the earlier decision for that part).</summary>
    public PreparationCriterion? Criterion { get; init; }
    public CriterionApplicability? Applicability { get; init; }
    public CriterionEvaluation? Evaluation { get; init; }
    public string? Evidence { get; init; }
}

public sealed record StoryGroupingChangeRequest
{
    /// <summary>Identifies this change across retries (2026-11-25): the browser derives it from the revision and the operations, so a retry after a
    /// lost answer is recognised and never applied twice.</summary>
    public string? RequestId { get; init; }
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

    /// <summary>Only supporting evidence (2026-11-25): every piece is a task's continuation line (implementation detail, «As built»). It shows a related
    /// area; it never decides the primary one.</summary>
    public bool SupportingOnly => Basis == StoryLinkBasis.Structural && Evidence.Count > 0 && Evidence.All(e => e.Kind is StoryTaskRequirement.TaskDetail or StoryTaskRequirement.PlanMention);

    /// <summary>The relationship's role for the story (2026-11-25): a reviewer's decision, else derived where the evidence shows it (cross-cutting
    /// breadth, supporting-only evidence), else <see cref="StoryLinkRole.Unknown"/>.</summary>
    public StoryLinkRole Role { get; init; }
    /// <summary>The role was set by a reviewer.</summary>
    public bool RoleReviewed { get; init; }

    /// <summary>
    /// Evidence that the story is responsible for the requirement, not only that it mentions it: the story's own text, one of its tagged tasks, its
    /// tasks.md phase goal, or a person's link. A requirement named only inside an acceptance scenario's text ("unless the counting option in
    /// FR-009 is on") or only in supporting lines is a real relationship with an unknown role; it never decides the primary area by itself.
    /// </summary>
    public bool ResponsibilityEvidence => Evidence.Any(e => e.Kind is "story" or "manual" or "confirmed" or "plan" or StoryTaskRequirement.TaskLine or StoryTaskRequirement.PhaseGoal);

}

/// <param name="Strongest">The strongest basis of the area's requirements for this story.</param>
public sealed record AreaEvidence(string Area, IReadOnlyList<string> RequirementIds, StoryLinkBasis Strongest)
{
    /// <summary>The area is a cross-cutting control: many stories link to it (see <see cref="StoryGroupingEngine.CrossCuttingAreas"/>).</summary>
    public bool CrossCutting { get; init; }
    /// <summary>Only supporting evidence (task details) points here.</summary>
    public bool SupportingOnly { get; init; }
}

/// <summary>
/// The grouping state of a story (navigation metadata only; never a readiness criterion):
/// <see cref="Confirmed"/> and <see cref="Suggested"/> are «Gruppert» (by a person / by clear source evidence); <see cref="NeedsReview"/> «Trenger
/// gjennomgang» (one possible area, too little or only cross-cutting evidence); <see cref="NeedsGrouping"/> «Flere mulige grupper»;
/// <see cref="NoRequirements"/> and <see cref="OnlySuggestions"/> are «Uten gruppe» with their reason.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GroupingState { Suggested, NeedsGrouping, NoRequirements, OnlySuggestions, Confirmed, NeedsReview }

public sealed record StoryGroupingProfile(string StoryKey, IReadOnlyList<StoryRequirementRelation> Requirements, IReadOnlyList<AreaEvidence> Areas,
    string? SuggestedArea, IReadOnlyList<string> SecondaryAreas, GroupingState State, string Explanation, string? PrimaryArea, string? GroupId,
    string? SuggestedGroupId, bool SuggestionChanged)
{
    /// <summary>The possible primary areas a reviewer chooses between (tied areas, the one thin or cross-cutting candidate); empty when settled.</summary>
    public IReadOnlyList<string> Candidates { get; init; } = [];
    /// <summary>A person decided the story stays under «Uten gruppe».</summary>
    public bool KeptUngrouped { get; init; }
    /// <summary>The placement names a business group that no longer exists (deleted elsewhere): the story is under «Uten gruppe» until placed again.</summary>
    public string? MissingGroupId { get; init; }
    /// <summary>A person's placement exists and the current suggestion differs from the one they decided against: «Trenger gjennomgang» while it stays.</summary>
    public bool NeedsReviewFlag => SuggestionChanged && State == GroupingState.Confirmed;
}

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
        foreach (var t in story.TaskRequirements) Add(t.RequirementId, StoryLinkBasis.Structural, new(t.Kind ?? StoryTaskRequirement.TaskLine, $"{t.TasksPath}:{t.Line}", t.TaskText));
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

    /// <summary>Grouping evidence that may decide a primary area: established, not a cross-cutting requirement, not only supporting, in a known area.</summary>
    /// <summary>Grouping evidence that may decide a primary area (2026-11-25): a reviewer's Primary role; otherwise an unreviewed relationship with
    /// responsibility evidence that is not cross-cutting or supporting-only. Supporting, cross-cutting and contextual roles never decide.</summary>
    private static bool Deciding(StoryRequirementRelation r) => r.Established && r.Area != UnknownArea && (r.RoleReviewed
        ? r.Role == StoryLinkRole.Primary
        : !r.CrossCutting && !r.SupportingOnly && r.ResponsibilityEvidence);

    /// <summary>
    /// Cross-cutting functional areas (2026-11-25): an area that deciding evidence of at least three stories, and of at least half of the stories with
    /// established links, points to is a control many stories pass through (SkoleService: «Classification» — US1, US2, US3 and US8 name gradering
    /// requirements). Breadth alone; no keyword. Such an area is related to those stories, never decides their primary area on its own. A person may
    /// also declare an area cross-cutting (<see cref="StoryGroupingDocument.CrossCutting"/>).
    /// </summary>
    public static HashSet<string> CrossCuttingAreas(IReadOnlyList<IReadOnlyList<StoryRequirementRelation>> perStory, IEnumerable<string> declared)
    {
        var result = new HashSet<string>(declared, StringComparer.OrdinalIgnoreCase);
        var linked = perStory.Count(r => r.Any(x => x.Established));
        foreach (var g in perStory.SelectMany(r => r.Where(Deciding).Select(x => x.Area!).Distinct(StringComparer.Ordinal)).GroupBy(a => a, StringComparer.Ordinal))
            if (g.Count() >= 3 && g.Count() * 2 >= linked) result.Add(g.Key);
        return result;
    }

    /// <summary>
    /// The grouping profile of every story, each decided on its own relations only (no transitive clustering). Deterministic, from source evidence:
    /// <list type="number">
    /// <item>No relation: «Uten gruppe» (no requirements). Only suggestions: «Uten gruppe» (a suggestion never decides).</item>
    /// <item>Deciding evidence: established, not cross-cutting, not only a task detail, in a known area outside the cross-cutting areas.</item>
    /// <item>No deciding evidence: the remaining known areas are candidates — one: «Trenger gjennomgang»; several: «Flere mulige grupper».</item>
    /// <item>A tie for most distinct deciding requirements: «Flere mulige grupper» (never an arbitrary pick).</item>
    /// <item>A single winner resting on one task-line requirement while the story touches other areas: «Trenger gjennomgang» (too little to group
    /// silently). Otherwise the winner is suggested.</item>
    /// </list>
    /// A person's placement wins (and is flagged when the suggestion has changed since); a placement naming a deleted group falls back to «Uten gruppe».
    /// </summary>
    public static List<StoryGroupingProfile> Profiles(IReadOnlyList<StoryInputs> inputs, RequirementSectionMap sections, StoryGroupingDocument? doc)
    {
        var raw = inputs.Select(i => Relations(i, sections, doc)).ToList();
        var cross = CrossCutting([.. raw], doc?.CrossCutting ?? []);
        var reviewed = (doc?.Roles ?? []).GroupBy(d => (d.StoryKey, RequirementReferenceParser.Normalize(d.RequirementId)))
            .ToDictionary(g => g.Key, g => g.Last().Role);
        var relations = raw.Select((r, i) => r.Select(x =>
        {
            var withCross = cross.Contains(x.RequirementId) ? x with { CrossCutting = true } : x;
            return reviewed.TryGetValue((inputs[i].Story.Key, x.RequirementId), out var role) && role != StoryLinkRole.Unknown
                ? withCross with { Role = role, RoleReviewed = true }
                : withCross with { Role = withCross.CrossCutting ? StoryLinkRole.CrossCutting : withCross.SupportingOnly ? StoryLinkRole.Supporting : StoryLinkRole.Unknown };
        }).ToList()).ToList();
        var crossAreas = CrossCuttingAreas([.. relations], (doc?.CrossCutting ?? []).Where(c => sections.Sections.Any(s => s.Title.Equals(c, StringComparison.OrdinalIgnoreCase))));
        relations = [.. relations.Select(r => r.Select(x => !x.RoleReviewed && x.Role == StoryLinkRole.Unknown && x.Area is { } a && crossAreas.Contains(a) ? x with { Role = StoryLinkRole.CrossCutting } : x).ToList())];
        var linkedStories = relations.Count(r => r.Any(x => x.Established));
        var holders = relations.SelectMany(r => r.Where(Deciding).Select(x => x.Area!).Distinct(StringComparer.Ordinal)).GroupBy(a => a, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var groups = (doc?.Groups ?? []).OrderBy(g => g.Order).ToList();
        var result = new List<StoryGroupingProfile>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var story = inputs[i].Story;
            var rel = relations[i];
            var areas = rel.Where(r => r.Established).GroupBy(r => r.Area ?? UnknownArea)
                .Select(g => new AreaEvidence(g.Key, [.. g.Select(r => r.RequirementId)], g.Min(r => r.Basis))
                    { CrossCutting = crossAreas.Contains(g.Key), SupportingOnly = g.All(r => r.SupportingOnly) })
                .OrderByDescending(a => a.RequirementIds.Count).ThenBy(a => a.Strongest).ThenBy(a => a.Area, StringComparer.Ordinal).ToList();
            var deciding = rel.Where(r => Deciding(r) && !crossAreas.Contains(r.Area!)).GroupBy(r => r.Area!)
                .Select(g => (Area: g.Key, Ids: g.Select(r => r.DisplayId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), Strongest: g.Min(r => r.Basis)))
                .OrderByDescending(a => a.Ids.Count).ThenBy(a => a.Strongest).ThenBy(a => a.Area, StringComparer.Ordinal).ToList();
            string? suggested = null; GroupingState state; string why; List<string> candidates = [];
            string Ids(string area) => string.Join(", ", rel.Where(r => r.Established && r.Area == area).Select(r => r.DisplayId));
            string CrossNote(string area) => holders.TryGetValue(area, out var n) ? $"«{area}» er knyttet til {n} av {linkedStories} brukerhistorier (tverrgående)" : $"«{area}» er tverrgående";
            if (rel.Count == 0)
            {
                state = GroupingState.NoRequirements;
                why = "Ingen tilknyttede funksjonelle krav. Verken brukerhistorien, scenarioene, oppgavene eller en person knytter den til et krav.";
            }
            else if (!rel.Any(r => r.Established))
            {
                state = GroupingState.OnlySuggestions;
                candidates = [.. rel.Select(r => r.Area).OfType<string>().Where(a => a != UnknownArea).Distinct(StringComparer.Ordinal)];
                why = $"Bare foreslåtte kravkoblinger ({string.Join(", ", rel.Select(r => r.DisplayId))}). Et forslag avgjør aldri grupperingen; bekreft en kobling eller velg gruppe.";
            }
            else if (deciding.Count == 0)
            {
                var crossOnly = rel.Where(r => Deciding(r) && crossAreas.Contains(r.Area!)).Select(r => r.Area!).Distinct(StringComparer.Ordinal).ToList();
                candidates = [.. crossOnly.Concat(areas.Select(a => a.Area).Where(a => a != UnknownArea)).Distinct(StringComparer.Ordinal)];
                state = candidates.Count > 1 ? GroupingState.NeedsGrouping : GroupingState.NeedsReview;
                why = candidates.Count == 0
                    ? "De tilknyttede kravene står ikke under noen funksjonsdel i spesifikasjonen."
                    : crossOnly.Count > 0
                        ? $"Kravene peker bare mot {string.Join(" og ", crossOnly.Select(CrossNote))}: {string.Join("; ", crossOnly.Select(a => $"{a}: {Ids(a)}"))}. Kilden knytter ikke brukerhistorien til et eget område; bekreft området eller velg et annet."
                        : rel.Any(r => r.Established && !r.ResponsibilityEvidence && !r.SupportingOnly && r.Role == StoryLinkRole.Unknown)
                            ? $"Kravene er bare nevnt i akseptansescenarioer eller støttende linjer ({string.Join(", ", candidates.Select(c => $"«{c}» ({Ids(c)})"))}); en henvisning viser at kravet er relevant, ikke at historien eier det. Bekreft rollen eller velg område."
                            : $"Bare støttende henvisninger (oppgavedetaljer eller tverrgående krav) peker mot {string.Join(", ", candidates.Select(c => $"«{c}» ({Ids(c)})"))}; de avgjør ikke et hovedområde alene.";
            }
            else if (deciding.Count > 1 && deciding[0].Ids.Count == deciding[1].Ids.Count)
            {
                state = GroupingState.NeedsGrouping;
                var tied = deciding.TakeWhile(d => d.Ids.Count == deciding[0].Ids.Count).ToList();
                candidates = [.. tied.Select(d => d.Area)];
                why = $"Flere mulige grupper med like mye støtte: {string.Join(" og ", tied.Select(d => $"«{d.Area}» ({string.Join(", ", d.Ids)})"))}. Grupperingen trenger en person.";
            }
            else
            {
                var top = deciding[0];
                // Other areas the story's own (non-cross-cutting) evidence touches; a cross-cutting requirement or area never makes a suggestion thin.
                var others = areas.Where(a => a.Area != top.Area && a.Area != UnknownArea && !a.CrossCutting
                    // An incidental reference (role unknown, no responsibility evidence) neither decides nor makes a suggestion thin (2026-11-25).
                    && rel.Any(r => r.Established && !r.CrossCutting && r.Area == a.Area && (Deciding(r) || r.SupportingOnly))).Select(a => a.Area).ToList();
                if (top.Ids.Count == 1 && top.Strongest == StoryLinkBasis.Structural && others.Count > 0)
                {
                    state = GroupingState.NeedsReview;
                    candidates = [top.Area];
                    why = $"Mulig område «{top.Area}» bygger bare på {top.Ids[0]} fra én oppgave, mens brukerhistorien også berører {string.Join(", ", others.Select(o => $"«{o}»"))}. Bekreft området eller velg et annet.";
                }
                else
                {
                    suggested = top.Area; state = GroupingState.Suggested;
                    why = $"Foreslått under «{top.Area}» fordi {string.Join(", ", top.Ids)} står i denne delen av spesifikasjonen" +
                          (deciding.Count > 1 ? $" ({top.Ids.Count} mot {deciding[1].Ids.Count} for «{deciding[1].Area}»)." : ".") +
                          (others.Count > 0 ? $" Også relatert: {string.Join(", ", others.Select(o => $"«{o}»"))}." : "");
                }
            }
            var placement = doc?.Placements.LastOrDefault(p => p.StoryKey == story.Key);
            var missingGroup = placement?.GroupId is { } gid && groups.All(g => g.Id != gid) ? gid : null;
            var placedGroup = missingGroup is null ? placement?.GroupId : null;
            var kept = placement?.KeepUngrouped == true;
            var primary = kept ? null : placement?.PrimaryArea ?? suggested;
            var suggestedGroup = primary is null ? null : groups.Where(g => g.FunctionalAreas.Contains(primary, StringComparer.OrdinalIgnoreCase)).Select(g => g.Id).FirstOrDefault();
            var changed = placement is not null && !string.Equals(placement.SuggestedAtDecision ?? "", suggested ?? "", StringComparison.Ordinal);
            if (placement is not null && (placedGroup is not null || placement.PrimaryArea is not null || kept)) state = GroupingState.Confirmed;
            if (missingGroup is not null && state != GroupingState.Confirmed)
                why = $"Den valgte gruppen finnes ikke lenger. {why}";
            if (kept) why = $"Bevisst uten gruppe (besluttet av {placement!.By}). {why}";
            result.Add(new StoryGroupingProfile(story.Key, rel, areas, suggested,
                [.. areas.Select(a => a.Area).Where(a => a != primary)], state, why, primary, kept ? null : placedGroup ?? suggestedGroup, suggestedGroup, changed)
                { Candidates = candidates, KeptUngrouped = kept, MissingGroupId = missingGroup });
        }
        return result;
    }

    /// <summary>Placements of stories the current source does not have (removed, renamed or the source is temporarily missing). Kept, never deleted:
    /// they apply again when the story returns.</summary>
    public static List<StoryPlacement> OrphanPlacements(StoryGroupingDocument? doc, IReadOnlyList<StoryInputs> inputs)
    {
        var keys = inputs.Select(i => i.Story.Key).ToHashSet(StringComparer.Ordinal);
        return [.. (doc?.Placements ?? []).Where(p => !keys.Contains(p.StoryKey))];
    }

    // ── Applying review operations (server side; also used by the browser for the preview) ───────────────────────────────────────────

    /// <summary>The Norwegian label of a relationship role.</summary>
    public static string RoleLabel(StoryLinkRole role) => role switch
    {
        StoryLinkRole.Primary => "Primær atferd",
        StoryLinkRole.Supporting => "Støttende",
        StoryLinkRole.CrossCutting => "Tverrgående kontroll",
        StoryLinkRole.Contextual => "Kontekst",
        _ => "Uavklart rolle",
    };

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
        var roles = doc.Roles.ToList();
        var kinds = (doc.TestKinds ?? []).ToList();
        var testLinks = (doc.TestLinks ?? []).ToList();
        var scenarioTypes = (doc.ScenarioTypes ?? []).ToList();
        var preparation = (doc.Preparation ?? []).ToList();
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
                    var members = placements.Select((p, index) => (p, index)).Where(x => x.p.GroupId == g.Id).Select(x => x.index).ToList();
                    BusinessGroup? into = null;
                    if (members.Count > 0)
                    {
                        if (op.TargetGroupId is { Length: > 0 } target)
                        {
                            into = Group(target);
                            if (into.Id == g.Id) throw new GroupingRefusal("same-group", "Brukerhistoriene kan ikke flyttes til gruppen som slettes.");
                        }
                        else if (!op.ReleaseMembers)
                            throw new GroupingRefusal("group-not-empty", $"«{g.Name}» har {members.Count} brukerhistorier plassert i seg. Velg en annen gruppe for dem, eller la dem stå uten gruppe.");
                        foreach (var k in members) placements[k] = placements[k] with { GroupId = into?.Id, By = actor, At = now };
                    }
                    groups.Remove(g);
                    Log("Slettet gruppe", $"«{g.Name}»{(members.Count == 0 ? "" : into is null ? $" ({members.Count} brukerhistorier står nå uten gruppe)" : $" ({members.Count} brukerhistorier flyttet til «{into.Name}»)")} — bare grupperingen; ingen brukerhistorier, krav, scenarioer eller testtilfeller er berørt");
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
                    var placed = (k >= 0 ? placements[k] : new StoryPlacement { StoryKey = s }) with { GroupId = target?.Id, SuggestedAtDecision = op.SuggestedArea, KeepUngrouped = false, By = actor, At = now };
                    if (k >= 0) placements[k] = placed; else placements.Add(placed);
                    Log("Flyttet brukerhistorie", $"{s}: {before ?? "forslag"} → {target?.Name ?? "forslag"} (krav og scenarioer er uendret)");
                    break;
                }
                case StoryGroupingOperationKind.SetPrimaryArea:
                {
                    var s = Story(op.StoryKey);
                    var k = placements.FindLastIndex(p => p.StoryKey == s);
                    var placed = (k >= 0 ? placements[k] : new StoryPlacement { StoryKey = s }) with { PrimaryArea = string.IsNullOrWhiteSpace(op.PrimaryArea) ? null : op.PrimaryArea.Trim(), SuggestedAtDecision = op.SuggestedArea, KeepUngrouped = false, By = actor, At = now };
                    if (k >= 0) placements[k] = placed; else placements.Add(placed);
                    Log("Endret primært funksjonsområde", $"{s}: {placed.PrimaryArea ?? "forslaget"} (kravkoblinger og kravenes kildedeler er uendret)");
                    break;
                }
                case StoryGroupingOperationKind.SetLinkRole:
                {
                    var s = Story(op.StoryKey); var r = Req(op.RequirementId, "rolle");
                    var role = op.Role ?? StoryLinkRole.Unknown;
                    roles.Add(new StoryLinkRoleDecision { StoryKey = s, RequirementId = r, Role = role, Reason = string.IsNullOrWhiteSpace(op.Reason) ? null : op.Reason.Trim(), By = actor, At = now });
                    Log("Endret kravrolle", $"{s} → {op.DisplayId ?? r}: {RoleLabel(role)} (koblingen og kildebeviset er uendret)");
                    break;
                }
                case StoryGroupingOperationKind.SetScenarioTestKind:
                {
                    var id = string.IsNullOrWhiteSpace(op.ScenarioId) ? throw new GroupingRefusal("missing-scenario", "Mangler scenario.") : op.ScenarioId.Trim();
                    kinds.Add(new ScenarioTestKindDecision { ScenarioId = id, StoryKey = string.IsNullOrWhiteSpace(op.StoryKey) ? null : op.StoryKey,
                        Kind = op.TestKind, Reason = string.IsNullOrWhiteSpace(op.Reason) ? null : op.Reason.Trim(), By = actor, At = now });
                    Log("Endret testtype", $"{op.DisplayId ?? id}: {TestKindLabel(op.TestKind)} (scenarioet, testtilfellene og kilden er uendret)");
                    break;
                }
                case StoryGroupingOperationKind.SetScenarioTestLink:
                {
                    var scenario = string.IsNullOrWhiteSpace(op.ScenarioId) ? throw new GroupingRefusal("missing-scenario", "Mangler scenario.") : op.ScenarioId.Trim();
                    var test = string.IsNullOrWhiteSpace(op.TestId) ? throw new GroupingRefusal("missing-test", "Mangler test.") : op.TestId.Trim();
                    var review = op.LinkReview ?? throw new GroupingRefusal("missing-review", "Mangler vurdering.");
                    var extent = review == TestLinkReview.Confirmed ? op.Extent ?? TechnicalCoverageExtent.NotAssessed : TechnicalCoverageExtent.NotAssessed;
                    // A rejected or cleared link has no extent; «hele scenarioet» is only ever a reviewer's assessment of a confirmed link.
                    if (review != TestLinkReview.Confirmed && op.Extent is TechnicalCoverageExtent.Partial or TechnicalCoverageExtent.Full)
                        throw new GroupingRefusal("extent-needs-confirmed-link", "Dekningsgrad kan bare vurderes for en bekreftet kobling.");
                    testLinks.Add(new ScenarioTestLinkDecision { ScenarioId = scenario, TestId = test, Review = review, Extent = extent,
                        TestFingerprint = string.IsNullOrWhiteSpace(op.TestFingerprint) ? null : op.TestFingerprint, ScenarioFingerprint = string.IsNullOrWhiteSpace(op.ScenarioFingerprint) ? null : op.ScenarioFingerprint,
                        Note = string.IsNullOrWhiteSpace(op.Reason) ? null : op.Reason.Trim(), By = actor, At = now });
                    Log("Vurderte automatisk test", $"{op.DisplayId ?? scenario}: {TestLinkLabel(review, extent)} (testen, scenarioet og testtilfellene er uendret)");
                    break;
                }
                case StoryGroupingOperationKind.SetScenarioType:
                {
                    var scenario = string.IsNullOrWhiteSpace(op.ScenarioId) ? throw new GroupingRefusal("missing-scenario", "Mangler scenario.") : op.ScenarioId.Trim();
                    // Quality attributes belong to non-functional and mixed scenarios only; a functional or unclassified decision carries none.
                    var qualities = op.ScenarioType is ScenarioRequirementType.NonFunctional or ScenarioRequirementType.Mixed ? (op.Qualities ?? []).Distinct().ToList() : [];
                    scenarioTypes.Add(new ScenarioTypeDecision(scenario, string.IsNullOrWhiteSpace(op.StoryKey) ? null : op.StoryKey, op.ScenarioType, qualities,
                        string.IsNullOrWhiteSpace(op.Reason) ? null : op.Reason.Trim(), actor, now, string.IsNullOrWhiteSpace(op.ScenarioFingerprint) ? null : op.ScenarioFingerprint));
                    Log("Vurderte scenariotype", $"{op.DisplayId ?? scenario}: {(op.ScenarioType is { } t ? ScenarioTypeClassifier.Label(t) : "tilbake til forslaget")}"
                        + (qualities.Count > 0 ? $" ({string.Join(", ", qualities.Select(ScenarioTypeClassifier.Label))})" : "") + " (scenarioet, testtypen og testtilfellene er uendret)");
                    break;
                }
                case StoryGroupingOperationKind.SetScenarioPreparation:
                {
                    var scenario = string.IsNullOrWhiteSpace(op.ScenarioId) ? throw new GroupingRefusal("missing-scenario", "Mangler scenario.") : op.ScenarioId.Trim();
                    var criterion = op.Criterion ?? throw new GroupingRefusal("missing-criterion", "Mangler punkt.");
                    var evidence = string.IsNullOrWhiteSpace(op.Evidence) ? null : op.Evidence.Trim();
                    var reason = string.IsNullOrWhiteSpace(op.Reason) ? null : op.Reason.Trim();
                    var before = ScenarioPreparationEngine.Current(preparation, scenario).GetValueOrDefault(criterion);
                    // «Ikke relevant» is checked with the reason the decision will carry (a new one, or the one already given).
                    if (ScenarioPreparationEngine.Validate(op.Applicability, op.Evaluation, evidence, reason ?? (op.Applicability == CriterionApplicability.NotApplicable ? before?.Reason : null)) is { } refused)
                        throw new GroupingRefusal("preparation-invalid", refused);
                    if (op.Applicability is null && op.Evaluation is null && evidence is null && reason is null) throw new GroupingRefusal("preparation-empty", "Ingen endring å lagre.");
                    preparation.Add(new ScenarioPreparationDecision { ScenarioId = scenario, Criterion = criterion, Applicability = op.Applicability, Evaluation = op.Evaluation,
                        Evidence = evidence, Reason = reason, ScenarioFingerprint = string.IsNullOrWhiteSpace(op.ScenarioFingerprint) ? null : op.ScenarioFingerprint, By = actor, At = now });
                    string Was(CriterionApplicability? a, CriterionEvaluation? e) => a == CriterionApplicability.NotApplicable ? "Ikke relevant"
                        : $"{(a is { } x ? ScenarioPreparationEngine.Label(x) : "—")} · {(e is { } y ? ScenarioPreparationEngine.Label(y) : "—")}";
                    var after = ScenarioPreparationEngine.Current(preparation, scenario)[criterion];
                    Log("Klargjorde scenario", $"{op.DisplayId ?? scenario}: {ScenarioPreparationEngine.Label(criterion)} {Was(before?.Applicability, before?.Evaluation)} → {Was(after.Applicability, after.Evaluation)}"
                        + (reason is null ? "" : $" ({reason})") + " (scenarioet, testtilfellene, godkjenninger og resultater er uendret)");
                    break;
                }
                case StoryGroupingOperationKind.LeaveUngrouped:
                {
                    var s = Story(op.StoryKey);
                    var k = placements.FindLastIndex(p => p.StoryKey == s);
                    var placed = (k >= 0 ? placements[k] : new StoryPlacement { StoryKey = s }) with
                        { GroupId = null, PrimaryArea = null, KeepUngrouped = true, SuggestedAtDecision = op.SuggestedArea, By = actor, At = now };
                    if (k >= 0) placements[k] = placed; else placements.Add(placed);
                    Log("Beholdt uten gruppe", $"{s}{(string.IsNullOrWhiteSpace(op.Reason) ? "" : $": {op.Reason.Trim()}")} (brukerhistorien, scenarioene og testtilfellene er uendret)");
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
        return doc with { Revision = doc.Revision + 1, Groups = groups, Placements = placements, Decisions = decisions, History = history, Roles = roles, TestKinds = kinds, TestLinks = testLinks, ScenarioTypes = scenarioTypes, Preparation = preparation };
    }

    /// <summary>Every scenario's current type decision (source identity → decision); returned-to-proposal ones left out.</summary>
    public static Dictionary<string, ScenarioTypeDecision> ScenarioTypeDecisions(StoryGroupingDocument? doc) =>
        (doc?.ScenarioTypes ?? []).GroupBy(d => d.ScenarioId, StringComparer.Ordinal).Select(g => g.Last()).Where(d => d.Type is not null)
            .ToDictionary(d => d.ScenarioId, StringComparer.Ordinal);

    /// <summary>The reviewer's current manual/technical decision for a scenario (null = none, or returned to the suggestion).</summary>
    public static ScenarioTestKindDecision? TestKindDecision(StoryGroupingDocument? doc, string scenarioId) =>
        doc?.TestKinds?.LastOrDefault(k => string.Equals(k.ScenarioId, scenarioId, StringComparison.Ordinal)) is { Kind: not null } d ? d : null;

    /// <summary>Every scenario's current manual/technical decision in this document (scenario id → kind); returned-to-suggestion ones left out.</summary>
    public static Dictionary<string, ScenarioTestKind> TestKindDecisions(StoryGroupingDocument? doc) =>
        (doc?.TestKinds ?? []).GroupBy(k => k.ScenarioId, StringComparer.Ordinal).Select(g => g.Last()).Where(d => d.Kind is not null)
            .ToDictionary(d => d.ScenarioId, d => d.Kind!.Value, StringComparer.Ordinal);

    /// <summary>Every current test-link decision (key "scenarioId|testId"); cleared ones are left out.</summary>
    public static Dictionary<string, ScenarioTestLinkDecision> TestLinkDecisions(StoryGroupingDocument? doc) =>
        (doc?.TestLinks ?? []).GroupBy(d => TestLinkKey(d.ScenarioId, d.TestId), StringComparer.Ordinal).Select(g => g.Last()).Where(d => d.Review != TestLinkReview.Cleared)
            .ToDictionary(d => TestLinkKey(d.ScenarioId, d.TestId), StringComparer.Ordinal);

    public static string TestLinkKey(string scenarioId, string testId) => $"{scenarioId}|{testId}";

    public static string TestLinkLabel(TestLinkReview review, TechnicalCoverageExtent extent) => review switch
    {
        TestLinkReview.Rejected => "kobling avvist",
        TestLinkReview.Cleared => "vurdering fjernet",
        _ => extent switch { TechnicalCoverageExtent.Full => "bekreftet, dekker hele scenarioet", TechnicalCoverageExtent.Partial => "bekreftet, dekker deler av scenarioet", _ => "kobling bekreftet" },
    };

    public static string TestKindLabel(ScenarioTestKind? kind) => kind switch
    {
        ScenarioTestKind.Manual => "Manuell",
        ScenarioTestKind.Technical => "Teknisk",
        ScenarioTestKind.Both => "Begge",
        _ => "automatisk forslag",
    };
}
