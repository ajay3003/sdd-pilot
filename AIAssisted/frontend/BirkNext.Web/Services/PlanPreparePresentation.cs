using System.Text.RegularExpressions;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// Manuell testing → Planlegg og klargjør (2026-11-23): the master-detail case editor. Pure; no I/O.
/// <list type="bullet">
/// <item><b>Scenario first.</b> The plan's cases are listed under their scenario (<see cref="ScenarioPlanning.Groups"/>); a scenario can hold several
/// cases. Nothing is regrouped by guesswork: a hand-written case without a scenario stays under "no scenario".</item>
/// <item><b>Technical content</b> (API operations, page smoke checks, integration journeys, performance, and user stories / requirements about the
/// code base itself — renaming identifiers, build, tests, telemetry names, governing documents) is hidden until "Vis teknisk innhold" is on. A case
/// with an unresolved source conflict, a case a person wrote, and a case already approved or needing clarification are never hidden.</item>
/// <item><b>The editor</b> holds a working copy (<see cref="CaseForm"/>). Only human-owned fields are produced (<see cref="CaseForm.ToEdits"/>):
/// a value equal to the generated one is "no edit", so the generated content and a person's edits never overwrite each other.</item>
/// </list>
/// </summary>
public static partial class PlanPreparePresentation
{
    // ── Labels (Norwegian UI) ──────────────────────────────────────────────────────────────────────────────────

    public static string ReviewLabel(TestCaseReviewState state) => state switch
    {
        TestCaseReviewState.Generated => "Utkast",
        TestCaseReviewState.UnderReview => "Klar for gjennomgang",
        TestCaseReviewState.NeedsClarification => "Trenger avklaring",
        TestCaseReviewState.Accepted => "Godkjent",
        TestCaseReviewState.Excluded => "Utelatt",
        TestCaseReviewState.Superseded => "Erstattet",
        _ => state.ToString(),
    };

    public static string ReviewTone(TestCaseReviewState state) => state switch
    {
        TestCaseReviewState.Accepted => "ok",
        TestCaseReviewState.UnderReview => "info",
        TestCaseReviewState.NeedsClarification => "warn",
        TestCaseReviewState.Excluded or TestCaseReviewState.Superseded => "muted",
        _ => "neutral",
    };

    /// <summary>The source state in the tester's words, or null when the case is current or was never compared.</summary>
    public static string? SourceLabel(TestCaseDraft c) => c.SourceState switch
    {
        CaseSourceState.Changed => "Kildegrunnlaget er endret",
        CaseSourceState.Removed => "Utgått scenario",
        CaseSourceState.SourceUnavailable => "Kilde mangler",
        _ => c.SourceChanged ? "Kildegrunnlaget er endret" : null,
    };

    public static string TestDataNeedLabel(TestDataNeed need) => need switch
    {
        TestDataNeed.Required => "Trenger testdata",
        TestDataNeed.NotRequired => "Trenger ikke testdata",
        _ => "Ikke avklart",
    };

    public static string EvidenceLabel(string kind) => kind switch
    {
        "specification" => "Spesifikasjon", "requirement" => "Krav", "story" => "Brukerhistorie", "guide-scenario" or "guide-step" => "Systemforståelse",
        "source-file" => "Kildefil", "contract-operation" => "API-kontrakt", "journey" => "Integrasjonsflyt", "developer-test" => "Utviklertest",
        "source-evidence" => "Kildebevis", _ => kind,
    };

