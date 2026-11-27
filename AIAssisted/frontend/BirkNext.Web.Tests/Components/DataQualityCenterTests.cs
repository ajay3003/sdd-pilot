using BirkNext.DataQuality;
using BirkNext.TestPlanning;
using BirkNext.Web.Components.DataQuality;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// System Settings → Data Quality (2026-11-27): nothing runs on open; access is respected; a run shows progress, can be cancelled, and ends with
/// PASS/FAIL/WARNING/INCONCLUSIVE/NOT VERIFIED per check with the exact records; the browser stage computes the production dashboard values and
/// catches a count that is wrong in both API and display; reports download; identities are filterable.
/// </summary>
public sealed class DataQualityCenterTests : BunitContext
{
    private const string Project = "import:import-dq0000000000001";
    private readonly Mock<IDataQualityApiService> _api = new();
    private readonly Mock<ITestPlanSuggestionsApiService> _plans = new();

    public DataQualityCenterTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(_plans.Object);
        _api.Setup(x => x.ProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(([new DataQualityProjectOption(Project, Project,
            [new DataQualityBaseline { Revision = 2, Label = "Revisjon 2 · 2026-11-27 10:00" }])], (string?)null, false));
        _api.Setup(x => x.RecentAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new List<DataQualityRunSummary>(), (string?)null));
    }

    private static ManualTestingOverview Overview() => new()
    {
        ProjectKey = Project, ScenariosAvailable = true,
        Specifications = [new OverviewSpecification("Library/specs/001-loans/spec.md", "Library", "001-loans",
            [new("s1", "k", "US1", "S", 1, "P1", "A"), new("s2", "k", "US1", "S", 2, "P1", "B"), new("r1", null, null, null, null, null, "FR-002: x", RequirementOnly: true)])],
        Cases = [new OverviewCase { PlanId = Guid.NewGuid(), CaseId = "c1", SpecificationPath = "Library/specs/001-loans/spec.md", ScenarioIdentity = "s1", Review = TestCaseReviewState.Accepted }],
    };

    private static DataQualityRun Completed() => new()
    {
        RunId = "dq-1", ProjectKey = Project, State = DataQualityRunState.Completed, StartedAt = DateTimeOffset.UtcNow.AddSeconds(-3), FinishedAt = DateTimeOffset.UtcNow,
        Categories = [DataQualityCategory.Dashboard, DataQualityCategory.RequirementIdentity],
        Report = new DataQualityReport
        {
            RunId = "dq-1", ProjectKey = Project, Baseline = new DataQualityBaseline { Label = "Revisjon 2" }, FinishedAt = DateTimeOffset.UtcNow,
            Checks =
            [
                new DataQualityCheck { Id = "dashboard.acceptance", Category = DataQualityCategory.Dashboard, Stage = DataQualityStage.StoredToApi, Metric = "Akseptansescenarioer", Expected = "3", Actual = "2",
                    Difference = "-1", Status = DataQualityStatus.Fail, Cause = DataQualityCause.IncorrectAggregation, Explanation = "1 avvik.", Contract = "kontrakt",
                    Records = [new DataQualityRecord("Mangler i API", "Library/specs/001-loans/spec.md#US1#3", "Library/specs/001-loans/spec.md", "C")] },
                new DataQualityCheck { Id = "source.archive", Category = DataQualityCategory.SourceImport, Stage = DataQualityStage.SourceToStored, Metric = "Opprinnelig arkiv",
                    Status = DataQualityStatus.NotVerified, Cause = DataQualityCause.SourceUnavailable, Explanation = "SOURCE UNAVAILABLE" },
                new DataQualityCheck { Id = "dashboard.cases", Category = DataQualityCategory.Dashboard, Stage = DataQualityStage.StoredToApi, Metric = "Testtilfeller", Expected = "1", Actual = "1", Status = DataQualityStatus.Pass },
            ],
            Kpis =
            [
                new("metric.testCases", "Testtilfeller", "Prosjekt", 1, "kontrakt", []),
                new("metric.scenarios", "Scenarioforslag", "Prosjekt", 4, "kontrakt", []),
                new("e2e.candidates", "E2E-kandidater", "Prosjekt", 3, "kontrakt", []),
            ],
            Identities =
            [
                new IdentityDryRunRow { V1 = "tss-a", V2 = "tsr2-a", RequirementId = "FR-009", SpecificationPath = "M/specs/001-a/spec.md", DocumentsWithSameV1 = 2, Resolution = IdentityResolution.Ambiguous },
                new IdentityDryRunRow { V1 = "tss-a", V2 = "tsr2-b", RequirementId = "FR-009", SpecificationPath = "M/specs/002-b/spec.md", DocumentsWithSameV1 = 2, Resolution = IdentityResolution.Unique },
                new IdentityDryRunRow { V1 = "tss-c", V2 = "tsr2-c", RequirementId = "FR-010", SpecificationPath = "M/specs/001-a/spec.md", DocumentsWithSameV1 = 1, Resolution = IdentityResolution.NotAffected },
            ],
        },
    };

    private IRenderedComponent<DataQualityCenter> Open() => Render<DataQualityCenter>(p => p.Add(x => x.PollInterval, TimeSpan.Zero));

    [Fact]
    public void DQ01_opening_the_section_runs_nothing_and_offers_the_three_categories_and_the_baseline()
    {
        var cut = Open();
        cut.FindAll("[data-testid=dq-category]").Select(c => c.GetAttribute("data-category")).Should().Equal("Dashboard", "RequirementIdentity", "SourceImport");
        cut.Find("[data-testid=dq-baseline]").TextContent.Should().Contain("Revisjon 2");
        cut.Find("[data-testid=dq-start]").TextContent.Should().Be("Start kontroll");
        _api.Verify(x => x.StartAsync(It.IsAny<DataQualityRunRequest>(), It.IsAny<CancellationToken>()), Times.Never, "an audit is only ever user-initiated");
    }

    [Fact]
    public void DQ02_without_permission_the_section_says_so_and_offers_no_run_controls()
    {
        _api.Setup(x => x.ProjectsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(((List<DataQualityProjectOption>?)null, "nei", true));
        var cut = Open();
        cut.Find("[data-testid=dq-forbidden]").TextContent.Should().Contain("Du har ikke tilgang");
        cut.FindAll("[data-testid=dq-start]").Should().BeEmpty();
    }

    [Fact]
    public void DQ03_a_run_polls_to_completion_and_shows_every_status_with_counts_and_exact_records()
    {
        var running = Completed() with { State = DataQualityRunState.Running, Report = null, FinishedAt = null, Progress = new DataQualityProgress { Phase = "Dashbord", ChecksCompleted = 2 } };
        _api.Setup(x => x.StartAsync(It.IsAny<DataQualityRunRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((running, (string?)null));
        _api.Setup(x => x.GetAsync("dq-1", It.IsAny<CancellationToken>())).ReturnsAsync((Completed(), (string?)null));
        _plans.Setup(x => x.OverviewAsync(Project, It.IsAny<CancellationToken>())).ReturnsAsync((Overview(), (string?)null));
        var cut = Open();
        cut.Find("[data-testid=dq-start]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=dq-summary]"));
        _api.Verify(x => x.StartAsync(It.Is<DataQualityRunRequest>(r => r.ProjectKey == Project && r.Categories.Count == 3), It.IsAny<CancellationToken>()));
        cut.FindAll("[data-testid=dq-count]").Select(c => c.GetAttribute("data-status")).Should().Equal("Pass", "Fail", "Warning", "Inconclusive", "NotVerified");
        cut.Find("[data-testid=dq-count][data-status=Fail] strong").TextContent.Should().Be("1");
        var fail = cut.Find("[data-testid=dq-check][data-id='dashboard.acceptance']");
        fail.TextContent.Should().Contain("FAIL").And.Contain("3").And.Contain("2").And.Contain("-1");
        cut.Find("[data-testid=dq-check][data-id='source.archive']").TextContent.Should().Contain("NOT VERIFIED", "a check that could not run is never PASS");
        fail.QuerySelector("[data-testid=dq-check-toggle]")!.Click();
        cut.Find("[data-testid=dq-check-detail]").TextContent.Should().Contain("feil aggregering").And.Contain("kontrakt");
        cut.Find("[data-testid=dq-record]").TextContent.Should().Contain("Mangler i API").And.Contain("Library/specs/001-loans/spec.md#US1#3");
    }

    [Fact]
    public void DQ04_the_browser_stage_uses_the_production_dashboard_code_and_catches_a_count_wrong_in_API_and_display()
    {
        _api.Setup(x => x.RecentAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new List<DataQualityRunSummary> { new("dq-1", Project, DataQualityRunState.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 1, 0, 0, 1, "Revisjon 2") }, (string?)null));
        _api.Setup(x => x.GetAsync("dq-1", It.IsAny<CancellationToken>())).ReturnsAsync((Completed(), (string?)null));
        _plans.Setup(x => x.OverviewAsync(Project, It.IsAny<CancellationToken>())).ReturnsAsync((Overview(), (string?)null));
        List<DataQualityUiValue>? sent = null;
        _api.Setup(x => x.UiValuesAsync("dq-1", It.IsAny<List<DataQualityUiValue>>(), It.IsAny<CancellationToken>()))
            .Callback<string, List<DataQualityUiValue>, CancellationToken>((_, v, _) => sent = v).ReturnsAsync(((DataQualityRun?)null, (string?)null));
        var cut = Open();
        cut.Find("[data-testid=dq-recent-run] button").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=dq-ui-row]").Should().HaveCount(3));
        cut.Find("[data-testid=dq-ui-row][data-id='metric.testCases']").GetAttribute("data-status").Should().Be("Pass");
        cut.Find("[data-testid=dq-ui-row][data-id='metric.scenarios']").GetAttribute("data-status").Should().Be("Fail",
            "the dashboard shows 3 rows from the API, the reference expects 4 from the stored records");
        cut.Find("[data-testid=dq-ui-row][data-id='e2e.candidates']").GetAttribute("data-status").Should().Be("NotVerified", "the dashboard does not compute E2E candidates");
        sent.Should().NotBeNull();
        sent!.Single(v => v.Id == "metric.scenarios").Actual.Should().Be(3);
    }

    [Fact]
    public void DQ05_a_running_audit_can_be_cancelled_and_shows_its_phase_counts_and_elapsed_time()
    {
        var running = Completed() with { State = DataQualityRunState.Running, Report = null, FinishedAt = null, Progress = new DataQualityProgress { Phase = "Kravidentiteter", ChecksCompleted = 4, ChecksFailed = 1 } };
        _api.Setup(x => x.StartAsync(It.IsAny<DataQualityRunRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((running, (string?)null));
        _api.Setup(x => x.GetAsync("dq-1", It.IsAny<CancellationToken>())).ReturnsAsync((running, (string?)null));
        _api.Setup(x => x.CancelAsync("dq-1", It.IsAny<CancellationToken>())).ReturnsAsync((running with { State = DataQualityRunState.Cancelled, Error = "Avbrutt av brukeren." }, (string?)null));
        var cut = Render<DataQualityCenter>(p => p.Add(x => x.PollInterval, TimeSpan.FromMinutes(5)));
        cut.Find("[data-testid=dq-start]").Click();

        cut.Find("[data-testid=dq-progress]").TextContent.Should().Contain("Kjører").And.Contain("Kravidentiteter").And.Contain("4 kontroller fullført").And.Contain("1 feilet").And.Contain(" s");
        cut.Find("[data-testid=dq-start]").HasAttribute("disabled").Should().BeTrue("one run at a time");
        cut.Find("[data-testid=dq-cancel]").Click();
        cut.Find("[data-testid=dq-progress]").GetAttribute("data-state").Should().Be("Cancelled");
        cut.FindAll("[data-testid=dq-summary]").Should().BeEmpty("a cancelled run has no results that could pass");
    }

    [Fact]
    public void DQ06_identities_list_only_affected_requirements_and_filter_by_resolution()
    {
        _api.Setup(x => x.RecentAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new List<DataQualityRunSummary> { new("dq-1", Project, DataQualityRunState.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 1, 0, 0, 1, "") }, (string?)null));
        _api.Setup(x => x.GetAsync("dq-1", It.IsAny<CancellationToken>())).ReturnsAsync((Completed(), (string?)null));
        var cut = Open();
        cut.Find("[data-testid=dq-recent-run] button").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=dq-identity]").Should().HaveCount(2, "a requirement whose v1 identity is unique is not affected"));
        cut.Find("[data-testid=dq-identity-count]").TextContent.Should().Contain("2 berørt").And.Contain("1 ikke berørt");
        cut.Find("[data-testid=dq-filter-resolution]").Change(nameof(IdentityResolution.Ambiguous));
        cut.FindAll("[data-testid=dq-identity]").Should().ContainSingle().Which.TextContent.Should().Contain("AMBIGUOUS").And.Contain("tsr2-a");
    }

    [Fact]
    public void DQ07_status_filter_and_search_narrow_the_checks_and_reports_download_as_files()
    {
        _api.Setup(x => x.RecentAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new List<DataQualityRunSummary> { new("dq-1", Project, DataQualityRunState.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 1, 0, 0, 1, "") }, (string?)null));
        _api.Setup(x => x.GetAsync("dq-1", It.IsAny<CancellationToken>())).ReturnsAsync((Completed(), (string?)null));
        _api.Setup(x => x.ReportAsync("dq-1", "md", It.IsAny<CancellationToken>())).ReturnsAsync(("# BirkNext Data Quality", (string?)null));
        var cut = Open();
        cut.Find("[data-testid=dq-recent-run] button").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=dq-check]").Should().HaveCount(3));
        cut.Find("[data-testid=dq-filter-status]").Change(nameof(DataQualityStatus.Fail));
        cut.FindAll("[data-testid=dq-check]").Should().ContainSingle().Which.GetAttribute("data-id").Should().Be("dashboard.acceptance");
        cut.Find("[data-testid=dq-filter-status]").Change("");
        cut.Find("[data-testid=dq-search]").Input("arkiv");
        cut.FindAll("[data-testid=dq-check]").Should().ContainSingle().Which.GetAttribute("data-id").Should().Be("source.archive");
        cut.Find("[data-testid=dq-download-md]").Click();
        JSInterop.VerifyInvoke("downloadTextFile").Arguments.Should().Equal("birknext-data-quality-dq-1.md", "# BirkNext Data Quality", "text/markdown;charset=utf-8");
    }

    [Fact]
    public void DQ08_the_browser_stage_maps_each_expected_figure_to_the_production_dashboard_value()
    {
        var o = Overview();
        DataQualityUiStage.Actual(o, "metric.scenarios").Should().Be(3);
        DataQualityUiStage.Actual(o, "metric.testCases").Should().Be(1);
        DataQualityUiStage.Actual(o, "metric.approved").Should().Be(1);
        DataQualityUiStage.Actual(o, "metric.covered").Should().Be(1);
        DataQualityUiStage.Actual(o, "module.scenarios.Library").Should().Be(2, "decision 3: requirements are not scenarios in the module table");
        DataQualityUiStage.Actual(o, "module.requirements.Library").Should().Be(1);
        DataQualityUiStage.Actual(o, "gaps.P1").Should().Be(1);
        DataQualityUiStage.Actual(o, "e2e.candidates").Should().BeNull();
    }
}
