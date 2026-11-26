using BirkNext.Web.Layout;
using BirkNext.Web.Services;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Layout;

/// <summary>
/// Application-wide route-change feedback: NavigationProgress (state, ids, delay, cancel, timeout), NavigationLoadingIndicator (content-area
/// loader, live region), NavMenu's pending row, MainLayout's aria-busy and RouteErrorBoundary (error + Retry). Deterministic: a manual
/// TimeProvider drives every timer, and a gate LocationChanging handler holds a navigation open ("slow navigation").
/// </summary>
public sealed class NavigationProgressTests : BunitContext
{
    private readonly ManualTimeProvider _clock = new();
    private readonly NavigationGate _gate = new();

    public NavigationProgressTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<FeatureVisibilityService>();
        Services.AddScoped(sp => new NavigationProgress(sp.GetRequiredService<NavigationManager>(), _clock));
    }

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();
    private NavigationProgress Progress => Services.GetRequiredService<NavigationProgress>();

    /// <summary>The gate registers after NavigationProgress (as a page's handler would), so progress always sees the navigation start.</summary>
    private void Arm()
    {
        _ = Progress;
        _gate.Register(Nav);
    }

    [Fact]
    public void RevealDelay_IsOneCentralConstant_Of200Milliseconds()
    {
        NavigationLoadingTimings.RevealDelay.Should().Be(TimeSpan.FromMilliseconds(200));
        Progress.RevealDelay.Should().Be(NavigationLoadingTimings.RevealDelay);
    }

    [Fact]
    public void SlowNavigation_IsPendingImmediately_AndShowsTheLoaderOnlyAfterTheDelay()
    {
        var cut = Render<NavigationLoadingIndicator>();
        Arm();
        _gate.Block = true;

        Nav.NavigateTo("specification-explorer");

        Progress.State.Should().Be(NavigationProgressState.Navigating, "the click is acknowledged at once");
        cut.WaitForAssertion(() => cut.Find("[data-testid=navigation-progress-bar]"));
        cut.FindAll("[data-testid=navigation-loading]").Should().BeEmpty("the loader waits for the delay");
        cut.Find("[data-testid=navigation-loading-announcement]").TextContent.Should().BeEmpty();

        _clock.Advance(TimeSpan.FromMilliseconds(199));
        cut.FindAll("[data-testid=navigation-loading]").Should().BeEmpty();

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        cut.WaitForAssertion(() => cut.Find("[data-testid=navigation-loading]").TextContent.Should().Contain("Opening page…"));
        cut.Find("[data-testid=navigation-loading-announcement]").TextContent.Should().Be("Opening page…");
        cut.Find("[data-testid=navigation-progress-state]").GetAttribute("data-loader").Should().Be("true");

        _gate.Release("specification-explorer");

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=navigation-loading]").Should().BeEmpty());
        cut.FindAll("[data-testid=navigation-progress-bar]").Should().BeEmpty();
        cut.Find("[data-testid=navigation-loading-announcement]").TextContent.Should().BeEmpty();
        Progress.State.Should().Be(NavigationProgressState.Idle);
        Progress.LastOutcome.Should().Be(NavigationOutcome.Completed);
        _clock.ActiveTimers.Should().Be(0, "completion cancels the reveal and safety timers");
    }

    [Fact]
    public void FastNavigation_NeverRendersTheLoader_NorAnnouncesAnything()
    {
        var cut = Render<NavigationLoadingIndicator>();
        var everVisible = false;
        Progress.Changed += () => everVisible |= Progress.IsLoaderVisible;

        Nav.NavigateTo("specification-explorer");
        _clock.Advance(TimeSpan.FromSeconds(1));

        everVisible.Should().BeFalse();
        Progress.State.Should().Be(NavigationProgressState.Idle);
        Progress.LastOutcome.Should().Be(NavigationOutcome.Completed);
        _clock.ActiveTimers.Should().Be(0);
        cut.FindAll("[data-testid=navigation-loading]").Should().BeEmpty();
        cut.Find("[data-testid=navigation-loading-announcement]").TextContent.Should().BeEmpty();
    }

    [Fact]
    public void BrowserHistoryNavigation_NotIntercepted_IsTrackedTheSameWay()
    {
        bool? intercepted = null;
        Nav.LocationChanged += (_, e) => intercepted = e.IsNavigationIntercepted; // before Arm: the gate must observe LocationChanged last
        Arm();
        _gate.Block = true;

        Nav.NavigateTo("plan-explorer"); // like Back/Forward: LocationChanging + LocationChanged with IsNavigationIntercepted = false
        Progress.State.Should().Be(NavigationProgressState.Navigating);
        _clock.Advance(NavigationLoadingTimings.RevealDelay);
        Progress.IsLoaderVisible.Should().BeTrue();

        _gate.Release("plan-explorer");

        intercepted.Should().BeFalse();
        Progress.State.Should().Be(NavigationProgressState.Idle);
        Progress.LastOutcome.Should().Be(NavigationOutcome.Completed);
    }

    [Fact]
    public void SameRoute_QueryOrFragmentOnly_IsNotATransition()
    {
        Nav.NavigateTo("dashboard");
        Arm();
        _gate.Block = true;
        var id = Progress.NavigationId;

        Nav.NavigateTo("dashboard?tab=readiness");
        Nav.NavigateTo("dashboard#section");
        _clock.Advance(TimeSpan.FromSeconds(1));

        Progress.State.Should().Be(NavigationProgressState.Idle);
        Progress.NavigationId.Should().Be(id);
        Progress.IsLoaderVisible.Should().BeFalse();
    }

    [Fact]
    public void RapidNavigation_OnlyTheLatestCanCompleteOrReveal()
    {
        Arm();
        _gate.Block = true;

        Nav.NavigateTo("page-a");
        var a = Progress.NavigationId;
        Nav.NavigateTo("page-b");
        var b = Progress.NavigationId;
        b.Should().BeGreaterThan(a);
        Progress.TargetLocation.Should().EndWith("page-b");

        // A late completion of A must not clear B.
        Progress.Complete(Nav.ToAbsoluteUri("page-a").ToString());
        Progress.State.Should().Be(NavigationProgressState.Navigating);
        Progress.NavigationId.Should().Be(b);

        _clock.Advance(NavigationLoadingTimings.CancelSettle); // A's handler phase was cancelled by B: its settle check is ignored
        Progress.State.Should().Be(NavigationProgressState.Navigating);

        _clock.Advance(NavigationLoadingTimings.RevealDelay);
        Progress.IsLoaderVisible.Should().BeTrue("B's own reveal timer");

        _gate.Release("page-b");
        Progress.State.Should().Be(NavigationProgressState.Idle);
        Progress.LastOutcome.Should().Be(NavigationOutcome.Completed);
    }

    [Fact]
    public void CancelledNavigation_ClearsPending_AndRestoresTheActiveSidebarItem()
    {
        // Both rows exist in Test Management (the focused Manual Testing view lists neither).
        Services.AddScoped<NavigationSectionState>();
        Services.GetRequiredService<NavigationSectionState>().SetView(WorkspaceView.TestLead);
        Nav.NavigateTo("dashboard");
        var cut = Render<NavMenu>();
        Arm();
        _gate.Block = true;

        Nav.NavigateTo("specification-explorer");

        cut.WaitForAssertion(() =>
        {
            var target = cut.Find("a.nav-link[href='specification-explorer']");
            target.ClassList.Should().Contain("active").And.Contain("nav-pending");
            target.GetAttribute("data-nav-pending").Should().Be("true");
            target.HasAttribute("aria-current").Should().BeFalse("aria-current stays on the page actually shown");
            // The sidebar row, not the brand link (also href="dashboard", which never carries aria-current).
            var shown = cut.Find("a.nav-link[href='dashboard']");
            shown.ClassList.Should().NotContain("active");
            shown.GetAttribute("aria-current").Should().Be("page");
        });

        _gate.Release("specification-explorer", prevent: true); // e.g. an unsaved-changes guard
        _clock.Advance(NavigationLoadingTimings.CancelSettle);

        Progress.State.Should().Be(NavigationProgressState.Idle);
        Progress.LastOutcome.Should().Be(NavigationOutcome.Cancelled);
        cut.WaitForAssertion(() =>
        {
            cut.Find("a.nav-link[href='dashboard']").ClassList.Should().Contain("active");
            cut.Find("a.nav-link[href='specification-explorer']").ClassList.Should().NotContain("active").And.NotContain("nav-pending");
            cut.FindAll("[data-nav-pending]").Should().BeEmpty();
        });
        _clock.ActiveTimers.Should().Be(0);
    }

    [Fact]
    public void StuckNavigation_BecomesNeutral_AfterTheSafetyTimeout_NotAnError()
    {
        var cut = Render<NavigationLoadingIndicator>();
        Arm();
        _gate.Block = true;
        Nav.NavigateTo("specification-explorer");

        _clock.Advance(NavigationLoadingTimings.SafetyTimeout);

        Progress.State.Should().Be(NavigationProgressState.Idle);
        Progress.LastOutcome.Should().Be(NavigationOutcome.TimedOut);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=navigation-loading]").Should().BeEmpty());
        cut.FindAll("[data-testid=navigation-error]").Should().BeEmpty();
    }

    [Fact]
    public void RenderFailure_ShowsErrorWithRetry_AndNeverASpinner()
    {
        var thrower = new ThrowSwitch { On = true };
        Services.AddSingleton(thrower);
        Arm();
        _gate.Block = true;
        Nav.NavigateTo("specification-explorer");

        var cut = Render((RenderFragment)(b =>
        {
            b.OpenComponent<NavigationLoadingIndicator>(0);
            b.CloseComponent();
            b.OpenComponent<RouteErrorBoundary>(1);
            b.AddAttribute(2, nameof(RouteErrorBoundary.ChildContent), (RenderFragment)(c => { c.OpenComponent<Thrower>(0); c.CloseComponent(); }));
            b.CloseComponent();
        }));

        cut.WaitForAssertion(() => cut.Find("[data-testid=navigation-error]"));
        cut.Find("[data-testid=navigation-error-retry]").Should().NotBeNull();
        Progress.State.Should().Be(NavigationProgressState.Failed);
        _clock.Advance(TimeSpan.FromSeconds(30));
        cut.FindAll("[data-testid=navigation-loading]").Should().BeEmpty("a failure stops the loader");
        cut.FindAll("[data-testid=navigation-progress-bar]").Should().BeEmpty();
        _clock.ActiveTimers.Should().Be(0);

        thrower.On = false;
        _gate.Block = false;
        cut.Find("[data-testid=navigation-error-retry]").Click();

        cut.WaitForAssertion(() => cut.Find("#recovered-page"));
        cut.FindAll("[data-testid=navigation-error]").Should().BeEmpty();
        Progress.State.Should().Be(NavigationProgressState.Idle);
    }

    [Fact]
    public void Indicator_SubscribesOnce_AndUnsubscribesOnDisposal()
    {
        var cut = Render<IndicatorHost>(p => p.Add(h => h.Show, true));
        cut.Render(p => p.Add(h => h.Show, true));
        cut.Render(p => p.Add(h => h.Show, true));
        Progress.ListenerCount.Should().Be(1, "re-rendering never adds a second handler");

        cut.Render(p => p.Add(h => h.Show, false));
        Progress.ListenerCount.Should().Be(0);
    }

    [Fact]
    public void DisposalWhilePending_CancelsTimers_AndUnregistersTheHandlers()
    {
        Arm();
        _gate.Block = true;
        Nav.NavigateTo("specification-explorer");
        _clock.ActiveTimers.Should().BeGreaterThan(0);
        var id = Progress.NavigationId;

        Progress.Dispose();

        _clock.ActiveTimers.Should().Be(0);
        _gate.Release("specification-explorer");
        _gate.Block = false;
        Nav.NavigateTo("plan-explorer");
        _clock.Advance(TimeSpan.FromMinutes(1));
        Progress.NavigationId.Should().Be(id, "a disposed service no longer observes navigation");
        Progress.IsLoaderVisible.Should().BeFalse();
    }

    [Fact]
    public void Indicator_LiveRegionAndLoader_FollowTheA11yContract()
    {
        var cut = Render<NavigationLoadingIndicator>();
        var live = cut.Find("[data-testid=navigation-loading-announcement]");
        live.GetAttribute("role").Should().Be("status");
        live.GetAttribute("aria-live").Should().Be("polite");
        live.TextContent.Should().BeEmpty("the live region exists before anything is announced");

        Arm();
        _gate.Block = true;
        Nav.NavigateTo("specification-explorer");
        _clock.Advance(NavigationLoadingTimings.RevealDelay);

        cut.WaitForAssertion(() => cut.Find("[data-testid=navigation-loading]"));
        var loader = cut.Find("[data-testid=navigation-loading]");
        loader.GetAttribute("aria-hidden").Should().Be("true", "the live region announces; the visual copy is not read twice");
        loader.QuerySelector("svg.loading-progress.loading-progress-compact").Should().NotBeNull("the startup loader's spinner, compact");
        cut.FindAll("[tabindex]").Should().BeEmpty("the loader never takes or traps focus");
    }

    [Fact]
    public void MainLayout_MarksMainBusyOnlyWhileARouteChangeIsPending()
    {
        AddLayoutServices();
        var cut = Render<MainLayout>(p => p
            .Add(l => l.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<h1 id='page-body'>page</h1>")))
            .AddCascadingValue(new RouteData(typeof(BirkNext.Web.Pages.Dashboard), new Dictionary<string, object?>())));
        cut.WaitForAssertion(() => cut.Find("#page-body"));
        cut.Find("main").HasAttribute("aria-busy").Should().BeFalse();
        cut.Find("article.content [data-testid=navigation-loading-announcement]").Should().NotBeNull("the indicator lives in the content area");

        _gate.Register(Nav);
        _gate.Block = true;
        Nav.NavigateTo("specification-explorer");
        cut.WaitForAssertion(() => cut.Find("main").GetAttribute("aria-busy").Should().Be("true"));

        _gate.Release("specification-explorer");
        cut.WaitForAssertion(() => cut.Find("main").HasAttribute("aria-busy").Should().BeFalse());
    }

    [Fact]
    public void PaintYield_RunsAfterThePendingStateIsSet_AndNeverForSameRoute()
    {
        NavigationProgress? progress = null;
        var observed = new List<NavigationProgressState>();
        progress = new NavigationProgress(Nav, _clock, () => { observed.Add(progress!.State); return ValueTask.CompletedTask; });
        try
        {
            Nav.NavigateTo("specification-explorer");
            Nav.NavigateTo("specification-explorer?tab=x");

            observed.Should().Equal(new[] { NavigationProgressState.Navigating }, "the browser paints the pending row/bar before the destination renders");
            progress.State.Should().Be(NavigationProgressState.Idle);
        }
        finally { progress.Dispose(); }
    }

    [Fact]
    public void ReducedMotion_StopsTheSpinnerBarAndPendingPulse()
    {
        var app = Css("wwwroot", "css", "app.css");
        var reduced = app[app.LastIndexOf("@media (prefers-reduced-motion: reduce)", StringComparison.Ordinal)..];
        reduced.Should().Contain(".loading-progress.loading-progress-compact { animation: none; }");
        reduced.Should().Contain(".nav-progress-bar::before { animation: none;");

        var nav = Css("Layout", "NavMenu.razor.css");
        nav.Should().MatchRegex(@"prefers-reduced-motion: reduce\)\s*\{\s*\.nav-item ::deep a\.nav-pending,\s*\.nav-flyout-link\.nav-pending \{ animation: none; \}");
    }

    private static string Css(params string[] path) =>
        File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "BirkNext.Web", .. path]));

    private void AddLayoutServices()
    {
        var autoSave = new Mock<IWorkspaceAutoSaveService>();
        autoSave.Setup(x => x.StartMonitoringAsync()).Returns(Task.CompletedTask);
        autoSave.Setup(x => x.StopMonitoringAsync()).Returns(Task.CompletedTask);
        var workspace = new WorkspaceArtifactRepository();
        Services.AddSingleton<IWorkspaceArtifactRepository>(workspace);
        Services.AddSingleton<IWorkspaceSessionService>(workspace);
        Services.AddSingleton<IWorkspaceArtifactStatusService>(sp => new WorkspaceArtifactStatusService(sp.GetRequiredService<IWorkspaceSessionService>()));
        Services.AddSingleton<IWorkspaceUpdateCoordinator, WorkspaceUpdateCoordinator>();
        Services.AddSingleton(autoSave.Object);
        Services.AddSingleton(new Mock<IWorkspacePersistenceApiService>().Object);
        Services.AddSingleton(new Mock<IWorkspaceSessionRestoreService>().Object);
        Services.AddSingleton(new QualityReviewSessionService());
        Services.AddSingleton(new AdminApiService(new HttpClient()));
        Services.AddSingleton(NullLogger<MainLayout>.Instance);
    }

    public sealed class ThrowSwitch { public bool On { get; set; } }

    private sealed class Thrower : ComponentBase
    {
        [Inject] public ThrowSwitch Switch { get; set; } = default!;

        protected override void OnInitialized()
        {
            if (Switch.On) throw new InvalidOperationException("destination failed to render");
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddMarkupContent(0, "<h1 id='recovered-page'>Recovered</h1>");
    }

    private sealed class IndicatorHost : ComponentBase
    {
        [Parameter] public bool Show { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (!Show) return;
            builder.OpenComponent<NavigationLoadingIndicator>(0);
            builder.CloseComponent();
        }
    }

    /// <summary>A LocationChanging handler that holds navigations open until released (or prevents them) — a slow or guarded navigation.</summary>
    /// <remarks>
    /// Release waits until the navigation is decided. Completing the handler does not finish the navigation inline: the framework's
    /// NotifyLocationChangingAsync and bUnit's NavigateToCore resume on the renderer's dispatcher, so LocationChanged (continued) or the
    /// end of the handler phase (prevented) happen on another thread, after Release returns. Asserting right after Release raced that.
    /// Release therefore waits for the observable end of the navigation: LocationChanged for the path, or bUnit's history entry marked
    /// Prevented (pushed after the handler phase, i.e. after its cancellation callbacks ran). Bounded; a missing decision fails loudly.
    /// LocationChanged handlers run in subscription order, so anything a test observes there must subscribe before <see cref="Register"/>.
    /// </remarks>
    private sealed class NavigationGate
    {
        private static readonly TimeSpan DecisionTimeout = TimeSpan.FromSeconds(5);
        private readonly Dictionary<string, TaskCompletionSource<bool>> _waiting = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _changed = new(StringComparer.OrdinalIgnoreCase);
        private BunitNavigationManager? _navigation;
        public bool Block { get; set; }

        public IDisposable Register(NavigationManager navigation)
        {
            _navigation = navigation as BunitNavigationManager;
            navigation.LocationChanged += (_, e) => { lock (_changed) _changed[Key(e.Location)] = Changed(e.Location) + 1; };
            return navigation.RegisterLocationChangingHandler(async context =>
            {
                if (!Block) return;
                var release = new TaskCompletionSource<bool>();
                lock (_waiting) _waiting[Key(context.TargetLocation)] = release;
                if (await release.Task) context.PreventNavigation();
            });
        }

        public void Release(string path, bool prevent = false)
        {
            TaskCompletionSource<bool> release;
            lock (_waiting) release = _waiting[Key(path)];
            var changed = Changed(path);
            var prevented = Prevented(path);
            release.TrySetResult(prevent);
            var decided = SpinWait.SpinUntil(() => prevent ? Prevented(path) > prevented : Changed(path) > changed, DecisionTimeout);
            if (!decided) throw new TimeoutException($"The navigation to '{path}' was not {(prevent ? "prevented" : "completed")} within {DecisionTimeout}.");
        }

        private int Changed(string location) { lock (_changed) return _changed.GetValueOrDefault(Key(location)); }

        private int Prevented(string path)
        {
            if (_navigation is null) return 0;
            try { return _navigation.History.Count(h => h.State == NavigationState.Prevented && Key(h.Uri) == Key(path)); }
            catch (InvalidOperationException) { return -1; } // pushed while being read: not decided yet as far as this check knows
        }

        private static string Key(string location) => new Uri(new Uri("http://localhost/"), location).AbsolutePath.Trim('/');
    }

    /// <summary>Timers fire only when the test advances time.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public int ActiveTimers { get { lock (_timers) return _timers.Count(t => !t.Disposed && t.Due is not null); } }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_timers) _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            var target = _now + by;
            while (true)
            {
                ManualTimer? next;
                lock (_timers) next = _timers.Where(t => !t.Disposed && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null) break;
                _now = next.Due!.Value;
                next.Due = null; // one-shot
                next.Fire();
            }
            _now = target;
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? Due { get; set; }
            public bool Disposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
                return true;
            }

            public void Fire() => callback(state);
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        }
    }
}
