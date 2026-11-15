using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// Pure presentation rules for Test Plan Suggestions: areas, filters, labels, the save request built from the working copy (human-owned parts only),
/// and the evidence-backed next steps the Manual Testing focus panel shows on Dashboard and Recommended Workflow. No I/O.
/// </summary>
public static class TestPlanSuggestionsPresentation
{
    public const string Route = "test-plan-suggestions";

    /// <param name="Purpose">One sentence: what this step of test preparation is for (shown under the step list).</param>
    public sealed record Area(string Id, string Label, string Purpose = "");

    /// <summary>The four guided steps, in order. Saved snapshots, delivery comparison and the Azure DevOps preparation stay available as secondary areas.</summary>
    public static readonly IReadOnlyList<Area> Areas =
    [
        new("choose", "Choose scenarios", "Find what to test in this project's evidence and choose the scenarios to prepare."),
        new("prepare", "Prepare test cases", "Write readable test cases: preconditions, steps with an expected result each, and your review decision."),
        new("testability", "Check testability", "See which cases still need Test Data, a clarification or a prerequisite before someone can run them."),
        new("save", "Save & export", "Name the plan, save an immutable snapshot for a delivery and prepare it for Azure DevOps."),
    ];

    /// <summary>Secondary areas (not steps): snapshot history, delivery comparison and the Azure DevOps preparation.</summary>
    public static readonly IReadOnlyList<Area> MoreAreas =
    [
        new("snapshots", "Saved snapshots", "Every saved snapshot of this project's plans; a snapshot never changes."),
        new("compare", "Compare deliveries", "See what changed between two snapshots and what to retest."),
        new("export", "Prepare for Azure DevOps", "Export a snapshot as a reviewable proposal; nothing is written to Azure DevOps."),
    ];

    /// <summary>"Step 2 of 4 · Write readable …" for a step; the purpose alone for a secondary area. Pure.</summary>
    public static string StepLine(string area)
    {
        var index = Areas.ToList().FindIndex(a => a.Id == area);
        if (index >= 0) return $"Step {index + 1} of {Areas.Count} · {Areas[index].Purpose}";
        return MoreAreas.FirstOrDefault(a => a.Id == area)?.Purpose ?? "";
    }

    /// <summary>The area for a link; earlier area ids (discover, plan, review, data) map to their step so old links keep working.</summary>
    public static string AreaOrDefault(string? area) => area switch
    {
        "discover" => "choose",
        "plan" or "review" => "prepare",
        "data" => "testability",
        _ when Areas.Any(a => a.Id == area) || MoreAreas.Any(a => a.Id == area) => area!,
        _ => "choose",
    };

    /// <summary>The link from System Understanding "What to test": the guide and the selected scenario ids (never an approval).</summary>
    public static string PrepareLink(string guideKey, IEnumerable<string> scenarioIds) =>
        $"{Route}?guide={Uri.EscapeDataString(guideKey)}&scenarios={Uri.EscapeDataString(string.Join(',', scenarioIds))}";

