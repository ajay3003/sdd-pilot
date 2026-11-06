using BirkNext.Governance;
using BirkNext.Interpretation;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>One root cause that blocks the release, listed once with every dimension it affects and the place that resolves it.</summary>
public sealed record ReleaseBlockingCause(string Key, string Title, string Detail, IReadOnlyList<string> Dimensions, string Href, string ActionLabel);

/// <summary>A required dimension that could not be assessed (a gap, not a defect): what is missing and where to provide it.</summary>
public sealed record ReleaseRequiredGap(ReadinessDimensionKey Key, string Title, string Summary, string? NextAction, string? Href);

/// <summary>
/// Imported test results (TRX) in the workspace for one environment. They are reported as found, never as execution of the release scope: mapping
/// a result to a requirement in scope needs a confirmed test link and a result still current for the evidence version, which release readiness does
/// not read yet — so "Functional test execution" stays Not assessed.
/// </summary>
public sealed record ReleaseExecutionEvidence(int Results, int Passed, int Failed, int Other, string? Environment);

/// <summary>One executive-summary figure: a count, or "Not assessed" when unknown (never 0 for unknown), with what it measures.</summary>
public sealed record ReleaseMetric(string Key, string Value, string Label, string Detail, string Tone);

/// <summary>Dimensions of one applicability (Required, Optional, Not applicable) with a tally of their states.</summary>
public sealed record ReleaseDimensionGroup(DimensionApplicability Applicability, string Title, string Explanation, IReadOnlyList<ReadinessDimension> Dimensions, string Tally);

/// <summary>A dimension whose state differs between a frozen assessment and the current draft (or between two frozen assessments).</summary>
public sealed record ReleaseDimensionChange(ReadinessDimensionKey Key, string Title, GateDecision? Then, GateDecision? Now)
{
    public bool Changed => Then != Now;
}

public static class ReleaseReadinessPresentation
{
    /// <summary>The order prerequisites resolve in: scope first (nothing else is meaningful without it), then governance and decisions, then the evidence.</summary>
    private static readonly ReadinessDimensionKey[] PrerequisiteOrder =
    [
        ReadinessDimensionKey.ScopeClarity, ReadinessDimensionKey.Governance, ReadinessDimensionKey.OpenDecisions, ReadinessDimensionKey.Requirements,
        ReadinessDimensionKey.Traceability, ReadinessDimensionKey.TestDesign, ReadinessDimensionKey.Implementation,
    ];

