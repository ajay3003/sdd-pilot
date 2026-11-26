using BirkNext.ProjectImport;
using BirkNext.TaskAlignment;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Task Alignment results survive a browser reload: a completed analysis is persisted, and a new session (the reload) restores the latest run of
/// exactly this project and Tasks document — Current when the fingerprints match, Stale when the document changed. A run of another project or
/// of another Tasks document with the same file name is never reused, and a failed save is shown and never presented as saved.
/// </summary>
public sealed class TaskAlignmentPersistenceTests : IDisposable
{
    private const string Spec = ProjectImportActivationTests.Spec;
    private const string Tasks = "# Tasks: Cart\n\n## Phase 1: Setup\n\n- [ ] T001 Create project\n- [ ] T002 Store carts for FR-001\n";

    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ArtifactExplorerContext _explorers;
    private readonly TaskAlignmentRunApiFake _api = new();
    private readonly List<BunitContext> _contexts = [];

    public TaskAlignmentPersistenceTests()
    {
        _samples.Repository = _workspace;
        _explorers = new ArtifactExplorerContext(_workspace, _samples, _samples);
        var preview = ProjectImportActivationTests.Preview(new string('e', 64), false, ("shop/specs/001/spec.md", Spec), ("shop/specs/001/tasks.md", Tasks));
        new ProjectImportActivation(_workspace).Activate(preview, ProjectImportArtifactDiscovery.From(preview),
            ProjectImportActivationTests.Commit(preview, ProjectImportSourceState.NotDetected));
        _explorers.SelectPackAsync("001").GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        foreach (var context in _contexts) context.Dispose();
    }

    /// <summary>One browser session: a fresh session service (nothing in memory) over the same backend store and workspace.</summary>
    private (IRenderedComponent<TaskToSpecAlignment> Page, TaskAlignmentSessionService Session) OpenPage(bool withApi = true)
    {
        var context = new BunitContext();
        _contexts.Add(context);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var session = new TaskAlignmentSessionService();
        context.Services.AddSingleton<IArtifactExplorerContext>(_explorers);
        context.Services.AddSingleton(session);
        context.Services.AddSingleton<IArtifactParserService, ArtifactParserService>();
        context.Services.AddSingleton<IConstitutionAnalysisService, ConstitutionAnalysisService>();
        context.Services.AddSingleton<IPlanAnalysisService, PlanAnalysisService>();
        context.Services.AddSingleton<TaskSpecAlignmentService>();
        context.Services.AddSingleton(Mock.Of<IReportExportService>());
        if (withApi) context.Services.AddSingleton<ITaskAlignmentRunApiService>(_api);
        return (context.Render<TaskToSpecAlignment>(), session);
    }

    private string ProjectKey => _explorers.CurrentScope!;
    private string TasksPath => _explorers.GetStateAsync(WorkspaceArtifactType.Tasks).GetAwaiter().GetResult().Selected!.SourcePath!.Replace('\\', '/');

    [Fact]
    public void A_completed_analysis_is_saved_and_restored_after_a_reload_without_running_again()
    {
        var (first, firstSession) = OpenPage();
        first.WaitForAssertion(() => first.Find("[data-testid=ir-saved]").GetAttribute("data-state").Should().Be("saved"));
        _api.Runs.Should().ContainSingle();
        var stored = _api.Runs.Single().Summary;
        stored.ProjectKey.Should().Be(ProjectKey).And.StartWith("import:");
        stored.TasksPath.Should().Be(TaskAlignmentRunRules.NormalizePath(TasksPath));
        stored.TasksHash.Should().Be(TaskAlignmentSessionService.HashText(Tasks));
        _api.Runs.Single().Tasks.Select(t => t.TaskId).Should().Equal("T001", "T002");
        firstSession.Snapshot!.RunId.Should().Be(stored.Id);

        var (reloaded, session) = OpenPage();

        reloaded.WaitForAssertion(() => reloaded.Find("[data-testid=ir-status]").GetAttribute("data-state").Should().Be("Current"));
        session.Snapshot!.RunId.Should().Be(stored.Id, "the persisted run is restored");
        _api.SaveAttempts.Should().Be(1, "a restored current result is not analysed and saved again");
        session.Report!.TotalTasks.Should().Be(2);
        reloaded.Find("[data-testid=ir-analyzed-at]").TextContent.Should().Be(stored.AnalyzedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        reloaded.Find("[data-testid=ir-saved]").TextContent.Should().Be("Saved — available after a reload");
    }

    [Fact]
    public void A_restored_run_whose_tasks_document_changed_is_stale_with_its_analysis_time()
    {
        var earlier = new DateTimeOffset(2025, 12, 1, 8, 30, 0, TimeSpan.Zero);
        _api.Seed(ProjectKey, TasksPath, TaskAlignmentSessionService.HashText(Tasks + "- [ ] T003 Removed later\n"), earlier, ("T001", "TechnicalOnly"), ("T002", "Linked"), ("T003", "NeedsReview"));
        var seededHistory = _api.Seed(ProjectKey, TasksPath, new string('9', 64), earlier.AddDays(-3), ("T001", "Linked"));

        var (page, session) = OpenPage();

        page.WaitForAssertion(() => page.Find("[data-testid=ir-status]").GetAttribute("data-state").Should().Be("Stale"));
        page.Find("[data-testid=ir-stale-since]").TextContent.Should().Be($"Stale — inputs changed since {earlier.ToLocalTime():yyyy-MM-dd HH:mm}");
        session.Report!.TotalTasks.Should().Be(3, "the stale result describes the earlier document");
        _api.SaveAttempts.Should().Be(0, "a stale result is not silently replaced");
        page.Find("[data-testid=ir-history-run]").GetAttribute("data-state").Should().Be("historical");
        page.Find("[data-testid=ir-history]").TextContent.Should().Contain(TaskAlignmentSessionService.ShortHash(seededHistory.Summary.TasksHash));

        page.Find("[data-testid=ir-reanalyze]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=ir-status]").GetAttribute("data-state").Should().Be("Current"));
        _api.SaveAttempts.Should().Be(1);
        page.Find("[data-testid=ir-saved]").GetAttribute("data-state").Should().Be("saved");
    }

