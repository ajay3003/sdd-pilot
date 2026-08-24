using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Frontend client service for calling the backend Browser Runtime Review API.
/// Abstracts HTTP communication with the browser runtime engine.
/// </summary>
public interface IFrontendBrowserRuntimeReviewApiService
{
    /// <summary>
    /// Request a browser runtime review from the backend.
    /// </summary>
    Task<BrowserRuntimeResultDto> ReviewAsync(
        string targetUrl,
        int navigationTimeoutMs = 30000,
        int startupObservationMs = 5000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if the backend browser runtime engine is ready.
    /// </summary>
    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}
