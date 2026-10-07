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
        TaskTree? tasks,
        ReviewContext reviewContext)
    {
        // Flatten all nodes (used for matrix and gap detection)
        var allTaskNodes = tasks is not null ? FlattenTasks(tasks.Roots) : [];

        // Build ReviewContext-based chain relationships
        var constToSpec = BuildConstitutionToSpecFromContext(reviewContext, constitution, spec);
        var specToPlan  = BuildSpecToPlanFromContext(reviewContext, spec, plan);
        // Plan phase entries contain action text, not stable Task IDs. There is
        // no direct Plan→Task relation to assess in the current document model.
        var planToTask = new List<ChainCoverage>();

        // Task → requirement/SC traceability is a separate direct relation. A task
        // asset or a derived Plan relation does not satisfy this projection.
        var orphanTasks = spec is not null && tasks is not null
            ? FindOrphanTasks(allTaskNodes)
            : [];

        // Every percentage is projected from the same rows shown in its transition tab.
        var constCoverage = ComputeCoverage(constToSpec);
        var specCoverage  = ComputeCoverage(specToPlan);
        var planCoverage  = ComputeCoverage(planToTask);
        var taskCoverage  = spec is not null && tasks is not null
            ? ComputeTaskCoverage(allTaskNodes, orphanTasks)
            : new TraceabilityCoverageStats();

        // Gaps (sorted by severity desc)
        var gaps = BuildGaps(constToSpec, specToPlan, planToTask, orphanTasks)
            .GroupBy(g => (g.GapIn, g.ItemId, g.Status))
            .Select(group => group.First())
            .OrderBy(g => g.Severity)
            .ToList();

        // Full end-to-end matrix
        var matrix = BuildMatrix(constitution, constToSpec, specToPlan, planToTask);

        var health = BuildHealth(
            constitution, spec, plan, tasks,
            reviewContext,
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
            ReferenceOnlyConstitutionEntries = constitution?.RuleCatalog.Count(r => r.IsReferenceOnly) ?? 0,
        };
    }

    // ── Semantic Model Chain Builders ─────────────────────────────────────────

    private static List<ChainCoverage> BuildConstitutionToSpecFromContext(
        ReviewContext context,
        ConstitutionDocument? constitution,
        SpecTree? spec)
    {
        if (constitution is null || spec is null) return [];

        var result = new List<ChainCoverage>();
        var specToConstLinks = context.SpecToConstitution;

        // Reverse map: constitution rule → spec requirements that link to it
        var ruleToReqs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (specId, ruleIds) in specToConstLinks)
        {
            foreach (var ruleId in ruleIds)
            {
                if (!ruleToReqs.TryGetValue(ruleId, out var list))
                    ruleToReqs[ruleId] = list = [];
                list.Add(specId);
            }
        }

        foreach (var rule in constitution.RuleCatalog.Where(r => !r.IsReferenceOnly))
        {
            var allIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rule.RuleId };
            foreach (var alias in rule.Aliases) allIds.Add(alias);

            var links = new List<TraceabilityLink>();
            bool hasReqCoverage = false;

            foreach (var id in allIds)
            {
                if (!ruleToReqs.TryGetValue(id.ToUpperInvariant(), out var reqIds)) continue;

                var reqNodes = context.Specification.Requirements
                    .Where(r => reqIds.Contains(r.Id))
                    .ToList();

                foreach (var req in reqNodes)
                {
                    links.Add(new TraceabilityLink
                    {
                        SourceId    = rule.RuleId,
                        SourceType  = ArtifactType.Constitution,
                        TargetId    = req.Id,
                        TargetType  = ArtifactType.Specification,
                        SourceTitle = rule.Title,
                        TargetTitle = req.Text,
                    });
                    hasReqCoverage = true;
                }
            }

            var status = hasReqCoverage ? TraceabilityStatus.Covered : TraceabilityStatus.Missing;

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

    private static List<ChainCoverage> BuildSpecToPlanFromContext(
        ReviewContext context,
        SpecTree? spec,
        PlanDocument? plan)
    {
        if (spec is null || plan is null) return [];
        var result = new List<ChainCoverage>();
        var specToPlanLinks = context.SpecToPlan;

        foreach (var requirement in context.Specification.Requirements)
        {
            if (!specToPlanLinks.TryGetValue(requirement.Id, out var planIds))
                planIds = [];

            var links = new List<TraceabilityLink>();
            foreach (var planId in planIds)
            {
                var planItem = context.Plan.ArchitectureDecisions
                    .FirstOrDefault(d => d.Id == planId);

                if (planItem is not null)
                {
                    links.Add(new TraceabilityLink
                    {
                        SourceId    = requirement.Id,
                        SourceType  = ArtifactType.Specification,
                        TargetId    = planId,
                        TargetType  = ArtifactType.Plan,
                        SourceTitle = requirement.Text,
                        TargetTitle = planItem.Title,
                    });
                }
            }

            var status = links.Count > 0
                ? TraceabilityStatus.Covered
                : TraceabilityStatus.Missing;

            result.Add(new ChainCoverage
            {
                ItemId      = requirement.Id,
                ItemTitle   = requirement.Text,
                ItemType    = ArtifactType.Specification,
                Status      = status,
                Links       = links,
            });
        }

        return result;
    }

    private static TraceabilityCoverageStats ComputeCoverage(IReadOnlyCollection<ChainCoverage> rows) => new()
    {
        TotalItems = rows.Count,
        CoveredItems = rows.Count(r => r.Status == TraceabilityStatus.Covered),
        PartialItems = rows.Count(r => r.Status == TraceabilityStatus.Partial),
        MissingItems = rows.Count(r => r.Status == TraceabilityStatus.Missing),
        OrphanedItems = rows.Count(r => r.Status == TraceabilityStatus.Orphaned),
    };

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


    private static List<TaskNode> FindOrphanTasks(List<TaskNode> tasks)
        => tasks.Where(t => t.ReferencedFrIds.Count == 0 && t.ReferencedScIds.Count == 0).ToList();

    // ── Coverage stats ────────────────────────────────────────────────────────

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
                Description = "Task has no direct Requirement or Success Criterion reference.",
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

        // The matrix is an assessed chain projection, so without applicable
        // Constitution→Specification rows it must not invent Missing rows.
        if (constToSpec.Count == 0) return [];

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
        ReviewContext context,
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
        if (constitution is not null && spec is not null && constCov.HasApplicablePopulation)
        {
            var pct = constCov.CoveragePercentage;
            indicators.Add(new TraceabilityHealthIndicator
            {
                Icon  = pct >= 80 ? "✓" : pct >= 50 ? "⚠" : "✗",
                Message = $"Constitution coverage: {pct:0.#}% ({constCov.CoveredItems} of {constCov.TotalItems} authored rules linked to Specification)",
                Level = pct >= 80 ? TraceabilityHealthLevel.Good
                      : pct >= 50 ? TraceabilityHealthLevel.Warning
                      : TraceabilityHealthLevel.Error,
            });
        }

        // Spec coverage
        if (spec is not null && plan is not null && specCov.HasApplicablePopulation)
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
        if (tasks is not null && spec is not null && orphanTaskCount > 0)
        {
            indicators.Add(new TraceabilityHealthIndicator
            {
                Icon  = "⚠",
                Message = $"{orphanTaskCount} task{(orphanTaskCount != 1 ? "s" : "")} without a Requirement or Success Criterion reference",
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
        else if (constitution is not null && spec is not null && plan is not null && tasks is not null &&
                 constCov.HasApplicablePopulation && specCov.HasApplicablePopulation && planCov.HasApplicablePopulation)
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
        var totalCovered = constCov.CoveredItems + constCov.PartialItems + specCov.CoveredItems + specCov.PartialItems + planCov.CoveredItems + planCov.PartialItems;
        var aggPct = totalItems > 0 ? Math.Round((double)totalCovered / totalItems * 100, 1) : 0;

        var totalPlan = plan is not null ? context.Plan.PhaseGroups.Count : 0;

        return new TraceabilityHealth
        {
            TotalRules        = constitution?.RuleCatalog.Count(r => !r.IsReferenceOnly) ?? 0,
            TotalRequirements = context.Specification.Requirements.Count,
            TotalPlanItems    = totalPlan,
            TotalTasks        = totalTaskCount,
            CoveredCount      = constCov.CoveredItems + specCov.CoveredItems + planCov.CoveredItems,
            PartialCount      = constCov.PartialItems  + specCov.PartialItems  + planCov.PartialItems,
            MissingCount      = constCov.MissingItems  + specCov.MissingItems  + planCov.MissingItems,
            OrphanCount       = orphanTaskCount,
            CoveragePercentage = aggPct,
            CoverageDenominator = totalItems,
            GapCount          = gapCount,
            Indicators        = indicators,
        };
    }
}
