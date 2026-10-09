using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System.Net;
using System.Text;

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
        Services.AddSingleton(new SourceChangeImpactApiService(new HttpClient(new EmptyImpactEvidenceHandler())
        {
            BaseAddress = new Uri("http://localhost/")
        }));
    }

    [Fact]
    public void Imported_project_is_not_assessed_instead_of_asking_for_a_sample_project()
    {
        const string importId = "import-0123456789abcdef";
        _workspace.SddLifecycle.CurrentProjectImportId = importId;
        _workspace.SddLifecycle.ProjectImports.Add(new SddProjectImportRecord
        {
            ImportId = importId,
            ProjectName = "Imported fixture",
            ArchiveFileName = "fixture.zip"
        });
        _workspace.Set(WorkspaceArtifactType.Specification, "# Spec\n\n- FR-001: The system shall work.");

        var specDriftMarkup = Render<SpecDrift>().Markup;
        specDriftMarkup.Should().Contain("data-testid=\"sample-store-notice\"").And.Contain("Not assessed for this workspace");
        specDriftMarkup.Should().NotContain("Select a Sample Project").And.NotContain("notification-error");

        var impactMarkup = Render<ImpactAnalysis>().Markup;
        impactMarkup.Should().Contain("Imported fixture").And.Contain("No persisted requirement records were returned");
        impactMarkup.Should().NotContain("Select a Sample Project").And.NotContain("notification-error");
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

    private sealed class EmptyImpactEvidenceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri?.AbsolutePath.EndsWith("/source-change/snapshots", StringComparison.Ordinal) == true
                ? "{\"sourceAnalysisEnabled\":false,\"snapshots\":[]}"
                : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
