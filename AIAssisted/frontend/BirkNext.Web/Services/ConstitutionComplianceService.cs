using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed class ConstitutionComplianceService : IConstitutionComplianceService
{
    // ── Regex ─────────────────────────────────────────────────────────────────

    private static readonly Regex RuleIdRe = new(
        @"\b(PP-\d+|PS-\d+|GL-\d+|AC-\d+|FC-\d+|GV-\d+|MC-\d+|FP-\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Public API ────────────────────────────────────────────────────────────

    public ConstitutionComplianceReport Analyze(
        ConstitutionDocument? constitution,
        SpecTree?             spec,
        PlanDocument?         plan,
        TaskTree?             tasks,
        ReviewContext?        context = null)
    {
        if (constitution is null)
        {
            return new ConstitutionComplianceReport
            {
                HasConstitution  = false,
                HasSpecification = spec  is not null,
                HasPlan          = plan  is not null,
                HasTasks         = tasks is not null,
            };
        }

        if (constitution.RuleCatalog.Count == 0)
        {
            return new ConstitutionComplianceReport
            {
                HasConstitution  = true,
                HasSpecification = spec  is not null,
                HasPlan          = plan  is not null,
                HasTasks         = tasks is not null,
            };
        }

        // ── Build lookup maps ──────────────────────────────────────────────

        var specMentions  = BuildSpecMentions(spec);    // ruleId → [node titles]
        var planMentions  = BuildPlanMentions(plan);    // ruleId → [plan item titles]
        var taskMentions  = BuildTaskMentions(tasks);   // ruleId → [task titles]
        var planViolations = BuildPlanViolations(plan); // ruleId → [(item, severity, issue)]

        // ── Per-rule results ───────────────────────────────────────────────

        var results    = new List<ComplianceResult>();
        var violations = new List<ComplianceViolation>(planViolations.Values.SelectMany(v => v));
        var gaps       = new List<ComplianceGap>();
        var recs       = new List<ComplianceRecommendation>();

        foreach (var rule in constitution.RuleCatalog)
        {
            var allIds = AllIds(rule);

            var specRefs = allIds.SelectMany(id => specMentions.GetValueOrDefault(id.ToUpperInvariant(), [])).Distinct().ToList();
            var planRefs = allIds.SelectMany(id => planMentions.GetValueOrDefault(id.ToUpperInvariant(), [])).Distinct().ToList();
            var taskRefs = allIds.SelectMany(id => taskMentions.GetValueOrDefault(id.ToUpperInvariant(), [])).Distinct().ToList();

            bool hasSpec  = spec  is not null && specRefs.Count > 0;
            bool hasPlan  = plan  is not null && planRefs.Count > 0;
            bool hasTask  = tasks is not null && taskRefs.Count > 0;

            bool isViolated = allIds.Any(id => planViolations.ContainsKey(id.ToUpperInvariant()));

            var status = DetermineStatus(hasSpec, hasPlan, hasTask, isViolated,
                spec is not null, plan is not null, tasks is not null);

            results.Add(new ComplianceResult
            {
                RuleId          = rule.RuleId,
                RuleTitle       = rule.Title,
                RuleType        = rule.RuleType,
                Status          = status,
                HasSpecCoverage = hasSpec,
                HasPlanCoverage = hasPlan,
                HasTaskCoverage = hasTask,
                SpecReferences  = specRefs,
                PlanReferences  = planRefs,
                TaskReferences  = taskRefs,
            });

            // Gaps: rule not covered in at least one loaded artifact
            bool missSpec  = spec  is not null && !hasSpec;
            bool missPlan  = plan  is not null && !hasPlan;
            bool missTask  = tasks is not null && !hasTask;
            bool noArtifacts = spec is null && plan is null && tasks is null;

            if ((missSpec || missPlan || missTask) && status != ComplianceStatus.Violation)
            {
                var sev = GapSeverityFor(rule.RuleType, missSpec, missPlan, missTask);
                gaps.Add(new ComplianceGap
                {
                    RuleId        = rule.RuleId,
                    RuleTitle     = rule.Title,
                    RuleType      = rule.RuleType,
                    MissingInSpec  = missSpec,
                    MissingInPlan  = missPlan,
                    MissingInTasks = missTask,
                    Severity      = sev,
                });
                recs.AddRange(BuildRecommendations(rule, missSpec, missPlan, missTask, sev));
            }
            else if (noArtifacts && status == ComplianceStatus.Missing)
            {
                // No artifacts loaded — recommend adding coverage to all three
                var sev = GapSeverityFor(rule.RuleType, true, true, true);
                recs.AddRange(BuildRecommendations(rule, true, true, true, sev));
            }

            // Violation recommendations
            if (isViolated)
            {
                var violSev = allIds
                    .SelectMany(id => planViolations.GetValueOrDefault(id.ToUpperInvariant(), []))
                    .Select(v => v.Severity)
                    .DefaultIfEmpty(ViolationSeverity.High)
                    .Min();

                recs.Add(new ComplianceRecommendation
                {
                    RuleId        = rule.RuleId,
                    Text          = $"Resolve {rule.RuleId} ({rule.Title}) violation in the implementation plan.",
                    TargetArtifact = ArtifactType.Plan,
                    Priority      = violSev,
                });
            }
        }

        // Sort gaps and recs by severity
        gaps.Sort((a, b) => a.Severity.CompareTo(b.Severity));
        recs.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        var coverage = BuildCoverage(results);
        var health   = BuildHealth(results, violations, spec, plan, tasks);

        return new ConstitutionComplianceReport
        {
            Results          = results,
            Violations       = violations.OrderBy(v => v.Severity).ToList(),
            Gaps             = gaps,
            Recommendations  = recs,
            Coverage         = coverage,
            Health           = health,
            HasConstitution  = true,
            HasSpecification = spec  is not null,
            HasPlan          = plan  is not null,
            HasTasks         = tasks is not null,
        };
    }

    // ── Search / filter ───────────────────────────────────────────────────────

    public IEnumerable<ComplianceResult> SearchResults(IEnumerable<ComplianceResult> results, string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return results;
        var ci = StringComparison.OrdinalIgnoreCase;
        return results.Where(r =>
            r.RuleId.Contains(q, ci) ||
            r.RuleTitle.Contains(q, ci) ||
            r.RuleType.ToString().Contains(q, ci) ||
            r.Status.ToString().Contains(q, ci) ||
            r.SpecReferences.Any(s => s.Contains(q, ci)) ||
            r.PlanReferences.Any(s => s.Contains(q, ci)) ||
            r.TaskReferences.Any(s => s.Contains(q, ci)));
    }

    public IEnumerable<ComplianceResult> FilterResultsByStatus(IEnumerable<ComplianceResult> results, ComplianceStatus? status)
    {
        if (status is null) return results;
        return results.Where(r => r.Status == status);
    }

    public IEnumerable<ComplianceResult> FilterResultsByRuleType(IEnumerable<ComplianceResult> results, ConstitutionRuleType? type)
    {
        if (type is null) return results;
        return results.Where(r => r.RuleType == type);
    }

    public IEnumerable<ComplianceViolation> SearchViolations(IEnumerable<ComplianceViolation> violations, string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return violations;
        var ci = StringComparison.OrdinalIgnoreCase;
        return violations.Where(v =>
            v.RuleId.Contains(q, ci) ||
            v.RuleTitle.Contains(q, ci) ||
            v.Issue.Contains(q, ci) ||
            v.Artifact.ToString().Contains(q, ci) ||
            (v.Evidence?.Contains(q, ci) ?? false));
    }

    public IEnumerable<ComplianceViolation> FilterViolationsBySeverity(IEnumerable<ComplianceViolation> violations, ViolationSeverity? severity)
    {
        if (severity is null) return violations;
        return violations.Where(v => v.Severity == severity);
    }

    public IEnumerable<ComplianceViolation> FilterViolationsByArtifact(IEnumerable<ComplianceViolation> violations, ArtifactType? artifact)
    {
        if (artifact is null) return violations;
        return violations.Where(v => v.Artifact == artifact);
    }

    public IEnumerable<ComplianceGap> SearchGaps(IEnumerable<ComplianceGap> gaps, string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return gaps;
        var ci = StringComparison.OrdinalIgnoreCase;
        return gaps.Where(g =>
            g.RuleId.Contains(q, ci) ||
            g.RuleTitle.Contains(q, ci) ||
            g.MissingSummary.Contains(q, ci));
    }

    public IEnumerable<ComplianceGap> FilterGapsBySeverity(IEnumerable<ComplianceGap> gaps, ViolationSeverity? severity)
    {
        if (severity is null) return gaps;
        return gaps.Where(g => g.Severity == severity);
    }

    public IEnumerable<ComplianceRecommendation> SearchRecommendations(IEnumerable<ComplianceRecommendation> recs, string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return recs;
        var ci = StringComparison.OrdinalIgnoreCase;
        return recs.Where(r => r.RuleId.Contains(q, ci) || r.Text.Contains(q, ci));
    }

    public IEnumerable<ComplianceRecommendation> FilterRecommendationsByArtifact(IEnumerable<ComplianceRecommendation> recs, ArtifactType? artifact)
    {
        if (artifact is null) return recs;
        return recs.Where(r => r.TargetArtifact == artifact);
    }

    // ── Mention maps ──────────────────────────────────────────────────────────

    private static Dictionary<string, List<string>> BuildSpecMentions(SpecTree? spec)
    {
        if (spec is null) return [];
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var nodes = FlattenSpec(spec.Roots);
        foreach (var node in nodes)
        {
            var text = (node.Title ?? string.Empty) + " " +
                       (node.Excerpt ?? string.Empty) + " " +
                       (node.FullContent ?? string.Empty);
            foreach (Match m in RuleIdRe.Matches(text))
                AddMention(map, m.Groups[1].Value.ToUpperInvariant(), node.Title ?? node.Id);
        }
        return map;
    }

    private static Dictionary<string, List<string>> BuildPlanMentions(PlanDocument? plan)
    {
        if (plan is null) return [];
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Gates and check items — strongest signal
        foreach (var g in plan.Gates)
            if (!string.IsNullOrEmpty(g.RuleId))
                AddMention(map, g.RuleId.ToUpperInvariant(), $"Gate: {g.Gate}");

        foreach (var c in plan.ConstitutionCheckItems)
            if (!string.IsNullOrEmpty(c.RuleId))
                AddMention(map, c.RuleId.ToUpperInvariant(), $"Check: {c.Title}");

        // Scan free-text sections
        void ScanText(string? text, string label)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match m in RuleIdRe.Matches(text))
                AddMention(map, m.Groups[1].Value.ToUpperInvariant(), label);
        }

        foreach (var s in plan.Sections)     ScanText(s.RawContent, s.Title);
        foreach (var d in plan.ArchitectureDecisions)
        {
            ScanText(d.Context,   d.Title);
            ScanText(d.Decision,  d.Title);
            ScanText(d.Rationale, d.Title);
            ScanText(d.RawText,   d.Title);
        }
        foreach (var p in plan.Phases)
        {
            ScanText(p.Title,       p.Title);
            ScanText(p.Description, p.Title);
            foreach (var t in p.Tasks) ScanText(t, p.Title);
        }

        return map;
    }

    private static Dictionary<string, List<string>> BuildTaskMentions(TaskTree? tasks)
    {
        if (tasks is null) return [];
        var map   = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var nodes = FlattenTasks(tasks.Roots);
        foreach (var node in nodes)
        {
            var text = node.Title + " " + node.RawText;
            foreach (Match m in RuleIdRe.Matches(text))
                AddMention(map, m.Groups[1].Value.ToUpperInvariant(),
                    node.TaskId ?? node.ShortTitle ?? node.Title);
        }
        return map;
    }

    // Violations come from explicit non-compliant plan check items and failed gates
    private static Dictionary<string, List<ComplianceViolation>> BuildPlanViolations(PlanDocument? plan)
    {
        if (plan is null) return [];
        var map = new Dictionary<string, List<ComplianceViolation>>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in plan.ConstitutionCheckItems
            .Where(c => c.Status == ConstitutionCheckStatus.NonCompliant && !string.IsNullOrEmpty(c.RuleId)))
        {
            var v = new ComplianceViolation
            {
                RuleId   = c.RuleId,
                RuleTitle = c.Title,
                Artifact = ArtifactType.Plan,
                Issue    = c.Notes ?? $"Constitution check item marked Non-Compliant in plan.",
                Severity = ViolationSeverity.High,
                Evidence = c.Notes,
            };
            AddToList(map, c.RuleId.ToUpperInvariant(), v);
        }

        foreach (var g in plan.Gates.Where(g => g.Status == PlanGateStatus.Fail && !string.IsNullOrEmpty(g.RuleId)))
        {
            var v = new ComplianceViolation
            {
                RuleId    = g.RuleId,
                RuleTitle = g.Gate,
                Artifact  = ArtifactType.Plan,
                Issue     = g.Notes ?? $"Constitution gate FAILED in plan.",
                Severity  = ViolationSeverity.Critical,
                Evidence  = g.Evidence,
            };
            AddToList(map, g.RuleId.ToUpperInvariant(), v);
        }

        return map;
    }

    // ── Status computation ────────────────────────────────────────────────────

    private static ComplianceStatus DetermineStatus(
        bool hasSpec, bool hasPlan, bool hasTask, bool isViolated,
        bool specLoaded, bool planLoaded, bool taskLoaded)
    {
        if (isViolated) return ComplianceStatus.Violation;

        int loaded   = (specLoaded ? 1 : 0) + (planLoaded ? 1 : 0) + (taskLoaded ? 1 : 0);
        int covered  = (hasSpec ? 1 : 0)  + (hasPlan ? 1 : 0)  + (hasTask ? 1 : 0);

        if (covered == 0) return ComplianceStatus.Missing;
        if (covered == loaded) return ComplianceStatus.Compliant;
        return ComplianceStatus.Partial;
    }

    // ── Gap severity ─────────────────────────────────────────────────────────

    private static ViolationSeverity GapSeverityFor(
        ConstitutionRuleType ruleType, bool missSpec, bool missPlan, bool missTask)
    {
        // Principles are the most critical; missing across all artifacts = Critical
        bool allMissing = missSpec && missPlan && missTask;
        return ruleType switch
        {
            ConstitutionRuleType.Principle  => allMissing ? ViolationSeverity.Critical : ViolationSeverity.High,
            ConstitutionRuleType.Standard   => allMissing ? ViolationSeverity.High     : ViolationSeverity.Medium,
            ConstitutionRuleType.Constraint => allMissing ? ViolationSeverity.High     : ViolationSeverity.Medium,
            ConstitutionRuleType.Guideline  => allMissing ? ViolationSeverity.Medium   : ViolationSeverity.Low,
            _                               => ViolationSeverity.Low,
        };
    }

    // ── Recommendations ───────────────────────────────────────────────────────

    private static IEnumerable<ComplianceRecommendation> BuildRecommendations(
        ConstitutionRule rule, bool missSpec, bool missPlan, bool missTask, ViolationSeverity sev)
    {
        if (missSpec)
            yield return new ComplianceRecommendation
            {
                RuleId        = rule.RuleId,
                Text          = BuildSpecRec(rule),
                TargetArtifact = ArtifactType.Specification,
                Priority      = sev,
            };

        if (missPlan)
            yield return new ComplianceRecommendation
            {
                RuleId        = rule.RuleId,
                Text          = BuildPlanRec(rule),
                TargetArtifact = ArtifactType.Plan,
                Priority      = sev,
            };

        if (missTask)
            yield return new ComplianceRecommendation
            {
                RuleId        = rule.RuleId,
                Text          = BuildTaskRec(rule),
                TargetArtifact = ArtifactType.Task,
                Priority      = sev,
            };
    }

    private static string BuildSpecRec(ConstitutionRule rule)
    {
        var topic = InferTopic(rule);
        return $"Add {rule.RuleId} ({rule.Title}) requirements to the specification. {topic}";
    }

    private static string BuildPlanRec(ConstitutionRule rule)
    {
        var topic = InferTopic(rule);
        return $"Add {rule.RuleId} ({rule.Title}) implementation strategy to the plan. {topic}";
    }

    private static string BuildTaskRec(ConstitutionRule rule)
    {
        var topic = InferTopic(rule);
        return $"Add {rule.RuleId} ({rule.Title}) tasks to the task list. {topic}";
    }

    private static string InferTopic(ConstitutionRule rule)
    {
        var lower = (rule.Title + " " + rule.Description).ToLowerInvariant();
        if (lower.Contains("auth"))         return "Consider adding authorization requirements.";
        if (lower.Contains("test"))         return "Consider adding testing tasks.";
        if (lower.Contains("observ") || lower.Contains("log")) return "Consider adding observability requirements.";
        if (lower.Contains("secur"))        return "Consider adding security requirements.";
        if (lower.Contains("audit"))        return "Consider adding audit logging requirements.";
        if (lower.Contains("perform"))      return "Consider adding performance requirements.";
        if (lower.Contains("token") || lower.Contains("jwt")) return "Consider adding token validation requirements.";
        if (lower.Contains("role") || lower.Contains("rbac")) return "Consider adding role-based access requirements.";
        if (lower.Contains("encr"))         return "Consider adding encryption requirements.";
        return string.Empty;
    }

    // ── Coverage & health ─────────────────────────────────────────────────────

    private static ComplianceCoverage BuildCoverage(List<ComplianceResult> results)
    {
        if (results.Count == 0) return new ComplianceCoverage();
        return new ComplianceCoverage
        {
            TotalItems     = results.Count,
            CompliantItems = results.Count(r => r.Status == ComplianceStatus.Compliant),
            PartialItems   = results.Count(r => r.Status == ComplianceStatus.Partial),
            MissingItems   = results.Count(r => r.Status == ComplianceStatus.Missing),
            ViolationItems = results.Count(r => r.Status == ComplianceStatus.Violation),
        };
    }

    private static ComplianceHealth BuildHealth(
        List<ComplianceResult>    results,
        List<ComplianceViolation> violations,
        SpecTree?   spec,
        PlanDocument? plan,
        TaskTree?   tasks)
    {
        if (results.Count == 0) return new ComplianceHealth();

        var compliant  = results.Count(r => r.Status == ComplianceStatus.Compliant);
        var partial    = results.Count(r => r.Status == ComplianceStatus.Partial);
        var missing    = results.Count(r => r.Status == ComplianceStatus.Missing);
        var violated   = results.Count(r => r.Status == ComplianceStatus.Violation);
        var pct        = results.Count > 0
            ? Math.Round((double)(compliant + partial) / results.Count * 100.0, 1)
            : 0.0;

        var indicators = new List<ComplianceHealthIndicator>();

        // Violations always surface first
        if (violations.Count > 0)
        {
            var critCount = violations.Count(v => v.Severity == ViolationSeverity.Critical);
            indicators.Add(new ComplianceHealthIndicator
            {
                Icon    = "✗",
                Message = $"{violations.Count} compliance violation{(violations.Count != 1 ? "s" : "")} detected" +
                          (critCount > 0 ? $" — {critCount} critical" : string.Empty),
                Level   = ComplianceHealthLevel.Error,
            });
        }

        // Missing rules
        if (missing > 0)
            indicators.Add(new ComplianceHealthIndicator
            {
                Icon    = "⚠",
                Message = $"{missing} rule{(missing != 1 ? "s" : "")} with no coverage across loaded artifacts",
                Level   = ComplianceHealthLevel.Warning,
            });

        // Partial
        if (partial > 0)
            indicators.Add(new ComplianceHealthIndicator
            {
                Icon    = "◑",
                Message = $"{partial} rule{(partial != 1 ? "s" : "")} partially covered",
                Level   = ComplianceHealthLevel.Warning,
            });

        // Good
        if (compliant > 0)
            indicators.Add(new ComplianceHealthIndicator
            {
                Icon    = "✓",
                Message = $"{compliant} rule{(compliant != 1 ? "s" : "")} fully covered across all loaded artifacts",
                Level   = ComplianceHealthLevel.Good,
            });

        // Missing artifact warnings
        if (spec   is null) indicators.Add(new ComplianceHealthIndicator { Icon = "⚠", Message = "Specification not loaded — spec compliance unknown", Level = ComplianceHealthLevel.Warning });
        if (plan   is null) indicators.Add(new ComplianceHealthIndicator { Icon = "⚠", Message = "Plan not loaded — implementation compliance unknown", Level = ComplianceHealthLevel.Warning });
        if (tasks  is null) indicators.Add(new ComplianceHealthIndicator { Icon = "⚠", Message = "Tasks not loaded — task compliance unknown", Level = ComplianceHealthLevel.Warning });

        return new ComplianceHealth
        {
            TotalRules           = results.Count,
            CoveredRules         = compliant,
            PartialRules         = partial,
            MissingRules         = missing,
            ViolationCount       = violations.Count,
            CompliancePercentage = pct,
            Indicators           = indicators,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IEnumerable<string> AllIds(ConstitutionRule rule)
    {
        yield return rule.RuleId;
        foreach (var a in rule.Aliases) yield return a;
    }

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
        void Visit(TaskNode n) { result.Add(n); foreach (var c in n.Children) Visit(c); }
        foreach (var n in nodes) Visit(n);
        return result;
    }

    private static void AddMention(Dictionary<string, List<string>> map, string key, string label)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        if (!list.Contains(label, StringComparer.OrdinalIgnoreCase))
            list.Add(label);
    }

    private static void AddToList<TVal>(Dictionary<string, List<TVal>> map, string key, TVal value)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        list.Add(value);
    }
}
