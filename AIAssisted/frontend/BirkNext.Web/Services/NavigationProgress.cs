using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace BirkNext.Web.Services;

/// <summary>The application-wide navigation timings (one place). The loader is revealed only after <see cref="RevealDelay"/>, so a fast
/// navigation never flashes it; there is no minimum display time.</summary>
public static class NavigationLoadingTimings
{
    /// <summary>A route change still opening after this shows "Opening page…" in the content area.</summary>
    public static readonly TimeSpan RevealDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>After the location-changing handlers decided (their token is cancelled whether the navigation continues or was prevented),
    /// a navigation that continues raises LocationChanged at once; one that did not within this window was prevented (e.g. an
    /// unsaved-changes guard) and its pending state is cleared.</summary>
    public static readonly TimeSpan CancelSettle = TimeSpan.FromMilliseconds(150);

    /// <summary>A pending state still open after this becomes neutral (Idle, outcome TimedOut) — never a fake error, never a stuck spinner.</summary>
    public static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(15);
}

public enum NavigationProgressState { Idle, Navigating, Failed }

public enum NavigationOutcome { Completed, Cancelled, Superseded, TimedOut, Failed }

/// <summary>
/// Application-wide route-change progress, driven by NavigationManager: LocationChanging starts a transition (with a new id),
/// LocationChanged for that destination completes it. In this app the Router has no OnNavigateAsync and no lazy assemblies, so the
/// destination's first render happens synchronously inside LocationChanged: "complete" means the route rendered — never that the page's
/// data is loaded (pages keep their own loaders, e.g. "Loading project documents…"). Only the latest id can complete, reveal or clear.
/// Same-path navigation (query/fragment only: tabs, filters, in-page anchors) keeps the same page component and is not a transition.
/// A navigation prevented by another LocationChanging handler, or never completed, clears itself (Cancelled / TimedOut).
/// Not used for typing, card expansion, local filtering, autosave or background refresh — those never change the route path.
/// </summary>
public sealed class NavigationProgress : IDisposable
{
    private readonly NavigationManager _navigation;
    private readonly TimeProvider _time;
    private readonly IDisposable? _changingRegistration;
    private readonly object _gate = new();
    private ITimer? _revealTimer;
    private ITimer? _safetyTimer;
    private ITimer? _settleTimer;
    private string? _targetKey;
    private string _committedKey;
    private string _committedLocation;
    private bool _disposed;
    private readonly Func<ValueTask>? _yieldForPaint;

    /// <param name="yieldForPaint">Optional (the app passes birknextNav.nextPaint): awaited once when a transition starts, so the browser
    /// paints the pending sidebar row and progress bar before a heavy synchronous destination render takes the thread. Bounded (≤ 50 ms);
    /// failures are ignored.</param>
    public NavigationProgress(NavigationManager navigation, TimeProvider? time = null, Func<ValueTask>? yieldForPaint = null)
    {
        _navigation = navigation;
        _time = time ?? TimeProvider.System;
        _yieldForPaint = yieldForPaint;
        _committedLocation = navigation.Uri;
        _committedKey = Key(navigation.Uri);
        _changingRegistration = navigation.RegisterLocationChangingHandler(OnLocationChanging);
        navigation.LocationChanged += OnLocationChanged;
    }

    public TimeSpan RevealDelay { get; init; } = NavigationLoadingTimings.RevealDelay;
    public TimeSpan CancelSettle { get; init; } = NavigationLoadingTimings.CancelSettle;
    public TimeSpan SafetyTimeout { get; init; } = NavigationLoadingTimings.SafetyTimeout;

