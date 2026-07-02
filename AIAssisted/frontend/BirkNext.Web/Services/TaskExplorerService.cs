using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class TaskExplorerService
{
    // ── Regex patterns ────────────────────────────────────────────────────

    private static readonly Regex CheckboxTaskRe = new(
        @"^\s*[-*]\s+\[([xX ])\]\s+(.+)$", RegexOptions.Compiled);

    private static readonly Regex BareTaskRe = new(
        @"^\s*[-*]?\s*T(\d{2,4}[a-zA-Z]*)\b\s*[-–.]?\s*(.*)$", RegexOptions.Compiled);

    private static readonly Regex TaskIdRe = new(
        @"\bT(\d{2,4}[a-zA-Z]*)\b", RegexOptions.Compiled);

    private static readonly Regex TaskRangeRe = new(
        @"\bT(\d{2,4})[–\-]T(\d{2,4})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ParallelRe = new(
        @"\[P\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UserStoryTagRe = new(
        @"\[US(\d+(?:[–\-]\d+)?)\]|\[Story\???\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FrRefRe = new(
        @"\b(FR)-?(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ScRefRe = new(
        @"\b(SC)-?(\d{1,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // File path pattern for extracting class names and related files
    private static readonly Regex FilePathRe = new(
        @"(?:src|test[s]?)/[\w/.-]+\.(?:cs|json|md|yaml|yml|csproj|sln)\b",
        RegexOptions.Compiled);

    private static readonly Regex TestKeywordRe = new(
        @"\b(?:test|spec|assert|xunit|nunit|mstest|shouldly|testcontainer|bunit|webapplicationfactory|test[\s-]?(?:case|task)|integration[\s-]?test|unit[\s-]?test)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SecurityKeywordRe = new(
        @"\b(?:security|authoris[ae]|kode[\s-]?[67]|permission|access[\s-]?control|gradert|sikkerhet|auth(?:orization|entication|ority))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CriticalNoteRe = new(
        @"⚠️\s*CRITICAL|CRITICAL.*(?:phase|blocking|prerequisite)|No user story work can begin",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FrontendOnlyKeywordRe = new(
        @"\b(?:frontend[\s-]?only|blazor\s+wasm|spa|webassembly|client[\s-]?side)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WorkerServiceKeywordRe = new(
        @"\b(?:worker[\s-]?(?:service|role)|background|hosted[\s-]?service|queue|consumer)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProxyKeywordRe = new(
        @"\b(?:proxy|gateway|router|load[\s-]?balanc)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NoSqlKeywordRe = new(
        @"\b(?:no[\s-]?sql|no[\s-]?database|blob[\s-]?storage|stateless)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        var tokens = MarkdownTokenizer.Tokenize(markdown);
        var roots = new List<TaskNode>();
        var headingStack = new List<(int Level, TaskNode Node)>();

        var hTasks = 0; var hCompleted = 0; var hPhases = 0; var hUserStories = 0;
        var hTables = 0; var hRows = 0;
        var hCritical = 0; var hFrontend = 0; var hWorker = 0; var hProxy = 0; var hNoSql = 0; var hParallel = 0;

        var tableBuffer = new List<string>();

        void FlushTable()
        {
            if (tableBuffer.Count == 0) return;
            var tableNode = ParseTable(tableBuffer, ref hRows);
            if (tableNode is not null) { hTables++; AddToParent(roots, headingStack, tableNode); }
            tableBuffer.Clear();
        }

        foreach (var tok in tokens)
        {
            // Table rows accumulate; flush when a non-table token arrives
            if (tok.Kind is MarkdownTokenKind.TableRow or MarkdownTokenKind.TableSeparator)
            {
                tableBuffer.Add(tok.RawLine);
                continue;
            }
            FlushTable();

            // Heading
            if (tok.Kind == MarkdownTokenKind.Heading)
            {
                var level    = tok.HeadingLevel;
                var rawTitle = tok.Content;
                var title    = StripMarkdown(rawTitle);
                var nodeType = ClassifyHeading(level, rawTitle);

                if (nodeType == TaskNodeType.Phase) hPhases++;
                if (nodeType == TaskNodeType.UserStoryGroup) hUserStories++;

                var node = new TaskNode { Title = title, NodeType = nodeType, HeadingLevel = level };

                while (headingStack.Count > 0 && headingStack[^1].Level >= level)
                    headingStack.RemoveAt(headingStack.Count - 1);

                if (headingStack.Count == 0) roots.Add(node);
                else headingStack[^1].Node.Children.Add(node);

                headingStack.Add((level, node));
                continue;
            }

            // Checkbox task: - [ ] T001 ... or - [x] T002 ...
            var cm = CheckboxTaskRe.Match(tok.RawLine);
            if (cm.Success)
            {
                var completed = cm.Groups[1].Value is "x" or "X";
                var body = cm.Groups[2].Value.Trim();
                var task = BuildTaskNode(body, completed, tok.RawLine);
                if (task is not null)
                {
                    hTasks++;
                    if (completed) hCompleted++;
                    if (task.IsCritical) hCritical++;
                    if (task.IsFrontendOnly) hFrontend++;
                    if (task.IsWorkerService) hWorker++;
                    if (task.IsProxy) hProxy++;
                    if (task.IsNoSql) hNoSql++;
                    if (task.IsParallel) hParallel++;
                    InjectContext(task, headingStack);
                    AddToParent(roots, headingStack, task);
                }
                continue;
            }

            // Bare task: T001 Description (not inside a checkbox pattern)
            var bm = BareTaskRe.Match(tok.RawLine);
            if (bm.Success && headingStack.Count > 0)
            {
                var body = $"T{bm.Groups[1].Value} {bm.Groups[2].Value}".Trim();
                var task = BuildTaskNode(body, false, tok.RawLine);
                if (task is not null)
                {
                    hTasks++;
                    if (task.IsCritical) hCritical++;
                    if (task.IsFrontendOnly) hFrontend++;
                    if (task.IsWorkerService) hWorker++;
                    if (task.IsProxy) hProxy++;
                    if (task.IsNoSql) hNoSql++;
                    if (task.IsParallel) hParallel++;
                    InjectContext(task, headingStack);
                    AddToParent(roots, headingStack, task);
                }
                continue;
            }
        }

        // Flush final table buffer
        FlushTable();

        // Propagate descendant counts
        foreach (var root in roots)
            PropagateStats(root);

        // Post-processing: mark unresolved table task refs
        var taskIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectTaskIds(roots, taskIds);
        var hUnresolved = 0;
        MarkUnresolvedTableRefs(roots, taskIds, ref hUnresolved);
        var linkedTaskIds = BuildTableLinkedTaskIds(roots);

        var health = new TaskHealth
        {
            TotalTasks = hTasks,
            CompletedTasks = hCompleted,
            TotalPhases = hPhases,
            UserStoryCount = hUserStories,
            TablesDetected = hTables,
            TraceabilityRows = hRows,
            TasksLinkedFromTables = linkedTaskIds.Count,
            UnresolvedTableRefs = hUnresolved,
            CriticalTasks = hCritical,
            FrontendOnlyTasks = hFrontend,
            WorkerServiceTasks = hWorker,
            ProxyTasks = hProxy,
            NoSqlTasks = hNoSql,
            ParallelTasks = hParallel,
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

        var unresolved = all.Count(n => n.NodeType == TaskNodeType.TableTaskRef && n.IsUnresolved);
        var linkedIds = BuildTableLinkedTaskIds(tree.Roots);

        return new TaskHealth
        {
            TotalTasks = tree.Health.TotalTasks,
            CompletedTasks = tree.Health.CompletedTasks,
            TotalPhases = tree.Health.TotalPhases,
            UserStoryCount = tree.Health.UserStoryCount,
            TablesDetected = tables > 0 ? tables : tree.Health.TablesDetected,
            TraceabilityRows = rows > 0 ? rows : tree.Health.TraceabilityRows,
            TasksLinkedFromTables = linkedIds.Count > 0 ? linkedIds.Count : tree.Health.TasksLinkedFromTables,
            UnresolvedTableRefs = unresolved,
            SpecLinked = tasks.Count(t => t.Status == AlignmentStatus.Linked),
            TechnicalOnly = tasks.Count(t => t.Status == AlignmentStatus.TechnicalOnly),
            NeedsReview = tasks.Count(t => t.Status == AlignmentStatus.NeedsReview),
            PossibleDeviations = tasks.Count(t => t.Status == AlignmentStatus.PossibleDeviation),
            HighRisk = tasks.Count(t => t.Risk == AlignmentRisk.High),
            RegressionCandidates = tasks.Count(t => t.IsRegressionCandidate),
            FrLinkedTasks  = tasks.Count(t => t.ReferencedFrIds.Count > 0),
            ScLinkedTasks  = tasks.Count(t => t.ReferencedScIds.Count > 0),
            UnlinkedTasks  = tasks.Count(t => t.ReferencedFrIds.Count == 0
                                               && t.ReferencedScIds.Count == 0
                                               && t.SpecMatches.Count == 0),
            TestingTasks   = tasks.Count(t => t.IsTestingTask),
            SecurityTasks  = tasks.Count(t => t.IsSecurityTask),
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
        string? userStoryTag = null;
        if (usMatch.Success)
        {
            // Check if it matched [US\d+] or [Story]
            if (usMatch.Groups[1].Success)
                userStoryTag = $"US{usMatch.Groups[1].Value}";
            else
                userStoryTag = "Story"; // [Story] or [Story?]
        }

        var frIds = FrRefRe.Matches(body)
            .Select(m => $"FR-{m.Groups[2].Value.PadLeft(2, '0')}").Distinct().ToList();
        var scIds = ScRefRe.Matches(body)
            .Select(m => $"SC-{m.Groups[2].Value.PadLeft(2, '0')}").Distinct().ToList();

        // Find first task ID in the body
        var taskMatch = TaskIdRe.Match(body);
        string? taskId = null;
        if (taskMatch.Success)
        {
            var captured = taskMatch.Groups[1].Value;
            // Extract numeric part and optional letter suffix (e.g., "006b" → "006" + "b")
            var digitMatch = Regex.Match(captured, @"^(\d+)([a-zA-Z]*)$");
            if (digitMatch.Success)
            {
                var digits = digitMatch.Groups[1].Value.PadLeft(3, '0');
                var suffix = digitMatch.Groups[2].Value;
                taskId = $"T{digits}{suffix}";
            }
        }

        // Clean title: strip [P], [USN], FR/SC refs, then strip redundant leading task ID
        var title = ParallelRe.Replace(body, "").Trim();
        title = UserStoryTagRe.Replace(title, "").Trim();
        title = FrRefRe.Replace(title, "").Trim();
        title = ScRefRe.Replace(title, "").Trim();
        title = StripMarkdown(title);
        if (taskId != null)
            title = Regex.Replace(title, @"^T\d{2,4}\s*[-–.]?\s*", "").Trim();
        if (title.Length > 200) title = title[..200];

        var shortTitle = DeriveShortTitle(title);
        var relatedFiles = FilePathRe.Matches(rawLine).Select(m => m.Value).Distinct().ToList();

        return new TaskNode
        {
            Title = title,
            ShortTitle = shortTitle != title ? shortTitle : null,
            NodeType = TaskNodeType.Task,
            HeadingLevel = 0,
            TaskId = taskId,
            IsCompleted = completed,
            IsParallel = isParallel,
            UserStoryTag = userStoryTag,
            ReferencedFrIds = frIds,
            ReferencedScIds = scIds,
            RawText = rawLine.Trim(),
            RelatedFiles = relatedFiles,
            IsTestingTask = TestKeywordRe.IsMatch(rawLine),
            IsSecurityTask = SecurityKeywordRe.IsMatch(rawLine),
            IsCritical = CriticalNoteRe.IsMatch(rawLine),
            IsFrontendOnly = FrontendOnlyKeywordRe.IsMatch(rawLine),
            IsWorkerService = WorkerServiceKeywordRe.IsMatch(rawLine),
            IsProxy = ProxyKeywordRe.IsMatch(rawLine),
            IsNoSql = NoSqlKeywordRe.IsMatch(rawLine),
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
            TableKind = ClassifyTableKind(headers),
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
            var captured = m.Groups[1].Value;
            // Extract numeric part and optional letter suffix
            var digitMatch = Regex.Match(captured, @"^(\d+)([a-zA-Z]*)$");
            if (digitMatch.Success)
            {
                var digits = digitMatch.Groups[1].Value.PadLeft(3, '0');
                var suffix = digitMatch.Groups[2].Value;
                var id = $"T{digits}{suffix}";
                if (seen.Add(id)) result.Add(id);
            }
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

    private static void InjectContext(TaskNode task, List<(int Level, TaskNode Node)> stack)
    {
        task.PhaseTitle = stack.LastOrDefault(h => h.Level == 2).Node?.Title;
        task.UserStoryTitle = stack
            .LastOrDefault(h => h.Node.NodeType == TaskNodeType.UserStoryGroup).Node?.Title;
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
        if (node.NodeType == TaskNodeType.TableSection && node.TableKind != TaskTableType.Generic &&
            node.TableKind.ToString().Contains(q, ci)) return true;
        // Search raw text (file paths, technical terms) and phase context
        if (!string.IsNullOrEmpty(node.RawText) && node.RawText.Contains(q, ci)) return true;
        if (node.PhaseTitle?.Contains(q, ci) ?? false) return true;
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
            "Unresolved"        => node.NodeType == TaskNodeType.TableTaskRef && node.IsUnresolved,
            "TestingTasks"      => node.NodeType == TaskNodeType.Task && node.IsTestingTask,
            "SecurityTasks"     => node.NodeType == TaskNodeType.Task && node.IsSecurityTask,
            "MissingImplementation" => node.NodeType == TaskNodeType.Task
                                   && node.ReferencedFrIds.Count == 0
                                   && node.ReferencedScIds.Count == 0,
            "PartialCoverage"   => node.NodeType == TaskNodeType.Task
                                   && (node.ReferencedFrIds.Count == 0 || node.ReferencedScIds.Count == 0),
            "OnlyUserStories"   => node.NodeType == TaskNodeType.UserStoryGroup
                                   || (node.NodeType == TaskNodeType.Task && !string.IsNullOrWhiteSpace(node.UserStoryTag))
                                   || node.Title.Contains("US", StringComparison.OrdinalIgnoreCase),
            "OnlyRequirements"  => node.ReferencedFrIds.Count > 0,
            "OnlySuccessCriteria" => node.ReferencedScIds.Count > 0,
            "NoLinks"           => node.NodeType == TaskNodeType.Task
                                   && node.ReferencedFrIds.Count == 0
                                   && node.ReferencedScIds.Count == 0,
            _ => true,
        };
    }

    private static TaskTableType ClassifyTableKind(List<string> headers)
    {
        var joined = string.Join(" ", headers).ToLowerInvariant();
        if (Regex.IsMatch(joined, @"\bcriterion\b|\bsc\b|success criteria|traceability"))
            return TaskTableType.Traceability;
        if (Regex.IsMatch(joined, @"\bfr\b|\breq\b|requirement"))
            return TaskTableType.RequirementMapping;
        if (Regex.IsMatch(joined, @"depend|prerequisite|blocking"))
            return TaskTableType.DependencyTable;
        if (Regex.IsMatch(joined, @"parallel|concurrent|simultaneous"))
            return TaskTableType.ParallelExecution;
        return TaskTableType.Generic;
    }

    private static void CollectTaskIds(IEnumerable<TaskNode> nodes, HashSet<string> ids)
    {
        foreach (var node in nodes)
        {
            if (node.NodeType == TaskNodeType.Task && node.TaskId is not null)
                ids.Add(node.TaskId);
            CollectTaskIds(node.Children, ids);
        }
    }

    private static void MarkUnresolvedTableRefs(
        IEnumerable<TaskNode> nodes, HashSet<string> taskIds, ref int unresolvedCount)
    {
        foreach (var node in nodes)
        {
            if (node.NodeType == TaskNodeType.TableTaskRef && node.TaskId is not null
                && !taskIds.Contains(node.TaskId))
            {
                node.IsUnresolved = true;
                unresolvedCount++;
            }
            MarkUnresolvedTableRefs(node.Children, taskIds, ref unresolvedCount);
        }
    }

    // Derives a short, readable display title from the cleaned task body.
    // If the body contains a file path, extracts the class/filename and first clause.
    // Falls back to word-boundary truncation at ~80 chars.
    private static string DeriveShortTitle(string body)
    {
        if (body.Length <= 80) return body;

        var pathMatch = FilePathRe.Match(body);
        if (pathMatch.Success)
        {
            var pathVal = pathMatch.Value;
            var slash = pathVal.LastIndexOf('/');
            var fileName = slash >= 0 ? pathVal[(slash + 1)..] : pathVal;
            var nameNoExt = fileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^3] : fileName;

            var afterPath = body[(pathMatch.Index + pathMatch.Length)..].Trim();
            afterPath = Regex.Replace(afterPath,
                @"^[-–—]\s*|^implementing\s+\w+(?:\s*,\s*\w+)*\s*;?\s*",
                "", RegexOptions.IgnoreCase).Trim();
            var clauseEnd = afterPath.IndexOfAny(new[] { ';', '\n' });
            var clause = (clauseEnd > 0 ? afterPath[..clauseEnd] : afterPath).Trim();
            if (clause.Length > 55)
            {
                var wb = clause.LastIndexOf(' ', 52);
                clause = wb > 10 ? clause[..wb] + "…" : clause[..52] + "…";
            }
            return string.IsNullOrWhiteSpace(clause) ? nameNoExt : $"{nameNoExt}: {clause}";
        }

        var boundary = body.LastIndexOf(' ', 77);
        return boundary > 20 ? body[..boundary] + "…" : body[..77] + "…";
    }

    private static string StripMarkdown(string s) =>
        Regex.Replace(s, @"[*_`#\[\]]", "").Trim();

    // ── Build Semantic Model ───────────────────────────────────────────────

    public static TaskSemanticModel BuildSemanticModel(TaskTree taskTree)
    {
        var allTaskNodes = ExtractAllTaskNodes(taskTree.Roots);
        var phases = ExtractPhases(taskTree.Roots, allTaskNodes);
        var dependencies = ExtractDependencies(allTaskNodes);
        var parallelGroups = ExtractParallelGroups(phases, allTaskNodes);

        var userStoryToTasks = new Dictionary<string, List<string>>();
        var frToTasks = new Dictionary<string, List<string>>();
        var scToTasks = new Dictionary<string, List<string>>();
        var taskToDependencies = new Dictionary<string, List<string>>();

        foreach (var taskNode in allTaskNodes)
        {
            if (string.IsNullOrEmpty(taskNode.TaskId)) continue;

            if (!string.IsNullOrEmpty(taskNode.UserStoryTag))
            {
                if (!userStoryToTasks.ContainsKey(taskNode.UserStoryTag))
                    userStoryToTasks[taskNode.UserStoryTag] = [];
                userStoryToTasks[taskNode.UserStoryTag].Add(taskNode.TaskId);
            }

            foreach (var frId in taskNode.ReferencedFrIds)
            {
                if (!frToTasks.ContainsKey(frId))
                    frToTasks[frId] = [];
                if (!frToTasks[frId].Contains(taskNode.TaskId))
                    frToTasks[frId].Add(taskNode.TaskId);
            }

            foreach (var scId in taskNode.ReferencedScIds)
            {
                if (!scToTasks.ContainsKey(scId))
                    scToTasks[scId] = [];
                if (!scToTasks[scId].Contains(taskNode.TaskId))
                    scToTasks[scId].Add(taskNode.TaskId);
            }
        }

        var phaseProgress = new Dictionary<string, TaskPhaseProgress>();
        foreach (var phase in phases)
        {
            var phaseTasks = allTaskNodes.Where(t => t.PhaseTitle == phase.Title).ToList();
            var completedCount = phaseTasks.Count(t => t.IsCompleted);
            phaseProgress[phase.Id] = new TaskPhaseProgress
            {
                PhaseId = phase.Id,
                PhaseName = phase.Title,
                TotalTasks = phaseTasks.Count,
                CompletedTasks = completedCount,
                OpenTasks = phaseTasks.Count - completedCount,
                CompletionPercentage = phaseTasks.Count == 0 ? 0 : (completedCount * 100) / phaseTasks.Count,
                Status = completedCount == 0 ? "NotStarted" : (completedCount == phaseTasks.Count ? "Complete" : "InProgress"),
            };
        }

        var taskItems = allTaskNodes
            .Where(t => !string.IsNullOrEmpty(t.TaskId))
            .Select(t => new TaskItem
            {
                Id = t.TaskId ?? $"T-{t.Id[..3]}",
                Title = t.ShortTitle ?? t.Title,
                Description = t.RawText,
                IsCompleted = t.IsCompleted,
                IsParallel = t.IsParallel,
                IsTestingTask = t.IsTestingTask,
                IsSecurityTask = t.IsSecurityTask,
                UserStoryId = t.UserStoryTag,
                PhaseId = null,
                LinkedFRIds = t.ReferencedFrIds,
                LinkedSCIds = t.ReferencedScIds,
                RelatedFileIds = t.RelatedFiles,
            })
            .ToList();

        return new TaskSemanticModel
        {
            Title = "Tasks",
            Description = null,
            TotalTasks = allTaskNodes.Count(n => !string.IsNullOrEmpty(n.TaskId)),
            Phases = phases,
            AllTasks = taskItems,
            Dependencies = dependencies,
            ParallelGroups = parallelGroups,
            PhaseProgress = phaseProgress,
            UserStoryToTasks = userStoryToTasks,
            FRToTasks = frToTasks,
            SCToTasks = scToTasks,
            TaskToDependencies = taskToDependencies,
        };
    }

    private static List<TaskNode> ExtractAllTaskNodes(List<TaskNode> roots)
    {
        var tasks = new List<TaskNode>();
        var queue = new Queue<TaskNode>(roots);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.NodeType == TaskNodeType.Task && !string.IsNullOrEmpty(node.TaskId))
                tasks.Add(node);

            foreach (var child in node.Children)
                queue.Enqueue(child);
        }

        return tasks;
    }

    private static List<TaskPhase> ExtractPhases(List<TaskNode> roots, List<TaskNode> allTasks)
    {
        var phases = new List<TaskPhase>();
        var phaseNumber = 1;

        foreach (var node in roots)
        {
            if (node.NodeType == TaskNodeType.Phase)
            {
                var phaseTasks = ExtractPhaseTaskIds(node);
                phases.Add(new TaskPhase
                {
                    Id = node.Id,
                    Title = node.Title,
                    PhaseNumber = phaseNumber++,
                    Description = null,
                    TaskIds = phaseTasks,
                    CompletedCount = allTasks.Count(t => phaseTasks.Contains(t.TaskId ?? "") && t.IsCompleted),
                    TotalCount = phaseTasks.Count,
                });
            }
        }

        return phases;
    }

    private static List<string> ExtractPhaseTaskIds(TaskNode phaseNode)
    {
        var taskIds = new List<string>();
        var queue = new Queue<TaskNode>(phaseNode.Children);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.NodeType == TaskNodeType.Task && !string.IsNullOrEmpty(node.TaskId))
                taskIds.Add(node.TaskId);

            foreach (var child in node.Children)
                queue.Enqueue(child);
        }

        return taskIds;
    }

    private static List<TaskDependency> ExtractDependencies(List<TaskNode> allTasks)
    {
        var dependencies = new List<TaskDependency>();
        // Dependencies would be extracted from task relationships if they existed
        // For now, return empty list
        return dependencies;
    }

    private static List<TaskParallelGroup> ExtractParallelGroups(List<TaskPhase> phases, List<TaskNode> allTasks)
    {
        var groups = new List<TaskParallelGroup>();

        foreach (var phase in phases)
        {
            var parallelTasks = allTasks
                .Where(t => t.IsParallel && phase.TaskIds.Contains(t.TaskId ?? ""))
                .Select(t => t.TaskId ?? "")
                .Where(id => !string.IsNullOrEmpty(id))
                .ToList();

            if (parallelTasks.Count > 0)
            {
                groups.Add(new TaskParallelGroup
                {
                    PhaseId = phase.Id,
                    PhaseName = phase.Title,
                    ParallelTaskIds = parallelTasks,
                });
            }
        }

        return groups;
    }
}
