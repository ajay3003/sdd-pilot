using System.Text.RegularExpressions;
using BirkNext.Sdd;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// The specification's own context for testing (2026-11-25): special situations (Edge Cases), clarifications, success criteria, dependencies and
/// what is out of scope — read from the existing semantic model (<see cref="SpecExplorerService.BuildSemanticModel"/>), never re-extracted.
/// <para><b>Directly related</b> to a scenario only through an explicit identifier the item itself names: one of the scenario's requirement ids
/// (FR-015 and FR-015a stay distinct; no prefix is stripped) or its user story. Nothing is related by heading or word overlap. Everything stays
/// reachable at specification level («Generelt for spesifikasjonen»). The source's own markers are kept: a superseded clarification is shown as
/// replaced (with its note), a closed or withdrawn dependency as the document records it — live status is never invented.</para>
/// </summary>
public static partial class SpecContextPresentation
{
    public enum Kind { EdgeCase, Clarification, SuccessCriterion, Dependency, OutOfScope }

    /// <summary>One item as the tester sees it; <see cref="Text"/> is the source text unchanged.</summary>
    public sealed record Item(Kind Kind, string? Id, string Title, string? Text, string? Status, string? StatusNote, IReadOnlyList<string> Names)
    {
        public bool Historical => Status is "Erstattet" or "Trukket";
    }

    public sealed record Context(IReadOnlyList<Item> Direct, IReadOnlyList<Item> All)
    {
        public int Count(Kind k, bool direct) => (direct ? Direct : All).Count(i => i.Kind == k);
    }

    public static string Heading(Kind k) => k switch
    {
        Kind.EdgeCase => "Spesielle situasjoner",
        Kind.Clarification => "Avklaringer",
        Kind.SuccessCriterion => "Hva løsningen skal oppnå",
        Kind.Dependency => "Avhengigheter",
        _ => "Dette inngår ikke",
    };

    public static readonly Kind[] Order = [Kind.EdgeCase, Kind.Clarification, Kind.SuccessCriterion, Kind.Dependency, Kind.OutOfScope];

    /// <summary>Every context item of the specification, in the document's order per kind.</summary>
    public static IReadOnlyList<Item> Items(SpecificationSemanticModel model) =>
    [
        .. model.EdgeCases.Select(e => new Item(Kind.EdgeCase, null, e.Title, e.Description, null, null, [.. e.RelatedRequirementIds])),
        .. model.Clarifications.Select(c => new Item(Kind.Clarification, c.Id, c.Question, c.Answer,
            c.Status switch { SemanticClarification.Superseded => "Erstattet", SemanticClarification.Amended => "Endret", _ => "Gjeldende" }, c.StatusNote, [.. c.AffectedElements])),
        .. model.SuccessCriteria.Select(s => new Item(Kind.SuccessCriterion, s.Id, s.Id, s.Text, null, null,
            [.. s.LinkedRequirements.Select(r => r.Id), .. s.LinkedUserStories.Select(u => u.Id)])),
        .. model.Dependencies.Select(d => new Item(Kind.Dependency, d.Id, d.Statement, d.Blocks is { Length: > 0 } blocks ? $"Blokkerer: {blocks}" : null,
            d.Withdrawn ? "Trukket" : d.Closed ? "Lukket i spesifikasjonen" : "Åpen i spesifikasjonen", d.Owner is { Length: > 0 } owner ? $"Ansvarlig: {owner}" : null, [.. d.RequirementIds])),
        .. model.OutOfScope.Select(o => new Item(Kind.OutOfScope, null, o.Text, null, null, null, [])),
    ];

    /// <summary>The items that name one of the scenario's requirements or its user story; plus everything, for the specification-level view.</summary>
    public static Context For(IReadOnlyList<Item> all, IEnumerable<string> requirementIds, string? storyId)
    {
        var requirements = requirementIds.Select(RequirementReferenceParser.Normalize).ToHashSet(StringComparer.Ordinal);
        var story = StoryNumber(storyId);
        bool Names(Item i) => i.Names.Any(n => requirements.Contains(RequirementReferenceParser.Normalize(n)) || (story is not null && StoryNumber(n) == story));
        return new Context([.. all.Where(i => i.Kind != Kind.OutOfScope && Names(i))], all);
    }

    /// <summary>"US5", "US05", "User Story 5" → "5"; anything else → null (an FR or SC id is never a story).</summary>
    public static string? StoryNumber(string? id) => id is null ? null : StoryId().Match(id.Trim()) is { Success: true } m ? int.Parse(m.Groups[1].Value).ToString() : null;

    [GeneratedRegex(@"^(?:US|User\s+Story)\s*-?\s*(\d{1,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StoryId();

    public static string Summary(Context c)
    {
        var parts = Order.Where(k => k != Kind.OutOfScope).Select(k => (k, n: c.Count(k, direct: true))).Where(x => x.n > 0)
            .Select(x => $"{x.n} {Heading(x.k).ToLowerInvariant()}");
        var text = string.Join(" · ", parts);
        return text.Length > 0 ? text : "Ingen punkter i spesifikasjonen nevner dette scenarioets krav eller brukerhistorie";
    }
}
