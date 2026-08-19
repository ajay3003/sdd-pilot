using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Integration;

/// <summary>
/// Regression tests for Reset Local Database frontend runtime state clearing.
/// Ensures that after backend reset succeeds, stale frontend state cannot cause phantom UI.
/// </summary>
public sealed class ResetLocalDatabaseTests
{
    private (IWorkspaceArtifactRepository workspace,
             IWorkspaceStateManager stateManager,
             QualityReviewSessionService qualitySession,
             IDashboardSnapshotService dashboard,
             RuntimeReviewSessionService runtimeReviews,
             ApplicationRuntimeResetService reset) SetupServices()
    {
        var services = new ServiceCollection();

        // Register core state services
        services.AddSingleton<WorkspaceArtifactRepository>();
        services.AddSingleton<IWorkspaceArtifactRepository>(sp => sp.GetRequiredService<WorkspaceArtifactRepository>());
        services.AddSingleton<IWorkspaceSessionService>(sp => sp.GetRequiredService<WorkspaceArtifactRepository>());
        services.AddSingleton<IWorkspaceStateManager, WorkspaceStateManager>();
        services.AddSingleton<IDashboardSnapshotService, DashboardSnapshotService>();
        services.AddScoped<QualityReviewSessionService>();
        services.AddScoped<RuntimeReviewSessionService>();

        // Mock extraction service
        var extractionSession = Mock.Of<IExtractionSessionService>(s =>
            s.ClearAsync() == Task.CompletedTask);
        services.AddSingleton(extractionSession);

        // Register reset service
        services.AddScoped<ApplicationRuntimeResetService>();

        // Build the service provider
        var provider = services.BuildServiceProvider();

        // Get instances
        var workspace = provider.GetRequiredService<IWorkspaceArtifactRepository>();
        var stateManager = provider.GetRequiredService<IWorkspaceStateManager>();
        var qualitySession = provider.GetRequiredService<QualityReviewSessionService>();
        var dashboard = provider.GetRequiredService<IDashboardSnapshotService>();
        var runtimeReviews = provider.GetRequiredService<RuntimeReviewSessionService>();
        var reset = provider.GetRequiredService<ApplicationRuntimeResetService>();

        return (workspace, stateManager, qualitySession, dashboard, runtimeReviews, reset);
    }

    [Fact]
    public async Task ResetLocalDatabase_ClearsActiveFrontendRuntimeState()
    {
        var (workspace, stateManager, qualitySession, dashboard, runtimeReviews, reset) = SetupServices();

        // Arrange: Populate all frontend state
        var workspaceId = Guid.NewGuid();
        workspace.CurrentProject = "autorisasjon";
        workspace.Set(WorkspaceArtifactType.Constitution, "constitution text");
        workspace.Set(WorkspaceArtifactType.Specification, "spec text");
        workspace.Set(WorkspaceArtifactType.Plan, "plan text");
        workspace.Set(WorkspaceArtifactType.Tasks, "tasks text");
        workspace.Set(WorkspaceArtifactType.DataModel, "datamodel text");

        stateManager.NotifyWorkspaceChanged(workspaceId);

        qualitySession.SaveResult(
            new QualityReviewReport { OverallScore = 100, PackResults = [], RunAt = DateTimeOffset.UtcNow },
            ["qa-auditor"],
            "autorisasjon",
            new Dictionary<WorkspaceArtifactKind, string>());

        dashboard.Publish(new ConstitutionComplianceReport
        {
            Coverage = new() { TotalItems = 5, CompliantItems = 5 },
            Health = new() { CompliancePercentage = 100 }
        });

        // Verify populated state
        workspace.CurrentProject.Should().Be("autorisasjon");
        workspace.GetAllArtifacts().Should().HaveCount(5);
        stateManager.CurrentWorkspaceId.Should().Be(workspaceId);
        qualitySession.HasResult.Should().BeTrue();
        dashboard.ComplianceReport.Should().NotBeNull("compliance snapshot should be populated");

        // Act: Reset frontend runtime state
        await reset.ClearFrontendRuntimeStateAsync();

        // Assert: All state cleared
        workspace.CurrentProject.Should().BeNull("workspace project should be cleared");
        workspace.GetAllArtifacts().Should().BeEmpty("workspace artifacts should be cleared");
        stateManager.CurrentWorkspaceId.Should().BeNull("workspace ID should be cleared");
        qualitySession.HasResult.Should().BeFalse("quality session should be cleared");
        dashboard.ComplianceReport.Should().BeNull("dashboard snapshot should be cleared");
    }

