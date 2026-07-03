using BirkNext.Web.Models.Analysis;
using System.Net.Http.Json;

namespace BirkNext.Web.Services;

/// <summary>
/// Loads Analysis page models from the backend API.
/// Eliminates duplicate readiness and prerequisite logic in Analysis pages.
/// </summary>
public interface IAnalysisPageModelService
{
    Task<AnalysisPageModel?> GetSpecDriftModelAsync();
    Task<AnalysisPageModel?> GetImpactAnalysisModelAsync();
    Task<AnalysisPageModel?> GetRequirementsTraceabilityModelAsync();
    Task<AnalysisPageModel?> GetImplementationReviewModelAsync();
    Task<AnalysisPageModel?> GetImplementationTraceabilityModelAsync();
}

public class AnalysisPageModelService : IAnalysisPageModelService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AnalysisPageModelService> _logger;

    public AnalysisPageModelService(
        HttpClient httpClient,
        ILogger<AnalysisPageModelService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<AnalysisPageModel?> GetSpecDriftModelAsync()
    {
        try
        {
            _logger.LogInformation("Loading Spec Drift page model");
            return await _httpClient.GetFromJsonAsync<AnalysisPageModel>(
                "api/analysis-page-model/spec-drift");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Spec Drift page model");
            return null;
        }
    }

    public async Task<AnalysisPageModel?> GetImpactAnalysisModelAsync()
    {
        try
        {
            _logger.LogInformation("Loading Impact Analysis page model");
            return await _httpClient.GetFromJsonAsync<AnalysisPageModel>(
                "api/analysis-page-model/impact-analysis");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Impact Analysis page model");
            return null;
        }
    }

    public async Task<AnalysisPageModel?> GetRequirementsTraceabilityModelAsync()
    {
        try
        {
            _logger.LogInformation("Loading Requirements Traceability page model");
            return await _httpClient.GetFromJsonAsync<AnalysisPageModel>(
                "api/analysis-page-model/requirements-traceability");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Requirements Traceability page model");
            return null;
        }
    }

    public async Task<AnalysisPageModel?> GetImplementationReviewModelAsync()
    {
        try
        {
            _logger.LogInformation("Loading Implementation Review page model");
            return await _httpClient.GetFromJsonAsync<AnalysisPageModel>(
                "api/analysis-page-model/implementation-review");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Implementation Review page model");
            return null;
        }
    }

    public async Task<AnalysisPageModel?> GetImplementationTraceabilityModelAsync()
    {
        try
        {
            _logger.LogInformation("Loading Implementation Traceability page model");
            return await _httpClient.GetFromJsonAsync<AnalysisPageModel>(
                "api/analysis-page-model/implementation-traceability");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Implementation Traceability page model");
            return null;
        }
    }
}
