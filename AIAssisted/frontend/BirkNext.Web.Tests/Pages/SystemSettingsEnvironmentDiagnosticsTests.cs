using System.Net;
using System.Text;
using BirkNext.Web.Pages.Admin;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

public class SystemSettingsEnvironmentDiagnosticsTests : BunitContext
{
    private readonly AdminApiHandler _handler = new();

    public SystemSettingsEnvironmentDiagnosticsTests()
    {
        var httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };

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
        Services.AddSingleton<WorkspaceArtifactRepository>();
        Services.AddSingleton<IWorkspaceArtifactRepository>(sp => sp.GetRequiredService<WorkspaceArtifactRepository>());
        Services.AddSingleton<IWorkspaceSessionService>(sp => sp.GetRequiredService<WorkspaceArtifactRepository>());
        Services.AddSingleton<IDashboardSnapshotService, DashboardSnapshotService>();
        Services.AddScoped<RuntimeReviewSessionService>();
        Services.AddScoped<QualityReviewSessionService>();
        Services.AddScoped<IExtractionSessionService, ExtractionSessionService>();
    }

    [Fact]
    public void InitialPageState_ShowsNotRunAndNoFail()
    {
        var cut = RenderEnvironmentDiagnostics();

        cut.Markup.Should().Contain("Overall Status");
        cut.Markup.Should().Contain("Not Run");
        cut.Markup.Should().Contain("Diagnostics have not been executed yet.");
        cut.Markup.Should().NotContain(">Fail<");
    }

    [Fact]
    public void DiagnosticsExecutedSuccessfully_RendersStatusSummaryAndSections()
    {
        _handler.EnvironmentDiagnosticsJson = SuccessfulDiagnosticsJson;
        var cut = RenderEnvironmentDiagnostics();

        ClickRunDiagnostics(cut);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Diagnostics completed"));
        cut.Markup.Should().Contain("6 checks executed");
        cut.Markup.Should().Contain("4 Passed");
        cut.Markup.Should().Contain("1 Warnings");
        cut.Markup.Should().Contain("0 Failed");
        cut.Markup.Should().Contain("Environment");
        cut.Markup.Should().Contain("Database");
        cut.Markup.Should().Contain("Backend / API");
        cut.Markup.Should().Contain("Workspace");
        cut.Markup.Should().Contain("ReviewContext");
        cut.Markup.Should().Contain("Export / Reports");
        cut.Markup.Should().Contain("Hosting Environment");
        cut.Markup.Should().Contain("Database Reachable");
        cut.Markup.Should().Contain("Backend Reachable");
        cut.Markup.Should().Contain("Workspace Persistence Tables");
        cut.Markup.Should().Contain("ReviewContext Available");
    }

    [Fact]
    public void ZeroChecks_RendersUnavailableAndNeverFail()
    {
        _handler.EnvironmentDiagnosticsJson = ZeroChecksDiagnosticsJson;
        var cut = RenderEnvironmentDiagnostics();

        ClickRunDiagnostics(cut);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("0 checks executed"));
        cut.Markup.Should().Contain("Unavailable");
        cut.Markup.Should().Contain("No diagnostics returned for this section.");
        cut.Markup.Should().NotContain(">Fail<");
    }

    [Fact]
    public void TimestampFormatting_UsesFormattedLocalTimestamp()
    {
        var timestamp = new DateTime(2026, 7, 3, 12, 34, 56, DateTimeKind.Utc);

        SystemSettings.FormatEnvironmentDiagnosticsTimestamp(timestamp)
            .Should().Be(timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
    }

    [Fact]
    public void SummaryRendering_UsesStatusSummaryCounts()
    {
        var report = new EnvironmentDiagnosticsReportDto
        {
            Summary = new StatusSummaryDto
            {
                PassCount = 13,
                WarningCount = 2,
                FailCount = 0,
                UnavailableCount = 0
            }
        };

        SystemSettings.BuildEnvironmentDiagnosticsSummary(report)
            .Should().Be("Diagnostics completed\n15 checks executed\n13 Passed\n2 Warnings\n0 Failed");
    }

    [Fact]
    public void EmptySectionHandling_RendersEmptySectionMessage()
    {
        _handler.EnvironmentDiagnosticsJson = ZeroChecksDiagnosticsJson;
        var cut = RenderEnvironmentDiagnostics();

        ClickRunDiagnostics(cut);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Environment"));
        cut.Markup.Should().Contain("No diagnostics returned for this section.");
    }

    private IRenderedComponent<SystemSettings> RenderEnvironmentDiagnostics()
    {
        var cut = Render<SystemSettings>();
        cut.WaitForAssertion(() =>
            cut.FindAll("button").Any(button => button.TextContent.Contains("Environment Diagnostics"))
                .Should().BeTrue());

        cut.FindAll("button")
            .First(button => button.TextContent.Contains("Environment Diagnostics"))
            .Click();

        return cut;
    }

    private static void ClickRunDiagnostics(IRenderedComponent<SystemSettings> cut)
    {
        cut.FindAll("button")
            .First(button => button.TextContent.Contains("Run Diagnostics"))
            .Click();
    }

    private sealed class TestHostEnvironment : IWebAssemblyHostEnvironment
    {
        public string Environment { get; set; } = "Development";
        public string BaseAddress { get; set; } = "http://localhost:5173/";
    }

    private sealed class AdminApiHandler : HttpMessageHandler
    {
        public string EnvironmentDiagnosticsJson { get; set; } = SuccessfulDiagnosticsJson;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var json = path switch
            {
                "/api/admin/system-settings" => SystemSettingsJson,
                "/api/admin/editable-settings" => EditableSettingsJson,
                "/api/admin/environment-diagnostics" => EnvironmentDiagnosticsJson,
                _ => "[]"
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private const string SystemSettingsJson = """
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
        """;

    private const string EditableSettingsJson = """
        {
          "featureVisibility": {
            "platform": [{ "key": "AdminSystemSettings", "label": "System Settings", "value": true, "locked": true }],
            "core": [],
            "advanced": []
          },
          "logging": { "minimumLevel": "Information", "seqUrl": "" },
          "admin": { "showDiagnostics": true }
        }
        """;

    private const string SuccessfulDiagnosticsJson = """
        {
          "generatedAt": "2026-07-03T12:34:56Z",
          "environment": "Development",
          "overallStatus": "Warning",
          "summary": { "passCount": 4, "warningCount": 1, "failCount": 0, "unavailableCount": 1, "overallStatus": "Warning" },
          "sections": [
            { "title": "Environment", "description": "Environment checks", "status": "Pass", "items": [{ "name": "Hosting Environment", "value": "Development", "status": "Pass", "description": "Development", "recommendation": "", "isRequired": true }], "isRequired": true },
            { "title": "Database", "description": "Database checks", "status": "Pass", "items": [{ "name": "Database Reachable", "value": "Connected successfully", "status": "Pass", "description": "Connected successfully", "recommendation": "", "isRequired": true }], "isRequired": true },
            { "title": "Backend / API", "description": "Backend checks", "status": "Pass", "items": [{ "name": "Backend Reachable", "value": "http://localhost:5000", "status": "Pass", "description": "http://localhost:5000", "recommendation": "", "isRequired": true }], "isRequired": true },
            { "title": "Workspace", "description": "Workspace checks", "status": "Warning", "items": [{ "name": "Workspace Persistence Tables", "value": "Missing optional data", "status": "Warning", "description": "Missing optional data", "recommendation": "Review workspace storage", "isRequired": false }], "isRequired": false },
            { "title": "ReviewContext", "description": "ReviewContext checks", "status": "Unavailable", "items": [{ "name": "ReviewContext Available", "value": "Active browser state unavailable", "status": "Unavailable", "description": "Active browser state unavailable", "recommendation": "", "isRequired": false }], "isRequired": false },
            { "title": "Export / Reports", "description": "Export checks", "status": "Pass", "items": [{ "name": "JSON Export", "value": "Available", "status": "Pass", "description": "Available", "recommendation": "", "isRequired": false }], "isRequired": false }
          ]
        }
        """;

    private const string ZeroChecksDiagnosticsJson = """
        {
          "generatedAt": "2026-07-03T12:34:56Z",
          "environment": "Development",
          "overallStatus": "Unavailable",
          "summary": { "passCount": 0, "warningCount": 0, "failCount": 0, "unavailableCount": 0, "overallStatus": "Unavailable" },
          "sections": [
            { "title": "Environment", "description": "Environment checks", "status": "Unavailable", "items": [], "isRequired": true },
            { "title": "Database", "description": "Database checks", "status": "Unavailable", "items": [], "isRequired": true },
            { "title": "Backend / API", "description": "Backend checks", "status": "Unavailable", "items": [], "isRequired": true },
            { "title": "Workspace", "description": "Workspace checks", "status": "Unavailable", "items": [], "isRequired": false },
            { "title": "ReviewContext", "description": "ReviewContext checks", "status": "Unavailable", "items": [], "isRequired": false },
            { "title": "Export / Reports", "description": "Export checks", "status": "Unavailable", "items": [], "isRequired": false }
          ]
        }
        """;
}
