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

    // ── Journey presentation (generic; no positions are specific to one system) ────────────────────────────────

    /// <summary>One stage of the main journey, in journey order.</summary>
    public sealed record JourneyStage(GuideComponent Component, int Index);

    /// <summary>The connection from stage <paramref name="FromIndex"/> to the next stage (null edge: the two stages are not directly connected).</summary>
    public sealed record JourneyConnector(GuideEdge? Edge, int FromIndex, string Label);

    /// <summary>A connection that goes back along the main journey (e.g. an acknowledgment), drawn on its own track below the stages: spans
    /// on one track never overlap, so two return paths never share a line.</summary>
    public sealed record JourneyReturn(GuideEdge Edge, int FromIndex, int ToIndex, int Track, string Label)
    {
        public int Start => Math.Min(FromIndex, ToIndex);
        public int End => Math.Max(FromIndex, ToIndex);
    }

    /// <summary>How a supporting component relates to a main stage: one request (component → stage or stage → component) and, when both
    /// directions exist, the answer as the same relationship (request/response is one connection, not two crossing arrows).</summary>
    public sealed record JourneyAttachment(GuideEdge Edge, GuideEdge? Reverse, string MemberId, string StageId, bool IntoStage);

    /// <summary>A supporting integration: the components of one side lane, the chain between them and how the chain attaches to the journey.</summary>
    public sealed record JourneyGroup(string Lane, string Title, GuideFlowKind Kind, IReadOnlyList<GuideComponent> Members, IReadOnlyList<GuideEdge> Chain,
        IReadOnlyList<JourneyAttachment> Attachments);

    public sealed record Journey(IReadOnlyList<JourneyStage> Stages, IReadOnlyList<JourneyConnector> Connectors, IReadOnlyList<JourneyReturn> Returns,
        IReadOnlyList<GuideEdge> Bypasses, IReadOnlyList<JourneyGroup> Groups, IReadOnlyList<GuideEdge> Unplaced)
    {
        public int ReturnTracks => Returns.Count == 0 ? 0 : Returns.Max(r => r.Track) + 1;
    }

    /// <summary>
    /// Builds the reading model of a guide's flow:
    /// <list type="bullet">
    /// <item>Stages: the components of the main lane in journey order (flow order; a guide without a "main" lane uses its main-report flow).</item>
    /// <item>Connectors: only between neighbouring stages, so the primary row never has a crossing; a forward connection that skips stages is a
    /// bypass, and a backward one is a return on its own track.</item>
    /// <item>Groups: each side lane with its internal chain and its attachments to stages (both directions merged into one relationship).</item>
    /// <item>Unplaced: connections whose components are unknown (shown as such, never dropped silently).</item>
    /// </list>
    /// </summary>
    public static Journey BuildJourney(SystemGuideContent c)
    {
        var order = c.Flows.SelectMany(f => f.Path).Concat(c.Components.Select(x => x.Id)).Distinct().ToList();
        int Rank(string id) => order.IndexOf(id) is var i and >= 0 ? i : int.MaxValue;
        var ids = c.Components.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var mainFlow = c.Flows.FirstOrDefault(f => f.Kind == GuideFlowKind.MainReport);
        var mainComponents = c.Components.Any(x => x.Lane == "main")
            ? c.Components.Where(x => x.Lane == "main").OrderBy(x => Rank(x.Id)).ToList()
            : mainFlow is null ? [] : mainFlow.Path.Select(id => c.Components.FirstOrDefault(x => x.Id == id)).OfType<GuideComponent>().ToList();
        var stages = mainComponents.Select((x, i) => new JourneyStage(x, i)).ToList();
        var index = stages.ToDictionary(s => s.Component.Id, s => s.Index, StringComparer.Ordinal);

        var connectors = new List<JourneyConnector>();
        for (var i = 0; i + 1 < stages.Count; i++)
        {
            var edge = c.Edges.FirstOrDefault(e => e.From == stages[i].Component.Id && e.To == stages[i + 1].Component.Id);
            connectors.Add(new JourneyConnector(edge, i, edge is null ? "" : Short(edge.DataFormat)));
        }

        var returns = new List<JourneyReturn>();
        var bypasses = new List<GuideEdge>();
        foreach (var e in c.Edges.Where(e => index.ContainsKey(e.From) && index.ContainsKey(e.To)))
        {
            var (from, to) = (index[e.From], index[e.To]);
            if (to == from + 1) continue;
            if (to > from) { bypasses.Add(e); continue; }
            var start = Math.Min(from, to); var end = Math.Max(from, to);
            var track = 0;
            while (returns.Any(r => r.Track == track && !(end < r.Start || start > r.End))) track++;
            returns.Add(new JourneyReturn(e, from, to, track, Essence(e.DataFormat)));
        }

        var groups = new List<JourneyGroup>();
        var lanes = c.Components.Where(x => !index.ContainsKey(x.Id)).GroupBy(x => x.Lane)
            .OrderBy(g => g.Min(x => Rank(x.Id))).ToList();
        foreach (var lane in lanes)
        {
            var members = lane.OrderBy(x => Rank(x.Id)).ToList();
            var memberIds = members.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            var flow = c.Flows.Where(f => f.Kind != GuideFlowKind.MainReport).OrderByDescending(f => f.Path.Count(memberIds.Contains)).ThenBy(f => c.Flows.IndexOf(f))
                .FirstOrDefault(f => f.Path.Any(memberIds.Contains));
            var chain = c.Edges.Where(e => memberIds.Contains(e.From) && memberIds.Contains(e.To)).OrderBy(e => Rank(e.From)).ToList();
            var attachments = new List<JourneyAttachment>();
            foreach (var e in c.Edges.Where(e => (memberIds.Contains(e.From) && index.ContainsKey(e.To)) || (memberIds.Contains(e.To) && index.ContainsKey(e.From))))
            {
                var member = memberIds.Contains(e.From) ? e.From : e.To;
                var stage = member == e.From ? e.To : e.From;
                if (attachments.Any(a => a.Reverse?.Id == e.Id)) continue;
                var reverse = c.Edges.FirstOrDefault(r => r.From == e.To && r.To == e.From && r.Id != e.Id);
                if (reverse is not null && attachments.Any(a => a.Edge.Id == reverse.Id)) continue;
                attachments.Add(new JourneyAttachment(e, reverse, member, stage, IntoStage: member == e.From));
            }
            groups.Add(new JourneyGroup(lane.Key, flow?.Title ?? lane.Key, flow?.Kind ?? GuideFlowKind.SupportingData, members, chain, attachments));
        }

        var unplaced = c.Edges.Where(e => !ids.Contains(e.From) || !ids.Contains(e.To)).ToList();
        return new Journey(stages, connectors, returns, bypasses, groups, unplaced);
    }

    // ── Two-row placement for medium widths (an intentional reflow instead of shrinking text): the first half of the stages on row 1, the rest
    // on row 3; returns of one half on the row below it; the connector that crosses the break sits at the end of row 1 ("continues").

    public static int Half(int stages) => (stages + 1) / 2;

    public static string TwoRowColumns(int stages) =>
        string.Join(" ", Enumerable.Range(0, Half(stages)).Select(_ => "minmax(7.5rem, 1fr) minmax(2.75rem, 3.75rem)"));

    public static (int Row, int Column) TwoRowStage(int index, int stages) =>
        (index < Half(stages) ? 1 : 3, 2 * (index % Half(stages)) + 1);

    public static (int Row, int Column, bool Break) TwoRowConnector(int fromIndex, int stages)
    {
        var half = Half(stages);
        var crosses = fromIndex == half - 1;
        return crosses ? (1, 2 * half, true) : (fromIndex < half ? 1 : 3, 2 * (fromIndex % half) + 2, false);
    }

    public static (int Row, string Columns) TwoRowReturn(int start, int end, int stages)
    {
        var half = Half(stages);
        if (start < half && end < half) return (2, $"{2 * start + 1} / {2 * end + 2}");
        if (start >= half && end >= half) return (4, $"{2 * (start % half) + 1} / {2 * (end % half) + 2}");
        return (4, "1 / -1"); // a return across the break is listed on its own full-width row
    }

    /// <summary>CSS grid columns of the journey: a stage column, then a connector column, alternating (stage i sits in column 2i+1).</summary>
    public static string StageColumns(int stages) => stages <= 0 ? "minmax(0, 1fr)"
        : string.Join(" ", Enumerable.Range(0, stages).Select(i => i == stages - 1 ? "minmax(6.5rem, 1fr)" : "minmax(6.5rem, 1fr) minmax(2.75rem, 3.25rem)"));

    /// <summary>The short label on the diagram (full transport and format are in the connection details).</summary>
    /// <summary>The essence of a format without shortening (return paths have room for the whole phrase).</summary>
    public static string Essence(string format)
    {
        var s = format.Split('(')[0];
        if (s.LastIndexOf(": ", StringComparison.Ordinal) is var colon and >= 0) s = s[(colon + 2)..];
        return s.Split(" + ")[0].Trim();
    }

    public static string Short(string format)
    {
        // The essence of the format: before any parenthesis, after a "Planned:"-style prefix, before a "+" companion ("Parquet + manifest").
        var s = format.Split('(')[0];
        if (s.LastIndexOf(": ", StringComparison.Ordinal) is var colon and >= 0) s = s[(colon + 2)..];
        s = s.Split(" + ")[0].Trim();
        return s.Length > 18 ? s[..17] + "…" : s;
    }


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
