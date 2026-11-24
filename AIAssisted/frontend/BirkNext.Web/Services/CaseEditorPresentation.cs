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
        var texts = cases.Select(c => (c.EffectiveTitle, Text: string.Join(" ",
            new[] { c.EffectiveTitle, c.EffectivePurpose, c.EffectivePreconditions, c.EffectiveExpected }.Concat(c.EffectiveSteps).Concat(c.EffectiveStepExpected)
                .Where(x => !string.IsNullOrWhiteSpace(x))).ToLowerInvariant())).ToList();
        return [.. StoryInterpretation.Objectives(story.IndependentTest).Select(o =>
        {
            var words = ContentWords(o.Text).ToList();
            var hits = words.Count == 0 ? [] : texts.Where(t => words.Count(w => t.Text.Contains(w, StringComparison.Ordinal)) * 10 >= words.Count * 7)
                .Select(t => t.EffectiveTitle).Distinct(StringComparer.Ordinal).ToList();
            return new ObjectiveCoverage(o, hits);
        })];
    }
}
