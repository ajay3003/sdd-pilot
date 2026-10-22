using BirkNext.PipelineReview;
using BirkNext.SourceArchitecture;

namespace BirkNext.Web.Services;

/// <summary>Findings that share one rule (category, severity, environment and the rule's "why it matters"): guidance is shown once.</summary>
public sealed record PipelineFindingGroup(string Key, string Title, PipelineFindingCategory Category, PipelineFindingSeverity Severity, string? Environment,
    string WhyItMatters, string? SuggestedAction, IReadOnlyList<PipelineReviewFinding> Findings);

/// <summary>How a defined test relates to deployments, in the review's own gate vocabulary.</summary>
public enum TestGateSummary { GatesDeployment, Conditional, MayNotBlock, NotAssessable, RunsAfterDeployment, NotAssociated }

/// <summary>How a deployment is protected before it runs.</summary>
public enum DeploymentGating { Gated, MayNotBlock, NoGatingTest, NotAssessable }

public sealed record EnvironmentGatingSummary(string Environment, int Deployments, int Gated, int MayNotBlock, int NoGatingTest, int NotAssessable, int WithPostValidation);

/// <summary>
/// Presentation-only aggregation for Pipeline Review: groups repeated findings, summarizes triggers/artifacts/tests/deployments and turns the
/// analyzer's long sentences into compact labels (the full sentence stays one click away). Counts always come from the review result; no
/// parsing, gate, severity or comparison rule is recomputed here.
/// </summary>
public static class PipelineReviewGrouping
{
    private static readonly HashSet<string> TrailingWords = new(StringComparer.OrdinalIgnoreCase) { "in", "for", "of", "the", "to", "after", "before", "on", "from", "and" };

    public static IReadOnlyList<PipelineFindingGroup> Group(IEnumerable<PipelineReviewFinding> findings) =>
        [.. findings
            .GroupBy(f => (f.Category, f.Severity, f.Environment, f.WhyItMatters))
            .Select(g =>
            {
                var items = g.ToList();
                var actions = items.Select(f => f.SuggestedAction).Distinct(StringComparer.Ordinal).ToList();
                return new PipelineFindingGroup($"{g.Key.Category}|{g.Key.Severity}|{g.Key.Environment}|{Math.Abs(StringComparer.Ordinal.GetHashCode(g.Key.WhyItMatters))}",
                    GroupTitle(items.Select(f => f.Title).ToList(), g.Key.Category, g.Key.Environment), g.Key.Category, g.Key.Severity, g.Key.Environment,
                    g.Key.WhyItMatters, actions.Count == 1 ? actions[0] : null, items);
            })
            .OrderBy(g => g.Severity).ThenByDescending(g => g.Findings.Count).ThenBy(g => g.Title, StringComparer.Ordinal)];