    public static IReadOnlyList<string> ParseScenarios(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).Take(300)];

    // ── Filters ──────────────────────────────────────────────────────────────────────────────────────────────

    public sealed record Filter(string Query = "", string? Stage = null, string? GuideState = null, string? Requirement = null, string? Gap = null);

    public static IReadOnlyList<ScenarioCandidate> Apply(IEnumerable<ScenarioCandidate> candidates, Filter f) => [.. candidates.Where(c =>
        (string.IsNullOrWhiteSpace(f.Query) || Contains(c.Title, f.Query) || Contains(c.Objective, f.Query) || c.Source.ScenarioId.Contains(f.Query.Trim(), StringComparison.OrdinalIgnoreCase))
        && (f.Stage is null || c.StageId == f.Stage)
        && (f.GuideState is null || c.GuideState == f.GuideState)
        && (f.Requirement is null || c.Requirements.Any(r => r.RuleId == f.Requirement))
        && (f.Gap is null || f.Gap switch { "expected" => !c.HasExpected, "data" => !c.HasTestData, "observe" => !c.HasObservation, "linked" => c.Correlations.Count > 0, _ => true }))];

    private static bool Contains(string text, string query) => text.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase);

    // ── Labels ───────────────────────────────────────────────────────────────────────────────────────────────

    public sealed record Label(string Text, string Tone);

    public static Label GuideState(string state) => state switch
    {
        "NeedsClarification" => new("Needs clarification", "warn"),
        "ExistingDefinition" => new("Test definition exists", "info"),
        "Executed" => new("Executed", "info"),
        "Verified" => new("Verified", "ok"),
        _ => new("Suggested", "muted"),
    };

    public static Label Review(TestCaseReviewState state) => state switch
    {
        TestCaseReviewState.UnderReview => new("Under review", "info"),
        TestCaseReviewState.NeedsClarification => new("Needs clarification", "warn"),
        TestCaseReviewState.Accepted => new("Accepted for preparation", "ok"),
        TestCaseReviewState.Excluded => new("Excluded", "muted"),
        TestCaseReviewState.Superseded => new("Superseded (left the source)", "muted"),
        _ => new("Generated — not reviewed", "muted"),
    };

    public static Label Expected(TestCaseDraft c) => c.Edits.ExpectedResult is not null ? new("Clarified by a reviewer", "ok") : c.Generated.ExpectedBasis switch
    {
        ExpectedResultBasis.ReviewedGuide => new("From the published guide", "ok"),
        ExpectedResultBasis.UnreviewedGuide => new("From an unreviewed guide draft", "warn"),
        ExpectedResultBasis.NeedsClarification => new("NEEDS CLARIFICATION", "bad"),
        ExpectedResultBasis.Specification when c.Generated.ExpectedResult is not null => new("Stated in the project's documentation — confirm by accepting", "info"),
        ExpectedResultBasis.Contract when c.Generated.ExpectedResult is not null => new("Documented in the API contract — confirm by accepting", "info"),
        ExpectedResultBasis.SourceDerived when c.Generated.ExpectedResult is not null => new("Proposed from source code — confirm or clarify", "warn"),
        _ => new("NEEDS CLARIFICATION — none in the source", "bad"),
    };

    public static Label Fact(TestabilityFact fact) => fact switch
    {
        TestabilityFact.Known => new("Known", "ok"),
        TestabilityFact.NotVerified => new("Not verified here", "muted"),
        TestabilityFact.NeedsAssistance => new("Needs technical assistance", "warn"),
        _ => new("Missing", "bad"),
    };

    public static string BaselineState(string code) => code switch
    {
        "BASELINE_SELECTED" => "analysis baseline selected",
        "SINGLE_SNAPSHOT" => "one snapshot",
        "IDENTICAL_CONTENT" => "identical imports",
        "BASELINE_NOT_SELECTED" => "BASELINE_NOT_SELECTED — alternative versions, none chosen",
        "BASELINE_UNAVAILABLE" => "selected baseline no longer available",
        "NOT_IMPORTED" => "not imported",
        _ => code,
    };

    public static bool HasBaselineWarning(TestPlanSourceBasis? basis) => basis?.Roles.Any(r => r.State is "BASELINE_NOT_SELECTED" or "BASELINE_UNAVAILABLE") == true;

    // ── Working copy → save request ──────────────────────────────────────────────────────────────────────────

    /// <summary>Only human-owned parts are sent; the server keeps and rebuilds generated content itself.</summary>
    /// <param name="requirements">The project's current specification requirements; sent with a project plan so new manual links can be checked.</param>
    public static SaveTestPlanDraftRequest ToSaveRequest(TestPlanDraft draft, IEnumerable<string>? addScenarios = null, IReadOnlyCollection<ProjectRequirementOption>? requirements = null) => new()
    {
        RequirementCatalog = draft.ProjectKey is { } project && requirements is not null ? new ProjectRequirementCatalog { ProjectKey = project, Requirements = [.. requirements] } : null,
        BaseRevision = draft.Revision, PlanName = draft.PlanName, DeliveryLabel = draft.DeliveryLabel,
        ScenarioIds = [.. draft.Cases.Where(c => c.Review != TestCaseReviewState.Superseded).Select(c => c.Source.ScenarioId).Concat(addScenarios ?? []).Distinct(StringComparer.Ordinal)],
        Suites = draft.Suites,
        Cases = [.. draft.Cases.Select(c => new TestCaseHumanPart
        {
            ScenarioId = c.Source.ScenarioId, SuiteId = c.SuiteId, Edits = c.Edits, Risk = c.Risk, TestDataAssetIds = [.. c.TestData.Select(d => d.AssetId)],
            Correlations = c.Correlations.Where(x => x.State != CorrelationState.Candidate).ToDictionary(x => x.Target.Key, x => x.State, StringComparer.Ordinal),
            ManualLinks = [.. c.Correlations.Where(x => x.Basis.StartsWith("Linked by a reviewer", StringComparison.Ordinal)).Select(x => x.Target.Key)],
            RequirementLinks = draft.ProjectKey is null ? null : [.. c.RequirementLinks],
        })],
    };

    public static TestPlanDraft WithCase(TestPlanDraft draft, string scenarioId, Func<TestCaseDraft, TestCaseDraft> change) =>
        draft with { Cases = [.. draft.Cases.Select(c => c.Source.ScenarioId == scenarioId ? change(c) : c)] };

    /// <summary>Steps as edited text (one per line) → list; identical to the generated steps means "no edit".</summary>
    public static List<string>? StepsEdit(TestCaseDraft c, string text)
    {
        var steps = text.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        return steps.SequenceEqual(c.Generated.Steps) ? null : steps;
    }

    public static string? TextEdit(string? generated, string? text) =>
        string.IsNullOrWhiteSpace(text) || string.Equals(text.Trim(), generated?.Trim(), StringComparison.Ordinal) ? null : text.Trim();

    // ── Focus (Dashboard / Recommended Workflow, Manual Testing view) ────────────────────────────────────────

    public sealed record PlanFact(string Id, string Label, int Count);

    public sealed record PlanFocus(IReadOnlyList<PlanFact> Facts, IReadOnlyList<FocusAction> Actions, TestPlanSnapshotSummary? Latest);

    /// <summary>
    /// Facts and next steps from persisted proposals only (one workspace call): suggestions to review, missing expected results, missing Test Data,
    /// testability blockers, latest snapshot, a pending snapshot or comparison, export. No percentages; nothing is "done" that is not stored.
    /// </summary>
    /// <summary>A Discover group: one of the guide's business-process stages (its own order and title), or "Other" for candidates without a stage.</summary>
    public sealed record StageGroup(string Id, string Title, IReadOnlyList<ScenarioCandidate> Items);

    /// <summary>Candidates grouped by the guide's stages in the guide's order; never an invented category. Empty groups are left out.</summary>
    public static IReadOnlyList<StageGroup> ByStage(IReadOnlyList<ScenarioCandidate> candidates, IReadOnlyList<TestPlanStage> stages)
    {
        var known = stages.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var groups = stages.Select(s => new StageGroup(s.Id, s.Title, [.. candidates.Where(c => c.StageId == s.Id)])).Where(g => g.Items.Count > 0).ToList();
        var other = candidates.Where(c => c.StageId is null || !known.Contains(c.StageId)).ToList();
        if (other.Count > 0) groups.Add(new StageGroup("other", "Other scenarios", other));
        return groups;
    }

    public static PlanFocus Focus(TestPlanWorkspace? ws, RequirementCoverageView? coverage = null)
    {
        var plan = PlanOnly(ws);
        if (coverage is not { Known: true } c) return plan; // unknown coverage adds no count (no denominator) and no action
        var facts = plan.Facts.ToList();
        facts.Add(new("req-reviewed", $"Requirements with reviewed scenario coverage (of {c.Requirements})", c.Reviewed));
        facts.Add(new("req-needs", "Requirements needing association review", c.NeedsReview));
        var actions = plan.Actions.ToList();
        var at = Math.Min(1, actions.Count);
        if (c.Associations.Any(a => a.InterpretationOutdated))
            actions.Insert(at, new("req-reinterpret", "Re-run relationship analysis", "A guide scenario changed after the last interpretation; its requirement associations are not verified for the new text.",
                RequirementCoveragePresentation.RelationshipReviewHref, FocusActionState.CanDoNow, "Ready now", null));
        if (c.NeedsReview > 0)
            actions.Insert(at, new("req-associations", "Review requirement/scenario associations", $"{c.NeedsReview} requirement(s) have only suggested scenario associations; nothing is counted until decided.",
                RequirementCoveragePresentation.RelationshipReviewHref, FocusActionState.CanDoNow, "Ready now", null));
        if (c.VerifiedNotReviewed > 0)
            actions.Add(new("req-scenarios", "Review linked scenarios", $"{c.VerifiedNotReviewed} requirement(s) are linked to a scenario no tester has accepted in its current revision.",
                $"{Route}?area=review", FocusActionState.CanDoNow, "Ready now", null));
        return new(facts, actions, plan.Latest);
    }

    private static PlanFocus PlanOnly(TestPlanWorkspace? ws)
    {
        if (ws is null || ws.GuideKey is null) return new([], [], null);
        var draft = ws.Drafts.OrderByDescending(d => d.UpdatedAt).FirstOrDefault();
        var latest = ws.Snapshots.OrderByDescending(s => s.CreatedAt).FirstOrDefault();
        var link = ws.ProjectKey is not null ? $"{Route}?project=1" : $"{Route}?guide={Uri.EscapeDataString(ws.GuideKey)}";
        var actions = new List<FocusAction>();
        if (draft is null && ws.ProjectKey is not null)
        {
            actions.Add(new("tps-review", "Discover test scenarios", "No test cases are prepared for this project yet. Discover scenarios from its evidence, or write cases by hand.",
                link + "&area=choose", FocusActionState.CanDoNow, "Ready now", null));
            return new([], actions, latest);
        }
        if (draft is null)
        {
            actions.Add(new("tps-review", "Review Test Plan Suggestions", $"{ws.Candidates.Count} scenario candidates from System Understanding; none prepared yet.", link + "&area=discover",
                FocusActionState.CanDoNow, "Ready now", null));
            return new([new("candidates", "Scenario candidates", ws.Candidates.Count)], actions, latest);
        }
        var draftLink = $"{link}&draft={draft.Id}";
        var unreviewed = draft.Cases - draft.Accepted;
        var facts = new List<PlanFact>
        {
            new("cases", "Prepared Test Case drafts", draft.Cases), new("accepted", "Accepted for preparation", draft.Accepted), new("expected", "Missing expected results", draft.MissingExpected),
            new("data", "Without Test Data", draft.MissingTestData), new("blocked", "Testability blockers", draft.Blocked),
        };
        if (unreviewed > 0) actions.Add(new("tps-review", "Review Test Plan Suggestions", $"{unreviewed} of {draft.Cases} drafts not accepted.", draftLink + "&area=review", FocusActionState.CanDoNow, "Ready now", null));
        if (draft.MissingExpected > 0) actions.Add(new("tps-expected", "Clarify expected outcomes", $"{draft.MissingExpected} drafts need clarification of the expected result.", draftLink + "&area=review", FocusActionState.CanDoNow, "Ready now", null));
        if (draft.MissingTestData > 0) actions.Add(new("tps-data", "Prepare missing Test Data", $"{draft.MissingTestData} drafts have no Test Data linked.", draftLink + "&area=data", FocusActionState.CanDoNow, "Ready now", null));
        var snapshotOfDraft = ws.Snapshots.Where(s => s.DraftId == draft.Id).OrderByDescending(s => s.Sequence).FirstOrDefault();
        if (snapshotOfDraft is null || snapshotOfDraft.DraftRevision < draft.Revision)
            actions.Add(new("tps-snapshot", "Save Test Plan snapshot", snapshotOfDraft is null ? "No snapshot of this proposal yet." : $"Changed since snapshot {snapshotOfDraft.Sequence}.", draftLink + "&area=snapshots", FocusActionState.CanDoNow, "Ready now", null));
        var ofDraft = ws.Snapshots.Count(s => s.DraftId == draft.Id);
        if (ofDraft >= 2) actions.Add(new("tps-compare", "Compare deliveries", $"{ofDraft} snapshots of this proposal.", draftLink + "&area=compare", FocusActionState.CanDoNow, "Ready now", null));
        if (latest is not null) actions.Add(new("tps-export", "Prepare Azure DevOps export", $"From snapshot {latest.Sequence} (a reviewable proposal, not written to Azure DevOps).", draftLink + "&area=export", FocusActionState.CanDoNow, "Ready now", null));
        return new(facts, actions, latest);
    }

    // ── Project discovery: catalog, scope, guided steps, readiness and step editing ──────────────────────────────

    /// <summary>Rows rendered per page of the scenario catalog; "Show more" adds the next page (hundreds of candidates stay responsive).</summary>
    public const int PageSize = 60;

    public enum CatalogGrouping { Area, Type, Source }

    /// <param name="Tests">"with" = only candidates with an associated existing test, "without" = only without; null = all.</param>
    public sealed record CatalogFilter(string Query = "", ScenarioCandidateType? Type = null, string? Area = null, string? Source = null, string? Tests = null, bool AttentionOnly = false);

    public static string AreaOf(DiscoveredScenario c) => string.IsNullOrWhiteSpace(c.Area) ? "Not grouped" : c.Area!;

    /// <summary>Only when a person must look: no expected behaviour in the evidence, or a possible duplicate of another candidate.</summary>
    public static string? Warning(DiscoveredScenario c) => c.NeedsClarification ? "Needs clarification" : c.PossibleDuplicates.Count > 0 ? "Possible duplicate" : null;

    public static IReadOnlyList<DiscoveredScenario> Apply(IEnumerable<DiscoveredScenario> candidates, CatalogFilter f) => [.. candidates.Where(c =>
        (string.IsNullOrWhiteSpace(f.Query) || Contains(c.Title, f.Query) || Contains(c.Purpose, f.Query) || Contains(AreaOf(c), f.Query)
            || c.RequirementIds.Any(r => r.Contains(f.Query.Trim(), StringComparison.OrdinalIgnoreCase)))
        && (f.Type is null || c.Type == f.Type)
        && (f.Area is null || AreaOf(c) == f.Area)
        && (f.Source is null || c.SourceCategory == f.Source)
        && (f.Tests is null || (f.Tests == "with") == (c.ExistingTests.Count > 0))
        && (!f.AttentionOnly || Warning(c) is not null))];

    public sealed record CatalogGroup(string Id, string Title, IReadOnlyList<DiscoveredScenario> Items);

    /// <summary>Groups by real metadata only (the area/module the evidence names, the candidate type or the source category), in first-seen order.</summary>
    public static IReadOnlyList<CatalogGroup> Group(IReadOnlyList<DiscoveredScenario> items, CatalogGrouping by) =>
        [.. items.GroupBy(c => by switch { CatalogGrouping.Type => ScenarioCandidateTypes.Label(c.Type), CatalogGrouping.Source => c.SourceCategory, _ => AreaOf(c) })
            .Select(g => new CatalogGroup(Slug(g.Key), g.Key, [.. g]))];

    /// <summary>A readable group id from a real area title: letters (any script, e.g. "periodelås") and digits kept, everything else a hyphen.</summary>
    public static string Slug(string value)
    {
        var slug = new string([.. value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')]).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Length == 0 ? "other" : slug;
    }

    /// <summary>
    /// Whether a candidate is inside the selected test areas (Project Test Setup): a scope member with the candidate's own canonical key, a scenario
    /// reached through the scope's requirements by an established Verifies link, or a requirement member whose canonical identity carries the
    /// same requirement id in this project. No selection keeps every candidate; titles are never compared.
    /// </summary>
    public static bool InScope(DiscoveredScenario c, BirkNext.Scopes.ScopeSelection? selection, IReadOnlyCollection<string> viaRequirementScenarioKeys)
    {
        if (selection is null) return true;
        var members = selection.Members.Select(m => m.Member).ToList();
        if (c.ScopeKeys.Any(k => members.Any(m => m.Key == k))) return true;
        if (c.ScopeKeys.Any(k => k.StartsWith("Scenario:", StringComparison.Ordinal) && viaRequirementScenarioKeys.Contains(k["Scenario:".Length..]))) return true;
        var ids = c.RequirementIds.Select(r => r.Trim().ToLowerInvariant()).Where(r => r.Length > 0).ToList();
        return ids.Count > 0 && members.Any(m => m.Kind == BirkNext.Scopes.ScopeMemberKind.Requirement
            && ids.Any(id => m.TargetId.EndsWith("/" + id, StringComparison.OrdinalIgnoreCase) || m.TargetId.EndsWith("|" + id, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>A System Understanding guide candidate as a catalog row (historical guide plans keep their own workflow and ids).</summary>
    public static DiscoveredScenario FromGuideCandidate(ScenarioCandidate c) => new()
    {
        CandidateId = c.Source.ScenarioId, ProjectKey = c.Source.Owner, CapabilityId = "system-guide", SourceCategory = "System Understanding guide", Area = c.StageTitle,
        Title = c.Title, Purpose = c.Objective, Type = ScenarioCandidateType.BusinessFunctional, Confidence = CandidateConfidence.CandidateAssociation,
        ExpectedBehaviour = c.HasExpected ? "Stated in the guide (shown in the prepared case)." : null, ExpectedBasis = c.HasExpected ? "System Understanding guide" : null,
        RequirementIds = [.. c.Requirements.Select(r => r.RuleId)], Engine = "System Understanding guide",
        TestDataNeeds = c.HasTestData ? ["Described in the guide"] : [],
        ScopeKeys = ["Scenario:" + BirkNext.Interpretation.CanonicalKeys.EntityKey(BirkNext.Interpretation.CanonicalEntityType.Scenario,
            BirkNext.Interpretation.CorrelationRules.GuideScenarioIdentity(c.Source.Owner, c.Source.ScenarioId))],
        Evidence = [new("guide-scenario", $"{c.Source.Owner}/{c.Source.ScenarioId}", c.Source.Revision is { } r ? $"Guide revision {r}" : null)],
        OpenQuestions = [.. c.GuideState == "NeedsClarification" ? new[] { "The guide marks this scenario as needing clarification." } : []],
    };

    /// <summary>A case still needs someone's action before it can be run: not ready for Test Data, a clarification or a prerequisite.</summary>
    public static bool NeedsAction(TestCaseDraft c) => c.Review is not (TestCaseReviewState.Excluded or TestCaseReviewState.Superseded)
        && c.Testability.Readiness is not (CaseReadiness.RequiredAndAvailable or CaseReadiness.NotRequired);

    public static Label ReadinessLabel(CaseReadiness readiness) => readiness switch
    {
        CaseReadiness.NotRequired => new("Not required", "ok"),
        CaseReadiness.RequiredAndAvailable => new("Required and available", "ok"),
        CaseReadiness.RequiredButMissing => new("Required but missing", "bad"),
        CaseReadiness.NeedsClarification => new("Needs clarification", "warn"),
        CaseReadiness.LinkedButUnsuitable => new("Linked but unsuitable", "bad"),
        CaseReadiness.BlockedByPrerequisite => new("Blocked by prerequisite", "bad"),
        _ => new("Not assessed", "muted"),
    };

    public static string DataNeedLabel(TestDataNeed need) => need switch
    {
        TestDataNeed.Required => "Required",
        TestDataNeed.NotRequired => "Not required (decided by a reviewer)",
        _ => "Not assessed yet",
    };

    /// <summary>
    /// The four guided steps from what is stored: a plan with cases, every case reviewed, no case needing action, a snapshot of the current revision.
    /// The first unfinished step is current; later steps wait. Each step offers one in-page action (open it).
    /// </summary>
    public static IReadOnlyList<GuidedStep> Steps(TestPlanDraft? draft, IReadOnlyList<TestPlanSnapshotSummary> snapshots, int candidates, bool discovered, int selected)
    {
        var active = draft?.Cases.Where(c => c.Review is not (TestCaseReviewState.Excluded or TestCaseReviewState.Superseded)).ToList() ?? [];
        var accepted = active.Count(c => c.Review == TestCaseReviewState.Accepted);
        var action = active.Count(NeedsAction);
        var snapshot = draft is null ? null : snapshots.Where(s => s.DraftId == draft.Id).OrderByDescending(s => s.Sequence).FirstOrDefault();
        var done = new[] { active.Count > 0, active.Count > 0 && accepted == active.Count, active.Count > 0 && action == 0, snapshot is not null && snapshot.DraftRevision >= draft!.Revision };
        var current = Array.FindIndex(done, d => !d);
        GuidedStepState State(int i) => current < 0 || i < current ? (done[i] ? GuidedStepState.Done : GuidedStepState.Waiting) : i == current ? GuidedStepState.Current : GuidedStepState.Waiting;
        var noDraft = draft is null ? "Prepare test cases first." : null;
        return
        [
            new("choose", Areas[0].Label, State(0), active.Count > 0 ? $"{active.Count} in the plan" : discovered ? $"{candidates} candidates · {selected} selected" : "Not discovered yet",
                Areas[0].Purpose, ActionLabel: "Open", ActionId: "choose"),
            new("prepare", Areas[1].Label, State(1), draft is null ? "Waiting" : $"{accepted} of {active.Count} accepted", Areas[1].Purpose, ActionLabel: "Open", ActionId: "prepare", DisabledReason: noDraft),
            new("testability", Areas[2].Label, State(2), draft is null ? "Waiting" : action == 0 ? "No case needs action" : $"{action} need action", Areas[2].Purpose,
                ActionLabel: "Open", ActionId: "testability", DisabledReason: noDraft),
            new("save", Areas[3].Label, State(3), snapshot is null ? "Not saved" : snapshot.DraftRevision >= (draft?.Revision ?? 0) ? $"Saved as snapshot #{snapshot.Sequence}" : $"Changed since snapshot #{snapshot.Sequence}",
                Areas[3].Purpose, ActionLabel: "Open", ActionId: "save", DisabledReason: noDraft),
        ];
    }

    /// <summary>The working copy's steps with their expected results (null = Needs clarification).</summary>
    public static (List<string> Steps, List<string?> Expected) StepsOf(TestCaseDraft c) => ([.. c.EffectiveSteps], [.. c.EffectiveStepExpected]);

    /// <summary>Sets the edited steps; identical to the generated steps and results means "no edit" (the generated ones stay in force).</summary>
    public static TestCaseDraft WithSteps(TestCaseDraft c, List<string> steps, List<string?> expected)
    {
        var generatedExpected = c.Generated.Steps.Select((_, i) => i < c.Generated.StepExpected.Count ? c.Generated.StepExpected[i] : null).ToList();
        var same = steps.SequenceEqual(c.Generated.Steps) && expected.SequenceEqual(generatedExpected);
        return c with { Edits = c.Edits with { Steps = same ? null : steps, StepExpected = same ? null : expected } };
    }

    public static TestCaseDraft SetStep(TestCaseDraft c, int index, string? action, string? expected)
    {
        var (steps, results) = StepsOf(c);
        if (index < 0 || index >= steps.Count) return c;
        if (action is not null) steps[index] = action;
        if (expected is not null) results[index] = string.IsNullOrWhiteSpace(expected) ? null : expected;
        return WithSteps(c, steps, results);
    }

    public static TestCaseDraft AddStep(TestCaseDraft c, string action)
    {
        var (steps, results) = StepsOf(c);
        steps.Add(action);
        results.Add(null);
        return WithSteps(c, steps, results);
    }

    public static TestCaseDraft RemoveStep(TestCaseDraft c, int index)
    {
        var (steps, results) = StepsOf(c);
        if (index < 0 || index >= steps.Count) return c;
        steps.RemoveAt(index);
        results.RemoveAt(index);
        return WithSteps(c, steps, results);
    }

    public static TestCaseDraft MoveStep(TestCaseDraft c, int index, int delta)
    {
        var (steps, results) = StepsOf(c);
        var to = index + delta;
        if (index < 0 || index >= steps.Count || to < 0 || to >= steps.Count) return c;
        (steps[index], steps[to]) = (steps[to], steps[index]);
        (results[index], results[to]) = (results[to], results[index]);
        return WithSteps(c, steps, results);
    }

    // ── Test type, execution method and performance wording (plain language for manual testers) ────────────────────

    public static string TypeLabel(TestCaseType type) => type switch
    {
        TestCaseType.Functional => "Functional",
        TestCaseType.Integration => "Integration",
        TestCaseType.EndToEnd => "End-to-end",
        TestCaseType.Performance => "Performance",
        TestCaseType.Security => "Security",
        TestCaseType.ApiContract => "API / contract",
        _ => "Not classified yet",
    };

    public static string MethodLabel(TestExecutionMethod method) => method switch
    {
        TestExecutionMethod.Manual => "Manual",
        TestExecutionMethod.ToolAssisted => "Tool-assisted",
        TestExecutionMethod.Automated => "Automated",
        _ => "Not decided",
    };

    public static string SubtypeLabel(PerformanceTestSubtype subtype) => subtype switch
    {
        PerformanceTestSubtype.Load => "Load (expected normal traffic)",
        PerformanceTestSubtype.Stress => "Stress (beyond normal traffic)",
        PerformanceTestSubtype.Spike => "Spike (sudden burst)",
        PerformanceTestSubtype.Soak => "Soak / endurance (long duration)",
        PerformanceTestSubtype.Capacity => "Capacity (how much the system can take)",
        PerformanceTestSubtype.Baseline => "Baseline / benchmark",
        _ => "Not chosen",
    };
}
