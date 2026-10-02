namespace BirkNext.Web.Services;

/// <summary>
/// Dashboard quality vs coverage. Quality is averaged over ASSESSED areas only; an area without evidence (or a report built with nothing to
/// assess) is a coverage gap, never a 0 in the average. There is no global score over unassessed areas.
/// </summary>
public static class DashboardQualityAggregate
{
    public sealed record Result(double? Quality, int Assessed, int Total)
    {
        public string CoverageLabel => $"{Assessed} of {Total} areas assessed";
    }

    public static Result Compute(IReadOnlyCollection<(bool Assessed, double Score)> areas)
    {
        var assessed = areas.Where(a => a.Assessed).Select(a => a.Score).ToList();
        return new Result(assessed.Count == 0 ? null : assessed.Average(), assessed.Count, areas.Count);
    }

    /// <summary>A QA result counts only when a specification was there to assess (otherwise its 0 means "insufficient data").</summary>
    public static bool QaAssessed(bool hasData, bool sessionResult, bool workspaceHasSpecification, bool? auditHasSpecification, bool? readinessHasSpecification) =>
        hasData && (sessionResult ? workspaceHasSpecification : auditHasSpecification ?? readinessHasSpecification ?? false);
}