    /// <summary>The shared title when instances repeat it; otherwise their common leading words (instance names removed); else the rule label.</summary>
    public static string GroupTitle(IReadOnlyList<string> titles, PipelineFindingCategory category, string? environment)
    {
        if (titles.Count == 0) return PipelineReviewText.Label(category);
        if (titles.Distinct(StringComparer.Ordinal).Count() == 1) return titles[0];
        var words = titles.Select(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var common = new List<string>();
        for (var i = 0; i < words.Min(w => w.Length); i++)
        {
            var word = words[0][i];
            if (words.Any(w => !string.Equals(w[i], word, StringComparison.Ordinal))) break;
            common.Add(word);
        }
        while (common.Count > 0 && (TrailingWords.Contains(common[^1]) || common[^1].StartsWith('`'))) common.RemoveAt(common.Count - 1);
        return common.Count >= 3 ? string.Join(' ', common) : $"{PipelineReviewText.Label(category)}{(environment is null ? "" : $" · {environment}")}";
    }

    public static string TriggerKindLabel(string kind) => kind switch
    {
        "push" => "Push",
        "pull-request" => "Pull request",
        "schedule" => "Scheduled",
        "pipeline-resource" => "Pipeline completion",
        _ => "Manual",
    };

    /// <summary>How a runnable pipeline starts: its active trigger kinds, or Manual when it has none ("trigger: none" disables, it does not start).</summary>
    public static IReadOnlyList<string> TriggerLabels(ReviewedPipeline pipeline)
    {
        var active = pipeline.Triggers.Where(t => t.Kind != "none").Select(t => TriggerKindLabel(t.Kind)).Distinct().ToList();
        return pipeline.ManualOnly || active.Count == 0 ? ["Manual"] : active;
    }

    /// <summary>Runnable pipelines per start mode (a pipeline with push and schedule counts in both).</summary>
    public static IReadOnlyList<(string Label, int Pipelines)> TriggerSummary(PipelineReviewResult r)
    {
        var runnable = r.Pipelines.Where(p => !p.IsTemplate).ToList();
        string[] order = ["Manual", "Scheduled", "Push", "Pull request", "Pipeline completion"];
        return [.. order.Select(label => (label, runnable.Count(p => TriggerLabels(p).Contains(label)))).Where(x => x.Item2 > 0)];
    }

    public static (int Named, int Resolved, int Unresolved) ArtifactSummary(PipelineReviewResult r)
    {
        var named = r.Pipelines.Where(p => !p.IsTemplate).SelectMany(p => p.Artifacts).Distinct(StringComparer.Ordinal).Count();
        var deployed = r.Deployments.SelectMany(d => d.Artifacts).GroupBy(a => a.Artifact, StringComparer.Ordinal).ToList();
        var unresolved = deployed.Count(g => g.Any(a => a.State == ArchitectureEvidenceState.Unresolved));
        return (named, deployed.Count - unresolved, unresolved);
    }

    private static bool IsTest(ValidationCategory c) => c is not (ValidationCategory.Build or ValidationCategory.InfrastructurePlan);

    public static DeploymentGating Gating(DeploymentReview d)
    {
        var tests = d.Before.Where(g => IsTest(g.Category)).ToList();
        if (tests.Any(g => g.State is GateState.Gates or GateState.Inherited)) return DeploymentGating.Gated;
        if (tests.Any(g => g.State is GateState.SoftGate or GateState.Conditional)) return DeploymentGating.MayNotBlock;
        if (d.Before.Any(g => g.State == GateState.Unknown)) return DeploymentGating.NotAssessable;
        return DeploymentGating.NoGatingTest;
    }

    public static string Label(DeploymentGating gating) => gating switch
    {
        DeploymentGating.Gated => "Gated by tests",
        DeploymentGating.MayNotBlock => "Tests may not block",
        DeploymentGating.NotAssessable => "Not assessable",
        _ => "No gating test detected",
    };

    public static IReadOnlyList<EnvironmentGatingSummary> Environments(PipelineReviewResult r) =>
        [.. r.Environments.Select(env =>
        {
            var deployments = r.Deployments.Where(d => env.Deployments.Contains(d.Id)).ToList();
            var gating = deployments.Select(Gating).ToList();
            return new EnvironmentGatingSummary(env.Environment, deployments.Count, gating.Count(g => g == DeploymentGating.Gated), gating.Count(g => g == DeploymentGating.MayNotBlock),
                gating.Count(g => g == DeploymentGating.NoGatingTest), gating.Count(g => g == DeploymentGating.NotAssessable), deployments.Count(d => d.After.Count > 0));
        })];

    /// <summary>A test's relation to deployments: the strongest gate state among the deployments that list it, else runs-after, else none.</summary>
    public static TestGateSummary Gate(ReviewedTest test, PipelineReviewResult r)
    {
        if (test.Gates.Count == 0) return test.RunsAfter.Count > 0 ? TestGateSummary.RunsAfterDeployment : TestGateSummary.NotAssociated;
        var states = r.Deployments.Where(d => test.Gates.Contains(d.Id)).SelectMany(d => d.Before)
            .Where(g => g.Evidence.File == test.Evidence.File && g.Evidence.Line == test.Evidence.Line).Select(g => g.State).ToList();
        if (states.Count == 0 || states.Any(s => s is GateState.Gates or GateState.Inherited)) return TestGateSummary.GatesDeployment;
        if (states.Any(s => s == GateState.Conditional)) return TestGateSummary.Conditional;
        if (states.Any(s => s == GateState.SoftGate)) return TestGateSummary.MayNotBlock;
        return TestGateSummary.NotAssessable;
    }

    public static string Label(TestGateSummary gate) => gate switch
    {
        TestGateSummary.GatesDeployment => "Gates deployment",
        TestGateSummary.Conditional => "Conditional",
        TestGateSummary.MayNotBlock => "May not block",
        TestGateSummary.NotAssessable => "Not assessable",
        TestGateSummary.RunsAfterDeployment => "Runs after deployment",
        _ => "Not associated with deployment",
    };

    /// <summary>Review order: what can let a deployment through first, then what does not gate, then what does.</summary>
    public static int Priority(TestGateSummary gate) => gate switch
    {
        TestGateSummary.MayNotBlock => 0, TestGateSummary.Conditional => 1, TestGateSummary.NotAssessable => 2,
        TestGateSummary.NotAssociated => 3, TestGateSummary.RunsAfterDeployment => 4, _ => 5,
    };

    /// <summary>Compact approval label; the analyzer's full sentence stays in the deployment details.</summary>
    public static string ApprovalLabel(string approval) =>
        approval.Contains("manual validation step", StringComparison.OrdinalIgnoreCase) ? "Manual validation step"
        : approval.StartsWith("No Azure DevOps environment is targeted", StringComparison.Ordinal) ? "No environment targeted"
        : approval.Contains("reports no checks", StringComparison.Ordinal) ? "No checks configured (Azure DevOps)"
        : approval.Contains("reports checks", StringComparison.Ordinal) ? "Checks configured (Azure DevOps)"
        : approval.Contains(PipelineReviewText.ApprovalNotAssessable, StringComparison.Ordinal) ? "Runtime metadata required"
        : approval;

    /// <summary>Compact rollback label. Absence is "not detected in source", never "no rollback exists".</summary>
    public static string RollbackLabel(string rollback) =>
        rollback.StartsWith("No rollback mechanism detected", StringComparison.Ordinal) ? "Not detected in source"
        : rollback.StartsWith("Rollback step", StringComparison.Ordinal) ? "Rollback step defined"
        : rollback.Contains("steps on failure", StringComparison.Ordinal) ? "On-failure steps"
        : rollback.Contains("slot swap", StringComparison.Ordinal) ? "Slot swap"
        : rollback.Contains("strategy", StringComparison.Ordinal) ? "Rolling/canary strategy"
        : rollback;

    public static string ChangeKind(PipelineChangeKind kind) => kind switch
    {
        PipelineChangeKind.PipelineAdded => "Pipeline added",
        PipelineChangeKind.PipelineRemoved => "Pipeline removed",
        PipelineChangeKind.TriggerChanged => "Trigger changed",
        PipelineChangeKind.PathFilterChanged => "Path filter changed",
        PipelineChangeKind.ConditionChanged => "Condition changed",
        PipelineChangeKind.TestAdded => "Test added",
        PipelineChangeKind.TestRemoved => "Test removed",
        PipelineChangeKind.DeploymentAdded => "Deployment added",
        PipelineChangeKind.DeploymentRemoved => "Deployment removed",
        PipelineChangeKind.GatingChanged => "Gating changed",
        PipelineChangeKind.ArtifactFlowChanged => "Artifact flow changed",
        _ => "Template changed",
    };

    /// <summary>The change category a test lead reads: pipelines, triggers, tests/checks, gates, deployments, artifacts or templates.</summary>
    public static string ChangeGroup(PipelineChangeKind kind) => kind switch
    {
        PipelineChangeKind.PipelineAdded or PipelineChangeKind.PipelineRemoved => "Pipelines",
        PipelineChangeKind.TriggerChanged or PipelineChangeKind.PathFilterChanged => "Triggers",
        PipelineChangeKind.TestAdded or PipelineChangeKind.TestRemoved => "Tests/checks",
        PipelineChangeKind.GatingChanged or PipelineChangeKind.ConditionChanged => "Gates",
        PipelineChangeKind.DeploymentAdded or PipelineChangeKind.DeploymentRemoved => "Deployments",
        PipelineChangeKind.ArtifactFlowChanged => "Artifacts",
        _ => "Templates",
    };

    public static readonly IReadOnlyList<string> ChangeGroups = ["Pipelines", "Triggers", "Tests/checks", "Gates", "Deployments", "Artifacts", "Templates"];

    public static string ChangeAction(PipelineChangeKind kind) => kind switch
    {
        PipelineChangeKind.PipelineAdded or PipelineChangeKind.TestAdded or PipelineChangeKind.DeploymentAdded => "Added",
        PipelineChangeKind.PipelineRemoved or PipelineChangeKind.TestRemoved or PipelineChangeKind.DeploymentRemoved => "Removed",
        _ => "Changed",
    };

    /// <summary>One pipeline's start conditions in plain language ("Push to main", "No push trigger", "PR trigger: none") — never a bare "none".</summary>
    public sealed record TriggerReading(string Starts, string PullRequest, string PathScope, IReadOnlyList<string> Paths);

    public static TriggerReading ReadTriggers(ReviewedPipeline p)
    {
        static string Branches(List<string> b) => b.Count == 0 ? "any branch" : b.Count <= 2 ? string.Join(", ", b) : $"{b.Count} branches";
        var ci = p.Triggers.Where(t => t.Kind == "push").ToList();
        var pr = p.Triggers.Where(t => t.Kind == "pull-request").ToList();
        var ciDisabled = p.Triggers.Any(t => t.Kind == "none" && !t.Text.TrimStart().StartsWith("pr", StringComparison.OrdinalIgnoreCase));
        var starts = new List<string>();
        if (ci.Count > 0) starts.Add($"Push to {Branches([.. ci.SelectMany(t => t.Branches).Distinct()])}");
        foreach (var s in p.Triggers.Where(t => t.Kind == "schedule")) starts.Add(s.Schedule is { Length: > 0 } cron ? $"Scheduled ({cron})" : "Scheduled");
        if (p.Triggers.Any(t => t.Kind == "pipeline-resource")) starts.Add("When another pipeline completes");
        if (ci.Count == 0 && ciDisabled) starts.Insert(0, "No push trigger");
        if (starts.Count == 0 || p.ManualOnly && ci.Count == 0) starts.Add("Manual/other trigger only");
        var paths = p.Triggers.Where(t => t.Kind is "push" or "pull-request").SelectMany(t => t.PathsInclude).Distinct(StringComparer.Ordinal).ToList();
        var excluded = p.Triggers.Where(t => t.Kind is "push" or "pull-request").SelectMany(t => t.PathsExclude).Distinct(StringComparer.Ordinal).Count();
        var scope = paths.Count == 0 ? ci.Count + pr.Count == 0 ? "—" : "All paths" : paths.Count <= 2 ? string.Join(", ", paths) : $"{paths.Count} paths";
        if (excluded > 0) scope += $" ({excluded} excluded)";
        return new(string.Join(" · ", starts.Distinct()), pr.Count > 0 ? $"Pull requests to {Branches([.. pr.SelectMany(t => t.Branches).Distinct()])}" : "None", scope, paths);
    }

    /// <summary>Validation by environment: deployments, test-gated, no gating detected, post-deployment validation.</summary>
    public sealed record EnvironmentValidation(string Environment, int Deployments, int TestGated, int MayNotBlock, int NoGating, int NotAssessable, int PostDeployment);

    public static IReadOnlyList<EnvironmentValidation> Validation(PipelineReviewResult r) =>
        [.. Environments(r).Select(s => new EnvironmentValidation(s.Environment, s.Deployments, s.Gated, s.MayNotBlock, s.NoGatingTest, s.NotAssessable, s.WithPostValidation))];

    /// <summary>The DEV → QA (→ …) relationship as found in source: confirmed/inferred when a progression exists, otherwise "No source
    /// relationship detected" — never "no promotion".</summary>
    public sealed record ReleaseStep(string Label, string Detail);

    public static IReadOnlyList<ReleaseStep> ReleasePath(PipelineReviewResult r)
    {
        var steps = new List<ReleaseStep>();
        var validation = r.Pipelines.Where(p => !p.IsTemplate && p.Environments.Count == 0 && r.Tests.Any(t => t.Pipeline == p.Id)).ToList();
        if (validation.Count > 0)
            steps.Add(new("PR / test validation", $"{validation.Count} pipeline{(validation.Count == 1 ? "" : "s")} validate without deploying ({string.Join(", ", validation.Take(3).Select(p => p.Name))}{(validation.Count > 3 ? ", …" : "")})."));
        var envs = Validation(r);
        for (var i = 0; i < envs.Count; i++)
        {
            var e = envs[i];
            steps.Add(new($"{e.Environment} deployment", $"{e.TestGated} of {e.Deployments} deployment{(e.Deployments == 1 ? "" : "s")} gated by tests · post-deployment validation: {(e.PostDeployment == 0 ? "not detected" : $"{e.PostDeployment} detected")}"));
            if (i + 1 < envs.Count)
            {
                var next = envs[i + 1].Environment;
                var link = r.Progression.FirstOrDefault(p => string.Equals(p.From, e.Environment, StringComparison.OrdinalIgnoreCase) && string.Equals(p.To, next, StringComparison.OrdinalIgnoreCase));
                steps.Add(new($"{e.Environment} → {next}", link is null
                    ? "No source relationship detected — no dependency, trigger, artifact or promotion relationship connecting these environments was found in the selected snapshot."
                    : $"Source relationship {(link.State == BirkNext.SourceArchitecture.ArchitectureEvidenceState.Confirmed ? "confirmed" : "inferred")}: {link.Basis}"));
            }
        }
        return steps;
    }

    public static string PipelineGate(PipelineReviewResult r, string pipelineId)
    {
        var deployments = r.Deployments.Where(d => d.Pipeline == pipelineId).Select(Gating).ToList();
        if (deployments.Count == 0) return "No deployment";
        if (deployments.All(g => g == DeploymentGating.Gated)) return "Gated by tests";
        return deployments.Contains(DeploymentGating.NoGatingTest) ? "No gating test detected" : deployments.Contains(DeploymentGating.MayNotBlock) ? "Tests may not block" : "Not assessable";
    }
}
