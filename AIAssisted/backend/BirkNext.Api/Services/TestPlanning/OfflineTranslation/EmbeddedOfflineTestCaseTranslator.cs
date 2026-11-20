using System.Text.RegularExpressions;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning.OfflineTranslation;

/// <summary>
/// Translation by the offline translator packaged with BirkNext (English → Norwegian bokmål, CPU, nothing leaves this PC).
/// <list type="bullet">
/// <item>Protected identifiers (⟦n⟧ from <see cref="ScenarioPlanning.Protect"/>) and glossary terms become &lt;xN&gt; markers, which the model
/// keeps in place (⟦n⟧ itself it splits); each marker must come back exactly once, else the text is translated again piece by piece between the
/// markers, so an identifier or a term is never lost or altered.</item>
/// <item>A glossary term is always written as configured (System Settings → Translation → Advanced). Norwegian terms in English text are kept.</item>
/// <item>A text that is already Norwegian is returned unchanged (the English → Norwegian model would garble it).</item>
/// </list>
/// The result is a draft for a tester to review; it never replaces the original and approves nothing.
/// </summary>
public sealed partial class EmbeddedOfflineTestCaseTranslator(OfflineTranslationEngine engine, IReadOnlyList<TranslationGlossaryEntry> glossary) : ITestCaseTranslator
{
    public const string ProviderName = "BirkNext offline translator";
    public const string PairOnly = "The offline translator translates English → Norwegian (bokmål) only. Choose English as \"From\" and Norwegian (bokmål) as \"To\" in System Settings → Translation, or write the translation by hand.";

    public TranslationAvailability Availability { get; } = Describe(engine);

    private static TranslationAvailability Describe(OfflineTranslationEngine engine)
    {
        var (bundle, problem) = engine.Bundle();
        return bundle is null
            ? new(false, null, problem + " You can write the translated text by hand.")
            : new(true, $"{ProviderName} ({bundle.Manifest.Model.Id})", "Offline translation on this PC by the model packaged with BirkNext; nothing leaves this PC. Review the translation before you use it.");
    }

    [GeneratedRegex("⟦(\\d+)⟧")] private static partial Regex Protected();
    [GeneratedRegex(@"<\s*x\s*(\d+)\s*>", RegexOptions.IgnoreCase)] private static partial Regex Marker();
    [GeneratedRegex(@"[\p{L}]+")] private static partial Regex Word();

