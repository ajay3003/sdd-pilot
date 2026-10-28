using System.Text.RegularExpressions;

namespace BirkNext.Web.Services;

public enum GeneratedDiagramKind { Sequence, Graph }

/// <summary>One message of a sequence diagram ("A → B: message"); <see cref="Branch"/> names the alt/else/opt/loop block it sits in, if any.</summary>
public sealed record DiagramStep(int Number, string From, string To, string Message, string? Branch);

/// <summary>One edge of a flow/graph diagram ("A → B", optional label).</summary>
public sealed record DiagramEdge(string From, string To, string? Label);

/// <summary>
/// A diagram read deterministically from generated documentation: participants and ordered steps (sequence) or nodes, groups and edges
/// (graph/flowchart). Labels are plain text; nothing is rendered as HTML or executed. <see cref="Branches"/> lists alt/else/opt labels — the
/// failure and alternative paths the document itself names.
/// </summary>
public sealed record GeneratedDiagram(GeneratedDiagramKind Kind, string Source, IReadOnlyList<string> Participants, IReadOnlyList<DiagramStep> Steps,
    IReadOnlyList<string> Branches, IReadOnlyList<(string Id, string Label)> Nodes, IReadOnlyList<DiagramEdge> Edges, IReadOnlyList<(string Name, IReadOnlyList<string> Members)> Groups);

/// <summary>Mermaid sequence and graph/flowchart reading for the Generated Documentation Explorer (local, deterministic, no remote rendering).</summary>
public static partial class GeneratedDiagrams
{
    /// <summary>True when a fenced block is a Mermaid diagram this reader recognises (by its first keyword).</summary>
    public static bool IsDiagram(string block) => FirstKeyword(block) is "sequenceDiagram" or "graph" or "flowchart" or "erDiagram" or "classDiagram" or "stateDiagram" or "stateDiagram-v2";

    /// <summary>The diagram, or null when the type is not supported here (the caller shows "Diagram preview unavailable" and the source).</summary>
    public static GeneratedDiagram? Read(string block)
    {
        try
        {
            return FirstKeyword(block) switch
            {
                "sequenceDiagram" => Sequence(block),
                "graph" or "flowchart" => Graph(block),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException) { return null; }
    }

    private static string FirstKeyword(string block) =>
        block.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("%%", StringComparison.Ordinal))?.Split(' ', 2)[0] ?? "";

