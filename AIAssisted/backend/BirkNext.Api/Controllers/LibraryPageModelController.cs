using BirkNext.Api.Services.Library;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Provides structured page models for Library pages.
/// Each library page receives a consistent LibraryPageModel with readiness status and items.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class LibraryPageModelController : ControllerBase
{
    private readonly ILibraryPageModelService _modelService;
    private readonly ILogger<LibraryPageModelController> _logger;

    public LibraryPageModelController(
        ILibraryPageModelService modelService,
        ILogger<LibraryPageModelController> logger)
    {
        _modelService = modelService;
        _logger = logger;
    }

    /// <summary>
    /// Get structured page model for QA Artifact Library page.
    /// Returns loaded artifacts and available actions.
    /// </summary>
    [HttpGet("qa-artifact-library")]
    [ProducesResponseType(typeof(LibraryPageModel), StatusCodes.Status200OK)]
    public async Task<ActionResult<LibraryPageModel>> GetQAArtifactLibraryModel()
    {
        _logger.LogInformation("Building QA Artifact Library page model");
        var model = await _modelService.BuildQAArtifactLibraryModelAsync();
        return Ok(model);
    }

    /// <summary>
    /// Get structured page model for Sample Projects page.
    /// Returns available sample projects and load actions.
    /// </summary>
    [HttpGet("sample-projects")]
    [ProducesResponseType(typeof(LibraryPageModel), StatusCodes.Status200OK)]
    public async Task<ActionResult<LibraryPageModel>> GetSampleProjectsModel()
    {
        _logger.LogInformation("Building Sample Projects page model");
        var model = await _modelService.BuildSampleProjectsModelAsync();
        return Ok(model);
    }
}