    public async Task<TranslatedTexts> TranslateAsync(IReadOnlyList<string?> texts, string source, string target, CancellationToken ct)
    {
        if (!Availability.Available) throw new TestCaseTranslationException(Availability.Reason);
        if (source is not (TranslationLanguages.English or TranslationLanguages.Auto) || !string.Equals(target, TranslationLanguages.NorwegianBokmal, StringComparison.OrdinalIgnoreCase))
            throw new TestCaseTranslationException(PairOnly);
        var result = texts.Select(_ => (string?)null).ToList();
        var work = new List<(int Index, Masked Masked)>();
        for (var i = 0; i < texts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(texts[i])) continue;
            var masked = Mask(texts[i]!, glossary);
            if (LooksNorwegian(masked.Text)) { result[i] = texts[i]; continue; }
            if (!masked.Text.Any(char.IsLetter)) { result[i] = texts[i]; continue; }
            work.Add((i, masked));
        }
        if (work.Count > 0)
        {
            var translated = await engine.TranslateAsync([.. work.Select(w => w.Masked.Text)], ct);
            var retry = new List<(int Index, Masked Masked)>();
            for (var k = 0; k < work.Count; k++)
            {
                if (Unmask(translated[k], work[k].Masked) is { } ok) result[work[k].Index] = ok;
                else retry.Add(work[k]);
            }
            foreach (var (index, masked) in retry) result[index] = await PieceByPieceAsync(masked, ct);
        }
        return new(result, Availability.Provider!);
    }

    /// <summary>A text with markers: <see cref="Values"/>[n] is what &lt;xn&gt; stands for (the original ⟦k⟧ token or the glossary target).</summary>
    public sealed record Masked(string Text, IReadOnlyList<string> Values, IReadOnlyList<bool> CapitalizeAtStart);

    /// <summary>Protected identifiers and glossary terms → &lt;xN&gt; (longest glossary term first, whole words, any case).</summary>
    public static Masked Mask(string text, IReadOnlyList<TranslationGlossaryEntry> glossary)
    {
        var values = new List<string>();
        var capitalize = new List<bool>();
        var masked = Protected().Replace(text, m => { values.Add(m.Value); capitalize.Add(false); return $"<x{values.Count - 1}>"; });
        foreach (var entry in glossary.Where(e => !string.IsNullOrWhiteSpace(e.Source) && !string.IsNullOrWhiteSpace(e.Target)).OrderByDescending(e => e.Source.Trim().Length))
        {
            var pattern = $@"(?<![\p{{L}}\p{{N}}<]){Regex.Escape(entry.Source.Trim())}(?![\p{{L}}\p{{N}}>])";
            masked = Regex.Replace(masked, pattern, m =>
            {
                values.Add(entry.Target.Trim());
                capitalize.Add(char.IsUpper(m.Value[0]));
                return $"<x{values.Count - 1}>";
            }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        return new(masked, values, capitalize);
    }

    /// <summary>The translation with every marker replaced, or null when a marker is missing, repeated or unknown (then translated piece by piece).</summary>
    public static string? Unmask(string translated, Masked masked)
    {
        var seen = new int[masked.Values.Count];
        foreach (Match m in Marker().Matches(translated))
        {
            if (!int.TryParse(m.Groups[1].Value, out var n) || n >= seen.Length) return null;
            seen[n]++;
        }
        if (seen.Any(c => c != 1)) return null;
        var text = Marker().Replace(translated, m =>
        {
            var n = int.Parse(m.Groups[1].Value);
            var value = masked.Values[n];
            var atStart = m.Index == 0 || translated[..m.Index].TrimEnd().EndsWith('.') || translated[..m.Index].TrimEnd().EndsWith(':');
            return (masked.CapitalizeAtStart[n] || atStart) && value.Length > 0 && char.IsLower(value[0]) && !value.StartsWith('⟦') ? char.ToUpper(value[0]) + value[1..] : value;
        });
        return text;
    }

    /// <summary>Fallback: the plain-language pieces between markers are translated on their own and the markers' values put back unchanged.</summary>
    private async Task<string> PieceByPieceAsync(Masked masked, CancellationToken ct)
    {
        var parts = Marker().Split(masked.Text);
        // Split with a capture group: even indexes are text, odd indexes are marker numbers.
        var pieces = parts.Where((p, i) => i % 2 == 0 && p.Any(char.IsLetter)).Select(p => p.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var translated = pieces.Count == 0 ? [] : await engine.TranslateAsync(pieces, ct);
        var map = pieces.Zip(translated).ToDictionary(x => x.First, x => x.Second.Trim(), StringComparer.Ordinal);
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 1) { builder.Append(masked.Values[int.Parse(parts[i])]); continue; }
            var p = parts[i];
            var core = p.Trim();
            if (core.Length == 0 || !map.TryGetValue(core, out var t)) { builder.Append(p); continue; }
            var start = p.IndexOf(core, StringComparison.Ordinal);
            builder.Append(p[..start]).Append(t).Append(p[(start + core.Length)..]);
        }
        return builder.ToString();
    }

    private static readonly HashSet<string> NorwegianWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "og", "ikke", "er", "som", "til", "av", "med", "på", "det", "en", "et", "ei", "at", "skal", "kan", "har", "blir", "når", "fra", "den", "de", "eller", "om", "ved",
        "gir", "samme", "ingen", "ny", "nytt", "sjekk", "send", "barnet", "skolen", "meldingen", "rapporten", "perioden", "hvis", "uten", "etter", "før", "også", "må",
    };

    private static readonly HashSet<string> EnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "is", "are", "of", "to", "with", "that", "this", "not", "be", "when", "from", "should", "must", "can", "has", "have", "will", "an", "in", "on",
        "it", "its", "as", "by", "for", "or", "if", "then", "each", "no", "same", "send", "check", "open", "select", "child", "report", "message",
    };

    /// <summary>Whether a (masked) text is already Norwegian: more Norwegian than English function words, or Norwegian letters and no English word.</summary>
    public static bool LooksNorwegian(string text)
    {
        var words = Word().Matches(Marker().Replace(text, " ")).Select(m => m.Value).ToList();
        if (words.Count == 0) return false;
        var nb = words.Count(w => NorwegianWords.Contains(w) && !EnglishWords.Contains(w));
        var en = words.Count(w => EnglishWords.Contains(w) && !NorwegianWords.Contains(w));
        var letters = words.Any(w => w.IndexOfAny(['æ', 'ø', 'å', 'Æ', 'Ø', 'Å']) >= 0);
        return en == 0 ? nb > 0 || letters : nb >= 2 && nb > en * 2;
    }
}
