using BirkNext.SystemUnderstanding;

namespace BirkNext.Web.Services;

/// <summary>
/// Pure presentation of a System Understanding guide: evidence labels (text + glyph, never colour alone), the flow layout (a main lane and
/// packed side lanes computed from the guide itself — nothing is system-specific), flow highlighting and local search.
/// </summary>
public static class SystemUnderstandingPresentation
{
    public sealed record EvidenceBadge(string Label, string Tone, string Glyph, string Meaning);

    public static EvidenceBadge Badge(GuideEvidenceClass c) => c switch
    {
        GuideEvidenceClass.RuntimeVerified => new("Runtime verified", "ok", "✓", "Observed in a running environment."),
        GuideEvidenceClass.TestVerified => new("Test verified", "ok", "✓", "A current executed test passed."),
        GuideEvidenceClass.SourceImplemented => new("In source", "info", "◆", "The code exists in the reviewed source. That proves code, not that the integration works."),
        GuideEvidenceClass.TestDefined => new("Test defined", "info", "◇", "A test exists in source. It has not been executed here."),
        GuideEvidenceClass.Specified => new("Specified", "neutral", "▢", "Described in a specification or design; not shown in code."),
        GuideEvidenceClass.Inferred => new("Inferred", "neutral", "~", "Reasoned from evidence; not shown directly."),
        GuideEvidenceClass.NeedsClarification => new("Needs clarification", "warn", "?", "Not established; must be checked against source or the owner."),
        GuideEvidenceClass.Conflicting => new("Conflicting", "bad", "!", "Sources disagree."),
        _ => new("Not assessed", "neutral", "–", "Nothing has assessed this."),
    };

    /// <summary>The distinct evidence classes of an item, most significant first (open questions before confirmations), at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<GuideEvidenceClass> Classes(IEnumerable<GuideEvidence> evidence, int max = 2)
    {
        GuideEvidenceClass[] order = [GuideEvidenceClass.Conflicting, GuideEvidenceClass.NeedsClarification, GuideEvidenceClass.RuntimeVerified, GuideEvidenceClass.TestVerified,
            GuideEvidenceClass.SourceImplemented, GuideEvidenceClass.TestDefined, GuideEvidenceClass.Specified, GuideEvidenceClass.Inferred, GuideEvidenceClass.NotAssessed];
        var present = evidence.Select(e => e.Class).Distinct().ToHashSet();
        var result = order.Where(present.Contains).Take(max).ToList();
        return result.Count == 0 ? [GuideEvidenceClass.NotAssessed] : result;
    }

    public static (string Label, string Css) Kind(GuideFlowKind kind) => kind switch
    {
        GuideFlowKind.MainReport => ("Main flow", "k-main"),
        GuideFlowKind.SupportingData => ("Supporting data", "k-support"),
        GuideFlowKind.IdentityLookup => ("Identity lookup", "k-identity"),
        GuideFlowKind.PlacementLookup => ("Placement lookup", "k-placement"),
        GuideFlowKind.Acknowledgment => ("Acknowledgment", "k-ack"),
        GuideFlowKind.PeriodicExport => ("Periodic export", "k-export"),
        _ => ("Scheduled / event", "k-event"),
    };

    public static (string Label, string Tone) ScenarioState(GuideScenarioState s) => s switch
    {
        GuideScenarioState.Verified => ("Verified", "ok"),
        GuideScenarioState.Executed => ("Executed", "info"),
        GuideScenarioState.ExistingDefinition => ("Test defined", "info"),
        GuideScenarioState.NeedsClarification => ("Needs clarification", "warn"),
        _ => ("Suggested", "neutral"),
    };

    // ── Flow layout ─────────────────────────────────────────────────────────────────────────────────────────

    public const int ColumnWidth = 226, RowHeight = 138, NodeWidth = 144, NodeHeight = 68, Padding = 24, TopMargin = 46;

    public sealed record LaidNode(GuideComponent Component, int Column, int Row)
    {
        public double X => Padding + Column * ColumnWidth;
        public double Y => TopMargin + Row * RowHeight;
        public double CenterX => X + NodeWidth / 2.0;
        public double CenterY => Y + NodeHeight / 2.0;
    }

    public sealed record LaidEdge(GuideEdge Edge, string Path, double LabelX, double LabelY, string ShortLabel);

    public sealed record FlowLayout(IReadOnlyList<LaidNode> Nodes, IReadOnlyList<LaidEdge> Edges, double Width, double Height);

