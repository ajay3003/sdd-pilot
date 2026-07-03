using BirkNext.Api.Services.Analysis;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Provides structured page models for Analysis pages.
/// Each analysis page receives a consistent AnalysisPageModel with readiness status and prerequisites.
/// </summary>
[ApiController]
[Route("api/analysis-page-model")]
public class AnalysisPageModelController : ControllerBase
{
    private readonly IAnalysisPageModelService _modelService;
    private readonly ILogger<AnalysisPageModelController> _logger;

    public AnalysisPageModelController(
        IAnalysisPageModelService modelService,
        ILogger<AnalysisPageModelController> logger)
    {
        _modelService = modelService;
        _logger = logger;
    }

    /// <summary>
    /// Get structured page model for Spec Drift page.
    /// Returns artifact prerequisites, readiness status, and missing inputs.
    /// </summary>
    [HttpGet("spec-drift")]
    [ProducesResponseType(typeof(AnalysisPageModel), StatusCodes.Status200OK)]
    public async Task<ActionResult<AnalysisPageModel>> GetSpecDriftModel()
    {
        _logger.LogInformation("Building Spec Drift page model");
        var model = await _modelService.BuildSpecDriftModelAsync();
        return Ok(model);
    }

    /// <summary>
    /// Get structured page model for Impact Analysis page.
    /// Returns prerequisite requirements and analysis readiness.
    /// </summary>
    [HttpGet("impact-analysis")]
    [ProducesResponseType(typeof(AnalysisPageModel), StatusCodes.Status200OK)]
    public async Task<ActionResult<AnalysisPageModel>> GetImpactAnalysisModel()
    {
        _logger.LogInformation("Building Impact Analysis page model");
        var model = await _modelService.BuildImpactAnalysisModelAsync();
        return Ok(model);
    }

    /// <summary>
    /// Get structured page model for Requirements Traceability page.
    /// Returns coverage status and missing traceability links.
    /// </summary>
    [HttpGet("requirements-traceability")]
    [ProducesResponseType(typeof(AnalysisPageModel), StatusCodes.Status200OK)]
    public async Task<ActionResult<AnalysisPageModel>> GetRequirementsTraceabilityModel()
    {
        _logger.LogInformation("Building Requirements Traceability page model");
        var model = await _modelService.BuildRequirementsTraceabilityModelAsync();
        return Ok(model);
    }

    /// <summary>
    /// Get structured page model for Implementation Review page.
    /// Returns code review readiness and artifact prerequisites.
    /// </summary>
    [HttpGet("implementation-review")]
    [ProducesResponseType(typeof(AnalysisPageModel), StatusCodes.Status200OK)]
    public async Task<ActionResult<AnalysisPageModel>> GetImplementationReviewModel()
    {
        _logger.LogInformation("Building Implementation Review page model");
        var model = await _modelService.BuildImplementationReviewModelAsync();
        return Ok(model);
    }

    /// <summary>
    /// Get structured page model for Implementation Traceability page.
    /// Returns code-to-requirement traceability readiness.
    /// </summary>
    [HttpGet("implementation-traceability")]
    [ProducesResponseType(typeof(AnalysisPageModel), StatusCodes.Status200OK)]
    public async Task<ActionResult<AnalysisPageModel>> GetImplementationTraceabilityModel()
    {
        _logger.LogInformation("Building Implementation Traceability page model");
        var model = await _modelService.BuildImplementationTraceabilityModelAsync();
        return Ok(model);
    }
}
