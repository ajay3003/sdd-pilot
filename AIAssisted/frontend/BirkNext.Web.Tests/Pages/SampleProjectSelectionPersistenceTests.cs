using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Web.Layout;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The selected Sample Project is an independent persisted user choice (canonical slug in the auto-saved workspace's
/// ProjectName, restored by MainLayout on startup). It must only change when the user explicitly selects or clears it:
/// never through Target Environment actions, never by a first-project fallback, and never by a restart.
///
/// Root cause covered here: a Sample Project selection is persisted identity-only (zero artifact copies), and the
/// startup restore skipped every workspace without artifacts, so a full application restart lost the selection while
/// the backend still held it.
/// </summary>
public sealed class SampleProjectSelectionPersistenceTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly SampleProjectsHttpHandler _catalog = new();
    private readonly Mock<IWorkspacePersistenceApiService> _persistenceApi = new();
    private readonly Mock<IWorkspaceAutoSaveService> _autoSave = new();
    private readonly Mock<IReviewContextProvider> _reviewContext = new();
    private readonly WorkspaceStateManager _stateManager = new();

    public SampleProjectSelectionPersistenceTests()
    {
        _catalog.SetProjects(Project("autorisasjon", "Autorisasjon"), Project("person-module", "Person Module"));
        _autoSave.Setup(x => x.StartMonitoringAsync()).Returns(Task.CompletedTask);
        _autoSave.Setup(x => x.StopMonitoringAsync()).Returns(Task.CompletedTask);
        _autoSave.Setup(x => x.SaveNowAsync()).ReturnsAsync(true);
        _reviewContext.Setup(x => x.RebuildAsync()).Returns(Task.CompletedTask);

        var catalogClient = new HttpClient(_catalog) { BaseAddress = new Uri("http://localhost/") };
        var sampleProjectsApi = new SampleProjectsApiService(catalogClient);

        Services.AddSingleton<IWorkspaceArtifactRepository>(_workspace);
        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<IWorkspaceArtifactStatusService>(sp => new WorkspaceArtifactStatusService(sp.GetRequiredService<IWorkspaceSessionService>()));
        Services.AddSingleton<IWorkspaceUpdateCoordinator, WorkspaceUpdateCoordinator>();
        Services.AddSingleton<IWorkspaceStateManager>(_stateManager);
        Services.AddSingleton(_autoSave.Object);
        Services.AddSingleton(_persistenceApi.Object);
        // Real restore service: the startup path under test is MainLayout → restore service → repository.
        Services.AddSingleton<IWorkspaceSessionRestoreService>(new WorkspaceSessionRestoreService(_workspace, _stateManager, _reviewContext.Object, sampleProjectsApi, NullLogger<WorkspaceSessionRestoreService>.Instance));
        Services.AddSingleton(new QualityReviewSessionService());
        Services.AddSingleton(Mock.Of<IDashboardSnapshotService>());
        Services.AddSingleton(new FeatureVisibilityService());
        Services.AddSingleton(new AdminApiService(new HttpClient(new NotFoundHandler()) { BaseAddress = new Uri("http://localhost/") }));
        Services.AddSingleton<ITargetEnvironmentHintExtractor>(new TargetEnvironmentHintExtractor());
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddSingleton(Mock.Of<IIntegrationTargetRegistryService>());
        Services.AddSingleton(NullLogger<MainLayout>.Instance);
        Services.AddSingleton(NullLogger<SampleProjects>.Instance);
        Services.AddSingleton(sampleProjectsApi);
        JSInterop.Setup<bool>("confirm", _ => true).SetResult(true);
    }

    // ── Restart (fresh application instance) ─────────────────────────────────────

    [Fact]
    public void Restart_IdentityOnlySelection_IsRestoredWithoutArtifactCopies()
    {
        // A selected Sample Project is persisted as slug only: the auto-saved workspace has zero artifacts.
        var id = Guid.NewGuid();
        _persistenceApi.Setup(x => x.GetCurrentStateAsync()).ReturnsAsync(new CurrentWorkspaceStateDto { CurrentWorkspaceId = id, ProjectName = "person-module", ArtifactCount = 0, Status = "AutoSaved" });
        _persistenceApi.Setup(x => x.LoadAsync(id)).ReturnsAsync(new SavedWorkspaceDto { Id = id, Name = "Auto_1", ProjectName = "person-module", AutoSaved = true, Artifacts = new() });
        _workspace.CurrentProject.Should().BeNull("a fresh application instance starts empty");

        var cut = Render<MainLayout>();

        cut.WaitForAssertion(() => _workspace.CurrentProject.Should().Be("person-module"));
        _workspace.GetAllArtifacts().Should().BeEmpty("identity-only persistence never restores artifact copies");
        _stateManager.CurrentWorkspaceId.Should().Be(id);
        _persistenceApi.Verify(x => x.LoadAsync(id), Times.Once);
    }

    [Fact]
    public void Restart_SecondProjectSelected_RestoresThatProjectNotTheFirstInCatalog()
    {
        var id = Guid.NewGuid();
        _persistenceApi.Setup(x => x.GetCurrentStateAsync()).ReturnsAsync(new CurrentWorkspaceStateDto { CurrentWorkspaceId = id, ProjectName = "person-module", ArtifactCount = 0 });
        _persistenceApi.Setup(x => x.LoadAsync(id)).ReturnsAsync(new SavedWorkspaceDto { Id = id, Name = "Auto_1", ProjectName = "person-module", Artifacts = new() });

        Render<MainLayout>();
        var page = Render<SampleProjects>();

        page.WaitForAssertion(() => page.Find("[data-testid=sp-selected-project]").TextContent.Should().Contain("Person Module"));
        page.FindAll(".sp-card-selected").Should().HaveCount(1);
        page.Find(".sp-card-selected").TextContent.Should().Contain("Person Module").And.NotContain("Autorisasjon");
    }

    [Fact]
    public void Restart_ClearedSelection_StaysEmpty_NoFirstProjectFallback()
    {
        var id = Guid.NewGuid();
        _persistenceApi.Setup(x => x.GetCurrentStateAsync()).ReturnsAsync(new CurrentWorkspaceStateDto { CurrentWorkspaceId = id, ProjectName = "", ArtifactCount = 0 });
        _persistenceApi.Setup(x => x.LoadAsync(id)).ReturnsAsync(new SavedWorkspaceDto { Id = id, Name = "Auto_1", ProjectName = "", Artifacts = new() });

        Render<MainLayout>();
        var page = Render<SampleProjects>();

        page.WaitForAssertion(() => page.FindAll(".sp-card").Should().HaveCount(2));
        _workspace.CurrentProject.Should().BeNull();
        page.FindAll(".sp-card-selected").Should().BeEmpty("no project may be selected on the user's behalf");
        page.FindAll("[data-testid=sp-selected-project]").Should().BeEmpty();
        page.FindAll("[data-testid=sp-selection-unavailable]").Should().BeEmpty();
    }

    [Fact]
    public void Restart_LegacyWorkspaceWithoutProjectName_RestoresNothing()
    {
        _persistenceApi.Setup(x => x.GetCurrentStateAsync()).ReturnsAsync(new CurrentWorkspaceStateDto { CurrentWorkspaceId = null, Status = "NotSaved", ArtifactCount = 0 });

        Render<MainLayout>();

        _workspace.CurrentProject.Should().BeNull();
        _persistenceApi.Verify(x => x.LoadAsync(It.IsAny<Guid>()), Times.Never);
    }

    // ── Missing project ─────────────────────────────────────────────────────────

    [Fact]
    public void PersistedProjectRemovedFromCatalog_ShowsExplicitUnavailableState_NoSubstitution()
    {
        var id = Guid.NewGuid();
        _persistenceApi.Setup(x => x.GetCurrentStateAsync()).ReturnsAsync(new CurrentWorkspaceStateDto { CurrentWorkspaceId = id, ProjectName = "removed-project", ArtifactCount = 0 });
        _persistenceApi.Setup(x => x.LoadAsync(id)).ReturnsAsync(new SavedWorkspaceDto { Id = id, Name = "Auto_1", ProjectName = "removed-project", Artifacts = new() });

        Render<MainLayout>();
        var page = Render<SampleProjects>();

        page.WaitForAssertion(() => page.Find("[data-testid=sp-selection-unavailable]").TextContent.Should().Contain("removed-project").And.Contain("unavailable"));
        _workspace.CurrentProject.Should().Be("removed-project", "the stale id is kept as-is until the user decides");
        page.FindAll(".sp-card-selected").Should().BeEmpty("no other project is selected automatically");
        page.FindAll("button.sp-btn-primary[disabled]").Should().BeEmpty("both catalog projects remain selectable");

        page.Find("[data-testid=sp-clear-selection]").Click();

        page.WaitForAssertion(() => _workspace.CurrentProject.Should().BeNull());
        _autoSave.Verify(x => x.SaveNowAsync(), Times.Once, "the explicit clear is persisted immediately");
        page.FindAll("[data-testid=sp-selection-unavailable]").Should().BeEmpty();
    }

    // ── Explicit selection: persisted immediately, reverted on save failure ──────

    [Fact]
    public void SelectProject_PersistsImmediately_AndUpdatesSelection()
    {
        var page = Render<SampleProjects>();
        page.WaitForAssertion(() => page.FindAll(".sp-card").Should().HaveCount(2));

        page.FindAll(".sp-card").Single(c => c.TextContent.Contains("Person Module")).QuerySelector("button.sp-btn-primary")!.Click();

        page.WaitForAssertion(() => _workspace.CurrentProject.Should().Be("person-module"));
        _autoSave.Verify(x => x.SaveNowAsync(), Times.Once);
        page.Find("[data-testid=sp-selected-project]").TextContent.Should().Contain("Person Module");
    }

    [Fact]
    public void SelectProject_SaveFails_PreviousSelectionRestored_MessageShown()
    {
        _workspace.CurrentProject = "autorisasjon";
        _autoSave.Setup(x => x.SaveNowAsync()).ReturnsAsync(false);
        var page = Render<SampleProjects>();
        page.WaitForAssertion(() => page.FindAll(".sp-card").Should().HaveCount(2));

        page.FindAll(".sp-card").Single(c => c.TextContent.Contains("Person Module")).QuerySelector("button.sp-btn-primary")!.Click();

        page.WaitForAssertion(() => page.Find("[data-testid=sp-selection-error]").TextContent.Should().Contain("Could not save the selection").And.Contain("previous selection was kept"));
        _workspace.CurrentProject.Should().Be("autorisasjon", "the UI never shows a selection that was not persisted");
        page.Find("[data-testid=sp-selected-project]").TextContent.Should().Contain("Autorisasjon");
    }

    // ── Independence from Target Environment settings ───────────────────────────

    [Fact]
    public async Task TargetEnvironmentActions_NeverTouchSampleProjectSelection()
    {
        _workspace.CurrentProject = "person-module";
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<string?>("birkNextStorage.getItem", It.IsAny<object[]>())).ReturnsAsync((string?)null);
        js.Setup(j => j.InvokeAsync<IJSVoidResult>("birkNextStorage.setItem", It.IsAny<object[]>())).ReturnsAsync(Mock.Of<IJSVoidResult>());
        var settings = new FrontendAnalysisSettingsService();
        var dev = settings.CreateProfile("M2LB DEV", FrontendEnvironmentType.Development);
        var qa = settings.CreateProfile("M2LB QA", FrontendEnvironmentType.QA);
        var changes = 0;
        _workspace.ProjectSelectionChanged += (_, _) => changes++;

        settings.SelectActiveProfile(qa.Id);                      // Set as Active
        await settings.SaveAsync(js.Object);                      // Save
        dev.Features.EnableAccessibilityEngine = false;           // engine configuration change
        settings.UpdateProfile(dev);
        await settings.SaveAsync(js.Object);
        var copy = settings.DuplicateProfile(dev.Id);             // Duplicate
        await settings.SaveAsync(js.Object);
        settings.ResetProfile(qa.Id);                             // Reset profile
        await settings.SaveAsync(js.Object);
        settings.SelectActiveProfile(copy.Id);                    // Set another environment active
        await settings.SaveAsync(js.Object);
        settings.DeleteProfile(copy.Id);
        await settings.SaveAsync(js.Object);
        await settings.LoadAsync(js.Object);                      // Cancel/reload of saved settings

        _workspace.CurrentProject.Should().Be("person-module");
        changes.Should().Be(0, "settings actions must not even signal a selection change");
        var settingsJson = JsonSerializer.Serialize(settings.Settings);
        settingsJson.Should().NotContain("person-module", "the Sample Project selection is not stored inside Target Environment settings");
        settingsJson.Should().Contain("activeProfileId", "activeProfileId and the Sample Project selection are independent pointers");
    }

    [Fact]
    public void NavigationAwayAndBack_KeepsSelection()
    {
        _workspace.CurrentProject = "person-module";
        var first = Render<SampleProjects>();
        first.WaitForAssertion(() => first.Find("[data-testid=sp-selected-project]").TextContent.Should().Contain("Person Module"));

        // Navigating away and back creates a new page instance over the same singleton session state.
        var second = Render<SampleProjects>();

        second.WaitForAssertion(() => second.Find("[data-testid=sp-selected-project]").TextContent.Should().Contain("Person Module"));
        _workspace.CurrentProject.Should().Be("person-module");
        _autoSave.Verify(x => x.SaveNowAsync(), Times.Never, "rendering never rewrites the selection");
    }

    // ── Persistence wire format ─────────────────────────────────────────────────

    [Fact]
    public async Task AutoSaveRequest_CarriesSlugWhenSelected_AndEmptyStringWhenCleared()
    {
        var handler = new CapturingHandler();
        var service = new WorkspacePersistenceApiService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<WorkspacePersistenceApiService>.Instance, _workspace);

        _workspace.CurrentProject = "person-module";
        await service.AutoSaveAsync();
        _workspace.CurrentProject = null;
        await service.AutoSaveAsync();

        handler.Bodies.Should().HaveCount(2);
        JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("projectName").GetString().Should().Be("person-module");
        JsonDocument.Parse(handler.Bodies[1]).RootElement.GetProperty("projectName").GetString().Should().Be("", "an explicit clear is sent as empty string so the backend persists it");
    }

    [Fact]
    public void SerializationRoundTrip_KeepsProjectIdentityAndActiveProfileIndependent()
    {
        var workspace = new SavedWorkspaceDto { Id = Guid.NewGuid(), Name = "Auto_1", ProjectName = "person-module", Artifacts = new() };
        var settings = new FrontendAnalysisSettings { ActiveProfileId = "qa", Profiles = [new FrontendAnalysisProfile { Id = "qa", Name = "QA" }] };
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var workspaceBack = JsonSerializer.Deserialize<SavedWorkspaceDto>(JsonSerializer.Serialize(workspace, web), web)!;
        var settingsBack = JsonSerializer.Deserialize<FrontendAnalysisSettings>(JsonSerializer.Serialize(settings, web), web)!;
        var stateBack = JsonSerializer.Deserialize<CurrentWorkspaceStateDto>("{\"currentWorkspaceId\":\"" + workspace.Id + "\",\"projectName\":\"person-module\",\"artifactCount\":0,\"status\":\"AutoSaved\"}", web)!;

        workspaceBack.ProjectName.Should().Be("person-module");
        workspaceBack.Artifacts.Should().BeEmpty();
        settingsBack.ActiveProfileId.Should().Be("qa");
        stateBack.ProjectName.Should().Be("person-module");
    }

    // ── Auto-save timing: a throttled change is re-armed, never dropped ─────────

    [Fact]
    public async Task AutoSave_ThrottledChange_IsSavedWhenTheWindowOpens()
    {
        var saveTimes = new List<DateTime>();
        var persistence = new Mock<IWorkspacePersistenceApiService>();
        persistence.Setup(x => x.AutoSaveAsync(It.IsAny<string?>())).ReturnsAsync(() => { lock (saveTimes) saveTimes.Add(DateTime.UtcNow); return new SavedWorkspaceDto { Id = Guid.NewGuid(), Name = "Auto" }; });
        var repo = new WorkspaceArtifactRepository();
        var service = new WorkspaceAutoSaveService(repo, persistence.Object, Mock.Of<IWorkspaceSessionRestoreService>(), new WorkspaceUpdateCoordinator(), NullLogger<WorkspaceAutoSaveService>.Instance, autoSaveIntervalMs: 30, autoSaveThrottleMs: 400);

        (await service.SaveNowAsync()).Should().BeTrue();
        repo.CurrentProject = "autorisasjon"; // change immediately after a save, i.e. inside the throttle window

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (SaveCount() < 2 && DateTime.UtcNow < deadline) await Task.Delay(25);

        SaveCount().Should().Be(2, "the throttled change is re-armed and saved once the window opens instead of being dropped");
        (saveTimes[1] - saveTimes[0]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(380), "the throttle window is still honoured");
        int SaveCount() { lock (saveTimes) return saveTimes.Count; }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static SampleProjectDto Project(string slug, string name) =>
        new(slug, name, "Domain", "", $"C:\\SampleData\\{slug}", false,
            [new SampleFileDto("spec.md", true, "spec", "Specification Explorer", "/specification-explorer", true, false)]);

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"Auto\",\"projectName\":\"\",\"artifacts\":[]}", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class SampleProjectsHttpHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly Dictionary<string, SampleProjectDto> _projects = new(StringComparer.OrdinalIgnoreCase);
        public void SetProjects(params SampleProjectDto[] projects) { foreach (var p in projects) _projects[p.Slug] = p; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');
            if (request.Method == HttpMethod.Get && path == "api/sample-projects") return Json(_projects.Values.ToList());
            if (request.Method == HttpMethod.Get && path == "api/sample-projects/meta") return Json(new SampleProjectsMetaDto("C:\\SampleData", "test", true));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        private static Task<HttpResponseMessage> Json<T>(T data) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data, JsonOptions), Encoding.UTF8, "application/json") });
    }
}
