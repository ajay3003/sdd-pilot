using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Spec Drift and Impact Analysis read the backend's per-Sample-Project requirement/test store, which Project Import does not write.
/// Found by real-project acceptance: with an imported project they told the user to select a Sample Project (replacing their project).
/// They now say the workspace is not assessed by them, and only fall back to the import/sample choice when nothing is loaded.
/// </summary>
public sealed class SampleProjectStoreNoticeTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();

    public SampleProjectStoreNoticeTests()
    {
        Services.AddSingleton<FeatureVisibilityService>();
        Services.AddSingleton(new Mock<IBirkNextClient>().Object);
        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
    }

    [Fact]
    public void Imported_project_is_not_assessed_instead_of_asking_for_a_sample_project()
    {
        _workspace.SddLifecycle.CurrentProjectImportId = "import-0123456789abcdef";
        _workspace.Set(WorkspaceArtifactType.Specification, "# Spec\n\n- FR-001: The system shall work.");

        foreach (var markup in new[] { Render<SpecDrift>().Markup, Render<ImpactAnalysis>().Markup })
        {
            markup.Should().Contain("data-testid=\"sample-store-notice\"").And.Contain("Not assessed for this workspace");
            markup.Should().NotContain("Select a Sample Project");
            markup.Should().NotContain("notification-error");
        }
    }

    [Fact]
    public void Manually_imported_documents_without_a_project_are_also_not_assessed()
    {
        _workspace.Set(WorkspaceArtifactType.Plan, "# Plan");

        SampleProjectStoreNotice.Message("Impact Analysis", _workspace).Should().StartWith("Not assessed for this workspace: Impact Analysis");
    }

    [Fact]
    public void Empty_workspace_offers_import_or_sample_project()
    {
        SampleProjectStoreNotice.HasNonSampleWorkspace(_workspace).Should().BeFalse();
        SampleProjectStoreNotice.Message("Spec Drift", _workspace).Should().Be("No project loaded. Import a project or select a Sample Project to use Spec Drift.");
    }

    [Fact]
    public void A_selected_sample_project_is_not_treated_as_an_imported_workspace()
    {
        _workspace.CurrentProject = "sample-slug";

        SampleProjectStoreNotice.HasNonSampleWorkspace(_workspace).Should().BeFalse();
    }
}
