using BirkNext.Api.Services.Review;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

/// <summary>
/// Exposes Review page models via REST API.
/// </summary>
[ApiController]
[Route("api/review-page-model")]
public class ReviewPageModelController(ReviewPageModelService service) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboardModel()
    {
        var model = await service.GetDashboardModelAsync();
        return Ok(model);
    }

    [HttpGet("constitution-explorer")]
    public async Task<IActionResult> GetConstitutionExplorerModel()
    {
        var model = await service.GetConstitutionExplorerModelAsync();
        return Ok(model);
    }

    [HttpGet("data-model-explorer")]
    public async Task<IActionResult> GetDataModelExplorerModel()
    {
        var model = await service.GetDataModelExplorerModelAsync();
        return Ok(model);
    }

    [HttpGet("plan-explorer")]
    public async Task<IActionResult> GetPlanExplorerModel()
    {
        var model = await service.GetPlanExplorerModelAsync();
        return Ok(model);
    }

    [HttpGet("task-explorer")]
    public async Task<IActionResult> GetTaskExplorerModel()
    {
        var model = await service.GetTaskExplorerModelAsync();
        return Ok(model);
    }

    [HttpGet("specification-review")]
    public async Task<IActionResult> GetSpecificationReviewModel()
    {
        var model = await service.GetSpecificationReviewModelAsync();
        return Ok(model);
    }
}
