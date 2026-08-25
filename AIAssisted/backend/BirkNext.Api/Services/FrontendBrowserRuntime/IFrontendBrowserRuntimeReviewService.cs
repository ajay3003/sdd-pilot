namespace BirkNext.Api.Services.FrontendBrowserRuntime;

public interface IFrontendBrowserRuntimeReviewService
{
    /// <summary>
    /// Execute browser runtime review of the target.
    /// Launches Chromium, navigates to target, captures runtime observations.
    /// </summary>
    Task<BrowserRuntimeResult> ReviewAsync(
        string targetUrl,
        BrowserRuntimeOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check whether browser runtime analysis is available.
    /// Validates Playwright package and Chromium executable presence.
    /// </summary>
    Task<BrowserRuntimeReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default);
}

public sealed record BrowserRuntimeReadinessResult(
    bool IsAvailable,
    string? ErrorMessage = null,
    string? BrowserName = null,
    string? BrowserVersion = null);
