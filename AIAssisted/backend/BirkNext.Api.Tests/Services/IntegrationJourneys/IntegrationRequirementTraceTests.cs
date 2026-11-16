using BirkNext.Api.Data.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationSuites;
using BirkNext.Integrations;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationJourneys;

/// <summary>
/// The integration side of the shared requirements trace view: only confirmed requirement ↔ journey links of the project appear, a retired
/// journey no longer counts, a revised journey says so, and nothing is added to the manual execution counts.
/// </summary>
public sealed class IntegrationRequirementTraceTests
{
    private const string Project = "import:lendo";
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly string _database = $"integration-trace-{Guid.NewGuid():N}";
    private IntegrationJourneysDbContext Db() => new(new DbContextOptionsBuilder<IntegrationJourneysDbContext>().UseInMemoryDatabase(_database).Options);

    private static readonly ProjectRequirementCatalog Catalog = new()
    {
        ProjectKey = Project, Requirements = [new("FR-001", "Register a loan.", "spec.md"), new("FR-002", "Email a receipt.", "spec.md"), new("FR-003", "Block a member.", "spec.md")],
    };

    private async Task<DeclaredIntegrationJourney> Declare(string name) =>
        (await new DeclaredIntegrationJourneyStore(Db()).DeclareAsync(new DeclaredIntegrationJourneyRequest { ProjectKey = Project, DisplayName = name, Components = ["Web", "Api"], DeclaredBy = "Kari" }, Now)).Journey!;

    [Fact]
    public async Task Only_confirmed_links_of_current_journeys_appear_apart_from_the_manual_counts()
    {
        var loans = await Declare("Loan flow");
        var retired = await Declare("Old flow");
        var links = new JourneyRequirementLinkStore(Db());
        await links.LinkAsync(Project, loans.JourneyId, new("FR-001", "spec.md", Catalog, "Kari"), "u", Now, default);
        await links.LinkAsync(Project, loans.JourneyId, new("FR-002", "spec.md", Catalog, "Auto", Proposed: true), "u", Now, default);
        await links.LinkAsync(Project, retired.JourneyId, new("FR-003", "spec.md", Catalog, "Kari"), "u", Now, default);
        await new DeclaredIntegrationJourneyStore(Db()).RetireAsync(Project, retired.JourneyId, "Kari", Now);
        await new DeclaredIntegrationJourneyStore(Db()).ReviseAsync(Project, loans.JourneyId, new DeclaredIntegrationJourneyRequest
            { ProjectKey = Project, DisplayName = "Loan flow", Components = ["Web", "Api", "Queue"], DeclaredBy = "Ola", ExpectedRevision = 1 }, Now);

        var view = await IntegrationRequirementTrace.WithIntegrationAsync(new PreparedCaseTraceView { ProjectKey = Project, ExecutedCases = 2, RequirementsExecuted = 1 },
            Project, links, new DeclaredIntegrationJourneyStore(Db()), suites: null, default);

        view.Integration.Should().ContainSingle("the proposal is undecided and the retired journey no longer counts");
        view.Integration[0].Should().Match<RequirementIntegrationTrace>(t => t.RequirementId == "FR-001" && t.JourneyName == "Loan flow" && t.LinkedAtJourneyRevision == 1
            && t.CurrentJourneyRevision == 2 && t.Suites.Count == 0);
        view.ExecutedCases.Should().Be(2, "integration links never change the manual execution counts");
        view.RequirementsExecuted.Should().Be(1);
        (await IntegrationRequirementTrace.WithIntegrationAsync(new PreparedCaseTraceView(), "import:other", links, new DeclaredIntegrationJourneyStore(Db()), null, default))
            .Integration.Should().BeEmpty("another project sees none of them");
    }

    [Fact]
    public void A_suite_outcome_is_labelled_from_recorded_runs_and_not_executed_by_default()
    {
        IntegrationRequirementTrace.Label(BirkNext.IntegrationSuites.SuiteExecutionOutcome.NotExecuted).Should().Be(PreparedCaseTraceView.NotExecuted);
        IntegrationRequirementTrace.Label(BirkNext.IntegrationSuites.SuiteExecutionOutcome.Failed).Should().Be("Failed");
        IntegrationRequirementTrace.Label(BirkNext.IntegrationSuites.SuiteExecutionOutcome.Executed).Should().Be("Executed, not verified");
    }
}
