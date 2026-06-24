using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed class ArtifactTraceabilityService : IArtifactTraceabilityService
{
    // ── Regex ─────────────────────────────────────────────────────────────────

    private static readonly Regex RuleIdRe = new(
        @"\b(PP-\d+|PS-\d+|GL-\d+|AC-\d+|FC-\d+|GV-\d+|MC-\d+|FP-\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FrIdRe = new(
        @"\b(FR-\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Public API ────────────────────────────────────────────────────────────

    public ArtifactTraceabilityReport Analyze(
        ConstitutionDocument? constitution,
        SpecTree? spec,
        PlanDocument? plan,
        TaskTree? tasks)
    {
        // Flatten all nodes
        var allSpecNodes  = spec  is not null ? FlattenSpec(spec.Roots)   : [];
        var allTaskNodes  = tasks is not null ? FlattenTasks(tasks.Roots)  : [];

        // Build lookup caches
        var specNodesByFrId = BuildSpecNodesByFrId(allSpecNodes);
        var taskNodesByFrId = BuildTaskNodesByFrId(allTaskNodes);
        var planRuleIds     = BuildPlanRuleIds(plan);
        var planTextFrIds   = BuildPlanTextFrIds(plan);

        // Plan items list (reused in multiple chain steps)
        var planItems = BuildPlanItems(plan);

        // Per-chain analysis
        var constToSpec = BuildConstitutionToSpec(constitution, allSpecNodes, planRuleIds);
        var specToPlan  = BuildSpecToPlan(specNodesByFrId, planTextFrIds, planItems);
        var planToTask  = BuildPlanToTask(plan, taskNodesByFrId);

        var orphanTasks = FindOrphanTasks(allTaskNodes);

        // Coverage stats
        var constCoverage = ComputeStats(constToSpec);
        var specCoverage  = ComputeStats(specToPlan);
        var planCoverage  = ComputeStats(planToTask);
        var taskCoverage  = ComputeTaskCoverage(allTaskNodes, orphanTasks);

        // Gaps (sorted by severity desc)
        var gaps = BuildGaps(constToSpec, specToPlan, planToTask, orphanTasks)
            .OrderBy(g => g.Severity)
            .ToList();

        // Full end-to-end matrix
        var matrix = BuildMatrix(constitution, constToSpec, specToPlan, planToTask);

        var health = BuildHealth(
            constitution, spec, plan, tasks,
            constCoverage, specCoverage, planCoverage, taskCoverage,
            gaps.Count, allTaskNodes.Count, orphanTasks.Count);

        return new ArtifactTraceabilityReport
        {
            ConstitutionCoverage  = constCoverage,
            SpecificationCoverage = specCoverage,
            PlanCoverage          = planCoverage,
            TaskCoverage          = taskCoverage,
            ConstitutionToSpec    = constToSpec,
            SpecToPlan            = specToPlan,
            PlanToTask            = planToTask,
            Gaps                  = gaps,
            Matrix                = matrix,
            Health                = health,
            HasConstitution       = constitution is not null,
            HasSpecification      = spec is not null,
            HasPlan               = plan is not null,
            HasTasks              = tasks is not null,
        };
    }

    // ── Search / filter ───────────────────────────────────────────────────────

    public IEnumerable<TraceabilityMatrixRow> SearchMatrix(
        IEnumerable<TraceabilityMatrixRow> rows, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return rows;
        var ci = StringComparison.OrdinalIgnoreCase;
        return rows.Where(r =>
            r.ConstitutionRuleId.Contains(query, ci)   ||
            r.ConstitutionRuleTitle.Contains(query, ci)||
            (r.SpecRequirementId?.Contains(query, ci) ?? false) ||
            (r.SpecRequirementTitle?.Contains(query, ci) ?? false) ||
            (r.PlanItemId?.Contains(query, ci) ?? false) ||
            (r.PlanItemTitle?.Contains(query, ci) ?? false) ||
            (r.TaskId?.Contains(query, ci) ?? false) ||
            (r.TaskTitle?.Contains(query, ci) ?? false) ||
            r.Status.ToString().Contains(query, ci));
    }

    public IEnumerable<TraceabilityMatrixRow> FilterMatrixByStatus(
        IEnumerable<TraceabilityMatrixRow> rows, TraceabilityStatus? status)
    {
        if (status is null) return rows;
        return rows.Where(r => r.Status == status);
    }

    public IEnumerable<TraceabilityGap> SearchGaps(
        IEnumerable<TraceabilityGap> gaps, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return gaps;
        var ci = StringComparison.OrdinalIgnoreCase;
        return gaps.Where(g =>
            g.ItemId.Contains(query, ci) ||
            g.ItemTitle.Contains(query, ci) ||
            g.Description.Contains(query, ci) ||
            g.GapIn.ToString().Contains(query, ci));
    }

    public IEnumerable<TraceabilityGap> FilterGapsByArtifact(
        IEnumerable<TraceabilityGap> gaps, ArtifactType? type)
    {
        if (type is null) return gaps;
        return gaps.Where(g => g.GapIn == type);
    }

    public IEnumerable<TraceabilityGap> FilterGapsBySeverity(
        IEnumerable<TraceabilityGap> gaps, GapSeverity? severity)
    {
        if (severity is null) return gaps;
        return gaps.Where(g => g.Severity == severity);
    }

    public IEnumerable<ChainCoverage> SearchChain(
        IEnumerable<ChainCoverage> chain, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return chain;
        var ci = StringComparison.OrdinalIgnoreCase;
        return chain.Where(c =>
            c.ItemId.Contains(query, ci) ||
            c.ItemTitle.Contains(query, ci) ||
            (c.ItemSubType?.Contains(query, ci) ?? false) ||
            c.Status.ToString().Contains(query, ci) ||
            c.Links.Any(l =>
                l.TargetId.Contains(query, ci) ||
                (l.TargetTitle?.Contains(query, ci) ?? false)));
    }

    public IEnumerable<ChainCoverage> FilterChainByStatus(
        IEnumerable<ChainCoverage> chain, TraceabilityStatus? status)
    {
        if (status is null) return chain;
        return chain.Where(c => c.Status == status);
    }

    // ── Node flattening ───────────────────────────────────────────────────────

    private static List<SpecNode> FlattenSpec(IEnumerable<SpecNode> nodes)
    {
        var result = new List<SpecNode>();
        void Visit(SpecNode n) { result.Add(n); foreach (var c in n.Children) Visit(c); }
        foreach (var n in nodes) Visit(n);
        return result;
    }

    private static List<TaskNode> FlattenTasks(IEnumerable<TaskNode> nodes)
    {
        var result = new List<TaskNode>();
        void Visit(TaskNode n)
        {
            if (n.NodeType == TaskNodeType.Task) result.Add(n);
            foreach (var c in n.Children) Visit(c);
        }
        foreach (var n in nodes) Visit(n);
        return result;
    }

    // ── Lookup map builders ───────────────────────────────────────────────────

    private static Dictionary<string, List<SpecNode>> BuildSpecNodesByFrId(List<SpecNode> nodes)
    {
        var map = new Dictionary<string, List<SpecNode>>(StringComparer.OrdinalIgnoreCase);

        void Add(string id, SpecNode node)
        {
            if (!map.TryGetValue(id, out var list))
                map[id] = list = [];
            if (!list.Contains(node)) list.Add(node);
        }

        foreach (var node in nodes)
        {
            // Primary: explicit SpecItemId set by the parser
            if (!string.IsNullOrEmpty(node.SpecItemId) && FrIdRe.IsMatch(node.SpecItemId))
                Add(node.SpecItemId.ToUpperInvariant(), node);

            // Fallback: extract FR-### from the heading title (covers parsers that don't set SpecItemId)
            foreach (Match m in FrIdRe.Matches(node.Title))
                Add(m.Groups[1].Value.ToUpperInvariant(), node);
        }
        return map;
    }

    private static Dictionary<string, List<TaskNode>> BuildTaskNodesByFrId(List<TaskNode> tasks)
    {
        var map = new Dictionary<string, List<TaskNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            foreach (var frId in task.ReferencedFrIds)
            {
                if (!map.TryGetValue(frId, out var list))
                    map[frId] = list = [];
                list.Add(task);
            }
        }
        return map;
    }

    // All rule IDs directly mentioned in plan gates and check items
    private static HashSet<string> BuildPlanRuleIds(PlanDocument? plan)
    {
        if (plan is null) return [];
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in plan.Gates)
            if (!string.IsNullOrEmpty(g.RuleId)) set.Add(g.RuleId.ToUpperInvariant());
        foreach (var c in plan.ConstitutionCheckItems)
            if (!string.IsNullOrEmpty(c.RuleId)) set.Add(c.RuleId.ToUpperInvariant());
        return set;
    }

    // All FR-### IDs found anywhere in the plan text
    private static HashSet<string> BuildPlanTextFrIds(PlanDocument? plan)
    {
        if (plan is null) return [];
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddFromText(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match m in FrIdRe.Matches(text))
                set.Add(m.Groups[1].Value.ToUpperInvariant());
        }
        foreach (var s in plan.Sections) AddFromText(s.RawContent);
        foreach (var d in plan.ArchitectureDecisions)
        {
            AddFromText(d.Context);
            AddFromText(d.Decision);
            AddFromText(d.Rationale);
            AddFromText(d.RawText);
        }
        foreach (var p in plan.Phases)
        {
            AddFromText(p.Title);
            AddFromText(p.Description);
            foreach (var t in p.Tasks) AddFromText(t);
        }
        foreach (var g in plan.Gates)   AddFromText(g.Gate);
        foreach (var c in plan.ConstitutionCheckItems) AddFromText(c.Title);
        return set;
    }

    // Plan items with their FR mentions (for Plan→Task analysis)
    // Each entry: (itemId, itemTitle, frIds extracted from that item)
    private static List<(string Id, string Title, List<string> FrIds)> BuildPlanItems(PlanDocument? plan)
    {
        if (plan is null) return [];
        var items = new List<(string, string, List<string>)>();

        // Architecture decisions
        foreach (var d in plan.ArchitectureDecisions)
        {
            var text = string.Join(" ", d.Context, d.Decision, d.Rationale, d.RawText);
            var frIds = ExtractFrIds(text);
            items.Add((d.Id.Length > 0 ? d.Id : d.Title, d.Title, frIds));
        }

        // Implementation phases
        foreach (var p in plan.Phases)
        {
            var text = string.Join(" ",
                p.Title,
                p.Description ?? "",
                string.Join(" ", p.Tasks));
            var frIds = ExtractFrIds(text);
            items.Add(($"Phase {p.PhaseNumber}", p.Title, frIds));
        }

        return items;
    }

    private static string? ExtractFirstFrId(string text)
    {
        var m = FrIdRe.Match(text);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static List<string> ExtractFrIds(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        foreach (Match m in FrIdRe.Matches(text))
        {
            var id = m.Groups[1].Value.ToUpperInvariant();
            if (!result.Contains(id)) result.Add(id);
        }
        return result;
    }

    private static List<TaskNode> FindOrphanTasks(List<TaskNode> tasks)
        => tasks
            .Where(t => t.ReferencedFrIds.Count == 0 && t.ReferencedScIds.Count == 0)
            .ToList();

    // ── Chain builders ────────────────────────────────────────────────────────

    private static List<ChainCoverage> BuildConstitutionToSpec(
        ConstitutionDocument? constitution,
        List<SpecNode> allSpecNodes,
        HashSet<string> planRuleIds)
    {
        if (constitution is null) return [];
        // Nothing to cross-reference against — skip analysis (matrix handles Missing rows directly)
        if (allSpecNodes.Count == 0 && planRuleIds.Count == 0) return [];

        var result = new List<ChainCoverage>();

        // Pre-build: map all rule IDs (including aliases) → spec nodes that mention them
        // by scanning FullContent + Title of each spec node
        var specMentions = BuildRuleToSpecMentions(allSpecNodes);

        foreach (var rule in constitution.RuleCatalog)
        {
            var allIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rule.RuleId };
            foreach (var alias in rule.Aliases) allIds.Add(alias);

            var links = new List<TraceabilityLink>();
            bool hasReqCoverage = false;
            bool hasOtherCoverage = false;

            foreach (var id in allIds)
            {
                if (!specMentions.TryGetValue(id.ToUpperInvariant(), out var nodes)) continue;
                foreach (var node in nodes)
                {
                    var nodeId = node.SpecItemId ?? ExtractFirstFrId(node.Title) ?? node.Title;
                    links.Add(new TraceabilityLink
                    {
                        SourceId    = rule.RuleId,
                        SourceType  = ArtifactType.Constitution,
                        TargetId    = nodeId,
                        TargetType  = ArtifactType.Specification,
                        SourceTitle = rule.Title,
                        TargetTitle = node.Title,
                    });

                    // A node is a "requirement" if its NodeType is Requirement/UserStory
                    // OR if its title starts with a FR-/US- pattern (parser may classify as SubSection)
                    bool isReqNode = node.NodeType is SpecNodeType.Requirement or SpecNodeType.UserStory
                                     || FrIdRe.IsMatch(node.Title);
                    if (isReqNode)
                        hasReqCoverage = true;
                    else
                        hasOtherCoverage = true;
                }
            }

            // Plan-level gates also provide partial evidence
            bool planCoverage = allIds.Any(id => planRuleIds.Contains(id.ToUpperInvariant()));
            if (planCoverage && !hasReqCoverage && !hasOtherCoverage)
                hasOtherCoverage = true;

            var status = hasReqCoverage
                ? TraceabilityStatus.Covered
                : (hasOtherCoverage ? TraceabilityStatus.Partial : TraceabilityStatus.Missing);

            result.Add(new ChainCoverage
            {
                ItemId      = rule.RuleId,
                ItemTitle   = rule.Title,
                ItemType    = ArtifactType.Constitution,
                ItemSubType = rule.RuleType.ToString(),
                Status      = status,
                Links       = links,
            });
        }

        return result;
    }

    // Scan all spec nodes' text (title, excerpt, full content) for rule ID mentions
    private static Dictionary<string, List<SpecNode>> BuildRuleToSpecMentions(List<SpecNode> nodes)
    {
        var map = new Dictionary<string, List<SpecNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            // Scan title + excerpt + fullContent + linked IDs
            // Excerpt is always populated; FullContent may be null in some parsers
            var text = string.Join(" ",
                node.Title ?? string.Empty,
                node.Excerpt ?? string.Empty,
                node.FullContent ?? string.Empty,
                string.Join(" ", node.LinkedSpecItemIds));

            foreach (Match m in RuleIdRe.Matches(text))
            {
                var id = m.Groups[1].Value.ToUpperInvariant();
                if (!map.TryGetValue(id, out var list))
                    map[id] = list = [];
                if (!list.Contains(node)) list.Add(node);
            }
        }
        return map;
    }

    private static List<ChainCoverage> BuildSpecToPlan(
        Dictionary<string, List<SpecNode>> specNodesByFrId,
        HashSet<string> planTextFrIds,
        List<(string Id, string Title, List<string> FrIds)> planItems)
    {
        if (specNodesByFrId.Count == 0 && planTextFrIds.Count == 0) return [];

        // Pre-build: FR-ID → plan items that mention this FR
        var frToPlanItems = new Dictionary<string, List<(string Id, string Title)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (itemId, itemTitle, itemFrIds) in planItems)
        {
            foreach (var frId in itemFrIds)
            {
                var key = frId.ToUpperInvariant();
                if (!frToPlanItems.TryGetValue(key, out var list))
                    frToPlanItems[key] = list = [];
                list.Add((itemId, itemTitle));
            }
        }

        var result = new List<ChainCoverage>();

        // Analyze all FR-### spec nodes regardless of NodeType
        foreach (var (frId, nodes) in specNodesByFrId)
        {
            // Prefer Requirement/UserStory type, fall back to any node with FR-### title
            var reqNodes = nodes
                .Where(n => n.NodeType is SpecNodeType.Requirement or SpecNodeType.UserStory
                            || FrIdRe.IsMatch(n.Title))
                .ToList();
            if (reqNodes.Count == 0) reqNodes = nodes;
            if (reqNodes.Count == 0) continue;

            var representative = reqNodes[0];
            var frIdUpper = frId.ToUpperInvariant();

            frToPlanItems.TryGetValue(frIdUpper, out var coveringPlanItems);
            bool isCovered = planTextFrIds.Contains(frIdUpper);

            var links = new List<TraceabilityLink>();
            if (coveringPlanItems is { Count: > 0 })
            {
                foreach (var (planId, planTitle) in coveringPlanItems)
                {
                    links.Add(new TraceabilityLink
                    {
                        SourceId    = frId,
                        SourceType  = ArtifactType.Specification,
                        TargetId    = planId,         // actual plan item ID
                        TargetType  = ArtifactType.Plan,
                        SourceTitle = representative.Title,
                        TargetTitle = planTitle,
                    });
                }
            }
            else if (isCovered)
            {
                // FR mentioned in plan text but not linked to a specific plan item
                links.Add(new TraceabilityLink
                {
                    SourceId    = frId,
                    SourceType  = ArtifactType.Specification,
                    TargetId    = "plan",
                    TargetType  = ArtifactType.Plan,
                    SourceTitle = representative.Title,
                    TargetTitle = "Referenced in plan",
                });
            }

            result.Add(new ChainCoverage
            {
                ItemId      = frId,
                ItemTitle   = representative.Title,
                ItemType    = ArtifactType.Specification,
                ItemSubType = representative.NodeType.ToString(),
                Status      = (links.Count > 0 || isCovered) ? TraceabilityStatus.Covered : TraceabilityStatus.Missing,
                Links       = links,
            });
        }

        return result;
    }

    private static List<ChainCoverage> BuildPlanToTask(
        PlanDocument? plan,
        Dictionary<string, List<TaskNode>> taskNodesByFrId)
    {
        if (plan is null) return [];

        var result     = new List<ChainCoverage>();
        var planItems  = BuildPlanItems(plan);

        foreach (var (itemId, itemTitle, frIds) in planItems)
        {
            var links = new List<TraceabilityLink>();

            foreach (var frId in frIds)
            {
                if (!taskNodesByFrId.TryGetValue(frId, out var matchingTasks)) continue;
                foreach (var task in matchingTasks)
                {
                    links.Add(new TraceabilityLink
                    {
                        SourceId    = itemId,
                        SourceType  = ArtifactType.Plan,
                        TargetId    = task.TaskId ?? task.Title,
                        TargetType  = ArtifactType.Task,
                        SourceTitle = itemTitle,
                        TargetTitle = task.ShortTitle ?? task.Title,
                    });
                }
            }

            var status = links.Count > 0
                ? TraceabilityStatus.Covered
                : (frIds.Count > 0 ? TraceabilityStatus.Missing : TraceabilityStatus.Partial);

            result.Add(new ChainCoverage
            {
                ItemId      = itemId,
                ItemTitle   = itemTitle,
                ItemType    = ArtifactType.Plan,
                Status      = status,
                Links       = links,
            });
        }

        return result;
    }

    // ── Coverage stats ────────────────────────────────────────────────────────

    private static TraceabilityCoverageStats ComputeStats(List<ChainCoverage> chain)
    {
        if (chain.Count == 0) return new TraceabilityCoverageStats();
        return new TraceabilityCoverageStats
        {
            TotalItems   = chain.Count,
            CoveredItems = chain.Count(c => c.Status == TraceabilityStatus.Covered),
            PartialItems = chain.Count(c => c.Status == TraceabilityStatus.Partial),
            MissingItems = chain.Count(c => c.Status == TraceabilityStatus.Missing),
            OrphanedItems = 0,
        };
    }

    private static TraceabilityCoverageStats ComputeTaskCoverage(
        List<TaskNode> allTasks,
        List<TaskNode> orphanTasks)
    {
        if (allTasks.Count == 0) return new TraceabilityCoverageStats();
        var linked    = allTasks.Count - orphanTasks.Count;
        return new TraceabilityCoverageStats
        {
            TotalItems   = allTasks.Count,
            CoveredItems = linked,
            OrphanedItems = orphanTasks.Count,
            MissingItems = 0,
            PartialItems = 0,
        };
    }

    // ── Gaps ─────────────────────────────────────────────────────────────────

    private static List<TraceabilityGap> BuildGaps(
        List<ChainCoverage> constToSpec,
        List<ChainCoverage> specToPlan,
        List<ChainCoverage> planToTask,
        List<TaskNode> orphanTasks)
    {
        var gaps = new List<TraceabilityGap>();

        // Missing constitution rules → High
        foreach (var c in constToSpec.Where(c => c.Status == TraceabilityStatus.Missing))
            gaps.Add(new TraceabilityGap
            {
                GapIn       = ArtifactType.Constitution,
                ItemId      = c.ItemId,
                ItemTitle   = c.ItemTitle,
                Status      = TraceabilityStatus.Missing,
                Description = $"Constitution rule {c.ItemId} ({c.ItemTitle}) has no coverage in the specification.",
                Severity    = GapSeverity.High,
            });

        // Partial constitution coverage (only plan/non-req) → Medium
        foreach (var c in constToSpec.Where(c => c.Status == TraceabilityStatus.Partial))
            gaps.Add(new TraceabilityGap
            {
                GapIn       = ArtifactType.Constitution,
                ItemId      = c.ItemId,
                ItemTitle   = c.ItemTitle,
                Status      = TraceabilityStatus.Partial,
                Description = $"Constitution rule {c.ItemId} is partially covered — not referenced by a formal requirement.",
                Severity    = GapSeverity.Medium,
            });

        // Missing spec requirements in plan → High
        foreach (var c in specToPlan.Where(c => c.Status == TraceabilityStatus.Missing))
            gaps.Add(new TraceabilityGap
            {
                GapIn       = ArtifactType.Specification,
                ItemId      = c.ItemId,
                ItemTitle   = c.ItemTitle,
                Status      = TraceabilityStatus.Missing,
                Description = $"Requirement {c.ItemId} ({c.ItemTitle}) has no corresponding implementation strategy in the plan.",
                Severity    = GapSeverity.High,
            });

        // Missing plan items (no task coverage) → Medium
        foreach (var c in planToTask.Where(c => c.Status == TraceabilityStatus.Missing))
            gaps.Add(new TraceabilityGap
            {
                GapIn       = ArtifactType.Plan,
                ItemId      = c.ItemId,
                ItemTitle   = c.ItemTitle,
                Status      = TraceabilityStatus.Missing,
                Description = $"Plan item '{c.ItemTitle}' has no tasks in the task list.",
                Severity    = GapSeverity.Medium,
            });

        // Orphan tasks → Medium
        foreach (var t in orphanTasks)
            gaps.Add(new TraceabilityGap
            {
                GapIn       = ArtifactType.Task,
                ItemId      = t.TaskId ?? t.Title,
                ItemTitle   = t.ShortTitle ?? t.Title,
                Status      = TraceabilityStatus.Orphaned,
                Description = "Task has no references to any specification requirement or success criterion.",
                Severity    = GapSeverity.Medium,
            });

        return gaps;
    }

    // ── Matrix ────────────────────────────────────────────────────────────────

    private static List<TraceabilityMatrixRow> BuildMatrix(
        ConstitutionDocument? constitution,
        List<ChainCoverage> constToSpec,
        List<ChainCoverage> specToPlan,
        List<ChainCoverage> planToTask)
    {
        if (constitution is null) return [];

        // When no spec is loaded, emit a Missing row per rule from the catalog directly
        if (constToSpec.Count == 0)
        {
            return constitution.RuleCatalog.Select(rule => new TraceabilityMatrixRow
            {
                ConstitutionRuleId    = rule.RuleId,
                ConstitutionRuleTitle = rule.Title,
                Status                = TraceabilityStatus.Missing,
            }).ToList();
        }

        var rows = new List<TraceabilityMatrixRow>();

        // Build reverse lookups for plan item → tasks
        var planItemToTaskLinks = new Dictionary<string, List<TraceabilityLink>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in planToTask)
            planItemToTaskLinks[p.ItemId] = p.Links;

        // Build reverse lookup for spec req → plan items
        var specToFrLinks = BuildSpecFrToPlanLinks(specToPlan, planToTask);

        foreach (var ruleEntry in constToSpec)
        {
            if (ruleEntry.Links.Count == 0)
            {
                // No spec coverage — emit a single Missing row
                rows.Add(new TraceabilityMatrixRow
                {
                    ConstitutionRuleId    = ruleEntry.ItemId,
                    ConstitutionRuleTitle = ruleEntry.ItemTitle,
                    Status                = TraceabilityStatus.Missing,
                });
                continue;
            }

            // Group links by target spec node (one row per spec requirement)
            var specLinks = ruleEntry.Links
                .GroupBy(l => l.TargetId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var specGroup in specLinks)
            {
                var specId    = specGroup.Key;
                var specTitle = specGroup.First().TargetTitle;

                if (!specToFrLinks.TryGetValue(specId.ToUpperInvariant(), out var planLinks)
                    || planLinks.Count == 0)
                {
                    // Spec covered but no plan reference
                    rows.Add(new TraceabilityMatrixRow
                    {
                        ConstitutionRuleId    = ruleEntry.ItemId,
                        ConstitutionRuleTitle = ruleEntry.ItemTitle,
                        SpecRequirementId     = specId,
                        SpecRequirementTitle  = specTitle,
                        Status                = TraceabilityStatus.Partial,
                    });
                    continue;
                }

                // One row per plan item
                foreach (var planLink in planLinks)
                {
                    var planId    = planLink.TargetId;
                    var planTitle = planLink.TargetTitle;

                    if (!planItemToTaskLinks.TryGetValue(planId, out var taskLinks)
                        || taskLinks.Count == 0)
                    {
                        rows.Add(new TraceabilityMatrixRow
                        {
                            ConstitutionRuleId    = ruleEntry.ItemId,
                            ConstitutionRuleTitle = ruleEntry.ItemTitle,
                            SpecRequirementId     = specId,
                            SpecRequirementTitle  = specTitle,
                            PlanItemId            = planId,
                            PlanItemTitle         = planTitle,
                            Status                = TraceabilityStatus.Partial,
                        });
                        continue;
                    }

                    // One row per task (full coverage)
                    foreach (var taskLink in taskLinks.Take(3)) // cap at 3 to avoid explosion
                    {
                        rows.Add(new TraceabilityMatrixRow
                        {
                            ConstitutionRuleId    = ruleEntry.ItemId,
                            ConstitutionRuleTitle = ruleEntry.ItemTitle,
                            SpecRequirementId     = specId,
                            SpecRequirementTitle  = specTitle,
                            PlanItemId            = planId,
                            PlanItemTitle         = planTitle,
                            TaskId                = taskLink.TargetId,
                            TaskTitle             = taskLink.TargetTitle,
                            Status                = TraceabilityStatus.Covered,
                        });
                    }
                }
            }
        }

        return rows;
    }

    // Build: spec FR-ID (uppercase) → plan links that reference that FR
    private static Dictionary<string, List<TraceabilityLink>> BuildSpecFrToPlanLinks(
        List<ChainCoverage> specToPlan,
        List<ChainCoverage> planToTask)
    {
        var map = new Dictionary<string, List<TraceabilityLink>>(StringComparer.OrdinalIgnoreCase);

        // specToPlan links: source = FR-ID, target = plan ref
        foreach (var entry in specToPlan)
        {
            if (entry.Links.Count == 0) continue;
            var key = entry.ItemId.ToUpperInvariant();
            if (!map.TryGetValue(key, out var list))
                map[key] = list = [];
            foreach (var link in entry.Links)
                list.Add(new TraceabilityLink
                {
                    SourceId    = link.SourceId,
                    SourceType  = ArtifactType.Specification,
                    TargetId    = link.TargetId,
                    TargetType  = ArtifactType.Plan,
                    SourceTitle = link.SourceTitle,
                    TargetTitle = link.TargetTitle,
                });
        }

        return map;
    }

    // ── Health ────────────────────────────────────────────────────────────────

    private static TraceabilityHealth BuildHealth(
        ConstitutionDocument? constitution,
        SpecTree? spec,
        PlanDocument? plan,
        TaskTree? tasks,
        TraceabilityCoverageStats constCov,
        TraceabilityCoverageStats specCov,
        TraceabilityCoverageStats planCov,
        TraceabilityCoverageStats taskCov,
        int gapCount,
        int totalTaskCount,
        int orphanTaskCount)
    {
        var indicators = new List<TraceabilityHealthIndicator>();

        // Constitution coverage
        if (constitution is not null)
        {
            var pct = constCov.CoveragePercentage;
            indicators.Add(new TraceabilityHealthIndicator
            {
                Icon  = pct >= 80 ? "✓" : pct >= 50 ? "⚠" : "✗",
                Message = $"Constitution coverage: {pct:0.#}% ({constCov.CoveredItems} of {constCov.TotalItems} rules covered in spec)",
                Level = pct >= 80 ? TraceabilityHealthLevel.Good
                      : pct >= 50 ? TraceabilityHealthLevel.Warning
                      : TraceabilityHealthLevel.Error,
            });
        }

        // Spec coverage
        if (spec is not null && plan is not null)
        {
            var pct = specCov.CoveragePercentage;
            indicators.Add(new TraceabilityHealthIndicator
            {
                Icon  = pct >= 80 ? "✓" : pct >= 50 ? "⚠" : "✗",
                Message = $"Specification coverage: {pct:0.#}% ({specCov.CoveredItems} of {specCov.TotalItems} requirements addressed in plan)",
                Level = pct >= 80 ? TraceabilityHealthLevel.Good
                      : pct >= 50 ? TraceabilityHealthLevel.Warning
                      : TraceabilityHealthLevel.Error,
            });
        }

        // Orphan tasks
        if (tasks is not null && orphanTaskCount > 0)
        {
            indicators.Add(new TraceabilityHealthIndicator
            {
                Icon  = "⚠",
                Message = $"{orphanTaskCount} orphan task{(orphanTaskCount != 1 ? "s" : "")} — not linked to any specification requirement",
                Level = TraceabilityHealthLevel.Warning,
            });
        }

        // Gap count
        if (gapCount > 0)
        {
            indicators.Add(new TraceabilityHealthIndicator
            {
                Icon  = gapCount > 10 ? "✗" : "⚠",
                Message = $"{gapCount} traceability gap{(gapCount != 1 ? "s" : "")} detected",
                Level = gapCount > 10 ? TraceabilityHealthLevel.Error : TraceabilityHealthLevel.Warning,
            });
        }
        else if (constitution is not null && spec is not null && plan is not null && tasks is not null)
        {
            indicators.Add(new TraceabilityHealthIndicator
            {
                Icon  = "✓",
                Message = "Full chain traced — no gaps detected",
                Level = TraceabilityHealthLevel.Good,
            });
        }

        // Determine aggregate coverage %
        var totalItems = constCov.TotalItems + specCov.TotalItems + planCov.TotalItems;
        var totalCovered = constCov.CoveredItems + specCov.CoveredItems + planCov.CoveredItems;
        var aggPct = totalItems > 0 ? Math.Round((double)totalCovered / totalItems * 100, 1) : 0;

        var totalPlan = plan is not null
            ? plan.ArchitectureDecisions.Count + plan.Phases.Count
            : 0;

        return new TraceabilityHealth
        {
            TotalRules        = constitution?.RuleCatalog.Count ?? 0,
            TotalRequirements = specCov.TotalItems,
            TotalPlanItems    = totalPlan,
            TotalTasks        = totalTaskCount,
            CoveredCount      = constCov.CoveredItems + specCov.CoveredItems + planCov.CoveredItems,
            PartialCount      = constCov.PartialItems  + specCov.PartialItems  + planCov.PartialItems,
            MissingCount      = constCov.MissingItems  + specCov.MissingItems  + planCov.MissingItems,
            OrphanCount       = orphanTaskCount,
            CoveragePercentage = aggPct,
            GapCount          = gapCount,
            Indicators        = indicators,
        };
    }
}
