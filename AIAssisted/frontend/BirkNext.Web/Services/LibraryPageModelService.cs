using BirkNext.Web.Models.Library;
using System.Net.Http.Json;
using System.Text.Json;

namespace BirkNext.Web.Services;

public interface ILibraryPageModelService
{
    Task<LibraryPageModel?> GetQAArtifactLibraryModelAsync();
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
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error loading QA Artifact Library model: {StatusCode}", ex.StatusCode);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON deserialization error loading QA Artifact Library model");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error loading QA Artifact Library model");
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
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error loading Sample Projects model: {StatusCode}", ex.StatusCode);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "JSON deserialization error loading Sample Projects model");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error loading Sample Projects model");
            return null;
        }
    }
}
