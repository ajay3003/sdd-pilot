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
