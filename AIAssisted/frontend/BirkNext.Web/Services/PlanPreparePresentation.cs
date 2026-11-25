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

    /// <summary>Technical for manual testing: the shared rule (<see cref="ManualTestingRelevance"/>) — about the code base or its operation and
    /// nothing a person could observe. Uncertain text is never technical.</summary>
    public static bool IsTechnicalText(string? text) => ManualTestingRelevance.IsTechnical(text);

    /// <summary>A user story about the code base (renaming identifiers, tests passing, documents agreed …).</summary>
    public static bool IsTechnicalStory(CoverageUserStory? story) => ManualTestingRelevance.StoryReason(story) is not null;

    /// <summary>A functional requirement about the code base, by its wording (null text = unknown = not technical).</summary>
    public static bool IsTechnicalRequirement(string? text) => ManualTestingRelevance.RequirementReason(text) is not null;

    /// <summary>Why a case counts as technical (null = shown by default). Never hides what needs a person's decision.</summary>
    public static string? TechnicalReason(TestCaseDraft c)
    {
        // The saved case's own type decides first (2026-11-25): the list filters the entity it shows.
        if (c.Edits.TestKind is { } chosen) return chosen == ScenarioTestKind.Technical ? "Testtypen er Teknisk" : null;
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
    public sealed record ScenarioRow(string Key, string Title, string? StoryId, string? StoryTitle, List<TestCaseDraft> Cases, bool Technical)
    {
        /// <summary>The acceptance scenario badge («AC 2»), from the same saved identity the detail heading uses; null when there is none.</summary>
        public string? Ac { get; init; }
        /// <summary>The scenario's cases name more than one acceptance scenario: no number is chosen (shown as «AC ?»).</summary>
        public bool AcAmbiguous { get; init; }
    }

    /// <summary>
    /// The acceptance scenario of a scenario row (2026-11-25), grounded in the saved source identity — the same <c>Generated.Story</c> +
    /// <c>Generated.NumberInStory</c> that <see cref="CaseEditorPresentation.ScenarioHeading"/> shows in the detail: the scenario's own source case
    /// first, otherwise its other source cases. Exactly one (story, number) that belongs to the row's story → «AC n». None (hand-written cases, a
    /// scenario without a number) → no badge. More than one, or a number under another story → ambiguous, never a guess. Never from list position,
    /// ordering, titles or requirement numbers.
    /// </summary>
    public static (string? Label, bool Ambiguous) AcOf(TestCaseDraft? primary, IEnumerable<TestCaseDraft> cases, CoverageUserStory? story)
    {
        var sources = (primary is not null ? new[] { primary } : []).Concat(cases)
            .Where(c => c.Source.Kind != ScenarioSourceKind.Manual && c.Generated.Story is not null && c.Generated.NumberInStory is not null)
            .Select(c => (StoryKey: c.Generated.Story!.Key, Number: c.Generated.NumberInStory!.Value))
            .Distinct().ToList();
        if (sources.Count == 0) return (null, false);
        if (sources.Count > 1 || story is null || sources[0].StoryKey != story.Key) return (null, true);
        return (ScenarioWorkspacePresentation.AcLabel(sources[0].Number), false);
    }

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
            var (ac, acAmbiguous) = group.Unresolved ? (null, false) : AcOf(group.Primary, cases, story);
            if (q.Length > 0 && !Matches(q, title, story?.StoryId, story?.Title, visible) && !(ac is not null && Compact(ac).Contains(Compact(q), StringComparison.OrdinalIgnoreCase))) continue;
            rows.Add(new(group.Key, title, story?.StoryId, story?.Title, visible, technical.Count == cases.Count && cases.Count > 0) { Ac = ac, AcAmbiguous = acAmbiguous });
        }
        return (rows, hidden);
    }

    private static string Compact(string s) => s.Replace(" ", "", StringComparison.Ordinal);

    private static bool Matches(string q, string title, string? storyId, string? storyTitle, IEnumerable<TestCaseDraft> cases) =>
        title.Contains(q, StringComparison.OrdinalIgnoreCase) || (storyId ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
        || (storyTitle ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
        || cases.Any(c => c.EffectiveTitle.Contains(q, StringComparison.OrdinalIgnoreCase) || RequirementIds(c).Any(r => r.Contains(q, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The functional requirements (FR) the case's own evidence names, plus the ones a tester linked; never inherited from the story.</summary>
    public static List<string> RequirementIds(TestCaseDraft c) =>
        [.. c.Generated.Evidence.Where(e => e.Kind == "requirement").Select(e => e.Reference)
            .Concat(c.RequirementLinks.Select(l => l.RequirementId))
            .Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The specifications a saved plan's cases were prepared from, when none of them is the specification selected now (2026-11-25): a plan prepared for
    /// SkoleService stays as it is, but it is never presented as belonging to HendelseAdapter. Null when nothing is selected, the plan names no
    /// specification, or the selected one is among them.
    /// </summary>
    public static IReadOnlyList<string>? OtherSpecifications(TestPlanDraft plan, string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath)) return null;
        static string N(string p) => p.Replace('\\', '/').Trim().ToLowerInvariant();
        var paths = plan.Cases.Select(c => c.Generated.Story?.SpecificationPath).OfType<string>().Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0) return null;
        var selected = N(selectedPath);
        return paths.Any(p => N(p) == selected || N(p).EndsWith("/" + selected, StringComparison.Ordinal) || selected.EndsWith("/" + N(p), StringComparison.Ordinal)) ? null : paths;
    }

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
        /// <summary>The step in the translation language (shown when the case has a translation).</summary>
        public string TranslatedAction { get; set; } = "";
        public string TranslatedExpected { get; set; } = "";
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
        /// <summary>Manuell or Teknisk for this case (null = «Ikke angitt»; an older case is never given one it was not given).</summary>
        public ScenarioTestKind? TestKind { get; set; }
        /// <summary>How the result is observed, written by a person (2026-11-25); the readiness «Verifiseringsmetode» needs it.</summary>
        public string ObservationMethod { get; set; } = "";
        /// <summary>Access, environment and systems the test needs, written by a person («Tilgang og miljø»).</summary>
        public string EnvironmentPrerequisites { get; set; } = "";
        /// <summary>What stops the test from starting now (responsible / note included as the tester writes it); empty = nothing recorded.</summary>
        public string BlockedBy { get; set; } = "";
        public List<StepRow> Steps { get; set; } = [];
        /// <summary>Linked Test Data assets (immutable asset ids; values are never copied).</summary>
        public List<string> TestDataAssetIds { get; set; } = [];
        /// <summary>The Norwegian translation as the reviewer sees and edits it (null = the case has none).</summary>
        public string? TranslatedTitle { get; set; }
        public string TranslatedPurpose { get; set; } = "";
        public string TranslatedPreconditions { get; set; } = "";
        public string TranslatedExpected { get; set; } = "";
        public bool HasTranslation => TranslatedTitle is not null;

        public static CaseForm From(TestCaseDraft c)
        {
            var form = FromOriginal(c);
            form.TestDataAssetIds = [.. c.TestData.Select(d => d.AssetId)];
            if (c.Translation(ScenarioPlanning.Norwegian) is { } nb)
            {
                form.TranslatedTitle = nb.Title ?? "";
                form.TranslatedPurpose = nb.Purpose ?? "";
                form.TranslatedPreconditions = nb.Preconditions ?? "";
                form.TranslatedExpected = nb.ExpectedResult ?? "";
                for (var i = 0; i < form.Steps.Count; i++)
                {
                    form.Steps[i].TranslatedAction = nb.Steps.ElementAtOrDefault(i) ?? "";
                    form.Steps[i].TranslatedExpected = nb.StepExpected.ElementAtOrDefault(i) ?? "";
                }
            }
            return form;
        }

        private static CaseForm FromOriginal(TestCaseDraft c) => new()
        {
            CaseId = IdOf(c),
            Title = c.EffectiveTitle,
            Purpose = c.EffectivePurpose,
            Preconditions = c.EffectivePreconditions ?? "",
            ExpectedResult = c.EffectiveExpected ?? "",
            TestDataNeed = c.EffectiveTestDataNeed,
            TestKind = c.Edits.TestKind,
            ObservationMethod = c.Edits.ObservationMethod ?? "",
            EnvironmentPrerequisites = c.Edits.EnvironmentPrerequisites ?? "",
            BlockedBy = c.Edits.BlockedBy ?? "",
            Steps = [.. c.EffectiveSteps.Select((s, i) => new StepRow { Action = s, Expected = c.EffectiveStepExpected.ElementAtOrDefault(i) ?? "" })],
        };

        /// <summary>The translation after this form, or the held one unchanged (same instance) when the reviewer changed nothing. The server
        /// decides origin (machine to edited) and binds it to the original text; a translation is never created here from nothing.</summary>
        public TestCaseTranslation? ToTranslation(TestCaseDraft saved)
        {
            var held = saved.Translation(ScenarioPlanning.Norwegian);
            if (held is null || TranslatedTitle is null) return held;
            var steps = Kept;
            var next = held with
            {
                Title = Blank(TranslatedTitle), Purpose = Blank(TranslatedPurpose), Preconditions = Blank(TranslatedPreconditions), ExpectedResult = Blank(TranslatedExpected),
                Steps = [.. steps.Select(r => r.TranslatedAction.Trim())], StepExpected = [.. steps.Select(r => Blank(r.TranslatedExpected))],
            };
            return SameText(held, next) ? held : next;
        }

        private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        private static bool SameText(TestCaseTranslation a, TestCaseTranslation b) =>
            (a.Title ?? "") == (b.Title ?? "") && (a.Purpose ?? "") == (b.Purpose ?? "") && (a.Preconditions ?? "") == (b.Preconditions ?? "") && (a.ExpectedResult ?? "") == (b.ExpectedResult ?? "")
            && a.Steps.Select(x => x ?? "").SequenceEqual(b.Steps.Select(x => x ?? "")) && a.StepExpected.Select(x => x ?? "").SequenceEqual(b.StepExpected.Select(x => x ?? ""));

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
                TestKind = TestKind,
                ObservationMethod = Blank(ObservationMethod),
                EnvironmentPrerequisites = Blank(EnvironmentPrerequisites),
                BlockedBy = Blank(BlockedBy),
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
                || TestKind != now.TestKind
                || !string.Equals(ObservationMethod.Trim(), now.ObservationMethod.Trim(), StringComparison.Ordinal)
                || !string.Equals(EnvironmentPrerequisites.Trim(), now.EnvironmentPrerequisites.Trim(), StringComparison.Ordinal)
                || !string.Equals(BlockedBy.Trim(), now.BlockedBy.Trim(), StringComparison.Ordinal)
                || !TestDataAssetIds.Order(StringComparer.Ordinal).SequenceEqual(now.TestDataAssetIds.Order(StringComparer.Ordinal))
                || !Kept.Select(s => (s.Action.Trim(), s.Expected.Trim())).SequenceEqual(now.Kept.Select(s => (s.Action.Trim(), s.Expected.Trim())))
                || !ReferenceEquals(ToTranslation(saved), saved.Translation(ScenarioPlanning.Norwegian));
        }
    }

    /// <summary>The save request with one case's form applied (all other cases sent as stored).</summary>
    public static SaveTestPlanDraftRequest SaveWith(TestPlanDraft plan, CaseForm form)
    {
        var changed = TestPlanSuggestionsPresentation.WithCase(plan, form.CaseId, c =>
        {
            var next = c with
            {
                Edits = form.ToEdits(c),
                TestData = [.. form.TestDataAssetIds.Distinct(StringComparer.Ordinal).Select(id => c.TestData.FirstOrDefault(d => d.AssetId == id) ?? new TestCaseDataLink { AssetId = id, Alias = id })],
            };
            return form.ToTranslation(c) is { } t && !ReferenceEquals(t, c.Translation(ScenarioPlanning.Norwegian)) ? next.WithTranslation(ScenarioPlanning.Norwegian, t) : next;
        });
        return TestPlanSuggestionsPresentation.ToSaveRequest(changed);
    }
}

