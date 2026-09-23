namespace BirkNext.Api.Services.BrowserAutomationDiagnostic;

/// <summary>How one mode's browser is launched. The only difference between the two modes is in here.</summary>
public sealed record BrowserAutomationDiagnosticLaunchOptions(
    BrowserAutomationDiagnosticMode Mode,
    string ProfileDirectory,
    bool Headless,
    TimeSpan Timeout);

/// <summary>
/// The four things the diagnostic asks of a browser. It exists so the stage flow, the exception classification, the
/// comparison and the cleanup guarantee can be tested without a real Microsoft Edge — the parts most likely to be
/// wrong are exactly the ones a real browser makes impossible to exercise (a target that terminates automation cannot
/// be summoned on demand, and a headless-only restriction cannot be simulated by asking nicely).
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
}

/// <summary>Creates the browser one mode drives. One per mode, per run; never shared, never pooled.</summary>
internal interface IDiagnosticBrowserFactory
{
    IDiagnosticBrowser Create(BrowserAutomationDiagnosticMode mode);
}
