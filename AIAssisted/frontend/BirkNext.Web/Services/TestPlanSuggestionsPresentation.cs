using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// Pure presentation rules for Test Plan Suggestions: areas, filters, labels, the save request built from the working copy (human-owned parts only),
/// and the evidence-backed next steps the Manual Testing focus panel shows on Dashboard and Recommended Workflow. No I/O.
/// </summary>
public static class TestPlanSuggestionsPresentation
{
    public const string Route = "test-plan-suggestions";

    /// <summary>
    /// Why a test case cannot be approved yet, or null when it can. The same content rules as the Azure DevOps export (AdoTestPlanExporter: an
    /// expected result, at least one step, an expected result per step), so an approved case is never blocked only later, at the export. On M2LB
    /// (2026-11-19) two approved cases were first refused by the export: a step added without its expected result, and a guide scenario whose
    /// steps state none.
    /// </summary>
    /// <summary>What still stops approval: the Azure DevOps export's content rules (a step, an action and an expected result on every step). The
    /// case-level expected result is not part of an Azure DevOps test case and is not required. The API applies the same rule.</summary>
    public static string? ApproveBlocker(TestCaseDraft c)
    {
        var steps = c.EffectiveSteps;
        if (steps.Count == 0 || steps.All(string.IsNullOrWhiteSpace)) return "add at least one step with its expected result.";
        var expected = c.EffectiveStepExpected;
        var noAction = steps.Select((a, i) => (a, i)).Where(x => string.IsNullOrWhiteSpace(x.a)).Select(x => x.i + 1).ToList();
        if (noAction.Count > 0) return $"step {string.Join(", ", noAction)} need{(noAction.Count == 1 ? "s" : "")} an action.";
        var missing = steps.Select((_, i) => i).Where(i => i >= expected.Count || string.IsNullOrWhiteSpace(expected[i])).Select(i => i + 1).ToList();
        return missing.Count == 0 ? null : $"step {string.Join(", ", missing)} need{(missing.Count == 1 ? "s" : "")} an expected result.";
    }

    /// <param name="Purpose">One sentence: what this step of test preparation is for (shown under the step list).</param>
    public sealed record Area(string Id, string Label, string Purpose = "");

    /// <summary>The four guided steps, in order. Saved snapshots, delivery comparison and the Azure DevOps preparation stay available as secondary areas.</summary>
    public static readonly IReadOnlyList<Area> Areas =
    [
        new("choose", "Choose scenarios", "Find what to test in this project's evidence and choose the scenarios to prepare."),
        new("prepare", "Prepare test cases", "Write readable test cases: preconditions, steps with an expected result each, and your review decision."),
        new("testability", "Check testability", "See which cases still need Test Data, a clarification or a prerequisite before someone can run them."),
        new("save", "Save & export", "Name the plan and export it as a file for Azure DevOps Test Plans."),
    ];

    /// <summary>Snapshot history and delivery comparison: BirkNext's own history, collapsed under Advanced (Azure DevOps keeps the test plan).</summary>
    public static bool IsAdvancedArea(string? id) => id is "snapshots" or "compare";

    /// <summary>Secondary areas (not steps): snapshot history, delivery comparison and the Azure DevOps preparation.</summary>
    public static readonly IReadOnlyList<Area> MoreAreas =
    [
        new("snapshots", "Saved snapshots", "Every saved snapshot of this project's plans; a snapshot never changes."),
        new("compare", "Compare deliveries", "See what changed between two snapshots and what to retest."),
        new("export", "Export to Azure DevOps", "Create an Azure Test Plans import file (Excel or CSV) from the saved test cases and import it in Azure DevOps yourself; nothing is written to Azure DevOps."),
    ];

    /// <summary>"Step 2 of 4 · Write readable …" for a step; the purpose alone for a secondary area. Pure.</summary>
    public static string StepLine(string area)
    {
        var index = Areas.ToList().FindIndex(a => a.Id == area);
        if (index >= 0) return $"Step {index + 1} of {Areas.Count} · {Areas[index].Purpose}";
        return MoreAreas.FirstOrDefault(a => a.Id == area)?.Purpose ?? "";
    }

