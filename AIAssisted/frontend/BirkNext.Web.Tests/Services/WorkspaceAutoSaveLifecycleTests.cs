using System.Net;
using System.Text;
using BirkNext.Web.Layout;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Auto-save lifecycle: the unsaved state is cleared only by a backend confirmation of a save that included the change; a failed or
/// interrupted save stays unsaved and retryable; saves never overlap and an older confirmation never marks a newer change saved; in-app
/// navigation flushes unsaved changes; a fetch aborted by a page unload is "interrupted" (no error log), a real failure stays an error.
/// </summary>
public sealed class WorkspaceAutoSaveLifecycleTests : BunitContext
{
    // Long debounce/throttle: the timer never fires during a test, so every save below is the one the test triggers.
    private static WorkspaceAutoSaveService Service(WorkspaceArtifactRepository repo, IWorkspacePersistenceApiService persistence, ILogger<WorkspaceAutoSaveService>? logger = null) =>
        new(repo, persistence, Mock.Of<IWorkspaceSessionRestoreService>(), new WorkspaceUpdateCoordinator(),
            logger ?? NullLogger<WorkspaceAutoSaveService>.Instance, autoSaveIntervalMs: 600_000, autoSaveThrottleMs: 0);

    private static SavedWorkspaceDto Saved() => new() { Id = Guid.NewGuid(), Name = "Auto" };

    [Fact]
    public async Task A_confirmed_save_marks_the_workspace_saved()
    {
        var persistence = new Mock<IWorkspacePersistenceApiService>();
        persistence.Setup(p => p.AutoSaveAsync(It.IsAny<string?>())).ReturnsAsync(Saved());
        var service = Service(new WorkspaceArtifactRepository(), persistence.Object);

        service.OnArtifactChanged();
        service.HasUnsavedChanges.Should().BeTrue();
        service.SaveState.Should().Be(WorkspaceSaveState.Pending);

        (await service.FlushAsync()).Should().BeTrue();
        service.HasUnsavedChanges.Should().BeFalse();
        service.SaveState.Should().Be(WorkspaceSaveState.Saved);
    }