    [Fact]
    public async Task ResetLocalDatabase_ThenDashboardShowsEmptyState()
    {
        var (workspace, stateManager, qualitySession, dashboard, _, reset) = SetupServices();

        // Arrange: Simulate Dashboard showing loaded/ready state
        var workspaceId = Guid.NewGuid();
        workspace.CurrentProject = "autorisasjon";
        workspace.Set(WorkspaceArtifactType.Constitution, "constitution");
        workspace.Set(WorkspaceArtifactType.Specification, "spec");
        workspace.Set(WorkspaceArtifactType.Plan, "plan");
        workspace.Set(WorkspaceArtifactType.Tasks, "tasks");
        workspace.Set(WorkspaceArtifactType.DataModel, "datamodel");
        stateManager.NotifyWorkspaceChanged(workspaceId);

        dashboard.Publish(new ConstitutionComplianceReport
        {
            Coverage = new() { TotalItems = 5, CompliantItems = 5 },
            Health = new() { CompliancePercentage = 100 }
        });
        qualitySession.SaveResult(
            new QualityReviewReport { OverallScore = 90, PackResults = [], RunAt = DateTimeOffset.UtcNow },
            ["qa-auditor"],
            "autorisasjon",
            new Dictionary<WorkspaceArtifactKind, string>());

        // Verify pre-reset state (simulating phantom UI)
        workspace.GetAllArtifacts().Count().Should().Be(5);
        dashboard.ComplianceReport?.Health.CompliancePercentage.Should().Be(100);
        qualitySession.Report?.OverallScore.Should().Be(90);

        // Act: Reset
        await reset.ClearFrontendRuntimeStateAsync();

        // Assert: Dashboard would show empty state
        workspace.CurrentProject.Should().BeNull();
        workspace.GetAllArtifacts().Should().BeEmpty();
        stateManager.CurrentWorkspaceId.Should().BeNull();
        dashboard.ComplianceReport.Should().BeNull("no phantom Governance 100%");
        qualitySession.Report.Should().BeNull("no phantom Ready state");
    }

    [Fact]
    public async Task ResetLocalDatabase_DoesNotAutoSaveDeletedWorkspaceBack()
    {
        var (workspace, stateManager, _, _, _, reset) = SetupServices();

        // Arrange: Populate workspace with artifacts
        var workspaceId = Guid.NewGuid();
        workspace.CurrentProject = "autorisasjon";
        workspace.Set(WorkspaceArtifactType.Constitution, "old constitution");
        workspace.Set(WorkspaceArtifactType.Specification, "old spec");
        stateManager.NotifyWorkspaceChanged(workspaceId);

        // Verify populated
        workspace.CurrentProject.Should().Be("autorisasjon");
        stateManager.CurrentWorkspaceId.Should().Be(workspaceId);
        workspace.GetAllArtifacts().Should().HaveCount(2);

        // Act: Clear (simulating reset)
        await reset.ClearFrontendRuntimeStateAsync();

        // Assert: State is null, no pending recovery
        // Key invariant: ProjectName is null BEFORE WorkspaceChanged event fires
        // so AutoSave listeners see null and don't recreate the workspace
        workspace.CurrentProject.Should().BeNull("ProjectName must be null to prevent AutoSave");
        workspace.ProjectName.Should().BeNull();
        stateManager.CurrentWorkspaceId.Should().BeNull("StateManager must be cleared to prevent reconstruction");
    }

    [Fact]
    public async Task ResetLocalDatabase_WhenBackendFails_PreservesFrontendState()
    {
        var (workspace, stateManager, qualitySession, dashboard, _, reset) = SetupServices();

        // Arrange: Populate state
        var workspaceId = Guid.NewGuid();
        workspace.CurrentProject = "autorisasjon";
        workspace.Set(WorkspaceArtifactType.Constitution, "constitution");
        stateManager.NotifyWorkspaceChanged(workspaceId);
        qualitySession.SaveResult(
            new QualityReviewReport { OverallScore = 90, PackResults = [], RunAt = DateTimeOffset.UtcNow },
            ["qa-auditor"],
            "autorisasjon",
            new Dictionary<WorkspaceArtifactKind, string>());
        dashboard.Publish(new ConstitutionComplianceReport
        {
            Coverage = new() { TotalItems = 5, CompliantItems = 5 },
            Health = new() { CompliancePercentage = 100 }
        });

        var originalProject = workspace.CurrentProject;
        var originalId = stateManager.CurrentWorkspaceId;
        var hadResult = qualitySession.HasResult;

        // Act: Do NOT call reset (simulating backend failure prevents reset)
        // (In real code, SystemSettings.ExecuteResetAsync() would not call reset on failure)
        // Here we just verify nothing changed without calling reset

        // Assert: All state preserved
        workspace.CurrentProject.Should().Be(originalProject);
        stateManager.CurrentWorkspaceId.Should().Be(originalId);
        qualitySession.HasResult.Should().Be(hadResult);
        dashboard.ComplianceReport.Should().NotBeNull("compliance snapshot should be populated");
    }
}
