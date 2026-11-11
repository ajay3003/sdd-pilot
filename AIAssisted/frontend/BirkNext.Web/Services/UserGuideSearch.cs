using System.Net;
using System.Text.RegularExpressions;

namespace BirkNext.Web.Services;

/// <summary>A search hit: the topic, why it matched, and a short plain-text excerpt split around the match (rendered encoded, never as HTML).</summary>
public sealed record GuideSearchHit(GuideTopic Topic, int Score, string MatchedIn, string? Before, string? Match, string? After);

/// <summary>
/// Local, in-memory Help Center search. The index is built once, lazily, from the guide's own embedded sources (the same files Documentation
/// Health checks) plus the structured lists (status terms, glossary, troubleshooting) — nothing is fetched, stored or sent anywhere. Every
/// query term, or one of its synonyms, must appear in the topic's title, group, keywords, headings or body. Titles weigh most, body text least.
/// </summary>
public static partial class UserGuideSearch
{
    public const int MaxQueryLength = 200;
    public const int MaxResults = 12;

    /// <summary>Common words testers use for what the guide calls something else. Bidirectional lookups are listed explicitly.</summary>
    private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["login"] = ["authentication", "sign-in", "sign in"], ["signin"] = ["authentication", "sign-in"], ["auth"] = ["authentication"],
        ["e2e"] = ["end-to-end", "critical e2e"], ["end-to-end"] = ["e2e"], ["companion"] = ["browser companion", "edge companion"],
        ["fnr"] = ["fodselsnummer", "identity", "identities"], ["fødselsnummer"] = ["fodselsnummer", "identity"],
        ["results"] = ["evidence", "execution"], ["evidence"] = ["results", "execution"], ["version"] = ["revision", "build", "versioning"],
        ["revision"] = ["version"], ["build"] = ["version", "ci"], ["backup"] = ["restore", "transfer"], ["export"] = ["transfer", "package"],
        ["import"] = ["transfer", "upload"], ["env"] = ["environment"], ["environment"] = ["target environment"], ["swagger"] = ["openapi", "contract"],
        ["openapi"] = ["contract"], ["schema"] = ["xsd", "contract"], ["xsd"] = ["schema"], ["status"] = ["meaning", "statuses"],
        ["error"] = ["troubleshooting", "problem"], ["problem"] = ["troubleshooting"], ["stale"] = ["historical", "outdated", "needs refresh"],
        ["historical"] = ["history", "stale"], ["locator"] = ["dom", "selector"], ["selector"] = ["locator", "dom"], ["kode"] = ["kode 6", "security classification"],
        ["altinn"] = ["connected systems"], ["parquet"] = ["dataset", "bufdata"], ["load"] = ["performance", "k6"], ["k6"] = ["performance"],
    };

    private sealed record Entry(GuideTopic Topic, string Headings, string Body);

    private static readonly Lazy<IReadOnlyList<Entry>> Index = new(BuildIndex);

    public static IReadOnlyList<GuideSearchHit> Find(string? query)
    {
        var text = (query ?? "").Trim();
        if (text.Length > MaxQueryLength) text = text[..MaxQueryLength];
        var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        if (terms.Length == 0) return [];
        var hits = new List<GuideSearchHit>();
        foreach (var entry in Index.Value)
        {
            var t = entry.Topic;
            var meta = $"{t.Group} {t.Keywords}";
            var score = 0;
            string? where = null;
            string? excerptTerm = null;
            var all = true;
            foreach (var term in terms)
            {
                var forms = Forms(term);
                int Weight(string field, int w) => forms.Any(f => field.Contains(f, StringComparison.OrdinalIgnoreCase)) ? w : 0;
                var best = Math.Max(Math.Max(Weight(t.Title, 8), Weight(meta, 4)), Math.Max(Weight(entry.Headings, 3), Weight(entry.Body, 1)));
                if (best == 0) { all = false; break; }
                score += best;
                where ??= best switch { 8 => "title", 4 => "keywords", 3 => "headings", _ => "text" };
                excerptTerm ??= forms.FirstOrDefault(f => entry.Body.Contains(f, StringComparison.OrdinalIgnoreCase));
            }
            if (!all) continue;
            // The whole query as a phrase in the article ranks above the same words scattered across a longer article.
            if (terms.Length > 1 && entry.Body.Contains(text, StringComparison.OrdinalIgnoreCase)) { score += 5; excerptTerm = text; }
            var (before, match, after) = Excerpt(entry.Body, excerptTerm);
            hits.Add(new(t, score, where ?? "text", before, match, after));
        }
        return [.. hits.OrderByDescending(h => h.Score).ThenBy(h => IndexOf(h.Topic)).Take(MaxResults)];
    }

    private static int IndexOf(GuideTopic topic) { for (var i = 0; i < UserGuideContent.Topics.Count; i++) if (UserGuideContent.Topics[i].Id == topic.Id) return i; return int.MaxValue; }

    private static string[] Forms(string term) => Synonyms.TryGetValue(term, out var alternatives) ? [term, .. alternatives] : [term];

    /// <summary>About 70 characters on each side of the first match, cut at word boundaries. Plain text only.</summary>
    internal static (string? Before, string? Match, string? After) Excerpt(string body, string? term)
    {
        if (string.IsNullOrEmpty(term)) return (null, null, null);
        var at = body.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return (null, null, null);
        var start = Math.Max(0, at - 70);
        if (start > 0 && body.IndexOf(' ', start) is var sp and >= 0 && sp < at) start = sp + 1;
        var end = Math.Min(body.Length, at + term.Length + 70);
        if (end < body.Length && body.LastIndexOf(' ', end) is var ep && ep > at + term.Length) end = ep;
        return ((start > 0 ? "…" : "") + body[start..at], body.Substring(at, term.Length), body[(at + term.Length)..end] + (end < body.Length ? "…" : ""));
    }

    private static IReadOnlyList<Entry> BuildIndex()
    {
        var sources = DocumentationCatalog.ReadGuideSource(typeof(UserGuideSearch).Assembly) ?? [];
        var sections = new Dictionary<string, (string Headings, string Body)>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
            foreach (Match m in SectionPattern().Matches(source))
            {
                var inner = m.Groups["body"].Value;
                var headings = string.Join(" ", HeadingPattern().Matches(inner).Select(h => Clean(h.Groups["text"].Value)));
                sections[m.Groups["id"].Value] = (headings, Clean(inner));
            }
        string Structured(string topicId) => topicId switch
        {
            "status-reference" => string.Join(" ", UserGuideContent.StatusTerms.Concat(UserGuideContent.ExecutionOutcomes).Concat(UserGuideContent.ResultCurrency)
                .Select(s => $"{s.Term}: {s.Meaning} Not the same as {s.NotTheSameAs}")),
            "glossary" => string.Join(" ", UserGuideContent.Glossary.Select(g => $"{g.Term}: {g.Definition}")),
            "troubleshooting" => string.Join(" ", UserGuideContent.Troubleshooting.Select(e => $"{e.Problem}. {e.Cause} {e.Action}")),
            "feature-matrix" => string.Join(" ", DocumentationCatalog.Features.Select(f => f.DisplayName)),
            _ => "",
        };
        return [.. UserGuideContent.Topics.Select(t =>
        {
            var (headings, body) = sections.GetValueOrDefault(t.Id);
            return new Entry(t, headings ?? "", $"{body} {Structured(t.Id)}".Trim());
        })];
    }

    /// <summary>Razor/HTML to readable text: drop code lines and expressions, tags and entities; collapse whitespace.</summary>
    internal static string Clean(string razor)
    {
        var text = RazorStatement().Replace(razor, " ");
        text = RazorExpression().Replace(text, " ");
        text = Tag().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        return Whitespace().Replace(text, " ").Trim();
    }

    [GeneratedRegex("""<section id="(?<id>[a-z0-9-]+)"[^>]*>(?<body>.*?)</section>""", RegexOptions.Singleline)]
    private static partial Regex SectionPattern();
    [GeneratedRegex("""<h[23][^>]*>(?<text>.*?)</h[23]>""", RegexOptions.Singleline)]
    private static partial Regex HeadingPattern();
    [GeneratedRegex("""^\s*@(foreach|if|else|for|code|\{|\}).*$|^\s*[{}]\s*$""", RegexOptions.Multiline)]
    private static partial Regex RazorStatement();
    [GeneratedRegex("""@\((?:[^()]|\([^()]*\))*\)|@[A-Za-z_][\w.]*(\([^()]*\))?""")]
    private static partial Regex RazorExpression();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
