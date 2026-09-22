namespace BirkNext.Api.Services.BrowserAutomationDiagnostic;

/// <summary>
/// The four things the diagnostic asks of a browser. It exists so the stage flow, the exception classification and the
/// cleanup guarantee can be tested without a real Microsoft Edge — the parts most likely to be wrong are exactly the
/// ones a real browser makes impossible to exercise (a target that terminates automation cannot be summoned on demand).
///
/// The real implementation is <see cref="PlaywrightDiagnosticBrowser"/> and is the only place Playwright appears.
/// </summary>
internal interface IDiagnosticBrowser : IAsyncDisposable
{
    /// <summary>Microsoft Edge version, once known. Null when the browser has not started or does not report one.</summary>
    string? EdgeVersion { get; }

    /// <summary>Launches a persistent context against the dedicated profile and opens about:blank.</summary>
    Task LaunchAsync(string profileDirectory, TimeSpan timeout, CancellationToken ct);

    /// <summary>Navigates the diagnostic page. Exceptions propagate: their TYPE is the evidence.</summary>
    Task NavigateAsync(Uri url, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// One safe, read-only operation against the current page. Successful navigation is not enough on its own — the
    /// spike's failure was that the page CLOSED, which a navigation call can return from without complaint.
    /// </summary>
    Task<bool> IsControllableAsync(CancellationToken ct);
}

/// <summary>Creates the browser the diagnostic drives. One per run; never shared, never pooled.</summary>
internal interface IDiagnosticBrowserFactory
{
    IDiagnosticBrowser Create();
}
