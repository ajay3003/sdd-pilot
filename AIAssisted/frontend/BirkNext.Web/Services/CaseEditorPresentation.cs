using System.Text.RegularExpressions;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>Which language the one test-case editor shows: the Norwegian translation, the original, or both side by side.</summary>
public enum EditorLanguage { Norwegian, Original, Compare }

/// <summary>
/// The unified test-case editor (2026-11-24): one form, one step list, three views of it. Nothing here translates — a translation exists
/// only when the existing Translate action made it (or a person wrote it); the editor shows, edits and flags it.
/// </summary>
public static partial class PlanPreparePresentation
{
    /// <summary>
    /// The story text to show in a language (2026-11-25): Norwegian = the plan's stored translation («Oversett alle» translates stories with their
    /// cases) when there is one; <see cref="StoryText.Stale"/> when the story changed after it was translated. Never a machine translation on the fly.
    /// </summary>
    public sealed record StoryText(string Title, string? Description, string? Why, string? IndependentTest, bool Translated, bool Stale, string? Provider);

    public static StoryText StoryIn(TestPlanDraft? plan, BirkNext.TestCoverage.CoverageUserStory us, bool norwegian)
    {
        var original = new StoryText(ScenarioWorkspacePresentation.CleanStoryTitle(us.Title), us.Description, us.WhyThisPriority, us.IndependentTest, false, false, null);
        if (!norwegian) return original;
        var nb = plan?.StoryTranslations?.FirstOrDefault(t => t.StoryKey == us.Key && string.Equals(t.Language, ScenarioPlanning.Norwegian, StringComparison.OrdinalIgnoreCase));
        if (nb is null || string.IsNullOrWhiteSpace(nb.Title) && string.IsNullOrWhiteSpace(nb.Description)) return original;
        return new StoryText(ScenarioWorkspacePresentation.CleanStoryTitle(nb.Title ?? us.Title), nb.Description ?? us.Description, nb.WhyThisPriority ?? us.WhyThisPriority,
            nb.IndependentTest ?? us.IndependentTest, true, nb.SourceFingerprint != ScenarioPlanning.StoryFingerprint(us), nb.Provider);
    }

    /// <summary>The scenario heading of a case: "AC 3 — <scenario title>" when the case comes from a story's numbered acceptance scenario.</summary>
    public static string ScenarioHeading(TestCaseDraft c)
    {
        var title = c.Generated.Title;
        if (c.Generated.Story?.StoryId is { Length: > 0 } id && title.StartsWith(id + " — ", StringComparison.Ordinal)) title = title[(id.Length + 3)..];
        return c.Generated.Story is not null && c.Generated.NumberInStory is { } n ? $"{ScenarioWorkspacePresentation.AcLabel(n)} — {title}" : title;
    }

    /// <summary>The scenario heading in the editor's language (2026-11-26): Norsk/Sammenlign use the case's stored translation of the scenario title when
    /// it is current and the title was not rewritten by a person; otherwise the original (never a fresh translation).</summary>
    public static string ScenarioHeading(TestCaseDraft c, EditorLanguage language)
    {
        if (language == EditorLanguage.Original || c.Edits.Title is not null || c.Translation(ScenarioPlanning.Norwegian) is not { Title: { Length: > 0 } nbTitle } nb
            || nb.SourceFingerprint != ScenarioPlanning.SourceFingerprint(c)) return ScenarioHeading(c);
        var title = c.Generated.Story?.StoryId is { Length: > 0 } id && nbTitle.StartsWith(id + " — ", StringComparison.Ordinal) ? nbTitle[(id.Length + 3)..] : nbTitle;
        return c.Generated.Story is not null && c.Generated.NumberInStory is { } n ? $"{ScenarioWorkspacePresentation.AcLabel(n)} — {title}" : title;
    }

    /// <summary>Norwegian when a translation exists, otherwise the original. Never "translate on open".</summary>
    public static EditorLanguage DefaultLanguage(CaseForm form) => form.HasTranslation ? EditorLanguage.Norwegian : EditorLanguage.Original;

    public static string LanguageLabel(EditorLanguage language) => language switch
    {
        EditorLanguage.Norwegian => "Norsk",
        EditorLanguage.Original => "Original",
        _ => "Sammenlign",
    };

    /// <summary>Fields the original has text in and the translation does not (a partial translation is shown as such, never filled in).</summary>
    public static IReadOnlyList<string> UntranslatedFields(CaseForm form)
    {
        if (!form.HasTranslation) return [];
        var missing = new List<string>();
        void Check(string label, string? original, string? translated)
        {
            if (!string.IsNullOrWhiteSpace(original) && string.IsNullOrWhiteSpace(translated)) missing.Add(label);
        }
        Check("Tittel", form.Title, form.TranslatedTitle);
        Check("Formål", form.Purpose, form.TranslatedPurpose);
        Check("Forutsetninger", form.Preconditions, form.TranslatedPreconditions);
        for (var i = 0; i < form.Steps.Count; i++)
        {
            Check($"Steg {i + 1} handling", form.Steps[i].Action, form.Steps[i].TranslatedAction);
            Check($"Steg {i + 1} forventet resultat", form.Steps[i].Expected, form.Steps[i].TranslatedExpected);
        }
        Check("Samlet forventet resultat", form.ExpectedResult, form.TranslatedExpected);
        return missing;
    }