    /// <summary>
    /// What blocks the release: each root cause once. An identity conflict appears in Governance (blocking) and in Open review decisions; it is one
    /// cause with both dimensions listed and a link to the exact review item, never two blockers. An unresolved scope is its own cause.
    /// </summary>
    public static IReadOnlyList<ReleaseBlockingCause> BlockingCauses(ReleaseReadinessAssessment assessment)
    {
        var causes = new List<ReleaseBlockingCause>();
        var dims = assessment.Dimensions.Where(d => d.Applicability == DimensionApplicability.Required).ToList();
        if (dims.FirstOrDefault(d => d.Key == ReadinessDimensionKey.ScopeClarity && d.State == GateDecision.NotAssessed) is { } scope)
            causes.Add(new("scope", "Release scope not resolved", scope.Summary,
                [.. dims.Where(d => d.State == GateDecision.NotAssessed && d.Key is ReadinessDimensionKey.ScopeClarity or ReadinessDimensionKey.Requirements or ReadinessDimensionKey.Traceability
                    or ReadinessDimensionKey.TestDesign).Select(d => d.Title)], scope.Href ?? "correlation-review", "Resolve the scope"));
        if (dims.FirstOrDefault(d => d.Key == ReadinessDimensionKey.Governance && d.State == GateDecision.Blocked) is { } governance)
        {
            var decisions = dims.FirstOrDefault(d => d.Key == ReadinessDimensionKey.OpenDecisions);
            foreach (var finding in governance.Evidence.Where(e => e.StartsWith("Blocking", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal))
            {
                var title = finding.Split(" · ", 2) is [_, var rest] ? rest : finding;
                var identity = title.StartsWith("Identity conflict", StringComparison.Ordinal);
                IReadOnlyList<string> affected = identity && decisions is { State: not GateDecision.Ready } ? [governance.Title, decisions.Title] : [governance.Title];
                causes.Add(new(identity ? "identity-conflict" : $"governance:{title}", title,
                    identity ? "Two declarations disagree about what something is. It stays blocking until a reviewer decides; deferring does not clear it."
                        : "A blocking governance finding for release readiness.",
                    affected, identity ? "correlation-review?tab=review&type=IdentityConflict" : governance.Href ?? "correlation-review",
                    identity ? "Resolve in Relationship Review" : "Open the finding"));
            }
            if (causes.All(c => !c.Key.StartsWith("governance", StringComparison.Ordinal) && c.Key != "identity-conflict"))
                causes.Add(new("governance", "Governance", governance.Summary, [governance.Title], governance.Href ?? "correlation-review", "Open governance"));
        }
        foreach (var other in dims.Where(d => d.State == GateDecision.Blocked && d.Key is not (ReadinessDimensionKey.Governance or ReadinessDimensionKey.ScopeClarity)))
            causes.Add(new(other.Key.ToString(), other.Title, other.Summary, [other.Title], other.Href ?? "", other.NextAction ?? "Open"));
        return causes;
    }

    /// <summary>Required dimensions not assessed, except those an unresolved scope already explains (listed under that cause instead).</summary>
    public static IReadOnlyList<ReleaseRequiredGap> RequiredGaps(ReleaseReadinessAssessment assessment)
    {
        var scopeUnresolved = assessment.Dimensions.Any(d => d.Key == ReadinessDimensionKey.ScopeClarity && d.State == GateDecision.NotAssessed);
        return [.. assessment.Dimensions.Where(d => d.Applicability == DimensionApplicability.Required && d.State == GateDecision.NotAssessed)
            .Where(d => !scopeUnresolved || d.Key is not (ReadinessDimensionKey.ScopeClarity or ReadinessDimensionKey.Requirements or ReadinessDimensionKey.Traceability or ReadinessDimensionKey.TestDesign))
            .Select(d => new ReleaseRequiredGap(d.Key, d.Title, d.Summary, d.NextAction, d.Href))];
    }

    /// <summary>What to do next, ordered by prerequisite (scope → governance → decisions → requirements → traceability → test design → implementation); one step per dimension.</summary>
    public static IReadOnlyList<(string Title, string Action, string? Href)> NextSteps(ReleaseReadinessAssessment assessment) =>
        [.. assessment.Dimensions.Where(d => d.Applicability == DimensionApplicability.Required && d.State != GateDecision.Ready && d.NextAction is { Length: > 0 } && d.NextAction != "None")
            .OrderBy(d => Array.IndexOf(PrerequisiteOrder, d.Key) is var i && i >= 0 ? i : int.MaxValue)
            .Select(d => (d.Title, d.NextAction!, d.Key == ReadinessDimensionKey.Governance && BlockingCauses(assessment).Any(c => c.Key == "identity-conflict")
                ? "correlation-review?tab=review&type=IdentityConflict" : d.Href))];

    // ── NEXT57: executive summary, grouped dimensions, history comparison ───────────────────────────────────────────────

    private static readonly GateDecision[] StateOrder = [GateDecision.Blocked, GateDecision.NotAssessed, GateDecision.ReadyWithLimitations, GateDecision.PassedWithException, GateDecision.Ready];

    /// <summary>The executive summary figures: what is in scope, what is assessed, tested and executed, and what blocks — each "Not assessed" when unknown.</summary>
    public static IReadOnlyList<ReleaseMetric> Metrics(ReleaseReadinessAssessment a, ReleaseExecutionEvidence? execution)
    {
        var f = a.Facts;
        string Count(int? v) => v is { } x ? x.ToString() : "Not assessed";
        var gaps = RequiredGaps(a).Count + (BlockingCauses(a).Any(c => c.Key == "scope") ? 1 : 0);
        return
        [
            new("packs", Count(f?.FeaturePacks), "Feature packs in scope", f is null ? "No relationship analysis yet" : f.FeaturePacks == 0 ? "Release scope not resolved" : $"{f.Tasks} tasks", f is { FeaturePacks: > 0 } ? "neutral" : "warn"),
            new("requirements", Count(f?.Requirements), "Requirements assessed", f is null ? "" : $"{f.RequirementsWithoutTest} without any test link", "neutral"),
            new("tested", Count(f?.RequirementsWithConfirmedTest), "With a confirmed test", f is null ? "" : $"{f.RequirementsWithSuggestedTestOnly} more with a suggested test only — not counted", "neutral"),
            new("executed", "Not assessed", "Tests executed for this scope", execution is { } run ? $"{run.Results} imported result{(run.Results == 1 ? "" : "s")} found — not mapped to this scope" : "No execution evidence is read into release readiness", "muted"),
            new("blocking", Count(f?.BlockingFindings), "Blocking findings", BlockingCauses(a).Count is var c and > 0 ? $"{c} root cause{(c == 1 ? "" : "s")}" : "None", f is { BlockingFindings: > 0 } ? "bad" : "neutral"),
            new("decisions", Count(f?.OpenDecisions), "Decisions pending", "Relationship Review questions that change the assessment", f is { OpenDecisions: > 0 } ? "warn" : "neutral"),
            new("gaps", gaps.ToString(), "Required evidence gaps", gaps == 0 ? "Every required dimension is assessed" : "Required dimensions not assessed", gaps > 0 ? "warn" : "neutral"),
        ];
    }

    /// <summary>Dimensions grouped Required → Optional → Not applicable, each worst-first, with a tally such as "1 Blocked · 2 Not assessed · 3 Ready".</summary>
    public static IReadOnlyList<ReleaseDimensionGroup> Groups(ReleaseReadinessAssessment a) =>
    [
        .. new[]
        {
            (DimensionApplicability.Required, "Required", "Must be Ready (or Ready with limitations) before the release can be Ready. Blocked blocks; Not assessed leaves the release Not assessed."),
            (DimensionApplicability.Optional, "Optional", "Never blocks the release. Anything other than Ready makes the result Ready with limitations."),
            (DimensionApplicability.NotApplicable, "Not applicable", "Does not apply to this scope and never counts."),
        }.Select(g => (g, dims: a.Dimensions.Where(d => d.Applicability == g.Item1).OrderBy(d => Array.IndexOf(StateOrder, d.State)).ToList()))
         .Where(x => x.dims.Count > 0)
         .Select(x => new ReleaseDimensionGroup(x.g.Item1, x.g.Item2, x.g.Item3, x.dims, Tally(x.dims))),
    ];

    public static string Tally(IEnumerable<ReadinessDimension> dims) =>
        string.Join(" · ", dims.GroupBy(d => d.State).OrderBy(g => Array.IndexOf(StateOrder, g.Key)).Select(g => $"{g.Count()} {GovernancePolicies.DecisionLabel(g.Key)}"));

    public static string Tone(GateDecision d) => d switch
    {
        GateDecision.Ready => "ready",
        GateDecision.ReadyWithLimitations or GateDecision.PassedWithException => "limited",
        GateDecision.Blocked => "blocked",
        _ => "notassessed",
    };

    /// <summary>Per dimension, the frozen state against the current one (a dimension present on one side only reads as missing on the other).</summary>
    public static IReadOnlyList<ReleaseDimensionChange> Compare(ReleaseReadinessAssessment then, ReleaseReadinessAssessment now)
    {
        var keys = then.Dimensions.Select(d => d.Key).Concat(now.Dimensions.Select(d => d.Key)).Distinct();
        return [.. keys.Select(k => (t: then.Dimensions.FirstOrDefault(d => d.Key == k), n: now.Dimensions.FirstOrDefault(d => d.Key == k)))
            .Select(x => new ReleaseDimensionChange(x.t?.Key ?? x.n!.Key, (x.n ?? x.t)!.Title, x.t?.State, x.n?.State))
            .OrderByDescending(c => c.Changed)];
    }

    /// <summary>What a freeze would record, shown before freezing: scope, analysis, rules, result, blockers and gaps.</summary>
    public static IReadOnlyList<(string Label, string Value)> FreezeSummary(ReleaseReadinessAssessment a) =>
    [
        ("Scope", a.ScopeReference.Modules.Count == 0 ? "Entire project" : string.Join(", ", a.ScopeReference.Modules)),
        ("Relationship analysis", a.ScopeReference.InterpretationId is { } i ? i.ToString("D")[..8] : "Not prepared"),
        ("Feature packs · requirements", a.Facts is { } f ? $"{f.FeaturePacks} · {f.Requirements}" : "Not assessed"),
        ("Result", GovernancePolicies.DecisionLabel(a.Decision)),
        ("Blocking causes", BlockingCauses(a).Count.ToString()),
        ("Required gaps", RequiredGaps(a).Count.ToString()),
        ("Rules", $"{a.RuleSetVersion} · {a.PolicySetVersion}"),
    ];

    public static ReleaseExecutionEvidence? Execution(IEnumerable<SddTestExecutionEvidence>? executions, string? environment)
    {
        var all = (executions ?? []).Where(e => e.EvidenceKind == "Executed").ToList();
        if (all.Count == 0) return null;
        var inEnvironment = environment is null ? all
            : all.Where(e => e.EnvironmentReference is null || string.Equals(e.EnvironmentReference, environment, StringComparison.OrdinalIgnoreCase)).ToList();
        int Count(string result) => inEnvironment.Count(e => string.Equals(e.Result, result, StringComparison.OrdinalIgnoreCase));
        var passed = Count("Passed");
        var failed = Count("Failed");
        return new(inEnvironment.Count, passed, failed, inEnvironment.Count - passed - failed, environment);
    }
}