    /// <summary>
    /// Main-lane components in journey order on row 0; each side lane is placed next to the main component it connects to (its connecting
    /// node directly under it, the rest of the lane leading in from the left) and packed into the first row where it does not overlap.
    /// </summary>
    public static FlowLayout Layout(SystemGuideContent c)
    {
        var order = c.Flows.SelectMany(f => f.Path).Concat(c.Components.Select(x => x.Id)).Distinct().ToList();
        int Rank(string id) => order.IndexOf(id) is var i and >= 0 ? i : int.MaxValue;
        var byId = c.Components.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var main = c.Components.Where(x => x.Lane == "main").OrderBy(x => Rank(x.Id)).ToList();
        var nodes = main.Select((x, i) => new LaidNode(x, i, 0)).ToList();
        var column = nodes.ToDictionary(n => n.Component.Id, n => n.Column, StringComparer.Ordinal);
        var occupied = new List<(int Row, int From, int To)> { (0, 0, Math.Max(0, main.Count - 1)) };
        foreach (var lane in c.Components.Where(x => x.Lane != "main").Select(x => x.Lane).Distinct().ToList())
        {
            var members = c.Components.Where(x => x.Lane == lane).OrderBy(x => Rank(x.Id)).ToList();
            var link = c.Edges.FirstOrDefault(e => (members.Any(m => m.Id == e.From) && column.ContainsKey(e.To)) || (members.Any(m => m.Id == e.To) && column.ContainsKey(e.From)));
            var anchor = link is null ? 0 : column[column.ContainsKey(link.To) ? link.To : link.From];
            var connector = link is null ? members.Last().Id : members.Any(m => m.Id == link.From) ? link.From : link.To;
            // A lane that feeds the main flow leads in from the left and ends one column before its anchor; a lane the main flow calls sits
            // under its anchor (a single node may move one column aside to share a row). Then the first row where the lane does not overlap.
            var index = members.FindIndex(m => m.Id == connector);
            var feeds = link is not null && members.Any(m => m.Id == link.From);
            int[] candidates = feeds && members.Count > 1 ? [anchor - members.Count]
                : members.Count == 1 ? [anchor, anchor + 1, anchor - 1] : [anchor - index];
            int start = 0, row = 0;
            for (var r = 1; row == 0; r++)
                foreach (var candidate in candidates.Select(x => Math.Max(0, x)))
                    if (!occupied.Any(o => o.Row == r && !(candidate + members.Count - 1 < o.From || candidate > o.To))) { start = candidate; row = r; break; }
            occupied.Add((row, start, start + members.Count - 1));
            for (var i = 0; i < members.Count; i++)
            {
                var laid = new LaidNode(members[i], start + i, row);
                nodes.Add(laid);
                column.TryAdd(members[i].Id, laid.Column);
            }
        }
        var at = nodes.ToDictionary(n => n.Component.Id, StringComparer.Ordinal);
        var edges = new List<LaidEdge>();
        foreach (var e in c.Edges)
        {
            if (!at.TryGetValue(e.From, out var a) || !at.TryGetValue(e.To, out var b)) continue;
            string path; double lx, ly;
            if (a.Row == b.Row && b.Column > a.Column)
            {
                var y = a.CenterY;
                path = $"M {F(a.X + NodeWidth)} {F(y)} L {F(b.X)} {F(y)}";
                (lx, ly) = ((a.X + NodeWidth + b.X) / 2, y - 8);
            }
            else if (a.Row == b.Row)
            {
                // A reverse edge on the same row (e.g. an acknowledgment) arcs over the row.
                var top = a.Y - 30;
                path = $"M {F(a.CenterX - 18)} {F(a.Y)} C {F(a.CenterX - 18)} {F(top)}, {F(b.CenterX + 18)} {F(top)}, {F(b.CenterX + 18)} {F(b.Y)}";
                (lx, ly) = ((a.CenterX + b.CenterX) / 2, top + 2);
            }
            else if (b.Row > a.Row)
            {
                var x1 = a.CenterX + (b.Column - a.Column) * 4; var y1 = a.Y + NodeHeight; var y2 = b.Y;
                path = $"M {F(x1)} {F(y1)} C {F(x1)} {F((y1 + y2) / 2)}, {F(b.CenterX)} {F((y1 + y2) / 2)}, {F(b.CenterX)} {F(y2)}";
                (lx, ly) = ((x1 + b.CenterX) / 2 + 6, (y1 + y2) / 2);
            }
            else
            {
                // Upwards (a side flow feeding the main lane): leave from the top of the source, travel through the gap between the rows and
                // enter the target from below — never across a node of the source's row.
                var x1 = a.Column < b.Column ? a.X + NodeWidth - 18 : a.Column > b.Column ? a.X + 18 : a.CenterX; var y1 = a.Y;
                var x2 = a.Column < b.Column ? b.X + 18 : a.Column > b.Column ? b.X + NodeWidth - 18 : b.CenterX; var y2 = b.Y + NodeHeight;
                var mid = (y1 + y2) / 2;
                path = $"M {F(x1)} {F(y1)} C {F(x1)} {F(mid)}, {F(x2)} {F(mid)}, {F(x2)} {F(y2)}";
                (lx, ly) = ((x1 + x2) / 2, mid - 6);
            }
            edges.Add(new LaidEdge(e, path, lx, ly, Short(e.DataFormat)));
        }
        var columns = nodes.Count == 0 ? 1 : nodes.Max(n => n.Column) + 1;
        var rows = nodes.Count == 0 ? 1 : nodes.Max(n => n.Row) + 1;
        return new FlowLayout(nodes, edges, Padding * 2 + (columns - 1) * ColumnWidth + NodeWidth, TopMargin + (rows - 1) * RowHeight + NodeHeight + Padding);
    }