    // ── Technical content ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Words that make a user story or a requirement about the code base rather than behaviour a person observes (English and
    /// Norwegian). Deliberately narrow: an ordinary business story is never matched by a single common word.</summary>
    [GeneratedRegex(@"\b(developers?|utviklere?|code ?base|source code|kildekode(?:n)?|identifiers?|identifikatorer?|refactor\w*|rename\w*|namespaces?|"
        + @"tests? pass|unit tests?|enhetstester?|build pipeline|ci/cd|telemetry names?|configuration names?|storage names?|logging names?|"
        + @"governing documents?|styrende dokumenter|adr[- ]?\d+|domain term inventory|translate[ds]? (?:the )?(?:source|code|identifiers?|non-domain)|"
        + @"(?:to|into) english|norwegian knowledge|norsk(?:kunnskap| kunnskap)|lint\w*|static analysis|code review)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalWords();

    public static bool IsTechnicalText(string? text) => !string.IsNullOrWhiteSpace(text) && TechnicalWords().IsMatch(text);

    /// <summary>A user story about the code base (renaming identifiers, tests passing, documents agreed …): its title or description says so.</summary>
    public static bool IsTechnicalStory(CoverageUserStory? story) => story is not null && (IsTechnicalText(story.Title) || IsTechnicalText(story.Description));

    /// <summary>Why a case counts as technical (null = shown by default). Never hides what needs a person's decision.</summary>
    public static string? TechnicalReason(TestCaseDraft c)
    {
        if (c.Source.Kind == ScenarioSourceKind.Manual) return null;                       // written by a person
        if (c.HasUnresolvedConflict) return null;                                         // sources disagree: a person decides
        if (c.Review is TestCaseReviewState.Accepted or TestCaseReviewState.NeedsClarification or TestCaseReviewState.UnderReview) return null;
        if (!c.Edits.IsEmpty) return null;                                                // a person already worked on it
        var g = c.Generated;
        switch (g.Capability)
        {
            case ScenarioOwnership.ApiContract:
                return g.CandidateType == ScenarioCandidateType.SecurityAuthorization && g.Requirements.Count > 0 ? null : "API-operasjon (teknisk)";
            case ScenarioOwnership.UiSource when g.CandidateType == ScenarioCandidateType.UiSmoke:
                return "Sidesjekk (teknisk)";
            case ScenarioOwnership.IntegrationJourneys:
                return "Integrasjonsflyt (teknisk)";
        }
        if (g.CandidateType == ScenarioCandidateType.Performance) return "Ytelse (teknisk)";
        if (IsTechnicalStory(g.Story)) return "Brukerhistorien gjelder kodebasen";
        if (IsTechnicalText(c.EffectiveTitle)) return "Scenarioet gjelder kodebasen";
        return null;
    }

    /// <summary>One scenario of the plan as the master list shows it.</summary>
    public sealed record ScenarioRow(string Key, string Title, string? StoryId, string? StoryTitle, List<TestCaseDraft> Cases, bool Technical);

    /// <summary>The plan's scenarios with their visible cases (technical ones only when <paramref name="showTechnical"/>), filtered by
    /// <paramref name="query"/> (title, story, requirement id); and how many cases are hidden as technical.</summary>
    public static (List<ScenarioRow> Rows, int HiddenTechnical) Rows(TestPlanDraft plan, bool showTechnical, string? query = null)
    {
        var q = query?.Trim() ?? "";
        var rows = new List<ScenarioRow>();
        var hidden = 0;
        foreach (var group in ScenarioPlanning.Groups(plan))
        {
            var cases = group.Cases.Where(c => c.Review != TestCaseReviewState.Superseded).ToList();
            var technical = cases.Where(c => TechnicalReason(c) is not null).ToList();
            var visible = showTechnical ? cases : cases.Except(technical).ToList();
            hidden += cases.Count - visible.Count;
            if (visible.Count == 0) continue;
            var story = group.Primary?.Generated.Story ?? cases.Select(c => c.Generated.Story).FirstOrDefault(s => s is not null);
            var title = group.Unresolved ? "Uten scenario" : ScenarioBrowserPresentation.Concise(group.Title);
            if (q.Length > 0 && !Matches(q, title, story?.StoryId, story?.Title, visible)) continue;
            rows.Add(new(group.Key, title, story?.StoryId, story?.Title, visible, technical.Count == cases.Count && cases.Count > 0));
        }
        return (rows, hidden);
    }

    private static bool Matches(string q, string title, string? storyId, string? storyTitle, IEnumerable<TestCaseDraft> cases) =>
        title.Contains(q, StringComparison.OrdinalIgnoreCase) || (storyId ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
        || (storyTitle ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
        || cases.Any(c => c.EffectiveTitle.Contains(q, StringComparison.OrdinalIgnoreCase) || RequirementIds(c).Any(r => r.Contains(q, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The functional requirements (FR) the case's own evidence names, plus the ones a tester linked; never inherited from the story.</summary>
    public static List<string> RequirementIds(TestCaseDraft c) =>
        [.. c.Generated.Evidence.Where(e => e.Kind == "requirement").Select(e => e.Reference)
            .Concat(c.RequirementLinks.Select(l => l.RequirementId))
            .Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Stable id of a case within a plan (the server's own identity: scenario id of its source).</summary>
    public static string IdOf(TestCaseDraft c) => c.Source.ScenarioId;

    // ── Readiness for review ───────────────────────────────────────────────────────────────────────────────

    /// <summary>What still blocks "Klar for gjennomgang", in plain Norwegian (empty = may be marked ready). Uses the saved case.</summary>
    public static List<string> ReadyProblems(TestCaseDraft c)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(c.EffectiveTitle)) problems.Add("Testtilfellet mangler tittel.");
        var steps = c.EffectiveSteps;
        if (steps.Count == 0 || steps.All(string.IsNullOrWhiteSpace)) problems.Add("Legg til minst ett teststeg.");
        var stepExpected = c.EffectiveStepExpected;
        if (string.IsNullOrWhiteSpace(c.EffectiveExpected) && stepExpected.All(e => e is null)) problems.Add("Skriv forventet resultat (for testtilfellet eller for et steg).");
        if (c.HasUnresolvedConflict) problems.Add("Kildene er uenige om forventet resultat: avklar konflikten først.");
        return problems;
    }

    // ── Editor working copy ────────────────────────────────────────────────────────────────────────────────

    public sealed class StepRow
    {
        public string Action { get; set; } = "";
        public string Expected { get; set; } = "";
        /// <summary>Stable key for rendering while steps move (never sent).</summary>
        public Guid Key { get; } = Guid.NewGuid();
    }

    /// <summary>The editor's working copy of one case. <see cref="IsDirty"/> compares with the saved case it was opened from.</summary>
    public sealed class CaseForm
    {
        public string CaseId { get; init; } = "";
        public string Title { get; set; } = "";
        public string Purpose { get; set; } = "";
        public string Preconditions { get; set; } = "";
        public string ExpectedResult { get; set; } = "";
        public TestDataNeed TestDataNeed { get; set; }
        public List<StepRow> Steps { get; set; } = [];

        public static CaseForm From(TestCaseDraft c) => new()
        {
            CaseId = IdOf(c),
            Title = c.EffectiveTitle,
            Purpose = c.EffectivePurpose,
            Preconditions = c.EffectivePreconditions ?? "",
            ExpectedResult = c.EffectiveExpected ?? "",
            TestDataNeed = c.EffectiveTestDataNeed,
            Steps = [.. c.EffectiveSteps.Select((s, i) => new StepRow { Action = s, Expected = c.EffectiveStepExpected.ElementAtOrDefault(i) ?? "" })],
        };

        public void AddStep() => Steps.Add(new StepRow());

        public void RemoveStep(int index) { if (index >= 0 && index < Steps.Count) Steps.RemoveAt(index); }

        /// <summary>Moves a step up (-1) or down (+1); a move past either end does nothing.</summary>
        public void MoveStep(int index, int delta)
        {
            var to = index + delta;
            if (index < 0 || index >= Steps.Count || to < 0 || to >= Steps.Count) return;
            (Steps[index], Steps[to]) = (Steps[to], Steps[index]);
        }

        private List<StepRow> Kept => [.. Steps.Where(s => !string.IsNullOrWhiteSpace(s.Action) || !string.IsNullOrWhiteSpace(s.Expected))];

        /// <summary>The human-owned edits after this form: unchanged fields keep the case's existing edit (or none); a value equal to the
        /// generated one means "no edit". Fields the form does not show (test type, risk, template values …) are kept as they are.</summary>
        public TestCaseEdits ToEdits(TestCaseDraft saved)
        {
            var g = saved.Generated;
            var steps = Kept;
            var actions = steps.Select(s => s.Action.Trim()).ToList();
            var expected = steps.Select(s => string.IsNullOrWhiteSpace(s.Expected) ? null : s.Expected.Trim()).ToList();
            var generatedExpected = g.Steps.Select((_, i) => i < g.StepExpected.Count && !string.IsNullOrWhiteSpace(g.StepExpected[i]) ? g.StepExpected[i]!.Trim() : null).ToList();
            var stepsSame = actions.SequenceEqual(g.Steps.Select(s => s.Trim())) && expected.SequenceEqual(generatedExpected);
            return saved.Edits with
            {
                Title = TestPlanSuggestionsPresentation.TextEdit(g.Title, Title),
                Purpose = TestPlanSuggestionsPresentation.TextEdit(g.Purpose, Purpose),
                Preconditions = TestPlanSuggestionsPresentation.TextEdit(g.Preconditions, Preconditions),
                ExpectedResult = TestPlanSuggestionsPresentation.TextEdit(g.ExpectedResult, ExpectedResult),
                Steps = stepsSame ? null : actions,
                StepExpected = stepsSame ? null : expected,
                TestDataNeed = TestDataNeed == g.TestDataNeed ? null : TestDataNeed,
            };
        }

        /// <summary>Whether the form differs from the saved case (what "unsaved changes" means).</summary>
        public bool IsDirty(TestCaseDraft saved)
        {
            var now = From(saved);
            return !string.Equals(Title.Trim(), now.Title.Trim(), StringComparison.Ordinal)
                || !string.Equals(Purpose.Trim(), now.Purpose.Trim(), StringComparison.Ordinal)
                || !string.Equals(Preconditions.Trim(), now.Preconditions.Trim(), StringComparison.Ordinal)
                || !string.Equals(ExpectedResult.Trim(), now.ExpectedResult.Trim(), StringComparison.Ordinal)
                || TestDataNeed != now.TestDataNeed
                || !Kept.Select(s => (s.Action.Trim(), s.Expected.Trim())).SequenceEqual(now.Kept.Select(s => (s.Action.Trim(), s.Expected.Trim())));
        }
    }

    /// <summary>The save request with one case's form applied (all other cases sent as stored).</summary>
    public static SaveTestPlanDraftRequest SaveWith(TestPlanDraft plan, CaseForm form)
    {
        var changed = TestPlanSuggestionsPresentation.WithCase(plan, form.CaseId, c => c with { Edits = form.ToEdits(c) });
        return TestPlanSuggestionsPresentation.ToSaveRequest(changed);
    }
}
