using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Regression tests for Sample Projects selected-state UX.
/// Verifies active project is visually obvious with badge and disabled button.
/// </summary>
public sealed class SampleProjectsSelectedStateTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly SampleProjectsHttpHandler _handler = new();

    public SampleProjectsSelectedStateTests()
    {
        var httpClient = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost/") };

        Services.AddSingleton<IWorkspaceArtifactRepository>(_workspace);
        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<IWorkspaceArtifactStatusService>(sp =>
            new WorkspaceArtifactStatusService(sp.GetRequiredService<IWorkspaceSessionService>()));
        Services.AddSingleton<IWorkspaceUpdateCoordinator, WorkspaceUpdateCoordinator>();
        Services.AddSingleton(Moq.Mock.Of<IWorkspaceAutoSaveService>());
        Services.AddSingleton(new QualityReviewSessionService());
        Services.AddSingleton(Moq.Mock.Of<IDashboardSnapshotService>());
        Services.AddSingleton<ITargetEnvironmentHintExtractor>(new TargetEnvironmentHintExtractor());
        Services.AddSingleton(Moq.Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddSingleton(Moq.Mock.Of<IIntegrationTargetRegistryService>());
        Services.AddSingleton(NullLogger<SampleProjects>.Instance);
        Services.AddSingleton(new SampleProjectsApiService(httpClient));

        JSInterop.Setup<bool>("confirm", _ => true).SetResult(true);

        // Set up the catalog with test projects
        _handler.SetProjects(
            CreateProject("autorisasjon", "Autorisasjon", "AUTH"),
            CreateProject("person-module", "Person Module", "PERSON")
        );
    }

    [Fact]
    public void NoSelectedProject_DoesNotShowActiveProject()
    {
        // Arrange: CurrentProject is null
        _workspace.CurrentProject = null;

        // Act: Render Sample Projects
        var cut = Render<SampleProjects>();

        // Assert: No active project indicated
        cut.WaitForAssertion(() =>
        {
            // Banner not shown (no selected project)
            cut.Markup.Should().NotContain("Selected Sample Project:");

            // No "✓ Selected" badges
            cut.Markup.Should().NotContain("✓ Selected");

            // All buttons show "Select Project"
            var selectButtons = cut.FindAll("button.sp-btn-primary");
            selectButtons.Should().AllSatisfy(btn =>
                btn.TextContent.Should().Contain("Select Project", "All cards should show Select Project"));
        });
    }

    [Fact]
    public void RestoredSelectedSampleProject_ShowsActiveSelectedState()
    {
        // Arrange: Workspace has CurrentProject but zero artifacts (identity-only)
        _workspace.CurrentProject = "autorisasjon";
        // Workspace artifacts intentionally empty

        // Act: Render Sample Projects page (simulating restart with restored identity)
        var cut = Render<SampleProjects>();

        // Wait for projects to load from HTTP handler with extended timeout
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Autorisasjon");
            cut.Markup.Should().Contain("Selected Sample Project:");
        }, TimeSpan.FromSeconds(5));

        // Assert: Selected project is visually obvious
        cut.WaitForAssertion(() =>
        {
            // Banner shows human display name (split by strong tag in HTML)
            var bannerText = cut.Find(".sp-workspace-banner").TextContent;
            bannerText.Should().Contain("Selected Sample Project:");
            bannerText.Should().Contain("Autorisasjon");

            // Banner does NOT show "0 artifacts loaded"
            cut.Markup.Should().NotContain("0 artifact");

            // Active card shows "✓ Selected" badge
            var selectedBadges = cut.FindAll(".sp-card-selected-badge");
            selectedBadges.Should().HaveCount(1, "Exactly one project should show selected badge");

            // Card has selected styling
            cut.Markup.Should().Contain("sp-card-selected");

            // Select button is disabled and shows "✓ Selected"
            var selectButton = cut.FindAll("button.sp-btn-primary")
                .FirstOrDefault(btn => btn.TextContent.Contains("Selected"));
            selectButton.Should().NotBeNull("Selected button should be found");
            selectButton?.HasAttribute("disabled").Should().BeTrue("Selected button should be disabled");

            // Identity-only: Workspace artifacts remain empty
            _workspace.GetAllArtifacts().Should().BeEmpty("Workspace artifacts should not be copied for Sample Projects");

            // Verify CurrentProject remains canonical slug, not display name
            _workspace.CurrentProject.Should().Be("autorisasjon", "Internal identity must be slug");
        });
    }

    [Fact]
    public void SelectingProject_UpdatesActiveSelectedState()
    {
        // Arrange: No project selected initially
        _workspace.CurrentProject = null;

        // Act: Render and select Autorisasjon
        var cut = Render<SampleProjects>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Autorisasjon"), TimeSpan.FromSeconds(5));

        var projectCards = cut.FindAll(".sp-card");
        var autorisasjonCard = projectCards.Single(card => card.TextContent.Contains("Autorisasjon", StringComparison.Ordinal));
        var selectButton = autorisasjonCard.QuerySelector("button.sp-btn-primary");
        selectButton.Should().NotBeNull("Select button should exist");
        selectButton!.Click();

        // Assert: Selection is reflected in UI
        cut.WaitForAssertion(() =>
        {
            // Banner updated with display name
            var bannerText = cut.Find(".sp-workspace-banner").TextContent;
            bannerText.Should().Contain("Selected Sample Project:");
            bannerText.Should().Contain("Autorisasjon");

            // Active card shows "✓ Selected"
            cut.Markup.Should().Contain("✓ Selected");

            // Button is now disabled and shows "✓ Selected"
            var updatedButton = cut.FindAll("button.sp-btn-primary")
                .FirstOrDefault(btn => btn.TextContent.Contains("Selected"));
            updatedButton.Should().NotBeNull("Selected button should exist after selection");
            updatedButton!.HasAttribute("disabled").Should().BeTrue("Selected button should be disabled");

            // CurrentProject updated to canonical slug
            _workspace.CurrentProject.Should().Be("autorisasjon");

            // Identity-only architecture preserved
            _workspace.GetAllArtifacts().Should().BeEmpty("No artifacts should be copied");
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SelectingDifferentProject_MovesActiveIndicator()
    {
        // Arrange: Autorisasjon selected initially
        _workspace.CurrentProject = "autorisasjon";

        var cut = Render<SampleProjects>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("✓ Selected"), TimeSpan.FromSeconds(5));

        // Act: Select Person Module instead
        var projectCards = cut.FindAll(".sp-card");
        var personCard = projectCards.Single(card => card.TextContent.Contains("Person Module", StringComparison.Ordinal));
        var selectButton = personCard.QuerySelector("button.sp-btn-primary");
        selectButton.Should().NotBeNull("Select button should exist");
        selectButton!.Click();

        // Assert: Only Person Module is selected now
        cut.WaitForAssertion(() =>
        {
            // CurrentProject switched to new project
            _workspace.CurrentProject.Should().Be("person-module");

            // Banner shows new project display name
            var bannerText = cut.Find(".sp-workspace-banner").TextContent;
            bannerText.Should().Contain("Selected Sample Project:");
            bannerText.Should().Contain("Person Module");

            // Exactly one "✓ Selected" badge exists
            var selectedBadges = cut.FindAll(".sp-card-selected-badge");
            selectedBadges.Should().HaveCount(1, "Only one project can be selected");

            // Exactly one disabled selected button exists
            var selectedButtons = cut.FindAll("button.sp-btn-primary[disabled]")
                .Where(btn => btn.TextContent.Contains("Selected"))
                .ToList();
            selectedButtons.Should().HaveCount(1, "Only one button should be selected/disabled");

            // Identity-only architecture preserved
            _workspace.GetAllArtifacts().Should().BeEmpty("No artifacts should be copied");
        }, TimeSpan.FromSeconds(5));
    }

    private static SampleProjectDto CreateProject(string slug, string name, string domain)
    {
        var files = new[]
        {
            Supported("constitution.md", "constitution", "Constitution Explorer", "/constitution-explorer"),
            Supported("spec.md", "spec", "Specification Explorer", "/specification-explorer"),
            Supported("data-model.md", "datamodel", "Data Model Explorer", "/data-model-explorer"),
            Supported("plan.md", "plan", "Plan Explorer", "/plan-explorer"),
            Supported("tasks.md", "tasks", "Task Explorer", "/task-explorer")
        };

        return new SampleProjectDto(slug, name, domain, "", $"C:\\SampleData\\{slug}", true, files);

        static SampleFileDto Supported(string filename, string artifactKind, string reviewerName, string route) =>
            new(filename, true, artifactKind, reviewerName, route, true, false);
    }

    private sealed class SampleProjectsHttpHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly Dictionary<string, SampleProjectDto> _projects = new(StringComparer.OrdinalIgnoreCase);

        public void SetProjects(params SampleProjectDto[] projects)
        {
            foreach (var project in projects)
                _projects[project.Slug] = project;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');

            if (request.Method == HttpMethod.Get && path == "api/sample-projects")
                return Json(_projects.Values.ToList());

            if (request.Method == HttpMethod.Get && path == "api/sample-projects/meta")
                return Json(new SampleProjectsMetaDto("C:\\SampleData", "test", true));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private Task<HttpResponseMessage> Json<T>(T data)
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