    [Fact]
    public void A_run_of_another_tasks_document_with_the_same_file_name_or_of_another_project_is_never_reused()
    {
        var hash = TaskAlignmentSessionService.HashText(Tasks);
        var otherDocument = _api.Seed(ProjectKey, "other/specs/001/tasks.md", hash, DateTimeOffset.UnixEpoch, ("T001", "PossibleDeviation"), ("T002", "PossibleDeviation"));
        var otherProject = _api.Seed("import:another-project", TasksPath, hash, DateTimeOffset.UnixEpoch, ("T001", "PossibleDeviation"), ("T002", "PossibleDeviation"));

        var (page, session) = OpenPage();

        page.WaitForAssertion(() => page.Find("[data-testid=ir-saved]").GetAttribute("data-state").Should().Be("saved"));
        session.Snapshot!.RunId.Should().NotBe(otherDocument.Summary.Id).And.NotBe(otherProject.Summary.Id);
        _api.SaveAttempts.Should().Be(1, "this document had no run: it was analysed and saved");
        session.Snapshot.RunId.Should().Be(_api.Runs.Last().Summary.Id);
        session.Report!.Findings.Should().NotContain(f => f.Status == BirkNext.Web.Models.AlignmentStatus.PossibleDeviation && f.Title == "T001",
            "the other document's stored results are never shown");
        page.FindAll("[data-testid=ir-history]").Should().BeEmpty("runs of other documents or projects are not this document's history");
    }

    [Fact]
    public void A_failed_save_is_shown_and_never_presented_as_saved()
    {
        _api.FailSaveWith = "The database is unavailable.";

        var (page, session) = OpenPage();

        page.WaitForAssertion(() => page.Find("[data-testid=ir-saved]").GetAttribute("data-state").Should().Be("failed"));
        page.Find("[data-testid=ir-status]").GetAttribute("data-state").Should().Be("Current", "the analysis itself completed");
        page.Find("[data-testid=ir-save-failed]").TextContent.Should().Contain("Not saved").And.Contain("The database is unavailable.").And.Contain("will not be available after a reload");
        page.Markup.Should().NotContain("Saved — available after a reload");
        session.SaveState.Should().Be(TaskAlignmentSaveState.Failed);
        session.Snapshot!.RunId.Should().BeNull();
        _api.Runs.Should().BeEmpty();

        _api.FailSaveWith = null;
        var (reloaded, _) = OpenPage();
        reloaded.WaitForAssertion(() => reloaded.Find("[data-testid=ir-saved]").GetAttribute("data-state").Should().Be("saved"));
        _api.SaveAttempts.Should().Be(2, "nothing was restored after the failed save: the reload analysed again");
    }

    [Fact]
    public void Without_the_backend_client_the_page_keeps_the_session_only_behaviour()
    {
        var (page, session) = OpenPage(withApi: false);

        page.WaitForAssertion(() => page.Find("[data-testid=ir-status]").GetAttribute("data-state").Should().Be("Current"));
        page.FindAll("[data-testid=ir-saved]").Should().BeEmpty();
        session.SaveState.Should().Be(TaskAlignmentSaveState.NotSaved);
    }
}