/// <summary>Plan-level labels for Planlegg og klargjør and the saved-plan list.</summary>
public static partial class PlanPreparePresentation
{
    /// <summary>A Test Data asset as the picker shows it: its name, whether it is approved, and the environment it is recorded for — what the catalogue
    /// knows, nothing more (an asset in the catalogue is not proof the data exists in QA).</summary>
    public static string TestDataOptionLabel(TestDataOption o) =>
        $"{o.Alias}{(o.Label is { Length: > 0 } l ? $" — {l}" : "")} · {(string.Equals(o.Approval, "Approved", StringComparison.OrdinalIgnoreCase) ? "godkjent" : "ikke godkjent")}"
        + (o.Environment is { Length: > 0 } env ? $" · miljø: {env}" : " · miljø ikke oppgitt");

    public sealed record HistoryEntry(DateTimeOffset At, string By, string What);

    /// <summary>A case's edit and review history in the tester's words, newest first (field names translated; anything else shown as recorded).</summary>
    public static IReadOnlyList<HistoryEntry> CaseHistory(TestCaseDraft c) =>
        c.EditHistory.Select(e => new HistoryEntry(e.At, e.Editor, "Endret: " + string.Join(", ", e.Fields.Select(FieldLabel))))
            .Concat(c.ReviewHistory.Select(r => new HistoryEntry(r.At, r.Reviewer, $"Vurdering: {ReviewLabel(r.State)}{(string.IsNullOrWhiteSpace(r.Rationale) ? "" : $" — {r.Rationale}")}")))
            .OrderByDescending(h => h.At).ToList();

