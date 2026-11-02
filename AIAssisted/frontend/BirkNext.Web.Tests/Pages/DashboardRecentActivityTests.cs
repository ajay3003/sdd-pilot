using BirkNext.ProjectActivity;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Operations;
using BirkNext.Web.Services.ProjectActivity;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Dashboard → Recent activity is the logical application's persistent history (read by its stable id from the backend), never this browser
/// session's operations; running operations are a separate "Current operations" list. The Dashboard names the application, not the package
/// imported last. An import's badge is Imported/Attention — never a success check.
/// </summary>
public sealed class DashboardRecentActivityTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly Mock<IProjectActivityApiService> _activity = new();
    private readonly BackgroundOperationService _operations = new();

    private IRenderedComponent<Dashboard> Render(string shownPackage)
    {
        var workspace = WorkspaceSnapshots.Loaded(shownPackage, "import:abc", shownPackage, [.. CurrentWorkspaceSnapshot.WorkflowRoles]);
        var readiness = new Mock<IWorkflowReadinessService>();
        readiness.Setup(s => s.GetReadinessAsync()).ReturnsAsync(ReviewDecisionSemanticsTests.Readiness(workspace, []));
        Services.AddSingleton(readiness.Object);
        Services.AddSingleton(WorkspaceSnapshots.Projection(workspace).Object);
        Services.AddSingleton(new Mock<IReportExportService>().Object);
        Services.AddSingleton<IWorkspaceSessionService>(_repository);
        Services.AddSingleton(_repository);
        Services.AddSingleton(new Mock<IDashboardSnapshotService>().Object);
        Services.AddSingleton(new RuntimeReviewSessionService());
        Services.AddSingleton(new QualityReviewSessionService());
        Services.AddSingleton(_activity.Object);
        Services.AddSingleton(_operations);
        Services.AddScoped<ProjectActivityRecorder>();
        return Render<Dashboard>();
    }

    private string App(string name = "M2LB")
    {
        var lifecycle = _repository.SddLifecycle;
        var app = new SddApplicationContext { Id = "app-m2lb", Name = name, NameBasis = "PrimarySource" };
        lifecycle.Applications.Add(app);
        lifecycle.CurrentApplicationId = app.Id;
        lifecycle.ProjectImports.Add(new SddProjectImportRecord { ImportId = "import-m2lb", ProjectName = "M2LB", ApplicationId = app.Id });
        lifecycle.ProjectImports.Add(new SddProjectImportRecord { ImportId = "import-common", ProjectName = "M2LB.Common", ApplicationId = app.Id });
        lifecycle.CurrentProjectImportId = "import-common";
        return app.Id;
    }

    private static ProjectActivityItem Item(string title, ProjectActivityStatus status, ProjectActivityType type, int minutesAgo, string? summary = null) => new()
    {
        Id = Guid.NewGuid(), ProjectContextId = "app-m2lb", Title = title, Status = status, Type = type, Category = ProjectActivityCategories.Of(type),
        Summary = summary, Route = "project-import", OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
    };

    [Fact]
    public void ImportsOfTheApplication_AreListedFromPersistentActivity_ByItsStableId_NewestFirst()
    {
        var id = App();
        _activity.Setup(a => a.ListAsync(id, 10, It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            Item("Imported M2LB.Common", ProjectActivityStatus.Attention, ProjectActivityType.ImportCompleted, 1, "Supporting/shared source · 1 document · Source Analysis Partial · 1 item needs attention"),
            Item("Imported M2LB", ProjectActivityStatus.Imported, ProjectActivityType.ImportCompleted, 7, "Primary application source · 228 documents"),
        ]);

        var cut = Render("M2LB.Common");

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=db-activity]").Should().HaveCount(2));
        cut.Find("[data-testid=db-workspace-name]").TextContent.Should().Be("M2LB", "the logical application, not the package whose documents are shown");
        cut.Find("[data-testid=db-workspace-packages]").TextContent.Should().Contain("2 imported evidence packages");
        var rows = cut.FindAll("[data-testid=db-activity]");
        rows[0].TextContent.Should().Contain("Imported M2LB.Common").And.Contain("Supporting/shared source");
        rows[1].TextContent.Should().Contain("Imported M2LB");
        rows.Select(r => r.QuerySelector("[data-testid=db-activity-badge]")!.TextContent).Should().Equal("Attention", "Imported");
        cut.FindAll("[data-testid=db-recent-activity] .db-pill.is-good").Should().BeEmpty("an import is never shown as a QA success");
        rows[0].QuerySelector("[data-testid=db-activity-link]")!.TextContent.Should().StartWith("View import");
        rows[0].QuerySelector("time")!.GetAttribute("datetime").Should().NotBeNullOrEmpty();
        cut.Markup.Should().NotContain("No tracked operation has run");
        _activity.Verify(a => a.ListAsync("M2LB", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never(), "activity is never keyed by a display name");
    }

    [Fact]
    public void WithoutActivity_TheEmptyStateExplainsWhatWillAppear_AndRunningOperationsAreSeparate()
    {
        var id = App();
        _activity.Setup(a => a.ListAsync(id, 10, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var gate = new TaskCompletionSource<OperationOutcome>();
        _operations.Start(new OperationRequest(OperationKind.ProjectImport, "Importing M2LB", new OperationContext("M2LB"), "project-import"), (_, _) => gate.Task);

        var cut = Render("M2LB");

        cut.WaitForAssertion(() => cut.Find("[data-testid=db-activity-none]").TextContent.Should()
            .Contain("No recent activity yet.").And.Contain("Imports, completed reviews, configuration changes and other significant project activity will appear here."));
        cut.Find("[data-testid=db-current-operation]").TextContent.Should().Contain("Importing M2LB");
        cut.FindAll("[data-testid=db-activity]").Should().BeEmpty("a running operation is not project history");
        gate.SetResult(OperationOutcome.Success());
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=db-current-operations]").Should().BeEmpty());
    }

    [Fact]
    public void AnEventRecordedWhileTheDashboardIsOpen_AppearsWithoutNavigation()
    {
        var id = App();
        var items = new List<ProjectActivityItem>();
        _activity.Setup(a => a.ListAsync(id, 10, It.IsAny<CancellationToken>())).ReturnsAsync(() => [.. items]);
        _activity.Setup(a => a.RecordAsync(It.IsAny<ProjectActivityRecordRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProjectActivityRecordRequest r, CancellationToken _) =>
            {
                var item = Item(r.Title, r.Status, r.Type, 0);
                items.Insert(0, item);
                return item;
            });
        var cut = Render("M2LB");
        cut.WaitForAssertion(() => cut.Find("[data-testid=db-activity-none]"));

        var recorder = Services.GetRequiredService<ProjectActivityRecorder>();
        cut.InvokeAsync(() => recorder.RecordAsync(new ProjectActivityRecordRequest
        {
            Type = ProjectActivityType.PerformanceRunFailed, Status = ProjectActivityStatus.Failed, SourceModule = "Performance Test Review",
            Title = "Performance Test failed", IdempotencyKey = "run-1",
        }));

        cut.WaitForAssertion(() => cut.Find("[data-testid=db-activity]").TextContent.Should().Contain("Performance Test failed").And.Contain("Failed"));
        _activity.Verify(a => a.RecordAsync(It.Is<ProjectActivityRecordRequest>(r => r.ProjectContextId == id && r.ProjectName == "M2LB"), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void ASampleProject_HasNoImportedApplicationActivity()
    {
        App();
        _repository.ProjectName = "autorisasjon";
        var cut = Render("Autorisasjon");
        cut.WaitForAssertion(() => cut.Find("[data-testid=db-recent-activity]"));
        _activity.Verify(a => a.ListAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());
        Services.GetRequiredService<ProjectActivityRecorder>().CurrentContextId.Should().BeNull();
    }
}