    private static GeneratedDiagram? Sequence(string block)
    {
        var participants = new List<string>();
        var steps = new List<DiagramStep>();
        var branches = new List<string>();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        void Add(string name) { if (!participants.Contains(name)) participants.Add(name); }
        string Name(string id) => aliases.TryGetValue(id, out var label) ? label : id;
        foreach (var raw in block.Split('\n').Skip(1))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal)) continue;
            if (Participant().Match(line) is { Success: true } p)
            {
                var id = p.Groups["id"].Value;
                if (p.Groups["alias"].Success) aliases[id] = Clean(p.Groups["alias"].Value);
                Add(Name(id));
            }
            else if (Block().Match(line) is { Success: true } b)
            {
                var label = $"{b.Groups["kind"].Value} {Clean(b.Groups["label"].Value)}".Trim();
                if (b.Groups["kind"].Value is "alt" or "opt" or "loop" or "par" or "critical" or "break") stack.Push(label);
                else if (stack.Count > 0) { stack.Pop(); stack.Push(label); }
                branches.Add(label);
            }
            else if (line.Equals("end", StringComparison.Ordinal)) { if (stack.Count > 0) stack.Pop(); }
            else if (Message().Match(line) is { Success: true } m)
            {
                var from = Name(m.Groups["from"].Value.Trim());
                var to = Name(m.Groups["to"].Value.Trim());
                Add(from); Add(to);
                steps.Add(new(steps.Count + 1, from, to, Clean(m.Groups["msg"].Value), stack.Count > 0 ? stack.Peek() : null));
            }
        }
        return steps.Count == 0 && participants.Count == 0 ? null : new(GeneratedDiagramKind.Sequence, block, participants, steps, [.. branches.Distinct()], [], [], []);
    }

    private static GeneratedDiagram? Graph(string block)
    {
        var nodes = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        var edges = new List<DiagramEdge>();
        var groups = new List<(string Name, List<string> Members)>();
        (string Name, List<string> Members)? current = null;
        void Node(string id, string? label)
        {
            if (string.IsNullOrWhiteSpace(label)) label = null;
            if (!nodes.ContainsKey(id)) order.Add(id);
            if (label is not null || !nodes.ContainsKey(id)) nodes[id] = Clean(label ?? id);
            if (current is { } g && !g.Members.Contains(id)) g.Members.Add(id);
        }
        foreach (var raw in block.Split('\n').Skip(1))
        {
            var line = raw.Trim().TrimEnd(';');
            if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal) || line.StartsWith("classDef", StringComparison.Ordinal) || line.StartsWith("class ", StringComparison.Ordinal)
                || line.StartsWith("style ", StringComparison.Ordinal) || line.StartsWith("linkStyle", StringComparison.Ordinal) || line.StartsWith("direction", StringComparison.Ordinal)) continue;
            if (line.StartsWith("subgraph", StringComparison.Ordinal))
            {
                var title = Subgraph().Match(line) is { Success: true } s ? Clean(s.Groups["label"].Success ? s.Groups["label"].Value : s.Groups["id"].Value) : "Group";
                groups.Add((title, [])); current = groups[^1]; continue;
            }
            if (line == "end") { current = null; continue; }
            // Parts are the gaps between edge separators (Regex.Split would also return the captured labels as parts).
            var separators = EdgeSplit().Matches(line);
            var parts = new List<string>();
            var at = 0;
            foreach (Match separator in separators) { parts.Add(line[at..separator.Index]); at = separator.Index + separator.Length; }
            parts.Add(line[at..]);
            if (separators.Count > 0)
            {
                var labels = separators.Select(m => m.Groups["label"].Success ? Clean(m.Groups["label"].Value) : null).ToList();
                string? previous = null;
                for (var i = 0; i < parts.Count; i++)
                {
                    if (NodeRef().Match(parts[i].Trim()) is not { Success: true } n) { previous = null; continue; }
                    var id = n.Groups["id"].Value;
                    Node(id, n.Groups["label"].Success ? n.Groups["label"].Value : null);
                    if (previous is not null) edges.Add(new(nodes[previous], nodes[id], i - 1 < labels.Count ? labels[i - 1] : null));
                    previous = id;
                }
            }
            else if (NodeRef().Match(line) is { Success: true } single && single.Value.Length == line.Length)
                Node(single.Groups["id"].Value, single.Groups["label"].Success ? single.Groups["label"].Value : null);
        }
        if (order.Count == 0) return null;
        return new(GeneratedDiagramKind.Graph, block, [], [], [], [.. order.Select(id => (id, nodes[id]))], edges,
            [.. groups.Select(g => (g.Name, (IReadOnlyList<string>)[.. g.Members.Select(m => nodes.GetValueOrDefault(m, m))]))]);
    }

    /// <summary>Plain text for a label: quotes, HTML line breaks and markup characters removed (never interpreted).</summary>
    private static string Clean(string value) => HtmlBreak().Replace(value.Trim().Trim('"'), " ").Replace("<", "‹").Replace(">", "›").Trim();

    [GeneratedRegex(@"^(participant|actor)\s+(?<id>[^\s]+)(\s+as\s+(?<alias>.+))?$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex Participant();
    [GeneratedRegex(@"^(?<kind>alt|else|opt|loop|par|and|critical|option|break)\b\s*(?<label>.*)$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex Block();
    [GeneratedRegex(@"^(?<from>[^-<>:]+?)\s*(-{1,2}>>?|-{1,2}[x)]|-{1,2}>)\s*[+-]?(?<to>[^:]+?)\s*:\s*(?<msg>.*)$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex Message();
    [GeneratedRegex(@"\s*(?:-{2,3}>|={2,3}>|-\.->|-{2,3}|<-{2,3}>)(?:\|(?<label>[^|]*)\|)?\s*", RegexOptions.CultureInvariant, 200)]
    private static partial Regex EdgeSplit();
    [GeneratedRegex(@"^(?<id>[A-Za-z0-9_.\-]+)\s*(?:\[\(?|\(\[?|\{\{?|\[\[|>)?\s*""?(?<label>[^""\]\)\}]*)?""?\s*(?:\)?\]|\]?\)|\}\}?|\]\])?$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex NodeRef();
    [GeneratedRegex(@"^subgraph\s+(?<id>[^\[\s]+)?\s*(\[""?(?<label>[^""\]]+)""?\])?", RegexOptions.CultureInvariant, 200)]
    private static partial Regex Subgraph();
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex HtmlBreak();
}