    public static string FieldLabel(string field) => field switch
    {
        "Title" => "tittel", "Purpose" => "formål", "Preconditions" => "forutsetninger", "Steps" => "teststeg", "Step results" => "forventet per steg",
        "Expected result" => "forventet resultat", "Observation method" => "slik ser du resultatet", "Environment" => "tilgang og miljø", "Test type" => "type",
        "Execution method" => "gjennomføringsmåte", "Manual or technical" => "testtype (manuell/teknisk)", "Test Data need" => "testdatabehov", "Blocked by" => "hindring",
        "Priority" => "prioritet", "Roles" => "roller", "Dependencies" => "avhengigheter", "Template values" => "malverdier", "Performance" => "ytelse",
        _ when field.StartsWith("Test type: ", StringComparison.Ordinal) => "testtype satt til " + (field.EndsWith("Technical", StringComparison.Ordinal) ? "teknisk" : "manuell"),
        _ => field,
    };

    public static string CaseKindLabel(ScenarioTestKind? kind) => kind switch
    {
        ScenarioTestKind.Manual => "Manuell",
        ScenarioTestKind.Technical => "Teknisk",
        ScenarioTestKind.Both => "Begge",
        _ => "Ikke angitt",
    };

    /// <summary>
    /// What the scenario suggests for a case's type (2026-11-25), never a decision for the case: a reviewer's Manuell/Teknisk decision from Finn og
    /// forstå (<see cref="Reviewed"/>; may be preselected), else the scenario's execution suitability as Finn og forstå shows it (labels only; nothing
    /// is preselected, so «Manuell + teknisk» leaves both open). Null for a case written without a scenario.
    /// </summary>
    public sealed record KindSuggestion(ScenarioTestKind? Reviewed, string? ReviewedBy, IReadOnlyList<string> Approaches);

