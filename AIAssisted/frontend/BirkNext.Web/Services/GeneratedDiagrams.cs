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
    IReadOnlyList<string> Branches, IReadOnlyList<(string Id, string Label)> Nodes, IReadOnlyList<DiagramEdge> Edges, IReadOnlyList<(string Name, IReadOnlyList<string> Members)> Groups)
{
    /// <summary>The Mermaid diagram type the source declares ("sequenceDiagram", "flowchart", "stateDiagram-v2", "classDiagram", "erDiagram"). Empty: derived from <see cref="Kind"/>.</summary>
    public string SourceType { get; init; } = "";
}

/// <summary>Mermaid sequence and graph/flowchart reading for the Generated Documentation Explorer (local, deterministic, no remote rendering).</summary>
public static partial class GeneratedDiagrams
{
    /// <summary>True when a fenced block is a Mermaid diagram this reader recognises (by its first keyword).</summary>
    public static bool IsDiagram(string block) => FirstKeyword(block) is var keyword && (Drawn.Contains(keyword) || NotDrawn.Contains(keyword));

    /// <summary>Mermaid types read here (state/class/ER as nodes and labelled relationships only).</summary>
    private static readonly HashSet<string> Drawn = new(StringComparer.Ordinal)
        { "sequenceDiagram", "graph", "flowchart", "stateDiagram", "stateDiagram-v2", "classDiagram", "classDiagram-v2", "erDiagram" };

    /// <summary>Mermaid types recognised as diagrams but not drawn: their source is shown with the reason (never a guessed drawing).</summary>
    private static readonly HashSet<string> NotDrawn = new(StringComparer.Ordinal)
    {
        "gantt", "pie", "journey", "mindmap", "gitGraph", "timeline", "quadrantChart", "requirementDiagram", "C4Context", "C4Container", "C4Component",
        "C4Dynamic", "C4Deployment", "sankey-beta", "xychart-beta", "block-beta", "packet-beta", "architecture-beta", "kanban", "zenuml", "radar-beta", "treemap-beta",
    };

    /// <summary>The caption type of a read diagram: what was drawn, and for class/ER diagrams that only relationships are.</summary>
    public static string TypeLabel(GeneratedDiagram diagram) => diagram.SourceType switch
    {
        "stateDiagram" or "stateDiagram-v2" => "State diagram",
        "classDiagram" or "classDiagram-v2" => "Class diagram (relationships only)",
        "erDiagram" => "ER diagram (relationships only)",
        _ => diagram.Kind == GeneratedDiagramKind.Sequence ? "Sequence diagram" : "Flow / component diagram",
    };

    /// <summary>What the nodes and edges of a graph-kind diagram are called in its summary ("3 states, 4 transitions").</summary>
    public static (string Nodes, string Edges) Nouns(GeneratedDiagram diagram) => diagram.SourceType switch
    {
        "stateDiagram" or "stateDiagram-v2" => ("states", "transitions"),
        "classDiagram" or "classDiagram-v2" => ("classes", "relationships"),
        "erDiagram" => ("entities", "relationships"),
        _ => ("components", "connections"),
    };

    /// <summary>Why a recognised diagram block has no drawing (plain text; the type is named, never interpreted).</summary>
    public static string UnavailableReason(string block)
    {
        var keyword = FirstKeyword(block);
        if (keyword.Length > 40) keyword = keyword[..40];
        return Drawn.Contains(keyword)
            ? $"Nothing drawable could be read from this '{keyword}' diagram; its source is shown."
            : $"Diagram type '{(keyword.Length == 0 ? "unknown" : keyword)}' is not drawn by BirkNext; its source is shown.";
    }