    /// <summary>Whether one field is missing in the translation (the editor marks it "Ikke oversatt").</summary>
    public static bool IsUntranslated(CaseForm form, string? original, string? translated) =>
        form.HasTranslation && !string.IsNullOrWhiteSpace(original) && string.IsNullOrWhiteSpace(translated);

    /// <summary>Whether the reviewer owns the translation (edited or written by a person): a new machine translation keeps it.</summary>
    public static bool IsProtectedTranslation(TestCaseDraft c) =>
        c.Translation(ScenarioPlanning.Norwegian) is { Origin: TranslationOrigin.MachineEdited or TranslationOrigin.Manual };

    [GeneratedRegex(@"^(it|they|this|that|det|den|de|dette)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingPronoun();

    [GeneratedRegex(@"^(?:(?:it|the system|systemet|everything|alt|this|det)\s+)?(?:works|work|fungerer|virker|is ok|er ok|as expected|som forventet|correctly|riktig|ok)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex Vague();

    [GeneratedRegex(@"^\s*(?:trigger and observe|verify the result|check the (?:behaviou?r|result)|process the record|observe the result|utløs og observer|kontroller (?:resultatet|oppførselen)|verifiser resultatet|behandle posten)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GenericAction();

    [GeneratedRegex(@"must be specified|må angis|må spesifiseres|ikke oppgitt|not stated", RegexOptions.IgnoreCase)]
    private static partial Regex OpenAction();

    [GeneratedRegex(@"\b(vises|viser|lagres|lagret|returnerer|returns?|stored|displayed|shown|publiseres|published|status|melding|message|feilmelding|error|logg|log|rapport|report|svar|response|HTTP\s*\d{3}|\d{3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex Observable();

    /// <summary>Why one step is not yet actionable for a manual tester (2026-11-26) — a warning only: the step is never rewritten, an approved case
    /// stays approved, and nothing is invented (no endpoint, payload, query or environment). Null = the step reads as actionable.</summary>
    public static IReadOnlyList<string>? StepWarning(string? action, string? expected)
    {
        var reasons = new List<string>();
        var a = (action ?? "").Trim();
        var e = (expected ?? "").Trim();
        if (a.Length == 0 && e.Length == 0) return null;
        if (a.Length == 0) reasons.Add("Mangler handling: hva gjør testeren?");
        else if (GenericAction().IsMatch(a)) reasons.Add("Handlingen er generell: angi hva som utløser hendelsen og hvor.");
        else if (OpenAction().IsMatch(a)) reasons.Add("Kilden sier ikke hvordan dette utløses i testmiljøet — avklar før gjennomføring.");
        if (e.Length == 0) reasons.Add("Mangler forventet resultat for steget.");
        else if (Vague().IsMatch(e) || !Observable().IsMatch(e) && e.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 4)
            reasons.Add("Forventet resultat er ikke observerbart: hva ser testeren, og hvor?");
        return reasons.Count == 0 ? null : reasons;
    }

    /// <summary>Quality guidance on what a tester will actually do and check — advice, never a block (the review rules stay the gate).</summary>
    public static IReadOnlyList<string> QualityHints(CaseForm form)
    {
        var hints = new List<string>();
        var steps = form.Steps.Where(s => !string.IsNullOrWhiteSpace(s.Action) || !string.IsNullOrWhiteSpace(s.Expected)).ToList();
        if (steps.Count == 0) hints.Add("Ingen teststeg: skriv hva testeren gjør.");
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            if (string.IsNullOrWhiteSpace(s.Action)) hints.Add($"Steg {i + 1} har forventet resultat, men ingen handling.");
            else if (LeadingPronoun().IsMatch(s.Action.Trim())) hints.Add($"Steg {i + 1} begynner med «{LeadingPronoun().Match(s.Action.Trim()).Value}» — skriv hvem som handler eller hva som sendes.");
            if (!string.IsNullOrWhiteSpace(s.Expected) && Vague().IsMatch(s.Expected.Trim())) hints.Add($"Forventet resultat i steg {i + 1} er vagt — skriv hva testeren skal se.");
        }
        if (string.IsNullOrWhiteSpace(form.ExpectedResult) && steps.All(s => string.IsNullOrWhiteSpace(s.Expected)))
            hints.Add("Ingen forventet resultat: testen kan ikke bestås eller feile.");
        else if (!string.IsNullOrWhiteSpace(form.ExpectedResult) && Vague().IsMatch(form.ExpectedResult.Trim()))
            hints.Add("Samlet forventet resultat er vagt — skriv hva testeren skal se.");
        if (string.IsNullOrWhiteSpace(form.Preconditions) && form.TestDataNeed == TestDataNeed.Required && form.TestDataAssetIds.Count == 0)
            hints.Add("Testdata kreves, men verken forutsetninger eller testdata er oppgitt.");
        return hints;
    }

    /// <summary>
    /// «Uavhengig test» as test preparation (2026-11-26): the actions to perform, each with the expected results the source states after it, in
    /// source order. Expected results before the first action (or without any action) form a step with no action. Only an expected result can be
    /// «nevnt i et testtilfelle» (the literal word match against manual cases) — an action is never a gap, and nothing here is developer-test coverage.
    /// </summary>
    public sealed record PreparationStep(string? Action, IReadOnlyList<ObjectiveCoverage> Expected, bool ActionInterpreted = false, string? ActionSource = null);

    public static IReadOnlyList<PreparationStep> PreparationSteps(IReadOnlyList<ObjectiveCoverage> objectives)
    {
        var steps = new List<PreparationStep>();
        string? action = null; StoryTestObjective? actionObjective = null;
        var expected = new List<ObjectiveCoverage>();
        void Flush() { if (action is not null || expected.Count > 0) steps.Add(new(action, [.. expected], actionObjective?.NeedsReview == true, actionObjective?.Source)); action = null; actionObjective = null; expected = []; }
        foreach (var o in objectives)
        {
            if (o.Objective.Kind == StoryObjectiveKind.Condition) { Flush(); action = o.Objective.Text; actionObjective = o.Objective; }
            else expected.Add(o);
        }
        Flush();
        return steps;
    }

    /// <summary>One objective of the user story's Independent Test and the plan's cases that appear to address it.</summary>
    public sealed record ObjectiveCoverage(StoryTestObjective Objective, IReadOnlyList<string> CaseTitles)
    {
        public bool Covered => CaseTitles.Count > 0;
    }

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "that", "this", "with", "from", "into", "than", "then", "there", "their", "they", "have", "been", "were", "will", "when", "where", "which", "while",
        "anywhere", "afterwards", "exists", "exist", "known", "reports", "report", "verify", "check", "each", "every", "only", "also",
    };

    /// <summary>A plain singular form for matching only ("children" → "child", "reports" → "report", "identifiers" → "identifier"); never shown.</summary>
    internal static string Stem(string word) => word switch
    {
        "children" => "child", "barna" or "barnet" => "barn",
        _ when word.Length > 5 && word.EndsWith("ies", StringComparison.Ordinal) => word[..^3] + "y",
        _ when word.Length > 4 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) => word[..^1],
        _ => word,
    };

    private static IEnumerable<string> ContentWords(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(m => m.Value).Where(w => w.Length >= 4 && !Stop.Contains(w)).Distinct();

    /// <summary>
    /// What the story's Independent Test asks for and which cases in this plan appear to address each objective. "Appears" is literal: the
    /// objective's content words are found in a case of the same story (title, preconditions, steps, expected). A suggestion, never a verdict.
    /// </summary>
    public static IReadOnlyList<ObjectiveCoverage> StoryObjectiveCoverage(TestPlanDraft plan, CoverageUserStory story)
    {
        var cases = plan.Cases.Where(c => c.Review is not (TestCaseReviewState.Excluded or TestCaseReviewState.Superseded)
            && c.Generated.Story is { } s && string.Equals(s.Key, story.Key, StringComparison.Ordinal)).ToList();
        return ObjectiveCoverageOf(story, cases.Select(c => (c.EffectiveTitle, string.Join(" ",
            new[] { c.EffectiveTitle, c.EffectivePurpose, c.EffectivePreconditions, c.EffectiveExpected }.Concat(c.EffectiveSteps).Concat(c.EffectiveStepExpected)
                .Where(x => !string.IsNullOrWhiteSpace(x))))));
    }

    /// <summary>The same literal objective match over any labelled texts (cases of a plan, or the discovered scenarios of a story).</summary>
    public static IReadOnlyList<ObjectiveCoverage> ObjectiveCoverageOf(CoverageUserStory story, IEnumerable<(string Label, string Text)> sources)
    {
        var texts = sources.Select(s => (s.Label, Stems: Regex.Matches(s.Text.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(m => Stem(m.Value)).ToHashSet(StringComparer.Ordinal))).ToList();
        return [.. StoryInterpretation.Objectives(story.IndependentTest).Select(o =>
        {
            var words = ContentWords(o.Text).Select(Stem).Distinct(StringComparer.Ordinal).ToList();
            var hits = words.Count == 0 ? [] : texts.Where(t => words.Count(t.Stems.Contains) * 10 >= words.Count * 7)
                .Select(t => t.Label).Distinct(StringComparer.Ordinal).ToList();
            return new ObjectiveCoverage(o, hits);
        })];
    }
}
