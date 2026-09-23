namespace BirkNext.Api.Services.BrowserAutomationDiagnostic;

/// <summary>How one mode's browser is launched. The only difference between the two modes is in here.</summary>
public sealed record BrowserAutomationDiagnosticLaunchOptions(
    BrowserAutomationDiagnosticMode Mode,
    string ProfileDirectory,
    bool Headless,
    TimeSpan Timeout);

/// <summary>
/// One main-frame navigation as the browser reported it. The URL is RAW and never leaves the service unsanitized.
/// <paramref name="SecondaryPage"/> marks a navigation in a page the target OPENED (a sign-in popup, say) rather
/// than in the diagnostic's own page.
/// </summary>
internal sealed record DiagnosticNavigationObservation(long AtMs, string Url, bool SecondaryPage = false);

/// <summary>A lifecycle signal the browser reported, with when it arrived.</summary>
internal sealed record DiagnosticLifecycleObservation(BrowserAutomationLifecycleEventKind Kind, long AtMs);

/// <summary>
/// The result of one safe read against the current page. <paramref name="Url"/> is raw (sanitized by the service).
/// <paramref name="PageClosed"/> is true when the page was already gone — reported as an observation, not dressed up
/// as an exception the browser never threw.
/// </summary>
internal sealed record DiagnosticPageProbe(string Url, bool PageClosed, bool? MarkerFound);

internal enum DiagnosticSettleOutcome
{
    /// <summary>Main-frame navigation stayed quiet for the whole quiet period.</summary>
    Settled,
    /// <summary>The settle bound was reached while navigation was still happening. Observation continues regardless.</summary>
    BoundReached,
    /// <summary>A page close, crash, context close or disconnect was reported while waiting.</summary>
    Terminated,
}

internal sealed record DiagnosticSettleResult(DiagnosticSettleOutcome Outcome, long DurationMs);

/// <summary>
/// What the diagnostic asks of a browser. It exists so the stage flow, the exception classification, the comparison
/// and the cleanup guarantee can be tested without a real Microsoft Edge — the parts most likely to be wrong are
/// exactly the ones a real browser makes impossible to exercise (a target that closes the page a few seconds after it
/// loaded cannot be summoned on demand, and neither can an identity-provider redirect from script).
///
/// One implementation serves both modes: headed and headless differ by a launch flag, not by a different protocol, and
/// two implementations would let the two modes drift apart in ways the comparison would then misreport.
/// </summary>
internal interface IDiagnosticBrowser : IAsyncDisposable
{
    /// <summary>Microsoft Edge version, once known. Null when the browser has not started or does not report one.</summary>
    string? EdgeVersion { get; }

    /// <summary>Launches a persistent context against this mode's dedicated profile and opens about:blank.</summary>
    Task LaunchAsync(BrowserAutomationDiagnosticLaunchOptions options, CancellationToken ct);

    /// <summary>True once a persistent context exists. Checked as its own stage, because a launch can return without one.</summary>
    bool HasPersistentContext { get; }

    /// <summary>Navigates the diagnostic page. Exceptions propagate: their TYPE is the evidence.</summary>
    Task NavigateAsync(Uri url, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// One safe, read-only operation against the current page. Successful navigation is not enough on its own — the
    /// spike's failure was that the page CLOSED, which a navigation call can return from without complaint.
    /// </summary>
    Task<bool> IsControllableAsync(TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Starts a fresh navigation trace and lifecycle log for the target, on a clock that starts now. Everything the
    /// control pages produced is discarded, so the target's evidence is only the target's.
    /// </summary>
    void BeginTargetObservation();

    /// <summary>Main-frame navigations since <see cref="BeginTargetObservation"/>, in order.</summary>
    IReadOnlyList<DiagnosticNavigationObservation> NavigationTrace { get; }

    /// <summary>Page/context/browser lifecycle signals since <see cref="BeginTargetObservation"/>, in order.</summary>
    IReadOnlyList<DiagnosticLifecycleObservation> LifecycleEvents { get; }

    /// <summary>Milliseconds since <see cref="BeginTargetObservation"/>, on the same clock as the trace.</summary>
    long ObservationElapsedMs { get; }

    /// <summary>
    /// Waits, event-driven and bounded, until main-frame navigation has been quiet for <paramref name="quietPeriod"/> —
    /// so a script-driven redirect to an identity provider is seen before the location is read. Returns early when the
    /// page or context terminates. Exceptions propagate: a closed target throws, and that is evidence.
    /// </summary>
    Task<DiagnosticSettleResult> WaitForNavigationToSettleAsync(TimeSpan quietPeriod, TimeSpan bound, CancellationToken ct);

    /// <summary>
    /// Watches for <paramref name="window"/>, returning early and false the moment a page close, crash, context close
    /// or disconnect is reported. True means nothing terminated during the window — not, on its own, that control works.
    /// </summary>
    Task<bool> ObserveStabilityAsync(TimeSpan window, CancellationToken ct);

    /// <summary>
    /// A safe read of the current page: its title and its document element go through the automation channel, so a
    /// closed page throws instead of lying. Optionally checks for a structural application marker. Nothing read from
    /// the page is returned except its URL and whether the marker exists.
    /// </summary>
    Task<DiagnosticPageProbe> ProbeAsync(string? markerSelector, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Creates the browser one mode drives. One per mode, per run; never shared, never pooled.</summary>
internal interface IDiagnosticBrowserFactory
{
    IDiagnosticBrowser Create(BrowserAutomationDiagnosticMode mode);
}