    /// <summary>The diagram, or null when the type is not supported here (the caller shows "Diagram preview unavailable" and the source).</summary>
    public static GeneratedDiagram? Read(string block)
    {
        try
        {
            var keyword = FirstKeyword(block);
            var diagram = keyword switch
            {
                "sequenceDiagram" => Sequence(block),
                "graph" or "flowchart" => Graph(block),
                "stateDiagram" or "stateDiagram-v2" => State(block),
                "classDiagram" or "classDiagram-v2" => Class(block),
                "erDiagram" => Er(block),
                _ => null,
            };
            return diagram is null ? null : diagram with { SourceType = keyword };
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

    /// <summary>
    /// Nodes (by id, labelled once at the end) and labelled edges for the state/class/ER readers, emitted in the Graph shape the layout already
    /// draws: node id = its display label, edges by label (labels made unique so two declarations never merge into one box).
    /// </summary>
    private sealed class GraphBuilder
    {
        private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];
        private readonly List<(string From, string To, string? Label)> _edges = [];
        private readonly List<(string Name, List<string> Members)> _groups = [];
        private readonly Stack<int> _open = new();

        public string? CurrentGroup => _open.Count > 0 ? _groups[_open.Peek()].Name : null;

        public string Node(string id, string? label = null)
        {
            id = id.Trim().Trim('"');
            if (!_labels.ContainsKey(id))
            {
                _order.Add(id);
                _labels[id] = Clean(id);
                if (_open.Count > 0) _groups[_open.Peek()].Members.Add(id);
            }
            if (!string.IsNullOrWhiteSpace(label) && Clean(label) is { Length: > 0 } text) _labels[id] = text;
            return id;
        }

        public void Edge(string from, string to, string? label) => _edges.Add((from, to, string.IsNullOrWhiteSpace(label) ? null : Clean(label)));
        public void OpenGroup(string name) { _groups.Add((Clean(name), [])); _open.Push(_groups.Count - 1); }
        public void CloseGroup() { if (_open.Count > 0) _open.Pop(); }

        public GeneratedDiagram? Build(string block)
        {
            if (_order.Count == 0) return null;
            var display = new Dictionary<string, string>(StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in _order)
            {
                var label = _labels[id].Length > 0 ? _labels[id] : id;
                if (!used.Add(label)) { label = $"{label} ({Clean(id)})"; used.Add(label); }
                display[id] = label;
            }
            return new(GeneratedDiagramKind.Graph, block, [], [], [], [.. _order.Select(id => (display[id], display[id]))],
                [.. _edges.Select(e => new DiagramEdge(display[e.From], display[e.To], e.Label))],
                [.. _groups.Where(g => g.Members.Count > 0).Select(g => (g.Name, (IReadOnlyList<string>)[.. g.Members.Select(m => display[m])]))]);
        }
    }

    private static bool Ignorable(string line, params string[] prefixes) =>
        line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal) || prefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal));

    /// <summary>State diagram: transitions "A --> B : label" as labelled edges, [*] as Start/End nodes, composite states as groups. Notes are skipped.</summary>
    private static GeneratedDiagram? State(string block)
    {
        var g = new GraphBuilder();
        var inNote = false;
        string Terminal(bool start) => g.CurrentGroup is { } group
            ? g.Node($"[*]{(start ? "start" : "end")}:{group}", $"{(start ? "Start" : "End")} ({group})")
            : g.Node($"[*]{(start ? "start" : "end")}", start ? "Start" : "End");
        foreach (var raw in block.Split('\n').Skip(1))
        {
            var line = raw.Trim().TrimEnd(';');
            if (inNote) { if (line.StartsWith("end note", StringComparison.Ordinal)) inNote = false; continue; }
            if (Ignorable(line, "direction", "classDef", "class ", "style ", "hide ", "scale ", "accTitle", "accDescr") || line == "--") continue;
            if (line.StartsWith("note ", StringComparison.Ordinal)) { if (!line.Contains(':')) inNote = true; continue; }
            if (line.StartsWith('}')) { g.CloseGroup(); continue; }
            if (StateDecl().Match(line) is { Success: true } d)
            {
                var id = g.Node(d.Groups["id"].Value, d.Groups["label"].Success ? d.Groups["label"].Value : null);
                if (d.Groups["open"].Success) g.OpenGroup(id);
            }
            else if (StateTransition().Match(line) is { Success: true } t)
            {
                var from = t.Groups["a"].Value == "[*]" ? Terminal(true) : g.Node(t.Groups["a"].Value);
                var to = t.Groups["b"].Value == "[*]" ? Terminal(false) : g.Node(t.Groups["b"].Value);
                g.Edge(from, to, t.Groups["label"].Success ? t.Groups["label"].Value : null);
            }
            else if (StateDescription().Match(line) is { Success: true } s) g.Node(s.Groups["id"].Value, s.Groups["desc"].Value);
        }
        return g.Build(block);
    }

    /// <summary>Class diagram, relationships only: "A &lt;|-- B", "A --&gt; B", "A *-- B", "A o-- B", "A ..&gt; B", "A -- B" (optional ": label"); "class X"
    /// declares a node. Members, annotations and cardinalities are not drawn.</summary>
    private static GeneratedDiagram? Class(string block)
    {
        var g = new GraphBuilder();
        var inBody = false;
        foreach (var raw in block.Split('\n').Skip(1))
        {
            var line = raw.Trim().TrimEnd(';');
            if (inBody) { if (line.StartsWith('}')) inBody = false; continue; }
            if (Ignorable(line, "direction", "note ", "classDef", "style ", "cssClass", "click ", "link ", "callback ", "<<", "accTitle", "accDescr", "}")) continue;
            if (ClassDecl().Match(line) is { Success: true } d)
            {
                g.Node(d.Groups["id"].Value, d.Groups["label"].Success ? d.Groups["label"].Value : null);
                if (d.Groups["open"].Success && !line.EndsWith('}')) inBody = true;
            }
            else if (ClassRelation().Match(line) is { Success: true } r)
            {
                var (a, b) = (g.Node(r.Groups["a"].Value), g.Node(r.Groups["b"].Value));
                var (reverse, kind) = r.Groups["op"].Value switch
                {
                    "<|--" => (true, "inherits"),
                    "--|>" => (false, "inherits"),
                    "<|.." => (true, "realizes"),
                    "..|>" => (false, "realizes"),
                    "*--" => (false, "composition"),
                    "--*" => (true, "composition"),
                    "o--" => (false, "aggregation"),
                    "--o" => (true, "aggregation"),
                    "<--" => (true, "association"),
                    "-->" => (false, "association"),
                    "<.." => (true, "dependency"),
                    "..>" => (false, "dependency"),
                    _ => (false, "link"),
                };
                var label = r.Groups["label"].Success && r.Groups["label"].Value.Trim().Length > 0 ? r.Groups["label"].Value : kind;
                if (reverse) g.Edge(b, a, label); else g.Edge(a, b, label);
            }
            else if (ClassMember().Match(line) is { Success: true } m) g.Node(m.Groups["id"].Value);
        }
        return g.Build(block);
    }

    /// <summary>ER diagram, relationships only: "A ||--o{ B : label" as a labelled edge with its cardinality in words; entities become nodes,
    /// attributes are not drawn.</summary>
    private static GeneratedDiagram? Er(string block)
    {
        var g = new GraphBuilder();
        var inBody = false;
        foreach (var raw in block.Split('\n').Skip(1))
        {
            var line = raw.Trim().TrimEnd(';');
            if (inBody) { if (line.StartsWith('}')) inBody = false; continue; }
            if (Ignorable(line, "direction", "style ", "classDef", "accTitle", "accDescr", "title ", "}")) continue;
            if (ErRelation().Match(line) is { Success: true } r)
            {
                var (a, b) = (g.Node(r.Groups["a"].Value), g.Node(r.Groups["b"].Value));
                var rel = r.Groups["rel"].Value;
                var cardinality = $"{Cardinality(rel[..2])} to {Cardinality(rel[^2..])}";
                var label = r.Groups["label"].Success ? Clean(r.Groups["label"].Value) : "";
                g.Edge(a, b, label.Length > 0 ? $"{label} ({cardinality})" : cardinality);
            }
            else if (ErEntity().Match(line) is { Success: true } e)
            {
                g.Node(e.Groups["id"].Value, e.Groups["label"].Success ? e.Groups["label"].Value : null);
                if (e.Groups["open"].Success && !line.EndsWith('}')) inBody = true;
            }
        }
        return g.Build(block);

        static string Cardinality(string token) => token switch
        {
            "||" => "exactly one",
            "|o" or "o|" => "zero or one",
            "}|" or "|{" => "one or more",
            "}o" or "o{" => "zero or more",
            _ => "unspecified",
        };
    }

    /// <summary>Plain text for a label: quotes, HTML line breaks, Mermaid's literal "\n" breaks and HTML entities (&amp;quot; &amp;amp; …) turned into
    /// text, then markup characters neutralised — never interpreted. Escaped text rendered raw ("Validate\nreport") was the visible defect.</summary>
    internal static string Clean(string value)
    {
        var text = HtmlBreak().Replace(value.Trim().Trim('"'), " ").Replace("\\n", " ").Replace("\n", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return MultiSpace().Replace(text.Replace("<", "‹").Replace(">", "›"), " ").Trim();
    }

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
    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant, 200)]
    private static partial Regex MultiSpace();
    [GeneratedRegex(@"^state\s+(?:""(?<label>[^""]*)""\s+as\s+(?<id>[\w.\-]+)|(?<id>[\w.\-]+)(?:\s+as\s+""?(?<label>[^""{]*?)""?)?)\s*(?<open>\{)?\s*(?:<<\w+>>)?$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex StateDecl();
    [GeneratedRegex(@"^(?<a>\[\*\]|[\w.\-]+)\s*-->\s*(?<b>\[\*\]|[\w.\-]+)\s*(?::\s*(?<label>.*))?$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex StateTransition();
    [GeneratedRegex(@"^(?<id>[\w.\-]+)\s*:\s*(?<desc>.+)$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex StateDescription();
    [GeneratedRegex(@"^class\s+(?<id>[\w.\-]+)(?:~[^~]*~)?(?:\s*\[\s*""?(?<label>[^""\]]*)""?\s*\])?\s*(?<open>\{)?.*$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex ClassDecl();
    [GeneratedRegex(@"^(?<a>[\w.\-]+)(?:~[^~]*~)?\s*(?:""[^""]*""\s*)?(?<op><\|--|--\|>|<\|\.\.|\.\.\|>|\*--|--\*|o--|--o|<--|-->|<\.\.|\.\.>|--|\.\.)\s*(?:""[^""]*""\s*)?(?<b>[\w.\-]+)(?:~[^~]*~)?\s*(?::\s*(?<label>.*))?$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex ClassRelation();
    [GeneratedRegex(@"^(?<id>[\w.\-]+)\s*:\s*.+$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex ClassMember();
    [GeneratedRegex(@"^(?<a>""[^""]+""|[\w.\-]+)\s*(?<rel>[|}o]{2}(?:--|\.\.)[|o{]{2})\s*(?<b>""[^""]+""|[\w.\-]+)\s*(?::\s*(?<label>.*))?$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex ErRelation();
    [GeneratedRegex(@"^(?<id>""[^""]+""|[\w.\-]+)\s*(?:\[\s*""?(?<label>[^""\]]*)""?\s*\])?\s*(?<open>\{)?\s*\}?$", RegexOptions.CultureInvariant, 200)]
    private static partial Regex ErEntity();
}
