using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class TaskExplorerService
{
    // ── Regex patterns ────────────────────────────────────────────────────

    private static readonly Regex HeadingRe = new(
        @"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);

    private static readonly Regex CheckboxTaskRe = new(
        @"^\s*[-*]\s+\[([xX ])\]\s+(.+)$", RegexOptions.Compiled);

    private static readonly Regex BareTaskRe = new(
        @"^\s*[-*]?\s*T(\d{2,4})\b\s*[-–.]?\s*(.*)$", RegexOptions.Compiled);

    private static readonly Regex TaskIdRe = new(
        @"\bT(\d{2,4})\b", RegexOptions.Compiled);

    private static readonly Regex TaskRangeRe = new(
        @"\bT(\d{2,4})[–\-]T(\d{2,4})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ParallelRe = new(
        @"\[P\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UserStoryTagRe = new(
        @"\[US(\d+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FrRefRe = new(
        @"\b(FR)-?(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ScRefRe = new(
        @"\b(SC)-?(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TableRowRe = new(
        @"^\|(.+)\|$", RegexOptions.Compiled);

    private static readonly Regex TableSepRe = new(
        @"^\|[\s\-\|:]+\|$", RegexOptions.Compiled);

    // Known task group heading keywords (### level)
    private static readonly HashSet<string> TaskGroupKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "tests", "test", "implementation", "domain entities", "entity", "entities",
        "reference data", "infrastructure", "polish", "cross-cutting concerns",
        "dependencies", "execution order", "parallel execution", "implementation strategy",
        "success criteria traceability", "success criteria", "traceability", "notes",
        "setup", "configuration", "integration", "dependencies & execution order",
    };

    // ── Public API ────────────────────────────────────────────────────────

    public static TaskTree Parse(string markdown)
    {
        var lines = markdown.Split('\n').Select(l => l.TrimEnd()).ToArray();
        var roots = new List<TaskNode>();
        var headingStack = new List<(int Level, TaskNode Node)>();

        var hTasks = 0; var hCompleted = 0; var hPhases = 0;
        var hTables = 0; var hRows = 0;

        var tableBuffer = new List<string>();
        var i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            // Flush any pending table when we hit a non-table line
            if (tableBuffer.Count > 0 && !TableRowRe.IsMatch(line))
            {
                var tableNode = ParseTable(tableBuffer, ref hRows);
                if (tableNode is not null)
                {
                    hTables++;
                    AddToParent(roots, headingStack, tableNode);
                }
                tableBuffer.Clear();
            }

            // Collect table lines
            if (TableRowRe.IsMatch(line))
            {
                tableBuffer.Add(line);
                i++;
                continue;
            }

            // Heading
            var hm = HeadingRe.Match(line);
            if (hm.Success)
            {
                var level = hm.Groups[1].Value.Length;
                var rawTitle = hm.Groups[2].Value.Trim();
                var title = StripMarkdown(rawTitle);
                var nodeType = ClassifyHeading(level, rawTitle);

                if (nodeType == TaskNodeType.Phase) hPhases++;

                var node = new TaskNode { Title = title, NodeType = nodeType, HeadingLevel = level };

                while (headingStack.Count > 0 && headingStack[^1].Level >= level)
                    headingStack.RemoveAt(headingStack.Count - 1);

                if (headingStack.Count == 0) roots.Add(node);
                else headingStack[^1].Node.Children.Add(node);

                headingStack.Add((level, node));
                i++;
                continue;
            }

            // Checkbox task: - [ ] T001 ... or - [x] T002 ...
            var cm = CheckboxTaskRe.Match(line);
            if (cm.Success)
            {
                var completed = cm.Groups[1].Value is "x" or "X";
                var body = cm.Groups[2].Value.Trim();
                var task = BuildTaskNode(body, completed, line);
                if (task is not null)
                {
                    hTasks++;
                    if (completed) hCompleted++;
                    AddToParent(roots, headingStack, task);
                }
                i++;
                continue;
            }

            // Bare task: T001 Description (not inside a checkbox pattern)
            var bm = BareTaskRe.Match(line);
            if (bm.Success && headingStack.Count > 0)
            {
                var body = $"T{bm.Groups[1].Value} {bm.Groups[2].Value}".Trim();
                var task = BuildTaskNode(body, false, line);
                if (task is not null)
                {
                    hTasks++;
                    AddToParent(roots, headingStack, task);
                }
                i++;
                continue;
            }

            i++;
        }

        // Flush final table buffer
        if (tableBuffer.Count > 0)
        {
            var tableNode = ParseTable(tableBuffer, ref hRows);
            if (tableNode is not null)
            {
                hTables++;
                AddToParent(roots, headingStack, tableNode);
            }
        }

        // Propagate descendant counts
        foreach (var root in roots)
            PropagateStats(root);

        var health = new TaskHealth
        {
            TotalTasks = hTasks,
            CompletedTasks = hCompleted,
            TotalPhases = hPhases,
            TablesDetected = hTables,
            TraceabilityRows = hRows,
        };

        return new TaskTree { Roots = roots, Health = health };
    }

    public static TaskNode? FindNode(IEnumerable<TaskNode> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id) return node;
            var found = FindNode(node.Children, id);
            if (found is not null) return found;
        }
        return null;
    }

    public static List<(TaskNode Node, int Depth, bool IsMatch)> GetFlatVisible(
        IEnumerable<TaskNode> roots,
        HashSet<string> expandedIds,
        string searchQuery,
        string? filter,
        HashSet<string>? tableLinkedTaskIds = null)
    {
        var result = new List<(TaskNode, int, bool)>();

        HashSet<string>? matchIds = null;
        HashSet<string>? ancestorIds = null;

        if (!string.IsNullOrWhiteSpace(searchQuery) || !string.IsNullOrEmpty(filter))
        {
            matchIds = [];
            ancestorIds = [];
            CollectMatches(roots, searchQuery, filter, tableLinkedTaskIds, matchIds, ancestorIds, []);
        }

        foreach (var root in roots)
            FlattenNode(root, 0, result, expandedIds, matchIds, ancestorIds);

        return result;
    }

    public static HashSet<string> GetDefaultExpanded(IEnumerable<TaskNode> roots)
    {
        var expanded = new HashSet<string>();
        foreach (var root in roots)
        {
            expanded.Add(root.Id);
            foreach (var l2 in root.Children)
                if (l2.HeadingLevel > 0 || l2.NodeType == TaskNodeType.TableSection)
                    expanded.Add(l2.Id);
        }
        return expanded;
    }

    public static void EnrichWithReport(TaskTree tree, AlignmentReport report)
    {
        var taskMap = new Dictionary<string, TaskNode>(StringComparer.OrdinalIgnoreCase);
        CollectTaskNodes(tree.Roots, taskMap);

        foreach (var finding in report.Findings)
        {
            if (!taskMap.TryGetValue(finding.TaskId, out var node)) continue;
            node.Status = finding.Status;
            node.Risk = finding.Risk;
            node.Impact = finding.ImpactLevel;
            node.IsRegressionCandidate = finding.IsRegressionCandidate;
            node.AffectedAreas = finding.AffectedAreas.Select(a => a.ToString()).ToList();
            node.SpecMatches = [.. finding.Matches];
        }
    }

    public static TaskHealth ComputeEnrichedHealth(TaskTree tree)
    {
        var all = new List<TaskNode>();
        CollectTaskNodes(tree.Roots, null, all);

        var tasks = all.Where(n => n.NodeType == TaskNodeType.Task).ToList();
        var tables = all.Where(n => n.NodeType == TaskNodeType.TableSection).Count();
        var rows   = all.Where(n => n.NodeType == TaskNodeType.TableRow).Count();

        return new TaskHealth
        {
            TotalTasks = tree.Health.TotalTasks,
            CompletedTasks = tree.Health.CompletedTasks,
            TotalPhases = tree.Health.TotalPhases,
            TablesDetected = tables > 0 ? tables : tree.Health.TablesDetected,
            TraceabilityRows = rows > 0 ? rows : tree.Health.TraceabilityRows,
            SpecLinked = tasks.Count(t => t.Status == AlignmentStatus.Linked),
            TechnicalOnly = tasks.Count(t => t.Status == AlignmentStatus.TechnicalOnly),
            NeedsReview = tasks.Count(t => t.Status == AlignmentStatus.NeedsReview),
            PossibleDeviations = tasks.Count(t => t.Status == AlignmentStatus.PossibleDeviation),
            HighRisk = tasks.Count(t => t.Risk == AlignmentRisk.High),
            RegressionCandidates = tasks.Count(t => t.IsRegressionCandidate),
        };
    }

    // Build a set of all task IDs that appear in any TableRow.LinkedTaskIds
    public static HashSet<string> BuildTableLinkedTaskIds(IEnumerable<TaskNode> roots)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectTableLinkedIds(roots, ids);
        return ids;
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private static TaskNodeType ClassifyHeading(int level, string rawTitle)
    {
        if (level == 1) return TaskNodeType.Phase;
        if (level == 2)
        {
            var lower = rawTitle.ToLowerInvariant();
            if (lower.Contains("phase") || lower.Contains("user stor") || lower.Contains("us"))
                return TaskNodeType.Phase;
            return TaskNodeType.Phase; // treat all ## as Phase
        }
        if (level == 3)
        {
            var lower = rawTitle.ToLowerInvariant();
            if (Regex.IsMatch(lower, @"\bus\s*\d+\b|\buser stor") ||
                UserStoryTagRe.IsMatch(rawTitle))
                return TaskNodeType.UserStoryGroup;
            if (TaskGroupKeywords.Any(kw => lower.Contains(kw)))
                return TaskNodeType.TaskGroup;
            return TaskNodeType.TaskGroup;
        }
        return TaskNodeType.DeepGroup;
    }

    private static TaskNode? BuildTaskNode(string body, bool completed, string rawLine)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        var isParallel = ParallelRe.IsMatch(body);
        var usMatch = UserStoryTagRe.Match(body);
        var userStoryTag = usMatch.Success ? $"US{usMatch.Groups[1].Value}" : null;

        var frIds = FrRefRe.Matches(body)
            .Select(m => $"FR-{m.Groups[2].Value.PadLeft(2, '0')}").Distinct().ToList();
        var scIds = ScRefRe.Matches(body)
            .Select(m => $"SC-{m.Groups[2].Value.PadLeft(2, '0')}").Distinct().ToList();

        // Find first task ID in the body
        var taskMatch = TaskIdRe.Match(body);
        string? taskId = null;
        if (taskMatch.Success)
            taskId = $"T{taskMatch.Groups[1].Value.PadLeft(3, '0')}";

        // Clean title: strip [P], [USN], remove redundant task ID prefix from display text
        var title = ParallelRe.Replace(body, "").Trim();
        title = UserStoryTagRe.Replace(title, "").Trim();
        title = FrRefRe.Replace(title, "").Trim();
        title = ScRefRe.Replace(title, "").Trim();
        title = StripMarkdown(title);
        if (title.Length > 200) title = title[..200];

        return new TaskNode
        {
            Title = title,
            NodeType = TaskNodeType.Task,
            HeadingLevel = 0,
            TaskId = taskId,
            IsCompleted = completed,
            IsParallel = isParallel,
            UserStoryTag = userStoryTag,
            ReferencedFrIds = frIds,
            ReferencedScIds = scIds,
            RawText = rawLine.Trim(),
        };
    }

    private static TaskNode? ParseTable(List<string> lines, ref int rowCount)
    {
        if (lines.Count < 2) return null;

        // Parse header row
        var headers = SplitCells(lines[0]);
        if (headers.Count == 0) return null;

        var tableNode = new TaskNode
        {
            Title = string.Join(" | ", headers),
            NodeType = TaskNodeType.TableSection,
            HeadingLevel = 0,
            TableHeaders = headers,
        };

        // Find separator row index
        int dataStart = 1;
        if (dataStart < lines.Count && TableSepRe.IsMatch(lines[dataStart]))
            dataStart++;

        // Parse data rows
        for (int i = dataStart; i < lines.Count; i++)
        {
            var cells = SplitCells(lines[i]);
            if (cells.Count == 0) continue;

            var rowTitle = cells.Count > 0 ? cells[0].Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(rowTitle)) continue;

            // Expand task ranges and collect task IDs from all cells
            var allCellText = string.Join(" ", cells);
            var linkedTaskIds = ExtractTaskIds(allCellText);

            var rowNode = new TaskNode
            {
                Title = rowTitle,
                NodeType = TaskNodeType.TableRow,
                HeadingLevel = 0,
                CellValues = cells,
                LinkedTaskIds = linkedTaskIds,
                ReferencedScIds = ScRefRe.Matches(allCellText)
                    .Select(m => $"SC-{m.Groups[2].Value.PadLeft(2, '0')}").Distinct().ToList(),
                ReferencedFrIds = FrRefRe.Matches(allCellText)
                    .Select(m => $"FR-{m.Groups[2].Value.PadLeft(2, '0')}").Distinct().ToList(),
            };

            // Add TableTaskRef children for each linked task ID
            foreach (var tid in linkedTaskIds)
            {
                rowNode.Children.Add(new TaskNode
                {
                    Title = tid,
                    NodeType = TaskNodeType.TableTaskRef,
                    HeadingLevel = 0,
                    TaskId = tid,
                });
            }

            tableNode.Children.Add(rowNode);
            rowCount++;
        }

        return tableNode.Children.Count > 0 || headers.Count > 0 ? tableNode : null;
    }

    private static List<string> ExtractTaskIds(string text)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Expand ranges first: T034–T036 → T034, T035, T036
        var expanded = TaskRangeRe.Replace(text, m =>
        {
            var from = int.Parse(m.Groups[1].Value);
            var to = int.Parse(m.Groups[2].Value);
            if (to < from || to - from > 50) return m.Value; // sanity limit
            return string.Join(", ", Enumerable.Range(from, to - from + 1)
                .Select(n => $"T{n:D3}"));
        });

        foreach (Match m in TaskIdRe.Matches(expanded))
        {
            var id = $"T{m.Groups[1].Value.PadLeft(3, '0')}";
            if (seen.Add(id)) result.Add(id);
        }
        return result;
    }

    private static List<string> SplitCells(string line)
    {
        // Remove leading/trailing |
        var inner = line.Trim();
        if (inner.StartsWith('|')) inner = inner[1..];
        if (inner.EndsWith('|')) inner = inner[..^1];
        return inner.Split('|').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
    }

    private static void AddToParent(List<TaskNode> roots, List<(int Level, TaskNode Node)> stack, TaskNode child)
    {
        if (stack.Count == 0) roots.Add(child);
        else stack[^1].Node.Children.Add(child);
    }

    private static void PropagateStats(TaskNode node)
    {
        node.TaskCount = 0;
        node.CompletedCount = 0;
        node.TotalDescendants = 0;

        foreach (var child in node.Children)
        {
            if (child.NodeType == TaskNodeType.Task)
            {
                node.TaskCount++;
                if (child.IsCompleted) node.CompletedCount++;
                node.TotalDescendants++;
            }
            else if (child.NodeType is TaskNodeType.TableTaskRef)
            {
                node.TotalDescendants++;
            }
            else
            {
                PropagateStats(child);
                node.TaskCount += child.TaskCount;
                node.CompletedCount += child.CompletedCount;
                node.TotalDescendants += child.TotalDescendants + 1;
            }
        }
    }

    private static void CollectTaskNodes(
        IEnumerable<TaskNode> nodes,
        Dictionary<string, TaskNode>? map,
        List<TaskNode>? list = null)
    {
        foreach (var node in nodes)
        {
            list?.Add(node);
            if (map is not null && node.NodeType == TaskNodeType.Task && node.TaskId is not null)
                map.TryAdd(node.TaskId, node);
            CollectTaskNodes(node.Children, map, list);
        }
    }

    private static void CollectTableLinkedIds(IEnumerable<TaskNode> nodes, HashSet<string> ids)
    {
        foreach (var node in nodes)
        {
            if (node.NodeType == TaskNodeType.TableRow)
                foreach (var tid in node.LinkedTaskIds) ids.Add(tid);
            CollectTableLinkedIds(node.Children, ids);
        }
    }

    private static void FlattenNode(
        TaskNode node, int depth,
        List<(TaskNode, int, bool)> result,
        HashSet<string> expanded,
        HashSet<string>? matchIds,
        HashSet<string>? ancestorIds)
    {
        var isMatch = matchIds?.Contains(node.Id) ?? false;
        var isAncestor = ancestorIds?.Contains(node.Id) ?? false;

        if (matchIds is not null && !isMatch && !isAncestor) return;

        result.Add((node, depth, isMatch));

        var forceExpand = matchIds is not null && isAncestor;
        if ((expanded.Contains(node.Id) || forceExpand) && node.Children.Count > 0)
            foreach (var child in node.Children)
                FlattenNode(child, depth + 1, result, expanded, matchIds, ancestorIds);
    }

    private static bool CollectMatches(
        IEnumerable<TaskNode> nodes,
        string searchQuery,
        string? filter,
        HashSet<string>? tableLinkedIds,
        HashSet<string> matchIds,
        HashSet<string> ancestorIds,
        List<string> path)
    {
        var anyMatch = false;
        foreach (var node in nodes)
        {
            var isMatch = MatchesSearchAndFilter(node, searchQuery, filter, tableLinkedIds);

            path.Add(node.Id);
            var childMatch = CollectMatches(node.Children, searchQuery, filter, tableLinkedIds, matchIds, ancestorIds, path);
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

    private static bool MatchesSearchAndFilter(
        TaskNode node,
        string searchQuery,
        string? filter,
        HashSet<string>? tableLinkedIds)
    {
        // Search match (empty query = matches all)
        var searchMatch = string.IsNullOrWhiteSpace(searchQuery) || NodeMatchesSearch(node, searchQuery);

        // Filter match (empty filter = matches all)
        var filterMatch = string.IsNullOrEmpty(filter) || NodeMatchesFilter(node, filter, tableLinkedIds);

        return searchMatch && filterMatch;
    }

    private static bool NodeMatchesSearch(TaskNode node, string q)
    {
        var ci = StringComparison.OrdinalIgnoreCase;
        if (node.Title.Contains(q, ci)) return true;
        if (node.TaskId?.Contains(q, ci) ?? false) return true;
        if (node.UserStoryTag?.Contains(q, ci) ?? false) return true;
        if (node.ReferencedFrIds.Any(id => id.Contains(q, ci))) return true;
        if (node.ReferencedScIds.Any(id => id.Contains(q, ci))) return true;
        if (node.CellValues.Any(c => c.Contains(q, ci))) return true;
        if (node.LinkedTaskIds.Any(t => t.Contains(q, ci))) return true;
        return false;
    }

    private static bool NodeMatchesFilter(TaskNode node, string filter, HashSet<string>? tableLinkedIds)
    {
        return filter switch
        {
            "Completed"         => node.NodeType == TaskNodeType.Task && node.IsCompleted,
            "Open"              => node.NodeType == TaskNodeType.Task && !node.IsCompleted,
            "SpecLinked"        => node.Status == AlignmentStatus.Linked,
            "TechnicalOnly"     => node.Status == AlignmentStatus.TechnicalOnly,
            "NeedsReview"       => node.Status == AlignmentStatus.NeedsReview,
            "PossibleDeviation" => node.Status == AlignmentStatus.PossibleDeviation,
            "HighRisk"          => node.Risk == AlignmentRisk.High,
            "RegressionCandidate" => node.IsRegressionCandidate,
            "HasTableLinks"     => node.NodeType == TaskNodeType.Task && node.TaskId is not null
                                   && (tableLinkedIds?.Contains(node.TaskId) ?? false),
            "HasScLinks"        => node.ReferencedScIds.Count > 0 ||
                                   (node.NodeType == TaskNodeType.Task && node.SpecMatches
                                       .Any(m => m.MatchType == SpecMatchType.SuccessCriterion)),
            "HasFrLinks"        => node.ReferencedFrIds.Count > 0 ||
                                   (node.NodeType == TaskNodeType.Task && node.SpecMatches
                                       .Any(m => m.MatchType == SpecMatchType.Requirement)),
            _ => true,
        };
    }

    private static string StripMarkdown(string s) =>
        Regex.Replace(s, @"[*_`#\[\]]", "").Trim();
}
