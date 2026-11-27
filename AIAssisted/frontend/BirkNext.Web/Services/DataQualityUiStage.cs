using BirkNext.DataQuality;
using BirkNext.TestPlanning;
using O = BirkNext.Web.Services.ManualTestingOverviewPresentation;

namespace BirkNext.Web.Services;

/// <summary>
/// The browser stage of a Data Quality run (API → UI, 2026-11-27): the PRODUCTION dashboard calculation (<see cref="ManualTestingOverviewPresentation"/>,
/// the same code Testoversikt runs) over the same overview API response, for every figure the server's independent reference expects.
/// A figure the dashboard does not compute (for example E2E candidates, which live in Finn og forstå) is null: NOT VERIFIED, never PASS.
/// </summary>
public static class DataQualityUiStage
{
    public sealed record Comparison(DataQualityKpiExpectation Kpi, int? Actual, DataQualityStatus Status, string Note);

    public static int? Actual(ManualTestingOverview overview, string id)
    {
        var slice = O.Of(overview, O.Scope.Project);
        var figures = O.Compute(slice, overview.Plans);
        switch (id)
        {
            case "metric.scenarios": return figures.Scenarios;
            case "metric.testCases": return figures.Cases;
            case "metric.approved": return figures.Approved;
            case "metric.ready": return figures.Ready;
            case "metric.covered": return figures.Covered;
            case "e2e.documented": return O.TileCount(slice, new DashboardKpiTile(DashboardKpiTiles.DefaultId, null, KpiTileDimension.TestLevel, nameof(ScenarioTestLevel.EndToEnd)));
            case "plans": return overview.Plans.Count;
        }
        if (id.StartsWith("gaps.", StringComparison.Ordinal))
        {
            var key = id["gaps.".Length..];
            return O.Gaps(slice).FirstOrDefault(b => b.Key == key)?.Value ?? 0;
        }
        if (id.StartsWith("module.scenarios.", StringComparison.Ordinal) || id.StartsWith("module.requirements.", StringComparison.Ordinal))
        {
            var requirements = id.StartsWith("module.requirements.", StringComparison.Ordinal);
            var module = id[(requirements ? "module.requirements." : "module.scenarios.").Length..];
            var row = O.Rows(slice, O.Level.Project).FirstOrDefault(r => r.Key == module);
            return row is null ? 0 : requirements ? row.Requirements : row.Scenarios;
        }
        return null;
    }

    public static List<Comparison> Compare(ManualTestingOverview overview, IEnumerable<DataQualityKpiExpectation> kpis) =>
    [.. kpis.Select(k =>
    {
        var actual = Actual(overview, k.Id);
        if (actual is null) return new Comparison(k, null, DataQualityStatus.NotVerified, "Dashbordet regner ikke ut dette tallet (vises i Finn og forstå); ikke kontrollert her.");
        if (k.Expected is null) return new Comparison(k, actual, DataQualityStatus.NotVerified, "Referansen har ingen forventet verdi.");
        return actual == k.Expected
            ? new Comparison(k, actual, DataQualityStatus.Pass, "Dashbordet viser referansens verdi.")
            : new Comparison(k, actual, DataQualityStatus.Fail, $"Dashbordet viser {actual}, referansen forventer {k.Expected} (differanse {actual - k.Expected:+0;-0}).");
    })];
}
