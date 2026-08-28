using BirkNext.Web.Models;
using System.Net.Http.Json;

namespace BirkNext.Web.Services;

public interface IFrontendQualityEngineStatusApiService
{
    Task<FrontendQualityEngineStatusReportDto?> GetStatusAsync(
        ReviewAuthenticationModeDto authMode = ReviewAuthenticationModeDto.Anonymous,
        ReviewEngineSelectionDto? selection = null,
        CancellationToken ct = default);

    Task<FrontendQualityEngineReadinessReportDto?> RevalidateEngineReadinessAsync(
        FrontendQualityEngineIdDto engineId,
        CancellationToken ct = default);
}

public sealed class FrontendQualityEngineStatusApiService : IFrontendQualityEngineStatusApiService
{
    private readonly HttpClient _http;
    private readonly ILogger<FrontendQualityEngineStatusApiService> _logger;

    public FrontendQualityEngineStatusApiService(HttpClient http, ILogger<FrontendQualityEngineStatusApiService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<FrontendQualityEngineStatusReportDto?> GetStatusAsync(
        ReviewAuthenticationModeDto authMode = ReviewAuthenticationModeDto.Anonymous,
        ReviewEngineSelectionDto? selection = null,
        CancellationToken ct = default)
    {
        try
        {
            var query = new { authMode, selection };
            var response = await _http.PostAsJsonAsync("api/frontend-quality-engines/status", query, cancellationToken: ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch engine status: {StatusCode}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<FrontendQualityEngineStatusReportDto>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch frontend quality engine status");
            return null;
        }
    }

    public async Task<FrontendQualityEngineReadinessReportDto?> RevalidateEngineReadinessAsync(
        FrontendQualityEngineIdDto engineId,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"api/frontend-quality-engines/readiness/{(int)engineId}", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to revalidate readiness for engine {EngineId}: {StatusCode}", engineId, response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<FrontendQualityEngineReadinessReportDto>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to revalidate engine readiness for {EngineId}", engineId);
            return null;
        }
    }
}