    /// <summary>The short label on the diagram (full transport and format are in the connection details).</summary>
    public static string Short(string format)
    {
        // The essence of the format: before any parenthesis, after a "Planned:"-style prefix, before a "+" companion ("Parquet + manifest").
        var s = format.Split('(')[0];
        if (s.LastIndexOf(": ", StringComparison.Ordinal) is var colon and >= 0) s = s[(colon + 2)..];
        s = s.Split(" + ")[0].Trim();
        return s.Length > 14 ? s[..13] + "…" : s;
    }

    private static string F(double v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The edges of a flow (consecutive path pairs, in either direction) for highlighting.</summary>
    public static IReadOnlySet<string> FlowEdges(SystemGuideContent c, GuideFlow flow)
    {
        var pairs = flow.Path.Zip(flow.Path.Skip(1)).ToList();
        return c.Edges.Where(e => pairs.Any(p => (p.First == e.From && p.Second == e.To) || (flow.Kind == GuideFlowKind.Acknowledgment && p.First == e.From && p.Second == e.To)))
            .Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
    }

    // ── Search ──────────────────────────────────────────────────────────────────────────────────────────────

    public sealed record SearchResult(IReadOnlySet<string> Steps, IReadOnlySet<string> Rules, IReadOnlySet<string> Scenarios, IReadOnlySet<string> Components)
    {
        public int Count => Steps.Count + Rules.Count + Scenarios.Count + Components.Count;
    }

    /// <summary>Case- and accent-insensitive local search across step titles and texts, component names, rules, contracts and test guidance.</summary>
    public static SearchResult Search(SystemGuideContent c, string? query)
    {
        var terms = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Norm).Where(t => t.Length > 0).ToArray();
        if (terms.Length == 0) return new(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), new HashSet<string>());
        bool Hit(params string?[] texts) { var all = Norm(string.Join(' ', texts.Where(t => t is not null))); return terms.All(all.Contains); }
        var names = c.Components.ToDictionary(x => x.Id, x => x.Name);
        return new(
            c.Steps.Where(s => Hit(s.Title, s.Summary, s.Processing, s.Input, s.Output, s.DataFormat, s.HowToVerify, names.GetValueOrDefault(s.ComponentId), string.Join(' ', s.WhatCanGoWrong)))
                .Select(s => s.Id).ToHashSet(),
            c.Rules.Where(r => Hit(r.Title, r.Summary, r.Category, r.ExpectedBehavior, r.WhyItMatters)).Select(r => r.Id).ToHashSet(),
            c.Scenarios.Where(s => Hit(s.Title, s.Objective, s.ExpectedOutcome, s.TestData, string.Join(' ', s.ExistingTests))).Select(s => s.Id).ToHashSet(),
            c.Components.Where(x => Hit(x.Name, x.Responsibility, x.Description, x.Technology)).Select(x => x.Id)
                .Concat(c.Edges.Where(e => Hit(e.Transport, e.DataFormat, e.Contract, e.ContractVersion)).SelectMany(e => new[] { e.From, e.To })).ToHashSet());
    }

    private static string Norm(string s) => new string(s.Normalize(System.Text.NormalizationForm.FormD)
        .Where(ch => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray())
        .ToLowerInvariant().Replace("ø", "o").Replace("æ", "ae").Replace("å", "a");
}
