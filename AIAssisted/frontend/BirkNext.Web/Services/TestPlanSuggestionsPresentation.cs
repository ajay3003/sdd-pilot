using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// Pure presentation rules for Test Plan Suggestions: areas, filters, labels, the save request built from the working copy (human-owned parts only),
/// and the evidence-backed next steps the Manual Testing focus panel shows on Dashboard and Recommended Workflow. No I/O.
/// </summary>
public static class TestPlanSuggestionsPresentation
{
    public const string Route = "test-plan-suggestions";

    /// <param name="Purpose">One sentence: what this step of test preparation is for (shown under the area bar).</param>
    public sealed record Area(string Id, string Label, string Purpose = "");

    /// <summary>The preparation steps, in order. Understanding the system stays in System Understanding; these steps prepare how it is tested.</summary>
    public static readonly IReadOnlyList<Area> Areas =
    [
        new("discover", "Discover", "Choose the scenarios to include in a proposed test plan."),
        new("plan", "Suggested Test Plan", "Organise the chosen scenarios into a test plan and test suites."),
        new("review", "Review Test Cases", "Review preconditions, test steps and expected results, and record your decision."),
        new("data", "Test Data & Testability", "Link approved test data and see what still blocks manual execution."),
        new("snapshots", "Saved Snapshots", "Freeze the proposal as an immutable snapshot for a delivery."),
        new("compare", "Compare Deliveries", "See what changed between two snapshots and what to retest."),
        new("export", "Prepare for Azure DevOps", "Export a snapshot as a reviewable proposal; nothing is written to Azure DevOps."),
    ];

    /// <summary>"Step 2 of 7 · Organise …" for the current area. Pure.</summary>
    public static string StepLine(string area)
    {
        var index = Areas.ToList().FindIndex(a => a.Id == area);
        return index < 0 ? "" : $"Step {index + 1} of {Areas.Count} · {Areas[index].Purpose}";
    }

    public static string AreaOrDefault(string? area) => Areas.Any(a => a.Id == area) ? area! : "discover";

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
    public static SaveTestPlanDraftRequest ToSaveRequest(TestPlanDraft draft, IEnumerable<string>? addScenarios = null) => new()
    {
        BaseRevision = draft.Revision, PlanName = draft.PlanName, DeliveryLabel = draft.DeliveryLabel,
        ScenarioIds = [.. draft.Cases.Where(c => c.Review != TestCaseReviewState.Superseded).Select(c => c.Source.ScenarioId).Concat(addScenarios ?? []).Distinct(StringComparer.Ordinal)],
        Suites = draft.Suites,
        Cases = [.. draft.Cases.Select(c => new TestCaseHumanPart
        {
            ScenarioId = c.Source.ScenarioId, SuiteId = c.SuiteId, Edits = c.Edits, Risk = c.Risk, TestDataAssetIds = [.. c.TestData.Select(d => d.AssetId)],
            Correlations = c.Correlations.Where(x => x.State != CorrelationState.Candidate).ToDictionary(x => x.Target.Key, x => x.State, StringComparer.Ordinal),
            ManualLinks = [.. c.Correlations.Where(x => x.Basis.StartsWith("Linked by a reviewer", StringComparison.Ordinal)).Select(x => x.Target.Key)],
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
        var link = $"{Route}?guide={Uri.EscapeDataString(ws.GuideKey)}";
        var actions = new List<FocusAction>();
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
}
