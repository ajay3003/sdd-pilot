using System.Text;
using System.Text.Json;
using System.Net.Http.Json;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed class FrontendBrowserRuntimeReviewApiService : IFrontendBrowserRuntimeReviewApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<FrontendBrowserRuntimeReviewApiService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public FrontendBrowserRuntimeReviewApiService(
        HttpClient httpClient,
        ILogger<FrontendBrowserRuntimeReviewApiService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<BrowserRuntimeResultDto> ReviewAsync(
        string targetUrl,
        int navigationTimeoutMs = 30000,
        int startupObservationMs = 5000,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new
            {
                targetUrl,
                navigationTimeoutMs,
                startupObservationMs,
                headlessMode = true
            };

            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(
                "api/frontend-runtime/review",
                content,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Browser runtime review failed: {StatusCode} {ErrorContent}",
                    response.StatusCode, errorContent);

                return new BrowserRuntimeResultDto(
                    Status: BrowserRuntimeEngineStatusDto.EngineError,
                    RequestedUrl: targetUrl,
                    EngineError: $"HTTP {response.StatusCode}: {errorContent}");
            }

            var resultJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<BrowserRuntimeResultDto>(resultJson, _jsonOptions);

            return result ?? new BrowserRuntimeResultDto(
                Status: BrowserRuntimeEngineStatusDto.EngineError,
                RequestedUrl: targetUrl,
                EngineError: "Failed to parse browser runtime result");
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Browser runtime review cancelled");
            return new BrowserRuntimeResultDto(
                Status: BrowserRuntimeEngineStatusDto.Skipped,
                RequestedUrl: targetUrl,
                EngineError: "Review cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Browser runtime review API call failed");
            return new BrowserRuntimeResultDto(
                Status: BrowserRuntimeEngineStatusDto.EngineError,
                RequestedUrl: targetUrl,
                EngineError: $"API error: {ex.Message}");
        }
    }

    public Task<BrowserRuntimeResultDto> ReviewAsync(BrowserRuntimeApiExecutionRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(request, cancellationToken);

    private async Task<BrowserRuntimeResultDto> SendAsync(BrowserRuntimeApiExecutionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync("api/frontend-runtime/review", new
            {
                targetUrl = request.TargetUrl,
                navigationTimeoutMs = request.NavigationTimeoutMs,
                startupObservationMs = request.StartupObservationMs,
                headlessMode = true,
                executionMode = request.ExecutionMode,
                reviewSessionId = request.ReviewSessionId,
                profileId = request.ProfileId,
                authenticatedSessionId = request.AuthenticatedSessionId
            }, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new BrowserRuntimeResultDto(Status: BrowserRuntimeEngineStatusDto.EngineError, RequestedUrl: request.TargetUrl, EngineError: $"HTTP {(int)response.StatusCode}");
            return await response.Content.ReadFromJsonAsync<BrowserRuntimeResultDto>(cancellationToken: cancellationToken)
                ?? new BrowserRuntimeResultDto(Status: BrowserRuntimeEngineStatusDto.EngineError, RequestedUrl: request.TargetUrl, EngineError: "Empty Browser Runtime response");
        }
        catch (OperationCanceledException) { return new BrowserRuntimeResultDto(Status: BrowserRuntimeEngineStatusDto.Skipped, RequestedUrl: request.TargetUrl, EngineError: "Review cancelled"); }
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.PostAsync(
                "api/frontend-runtime/readiness",
                new StringContent("{}"),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                return false;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.TryGetProperty("isAvailable", out var availableProp) &&
                   availableProp.GetBoolean();
        }
        catch
        {
            return false;
        }
    }
}
