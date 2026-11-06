using BirkNext.IntegrationTopology;

namespace BirkNext.Web.Services;

/// <summary>
/// The Topology Map's graph: built once per (view, coverage, level) from the existing topology DTOs — never a second resolver. Two levels keep it
/// readable: the expected flow a Test Pack declares (its relationships, resolved or not) and the system connections (components with their
/// connections grouped per pair; interfaces stay in the Technical Inventory). Edge state is drawn by line style and stated in text, never colour
/// alone: Resolved/Observed solid, Candidate/Strongly supported dashed ("Inferred"), Expected but unresolved dotted ("Unresolved"), Conflict
/// dash-dot. Inferred topology is never presented as resolved, and nothing is "confirmed" (topology has no confirmed state).
/// </summary>
public static class TopologyMapPresentation
{
    public const string ExpectedLevel = "expected", SystemsLevel = "systems";

    public sealed record MapNode(string Id, string Name, string Subtitle, string Kind, string State);
    /// <param name="Draw">The renderer's line style: "" (solid), "Inferred", "Unresolved" or "Conflict".</param>
    public sealed record MapEdge(string Id, string From, string To, string Label, string Draw, string StateLabel, bool RuntimeObserved, string Filter);
    public sealed record MapGraph(IReadOnlyList<MapNode> Nodes, IReadOnlyList<MapEdge> Edges, int OmittedNodes);

    /// <summary>Map filter values: every edge belongs to exactly one.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Filters =
        [("", "All relationships"), ("resolved", "Resolved or observed"), ("inferred", "Candidate / strongly supported"), ("unresolved", "Expected but unresolved"), ("conflict", "Conflict")];

    public const int MaxSystemNodes = 40;

    public static MapGraph Build(IntegrationTopologyView view, TopologyCoverageView? coverage, string level) =>
        level == SystemsLevel || coverage is null || coverage.Relationships.Count == 0 ? Systems(view) : Expected(coverage);

    private static MapGraph Expected(TopologyCoverageView coverage)
    {
        var names = coverage.Relationships.SelectMany(r => new[] { FromOf(r), ToOf(r) }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string Id(string name) => "n:" + name.ToLowerInvariant();
        var nodes = names.Select(name => new MapNode(Id(name), name, "Expected component", "service", "Expected")).ToList();
        var edges = coverage.Relationships.Select(r =>
        {
            var (draw, filter) = Style(r.State);
            return new MapEdge("r:" + r.Id, Id(FromOf(r)), Id(ToOf(r)), $"{r.Transport}{(r.RuntimeObserved ? " · observed" : "")}", draw, TopologyPresentation.Relationship(r.State), r.RuntimeObserved, filter);
        }).ToList();
        return new(nodes, edges, 0);
    }

    /// <summary>The relationship's ends; a result without them falls back to its label "A → B" (display names, as the profile states them).</summary>
    public static string FromOf(TopologyRelationshipResult r) => r.From is { Length: > 0 } from ? from : r.Label.Split(" → ")[0].Trim();
    public static string ToOf(TopologyRelationshipResult r) => r.To is { Length: > 0 } to ? to : r.Label.Split(" → ") is { Length: > 1 } parts ? parts[^1].Trim() : "?";

    private static MapGraph Systems(IntegrationTopologyView view)
    {
        var pairs = view.Connections.GroupBy(c => (c.ProducerComponentId, c.ConsumerComponentId)).ToList();
        var degree = pairs.SelectMany(p => new[] { p.Key.ProducerComponentId, p.Key.ConsumerComponentId }).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        // Bounded: the most connected components first; an unconnected component adds nothing to a map (it stays in the inventory).
        var shown = view.Components.Where(c => degree.ContainsKey(c.Id)).OrderByDescending(c => degree[c.Id]).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSystemNodes).ToList();
        var ids = shown.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var nodes = shown.Select(c => new MapNode(c.Id, c.Name, c.ComponentType ?? TopologyPresentation.Kind(c.Kind),
            c.Kind switch { TopologyComponentKind.External => "external", TopologyComponentKind.Resource => "channel", _ => "service" }, TopologyPresentation.Status(c.Status))).ToList();
        var edges = pairs.Where(p => ids.Contains(p.Key.ProducerComponentId) && ids.Contains(p.Key.ConsumerComponentId)).Select(p =>
        {
            var conflict = p.Any(c => c.Status == TopologyStatus.Conflict);
            // A pair is as strong as its weakest stated connection would mislead; it is drawn by its best non-conflicting relationship and says so.
            var best = p.Where(c => c.Status != TopologyStatus.Conflict).Select(c => c.Relationship).DefaultIfEmpty(TopologyRelationshipState.Candidate).Max();
            var (draw, filter) = conflict ? ("Conflict", "conflict") : Style(best);
            var types = string.Join(", ", p.Select(c => TopologyPresentation.Type(c.Type)).Distinct());
            var observed = p.Any(c => c.Relationship == TopologyRelationshipState.Observed);
            return new MapEdge($"c:{p.Key.ProducerComponentId}>{p.Key.ConsumerComponentId}", p.Key.ProducerComponentId, p.Key.ConsumerComponentId,
                $"{p.Count()} × {types}", draw, conflict ? "Conflict" : TopologyPresentation.Relationship(best), observed, filter);
        }).ToList();
        return new(nodes, edges, degree.Count - shown.Count);
    }

    public static (string Draw, string Filter) Style(TopologyRelationshipState state) => state switch
    {
        TopologyRelationshipState.Resolved or TopologyRelationshipState.Observed => ("", "resolved"),
        TopologyRelationshipState.ExpectedUnresolved => ("Unresolved", "unresolved"),
        _ => ("Inferred", "inferred"),
    };

    public static string LineStyle(string draw) => draw switch
    {
        "Inferred" => "dashed line",
        "Unresolved" => "dotted line",
        "Conflict" => "dash-dot line",
        _ => "solid line",
    };
}