    public static KindSuggestion? SuggestKind(TestCaseDraft? scenarioCase, ScenarioTestKindDecision? decision)
    {
        if (scenarioCase is null && decision is null) return null;
        var approaches = scenarioCase is null ? [] : TestDesignAdvisor.Suitability(TestDesignAdvisor.FactsOf(scenarioCase)).Approaches
            .Select(a => TestDesignAdvisor.ApproachLabel(a.Approach)).Distinct().Take(3).ToList();
        return new KindSuggestion(decision?.Kind, decision?.By, approaches);
    }

    public static string SuggestionText(KindSuggestion? s) => s switch
    {
        { Reviewed: ScenarioTestKind.Both } => $"Scenarioet er vurdert som både manuelt og teknisk i Finn og forstå{(s.ReviewedBy is { Length: > 0 } both ? $" ({both})" : "")}. Velg testtype for dette testtilfellet.",
        { Reviewed: { } k } => $"Scenarioet er vurdert som {CaseKindLabel(k).ToLowerInvariant()} i Finn og forstå{(s.ReviewedBy is { Length: > 0 } by ? $" ({by})" : "")}. Et forslag — du velger for testtilfellet.",
        { Approaches.Count: > 0 } => $"Anbefalt for scenarioet: {string.Join(" · ", s.Approaches)}. Velg testtype for testtilfellet.",
        _ => "Velg testtype for testtilfellet.",
    };

