using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class SpecExplorerService
{
    private static readonly Regex HeadingRe = new(
        @"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);

    private static readonly Regex SpecItemRe = new(
        @"\b(FR|NFR|SC|US|UC|AC|TS|REQ)-?\s*(\d{1,4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UserStoryInlinRe = new(
        @"^[-*]\s+(?:User Story|US|Story)\s*[:\-–]\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ClarificationRe = new(
        @"^[-*]\s+(?:Clarification|OPEN|TBD|Question)\s*[:\-–]?\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EntityRe = new(
        @"\b([A-Z][a-z]{2,}(?:[A-Z][a-z]{2,})+)\b",
        RegexOptions.Compiled);

    // ── Public API ────────────────────────────────────────────────────────

    public static SpecTree Parse(string markdown)
    {
        var lines = markdown.Split('\n').Select(l => l.TrimEnd()).ToArray();
        var roots = new List<SpecNode>();

        // headingStack: list of (level, node), ordered from top-level to deepest
        var headingStack = new List<(int Level, SpecNode Node)>();

        // Counts for health summary
        var hHeadings = 0;
        var hReq = 0; var hUs = 0; var hTest = 0;
        var hClr = 0; var hSc = 0; var hEnt = 0; var hDomain = 0;

        // Current excerpt accumulator (per heading section)
        var excerptLines = new List<string>();

        foreach (var line in lines)
        {
            var hm = HeadingRe.Match(line);
            if (hm.Success)
            {
                FlushExcerpt(headingStack, excerptLines);
                excerptLines.Clear();

                var level = hm.Groups[1].Value.Length;
                var rawTitle = hm.Groups[2].Value.Trim();
                var title = StripMarkdown(rawTitle);

                var nodeType = level == 1 ? SpecNodeType.Module
                             : level == 2 ? SpecNodeType.Section
                             : level == 3 ? SpecNodeType.SubSection
                             : SpecNodeType.DeepSection;

                var node = new SpecNode { Title = title, NodeType = nodeType, HeadingLevel = level };
                hHeadings++;

                // Pop until we find a heading with a lower level
                while (headingStack.Count > 0 && headingStack[^1].Level >= level)
                    headingStack.RemoveAt(headingStack.Count - 1);

                if (headingStack.Count == 0)
                    roots.Add(node);
                else
                    headingStack[^1].Node.Children.Add(node);

                headingStack.Add((level, node));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) continue;

            // Must have an active heading parent to attach items to
            if (headingStack.Count == 0)
            {
                excerptLines.Add(line);
                continue;
            }

            var parent = headingStack[^1].Node;

            // Try spec ID patterns (FR-001, SC-002, etc.)
            var sm = SpecItemRe.Match(line);
            if (sm.Success)
            {
                var prefix = sm.Groups[1].Value.ToUpperInvariant();
                var numStr = sm.Groups[2].Value;
                var itemId = $"{prefix}-{numStr.PadLeft(3, '0')}";

                var nodeType = prefix switch
                {
                    "FR" or "NFR" or "REQ" => SpecNodeType.Requirement,
                    "US" or "UC" => SpecNodeType.UserStory,
                    "SC" => SpecNodeType.SuccessCriterion,
                    "AC" or "TS" => SpecNodeType.AcceptanceTest,
                    _ => SpecNodeType.Requirement,
                };

                var rawTitle = line.Trim().TrimStart('-', '*', '>', ' ');
                rawTitle = StripMarkdown(rawTitle);
                if (rawTitle.Length > 160) rawTitle = rawTitle[..160];

                parent.Children.Add(new SpecNode
                {
                    Title = rawTitle,
                    NodeType = nodeType,
                    HeadingLevel = 0,
                    SpecItemId = itemId,
                });

                CountByType(nodeType, ref hReq, ref hUs, ref hTest, ref hClr, ref hSc, ref hEnt, ref hDomain);
                continue;
            }

            // Try inline user story pattern
            var um = UserStoryInlinRe.Match(line);
            if (um.Success)
            {
                var title = StripMarkdown(um.Groups[1].Value.Trim());
                parent.Children.Add(new SpecNode
                {
                    Title = title.Length > 160 ? title[..160] : title,
                    NodeType = SpecNodeType.UserStory,
                    HeadingLevel = 0,
                });
                hUs++;
                continue;
            }

            // Try clarification pattern
            var cm = ClarificationRe.Match(line);
            if (cm.Success)
            {
                var title = StripMarkdown(cm.Groups[1].Value.Trim());
                parent.Children.Add(new SpecNode
                {
                    Title = title.Length > 160 ? title[..160] : title,
                    NodeType = SpecNodeType.Clarification,
                    HeadingLevel = 0,
                });
                hClr++;
                continue;
            }

            // Accumulate excerpt content
            if (excerptLines.Count < 8)
                excerptLines.Add(line);
        }

        FlushExcerpt(headingStack, excerptLines);

        // Post-process: propagate descendant counts up the tree
        foreach (var root in roots)
            PropagateStats(root);

        var health = new SpecHealth
        {
            TotalHeadings = hHeadings,
            Requirements = hReq,
            UserStories = hUs,
            Tests = hTest,
            Clarifications = hClr,
            SuccessCriteria = hSc,
            Entities = hEnt,
            DomainItems = hDomain,
        };

        return new SpecTree { Roots = roots, Health = health };
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void FlushExcerpt(List<(int Level, SpecNode Node)> stack, List<string> lines)
    {
        if (stack.Count == 0 || lines.Count == 0) return;
        var node = stack[^1].Node;
        if (string.IsNullOrEmpty(node.Excerpt))
            node.Excerpt = string.Join(" ", lines.Take(5)).Trim();
    }

    private static void PropagateStats(SpecNode node)
    {
        node.ReqCount = 0;
        node.UserStoryCount = 0;
        node.TestCount = 0;
        node.ClarCount = 0;
        node.ScCount = 0;
        node.TotalDescendants = 0;

        foreach (var child in node.Children)
        {
            if (child.HeadingLevel == 0)
            {
                // Leaf spec item
                switch (child.NodeType)
                {
                    case SpecNodeType.Requirement: node.ReqCount++; break;
                    case SpecNodeType.UserStory: node.UserStoryCount++; break;
                    case SpecNodeType.AcceptanceTest: node.TestCount++; break;
                    case SpecNodeType.Clarification: node.ClarCount++; break;
                    case SpecNodeType.SuccessCriterion: node.ScCount++; break;
                }
                node.TotalDescendants++;
            }
            else
            {
                PropagateStats(child);
                node.ReqCount += child.ReqCount;
                node.UserStoryCount += child.UserStoryCount;
                node.TestCount += child.TestCount;
                node.ClarCount += child.ClarCount;
                node.ScCount += child.ScCount;
                node.TotalDescendants += child.TotalDescendants + 1; // +1 for the heading itself
            }
        }
    }

    private static string StripMarkdown(string s) =>
        Regex.Replace(s, @"[*_`#\[\]]", "").Trim();

    private static void CountByType(SpecNodeType t,
        ref int req, ref int us, ref int test, ref int clr, ref int sc, ref int ent, ref int domain)
    {
        switch (t)
        {
            case SpecNodeType.Requirement: req++; break;
            case SpecNodeType.UserStory: us++; break;
            case SpecNodeType.AcceptanceTest: test++; break;
            case SpecNodeType.Clarification: clr++; break;
            case SpecNodeType.SuccessCriterion: sc++; break;
            case SpecNodeType.Entity: ent++; break;
            case SpecNodeType.DomainItem: domain++; break;
        }
    }

    // ── Tree navigation utilities (used by component) ─────────────────────

    public static SpecNode? FindNode(IEnumerable<SpecNode> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id) return node;
            var found = FindNode(node.Children, id);
            if (found is not null) return found;
        }
        return null;
    }

    public static List<(SpecNode Node, int Depth, bool IsMatch)> GetFlatVisible(
        IEnumerable<SpecNode> roots,
        HashSet<string> expandedIds,
        string searchQuery)
    {
        var result = new List<(SpecNode, int, bool)>();
        HashSet<string>? matchIds = null;
        HashSet<string>? ancestorIds = null;

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            matchIds = [];
            ancestorIds = [];
            CollectMatches(roots, searchQuery, matchIds, ancestorIds, []);
        }

        foreach (var root in roots)
            FlattenNode(root, 0, result, expandedIds, matchIds, ancestorIds);

        return result;
    }

    private static void FlattenNode(
        SpecNode node, int depth,
        List<(SpecNode, int, bool)> result,
        HashSet<string> expanded,
        HashSet<string>? matchIds,
        HashSet<string>? ancestorIds)
    {
        var isMatch = matchIds?.Contains(node.Id) ?? false;
        var isAncestor = ancestorIds?.Contains(node.Id) ?? false;

        // When searching, skip nodes that are neither matches nor ancestors
        if (matchIds is not null && !isMatch && !isAncestor) return;

        result.Add((node, depth, isMatch));

        var forceExpand = matchIds is not null && isAncestor;
        if ((expanded.Contains(node.Id) || forceExpand) && node.Children.Count > 0)
            foreach (var child in node.Children)
                FlattenNode(child, depth + 1, result, expanded, matchIds, ancestorIds);
    }

    private static bool CollectMatches(
        IEnumerable<SpecNode> nodes, string query,
        HashSet<string> matchIds, HashSet<string> ancestorIds,
        List<string> path)
    {
        var anyMatch = false;
        foreach (var node in nodes)
        {
            var isMatch = node.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                       || (node.SpecItemId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

            path.Add(node.Id);
            var childMatch = CollectMatches(node.Children, query, matchIds, ancestorIds, path);
            path.RemoveAt(path.Count - 1);

            if (isMatch || childMatch)
            {
                if (isMatch) matchIds.Add(node.Id);
                foreach (var id in path) ancestorIds.Add(id);
                anyMatch = true;
            }
        }
        return anyMatch;
    }

    public static HashSet<string> GetDefaultExpanded(IEnumerable<SpecNode> roots)
    {
        var expanded = new HashSet<string>();
        foreach (var root in roots)
        {
            expanded.Add(root.Id);                       // level 1 always expanded
            foreach (var l2 in root.Children)
                if (l2.HeadingLevel > 0)
                    expanded.Add(l2.Id);                 // level 2 expanded by default
        }
        return expanded;
    }
}
