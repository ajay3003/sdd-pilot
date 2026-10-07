using System.Net;
using System.Text;
using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The real M2LB upload failed as "ARCHIVE_REJECTED / Archive validation" although the archive was never read: with no active Target
/// Environment the context's blank profile (Id "") passed Source Analysis's null check, the upload was sent with "environmentId=",
/// and ASP.NET request validation answered with a code-less ProblemDetails that the client could only call an archive rejection.
/// </summary>
public sealed class SourceUploadEnvironmentTests : BunitContext
{
    private readonly Mock<IIntegrationCatalogApiService> _api = new();

    private void Setup(FrontendAnalysisContext context)
    {
        _api.Setup(a => a.ListSourceSnapshotsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(context);
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(factory.Object);
        Services.AddSingleton(Mock.Of<ITechnologyCoverageApiService>());
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddScoped<ProjectApplicabilityState>();
        Services.AddSingleton<IReportExportService, ReportExportService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>What the context factory returns when no Target Environment is active (see FrontendAnalysisContextFactory).</summary>
    private static FrontendAnalysisContext NoActiveEnvironment() => new() { ActiveTargetError = "No active Target Environment" };

    [Fact]
    public void Context_WithoutActiveEnvironment_HasNoActiveProfile_EvenThoughTheBlankDefaultIsNotNull()
    {
        var context = NoActiveEnvironment();

        context.ActiveProfile.Should().NotBeNull("other consumers rely on the non-null default");
        context.ActiveProfile.Id.Should().BeEmpty();
        context.ActiveProfileOrNull.Should().BeNull();
        new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev" } }.ActiveProfileOrNull!.Id.Should().Be("dev");
        new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "" } }.ActiveProfileOrNull.Should().BeNull();
    }

    [Fact]
    public void SourceAnalysis_WithoutActiveEnvironment_AsksForOne_AndOffersNoUploadThatCouldOnlyFail()
    {
        Setup(NoActiveEnvironment());

        var cut = Render<SourceAnalysis>();

        cut.WaitForElement("[data-testid=sa-no-target]").TextContent.Should().Contain("Select a target environment");
        cut.FindAll("[data-testid=sa-upload], [data-testid=sa-empty-upload]").Should().BeEmpty();
        _api.Verify(a => a.ListSourceSnapshotsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never, "no request with an empty environment id");
    }

    [Fact]
    public void SourceAnalysis_WithActiveEnvironment_ListsItsSnapshots()
    {
        Setup(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev" } });

        var cut = Render<SourceAnalysis>();

        cut.WaitForElement("[data-testid=sa-upload]");
        _api.Verify(a => a.ListSourceSnapshotsAsync("dev", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Client error mapping: request validation is never an archive rejection ──────────────────────────────────────────

    private static IntegrationCatalogApiService Client(HttpStatusCode status, string body, string contentType) =>
        new(new HttpClient(new StubHandler(status, body, contentType)) { BaseAddress = new Uri("http://localhost/") });

    [Fact]
    public async Task MissingEnvironmentProblemDetails_IsNoActiveEnvironment_NotArchiveRejected()
    {
        // The exact body ASP.NET returned for the real upload.
        const string body = """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{"environmentId":["The environmentId field is required."]},"traceId":"00-a37900332e6c78df3f0d81e525aba30b-2755b86e9ff55562-00"}""";
        var (snapshot, failure) = await Client(HttpStatusCode.BadRequest, body, "application/problem+json")
            .AnalyzeSourceSnapshotDetailedAsync("", "M2LB (2).zip", new MemoryStream([1, 2, 3]));

        snapshot.Should().BeNull();
        failure!.Code.Should().Be("NO_ACTIVE_ENVIRONMENT").And.NotBe("ARCHIVE_REJECTED");
        failure.Stage.Should().Be("prerequisite");
        failure.Message.Should().Contain("Target Environment");
    }

    [Fact]
    public async Task OtherRequestValidationProblemDetails_IsAnUploadRequestError_NotArchiveRejected()
    {
        const string body = """{"title":"One or more validation errors occurred.","status":400,"errors":{"file":["The file field is required."]}}""";
        var (_, failure) = await Client(HttpStatusCode.BadRequest, body, "application/problem+json")
            .AnalyzeSourceSnapshotDetailedAsync("dev", "x.zip", new MemoryStream([1]));

        failure!.Code.Should().Be("UPLOAD_REQUEST_INVALID");
        failure.Stage.Should().Be("upload");
        failure.Message.Should().Contain("archive was not read");
    }

    private sealed class StubHandler(HttpStatusCode status, string body, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) });
    }
}
