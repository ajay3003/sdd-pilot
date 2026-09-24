using System.Net;
using System.Text;
using BirkNext.Web.Models;
using BirkNext.Web.Pages.Admin;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The current project is three layers that used to be reported as one: the selected project (persisted identity),
/// its documents (a Sample Project resolves them on demand from SampleData, never copying them into the session), and
/// the artifacts imported into this browser session. Validation and Runtime Diagnostics read the layer the reviews read.
/// </summary>
public sealed class SystemSettingsProjectDocumentSourceTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();

    public SystemSettingsProjectDocumentSourceTests()
    {
        var httpClient = new HttpClient(new AdminApiHandler()) { BaseAddress = new Uri("http://localhost:5000/") };
        Services.AddSingleton(new AdminApiService(httpClient));
        Services.AddSingleton<FeatureVisibilityService>();
        Services.AddSingleton(new ImplementationTraceabilityApiService(httpClient));
        Services.AddSingleton(new ProjectDocumentApiService(httpClient));
        Services.AddSingleton(new WasmSecurityApiService(httpClient));
        Services.AddSingleton<IBlazorWasmPerformanceReviewService>(new BlazorWasmPerformanceReviewService(httpClient));
        Services.AddSingleton<IWebAssemblyHostEnvironment>(new TestHostEnvironment());
        Services.AddSingleton<IConstitutionAnalysisService, ConstitutionAnalysisService>();
        Services.AddSingleton<IPlanAnalysisService, PlanAnalysisService>();
        Services.AddSingleton<IArtifactParserService, ArtifactParserService>();
        Services.AddSingleton<IReviewContextValidator, ReviewContextValidator>();
        Services.AddSingleton(_workspace);
        Services.AddSingleton<IWorkspaceArtifactRepository>(_workspace);
        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<IWorkspaceStateManager, WorkspaceStateManager>();
        Services.AddSingleton<IWorkspaceArtifactStatusService, WorkspaceArtifactStatusService>();
        Services.AddSingleton<IDashboardSnapshotService, DashboardSnapshotService>();
        Services.AddSingleton<IFrontendAnalysisSettingsService, FrontendAnalysisSettingsService>();
        Services.AddSingleton(Moq.Mock.Of<IBrowserAutomationDiagnosticApiService>());
        Services.AddScoped<RuntimeReviewSessionService>();
        Services.AddScoped<QualityReviewSessionService>();
        Services.AddScoped<ApplicationRuntimeResetService>();
        Services.AddScoped<IExtractionSessionService, ExtractionSessionService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static string Sample(string file) => File.ReadAllText(TestDataHelper.ResolveSampleDataPath("autorisasjon", file));

    private void Resolver(string slug, params string[] files) =>
        Services.AddSingleton<ISampleProjectDocumentResolver>(new FakeResolver(slug, files.ToDictionary(f => f, Sample)));

    private IRenderedComponent<SystemSettings> Open(string menu)
    {
        var cut = Render<SystemSettings>();
        cut.WaitForAssertion(() => cut.FindAll("button").Any(b => b.TextContent.Contains(menu)).Should().BeTrue());
        cut.FindAll("button").First(b => b.TextContent.Contains(menu)).Click();
        return cut;
    }

    private static IRenderedComponent<SystemSettings> RunValidation(IRenderedComponent<SystemSettings> cut)
    {
        cut.FindAll("button").First(b => b.TextContent.Contains("Run Validation")).Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Canonical Metrics"));
        return cut;
    }

    private static string Text(IRenderedComponent<SystemSettings> cut) => string.Concat(cut.Nodes.Select(n => n.TextContent));

    private static string Row(IRenderedComponent<SystemSettings> cut, string label) =>
        cut.FindAll(".diag-item, tr, li, .ss-health-row, div").Where(e => e.Children.Length > 0 && e.TextContent.Contains(label))
            .OrderBy(e => e.TextContent.Length).First().TextContent;

    // The key case: a selected Sample Project with all five documents, nothing imported into the session.
    [Fact]
    public void ASelectedSampleProjectIsValidatedFromItsOwnDocuments_NotTheEmptySession()
    {
        _workspace.CurrentProject = "autorisasjon";
        Resolver("autorisasjon", "constitution.md", "spec.md", "plan.md", "tasks.md", "data-model.md");
        var cut = RunValidation(Open("ReviewContext Validation"));

        cut.WaitForAssertion(() => Text(cut).Should().Contain("Validation complete for Sample project 'autorisasjon'"));
        Text(cut).Should().NotContain("No workspace artifacts are loaded");
        Text(cut).Should().Contain("Source: Sample project 'autorisasjon' (SampleData, resolved on demand)");
        foreach (var file in new[] { "constitution.md", "spec.md", "plan.md", "tasks.md", "data-model.md" })
            Row(cut, file).Should().Contain("Loaded");
        // Metrics are read from real documents, so they are observations — not "Not evaluated", not zero-as-pass.
        Row(cut, "Requirements With Tests").Should().NotContain("Not evaluated");
        cut.Markup.Should().NotContain("Source document not present for the current project.");
    }

    // Partial project: the missing documents are named as absent from the project, and their metrics not evaluated.
    [Fact]
    public void APartialProjectMarksOnlyItsMissingDocuments()
    {
        _workspace.CurrentProject = "autorisasjon";
        Resolver("autorisasjon", "constitution.md", "plan.md", "tasks.md");
        var cut = RunValidation(Open("ReviewContext Validation"));

        Row(cut, "spec.md").Should().Contain("Not in this project");
        Row(cut, "constitution.md").Should().Contain("Loaded");
        Row(cut, "Coverage %").Should().Contain("Not evaluated");
    }

    // No project and nothing imported: nothing is inspected, and nothing reads as a healthy PASS.
    [Fact]
    public void WithNoProjectNothingIsEvaluated()
    {
        var cut = RunValidation(Open("ReviewContext Validation"));

        Text(cut).Should().Contain("No current project is selected and no artifacts are imported, so nothing was evaluated.");
        Row(cut, "Requirements").Should().Contain("Not evaluated");
        Row(cut, "Constitution Loaded").Should().Contain("Not evaluated");
    }

    // Runtime Diagnostics: the selected project and the (empty) session are different rows, and neither is a warning.
    [Fact]
    public void RuntimeDiagnosticsSeparatesTheSelectedProjectFromSessionArtifacts()
    {
        _workspace.CurrentProject = "autorisasjon";
        Resolver("autorisasjon", "constitution.md", "spec.md", "plan.md", "tasks.md", "data-model.md");
        var cut = Open("Runtime Diagnostics");

        cut.WaitForAssertion(() => Text(cut).Should().Contain("Sample project 'autorisasjon' (SampleData, resolved on demand)"));
        Text(cut).Should().Contain("Selected project").And.Contain("Session artifacts").And.Contain("None imported");
        Text(cut).Should().NotContain("No workspace loaded");
    }

    // A local, unpublished run has no build/commit/package-root metadata and an empty legacy document store: facts,
    // not missing capabilities — they are shown as not evaluated and never raise the overall status.
    [Fact]
    public void SystemDiagnosticsTreatsLocalLifecycleGapsAsNeutral()
    {
        var cut = Open("System Diagnostics");
        cut.WaitForAssertion(() => Text(cut).Should().Contain("Stored Project Documents (backend store)"));

        Row(cut, "Build").Should().Contain("Not configured").And.NotContain("Unavailable");
        Row(cut, "Package Root").Should().NotContain("Unavailable");
        Text(cut).Should().Contain("Not loaded / not evaluated");
    }

    // Reset Workspace clears imported artifacts only; the persisted project selection survives.
    [Fact]
    public void ResetWorkspaceKeepsTheSelectedProject()
    {
        _workspace.CurrentProject = "autorisasjon";
        _workspace.Set(WorkspaceArtifactKind.Tasks, "# Tasks");
        JSInterop.Setup<bool>("confirm", _ => true).SetResult(true);
        var cut = Open("Runtime Diagnostics");

        cut.WaitForAssertion(() => cut.FindAll("button").Any(b => b.TextContent.Trim() == "Reset Workspace").Should().BeTrue());
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Reset Workspace").Click();

        cut.WaitForAssertion(() => _workspace.Get(WorkspaceArtifactKind.Tasks).Should().BeNull());
        _workspace.CurrentProject.Should().Be("autorisasjon", "the selected project is persisted identity, not session state");
    }

    private sealed class FakeResolver(string slug, IReadOnlyDictionary<string, string> files) : ISampleProjectDocumentResolver
    {
        private static string File(ExplorerDocumentType type) => type switch
        {
            ExplorerDocumentType.Constitution => "constitution.md",
            ExplorerDocumentType.Specification => "spec.md",
            ExplorerDocumentType.Plan => "plan.md",
            ExplorerDocumentType.Tasks => "tasks.md",
            _ => "data-model.md",
        };

        public Task<SampleProjectDocumentResult> ResolveAsync(string projectSlug, ExplorerDocumentType documentType, CancellationToken cancellationToken = default) =>
            Task.FromResult(projectSlug == slug && files.TryGetValue(File(documentType), out var content)
                ? SampleProjectDocumentResult.Success(projectSlug, documentType, File(documentType), content)
                : SampleProjectDocumentResult.MissingDocument(projectSlug, documentType, File(documentType)));

        public Task<IReadOnlyList<SampleProjectDto>> GetAvailableProjectsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SampleProjectDto>>([new SampleProjectDto(slug, "Autorisasjon", "", "", "", false, [])]);

        public string? GetSelectedProject() => slug;
        public void SetSelectedProject(string? projectSlug) { }
        public void ClearProjectCache(string projectSlug) { }
    }

    private sealed class TestHostEnvironment : IWebAssemblyHostEnvironment
    {
        public string Environment { get; set; } = "Development";
        public string BaseAddress { get; set; } = "http://localhost:5173/";
    }

    private sealed class AdminApiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = (request.RequestUri?.AbsolutePath ?? "") switch
            {
                "/api/admin/system-settings" => """
                    {
                      "application": { "applicationName": "QA Review Studio", "environment": "Development", "version": "1.0.0", "packageMode": "Local" },
                      "frontend": { "frontendBaseUrl": "http://localhost:5173", "apiBaseUrl": "http://localhost:5000", "graphQlEndpoint": "http://localhost:5000/graphql", "environmentName": "Development", "staticHostingMode": true },
                      "backend": { "backendBaseUrl": "http://localhost:5000", "aspNetCoreEnvironment": "Development", "listeningUrls": "http://localhost:5000", "corsAllowedOrigins": "http://localhost:5173" },
                      "database": { "mode": "Local", "host": "localhost", "port": 5432, "databaseName": "test", "username": "test", "provider": "Postgres", "migrationStatus": "Up to date" },
                      "runtime": { "composeProjectName": "birknext", "expectedDatabaseVolume": "birknext_pgdata", "packageMode": "Local", "runningFromPublishedArtifact": false },
                      "logging": { "provider": "Console", "minimumLevel": "Information" },
                      "maintenance": { "resetAllowed": true, "databaseMode": "Local", "resetNotAllowedReason": "" },
                      "featureVisibility": { "adminSystemSettings": true },
                      "azureDevOps": { "enabled": false, "patConfigured": false }
                    }
                    """,
                "/api/admin/editable-settings" => """
                    {
                      "featureVisibility": { "platform": [{ "key": "AdminSystemSettings", "label": "System Settings", "value": true, "locked": true }], "core": [], "advanced": [] },
                      "logging": { "minimumLevel": "Information", "seqUrl": "" },
                      "admin": { "showDiagnostics": true }
                    }
                    """,
                _ => "[]",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
