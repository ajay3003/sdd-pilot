using BirkNext.TestPlanning;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Audit 2026-11-15 PERF-01: Execute &amp; document shows a long run history page by page from the server (newest first, exact total) and says how
/// much of it is shown, instead of silently showing the first 500. A host without paging falls back to the unpaged list.
/// </summary>
public sealed class ManualRunHistoryPagingTests : BunitContext
{
    private static readonly Guid PlanId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private readonly Mock<ITestPlanSuggestionsApiService> _plans = new();
    private readonly Mock<IManualTestRunsApiService> _runs = new();

    private static ManualRunSummary Run(int i) =>
        new(Guid.NewGuid(), PlanId, "tsc-borrow", $"{PlanId:N}/tsc-borrow", $"Borrow a book #{i}", 3, "fp", ManualRunState.Completed, ManualRunOutcome.Passed, "anna", "QA",
            DateTimeOffset.UtcNow.AddMinutes(-i), DateTimeOffset.UtcNow.AddMinutes(-i + 1), 1, 1, 0, 0, 0, 0, 0, 0, 0);

    private void Setup(int total, bool paging = true)
    {
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "library", "Library")).Object);
        _plans.Setup(x => x.ProjectAsync("library", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanWorkspace { ProjectKey = "library", GuideKey = "project:library", CanWrite = true, Drafts = [] }, (string?)null));
        var all = Enumerable.Range(0, total).Select(Run).ToList();
        _runs.Setup(x => x.ListAsync("library", It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((all.Take(5).ToList(), (string?)null));
        if (paging)
            _runs.Setup(x => x.PageAsync("library", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string _, int skip, int take, Guid? _, string? _, CancellationToken _) => (new ManualRunPage([.. all.Skip(skip).Take(take)], total, skip, take), (string?)null));
        Services.AddSingleton(_plans.Object);
        Services.AddSingleton(_runs.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<ManualTestingExecute> Open()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("manual-testing/execute");
        var cut = Render<ManualTestingExecute>();
        cut.WaitForAssertion(() => cut.Find("[aria-busy=false]"));
        return cut;
    }

    [Fact]
    public void A_long_history_shows_its_size_and_loads_older_runs_page_by_page()
    {
        Setup(total: 120);
        var cut = Open();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mte-history-row]").Should().HaveCount(50));
        cut.Find("[data-testid=mte-history-count]").TextContent.Should().Be("Showing 50 of 120 runs, newest first.");

        cut.Find("[data-testid=mte-history-more]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mte-history-row]").Should().HaveCount(100));
        _runs.Verify(x => x.PageAsync("library", 50, 50, null, null, It.IsAny<CancellationToken>()), Times.Once(), "the next page starts after the rows already shown");

        cut.Find("[data-testid=mte-history-more]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mte-history-row]").Should().HaveCount(120));
        cut.Find("[data-testid=mte-history-count]").TextContent.Should().Be("Showing 120 of 120 runs, newest first.");
        cut.FindAll("[data-testid=mte-history-more]").Should().BeEmpty("everything is shown");
        cut.FindAll("[data-testid=mte-history-row]").Select(r => r.GetAttribute("data-run")).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_short_history_has_no_more_button()
    {
        Setup(total: 3);
        var cut = Open();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mte-history-row]").Should().HaveCount(3));
        cut.Find("[data-testid=mte-history-count]").TextContent.Should().Be("Showing 3 of 3 runs, newest first.");
        cut.FindAll("[data-testid=mte-history-more]").Should().BeEmpty();
    }

    [Fact]
    public void Without_paging_the_unpaged_list_is_shown()
    {
        Setup(total: 9, paging: false);
        var cut = Open();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mte-history-row]").Should().HaveCount(5));
        cut.FindAll("[data-testid=mte-history-count]").Should().BeEmpty("no total is claimed when none is known");
    }
}