    /// <summary>The label of a step or secondary area (the heading of the area's section).</summary>
    public static string AreaLabel(string? area)
    {
        var id = AreaOrDefault(area);
        return Areas.Concat(MoreAreas).FirstOrDefault(a => a.Id == id)?.Label ?? Areas[0].Label;
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
        TestCaseReviewState.Accepted => new("Approved", "ok"),
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
            Norwegian = draft.ProjectKey is null ? null : c.Norwegian ?? new TestCaseTranslation(),
            RejectedSuggestions = draft.ProjectKey is null ? null : [.. c.RejectedSuggestions ?? []],
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

    /// <param name="population">R1: the authoritative requirement population — the selected specification's requirement ids
    /// (<see cref="RequirementPopulation.SelectedSpecificationAsync"/>, the same set Test Coverage counts). When given, every requirement count is
    /// over that population (relationship-analysis coverage is one measure of it); without a selected specification the counts say they are the
    /// analysis's own.</param>
    public static PlanFocus Focus(TestPlanWorkspace? ws, RequirementCoverageView? coverage = null, IReadOnlyList<string>? population = null)
    {
        var plan = PlanOnly(ws);
        if (coverage is not { Known: true } c) return plan; // unknown coverage adds no count (no denominator) and no action
        var facts = plan.Facts.ToList();
        var needsReview = c.NeedsReview;
        if (population is { Count: > 0 } && c.Rows.Count > 0)
        {
            var r = RequirementPopulation.Relationship(c, population);
            needsReview = r.NeedsReview;
            facts.Add(new("req-reviewed", $"Requirements with reviewed scenario coverage (of {r.Population} in the selected specification)", r.Reviewed));
            facts.Add(new("req-needs", "Requirements needing association review", r.NeedsReview));
            if (r.NotAnalysed > 0) facts.Add(new("req-not-analysed", "Requirements not in the last relationship analysis", r.NotAnalysed));
        }
        else
        {
            // No selected specification (or no rows): the analysis's own population, named as such — never presented as the specification's.
            facts.Add(new("req-reviewed", $"Requirements with reviewed scenario coverage (of {c.Requirements} in the last relationship analysis)", c.Reviewed));
            facts.Add(new("req-needs", "Requirements needing association review", c.NeedsReview));
        }
        var actions = plan.Actions.ToList();
        var at = Math.Min(1, actions.Count);
        if (c.Associations.Any(a => a.InterpretationOutdated))
            actions.Insert(at, new("req-reinterpret", "Re-run relationship analysis", "A guide scenario changed after the last interpretation; its requirement associations are not verified for the new text.",
                RequirementCoveragePresentation.RelationshipReviewHref, FocusActionState.CanDoNow, "Ready now", null));
        if (needsReview > 0)
            actions.Insert(at, new("req-associations", "Review requirement/scenario associations", $"{needsReview} requirement(s) have only suggested scenario associations; nothing is counted until decided.",
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
            new("cases", "Prepared Test Case drafts", draft.Cases), new("accepted", "Approved", draft.Accepted), new("expected", "Missing expected results", draft.MissingExpected),
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

    public enum CatalogGrouping { Area, Type, Source, Kind, Requirement }

    // ── Candidate views (2026-11-18): one inventory, four views ─────────────────────────────────────────────────────
    // A manual tester starts on what a person can test (business, guide and UI behaviour); technical candidates (API operations, authorization
    // checks, page checks, integration journeys) stay one click away in the same inventory. Nothing is copied, persisted per view or hidden from
    // "All candidates", and a selection survives every view switch.

    public enum CandidateView { Manual, Technical, All, Attention }

    public static readonly IReadOnlyList<CandidateView> Views = [CandidateView.Manual, CandidateView.Technical, CandidateView.All, CandidateView.Attention];

    public static CandidateView ViewOrDefault(string? view) => view switch
    {
        "technical" => CandidateView.Technical, "all" => CandidateView.All, "attention" => CandidateView.Attention, _ => CandidateView.Manual,
    };

    public static string ViewId(CandidateView view) => view switch
    {
        CandidateView.Technical => "technical", CandidateView.All => "all", CandidateView.Attention => "attention", _ => "manual",
    };

    public static string ViewLabel(CandidateView view) => view switch
    {
        CandidateView.Technical => "Technical candidates", CandidateView.All => "All candidates", CandidateView.Attention => "Needs attention", _ => "Manual testing",
    };

    public static string ViewHint(CandidateView view) => view switch
    {
        CandidateView.Technical => "API operations, authorization checks, page checks and integration journeys. They are normally verified in Technical Testing; you can still prepare one as a manual case.",
        CandidateView.All => "Every discovered candidate, whatever workspace it belongs to.",
        CandidateView.Attention => "Candidates a person must look at first: an unclear expected result, a classified open point, a possible duplicate or an unclassified source.",
        _ => "Business scenarios, guide scenarios and UI behaviour a person can test by hand.",
    };

    /// <summary>The classified inventory of one discovery result: computed once per result, never per render or per row.</summary>
    /// <param name="Operations">Structural operation identity → the candidate ids declaring it (the same operation in several contract files).</param>
    public sealed record Inventory(IReadOnlyDictionary<string, ScenarioOwnershipInfo> Owners, IReadOnlyDictionary<string, IReadOnlyList<string>> Operations,
        int Total, int Manual, int Technical, int Attention, int Unclassified, int OperationCandidates)
    {
        public static readonly Inventory Empty = new(new Dictionary<string, ScenarioOwnershipInfo>(), new Dictionary<string, IReadOnlyList<string>>(), 0, 0, 0, 0, 0, 0);
        public ScenarioOwnershipInfo Of(DiscoveredScenario c) => Owners.TryGetValue(c.CandidateId, out var o) ? o : ScenarioOwnership.Of(c);
        public int DistinctOperations => Operations.Count;
        public int Count(CandidateView view) => view switch { CandidateView.Manual => Manual, CandidateView.Technical => Technical, CandidateView.Attention => Attention, _ => Total };

        /// <summary>The other candidates that declare the same contract operation (another file of the same contract), never this one.</summary>
        public IReadOnlyList<string> SameOperation(DiscoveredScenario c) =>
            ScenarioOwnership.OperationIdentity(c) is { } key && Operations.TryGetValue(key, out var ids) ? [.. ids.Where(id => id != c.CandidateId)] : [];
    }

    public static Inventory Classify(IReadOnlyList<DiscoveredScenario> candidates)
    {
        if (candidates.Count == 0) return Inventory.Empty;
        var owners = new Dictionary<string, ScenarioOwnershipInfo>(candidates.Count, StringComparer.Ordinal);
        var operations = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        int manual = 0, technical = 0, attention = 0, unclassified = 0, operationCandidates = 0;
        foreach (var c in candidates)
        {
            var o = ScenarioOwnership.Of(c);
            owners[c.CandidateId] = o;
            if (o.ManualRelevant) manual++;
            else if (o.Technical) technical++;
            if (o.NeedsReview) unclassified++;
            if (o.NeedsReview || Warning(c) is not null) attention++;
            if (ScenarioOwnership.OperationIdentity(c) is { } key)
            {
                operationCandidates++;
                if (!operations.TryGetValue(key, out var list)) operations[key] = list = [];
                list.Add(c.CandidateId);
            }
        }
        return new(owners, operations.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.Ordinal), candidates.Count, manual, technical, attention, unclassified, operationCandidates);
    }

    public static bool InView(DiscoveredScenario c, ScenarioOwnershipInfo o, CandidateView view) => view switch
    {
        CandidateView.Manual => o.ManualRelevant,
        CandidateView.Technical => o.Technical,
        CandidateView.Attention => o.NeedsReview || Warning(c) is not null,
        _ => true,
    };

    /// <summary>A handoff to the specialist module that owns the technical work. Only real routes, and only query parameters the destination reads.</summary>
    /// <param name="Carries">What the destination receives (project selection always travels with the workspace; anything else is named).</param>
    public sealed record Handoff(string Label, string Href, string Carries);

    public static Handoff? HandoffOf(DiscoveredScenario c, ScenarioOwnershipInfo o)
    {
        const string project = "The selected project travels with you (the same project selector everywhere).";
        switch (o.Specialist)
        {
            case SpecialistArea.ContractTesting:
                // The exact source contract (snapshot + file path) and operation: Contract Testing opens that file's details (?mode=import&snapshot=&file=&operation=).
                if (Guid.TryParse(c.SnapshotKey, out var snapshot) && ScenarioOwnership.ContractFile(c) is { } file)
                    return new("Open in Contract Testing", ContractImportPresentation.SourceLink(snapshot, file, c.Template.FirstOrDefault(t => t.Key == "operation")?.Value),
                        $"{project} Opens {file} from this source snapshot, at this operation.");
                return new("Open Contract Testing", "api-quality-review/contracts",
                    $"{project} Contract Testing lists this snapshot's source contracts; look for {ScenarioOwnership.ContractFile(c) ?? "the contract"}.");
            case SpecialistArea.ApiQualityReview:
                return new("Open API Quality Review", "api-quality-review", $"{project} Authorization checks run there against the active Target Environment.");
            case SpecialistArea.FrontendQualityReview:
                return new("Open Frontend Quality Review", "frontend-quality-review", $"{project} Reviews the page's technical quality, not the behaviour itself.");
            case SpecialistArea.CriticalE2E:
                return new("Open Critical E2E Regression", "critical-e2e-regression", $"{project} The same UI analyzer proposes this check there for automation.");
            case SpecialistArea.PerformanceTestReview:
                return new("Open Performance Test Review", "performance-test-review", project);
            case SpecialistArea.IntegrationTesting:
                if (c.CapabilityId == ScenarioOwnership.IntegrationJourneys && ScenarioOwnership.JourneyParts(c) is { } declared)
                    return new("Prepare in Integration Testing", IntegrationTestingPresentation.StageRoute("prepare", $"{declared.Pack}:{declared.Journey}"),
                        $"{project} Opens the journey \"{c.Area ?? c.Title}\" in Integration Testing › Prepare test suite.");
                return new("Open Integration Testing", IntegrationTestingPresentation.StageRoute("discover"), $"{project} Shows the integrations the systems of this scenario use.");
            case SpecialistArea.TestPackJourney:
                if (ScenarioOwnership.JourneyParts(c) is { } pack && TestPackPages.Registered.TryGetValue(pack.Pack, out var page))
                    return new("Open the journey in its test pack", $"{page}?tab={Uri.EscapeDataString(pack.Journey)}", $"{project} Opens the journey \"{c.Area ?? pack.Journey}\" where its scenarios are prepared and run.");
                return new("Open Integration Quality Review", "integration-quality-review", $"{project} Test packs without a page of their own are reviewed there.");
            default:
                return null;
        }
    }

    /// <summary>The home of a specialist module (no candidate context): where its own candidate selection lives.</summary>
    public static string SpecialistHome(SpecialistArea area) => area switch
    {
        SpecialistArea.ContractTesting => "api-quality-review/contracts",
        SpecialistArea.ApiQualityReview => "api-quality-review",
        SpecialistArea.FrontendQualityReview => "frontend-quality-review",
        SpecialistArea.CriticalE2E => "critical-e2e-regression",
        SpecialistArea.PerformanceTestReview => "performance-test-review",
        SpecialistArea.IntegrationTesting or SpecialistArea.TestPackJourney => IntegrationTestingPresentation.HomeRoute,
        _ => "technical-testing/assess",
    };

    /// <summary>Technical candidates of one discovery result per specialist module, for Technical Testing (counts and links, never a second list).</summary>
    public sealed record SpecialistShare(SpecialistArea Area, string Label, int Candidates, int Distinct, IReadOnlyList<(string Kind, int Count)> Kinds, string Href);

    public static IReadOnlyList<SpecialistShare> TechnicalShares(IReadOnlyList<DiscoveredScenario> candidates, Inventory inventory) =>
        [.. candidates.Where(c => inventory.Of(c).Technical).GroupBy(c => inventory.Of(c).Specialist).OrderByDescending(g => g.Count()).Select(g =>
        {
            var operations = g.Select(ScenarioOwnership.OperationIdentity).Where(k => k is not null).ToList();
            var distinct = operations.Count == 0 ? g.Count() : g.Count() - operations.Count + operations.Distinct(StringComparer.Ordinal).Count();
            var kinds = g.GroupBy(c => inventory.Of(c).SourceKind).Select(k => (k.Key, k.Count())).OrderByDescending(k => k.Item2).ToList();
            var pack = g.Key == SpecialistArea.TestPackJourney ? g.Select(ScenarioOwnership.JourneyParts).FirstOrDefault(p => p is not null)?.Pack : null;
            var href = pack is not null && TestPackPages.Registered.TryGetValue(pack, out var page) ? page : SpecialistHome(g.Key);
            return new SpecialistShare(g.Key, ScenarioOwnership.SpecialistLabel(g.Key) is { Length: > 0 } label ? label : "Technical Testing", g.Count(), distinct, kinds, href);
        })];

    /// <summary>What preparing the selected candidates would do, shown before a bulk preparation (never for one ordinary case).</summary>
    /// <param name="IncompleteSteps">Candidates with no steps, or steps without an expected result: the export (and so Approve) needs both.</param>
    public sealed record PreparePreview(int Selected, int Adding, int AlreadyInPlan, int Technical, int Overlaps, int MissingExpected, int MissingPrerequisites,
        IReadOnlyList<(string Kind, int Count)> Kinds, bool NeedsConfirmation, int IncompleteSteps = 0);

    /// <summary>What the evidence leaves open in a candidate's steps (the export, and so Approve, needs at least one step and an expected result on
    /// every step); null when its steps are complete. Shown before preparing so the work after preparation is no surprise.</summary>
    public static string? StepGap(DiscoveredScenario c)
    {
        if (c.Steps.Count == 0) return "No steps in the source";
        var open = c.Steps.Count(s => string.IsNullOrWhiteSpace(s.Expected));
        return open == 0 ? null : open == c.Steps.Count
            ? $"No step states its expected result"
            : $"{open} of {c.Steps.Count} steps without an expected result";
    }

    /// <summary>Up to this many cases are prepared without a confirmation when nothing about them needs a look.</summary>
    public const int ConfirmFrom = 5;

    /// <param name="stepsKnown">False for System Understanding guide candidates read in guide mode: they do not carry their steps here, so
    /// nothing is said about them (the prepared case shows them).</param>
    public static PreparePreview Preview(IReadOnlyCollection<string> selected, IReadOnlyList<DiscoveredScenario> candidates, Inventory inventory, IReadOnlySet<string> inPlan,
        bool stepsKnown = true)
    {
        var byId = candidates.ToDictionary(c => c.CandidateId, StringComparer.Ordinal);
        var adding = selected.Where(id => !inPlan.Contains(id) && byId.ContainsKey(id)).Select(id => byId[id]).ToList();
        var technical = adding.Count(c => !inventory.Of(c).ManualRelevant);
        // Overlap: a candidate already in the plan names the same requirement through another source, or declares the same contract operation.
        var overlaps = adding.Count(c => c.PossibleDuplicates.Any(inPlan.Contains) || inventory.SameOperation(c).Any(inPlan.Contains)
            || inventory.SameOperation(c).Any(id => selected.Contains(id) && string.CompareOrdinal(id, c.CandidateId) < 0));
        var missingExpected = adding.Count(c => c.NeedsClarification);
        var missingPrerequisites = adding.Count(c => c.Gaps.Any(g => g.Kind == EvidenceGapKind.TestPrerequisiteMissing));
        var incompleteSteps = stepsKnown ? adding.Count(c => StepGap(c) is not null) : 0;
        var kinds = adding.GroupBy(c => inventory.Of(c).SourceKind).Select(g => (g.Key, g.Count())).OrderByDescending(k => k.Item2).ToList();
        var issues = technical + overlaps + missingExpected + missingPrerequisites;
        return new(selected.Count, adding.Count, selected.Count(inPlan.Contains), technical, overlaps, missingExpected, missingPrerequisites, kinds,
            adding.Count >= ConfirmFrom || adding.Count >= 2 && (issues > 0 || incompleteSteps > 0), incompleteSteps);
    }

    /// <summary>Selected candidates that would overlap a case already in the plan (shown on the preparation bar).</summary>
    public static int OverlapsWithPlan(IEnumerable<string> selected, IReadOnlyList<DiscoveredScenario> candidates, Inventory inventory, IReadOnlySet<string> inPlan)
    {
        var set = selected.ToHashSet(StringComparer.Ordinal);
        return candidates.Count(c => set.Contains(c.CandidateId) && !inPlan.Contains(c.CandidateId)
            && (c.PossibleDuplicates.Any(inPlan.Contains) || inventory.SameOperation(c).Any(inPlan.Contains)));
    }

    /// <summary>The adaptive empty Manual view: why it is empty and what the tester can actually do.</summary>
    public static string? ManualEmptyReason(Inventory inventory) => inventory.Manual > 0 || inventory.Total == 0 ? null
        : inventory.Technical > 0
            ? "No business-focused scenarios identified. You can prepare technical candidates manually or open Technical Testing."
            : "No candidate could be classified for manual testing. Review the unclassified candidates under Needs attention.";

    /// <param name="Tests">"with" = only candidates with an associated existing test, "without" = only without; null = all.</param>
    public sealed record CatalogFilter(string Query = "", ScenarioCandidateType? Type = null, string? Area = null, string? Source = null, string? Tests = null, bool AttentionOnly = false);

    public static string AreaOf(DiscoveredScenario c) => string.IsNullOrWhiteSpace(c.Area) ? "Not grouped" : c.Area!;

    /// <summary>
    /// Only when a person must look, named for what is actually open: an unclear expectation ("Needs clarification"), else the most pressing
    /// classified gap (contradiction, implementation, test prerequisite, verification), else a possible duplicate. A scenario that simply has
    /// not been run yet, or whose source association awaits review, gets no warning chip (that would mark almost every row); its details say so.
    /// </summary>
    public static string? Warning(DiscoveredScenario c) =>
        c.NeedsClarification ? "Needs clarification"
        : TopGap(c) is { } gap ? EvidenceGaps.Label(gap)
        : c.PossibleDuplicates.Count > 0 ? "Possible duplicate" : null;

    private static readonly EvidenceGapKind[] WarningOrder =
        [EvidenceGapKind.ContradictoryEvidence, EvidenceGapKind.ImplementationEvidenceMissing, EvidenceGapKind.TestPrerequisiteMissing, EvidenceGapKind.VerificationMechanismMissing];

    public static EvidenceGapKind? TopGap(DiscoveredScenario c) =>
        WarningOrder.Cast<EvidenceGapKind?>().FirstOrDefault(k => c.Gaps.Any(g => g.Kind == k));

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
    /// <param name="inventory">For <see cref="CatalogGrouping.Kind"/>: the producer's own kind of evidence (acceptance scenario, API operation …).</param>
    public static IReadOnlyList<CatalogGroup> Group(IReadOnlyList<DiscoveredScenario> items, CatalogGrouping by, Inventory? inventory = null) =>
        [.. items.GroupBy(c => by switch
            {
                CatalogGrouping.Type => ScenarioCandidateTypes.Label(c.Type),
                CatalogGrouping.Source => c.SourceCategory,
                CatalogGrouping.Kind => (inventory ?? Inventory.Empty).Of(c).SourceKind,
                CatalogGrouping.Requirement => c.RequirementIds.FirstOrDefault() is { } r ? r.Trim().ToUpperInvariant() : "No requirement named",
                _ => AreaOf(c),
            })
            .Select(g => new CatalogGroup(Slug(g.Key), g.Key, [.. g]))];

    public static string GroupingLabel(CatalogGrouping by) => by switch
    {
        CatalogGrouping.Type => "Type", CatalogGrouping.Source => "Source", CatalogGrouping.Kind => "Kind of evidence", CatalogGrouping.Requirement => "Requirement", _ => "Area / module",
    };

    /// <summary>A readable group id from a real area title: letters (any script, e.g. "periodelås") and digits kept, everything else a hyphen.</summary>
    public static string Slug(string value)
    {
        var slug = new string([.. value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')]).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Length == 0 ? "other" : slug;
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
            new("prepare", Areas[1].Label, State(1), draft is null ? "Waiting" : $"{accepted} of {active.Count} approved", Areas[1].Purpose, ActionLabel: "Open", ActionId: "prepare", DisabledReason: noDraft),
            new("testability", Areas[2].Label, State(2), draft is null ? "Waiting" : action == 0 ? "No case needs action" : $"{action} need action", Areas[2].Purpose,
                ActionLabel: "Open", ActionId: "testability", DisabledReason: noDraft),
            new("save", Areas[3].Label, State(3), snapshot is null ? (draft is { Revision: > 0 } saved ? $"Plan saved (revision {saved.Revision}) · no snapshot yet" : "Not saved") : snapshot.DraftRevision >= (draft?.Revision ?? 0) ? $"Saved as snapshot #{snapshot.Sequence}" : $"Changed since snapshot #{snapshot.Sequence}",
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

    public static TestCaseDraft AddStep(TestCaseDraft c, string action, string? expected = null)
    {
        var (steps, results) = StepsOf(c);
        steps.Add(action);
        results.Add(string.IsNullOrWhiteSpace(expected) ? null : expected.Trim());
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
