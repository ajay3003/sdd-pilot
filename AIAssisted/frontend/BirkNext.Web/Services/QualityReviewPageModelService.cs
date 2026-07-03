using BirkNext.Web.Models.QualityReview;
using System.Net.Http.Json;

namespace BirkNext.Web.Services;

/// <summary>
/// Loads Quality Review page models from the backend API.
/// Eliminates duplicate readiness, pack selection, and prerequisite logic.
/// </summary>
public interface IQualityReviewPageModelService
{
    Task<QualityReviewPageModel?> GetQualityReviewModelAsync();
    Task<QualityReviewPageModel?> GetApiQualityReviewModelAsync();
    Task<QualityReviewPageModel?> GetFrontendQualityReviewModelAsync();
    Task<QualityReviewPageModel?> GetIntegrationQualityReviewModelAsync();
}

public class QualityReviewPageModelService : IQualityReviewPageModelService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<QualityReviewPageModelService> _logger;

    public QualityReviewPageModelService(
        HttpClient httpClient,
        ILogger<QualityReviewPageModelService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<QualityReviewPageModel?> GetQualityReviewModelAsync()
    {
        try
        {
            _logger.LogInformation("Loading Quality Review page model");
            var response = await _httpClient.GetFromJsonAsync<QualityReviewPageModel>(
                "api/quality-review-page-model/quality-review");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Quality Review page model");
            return null;
        }
    }

    public async Task<QualityReviewPageModel?> GetApiQualityReviewModelAsync()
    {
        try
        {
            _logger.LogInformation("Loading API Quality Review page model");
            var response = await _httpClient.GetFromJsonAsync<QualityReviewPageModel>(
                "api/quality-review-page-model/api-quality-review");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading API Quality Review page model");
            return null;
        }
    }

    public async Task<QualityReviewPageModel?> GetFrontendQualityReviewModelAsync()
    {
        try
        {
            _logger.LogInformation("Loading Frontend Quality Review page model");
            var response = await _httpClient.GetFromJsonAsync<QualityReviewPageModel>(
                "api/quality-review-page-model/frontend-quality-review");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Frontend Quality Review page model");
            return null;
        }
    }

    public async Task<QualityReviewPageModel?> GetIntegrationQualityReviewModelAsync()
    {
        try
        {
            _logger.LogInformation("Loading Integration Quality Review page model");
            var response = await _httpClient.GetFromJsonAsync<QualityReviewPageModel>(
                "api/quality-review-page-model/integration-quality-review");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Integration Quality Review page model");
            return null;
        }
    }
}
