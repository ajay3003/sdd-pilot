using BirkNext.Dependencies;

namespace BirkNext.Web.Services;

/// <summary>
/// Pure projection of a Dependency Review result for the page and the export. Keeps outcomes distinct (ignored ≠ up to date, deferred ≠
/// blocked, automerge ≠ enabled), and only ever calls a simulated version a "Synthetic candidate".
/// </summary>
public static class DependencyReviewPresentation
{
    public static string Tone(PolicyResult result) => result switch
    {
        PolicyResult.Allowed => "ok",
        PolicyResult.Blocked or PolicyResult.BlockedByVersionConstraint => "attention",
        PolicyResult.RequiresApproval or PolicyResult.DeferredBySchedule => "info",
        _ => "muted",
    };

    public static string Tone(ReviewCategoryState state) => state switch
    {
        ReviewCategoryState.Ready => "ok",
        ReviewCategoryState.NeedsReview or ReviewCategoryState.Issue or ReviewCategoryState.Missing => "attention",
        ReviewCategoryState.Partial => "info",
        _ => "muted",
    };

    public static string Tone(RenovateCoverage coverage) => coverage switch
    {
        RenovateCoverage.Configured or RenovateCoverage.Inherited => "ok",
        RenovateCoverage.Missing => "attention",
        RenovateCoverage.Partial => "info",
        _ => "muted",
    };

    public static string Tone(DependencyFindingSeverity severity) => severity switch
    {
        DependencyFindingSeverity.NeedsReview => "attention",
        DependencyFindingSeverity.Warning => "attention",
        _ => "muted",
    };

    public static string Tone(ManagerState state) => state == ManagerState.Enabled ? "ok" : state == ManagerState.NoConfig ? "attention" : "muted";

    public static string Coverage(RenovateCoverage coverage) => coverage == RenovateCoverage.NotAssessed ? "Not assessed" : coverage.ToString();

    public static string UpdateType(DependencyUpdateType type) => type == DependencyUpdateType.NotAssessable ? "Not assessable" : type.ToString();

    /// <summary>Needs review first, then warnings, then information; within a severity by rule order.</summary>
    public static IReadOnlyList<DependencyFinding> OrderedFindings(RepositoryDependencyReview repo) =>
        repo.Findings.OrderBy(f => f.Severity).ThenBy(f => f.RuleIndex ?? int.MaxValue).ToList();

    /// <summary>Findings of one rule with one title, across the reviewed repositories; the shared detail is shown once when every instance says the same.</summary>
    public sealed record FindingGroup(IReadOnlyList<(string Repository, DependencyFinding Finding)> Findings, string? SharedDetail);

    /// <summary>"What needs attention": findings from every repository grouped by rule, in each repository's review order, needs-review first.</summary>
    public static IReadOnlyList<FindingGroup> FindingGroups(DependencyReviewResult result) =>
        [.. result.Repositories.SelectMany(repo => OrderedFindings(repo).Select(f => (repo.Repository, f)))
            .GroupBy(x => (x.f.RuleId, x.f.Severity, x.f.Title))
            .OrderBy(g => g.Key.Severity)
            .Select(g =>
            {
                var items = g.Select(x => (x.Repository, x.f)).ToList();
                var details = items.Select(x => x.f.Detail).Distinct(StringComparer.Ordinal).ToList();
                return new FindingGroup(items, details.Count == 1 ? details[0] : null);
            })];

    public static int Count(RepositoryDependencyReview repo, DependencyFindingSeverity severity) => repo.Findings.Count(f => f.Severity == severity);

    /// <summary>Synthetic scenarios matching an optional result filter.</summary>
    public static IReadOnlyList<PolicySimulation> Matrix(RepositoryDependencyReview repo, PolicyResult? filter) =>
        repo.Simulations.Where(s => filter is null || s.Result == filter).ToList();

    public static IReadOnlyList<(PolicyResult Result, int Count)> ResultCounts(RepositoryDependencyReview repo) =>
        repo.Simulations.GroupBy(s => s.Result).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())).ToList();

    public static string Automerge(EffectivePolicy policy) => policy.Automerge switch { true => "Automerge", false => "No automerge", null => "Automerge not set" };

    /// <summary>One line naming what the policy produced for a synthetic scenario.</summary>
    public static string Outcome(PolicySimulation simulation) => simulation.Result switch
    {
        PolicyResult.Allowed or PolicyResult.DeferredBySchedule => $"{DependencyLabels.Result(simulation.Result)} · {Automerge(simulation.Effective)}",
        _ => DependencyLabels.Result(simulation.Result),
    };

    public static string Rules(PolicySimulation simulation) =>
        simulation.MatchedRules.Count == 0 ? "No packageRule (repository settings)" : string.Join(", ", simulation.MatchedRules.Select(m => $"#{m.RuleIndex}"));

    public static string Location(DeclaredDependency dep) => dep.Line > 0 ? $"{dep.OwnerFile}:{dep.Line}" : dep.OwnerFile;

    public static string Short(string? sha) => sha is null ? "—" : sha[..Math.Min(12, sha.Length)];

    /// <summary>Distinct declared packages of a repository (for the single-package simulator), in name order.</summary>
    public static IReadOnlyList<DeclaredDependency> Packages(RepositoryDependencyReview repo) =>
        repo.Dependencies.GroupBy(d => (d.PackageName, d.Manager, d.OwnerFile)).Select(g => g.First()).OrderBy(d => d.PackageName, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.OwnerFile).ToList();

    public static string Headline(DependencyReviewResult result)
    {
        var needsReview = result.Repositories.Sum(r => Count(r, DependencyFindingSeverity.NeedsReview));
        var missing = result.Repositories.Where(r => r.Coverage == RenovateCoverage.Missing).Select(r => r.Repository).ToList();
        return $"{result.Repositories.Count} repositor{(result.Repositories.Count == 1 ? "y" : "ies")} reviewed · {needsReview} rule observation{(needsReview == 1 ? "" : "s")} need review"
            + (missing.Count > 0 ? $" · Renovate missing for {string.Join(", ", missing)}" : "");
    }
}
