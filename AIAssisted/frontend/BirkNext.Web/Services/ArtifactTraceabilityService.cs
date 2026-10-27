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
        ReviewContext reviewContext) => Analyze(constitution, spec, plan, tasks, reviewContext, TraceabilityPolicy.None);

    /// <summary>
    /// Authored-document reference evidence only. Absence of an explicit reference is informational unless <paramref name="policy"/> requires the
    /// relation; Source Analysis evidence is never used to fill an authored link, and no Plan→Task edge is inferred.
    /// </summary>
    public ArtifactTraceabilityReport Analyze(
        ConstitutionDocument? constitution,
        SpecTree? spec,
        PlanDocument? plan,
        TaskTree? tasks,
        ReviewContext reviewContext,
        TraceabilityPolicy policy)
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
        var gaps = BuildGaps(constToSpec, specToPlan, planToTask, orphanTasks, policy)
            .Concat(spec is not null && tasks is not null ? BuildReferenceIssues(allTaskNodes, reviewContext, spec) : [])
            .GroupBy(g => (g.GapIn, g.ItemId, g.Status, TraceabilityLabels.CategoryOf(g), g.Description))
            .Select(group => group.First())
            .OrderBy(g => g.Severity)
            .ToList();

        // Full end-to-end matrix
        var matrix = BuildMatrix(constitution, constToSpec, specToPlan, planToTask);

        var health = BuildHealth(
            constitution, spec, plan, tasks,
            reviewContext,
            constCoverage, specCoverage, planCoverage, taskCoverage,
            gaps.Count, allTaskNodes.Count, orphanTasks.Count, policy);

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
            Policy                = policy,
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
        ruleToReqs = new Dictionary<string, List<string>>(ruleToReqs.GroupBy(x => ConstitutionRuleIds.Normalize(x.Key))
            .ToDictionary(g => g.Key, g => g.SelectMany(x => x.Value).Distinct().ToList()), ConstitutionRuleIds.Comparer);

        // Rules without a citable ID cannot be referenced by ID: they are not counted as covered or missing (no deterministic mapping).
        foreach (var rule in constitution.RuleCatalog.Where(r => !r.IsReferenceOnly && !ConstitutionRuleIds.HasNoCitableId(r)))
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
            foreach (var planId in planIds.Where(id => !PlanTraceabilityNodes.IsStructureListing(id)))
            {
                // A decision, or a plan location ("§ Heading") where the requirement id is written explicitly.
                var planItem = context.Plan.ArchitectureDecisions
                    .FirstOrDefault(d => d.Id == planId);
                // A supporting plan document of the pack ("research.md § Testing Stack") names its document.
                var title = planItem?.Title ?? (planId.StartsWith("§ ", StringComparison.Ordinal) ? planId[2..]
                    : planId.Contains(".md", StringComparison.OrdinalIgnoreCase) ? planId : null);

                if (title is not null)
                {
                    links.Add(new TraceabilityLink
                    {
                        SourceId    = requirement.Id,
                        SourceType  = ArtifactType.Specification,
                        TargetId    = planId,
                        TargetType  = ArtifactType.Plan,
                        SourceTitle = requirement.Text,
                        TargetTitle = title,
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
        List<TaskNode> orphanTasks,
        TraceabilityPolicy policy)
    {
        var gaps = new List<TraceabilityGap>();

        // No explicit reference is evidence, not a defect: Informational unless a configured policy requires the relation.
        TraceabilityObservationCategory Category(bool required) => required ? TraceabilityObservationCategory.PolicyRequiredTraceNotEstablished : TraceabilityObservationCategory.NoExplicitReference;
        string? RequiredBy(bool required, string relation) => required ? $"Project traceability policy: explicit {relation} references required" : null;

        foreach (var c in constToSpec.Where(c => c.Status == TraceabilityStatus.Missing))
            gaps.Add(new TraceabilityGap
            {
                GapIn       = ArtifactType.Constitution,
                ItemId      = c.ItemId,
                ItemTitle   = c.ItemTitle,
                Status      = TraceabilityStatus.Missing,
                Description = "No explicit reference to this Constitution rule was found in the selected Specification. This does not establish that the principle is absent or inapplicable.",
                Severity    = policy.RequireConstitutionToSpec ? GapSeverity.High : GapSeverity.Informational,
                PolicyRequired = policy.RequireConstitutionToSpec,
                Category    = Category(policy.RequireConstitutionToSpec),
                RequiredBy  = RequiredBy(policy.RequireConstitutionToSpec, "Constitution→Specification"),
            });

        // Partial constitution coverage (only plan/non-req) → Medium
        foreach (var c in constToSpec.Where(c => c.Status == TraceabilityStatus.Partial))
            gaps.Add(new TraceabilityGap
            {
                GapIn       = ArtifactType.Constitution,
                ItemId      = c.ItemId,
                ItemTitle   = c.ItemTitle,
                Status      = TraceabilityStatus.Partial,
                Description = $"{c.ItemId} is partially linked — not referenced by a formal requirement.",
                Severity    = policy.RequireConstitutionToSpec ? GapSeverity.Medium : GapSeverity.Informational,
                PolicyRequired = policy.RequireConstitutionToSpec,
                Category    = Category(policy.RequireConstitutionToSpec),
                RequiredBy  = RequiredBy(policy.RequireConstitutionToSpec, "Constitution→Specification"),
            });

        // Missing spec requirements in plan → High
        foreach (var c in specToPlan.Where(c => c.Status == TraceabilityStatus.Missing))
            gaps.Add(new TraceabilityGap
            {
                GapIn       = ArtifactType.Specification,
                ItemId      = c.ItemId,
                ItemTitle   = c.ItemTitle,
                Status      = TraceabilityStatus.Missing,
                Description = $"No explicit Plan reference to {c.ItemId} was found. A Plan may still address this requirement without using its explicit ID.",
                Severity    = policy.RequireSpecToPlan ? GapSeverity.High : GapSeverity.Informational,
                PolicyRequired = policy.RequireSpecToPlan,
                Category    = Category(policy.RequireSpecToPlan),
                RequiredBy  = RequiredBy(policy.RequireSpecToPlan, "Specification→Plan"),
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
                Description = $"Task {t.TaskId ?? t.Title} has no explicit Requirement/Success Criterion reference. Use Task Alignment Review to determine whether a direct requirement link is expected.",
                Severity    = policy.RequireTaskReferences ? GapSeverity.Medium : GapSeverity.Informational,
                PolicyRequired = policy.RequireTaskReferences,
                Category    = Category(policy.RequireTaskReferences),
                RequiredBy  = RequiredBy(policy.RequireTaskReferences, "Task→Requirement/SC"),
            });

        return gaps;
    }

    /// <summary>
    /// Explicit Task references that do not resolve deterministically: an id found nowhere in the selected Specification is a broken
    /// reference; an id defined more than once is ambiguous and no candidate is chosen. Task reference counts are unchanged.
    /// </summary>
    private static List<TraceabilityGap> BuildReferenceIssues(List<TaskNode> tasks, ReviewContext context, SpecTree spec)
    {
        var definitions = context.Specification.Requirements.Select(r => r.Id)
            .Concat(context.Specification.SuccessCriteria.Select(c => c.Id))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        if (definitions.Count == 0) return [];
        // An id written anywhere in the Specification (also outside parsed requirement lists) is never reported as broken.
        var specText = string.Join("\n", FlattenSpec(spec.Roots).Select(n => $"{n.SpecItemId} {n.Title} {n.FullContent ?? n.Excerpt}"));

        var issues = new List<TraceabilityGap>();
        foreach (var task in tasks)
        {
            var id = task.TaskId ?? task.Title;
            foreach (var reference in task.ReferencedFrIds.Concat(task.ReferencedScIds).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (definitions.TryGetValue(reference, out var count))
                {
                    if (count > 1)
                        issues.Add(new TraceabilityGap
                        {
                            GapIn = ArtifactType.Task, ItemId = id, ItemTitle = task.ShortTitle ?? task.Title, Status = TraceabilityStatus.Covered,
                            Description = $"{reference} is defined {count} times in the selected Specification; the reference is not resolved to one candidate automatically.",
                            Severity = GapSeverity.Medium, Category = TraceabilityObservationCategory.AmbiguousReference,
                        });
                }
                else if (!Regex.IsMatch(specText, $@"(?<![A-Za-z0-9-]){Regex.Escape(reference)}(?![0-9])", RegexOptions.IgnoreCase))
                {
                    issues.Add(new TraceabilityGap
                    {
                        GapIn = ArtifactType.Task, ItemId = id, ItemTitle = task.ShortTitle ?? task.Title, Status = TraceabilityStatus.Covered,
                        Description = $"Task {id} explicitly references {reference}, which does not exist in the selected Specification.",
                        Severity = GapSeverity.High, Category = TraceabilityObservationCategory.BrokenReference,
                    });
                }
            }
        }
        return issues;
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
                // No explicit Specification reference: one row, edge evidence only (never "missing coverage").
                rows.Add(new TraceabilityMatrixRow
                {
                    ConstitutionRuleId    = ruleEntry.ItemId,
                    ConstitutionRuleTitle = ruleEntry.ItemTitle,
                    Status                = TraceabilityStatus.Missing,
                    SpecEdge              = "No explicit reference",
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
                    // Explicit Constitution→Specification link; no explicit Plan reference to the requirement.
                    rows.Add(new TraceabilityMatrixRow
                    {
                        ConstitutionRuleId    = ruleEntry.ItemId,
                        ConstitutionRuleTitle = ruleEntry.ItemTitle,
                        SpecRequirementId     = specId,
                        SpecRequirementTitle  = specTitle,
                        Status                = TraceabilityStatus.Covered,
                        SpecEdge              = "Explicit",
                        PlanEdge              = "No explicit reference",
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
                        // Plan→Task is not assessed (no stable shared identifier); it never turns the row partial.
                        rows.Add(new TraceabilityMatrixRow
                        {
                            ConstitutionRuleId    = ruleEntry.ItemId,
                            ConstitutionRuleTitle = ruleEntry.ItemTitle,
                            SpecRequirementId     = specId,
                            SpecRequirementTitle  = specTitle,
                            PlanItemId            = planId,
                            PlanItemTitle         = planTitle,
                            PlanItemKind          = PlanTraceabilityNodes.KindOf(planId),
                            Status                = TraceabilityStatus.Covered,
                            SpecEdge              = "Explicit",
                            PlanEdge              = "Explicit",
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
                            PlanItemKind          = PlanTraceabilityNodes.KindOf(planId),
                            Status                = TraceabilityStatus.Covered,
                            SpecEdge              = "Explicit",
                            PlanEdge              = "Explicit",
                            TaskEdge              = "Explicit",
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
        int orphanTaskCount,
        TraceabilityPolicy policy)
    {
        var indicators = new List<TraceabilityHealthIndicator>();
        // Evidence is neutral; only a configured policy may turn low explicit linkage into attention.
        TraceabilityHealthLevel Level(bool required, double pct) =>
            !required ? TraceabilityHealthLevel.Info : pct >= 80 ? TraceabilityHealthLevel.Good : pct >= 50 ? TraceabilityHealthLevel.Warning : TraceabilityHealthLevel.Error;

        // Constitution coverage
        if (constitution is not null && spec is not null && constCov.HasApplicablePopulation)
        {
            var pct = constCov.CoveragePercentage;
            indicators.Add(new TraceabilityHealthIndicator
            {
                Icon  = "ℹ",
                Message = $"Constitution → Specification: {constCov.CoveredItems} of {constCov.TotalItems} Constitution rules explicitly linked ({pct:0.#}% explicit reference coverage)",
                Level = Level(policy.RequireConstitutionToSpec, pct),
            });
        }

        // Spec coverage
        if (spec is not null && plan is not null && specCov.HasApplicablePopulation)
        {
            var pct = specCov.CoveragePercentage;
            indicators.Add(new TraceabilityHealthIndicator
            {
                Icon  = "ℹ",
                Message = $"Specification → Plan: {specCov.CoveredItems} of {specCov.TotalItems} requirements explicitly referenced in the Plan ({pct:0.#}% explicit reference coverage)",
                Level = Level(policy.RequireSpecToPlan, pct),
            });
        }

        // Orphan tasks
        if (tasks is not null && spec is not null && orphanTaskCount > 0)
        {
            indicators.Add(new TraceabilityHealthIndicator
            {
                Icon  = "ℹ",
                Message = $"{orphanTaskCount} task{(orphanTaskCount != 1 ? "s" : "")} without an explicit Requirement/SC reference — Task Alignment Review classifies them (technical, design-linked or needs review)",
                Level = policy.RequireTaskReferences ? TraceabilityHealthLevel.Warning : TraceabilityHealthLevel.Info,
            });
        }

        // Gap count
        if (gapCount > 0)
        {
            indicators.Add(new TraceabilityHealthIndicator
            {
                Icon  = "ℹ",
                Message = $"{gapCount} reference observation{(gapCount != 1 ? "s" : "")} — {(policy.Configured ? "see the state of each observation" : "informational; no explicit traceability policy configured")}",
                Level = policy.Configured ? TraceabilityHealthLevel.Warning : TraceabilityHealthLevel.Info,
            });
        }
        else if (constitution is not null && spec is not null && plan is not null && tasks is not null &&
                 constCov.HasApplicablePopulation && specCov.HasApplicablePopulation && planCov.HasApplicablePopulation)
        {
            indicators.Add(new TraceabilityHealthIndicator
            {
                Icon  = "✓",
                Message = "Every assessed item has an explicit authored reference",
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

/// <summary>
/// Plan nodes for authored traceability. A Plan location is typed from its own heading; project-structure listings (the Plan's
/// "Source Code (repository root)", "Project Structure" or documentation file trees) describe repository layout, not planning statements,
/// and never become traceability nodes. Source Analysis evidence is never a Plan node.
/// </summary>
public static class PlanTraceabilityNodes
{
    private static readonly Regex StructureListing = new(
        @"^§\s*(source code|project structure|repository (root|layout|structure)|directory structure|folder structure|documentation \(this feature\)|file structure)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Phase = new(@"^§\s*(phase|step|stage)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsStructureListing(string planId) => StructureListing.IsMatch(planId.Trim());

    public static string KindOf(string planId) =>
        planId.StartsWith("§ ", StringComparison.Ordinal) ? Phase.IsMatch(planId) ? "Plan phase" : "Plan section"
        : planId.Contains(".md", StringComparison.OrdinalIgnoreCase) ? "Supporting plan document"
        : "Architecture decision";
}
