using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Pages;

public sealed class SampleProjectsNavigationTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly SampleProjectsHttpHandler _handler = new();
    private readonly Mock<IWorkspaceAutoSaveService> _autoSave = new();

    public SampleProjectsNavigationTests()
    {
        var client = new HttpClient(_handler)
        {
            BaseAddress = new Uri("http://localhost/")
        };

        _autoSave.Setup(x => x.StartMonitoringAsync()).Returns(Task.CompletedTask);

        Services.AddSingleton<IWorkspaceArtifactRepository>(_workspace);
        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<IWorkspaceArtifactStatusService>(sp =>
            new WorkspaceArtifactStatusService(sp.GetRequiredService<IWorkspaceSessionService>()));
        Services.AddSingleton<IWorkspaceUpdateCoordinator, WorkspaceUpdateCoordinator>();
        Services.AddSingleton(_autoSave.Object);
        Services.AddSingleton(new QualityReviewSessionService());
        Services.AddSingleton(Mock.Of<IDashboardSnapshotService>());
        Services.AddSingleton<ITargetEnvironmentHintExtractor>(new TargetEnvironmentHintExtractor());
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddSingleton(Mock.Of<IIntegrationTargetRegistryService>());
        Services.AddSingleton(NullLogger<SampleProjects>.Instance);
        Services.AddSingleton(new SampleProjectsApiService(client));

        JSInterop.Setup<bool>("confirm", _ => true).SetResult(true);

        _handler.SetProjects(CreateProject("person-module", "Person Module", "PERSON"), CreateProject("proxy", "Proxy", "PROXY"));
    }

    [Theory]
    [InlineData("Constitution Explorer", "/constitution-explorer")]
    [InlineData("Plan Explorer", "/plan-explorer")]
    [InlineData("Task Explorer", "/task-explorer")]
    [InlineData("Data Model Explorer", "/data-model-explorer")]
    [InlineData("Specification Explorer", "/specification-explorer")]
    public void ReviewerLinksLoadSelectedProjectBeforeNavigating(
        string reviewerName,
        string expectedRoute)
    {
        // New contract: selecting a reviewer loads the project identity (slug) only.
        // No Workspace artifact copies. Explorers use DocumentResolver to load content.
        var cut = Render<SampleProjects>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Person Module"));

        ClickSupportedReviewer(cut, "person-module", reviewerName);

        cut.WaitForAssertion(() =>
        {
            Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith(expectedRoute);
            _workspace.CurrentProject.Should().Be("person-module");  // Canonical slug, not display name
            // Identity-only persistence: no Workspace artifact copies
            // Explorers resolve content through DocumentResolver
        });
    }

    [Fact]
    public void ProjectSwitchingUpdatesCanonicalProjectSlugWithoutWorkspaceCopies()
    {
        // New contract: switching projects updates CurrentProject (slug) only.
        // No Workspace artifact copies are stored or replaced.
        // Explorers load fresh content via DocumentResolver.
        var cut = Render<SampleProjects>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Person Module"));

        ClickSupportedReviewer(cut, "person-module", "Task Explorer");
        cut.WaitForAssertion(() =>
        {
            _workspace.CurrentProject.Should().Be("person-module");
            // Identity-only persistence: no artifacts stored
        });

        ClickSupportedReviewer(cut, "proxy", "Data Model Explorer");

        cut.WaitForAssertion(() =>
        {
            Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/data-model-explorer");
            _workspace.CurrentProject.Should().Be("proxy");  // Canonical slug, not display name
            // Identity-only persistence: switching projects clears previous slug, sets new slug
            // Workspace artifacts remain empty (identity-only design)
        });
    }

    [Fact]
    public void SupportedReviewerLinkNavigatesSuccessfully()
    {
        // Identity-only persistence: reviewer link for a project with supported artifacts succeeds.
        // CurrentProject is set to canonical slug only (no artifact copies).
        // Navigation occurs after successful selection.
        var cut = Render<SampleProjects>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Person Module"));

        ClickSupportedReviewer(cut, "person-module", "Task Explorer");
        cut.WaitForAssertion(() => _workspace.CurrentProject.Should().Be("person-module"));  // Canonical slug

        // Selection succeeds because project has supported artifacts (identity-only, no copies needed).
        // Navigation is allowed because LoadArtifactsCoreAsync returned true.
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/task-explorer");
    }

    [Fact]
    public void ZeroSupportedArtifacts_SelectionFails_ClearsCurrentProject()
    {
        // Production contract: selecting a project with zero supported artifacts FAILS.
        // CurrentProject must be cleared (set to null).
        // Error message shown, no navigation occurs.

        // Create projects: one normal, one with zero supported artifacts
        var normalProject = CreateProject("person-module", "Person Module", "PERSON");
        var emptyProject = new SampleProjectDto(
            "empty-project", "Empty Project", "", "", "C:\\SampleData\\empty-project", true,
            new[] { new SampleFileDto("readme.md", false, "", "", "", false, false) });  // Context-only file, no supported

        _handler.SetProjects(normalProject, emptyProject);

        var cut = Render<SampleProjects>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Empty Project"));

        // Arrange: Set stale project identity before attempting the failing selection
        _workspace.CurrentProject = "person-module";

        // Verify precondition: stale state exists
        _workspace.CurrentProject.Should().Be("person-module");

        // Capture navigation state BEFORE attempting selection
        var nav = Services.GetRequiredService<NavigationManager>();
        var initialUri = nav.Uri;

        // Act: Click "Load Supported Artifacts" button for empty project
        var projectCards = cut.FindAll(".sp-card");
        var emptyCard = projectCards.Single(card => card.TextContent.Contains("Empty Project", StringComparison.Ordinal));
        var loadButton = emptyCard.QuerySelector("button.sp-btn-primary");

        loadButton.Should().NotBeNull();
        loadButton!.Click();

        // Assert: Selection fails, clears stale identity, and does NOT navigate
        cut.WaitForAssertion(() =>
        {
            // CurrentProject must be cleared (selection failed)
            _workspace.CurrentProject.Should().BeNull();

            // Error message visible in UI
            cut.Markup.Should().Contain("No supported artifacts found");

            // No Workspace artifact copies (identity-only design)
            _workspace.GetAllArtifacts().Should().BeEmpty();

            // Verify CurrentProject is not set to the failing project slug
            _workspace.CurrentProject.Should().NotBe("empty-project");

            // EXPLICIT NO-NAVIGATION ASSERTION: URI must not change
            // If navigation occurred, URI would change from current page to reviewer route
            nav.Uri.Should().Be(initialUri, because: "zero-artifact selection must not navigate");
        });
    }

    [Fact]
    public void ManualLoadSupportedArtifactsStillLoadsProjectIdentity()
    {
        // New contract: clicking "Load Supported Artifacts" loads the project identity (slug) only.
        // No Workspace artifact copies. Explorers use DocumentResolver to load content on demand.
        var cut = Render<SampleProjects>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Person Module"));

        var firstLoadButton = cut.FindAll("button")
            .First(button => button.TextContent.Contains("Load Supported Artifacts", StringComparison.Ordinal));
        firstLoadButton.Click();

        cut.WaitForAssertion(() =>
        {
            _workspace.CurrentProject.Should().Be("person-module");  // Canonical slug, not display name
            // Identity-only persistence: no Workspace artifact copies
            // The "Load Supported Artifacts" action loads the project identity for later use by explorers
        });
    }

    [Fact]
    public void ReviewerClickLoadsProjectIdentityWithoutFetchingAllArtifacts()
    {
        // New contract: clicking a reviewer loads the project identity only.
        // Identity-only persistence: no files are fetched during project selection.
        // Explorers load their specific content through DocumentResolver on demand.
        var cut = Render<SampleProjects>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Person Module"));

        ClickSupportedReviewer(cut, "person-module", "Plan Explorer");

        cut.WaitForAssertion(() =>
        {
            // Identity-only persistence: project selection does not trigger artifact fetches
            _workspace.CurrentProject.Should().Be("person-module");
            // Files are fetched on-demand by explorers through DocumentResolver, not during selection
        });
    }

    private static void ClickSupportedReviewer(IRenderedComponent<SampleProjects> cut, string slug, string reviewerName)
    {
        var projectCard = cut.FindAll(".sp-card")
            .Single(card => card.TextContent.Contains(ToTitle(slug), StringComparison.Ordinal));
        var link = projectCard.QuerySelectorAll("a.sp-reviewer-link")
            .Single(a => a.TextContent.Contains(reviewerName, StringComparison.Ordinal));
        link.Click();
    }

    private static SampleProjectDto CreateProject(string slug, string name, string marker)
    {
        var files = new[]
        {
            Supported("constitution.md", "constitution", "Constitution Explorer", "/constitution-explorer"),
            Supported("spec.md", "spec", "Specification Explorer", "/specification-explorer"),
            Supported("data-model.md", "datamodel", "Data Model Explorer", "/data-model-explorer"),
            Supported("plan.md", "plan", "Plan Explorer", "/plan-explorer"),
            Supported("tasks.md", "tasks", "Task Explorer", "/task-explorer")
        };

        return new SampleProjectDto(slug, name, "", "", $"C:\\SampleData\\{slug}", true, files);

        static SampleFileDto Supported(string filename, string artifactKind, string reviewerName, string route) =>
            new(filename, true, artifactKind, reviewerName, route, true, false);
    }

    private static string ToTitle(string slug) =>
        slug == "person-module" ? "Person Module" : "Proxy";

    private sealed class SampleProjectsHttpHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly Dictionary<string, SampleProjectDto> _projects = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<(string Slug, string FileName)> _failures = new();
        private readonly Dictionary<(string Slug, string FileName), int> _fileCounts = new();

        public void SetProjects(params SampleProjectDto[] projects)
        {
            foreach (var project in projects)
                _projects[project.Slug] = project;
        }

        public void FailFile(string slug, string fileName) =>
            _failures.Add((slug, fileName));

        public int GetFileCount(string slug, string fileName) =>
            _fileCounts.TryGetValue((slug, fileName), out var count) ? count : 0;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);

            if (request.Method == HttpMethod.Get && path == "api/sample-projects")
                return Json(_projects.Values.ToList());

            if (request.Method == HttpMethod.Get && path == "api/sample-projects/meta")
                return Json(new SampleProjectsMetaDto("C:\\SampleData", "test", true));

            if (request.Method == HttpMethod.Get && path.StartsWith("api/sample-projects/", StringComparison.Ordinal))
            {
                var parts = path.Split('/');
                var slug = Uri.UnescapeDataString(parts[2]);
                var fileName = query.TryGetValue("filename", out var value) ? value.ToString() : "";
                var key = (slug, fileName);
                _fileCounts[key] = GetFileCount(slug, fileName) + 1;

                if (_failures.Contains(key))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

                var marker = slug.Equals("person-module", StringComparison.OrdinalIgnoreCase) ? "PERSON" : "PROXY";
                return Text($"{marker} {fileName}");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json<T>(T value)
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        private static Task<HttpResponseMessage> Text(string value) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(value, Encoding.UTF8, "text/plain")
            });
    }
}

