using BirkNext.Api.Models;
using BirkNext.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Controllers;

[ApiController]
[Route("api/recommended-workflow")]
public class RecommendedWorkflowController : ControllerBase
{
    private readonly IRecommendedWorkflowService _service;
    private readonly ILogger<RecommendedWorkflowController> _logger;

    public RecommendedWorkflowController(
        IRecommendedWorkflowService service,
        ILogger<RecommendedWorkflowController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpPost("build-steps")]
    public async Task<ActionResult<List<WorkflowStepViewModel>>> BuildSteps([FromBody] BuildStepsRequest request)
    {
        try
        {
            var steps = await _service.BuildWorkflowStepsAsync(
                request.WorkspaceId,
                request.HasConstitution,
                request.HasSpecification,
                request.HasPlan,
                request.HasTasks,
                request.HasDataModel);

            return Ok(steps);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building workflow steps for workspace {WorkspaceId}", request.WorkspaceId);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("mark-in-progress")]
    public async Task<ActionResult> MarkInProgress([FromBody] StepActionRequest request)
    {
        try
        {
            await _service.MarkStepInProgressAsync(request.WorkspaceId, request.StepKey);
            return Ok(new { message = "Step marked as in progress" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking step in progress");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("mark-reviewed")]
    public async Task<ActionResult> MarkReviewed([FromBody] StepActionRequest request)
    {
        try
        {
            await _service.MarkStepReviewedAsync(request.WorkspaceId, request.StepKey, request.Comment);
            return Ok(new { message = "Step marked as reviewed" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking step reviewed");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("approve")]
    public async Task<ActionResult> Approve([FromBody] ApprovalRequest request)
    {
        try
        {
            await _service.ApproveStepAsync(
                request.WorkspaceId,
                request.StepKey,
                request.ArtifactSetHash,
                request.Comment);

            return Ok(new { message = "Step approved" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving step");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("reject")]
    public async Task<ActionResult> Reject([FromBody] StepActionRequest request)
    {
        try
        {
            await _service.RejectStepAsync(request.WorkspaceId, request.StepKey, request.Comment);
            return Ok(new { message = "Step rejected" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rejecting step");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("invalidate-approvals")]
    public async Task<ActionResult> InvalidateApprovals([FromBody] InvalidateApprovalsRequest request)
    {
        try
        {
            await _service.InvalidateArtifactDependentApprovalsAsync(
                request.WorkspaceId,
                request.ChangedArtifactTypes,
                request.CurrentHash);

            return Ok(new { message = "Approvals invalidated" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invalidating approvals");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("readiness")]
    public async Task<ActionResult<WorkflowReadinessBreakdown>> GetReadiness([FromBody] BuildStepsRequest request)
    {
        try
        {
            var steps = await _service.BuildWorkflowStepsAsync(
                request.WorkspaceId,
                request.HasConstitution,
                request.HasSpecification,
                request.HasPlan,
                request.HasTasks,
                request.HasDataModel);

            var breakdown = _service.GetReadinessBreakdown(steps);
            return Ok(breakdown);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating readiness");
            return BadRequest(new { error = ex.Message });
        }
    }

    // Request DTOs
    public class BuildStepsRequest
    {
        public Guid WorkspaceId { get; set; }
        public bool HasConstitution { get; set; }
        public bool HasSpecification { get; set; }
        public bool HasPlan { get; set; }
        public bool HasTasks { get; set; }
        public bool HasDataModel { get; set; }
    }

    public class StepActionRequest
    {
        public Guid WorkspaceId { get; set; }
        public string StepKey { get; set; } = "";
        public string? Comment { get; set; }
    }

    public class ApprovalRequest
    {
        public Guid WorkspaceId { get; set; }
        public string StepKey { get; set; } = "";
        public string? Comment { get; set; }
        public string? ArtifactSetHash { get; set; }
    }

    public class InvalidateApprovalsRequest
    {
        public Guid WorkspaceId { get; set; }
        public List<string> ChangedArtifactTypes { get; set; } = new();
        public string CurrentHash { get; set; } = "";
    }
}
