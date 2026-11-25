using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// «Testgjennomføring» on a case (v1.0 acceptance, 2026-11-25): results fetched from Azure DevOps and runs recorded in BirkNext are two separate
/// lists, and a case that has both shows both. Before the fix, any Azure DevOps result hid every BirkNext run of the case.
/// </summary>
public sealed partial class ManualTestingPlanTests
{
    [Fact]
    public void A_case_of_another_specification_than_the_one_selected_is_never_shown_as_Kilde_mangler_and_the_plan_says_where_it_comes_from()
    {
        // 2026-11-25: a SkoleService plan opened while Frontend is selected in Finn og forstå showed «Testplanen hører til en annen spesifikasjon»,
        // «Kildegrunnlaget er endret … fordi kilden mangler» and «Kilde mangler» on every case. A saved case is resolved from its own source.
        var skole = new BirkNext.TestCoverage.CoverageUserStory { Key = "skole#US2", StoryId = "US2", Title = "US2", SpecificationPath = "SkoleService/specs/001-skoleservice/spec.md" };
        var c1 = Case("c1", "Submit a valid report", story: skole) with
        {
            SourceState = CaseSourceState.NotCompared, SourceStateReason = "Ikke sammenlignet: spesifikasjonen testtilfellet kommer fra er ikke lest for gjeldende kildeversjon.",
        };
        Setup(Plan(c1));
        var cut = Open();

        cut.Find("[data-testid=mtp-plan-source]").TextContent.Should().Be("Inneholder testtilfeller fra SkoleService (001-skoleservice).");
        cut.FindAll("[data-testid=mtp-scope-differs]").Should().BeEmpty("a saved plan is independent of the selected specification");
        cut.Markup.Should().NotContain("Kilde mangler");
        BirkNext.Web.Services.PlanPreparePresentation.SourceLabel(c1).Should().BeNull();
        BirkNext.Web.Services.PlanSourcePresentation.CaseState(c1)!.Text.Should().Be("Ikke sammenlignet");
        BirkNext.Web.Services.PlanSourcePresentation.CaseState(c1 with { SourceState = CaseSourceState.Removed })!.Text.Should().Be("Utgått scenario", "a real removal is still shown");
    }

    [Fact]
    public void V1_a_case_with_Azure_DevOps_results_still_lists_its_BirkNext_runs()
    {
        var c1 = Case("c1", "Submit a valid report");
        Setup(Plan(c1));
        var runs = new Mock<IManualTestRunsApiService>();
        runs.Setup(x => x.AvailabilityAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        runs.Setup(x => x.ListAsync("library", PlanId, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(([
            new ManualRunSummary(Guid.NewGuid(), PlanId, "c1", "TC-1", "Submit a valid report", 3, ManualRunRules.CaseFingerprint(c1), ManualRunState.Completed,
                ManualRunOutcome.Failed, "anna", "QA", DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, 2, 1, 1, 0, 0, 0, 0, 0, 0),
        ], (string?)null));
        Services.AddSingleton(runs.Object);
        var ado = new Mock<IAdoTestPlansApiService>();
        ado.Setup(x => x.ResultsAsync("library", PlanId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(([new AdoCaseResult { CaseId = "c1", Outcome = "Passed", FetchedAt = DateTimeOffset.UtcNow, Current = true }], (string?)null));
        Services.AddSingleton(ado.Object);

        var cut = Open();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtp-ado-result]").Should().ContainSingle());
        var birknext = cut.FindAll("[data-testid=mtp-run]");
        birknext.Should().ContainSingle("a BirkNext run is never hidden by an Azure DevOps result");
        birknext[0].TextContent.Should().Contain("Gjennomført i BirkNext");
        cut.FindAll("[data-testid=mtp-runs-none]").Should().BeEmpty();
    }
}
