using System.Net;
using System.Text;
using BirkNext.Web.Pages.Admin;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

public class SystemSettingsReviewContextValidationTests : BunitContext
{
    public SystemSettingsReviewContextValidationTests()
    {
        var httpClient = new HttpClient(new AdminApiHandler())
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
        Services.AddSingleton<IWorkspaceStateManager, WorkspaceStateManager>();
        Services.AddSingleton<IWorkspaceArtifactStatusService, WorkspaceArtifactStatusService>();
        Services.AddSingleton<IDashboardSnapshotService, DashboardSnapshotService>();
        Services.AddScoped<RuntimeReviewSessionService>();
        Services.AddScoped<QualityReviewSessionService>();
        Services.AddScoped<IExtractionSessionService, ExtractionSessionService>();
    }

    [Fact]
    public void SystemSettings_DeveloperMenu_RendersReviewContextValidationEmptyState()
    {
        var cut = Render<SystemSettings>();

        cut.WaitForAssertion(() =>
            cut.FindAll("button").Any(button => button.TextContent.Contains("ReviewContext Validation"))
                .Should().BeTrue());

        cut.FindAll("button")
            .First(button => button.TextContent.Contains("ReviewContext Validation"))
            .Click();

        cut.Markup.Should().Contain("Run Validation");
        cut.Markup.Should().Contain("Diagnostics have not been executed yet.");

        cut.FindAll("button")
            .First(button => button.TextContent.Contains("Run Validation"))
            .Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No workspace artifacts are loaded"));
        cut.Markup.Should().Contain("Loaded Artifacts");
        cut.Markup.Should().Contain("Canonical Metrics");
        cut.Markup.Should().Contain("Source Comparisons");
        cut.Markup.Should().Contain("Findings");
        cut.Markup.Should().Contain("Export JSON");
        cut.Markup.Should().Contain("Export HTML");
    }

    private sealed class TestHostEnvironment : IWebAssemblyHostEnvironment
    {
        public string Environment { get; set; } = "Development";
        public string BaseAddress { get; set; } = "http://localhost:5173/";
    }

    private sealed class AdminApiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var json = path switch
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
                      "featureVisibility": {
                        "platform": [{ "key": "AdminSystemSettings", "label": "System Settings", "value": true, "locked": true }],
                        "core": [],
                        "advanced": []
                      },
                      "logging": { "minimumLevel": "Information", "seqUrl": "" },
                      "admin": { "showDiagnostics": true }
                    }
                    """,
                _ => "[]"
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