    /// <summary>The translation's state in the tester's words, or null when the case has none.</summary>
    public static (string Text, string Tone)? TranslationStatus(TestCaseDraft c)
    {
        if (c.Translation(ScenarioPlanning.Norwegian) is not { } nb) return null;
        if (ScenarioPlanning.StateOf(c) == TranslationState.SourceChanged) return ("Oversatt fra en eldre tekst — gjennomgå", "warn");
        return nb.Origin switch
        {
            TranslationOrigin.Machine => ("Maskinoversatt — ikke gjennomgått", "info"),
            TranslationOrigin.MachineEdited => ("Maskinoversatt og redigert", "ok"),
            _ => ("Skrevet av en person", "ok"),
        };
    }

    /// <summary>A server message in the tester's words (stale saves and archived plans are the common ones).</summary>
    public static string Friendly(string? error, string fallback) => error switch
    {
        null or "" => fallback,
        var e when e.Contains("changed by someone else", StringComparison.OrdinalIgnoreCase) || e.Contains("is at revision", StringComparison.OrdinalIgnoreCase)
            => "Planen er endret et annet sted siden du åpnet den. Endringene dine er ikke lagret; last siden på nytt og gjør dem igjen.",
        var e => e,
    };

    public static string LifecycleLabel(TestPlanLifecycle state) => state switch { TestPlanLifecycle.Archived => "Arkivert", TestPlanLifecycle.Deleted => "Slettet", _ => "Aktiv" };
}

/// <summary>Plan facts and the expected-result basis (never "verified" unless a person decided it).</summary>
public static partial class PlanPreparePresentation
{
    /// <summary>Distinct scenarios the plan's cases belong to (hand-written cases without a scenario are not counted).</summary>
    public static int ScenarioCount(TestPlanDraft plan) =>
        plan.Cases.Where(c => c.Review != TestCaseReviewState.Superseded).Select(c => c.ParentScenario).OfType<string>().Distinct(StringComparer.Ordinal).Count();

    /// <summary>Why the expected result is what it is: the source that states it, and whether a person confirmed it.</summary>
    public static string ExpectedBasisText(TestCaseDraft c)
    {
        if (c.ConflictResolution is { } resolved) return $"Avklart av {resolved.ResolvedBy} {resolved.ResolvedAt.ToLocalTime():yyyy-MM-dd}: {resolved.Rationale}";
        if (c.HasUnresolvedConflict) return "Kildene er uenige — avklar konflikten.";
        if (c.Edits.ExpectedResult is not null) return "Skrevet av testeren" + (c.Review == TestCaseReviewState.Accepted ? " og godkjent." : "; bekreftes når testtilfellet godkjennes.");
        var confirmed = c.Review == TestCaseReviewState.Accepted ? " Bekreftet ved godkjenning." : " Bekreftes når testtilfellet godkjennes.";
        return c.Generated.ExpectedBasis switch
        {
            ExpectedResultBasis.Specification => "Står i spesifikasjonen (akseptansekriterium eller krav)." + confirmed,
            ExpectedResultBasis.Contract => "Dokumentert i API-kontrakten." + confirmed,
            ExpectedResultBasis.ReviewedGuide => "Står i en gjennomgått systembeskrivelse (Systemforståelse)." + confirmed,
            ExpectedResultBasis.UnreviewedGuide => "Fra en systembeskrivelse som ikke er gjennomgått — må bekreftes.",
            ExpectedResultBasis.ReviewerClarified => "Avklart av en gjennomganger.",
            ExpectedResultBasis.SourceDerived => "Foreslått fra kildekoden. Implementasjonen er ingen fasit — bekreft eller avklar.",
            ExpectedResultBasis.NeedsClarification => "Kilden er uklar — trenger avklaring.",
            _ => "Ikke oppgitt i kilden — skriv forventet resultat selv eller avklar.",
        };
    }
}
