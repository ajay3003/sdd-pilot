using BirkNext.Api.Services.QualityReview;

namespace BirkNext.Api.Services;

/// <summary>
/// Service for building and managing Quality Review page models.
/// Orchestrates model building across all four Quality Review pages.
/// </summary>
public interface IQualityReviewPageModelService
{
    /// <summary>Build model for Quality Review page</summary>
    Task<QualityReviewPageModel> BuildQualityReviewModelAsync();

    /// <summary>Build model for API Quality Review page</summary>
    Task<QualityReviewPageModel> BuildApiQualityReviewModelAsync();

    /// <summary>Build model for Frontend Quality Review page</summary>
    Task<QualityReviewPageModel> BuildFrontendQualityReviewModelAsync();

    /// <summary>Build model for Integration Quality Review page</summary>
    Task<QualityReviewPageModel> BuildIntegrationQualityReviewModelAsync();
}

/// <summary>
/// Builds Quality Review page models using page-specific builders.
/// All builders use the same QualityReviewPageModel contract for consistency.
/// </summary>
public class QualityReviewPageModelService : IQualityReviewPageModelService
{
    private readonly IQualityReviewPageModelBuilder_QualityReview _qualityReviewBuilder;
    private readonly IQualityReviewPageModelBuilder_ApiQuality _apiBuilder;
    private readonly IQualityReviewPageModelBuilder_FrontendQuality _frontendBuilder;
    private readonly IQualityReviewPageModelBuilder_IntegrationQuality _integrationBuilder;
    private readonly ILogger<QualityReviewPageModelService> _logger;

    public QualityReviewPageModelService(
        IQualityReviewPageModelBuilder_QualityReview qualityReviewBuilder,
        IQualityReviewPageModelBuilder_ApiQuality apiBuilder,
        IQualityReviewPageModelBuilder_FrontendQuality frontendBuilder,
        IQualityReviewPageModelBuilder_IntegrationQuality integrationBuilder,
        ILogger<QualityReviewPageModelService> logger)
    {
        _qualityReviewBuilder = qualityReviewBuilder;
        _apiBuilder = apiBuilder;
        _frontendBuilder = frontendBuilder;
        _integrationBuilder = integrationBuilder;
        _logger = logger;
    }

    public async Task<QualityReviewPageModel> BuildQualityReviewModelAsync()
    {
        try
        {
            return await _qualityReviewBuilder.BuildPageModelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Quality Review page model");
            return new QualityReviewPageModel
            {
                Title = "Quality Review",
                Description = "Error loading page model",
                ReadinessStatus = QualityReviewStatus.Fail,
                Summary = new() { ReadinessMessage = "Failed to load page configuration" }
            };
        }
    }

    public async Task<QualityReviewPageModel> BuildApiQualityReviewModelAsync()
    {
        try
        {
            return await _apiBuilder.BuildPageModelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building API Quality Review page model");
            return new QualityReviewPageModel
            {
                Title = "API Quality Review",
                Description = "Error loading page model",
                ReadinessStatus = QualityReviewStatus.Fail,
                Summary = new() { ReadinessMessage = "Failed to load page configuration" }
            };
        }
    }

    public async Task<QualityReviewPageModel> BuildFrontendQualityReviewModelAsync()
    {
        try
        {
            return await _frontendBuilder.BuildPageModelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Frontend Quality Review page model");
            return new QualityReviewPageModel
            {
                Title = "Frontend Quality Review",
                Description = "Error loading page model",
                ReadinessStatus = QualityReviewStatus.Fail,
                Summary = new() { ReadinessMessage = "Failed to load page configuration" }
            };
        }
    }

    public async Task<QualityReviewPageModel> BuildIntegrationQualityReviewModelAsync()
    {
        try
        {
            return await _integrationBuilder.BuildPageModelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Integration Quality Review page model");
            return new QualityReviewPageModel
            {
                Title = "Integration Quality Review",
                Description = "Error loading page model",
                ReadinessStatus = QualityReviewStatus.Fail,
                Summary = new() { ReadinessMessage = "Failed to load page configuration" }
            };
        }
    }
}
