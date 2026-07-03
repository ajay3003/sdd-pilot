using BirkNext.Web.Models.Library;
using System.Net.Http.Json;

namespace BirkNext.Web.Services;

public interface ILibraryPageModelService
{
    Task<LibraryPageModel?> GetQAArtifactLibraryModelAsync();
    Task<LibraryPageModel?> GetCreateTestScenarioModelAsync();
    Task<LibraryPageModel?> GetSampleProjectsModelAsync();
}

public class LibraryPageModelService : ILibraryPageModelService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LibraryPageModelService> _logger;

    public LibraryPageModelService(
        HttpClient httpClient,
        ILogger<LibraryPageModelService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<LibraryPageModel?> GetQAArtifactLibraryModelAsync()
    {
        try
        {
            _logger.LogInformation("Loading QA Artifact Library page model");
            return await _httpClient.GetFromJsonAsync<LibraryPageModel>(
                "api/library-page-model/qa-artifact-library");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading QA Artifact Library model");
            return null;
        }
    }

    public async Task<LibraryPageModel?> GetCreateTestScenarioModelAsync()
    {
        try
        {
            _logger.LogInformation("Loading Create Test Scenario page model");
            return await _httpClient.GetFromJsonAsync<LibraryPageModel>(
                "api/library-page-model/create-test-scenario");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Create Test Scenario model");
            return null;
        }
    }

    public async Task<LibraryPageModel?> GetSampleProjectsModelAsync()
    {
        try
        {
            _logger.LogInformation("Loading Sample Projects page model");
            return await _httpClient.GetFromJsonAsync<LibraryPageModel>(
                "api/library-page-model/sample-projects");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading Sample Projects model");
            return null;
        }
    }
}
