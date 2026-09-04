using BirkNext.Web.Models;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BirkNext.Web.Services;

/// <summary>
/// Frontend API service for target environment detection.
/// Communicates with backend detection endpoint.
/// </summary>
public interface ITargetEnvironmentDetectionApiService
{
    Task<TargetEnvironmentDetectionResult?> DetectFromUrlAsync(string targetUrl, CancellationToken cancellationToken = default);

    Task<TargetDetectionOutcome?> StartBrowserDetectionAsync(
        string targetUrl,
        string reviewSessionId,
        string profileId,
        CancellationToken cancellationToken = default);
}

public sealed class TargetEnvironmentDetectionApiService : ITargetEnvironmentDetectionApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly HttpClient _httpClient;
    private readonly ILogger<TargetEnvironmentDetectionApiService> _logger;

    public TargetEnvironmentDetectionApiService(
        HttpClient httpClient,
        ILogger<TargetEnvironmentDetectionApiService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<TargetEnvironmentDetectionResult?> DetectFromUrlAsync(
        string targetUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
        {
            _logger.LogWarning("Detection requested with empty URL");
            return null;
        }

        try
        {
            var request = new { targetUrl };
            var response = await _httpClient.PostAsJsonAsync(
                "api/frontend-target/detect",
                request,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<TargetEnvironmentDetectionResult>(JsonOptions, cancellationToken);
                return result;
            }

            _logger.LogWarning("Detection failed with status {Status}", response.StatusCode);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error during detection for {Url}", targetUrl);
            return null;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Detection timeout for {Url}", targetUrl);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during detection for {Url}", targetUrl);
            return null;
        }
    }

    public async Task<TargetDetectionOutcome?> StartBrowserDetectionAsync(
        string targetUrl,
        string reviewSessionId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetUrl) || string.IsNullOrWhiteSpace(reviewSessionId) || string.IsNullOrWhiteSpace(profileId))
        {
            _logger.LogWarning("Browser detection requested with missing parameters");
            return null;
        }

        try
        {
            var request = new { targetUrl, reviewSessionId, profileId };
            var response = await _httpClient.PostAsJsonAsync(
                "api/frontend-target/continue-in-browser",
                request,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<TargetDetectionOutcome>(JsonOptions, cancellationToken);
                return result;
            }

            _logger.LogWarning("Browser detection failed with status {Status}", response.StatusCode);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "HTTP error during browser detection for {Url}", targetUrl);
            return null;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Browser detection timeout for {Url}", targetUrl);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during browser detection for {Url}", targetUrl);
            return null;
        }
    }
}
