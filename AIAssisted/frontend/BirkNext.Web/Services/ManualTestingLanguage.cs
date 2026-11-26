using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using Microsoft.JSInterop;
using S = BirkNext.Web.Services.ScenarioBrowserPresentation;

namespace BirkNext.Web.Services;

/// <summary>
/// One language choice for Manual Testing (2026-11-26): Norsk / Original / Sammenlign, kept per browser (so per tester) and shared by Finn og forstå
/// and Planlegg og klargjør; it no longer resets when a case, tab or page changes. Norsk shows a STORED translation only when one exists and still
/// matches its source; otherwise the original is shown and labelled as such. Nothing is ever translated, saved or revised by choosing a language.
/// </summary>
public static class ManualTestingLanguage
{
    public const string Key = "birknext.manualTesting.language";
    public const EditorLanguage Default = EditorLanguage.Norwegian;

    public static async Task<EditorLanguage?> ReadAsync(IJSRuntime js)
    {
        try { return Enum.TryParse<EditorLanguage>(await js.InvokeAsync<string?>("localStorage.getItem", Key), out var l) && Enum.IsDefined(l) ? l : null; }
        catch (Exception e) when (e is JSException or InvalidOperationException or TaskCanceledException) { return null; }
    }

    public static async Task WriteAsync(IJSRuntime js, EditorLanguage language)
    {
        try { await js.InvokeVoidAsync("localStorage.setItem", Key, language.ToString()); }
        catch (Exception e) when (e is JSException or InvalidOperationException or TaskCanceledException) { }
    }

    /// <summary>What a text shows: the translated text (with its origin) or the original, and why.</summary>
    public sealed record Shown(string Text, bool Translated, string? Note, string? Original);

    public static string OriginLabel(TranslationOrigin o) => o switch
    {
        TranslationOrigin.Machine => "Maskinoversatt – ikke gjennomgått",
        TranslationOrigin.MachineEdited => "Maskinoversatt, redigert",
        _ => "Oversatt av en person",
    };

    /// <summary>A scenario's title in the chosen language: the stored current translation, else the original with «Norsk oversettelse mangler»
    /// (or «utdatert» when the stored one no longer matches its source — it is never shown as current).</summary>
    public static Shown ScenarioTitle(S.Item i, string original, EditorLanguage language)
    {
        if (language == EditorLanguage.Original) return new(original, false, null, null);
        var t = i.Work?.Translation;
        if (t is { Current: true, Title: { Length: > 0 } title })
        {
            var clean = StripStoryPrefix(i, title);
            return new(clean, true, OriginLabel(t.Origin), language == EditorLanguage.Compare ? original : null);
        }
        return new(original, false, t is { Current: false } ? "Norsk oversettelse er utdatert – viser originalen" : "Norsk oversettelse mangler – viser originalen", null);
    }

    /// <summary>The expected result in the chosen language (the scenario's «Then»/expected result from the stored translation).</summary>
    public static Shown Expected(S.Item i, string original, EditorLanguage language)
    {
        if (language == EditorLanguage.Original) return new(original, false, null, null);
        var t = i.Work?.Translation;
        var text = t is { Current: true } ? t.Then ?? t.ExpectedResult : null;
        return text is { Length: > 0 }
            ? new(text, true, OriginLabel(t!.Origin), language == EditorLanguage.Compare ? original : null)
            : new(original, false, t is { Current: false } ? "Norsk oversettelse er utdatert – viser originalen" : "Norsk oversettelse mangler – viser originalen", null);
    }

    /// <summary>A user story's title in the chosen language, from the plans' stored story translations (current only).</summary>
    public static Shown StoryTitle(CoverageUserStory us, IEnumerable<StoryTranslation> stored, EditorLanguage language)
    {
        var original = ScenarioWorkspacePresentation.CleanStoryTitle(us.Title);
        if (language == EditorLanguage.Original) return new(original, false, null, null);
        var nb = stored.FirstOrDefault(t => t.StoryKey == us.Key);
        if (nb is { Title: { Length: > 0 } title } && nb.SourceFingerprint == ScenarioPlanning.StoryFingerprint(us))
            return new(ScenarioWorkspacePresentation.CleanStoryTitle(title), true, OriginLabel(nb.Origin), language == EditorLanguage.Compare ? original : null);
        return new(original, false, nb is null ? "Norsk oversettelse mangler" : "Norsk oversettelse er utdatert", null);
    }

    private static string StripStoryPrefix(S.Item i, string title) =>
        i.Candidate.Story?.StoryId is { Length: > 0 } id && title.StartsWith(id + " — ", StringComparison.Ordinal) ? title[(id.Length + 3)..] : title;
}
