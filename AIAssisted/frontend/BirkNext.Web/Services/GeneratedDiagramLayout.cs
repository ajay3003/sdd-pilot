namespace BirkNext.Web.Services;

/// <summary>
/// Deterministic SVG geometry for a diagram read from generated documentation. Only what the diagram declares is placed: participants and
/// ordered messages (sequence), nodes and edges (graph). No edge, node or order is invented; long labels are shortened on screen and kept whole
/// in the element's title. Pure (no rendering), so it is computed once per diagram source and tested directly.
/// </summary>
public sealed record DiagramLayout(double Width, double Height, IReadOnlyList<DiagramBox> Boxes, IReadOnlyList<DiagramLine> Lines, IReadOnlyList<DiagramLabel> Labels)
{
    public static readonly DiagramLayout Empty = new(0, 0, [], [], []);

    private const double ColumnWidth = 180, BoxWidth = 156, BoxHeight = 38, Margin = 20, StepHeight = 44, HeaderHeight = 54;
    private const double RankGap = 90, RowGap = 26;
    private const int MaxLabel = 28, MaxBoxText = 22;

    public static DiagramLayout Of(GeneratedDiagram diagram) => diagram.Kind == GeneratedDiagramKind.Sequence ? Sequence(diagram) : Graph(diagram);

    /// <summary>Participants as columns with lifelines; each message one row, in source order, arrow from sender to receiver.</summary>
    private static DiagramLayout Sequence(GeneratedDiagram d)
    {
        var participants = d.Participants.Count > 0 ? d.Participants.ToList()
            : d.Steps.SelectMany(s => new[] { s.From, s.To }).Distinct(StringComparer.Ordinal).ToList();
        double X(string name) => Margin + ColumnWidth * Math.Max(0, participants.IndexOf(name)) + ColumnWidth / 2;
        var height = HeaderHeight + Margin + StepHeight * Math.Max(1, d.Steps.Count) + Margin;
        var width = Margin * 2 + ColumnWidth * Math.Max(1, participants.Count);
        var boxes = participants.Select(p => new DiagramBox(X(p) - BoxWidth / 2, Margin, BoxWidth, BoxHeight, Short(p, MaxBoxText), p, "gdv-box")).ToList();
        var lines = participants.Select(p => new DiagramLine(X(p), Margin + BoxHeight, X(p), height - Margin / 2, "gdv-lifeline", false)).ToList();
        var labels = new List<DiagramLabel>();
        for (var i = 0; i < d.Steps.Count; i++)
        {
            var step = d.Steps[i];
            var y = HeaderHeight + Margin + StepHeight * i + StepHeight / 2 + 6;
            var x1 = X(step.From);
            var x2 = X(step.To);
            var text = $"{step.Number}. {step.Message}";
            if (step.From == step.To)
            {
                // A message to itself: a small loop on the right of its lifeline.
                lines.Add(new(x1, y - 10, x1 + 30, y - 10, "gdv-message", false));
                lines.Add(new(x1 + 30, y - 10, x1 + 30, y + 4, "gdv-message", false));
                lines.Add(new(x1 + 30, y + 4, x1 + 2, y + 4, "gdv-message", true));
                labels.Add(new(x1 + 34 + ColumnWidth / 4, y - 14, Short(text, MaxLabel), Full(step)));
            }
            else
            {
                lines.Add(new(x1, y, x2, y, step.Branch is null ? "gdv-message" : "gdv-message gdv-branch", true));
                labels.Add(new((x1 + x2) / 2, y - 7, Short(text, MaxLabel + (int)(Math.Abs(x2 - x1) / ColumnWidth - 1) * 20), Full(step)));
            }
        }
        return new(width, height, boxes, lines, labels);

        static string Full(DiagramStep s) => $"{s.Number}. {s.From} → {s.To}: {s.Message}{(s.Branch is { } b ? $" ({b})" : "")}";
    }