    [Fact]
    public async Task A_failed_save_stays_unsaved_and_a_retry_succeeds()
    {
        var persistence = new Mock<IWorkspacePersistenceApiService>();
        persistence.Setup(p => p.AutoSaveAsync(It.IsAny<string?>())).ReturnsAsync((SavedWorkspaceDto?)null);
        persistence.SetupGet(p => p.LastSaveError).Returns("The save was refused (500).");
        var service = Service(new WorkspaceArtifactRepository(), persistence.Object);

        service.OnArtifactChanged();
        (await service.FlushAsync()).Should().BeFalse();
        service.SaveState.Should().Be(WorkspaceSaveState.Failed);
        service.LastSaveError.Should().Be("The save was refused (500).");
        service.HasUnsavedChanges.Should().BeTrue("a failed save never clears the unsaved state");

        persistence.Setup(p => p.AutoSaveAsync(It.IsAny<string?>())).ReturnsAsync(Saved());
        persistence.SetupGet(p => p.LastSaveError).Returns((string?)null);
        (await service.SaveNowAsync()).Should().BeTrue();
        service.SaveState.Should().Be(WorkspaceSaveState.Saved);
        service.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task An_older_confirmation_never_marks_a_newer_change_saved_and_the_newest_state_wins()
    {
        var repo = new WorkspaceArtifactRepository();
        var sent = new List<string?>();
        var first = new TaskCompletionSource<SavedWorkspaceDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistence = new Mock<IWorkspacePersistenceApiService>();
        persistence.Setup(p => p.AutoSaveAsync(It.IsAny<string?>())).Returns(() =>
        {
            lock (sent)
            {
                sent.Add(repo.CurrentProject);
                return sent.Count == 1 ? first.Task : Task.FromResult<SavedWorkspaceDto?>(Saved());
            }
        });
        var service = Service(repo, persistence.Object);

        repo.CurrentProject = "first";              // change 1
        var save1 = service.FlushAsync();           // in flight, carries "first"
        service.SaveState.Should().Be(WorkspaceSaveState.Saving);

        repo.CurrentProject = "second";             // change 2 while save 1 is in flight
        var flushA = service.FlushAsync();          // two triggers queued behind save 1 ...
        var flushB = service.FlushAsync();

        first.SetResult(Saved());                   // save 1 confirmed: only change 1 is confirmed
        await save1;
        await Task.WhenAll(flushA, flushB);

        sent.Should().Equal(new[] { "first", "second" }, "queued triggers coalesce into one save carrying the newest state");
        persistence.Verify(p => p.AutoSaveAsync(It.IsAny<string?>()), Times.Exactly(2));
        service.HasUnsavedChanges.Should().BeFalse();
        service.SaveState.Should().Be(WorkspaceSaveState.Saved);
    }

    [Fact]
    public async Task A_confirmation_for_an_older_change_reports_pending_not_saved()
    {
        var repo = new WorkspaceArtifactRepository();
        var gate = new TaskCompletionSource<SavedWorkspaceDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistence = new Mock<IWorkspacePersistenceApiService>();
        persistence.Setup(p => p.AutoSaveAsync(It.IsAny<string?>())).Returns(() => gate.Task);
        var service = Service(repo, persistence.Object);
        var states = new List<WorkspaceSaveState>();
        service.SaveStateChanged += (_, _) => { lock (states) states.Add(service.SaveState); };

        service.OnArtifactChanged();
        var save = service.FlushAsync();
        service.OnArtifactChanged();                // newer change during the save
        states.Should().NotContain(WorkspaceSaveState.Saved, "nothing is saved before the backend confirms");

        gate.SetResult(Saved());
        await save;

        service.SaveState.Should().Be(WorkspaceSaveState.Pending, "the confirmation covers only the older change");
        service.HasUnsavedChanges.Should().BeTrue();
        states.Should().NotContain(WorkspaceSaveState.Saved);
    }

    [Fact]
    public async Task Nothing_shows_saved_before_the_backend_confirms()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var gate = new TaskCompletionSource<SavedWorkspaceDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistence = new Mock<IWorkspacePersistenceApiService>();
        persistence.Setup(p => p.AutoSaveAsync(It.IsAny<string?>())).Returns(() => gate.Task);
        var service = Service(new WorkspaceArtifactRepository(), persistence.Object);
        Services.AddSingleton<IWorkspaceAutoSaveService>(service);
        var cut = Render<WorkspaceSaveIndicator>();

        service.OnArtifactChanged();
        var save = service.FlushAsync();
        cut.WaitForAssertion(() => cut.Find("[data-testid=workspace-save-indicator]").GetAttribute("data-state").Should().Be("Saving"));
        cut.Find("[data-testid=workspace-save-status]").GetAttribute("data-state").Should().Be("Saving");
        cut.Find("[data-testid=workspace-save-status]").GetAttribute("data-unsaved").Should().Be("true");

        gate.SetResult(Saved());
        await save;
        cut.WaitForAssertion(() => cut.Find("[data-testid=workspace-save-status]").GetAttribute("data-state").Should().Be("Saved"));
        cut.Find("[data-testid=workspace-save-status]").GetAttribute("data-unsaved").Should().Be("false");
        cut.FindAll("[data-testid=workspace-save-indicator]").Should().BeEmpty();
        var guard = JSInterop.Invocations.Where(i => i.Identifier == "birknextSaveGuard.setDirty").Select(i => (bool)i.Arguments[0]!).ToList();
        guard.Should().Contain(true, "the reload/close warning is armed while unsaved");
        guard.Last().Should().BeFalse("and released after the confirmation");
    }

    [Fact]
    public void In_app_navigation_with_unsaved_changes_flushes_without_holding_the_navigation()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var gate = new TaskCompletionSource<SavedWorkspaceDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var persistence = new Mock<IWorkspacePersistenceApiService>();
        persistence.Setup(p => p.AutoSaveAsync(It.IsAny<string?>())).Returns(() => gate.Task);
        var service = Service(new WorkspaceArtifactRepository(), persistence.Object);
        Services.AddSingleton<IWorkspaceAutoSaveService>(service);
        var cut = Render<WorkspaceSaveIndicator>();
        var navigation = Services.GetRequiredService<NavigationManager>();

        service.OnArtifactChanged();
        navigation.NavigateTo("/dashboard");

