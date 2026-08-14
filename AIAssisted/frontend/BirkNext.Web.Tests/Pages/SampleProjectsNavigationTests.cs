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
    [InlineData("Constitution Explorer", "/constitution-explorer", WorkspaceArtifactKind.Constitution, "PERSON constitution.md")]
    [InlineData("Plan Explorer", "/plan-explorer", WorkspaceArtifactKind.Plan, "PERSON plan.md")]
    [InlineData("Task Explorer", "/task-explorer", WorkspaceArtifactKind.Tasks, "PERSON tasks.md")]
    [InlineData("Data Model Explorer", "/data-model-explorer", WorkspaceArtifactKind.DataModel, "PERSON data-model.md")]
    [InlineData("Specification Review", "/extract", WorkspaceArtifactKind.Specification, "PERSON spec.md")]
    public void ReviewerLinksLoadSelectedProjectBeforeNavigating(
        string reviewerName,
        string expectedRoute,
        WorkspaceArtifactKind expectedKind,
        string expectedText)
    {
        var cut = Render<SampleProjects>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Person Module"));

        ClickSupportedReviewer(cut, "person-module", reviewerName);

        cut.WaitForAssertion(() =>
        {
            Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith(expectedRoute);
            _workspace.CurrentProject.Should().Be("Person Module");
            _workspace.Get(expectedKind)!.Text.Should().Be(expectedText);
        });
    }

    [Fact]
    public void ProjectSwitchingReplacesAllWorkspaceArtifacts()
    {
        var cut = Render<SampleProjects>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Person Module"));

        ClickSupportedReviewer(cut, "person-module", "Task Explorer");
        cut.WaitForAssertion(() => _workspace.Get(WorkspaceArtifactKind.Tasks)!.Text.Should().Be("PERSON tasks.md"));

        ClickSupportedReviewer(cut, "proxy", "Data Model Explorer");

        cut.WaitForAssertion(() =>
        {
            Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("/data-model-explorer");
            _workspace.CurrentProject.Should().Be("Proxy");
            _workspace.Get(WorkspaceArtifactKind.Constitution)!.Text.Should().Be("PROXY constitution.md");
            _workspace.Get(WorkspaceArtifactKind.Specification)!.Text.Should().Be("PROXY spec.md");
            _workspace.Get(WorkspaceArtifactKind.DataModel)!.Text.Should().Be("PROXY data-model.md");
            _workspace.Get(WorkspaceArtifactKind.Plan)!.Text.Should().Be("PROXY plan.md");
            _workspace.Get(WorkspaceArtifactKind.Tasks)!.Text.Should().Be("PROXY tasks.md");
        });
    }

    [Fact]
    public void FailedReviewerLoadDoesNotNavigateAndClearsStaleWorkspace()
    {
        var cut = Render<SampleProjects>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Person Module"));

        ClickSupportedReviewer(cut, "person-module", "Task Explorer");
        cut.WaitForAssertion(() => _workspace.CurrentProject.Should().Be("Person Module"));

        var navigation = Services.GetRequiredService<NavigationManager>();
        var previousUri = navigation.Uri;
        _handler.FailFile("proxy", "data-model.md");

        ClickSupportedReviewer(cut, "proxy", "Data Model Explorer");

        cut.WaitForAssertion(() =>
        {
            navigation.Uri.Should().Be(previousUri);
            _workspace.CurrentProject.Should().BeNull();
            _workspace.Get(WorkspaceArtifactKind.Constitution).Should().BeNull();
            _workspace.Get(WorkspaceArtifactKind.Specification).Should().BeNull();
            _workspace.Get(WorkspaceArtifactKind.DataModel).Should().BeNull();
            _workspace.Get(WorkspaceArtifactKind.Plan).Should().BeNull();
            _workspace.Get(WorkspaceArtifactKind.Tasks).Should().BeNull();
            cut.Markup.Should().Contain("Failed to fetch");
        });
    }

    [Fact]
    public void ManualLoadSupportedArtifactsStillLoadsWorkspace()
    {
        var cut = Render<SampleProjects>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Person Module"));

        var firstLoadButton = cut.FindAll("button")
            .First(button => button.TextContent.Contains("Load Supported Artifacts", StringComparison.Ordinal));
        firstLoadButton.Click();

        cut.WaitForAssertion(() =>
        {
            _workspace.CurrentProject.Should().Be("Person Module");
            _workspace.Get(WorkspaceArtifactKind.Constitution)!.Text.Should().Be("PERSON constitution.md");
            _workspace.Get(WorkspaceArtifactKind.Specification)!.Text.Should().Be("PERSON spec.md");
            _workspace.Get(WorkspaceArtifactKind.DataModel)!.Text.Should().Be("PERSON data-model.md");
            _workspace.Get(WorkspaceArtifactKind.Plan)!.Text.Should().Be("PERSON plan.md");
            _workspace.Get(WorkspaceArtifactKind.Tasks)!.Text.Should().Be("PERSON tasks.md");
        });
    }

    [Fact]
    public void ReviewerClickFetchesEachSupportedFileOnlyOnce()
    {
        var cut = Render<SampleProjects>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Person Module"));

        ClickSupportedReviewer(cut, "person-module", "Plan Explorer");

        cut.WaitForAssertion(() =>
        {
            _handler.GetFileCount("person-module", "constitution.md").Should().Be(1);
            _handler.GetFileCount("person-module", "spec.md").Should().Be(1);
            _handler.GetFileCount("person-module", "data-model.md").Should().Be(1);
            _handler.GetFileCount("person-module", "plan.md").Should().Be(1);
            _handler.GetFileCount("person-module", "tasks.md").Should().Be(1);
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
            Supported("spec.md", "spec", "Specification Review", "/extract"),
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
