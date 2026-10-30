using BirkNext.DeliveryReporting;
using BirkNext.Web.Layout;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Operations;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using DeliveryReportingPage = BirkNext.Web.Pages.DeliveryReporting;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Delivery Management lifecycle: report generation and exports run as operations owned by the application, not by the page. Leaving the page
/// never stops them and their completion never navigates the user elsewhere; the same report is not generated twice at once; an export stays
/// downloadable from the Activity Center; a prepared Azure DevOps import is offered again on return; results keep their project.
/// </summary>
public sealed class DeliveryOperationsTests : BunitContext
{
    private readonly DeliveryReportingPageTests.FakeApi _api = new();
    private readonly BirkNext.Web.Tests.Components.DeliveryAzureDevOpsImportTests.FakeApi _ado = new();

    public DeliveryOperationsTests()
    {
        Services.AddSingleton<IDeliveryReportingApiService>(_api);
        Services.AddSingleton<IDeliveryAzureDevOpsApiService>(_ado);
        Services.AddSingleton(new BackgroundOperationService());
        Services.AddSingleton(new DeliveryReportingSession());
        Services.AddScoped<NavigationSectionState>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private BackgroundOperationService Operations => Services.GetRequiredService<BackgroundOperationService>();
    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();
    private string Location => Nav.ToBaseRelativePath(Nav.Uri);
    private void Leave() => DisposeComponentsAsync().GetAwaiter().GetResult();

    private IRenderedComponent<DeliveryReportingPage> Open(string query, string ready = "[data-testid=dlr-generate-run]")
    {
        Nav.NavigateTo($"delivery-reporting{query}");
        var page = Render<DeliveryReportingPage>();
        page.WaitForAssertion(() => page.Find(ready).HasAttribute("disabled").Should().BeFalse());
        return page;
    }

    [Fact]
    public void Generation_ContinuesAfterLeaving_NeverNavigatesBack_AndKeepsItsProjectAndBaseline()
    {
        _api.GenerateGate = new();
        var page = Open($"?project={DeliveryReportingPageTests.ProjectId}&tab=overview");
        page.Find("[data-testid=dlr-generate-run]").Click();
        var op = Operations.Operations.Single(o => o.Kind == OperationKind.DeliveryReportGeneration);
        op.Status.Should().Be(OperationStatus.Running);
        op.Context.ProjectKey.Should().Be(DeliveryReportingPageTests.ProjectId.ToString());
        op.Context.Baseline.Should().Be("Monday");
        op.Context.Revision.Should().Be("Friday");

        Leave();
        Nav.NavigateTo("dashboard");
        Services.GetRequiredService<NavigationSectionState>().SetView(WorkspaceView.TestLead);
        _api.GenerateGate.SetResult();

        SpinWait.SpinUntil(() => Operations.Find(op.Id)!.Status == OperationStatus.Completed, TimeSpan.FromSeconds(5)).Should().BeTrue();
        Location.Should().Be("dashboard", "a finished generation never pulls the user back to Delivery Reports");
        Operations.Find(op.Id)!.ResultRoute.Should().Contain($"report={DeliveryReportingPageTests.SampleReport().ReportId}");
        Operations.Find(op.Id)!.ResultLabel.Should().Be("Open report 2026-W44");
    }

    [Fact]
    public void ReturningWhileItRuns_ShowsIt_AndPreventsASecondGeneration()
    {
        _api.GenerateGate = new();
        var page = Open($"?project={DeliveryReportingPageTests.ProjectId}&tab=overview");
        page.Find("[data-testid=dlr-generate-run]").Click();
        Leave();

        Nav.NavigateTo($"delivery-reporting?project={DeliveryReportingPageTests.ProjectId}&tab=overview");
        var back = Render<DeliveryReportingPage>();
        back.WaitForAssertion(() => back.Find("[data-testid=dlr-generate-running]").TextContent.Should().Contain("Monday → Friday"));
        back.Find("[data-testid=dlr-generate-run]").HasAttribute("disabled").Should().BeTrue("the same report is already being generated");
        _api.Generated.Should().Be(1);

        _api.GenerateGate.SetResult();
        back.WaitForAssertion(() => back.FindAll("[data-testid=dlr-generate-running]").Should().BeEmpty());
    }

    [Fact]
    public void Export_IsKeptByTheActivityCenter_AndCanBeDownloadedAgain()
    {
        var page = Open($"?project={DeliveryReportingPageTests.ProjectId}&tab=overview");
        page.Find("[data-testid=dlr-export-toggle]").Click();
        page.Find("[data-testid=dlr-export-markdown]").Click();

        page.WaitForAssertion(() => JSInterop.Invocations.Should().Contain(i => i.Identifier == "downloadTextFile"));
        var op = Operations.Operations.Single(o => o.Kind == OperationKind.DeliveryReportExport);
        op.Status.Should().Be(OperationStatus.Completed);
        op.File!.FileName.Should().Be("M2LB-Weekly-Delivery-Report-2026-W44.md");
        page.Markup.Should().Contain("Download of M2LB-Weekly-Delivery-Report-2026-W44.md started", "the browser download is started, not claimed as saved");

        Leave();
        var activity = Render<ActivityCenter>();
        activity.Find("[data-testid=activity-toggle]").Click();
        activity.Find("[data-testid=activity-download]").Click();
        JSInterop.Invocations.Count(i => i.Identifier == "downloadTextFile").Should().Be(2);
        _api.Exports.Should().Be(1, "downloading again does not export again");
    }

    [Fact]
    public void AzureDevOpsPreview_FinishedAfterLeaving_IsOfferedAgainForItsProject()
    {
        var session = Services.GetRequiredService<DeliveryReportingSession>();
        var preview = new DeliveryImportPreview
        {
            StagingId = Guid.NewGuid(), FileName = "M2LB@abc1234.zip", SourceType = DeliverySourceType.AzureDevOps,
            AzureDevOps = new("bufdir", "M2LB", "M2LB", "main", "abc1234def", new DateTimeOffset(2026, 10, 26, 7, 0, 0, TimeSpan.Zero)),
        };
        session.SetPending(DeliveryReportingPageTests.ProjectId, preview, "gap note");
        session.Pending(Guid.NewGuid()).Should().BeNull("a prepared import belongs to its reporting project only");

        var page = Open($"?project={DeliveryReportingPageTests.ProjectId}&tab=snapshots", "[data-testid=dlr-import-open]");
        page.Find("[data-testid=dlr-import-pending]").TextContent.Should().Contain("abc1234");
        page.Find("[data-testid=dlr-import-pending]").Click();
        page.Find("[data-testid=dlr-preview-ado]").TextContent.Should().Be("Azure DevOps · M2LB / M2LB / main");
        page.Find("[data-testid=dlr-preview-ado-note]").TextContent.Should().Be("gap note");
        page.Find("[data-testid=dlr-import-cancel]").Click();
        session.Pending(DeliveryReportingPageTests.ProjectId).Should().BeNull("cancelling the dialog discards the prepared import");
    }

    [Fact]
    public void DeliveryOverview_ShowsTheBacklogOfTheChosenProject_WithDistinctStatuses_AndNoInferredVerified()
    {
        Nav.NavigateTo($"delivery-overview?project={DeliveryReportingPageTests.ProjectId}");
        var page = Render<BirkNext.Web.Pages.DeliveryOverview>();

        page.WaitForAssertion(() => page.FindAll("[data-testid=do-item]").Should().HaveCount(2));
        page.FindAll("[data-testid=do-tile]").Select(t => t.GetAttribute("data-status")).Should().Equal("Planned", "InProgress", "ChecklistComplete", "ImplementationVerified", "QaVerified", "Accepted", "Blocked");
        page.Find("[data-testid=do-tile][data-status=InProgress] .do-tile-count").TextContent.Should().Be("2");
        page.Find("[data-testid=do-tile][data-status=QaVerified] .do-tile-count").TextContent.Should().Be("–");
        page.Find("[data-testid=do-tile][data-status=ImplementationVerified] .do-tile-count").TextContent.Should().Be("–");
        page.Find("[data-testid=do-verified-note]").TextContent.Should().Contain("never verified because its tasks are checked");
        page.Find("[data-testid=do-source]").TextContent.Should().Contain("From report 2026-W44").And.Contain("baseline “Monday”");
        page.Find("[data-testid=do-provenance]").TextContent.Should().Be("ZIP import");
        page.Find("[data-testid=do-open-report]").GetAttribute("href").Should().Contain($"project={DeliveryReportingPageTests.ProjectId}").And.Contain("tab=stories");

        page.Find("[data-testid=do-filter]").Change(nameof(BacklogStatus.Accepted));
        page.FindAll("[data-testid=do-item]").Should().BeEmpty();
        page.Find("[data-testid=do-empty-filter]").Should().NotBeNull();
    }

    [Fact]
    public void PreparedImport_ExpiresWithTheStaging()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var session = new DeliveryReportingSession(clock);
        session.SetPending(Guid.Empty, new DeliveryImportPreview(), null);
        clock.Now += DeliveryReportingSession.PreviewLifetime + TimeSpan.FromMinutes(1);
        session.Pending(Guid.Empty).Should().BeNull();
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