        cut.WaitForAssertion(() => navigation.Uri.Should().EndWith("/dashboard", "the in-flight save never blocks the route change"));
        cut.WaitForAssertion(() => persistence.Verify(p => p.AutoSaveAsync(It.IsAny<string?>()), Times.Once(), "the debounce is bypassed on navigation"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=workspace-save-status]").GetAttribute("data-state").Should().Be("Saving"));

        gate.SetResult(Saved());
        cut.WaitForAssertion(() => cut.Find("[data-testid=workspace-save-status]").GetAttribute("data-state").Should().Be("Saved"));
        service.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public void In_app_navigation_without_unsaved_changes_sends_nothing()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var persistence = new Mock<IWorkspacePersistenceApiService>();
        Services.AddSingleton<IWorkspaceAutoSaveService>(Service(new WorkspaceArtifactRepository(), persistence.Object));
        Render<WorkspaceSaveIndicator>();

        Services.GetRequiredService<NavigationManager>().NavigateTo("/dashboard");

        persistence.Verify(p => p.AutoSaveAsync(It.IsAny<string?>()), Times.Never());
    }

    [Fact]
    public async Task A_fetch_aborted_by_page_unload_is_interrupted_not_an_error_and_stays_unsaved()
    {
        var apiLog = new ListLogger<WorkspacePersistenceApiService>();
        var saveLog = new ListLogger<WorkspaceAutoSaveService>();
        var repo = new WorkspaceArtifactRepository();
        var handler = new FailingHandler();
        var api = new WorkspacePersistenceApiService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, apiLog, repo, null, new Unload { IsUnloading = true });
        var service = Service(repo, api, saveLog);

        service.OnArtifactChanged();
        (await service.FlushAsync()).Should().BeFalse();

        service.SaveState.Should().Be(WorkspaceSaveState.Interrupted);
        service.HasUnsavedChanges.Should().BeTrue("the backend may or may not have committed the aborted request");
        api.LastSaveInterrupted.Should().BeTrue();
        apiLog.Entries.Concat(saveLog.Entries).Should().NotContain(e => e.Level >= LogLevel.Error);
        apiLog.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("interrupted"));

        // The page stayed after all (bfcache restore): the retry succeeds and clears the unsaved state.
        handler.Fail = false;
        (await service.SaveNowAsync()).Should().BeTrue();
        service.SaveState.Should().Be(WorkspaceSaveState.Saved);
        service.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task A_network_failure_while_the_page_stays_is_a_failure_and_an_error()
    {
        var apiLog = new ListLogger<WorkspacePersistenceApiService>();
        var repo = new WorkspaceArtifactRepository();
        var api = new WorkspacePersistenceApiService(new HttpClient(new FailingHandler()) { BaseAddress = new Uri("http://localhost/") }, apiLog, repo, null, new Unload { IsUnloading = false });
        var service = Service(repo, api);

        service.OnArtifactChanged();
        (await service.FlushAsync()).Should().BeFalse();

        service.SaveState.Should().Be(WorkspaceSaveState.Failed);
        service.HasUnsavedChanges.Should().BeTrue();
        api.LastSaveInterrupted.Should().BeFalse();
        apiLog.Entries.Should().Contain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task A_server_error_is_a_failure_with_its_reason()
    {
        var repo = new WorkspaceArtifactRepository();
        var api = new WorkspacePersistenceApiService(new HttpClient(new StatusHandler(HttpStatusCode.InternalServerError)) { BaseAddress = new Uri("http://localhost/") },
            NullLogger<WorkspacePersistenceApiService>.Instance, repo, null, new Unload { IsUnloading = true });
        var service = Service(repo, api);

        service.OnArtifactChanged();
        (await service.FlushAsync()).Should().BeFalse();

        service.SaveState.Should().Be(WorkspaceSaveState.Failed, "an HTTP error answer is never an interruption, even during unload");
        service.LastSaveError.Should().Be("boom");
        service.HasUnsavedChanges.Should().BeTrue();
    }

    private sealed class Unload : IPageUnloadSignal
    {
        public bool IsUnloading { get; set; }
    }

    /// <summary>What the browser's fetch does when the page unloads (or the network is down): "TypeError: Failed to fetch".</summary>
    private sealed class FailingHandler : HttpMessageHandler
    {
        public bool Fail { get; set; } = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail) throw new HttpRequestException("TypeError: Failed to fetch");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"Auto\",\"projectName\":\"\",\"artifacts\":[]}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{\"error\":\"boom\"}", Encoding.UTF8, "application/json") });
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
