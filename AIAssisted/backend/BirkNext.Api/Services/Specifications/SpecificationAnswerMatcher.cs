using System.Text.RegularExpressions;
using BirkNext.IntegrationSuites;
using BirkNext.Specifications;

namespace BirkNext.Api.Services.Specifications;

/// <summary>
/// Finds the requirements of a project's specification catalog that appear to answer a review question. A question is described by term groups:
/// a requirement is a candidate only when its wording contains at least one term of EVERY group (e.g. a classification term AND an intake term
/// AND an outcome term), so a requirement that merely mentions Kode 6/7 is not proposed. Candidates are ranked by how many distinct terms they
/// contain. A requirement whose wording names the matter as open (an <c>OQ-nn</c> reference, "is open", "decided later", "er åpen") is a match
/// of kind <see cref="SuiteSpecificationAnswer.OpenInSpecification"/>: it documents the gap, it does not close it.
/// <para>Proposals only: nothing here records a decision. A tester confirms or rejects each link.</para>
/// </summary>
public static class SpecificationAnswerMatcher
{
    public const int MaxProposals = 2;
    private const int ExcerptLength = 280;

    private static readonly Regex OpenQuestion = new(@"\bOQ-\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] OpenPhrases =
        ["is open", "remains open", "are open", "decided later", "to be decided", "not decided", "tbd", "er åpen", "er åpent", "avklares senere", "ikke avklart"];

    public static List<SuiteSpecificationLink> Match(IEnumerable<SpecificationRequirementEntry> requirements, IReadOnlyList<string[]> termGroups,
        IReadOnlyCollection<string>? rejected = null, int max = MaxProposals)
    {
        if (termGroups.Count == 0) return [];
        var candidates = new List<(SpecificationRequirementEntry Entry, int Score, List<string> Terms)>();
        foreach (var r in requirements)
        {
            if (rejected?.Contains(SpecificationCatalogRules.NormalizeId(r.RequirementId)) == true) continue;
            var text = r.Title.ToLowerInvariant();
            var matched = new List<string>();
            var everyGroup = true;
            foreach (var group in termGroups)
            {
                var hits = group.Where(t => text.Contains(t, StringComparison.Ordinal)).ToList();
                if (hits.Count == 0) { everyGroup = false; break; }
                matched.AddRange(hits);
            }
            if (everyGroup) candidates.Add((r, matched.Distinct(StringComparer.Ordinal).Count(), matched.Distinct(StringComparer.Ordinal).ToList()));
        }
        return [.. candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Entry.RequirementId, StringComparer.OrdinalIgnoreCase).Take(max).Select(c =>
        {
            var open = OpenMarker(c.Entry.Title);
            return new SuiteSpecificationLink(c.Entry.RequirementId, c.Entry.Path, Excerpt(c.Entry.Title), c.Entry.TextSha256,
                open is null ? SuiteSpecificationAnswer.Answers : SuiteSpecificationAnswer.OpenInSpecification,
                $"Proposed by BirkNext: the wording contains {string.Join(", ", c.Terms.Take(5).Select(t => $"\"{t}\""))}. Not a decision until a tester confirms it.",
                open);
        })];
    }

    /// <summary>The open-question marker in a requirement's wording ("OQ-26", or a phrase such as "is open"), or null.</summary>
    public static string? OpenMarker(string text)
    {
        if (OpenQuestion.Match(text) is { Success: true } m) return m.Value.ToUpperInvariant();
        var lower = text.ToLowerInvariant();
        return OpenPhrases.FirstOrDefault(p => Regex.IsMatch(lower, $@"(^|\W){Regex.Escape(p)}(\W|$)")) is { } phrase ? $"\"{phrase}\"" : null;
    }

    private static string Excerpt(string text)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= ExcerptLength ? flat : flat[..ExcerptLength].TrimEnd() + "…";
    }
}