    public NavigationProgressState State { get; private set; }
    /// <summary>Increments for every transition (and every supersede/clear), so late timers or completions of earlier ones are ignored.</summary>
    public long NavigationId { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    /// <summary>Absolute destination of the pending transition.</summary>
    public string? TargetLocation { get; private set; }
    /// <summary>True once the pending transition outlived <see cref="RevealDelay"/>: the content-area loader is shown.</summary>
    public bool IsLoaderVisible { get; private set; }
    public NavigationOutcome? LastOutcome { get; private set; }

    public bool IsPending => State == NavigationProgressState.Navigating;

    /// <summary>The pending destination relative to the base URI without fragment (the shape NavigationCatalog.RelativeLocation returns).</summary>
    public string? PendingRelativeLocation => IsPending && TargetLocation is { } target ? Relative(target) : null;

    /// <summary>The location the app last committed (LocationChanged), relative and without fragment. Unlike NavigationManager.Uri in test
    /// doubles, it never moves to a destination that is still pending or was prevented.</summary>
    public string CommittedRelativeLocation => Relative(_committedLocation);

    /// <summary>Raised on every state change, from any thread (timers): subscribers use InvokeAsync.</summary>
    public event Action? Changed;

    internal int ListenerCount => Changed?.GetInvocationList().Length ?? 0;

    private ValueTask OnLocationChanging(LocationChangingContext context)
    {
        var id = Begin(context.TargetLocation);
        if (id == 0) return ValueTask.CompletedTask;
        // The token is cancelled when the handler phase ends — continued or prevented. Continued is followed by LocationChanged.
        try { context.CancellationToken.Register(static s => ((Settle)s!).Run(), new Settle(this, id)); }
        catch (ObjectDisposedException) { }
        return _yieldForPaint is null ? ValueTask.CompletedTask : YieldForPaintAsync(context.CancellationToken);
    }

    private async ValueTask YieldForPaintAsync(CancellationToken token)
    {
        try { if (!token.IsCancellationRequested) await _yieldForPaint!(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private sealed record Settle(NavigationProgress Progress, long Id)
    {
        public void Run() => Progress.OnDecisionMade(Id);
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e) => Complete(e.Location);

    /// <summary>Starts a transition to <paramref name="targetLocation"/>; returns its id, or 0 when it is not a route change (same path).</summary>
    internal long Begin(string targetLocation)
    {
        var key = Key(targetLocation);
        long id = 0;
        lock (_gate)
        {
            if (_disposed) return 0;
            if (key == _committedKey)
            {
                // Same page (query/fragment only), or back to the page still shown: any pending transition is superseded, nothing to open.
                if (State != NavigationProgressState.Navigating) return 0;
                Clear(NavigationOutcome.Superseded);
            }
            else
            {
                StopTimers();
                if (State == NavigationProgressState.Navigating) LastOutcome = NavigationOutcome.Superseded;
                id = ++NavigationId;
                var started = id;
                State = NavigationProgressState.Navigating;
                TargetLocation = _navigation.ToAbsoluteUri(targetLocation).ToString();
                _targetKey = key;
                StartedAt = _time.GetUtcNow();
                IsLoaderVisible = false;
                _revealTimer = _time.CreateTimer(_ => Reveal(started), null, RevealDelay, Timeout.InfiniteTimeSpan);
                _safetyTimer = _time.CreateTimer(_ => TimeOut(started), null, SafetyTimeout, Timeout.InfiniteTimeSpan);
            }
        }
        Raise();
        return id;
    }

    /// <summary>LocationChanged: the route rendered. Completes only the pending transition to this destination; a late completion of an
    /// earlier (superseded) transition is ignored.</summary>
    internal void Complete(string location)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var key = Key(location);
            _committedLocation = _navigation.ToAbsoluteUri(location).ToString();
            _committedKey = key;
            if (State != NavigationProgressState.Navigating || key != _targetKey) return;
            Clear(NavigationOutcome.Completed);
        }
        Raise();
    }

    /// <summary>The destination failed while rendering (RouteErrorBoundary): the content shows the error with Retry, never a spinner.</summary>
    public void ReportFailure()
    {
        lock (_gate)
        {
            if (_disposed) return;
            StopTimers();
            NavigationId++;
            State = NavigationProgressState.Failed;
            IsLoaderVisible = false;
            TargetLocation = null;
            _targetKey = null;
            LastOutcome = NavigationOutcome.Failed;
        }
        Raise();
    }

    /// <summary>Retry recovered the failed page.</summary>
    public void ClearFailure()
    {
        lock (_gate)
        {
            if (State != NavigationProgressState.Failed) return;
            State = NavigationProgressState.Idle;
        }
        Raise();
    }

    private void OnDecisionMade(long id)
    {
        lock (_gate)
        {
            if (_disposed || id != NavigationId || State != NavigationProgressState.Navigating) return;
            _settleTimer?.Dispose();
            _settleTimer = _time.CreateTimer(_ => Cancel(id), null, CancelSettle, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The navigation was prevented: the pending state (and the sidebar's pending highlight) clears.</summary>
    internal void Cancel(long id)
    {
        lock (_gate)
        {
            if (_disposed || id != NavigationId || State != NavigationProgressState.Navigating) return;
            Clear(NavigationOutcome.Cancelled);
        }
        Raise();
    }

    private void Reveal(long id)
    {
        lock (_gate)
        {
            if (_disposed || id != NavigationId || State != NavigationProgressState.Navigating || IsLoaderVisible) return;
            IsLoaderVisible = true;
        }
        Raise();
    }

    private void TimeOut(long id)
    {
        lock (_gate)
        {
            if (_disposed || id != NavigationId || State != NavigationProgressState.Navigating) return;
            Clear(NavigationOutcome.TimedOut);
        }
        Raise();
    }

    private void Clear(NavigationOutcome outcome)
    {
        StopTimers();
        NavigationId++;
        State = NavigationProgressState.Idle;
        IsLoaderVisible = false;
        TargetLocation = null;
        _targetKey = null;
        LastOutcome = outcome;
    }

    private void StopTimers()
    {
        _revealTimer?.Dispose();
        _safetyTimer?.Dispose();
        _settleTimer?.Dispose();
        _revealTimer = _safetyTimer = _settleTimer = null;
    }

    private void Raise() => Changed?.Invoke();

    /// <summary>The route identity: the absolute path, case-insensitive, without trailing slash, query or fragment.</summary>
    private string Key(string location)
    {
        try
        {
            var path = _navigation.ToAbsoluteUri(location).AbsolutePath.TrimEnd('/');
            return path.ToLowerInvariant();
        }
        catch (UriFormatException) { return location; }
    }

    private string Relative(string absolute)
    {
        try
        {
            var relative = _navigation.ToBaseRelativePath(absolute);
            var hash = relative.IndexOf('#');
            return hash >= 0 ? relative[..hash] : relative;
        }
        catch (ArgumentException) { return absolute; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopTimers();
        }
        _changingRegistration?.Dispose();
        _navigation.LocationChanged -= OnLocationChanged;
        Changed = null;
    }
}