    /// <summary>
    /// Nodes in ranks left to right: a node's rank is the longest path to it from a node without incoming edges (cycles are cut by visiting each
    /// node once). Within a rank, nodes keep their declaration order. Edges are straight lines between box sides.
    /// </summary>
    private static DiagramLayout Graph(GeneratedDiagram d)
    {
        var ids = d.Nodes.Select(n => n.Id).Concat(d.Edges.SelectMany(e => new[] { e.From, e.To })).Distinct(StringComparer.Ordinal).ToList();
        var labelOf = d.Nodes.GroupBy(n => n.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Label, StringComparer.Ordinal);
        var outgoing = ids.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        var incoming = ids.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var edge in d.Edges.Where(e => e.From != e.To)) { outgoing[edge.From].Add(edge.To); incoming[edge.To]++; }

        var rank = ids.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var roots = ids.Where(id => incoming[id] == 0).ToList();
        if (roots.Count == 0 && ids.Count > 0) roots.Add(ids[0]);
        // Longest-path ranks over an acyclic traversal: each node is relaxed at most |ids| times (cycle-safe, bounded).
        var queue = new Queue<string>(roots);
        var relaxations = 0;
        while (queue.Count > 0 && relaxations++ < ids.Count * ids.Count + 10)
        {
            var node = queue.Dequeue();
            foreach (var next in outgoing[node])
                if (rank[next] < rank[node] + 1 && rank[node] + 1 < ids.Count) { rank[next] = rank[node] + 1; queue.Enqueue(next); }
        }

        var columns = ids.GroupBy(id => rank[id]).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        var position = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        for (var c = 0; c < columns.Count; c++)
            for (var r = 0; r < columns[c].Count; r++)
                position[columns[c][r]] = (Margin + c * (BoxWidth + RankGap), Margin + r * (BoxHeight + RowGap));
        var width = Margin * 2 + Math.Max(1, columns.Count) * BoxWidth + Math.Max(0, columns.Count - 1) * RankGap;
        var height = Margin * 2 + Math.Max(1, columns.Max(c => c.Count)) * (BoxHeight + RowGap) - RowGap;

        var boxes = ids.Select(id => labelOf.TryGetValue(id, out var label) && label.Length > 0 ? (Id: id, Text: label) : (Id: id, Text: id))
            .Select(n => new DiagramBox(position[n.Id].X, position[n.Id].Y, BoxWidth, BoxHeight, Short(n.Text, MaxBoxText), n.Text, "gdv-box")).ToList();
        var lines = new List<DiagramLine>();
        var labels = new List<DiagramLabel>();
        foreach (var edge in d.Edges)
        {
            var (fx, fy) = position[edge.From];
            var (tx, ty) = position[edge.To];
            var forward = tx > fx;
            var x1 = forward ? fx + BoxWidth : fx + BoxWidth / 2;
            var y1 = forward ? fy + BoxHeight / 2 : fy + BoxHeight;
            var x2 = forward ? tx : tx + BoxWidth / 2;
            var y2 = forward ? ty + BoxHeight / 2 : ty;
            if (edge.From == edge.To) { x1 = fx + BoxWidth; y1 = fy + 8; x2 = fx + BoxWidth; y2 = fy + BoxHeight - 8; }
            lines.Add(new(x1, y1, x2, y2, "gdv-edge", true));
            if (edge.Label is { Length: > 0 } text) labels.Add(new((x1 + x2) / 2, (y1 + y2) / 2 - 5, Short(text, 18), $"{edge.From} → {edge.To}: {text}"));
        }
        return new(width, height, boxes, lines, labels);
    }

    private static string Short(string text, int max) => text.Length <= max ? text : text[..Math.Max(1, max - 1)].TrimEnd() + "…";
}

public sealed record DiagramBox(double X, double Y, double W, double H, string Text, string Full, string Class);
public sealed record DiagramLine(double X1, double Y1, double X2, double Y2, string Class, bool Arrow);
public sealed record DiagramLabel(double X, double Y, string Text, string Full);
