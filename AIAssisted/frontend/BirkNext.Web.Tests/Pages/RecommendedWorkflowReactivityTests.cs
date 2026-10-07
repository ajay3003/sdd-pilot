using BirkNext.Technology;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// An open Recommended Workflow follows Target Environment and source changes as they happen — through the existing owners'
/// change signals (Target Environment settings saved, Source Analysis snapshot changed), never polling or navigation — and a
/// closed page stops listening.
/// </summary>
public sealed class RecommendedWorkflowReactivityTests : BunitContext
{
    private readonly Mock<IFrontendAnalysisSettingsService> _settings = new();
    private readonly Mock<IRecommendedWorkflowApiService> _workflowApi = new();
    private FrontendAnalysisProfile? _environment;
    private ProjectTechnologyCoverage? _coverage;
    private int _readinessReads;

    public RecommendedWorkflowReactivityTests()
    {
        var contexts = new Mock<IFrontendAnalysisContextFactory>();
        contexts.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(() => _environment is null
            ? new FrontendAnalysisContext { ActiveTargetError = "No active Target Environment" }
            : new FrontendAnalysisContext { ActiveProfile = _environment, TargetUrl = _environment.TargetUrl });
        var coverage = new Mock<ITechnologyCoverageApiService>();
        coverage.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _coverage);
        _workflowApi.Setup(w => w.BuildWorkflowStepsAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync(() => { _readinessReads++; return []; });

        Services.AddSingleton(_settings.Object);
        Services.AddSingleton(contexts.Object);
        Services.AddSingleton(coverage.Object);
        Services.AddSingleton(WorkspaceSnapshots.Projection(WorkspaceSnapshots.AllRoles("Person Module", "person-module")).Object);
        Services.AddScoped<ProjectApplicabilityState>();
        Services.AddSingleton(_workflowApi.Object);
        Services.AddScoped<IWorkflowReadinessService, WorkflowReadinessService>();
        Services.AddSingleton(Mock.Of<IWorkspacePersistenceApiService>());
        Services.AddSingleton(Mock.Of<IWorkspaceSessionRestoreService>());
        var autoSave = new Mock<IWorkspaceAutoSaveService>();
        autoSave.Setup(a => a.StartMonitoringAsync()).Returns(Task.CompletedTask);
        Services.AddSingleton(autoSave.Object);
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    }

    private void TargetEnvironmentsSaved() => _settings.Raise(s => s.Changed += null);

    private static string Status(IRenderedComponent<RecommendedWorkflow> cut, string kind) =>
        cut.Find($"[data-testid=rw-input][data-kind={kind}]").GetAttribute("data-status")!;

    [Fact]
    public void TargetSaved_UpdatesTheOpenPage_WithoutNavigation()
    {
        var cut = Render<RecommendedWorkflow>();
        cut.WaitForAssertion(() => Status(cut, "Target").Should().Be("Absent"));
        var url = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri;

        _environment = new FrontendAnalysisProfile { Id = "env-dev", Name = "DEV", TargetUrl = "https://m2lbdev.bufetat.no" };
        _coverage = new ProjectTechnologyCoverage { EnvironmentId = "env-dev" };
        TargetEnvironmentsSaved();

        cut.WaitForAssertion(() => Status(cut, "Target").Should().Be("Ready"));
        cut.Find("[data-testid=rw-input][data-kind=Target]").TextContent.Should().Contain("m2lbdev.bufetat.no");
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri.Should().Be(url, "no navigation happened");
    }

    [Fact]
    public void SourceAnalyzed_UpdatesTheOpenPage_WithoutNavigation()
    {
        _environment = new FrontendAnalysisProfile { Id = "env-dev", Name = "DEV", TargetUrl = "" };
        _coverage = new ProjectTechnologyCoverage { EnvironmentId = "env-dev" };
        var cut = Render<RecommendedWorkflow>();
        cut.WaitForAssertion(() => Status(cut, "Source").Should().Be("Absent"));

        _coverage = new ProjectTechnologyCoverage
        {
            EnvironmentId = "env-dev", SourceSnapshotId = Guid.NewGuid(), SourceArchive = "M2LB_2_.zip", AnalyzedAt = DateTimeOffset.UtcNow,
            Source = new SourceTechnologyCoverage(),
        };
        _ = Services.GetRequiredService<ProjectApplicabilityState>().RefreshAsync(); // Source Analysis after a snapshot is analyzed

        cut.WaitForAssertion(() => Status(cut, "Source").Should().Be("Ready"));
        Status(cut, "Target").Should().Be("Partial", "the source lives in an environment without an application URL");
    }

    [Fact]
    public void ClosedPages_StopListening_OnlyTheOpenPageRereads()
    {
        for (var i = 0; i < 3; i++)
        {
            var closed = Render<RecommendedWorkflow>();
            closed.WaitForAssertion(() => closed.FindAll("[data-testid=rw-input]").Should().HaveCount(3));
            closed.Instance.Dispose(); // what the renderer calls when the user navigates away
        }
        var open = Render<RecommendedWorkflow>();
        open.WaitForAssertion(() => open.FindAll("[data-testid=rw-input]").Should().HaveCount(3));
        _readinessReads = 0;

        TargetEnvironmentsSaved();

        open.WaitForAssertion(() => _readinessReads.Should().Be(1, "one open page re-reads once; the closed pages' subscriptions were removed"));
    }
}

/// <summary>Analyzing a source snapshot refreshes the shared applicability state, so the source input and source reviews update now.</summary>
public sealed class SourceAnalysisApplicabilityRefreshTests : BunitContext
{
    [Fact]
    public void AnalyzedSnapshot_RefreshesApplicability()
    {
        var snapshots = new List<BirkNext.Integrations.IqrSourceSnapshot>();
        var api = new Mock<IIntegrationCatalogApiService>();
        api.Setup(a => a.ListSourceSnapshotsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => snapshots.ToList());
        api.Setup(a => a.AnalyzeSourceSnapshotDetailedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var s = new BirkNext.Integrations.IqrSourceSnapshot { Id = Guid.NewGuid(), Archive = new("shop-api.zip", new string('e', 64), 3), AnalyzedAt = DateTimeOffset.UtcNow };
                snapshots.Insert(0, s);
                return (s, (SourceUploadFailure?)null);
            });
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev" } });
        var coverage = new Mock<ITechnologyCoverageApiService>();
        Services.AddSingleton(api.Object);
        Services.AddSingleton(context.Object);
        Services.AddSingleton(coverage.Object);
        Services.AddSingleton(WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddScoped<ProjectApplicabilityState>();
        Services.AddSingleton<IReportExportService, ReportExportService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<SourceAnalysis>();
        cut.WaitForAssertion(() => cut.FindComponents<InputFile>().Should().NotBeEmpty());
        coverage.Invocations.Clear();

        cut.FindComponents<InputFile>()[0].UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "shop-api.zip"));

        cut.WaitForAssertion(() => coverage.Verify(c => c.GetAsync("dev", It.IsAny<CancellationToken>()), Times.AtLeastOnce(),
            "the new snapshot is re-read for the active environment"));
    }
}
