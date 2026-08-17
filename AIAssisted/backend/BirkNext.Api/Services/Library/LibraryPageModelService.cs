using Microsoft.Extensions.Logging;

namespace BirkNext.Api.Services.Library;

/// <summary>
/// Service that builds library page models for library pages.
/// Orchestrates the individual page builders with error handling.
/// </summary>
public interface ILibraryPageModelService
{
    Task<LibraryPageModel> BuildQAArtifactLibraryModelAsync();
    Task<LibraryPageModel> BuildSampleProjectsModelAsync();
}

public class LibraryPageModelService : ILibraryPageModelService
{
    private readonly IQAArtifactLibraryPageModelBuilder _qaArtifactBuilder;
    private readonly ISampleProjectsPageModelBuilder _sampleProjectsBuilder;
    private readonly ILogger<LibraryPageModelService> _logger;

    public LibraryPageModelService(
        IQAArtifactLibraryPageModelBuilder qaArtifactBuilder,
        ISampleProjectsPageModelBuilder sampleProjectsBuilder,
        ILogger<LibraryPageModelService> logger)
    {
        _qaArtifactBuilder = qaArtifactBuilder;
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
