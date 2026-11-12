using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BirkNext.SystemUnderstanding;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// The pure rules of Test Plan Suggestions (rule version <see cref="RuleVersion"/>). Reads the System Understanding guide's own scenarios, rules,
/// steps and stages; never extracts scenarios from source, never invents an expected result, never merges by title similarity and never treats
/// a documented scenario as executable. Unit-testable: no I/O.
/// </summary>
public static partial class TestPlanSuggestionEngine
{
    /// <summary>Changes when these rules change. A snapshot records it; a difference in it alone is never an application behaviour change.</summary>
    public const string RuleVersion = "tps-1";

    public const string UngroupedSuiteId = "suite:ungrouped";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Identity ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The scenario's own fingerprint: the scenario plus the guide rules that name it and the steps it uses. A change to any of them is a
    /// source change of this scenario (detected, never applied silently to reviewed drafts).</summary>
    public static string ScenarioFingerprint(SystemGuideContent guide, GuideTestScenario scenario)
    {
        var rules = guide.Rules.Where(r => r.TestScenarioIds.Contains(scenario.Id, StringComparer.Ordinal)).OrderBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => new { r.Id, r.Title, r.ExpectedBehavior, r.Implementation });
        var steps = scenario.StepIds.Select(id => guide.Steps.FirstOrDefault(s => s.Id == id)).OfType<GuideStep>().Select(s => new { s.Id, s.Title, s.Stage, s.HowToVerify, s.ExpectedResult });
        return Sha(JsonSerializer.Serialize(new { scenario, rules, steps }, Json));
    }

    public static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static bool IsReviewedGuide(string reviewState) => reviewState.StartsWith("Published", StringComparison.Ordinal);

    // ── Generation ───────────────────────────────────────────────────────────────────────────────────────────

    public static List<TestCaseRequirement> Requirements(SystemGuideContent guide, string scenarioId) =>
        [.. guide.Rules.Where(r => r.TestScenarioIds.Contains(scenarioId, StringComparer.Ordinal)).Select(r => new TestCaseRequirement(r.Id, r.Title, r.ExpectedBehavior))];

    public static string? StageOf(SystemGuideContent guide, GuideTestScenario scenario) =>
        scenario.StepIds.Select(id => guide.Steps.FirstOrDefault(s => s.Id == id)?.Stage).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

    /// <summary>The draft content of one scenario. Steps come from the guide's "how to verify" of each referenced step (its own words); the expected
    /// result is the scenario's own, classified by how reviewed the guide is. A scenario the guide marks NeedsClarification stays so.</summary>
    public static TestCaseContent Generate(SystemGuideContent guide, GuideTestScenario scenario, bool reviewedGuide)
    {
        var steps = scenario.StepIds.Select(id => guide.Steps.FirstOrDefault(s => s.Id == id)).OfType<GuideStep>()
            .Select(s => string.IsNullOrWhiteSpace(s.HowToVerify) ? s.Title : $"{s.Title}: {s.HowToVerify}").ToList();
        var basis = scenario.State == GuideScenarioState.NeedsClarification ? ExpectedResultBasis.NeedsClarification
            : string.IsNullOrWhiteSpace(scenario.ExpectedOutcome) ? ExpectedResultBasis.Missing
            : reviewedGuide ? ExpectedResultBasis.ReviewedGuide : ExpectedResultBasis.UnreviewedGuide;
        var components = scenario.StepIds.Select(id => guide.Steps.FirstOrDefault(s => s.Id == id)?.ComponentId).OfType<string>().Distinct(StringComparer.Ordinal)
            .Select(id => guide.Components.FirstOrDefault(c => c.Id == id)?.Name ?? id).ToList();
        return new TestCaseContent
        {
            Title = scenario.Title,
            Purpose = scenario.Objective,
            Preconditions = Blank(scenario.Preconditions),
            Steps = steps,
            ExpectedResult = Blank(scenario.ExpectedOutcome),
            ExpectedBasis = basis,
            RequiredTestData = Blank(scenario.TestData),
            ObservationMethod = Blank(scenario.ObservableEvidence),
            EnvironmentPrerequisites = components.Count == 0 ? null : "Involved components: " + string.Join(", ", components),
            FailureCases = [.. scenario.FailureCases],
            Requirements = Requirements(guide, scenario.Id),
            NamedExistingTests = [.. scenario.ExistingTests],
            FollowUp = Blank(scenario.FollowUp),
            StageId = StageOf(guide, scenario),
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Suites proposed from the guide's business-process stages (in the guide's stage order) for the selected scenarios; scenarios whose
    /// steps name no stage go to one "Not grouped" suite for a reviewer to place. Never from technical directory names.</summary>
    public static List<TestSuiteDraft> ProposeSuites(SystemGuideContent guide, IEnumerable<TestCaseContent> cases)
    {
        var used = cases.Select(c => c.StageId).ToList();
        var suites = guide.Stages.Where(stage => used.Contains(stage.Id)).Select(stage => new TestSuiteDraft("suite:" + stage.Id, stage.Title, "stage", stage.Id)).ToList();
        if (used.Any(s => s is null)) suites.Add(new TestSuiteDraft(UngroupedSuiteId, "Not grouped (no business-process stage in the guide)", "stage", null));
        return suites;
    }

    public static string SuiteFor(TestCaseContent content) => content.StageId is { } stage ? "suite:" + stage : UngroupedSuiteId;

    // ── Correlation ──────────────────────────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"[a-z0-9]+(?:-[a-z0-9]+)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    /// <summary>
    /// Candidate links to executable definitions from explicit evidence only: a test the guide names that contains an executable scenario's exact
    /// identifier (e.g. "BirkNext: Altinn-reise valid-report" → journey scenario <c>valid-report</c>). Several executables with that identifier are
    /// ambiguous (none chosen). Titles are never compared.
    /// </summary>
    public static List<ScenarioCorrelation> Correlate(IEnumerable<string> namedTests, IReadOnlyList<ExecutableScenarioOption> executables)
    {
        var result = new List<ScenarioCorrelation>();
        foreach (var named in namedTests)
        {
            var ids = Identifier().Matches(named).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hits = executables.Where(e => ids.Contains(e.Reference.ScenarioId)).ToList();
            foreach (var hit in hits)
                if (!result.Any(r => r.Target.Key == hit.Reference.Key))
                    result.Add(new ScenarioCorrelation
                    {
                        Target = hit.Reference, Label = hit.Label, State = CorrelationState.Candidate, Capability = hit.Detail, Ambiguous = hits.Count > 1,
                        Basis = $"The guide names \"{named}\", which contains the identifier \"{hit.Reference.ScenarioId}\". Not confirmed.",
                    });
        }
        return result;
    }

    // ── Assessment ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Missing information and manual testability of one draft, from its content, edits, links and confirmed correlations.</summary>
    public static TestCaseDraft Assess(TestCaseDraft draft)
    {
        var missing = new List<string>();
        var expected = draft.EffectiveExpected;
        var expectedKnown = draft.Edits.ExpectedResult is not null
            || draft.Generated.ExpectedBasis == ExpectedResultBasis.ReviewedGuide;
        if (string.IsNullOrWhiteSpace(expected)) missing.Add("Expected result: NEEDS CLARIFICATION (none in the source)");
        else if (draft.Generated.ExpectedBasis == ExpectedResultBasis.NeedsClarification && draft.Edits.ExpectedResult is null)
            missing.Add("Expected result: NEEDS CLARIFICATION (the guide marks this scenario as needing clarification)");
        else if (!expectedKnown) missing.Add("Expected result: from an unreviewed guide draft; a reviewer must confirm it");
        if (draft.Generated.Requirements.Count == 0) missing.Add("Requirement: no business rule in the guide names this scenario");
        if (string.IsNullOrWhiteSpace(draft.Generated.RequiredTestData) && draft.TestData.Count == 0) missing.Add("Test data: not described and none linked");
        else if (draft.TestData.Count == 0) missing.Add("Test data: described but no Test Data asset linked");
        else if (draft.TestData.Any(d => !d.Resolved)) missing.Add("Test data: a linked asset no longer exists");
        if (string.IsNullOrWhiteSpace(draft.EffectiveObservation)) missing.Add("Observation method: how to see the result is not described");
        if (draft.EffectiveSteps.Count == 0) missing.Add("Test steps: none in the source");
        if (draft.EffectivePreconditions is null) missing.Add("Preconditions: not described");

        var confirmed = draft.Correlations.Where(c => c.State == CorrelationState.Confirmed).ToList();
        var input = confirmed.Count > 0 ? TestabilityFact.Known : TestabilityFact.NeedsAssistance;
        var data = draft.TestData.Count > 0 && draft.TestData.All(d => d.Resolved && d.Approval == "Approved") ? TestabilityFact.Known : TestabilityFact.Missing;
        var behaviour = expectedKnown && !string.IsNullOrWhiteSpace(expected) ? TestabilityFact.Known : TestabilityFact.Missing;
        var observation = string.IsNullOrWhiteSpace(draft.EffectiveObservation) ? TestabilityFact.Missing : TestabilityFact.Known;
        var blockers = new List<string>();
        if (input != TestabilityFact.Known) blockers.Add("No authorized input method linked (confirm an executable scenario or input channel)");
        if (data != TestabilityFact.Known) blockers.Add(draft.TestData.Count == 0 ? "No approved Test Data linked" : "Linked Test Data is not approved or no longer exists");
        if (behaviour != TestabilityFact.Known) blockers.Add("Expected behaviour is not established by a reviewed source");
        if (observation != TestabilityFact.Known) blockers.Add("The actual outcome cannot be observed (no observation method)");
        var testability = new TestCaseTestability
        {
            InputMethod = input, TestData = data, ExpectedBehavior = behaviour, Observation = observation,
            NeedsTechnicalAssistance = input != TestabilityFact.Known,
            Blockers = blockers,
            Summary = blockers.Count == 0
                ? "Prepared: input method, approved Test Data, reviewed expected result and observation method are known. Environment availability and tester permissions are not verified here."
                : $"Not ready for manual execution: {blockers.Count} open item{(blockers.Count == 1 ? "" : "s")}",
        };
        return draft with { Missing = missing, Testability = testability };
    }

    // ── Snapshot identity ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The content identity of a proposal: everything a snapshot preserves except bookkeeping (draft revision, timestamps, who saved).
    /// Two saves of the same content have the same fingerprint.</summary>
    public static string ContentFingerprint(TestPlanDraft plan) => Sha(JsonSerializer.Serialize(plan with
    {
        Revision = 0, CreatedAt = default, UpdatedAt = default, CreatedBy = "", UpdatedBy = "", Id = Guid.Empty,
    }, Json));

    // ── Delivery comparison ──────────────────────────────────────────────────────────────────────────────────

    public static DeliveryComparison Compare(TestPlanSnapshot from, TestPlanSnapshot to)
    {
        if (!string.Equals(from.Plan.GuideKey, to.Plan.GuideKey, StringComparison.Ordinal))
            return new DeliveryComparison
            {
                From = from.Summary, To = to.Summary, Comparable = false,
                IncomparableReason = $"The snapshots belong to different systems ({from.Plan.GuideKey} and {to.Plan.GuideKey}). Cross-system plans are never compared case by case.",
            };
        var context = Context(from.Plan, to.Plan);
        var cases = new List<CaseComparison>();
        var before = from.Plan.Cases.ToDictionary(c => c.Source.ScenarioId, StringComparer.Ordinal);
        var after = to.Plan.Cases.ToDictionary(c => c.Source.ScenarioId, StringComparer.Ordinal);
        var suitesBefore = from.Plan.Suites.ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal);
        var suitesAfter = to.Plan.Suites.ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal);
        foreach (var (id, a) in after)
        {
            if (!before.TryGetValue(id, out var b))
            {
                cases.Add(new CaseComparison { ScenarioId = id, Title = a.EffectiveTitle, Kind = CaseChangeKind.Added, Assessment = "New in this delivery's proposal." });
                continue;
            }
            var changes = Changes(b, a, suitesBefore, suitesAfter);
            var expectedChanged = changes.Any(c => c.Category == "ExpectedResult");
            var reviewedBoth = Reviewed(b) && Reviewed(a);
            cases.Add(new CaseComparison
            {
                ScenarioId = id, Title = a.EffectiveTitle, Kind = changes.Count == 0 ? CaseChangeKind.Unchanged : CaseChangeKind.Changed, Changes = changes,
                PotentialBehaviourChange = expectedChanged && reviewedBoth,
                Assessment = changes.Count == 0 ? "No change."
                    : expectedChanged && reviewedBoth ? "Potential business-rule change: the expected result changed and both sides rest on a reviewed basis. Confirm with the business owner."
                    : expectedChanged ? "The expected result text changed, but at least one side is not established by a reviewed basis: needs review, not a confirmed behaviour change."
                    : "Changed; review the listed fields.",
            });
        }
        foreach (var (id, b) in before.Where(p => !after.ContainsKey(p.Key)))
            cases.Add(new CaseComparison { ScenarioId = id, Title = b.EffectiveTitle, Kind = CaseChangeKind.Removed, Assessment = "Not in this delivery's proposal (excluded or removed from the source)." });
        cases = [.. cases.OrderBy(c => c.Kind).ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)];
        return new DeliveryComparison { From = from.Summary, To = to.Summary, Comparable = true, Context = context, Cases = cases, Recommendations = Recommend(to.Plan, cases) };
    }

    private static bool Reviewed(TestCaseDraft c) => c.Edits.ExpectedResult is not null && c.Review == TestCaseReviewState.Accepted
        || c.Generated.ExpectedBasis == ExpectedResultBasis.ReviewedGuide && c.Edits.ExpectedResult is null;

    private static List<ContextDifference> Context(TestPlanDraft a, TestPlanDraft b)
    {
        var list = new List<ContextDifference>();
        if (a.Basis.GuideRevision != b.Basis.GuideRevision || a.Basis.GuideFingerprint != b.Basis.GuideFingerprint)
            list.Add(new("Scenario source", $"Guide revision {a.Basis.GuideRevision} ({a.Basis.GuideReviewState})", $"Guide revision {b.Basis.GuideRevision} ({b.Basis.GuideReviewState})",
                "The scenario and requirement descriptions may differ; per-case differences below show where."));
        foreach (var role in a.Basis.Roles.Select(r => r.Role).Union(b.Basis.Roles.Select(r => r.Role)).Distinct(StringComparer.Ordinal))
        {
            var x = a.Basis.Roles.FirstOrDefault(r => r.Role == role);
            var y = b.Basis.Roles.FirstOrDefault(r => r.Role == role);
            if (x?.SnapshotId != y?.SnapshotId || x?.State != y?.State)
                list.Add(new("Source", Describe(x), Describe(y), "A different source snapshot or baseline. Source changes are a reason to review, not a proven behaviour change."));
        }
        if (!string.Equals(a.RuleVersion, b.RuleVersion, StringComparison.Ordinal))
            list.Add(new("Suggestion rules", a.RuleVersion, b.RuleVersion, "BirkNext's suggestion rules changed. This alone is not an application behaviour change."));
        return list;
    }

    private static string Describe(TestPlanSourceRole? role) => role is null ? "Not part of the basis"
        : role.SnapshotId is { } id ? $"{role.Label}: snapshot {id.ToString()[..8]} ({role.State})" : $"{role.Label}: no snapshot used ({role.State})";

    private static List<CaseFieldChange> Changes(TestCaseDraft b, TestCaseDraft a, IReadOnlyDictionary<string, string> sb, IReadOnlyDictionary<string, string> sa)
    {
        var list = new List<CaseFieldChange>();
        void Diff(string category, string? x, string? y) { if (!string.Equals(x ?? "", y ?? "", StringComparison.Ordinal)) list.Add(new(category, x, y)); }
        Diff("Title", b.EffectiveTitle, a.EffectiveTitle);
        Diff("ExpectedResult", b.EffectiveExpected, a.EffectiveExpected);
        Diff("Requirements", string.Join("; ", b.Generated.Requirements.Select(r => $"{r.RuleId}: {r.ExpectedBehavior ?? r.Title}")), string.Join("; ", a.Generated.Requirements.Select(r => $"{r.RuleId}: {r.ExpectedBehavior ?? r.Title}")));
        Diff("TestData", string.Join("; ", b.TestData.Select(d => $"{d.Alias}@{Short(d.Fingerprint)} {d.Approval}")), string.Join("; ", a.TestData.Select(d => $"{d.Alias}@{Short(d.Fingerprint)} {d.Approval}")));
        Diff("Preconditions", b.EffectivePreconditions, a.EffectivePreconditions);
        Diff("Steps", string.Join("\n", b.EffectiveSteps), string.Join("\n", a.EffectiveSteps));
        Diff("Observation", b.EffectiveObservation, a.EffectiveObservation);
        Diff("Risk", b.Risk.ToString(), a.Risk.ToString());
        Diff("Testability", b.Testability.Summary + Facts(b.Testability), a.Testability.Summary + Facts(a.Testability));
        Diff("Review", b.Review.ToString(), a.Review.ToString());
        Diff("Suite", sb.GetValueOrDefault(b.SuiteId, b.SuiteId), sa.GetValueOrDefault(a.SuiteId, a.SuiteId));
        Diff("SourceScenario", Short(b.Source.Fingerprint), Short(a.Source.Fingerprint));
        return list;
    }

    private static string Facts(TestCaseTestability t) => $" [input {t.InputMethod}, data {t.TestData}, expected {t.ExpectedBehavior}, observe {t.Observation}]";
    private static string Short(string? fingerprint) => fingerprint is { Length: >= 12 } f ? f[..12] : fingerprint ?? "-";

    /// <summary>Regression suggestions from the comparison (reuses the comparison, no separate impact engine). Unchanged high-risk and previously
    /// blocked cases stay suggested: unchanged content is never a reason to drop an important regression case.</summary>
    public static List<RegressionRecommendation> Recommend(TestPlanDraft to, IReadOnlyList<CaseComparison> cases)
    {
        var list = new List<RegressionRecommendation>();
        var byId = to.Cases.ToDictionary(c => c.Source.ScenarioId, StringComparer.Ordinal);
        foreach (var c in cases)
        {
            byId.TryGetValue(c.ScenarioId, out var draft);
            switch (c.Kind)
            {
                case CaseChangeKind.Added:
                    list.Add(new("New Test Case", c.ScenarioId, c.Title, "New in this proposal: prepare and review it."));
                    break;
                case CaseChangeKind.Changed:
                    var cats = c.Changes.Select(x => x.Category).ToHashSet(StringComparer.Ordinal);
                    if (cats.Contains("ExpectedResult")) list.Add(new("Expected result update", c.ScenarioId, c.Title, c.PotentialBehaviourChange ? "Potential business-rule change." : "Expected result text changed; confirm it."));
                    if (cats.Contains("Requirements") || cats.Contains("SourceScenario")) list.Add(new("Existing Test Case review", c.ScenarioId, c.Title, "The scenario or its business rules changed in the source."));
                    if (cats.Contains("TestData")) list.Add(new("Test Data update", c.ScenarioId, c.Title, "Linked Test Data or its revision changed."));
                    if (cats.Contains("Testability")) list.Add(new("Testability recheck", c.ScenarioId, c.Title, "What is needed to run it manually changed."));
                    list.Add(new("Regression inclusion", c.ScenarioId, c.Title, "Changed since the previous delivery."));
                    break;
                case CaseChangeKind.Unchanged when draft is { Risk: TestCaseRisk.High }:
                    list.Add(new("Regression inclusion", c.ScenarioId, c.Title, "Unchanged, but rated high risk: keep it in regression."));
                    break;
            }
            if (draft is not null && c.Kind != CaseChangeKind.Removed && !draft.Testability.Ready && draft.Review == TestCaseReviewState.Accepted)
                list.Add(new("Previously blocked", c.ScenarioId, c.Title, "Accepted but not ready for manual execution: " + string.Join("; ", draft.Testability.Blockers.Take(2))));
        }
        return list;
    }

    // ── Export ───────────────────────────────────────────────────────────────────────────────────────────────

    public const string ExportFormat = "birknext.test-plan-proposal/v1";
    public const string Boundary = "A reviewable proposal prepared in BirkNext. It is not an Azure DevOps Test Plan, nothing was written to Azure DevOps, and no Azure DevOps Test Case id is claimed unless verified. Import compatibility with Azure DevOps is not verified.";

    public static TestPlanExport Export(TestPlanSnapshot snapshot)
    {
        var plan = snapshot.Plan;
        var unresolved = plan.Cases.Where(c => c.Review != TestCaseReviewState.Excluded).SelectMany(c => c.Missing.Select(m => $"{c.EffectiveTitle}: {m}")).ToList();
        var included = plan.Cases.Where(c => c.Review != TestCaseReviewState.Excluded).ToList();
        var document = new
        {
            format = ExportFormat, boundary = Boundary, snapshotId = snapshot.Summary.Id, contentFingerprint = snapshot.Summary.ContentFingerprint,
            plan = plan.PlanName, delivery = plan.DeliveryLabel, ruleVersion = plan.RuleVersion,
            sourceBaseline = new { guide = plan.Basis.GuideKey, guideRevision = plan.Basis.GuideRevision, guideReviewState = plan.Basis.GuideReviewState, roles = plan.Basis.Roles },
            suites = plan.Suites.Select(s => new
            {
                name = s.Name,
                cases = included.Where(c => c.SuiteId == s.Id).Select(c => new
                {
                    title = c.EffectiveTitle, scenario = c.Source.Key, review = c.Review.ToString(), risk = c.Risk.ToString(), preconditions = c.EffectivePreconditions,
                    steps = c.EffectiveSteps, expected = c.EffectiveExpected, expectedBasis = c.Edits.ExpectedResult is not null ? nameof(ExpectedResultBasis.ReviewerClarified) : c.Generated.ExpectedBasis.ToString(),
                    requirements = c.Generated.Requirements, testData = c.TestData.Select(d => new { d.AssetId, d.Alias, d.Fingerprint, d.Approval }), requiredTestData = c.Generated.RequiredTestData,
                    observation = c.EffectiveObservation, testability = c.Testability.Summary, unresolved = c.Missing, azureDevOpsTestCaseId = c.VerifiedAzureDevOpsTestCaseId,
                }),
            }),
            unresolved,
        };
        var text = new StringBuilder();
        text.AppendLine($"Test Plan (proposal): {plan.PlanName}").AppendLine($"Delivery: {plan.DeliveryLabel ?? "—"} · Snapshot {snapshot.Summary.Id} · {snapshot.Summary.ContentFingerprint[..12]}")
            .AppendLine($"Source: {plan.Basis.GuideKey} guide revision {plan.Basis.GuideRevision} ({plan.Basis.GuideReviewState})").AppendLine(Boundary).AppendLine();
        foreach (var suite in plan.Suites)
        {
            var inSuite = included.Where(c => c.SuiteId == suite.Id).ToList();
            if (inSuite.Count == 0) continue;
            text.AppendLine($"## Suite: {suite.Name}");
            foreach (var c in inSuite)
            {
                text.AppendLine($"### {c.EffectiveTitle}  [{c.Review}; risk {c.Risk}]");
                if (c.EffectivePreconditions is { } pre) text.AppendLine($"Preconditions: {pre}");
                var i = 1;
                foreach (var step in c.EffectiveSteps) text.AppendLine($"{i++}. {step}");
                text.AppendLine($"Expected: {c.EffectiveExpected ?? "NEEDS CLARIFICATION"}");
                if (c.Generated.Requirements.Count > 0) text.AppendLine("Requirements: " + string.Join("; ", c.Generated.Requirements.Select(r => $"{r.RuleId} {r.Title}")));
                text.AppendLine("Test Data: " + (c.TestData.Count == 0 ? "none linked" : string.Join(", ", c.TestData.Select(d => $"{d.Alias} ({d.Approval})"))));
                foreach (var m in c.Missing) text.AppendLine($"Open: {m}");
                text.AppendLine();
            }
        }
        var file = $"test-plan-proposal-{Slug(plan.PlanName)}-{snapshot.Summary.Sequence}.json";
        return new TestPlanExport(ExportFormat, file, JsonSerializer.Serialize(document, new JsonSerializerOptions(Json) { WriteIndented = true }), text.ToString(), unresolved, Boundary);
    }

    private static string Slug(string value)
    {
        var slug = new string([.. value.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')]).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Length == 0 ? "plan" : slug[..Math.Min(slug.Length, 60)];
    }
}
