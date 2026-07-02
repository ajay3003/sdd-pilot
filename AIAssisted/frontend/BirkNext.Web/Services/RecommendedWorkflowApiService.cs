using System.Net.Http.Json;

namespace BirkNext.Web.Services;

/// <summary>
/// Frontend HTTP client for backend RecommendedWorkflowService.
/// Handles workflow step building and approval operations.
/// </summary>
public interface IRecommendedWorkflowApiService
{
    /// <summary>
    /// Build workflow steps for a workspace.
    /// </summary>
    Task<List<WorkflowStepViewModel>?> BuildWorkflowStepsAsync(
        Guid workspaceId,
        bool hasConstitution,
        bool hasSpecification,
        bool hasPlan,
        bool hasTasks,
        bool hasDataModel);

    /// <summary>
    /// Mark a step as in-progress.
    /// </summary>
    Task MarkStepInProgressAsync(Guid workspaceId, string stepKey);

    /// <summary>
    /// Mark a step as reviewed.
    /// </summary>
    Task MarkStepReviewedAsync(Guid workspaceId, string stepKey, string? comment = null);

    /// <summary>
    /// Approve a step.
    /// </summary>
    Task ApproveStepAsync(Guid workspaceId, string stepKey, string? comment = null, string? artifactSetHash = null);

    /// <summary>
    /// Reject a step.
    /// </summary>
    Task RejectStepAsync(Guid workspaceId, string stepKey, string? comment = null);

    /// <summary>
    /// Invalidate approvals when artifacts change.
    /// </summary>
    Task InvalidateApprovalsAsync(Guid workspaceId, List<string> changedArtifactTypes, string currentHash);
}

public class RecommendedWorkflowApiService : IRecommendedWorkflowApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<RecommendedWorkflowApiService> _logger;

    public RecommendedWorkflowApiService(
        HttpClient httpClient,
        ILogger<RecommendedWorkflowApiService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<WorkflowStepViewModel>?> BuildWorkflowStepsAsync(
        Guid workspaceId,
        bool hasConstitution,
        bool hasSpecification,
        bool hasPlan,
        bool hasTasks,
        bool hasDataModel)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "api/recommended-workflow/build-steps",
                new
                {
                    workspaceId,
                    hasConstitution,
                    hasSpecification,
                    hasPlan,
                    hasTasks,
                    hasDataModel
                });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Build workflow steps failed with status {StatusCode}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<List<WorkflowStepViewModel>>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building workflow steps");
            return null;
        }
    }

    public async Task MarkStepInProgressAsync(Guid workspaceId, string stepKey)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "api/recommended-workflow/mark-in-progress",
                new { workspaceId, stepKey });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Mark step in progress failed with status {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking step in progress");
        }
    }

    public async Task MarkStepReviewedAsync(Guid workspaceId, string stepKey, string? comment = null)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "api/recommended-workflow/mark-reviewed",
                new { workspaceId, stepKey, comment });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Mark step reviewed failed with status {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking step reviewed");
        }
    }

    public async Task ApproveStepAsync(Guid workspaceId, string stepKey, string? comment = null, string? artifactSetHash = null)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "api/recommended-workflow/approve",
                new { workspaceId, stepKey, comment, artifactSetHash });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Approve step failed with status {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving step");
        }
    }

    public async Task RejectStepAsync(Guid workspaceId, string stepKey, string? comment = null)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "api/recommended-workflow/reject",
                new { workspaceId, stepKey, comment });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Reject step failed with status {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rejecting step");
        }
    }

    public async Task InvalidateApprovalsAsync(Guid workspaceId, List<string> changedArtifactTypes, string currentHash)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "api/recommended-workflow/invalidate-approvals",
                new { workspaceId, changedArtifactTypes, currentHash });

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Invalidate approvals failed with status {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invalidating approvals");
        }
    }
}
