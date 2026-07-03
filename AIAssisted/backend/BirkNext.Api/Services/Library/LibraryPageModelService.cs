using Microsoft.Extensions.Logging;

namespace BirkNext.Api.Services.Library;

/// <summary>
/// Service that builds library page models for all 3 library pages.
/// Orchestrates the individual page builders with error handling.
/// </summary>
public interface ILibraryPageModelService
{
    Task<LibraryPageModel> BuildQAArtifactLibraryModelAsync();
    Task<LibraryPageModel> BuildCreateTestScenarioModelAsync();
    Task<LibraryPageModel> BuildSampleProjectsModelAsync();
}

public class LibraryPageModelService : ILibraryPageModelService
{
    private readonly IQAArtifactLibraryPageModelBuilder _qaArtifactBuilder;
    private readonly ICreateTestScenarioPageModelBuilder _createScenarioBuilder;
    private readonly ISampleProjectsPageModelBuilder _sampleProjectsBuilder;
    private readonly ILogger<LibraryPageModelService> _logger;

    public LibraryPageModelService(
        IQAArtifactLibraryPageModelBuilder qaArtifactBuilder,
        ICreateTestScenarioPageModelBuilder createScenarioBuilder,
        ISampleProjectsPageModelBuilder sampleProjectsBuilder,
        ILogger<LibraryPageModelService> logger)
    {
        _qaArtifactBuilder = qaArtifactBuilder;
        _createScenarioBuilder = createScenarioBuilder;
        _sampleProjectsBuilder = sampleProjectsBuilder;
        _logger = logger;
    }

    public async Task<LibraryPageModel> BuildQAArtifactLibraryModelAsync()
    {
        try
        {
            _logger.LogInformation("Building QA Artifact Library page model");
            return await _qaArtifactBuilder.BuildPageModelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building QA Artifact Library model");
            return ErrorModel("QA Artifact Library", "Failed to build page model");
        }
    }

    public async Task<LibraryPageModel> BuildCreateTestScenarioModelAsync()
    {
        try
        {
            _logger.LogInformation("Building Create Test Scenario page model");
            return await _createScenarioBuilder.BuildPageModelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Create Test Scenario model");
            return ErrorModel("Create Test Scenario", "Failed to build page model");
        }
    }

    public async Task<LibraryPageModel> BuildSampleProjectsModelAsync()
    {
        try
        {
            _logger.LogInformation("Building Sample Projects page model");
            return await _sampleProjectsBuilder.BuildPageModelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Sample Projects model");
            return ErrorModel("Sample Projects", "Failed to build page model");
        }
    }

    private static LibraryPageModel ErrorModel(string title, string message)
    {
        return new LibraryPageModel
        {
            Title = title,
            Description = "Library page",
            ReadinessStatus = LibraryStatus.Fail,
            RequiredInputs = [],
            MissingInputs = [],
            Summary = new LibrarySummary
            {
                StatusMessage = message,
                HasAvailableActions = false
            }
        };
    }
}
