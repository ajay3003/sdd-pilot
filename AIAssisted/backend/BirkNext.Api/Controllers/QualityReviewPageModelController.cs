using BirkNext.Api.Services;
using BirkNext.Api.Services.QualityReview;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Provides structured page models for Quality Review pages.
/// Each page receives a consistent QualityReviewPageModel with readiness status and pack prerequisites.
/// </summary>
[ApiController]
[Route("api/quality-review-page-model")]
public class QualityReviewPageModelController : ControllerBase
{
    private readonly IQualityReviewPageModelService _modelService;
    private readonly ILogger<QualityReviewPageModelController> _logger;

    public QualityReviewPageModelController(
        IQualityReviewPageModelService modelService,
        ILogger<QualityReviewPageModelController> logger)
    {
        _modelService = modelService;
        _logger = logger;
    }

    /// <summary>
    /// Get structured page model for Quality Review page.
    /// Returns pack availability, missing prerequisites, and readiness status.
    /// </summary>
    [HttpGet("quality-review")]
    [ProducesResponseType(typeof(QualityReviewPageModel), StatusCodes.Status200OK)]
    public async Task<ActionResult<QualityReviewPageModel>> GetQualityReviewModel()
    {
        _logger.LogInformation("Building Quality Review page model");
        var model = await _modelService.BuildQualityReviewModelAsync();
        return Ok(model);
    }

    /// <summary>
    /// Get structured page model for API Quality Review page.
    /// Returns endpoint configuration status and connectivity checks.
    /// </summary>
    [HttpGet("api-quality-review")]
    [ProducesResponseType(typeof(QualityReviewPageModel), StatusCodes.Status200OK)]
    public async Task<ActionResult<QualityReviewPageModel>> GetApiQualityReviewModel()
    {
        _logger.LogInformation("Building API Quality Review page model");
        var model = await _modelService.BuildApiQualityReviewModelAsync();
        return Ok(model);
    }

    /// <summary>
    /// Get structured page model for Frontend Quality Review page.
    /// Returns frontend target URL readiness and analysis area availability.
    /// </summary>
    [HttpGet("frontend-quality-review")]
    [ProducesResponseType(typeof(QualityReviewPageModel), StatusCodes.Status200OK)]
    public async Task<ActionResult<QualityReviewPageModel>> GetFrontendQualityReviewModel()
    {
        _logger.LogInformation("Building Frontend Quality Review page model");
        var model = await _modelService.BuildFrontendQualityReviewModelAsync();
        return Ok(model);
    }

    /// <summary>
    /// Get structured page model for Integration Quality Review page.
    /// Returns integration configuration status and readiness for selected integrations.
    /// </summary>
    [HttpGet("integration-quality-review")]
    [ProducesResponseType(typeof(QualityReviewPageModel), StatusCodes.Status200OK)]
    public async Task<ActionResult<QualityReviewPageModel>> GetIntegrationQualityReviewModel()
    {
        _logger.LogInformation("Building Integration Quality Review page model");
        var model = await _modelService.BuildIntegrationQualityReviewModelAsync();
        return Ok(model);
    }
}
