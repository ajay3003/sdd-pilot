using System.Net.Http.Json;

namespace BirkNext.Web.Services;

/// <summary>
/// Frontend HTTP client for backend RecommendedWorkflowService.
/// Handles workflow step building and approval operations.
/// </summary>
public interface IRecommendedWorkflowApiService
{
    /// <summary>
    /// Build workflow steps for a workspace. <paramref name="artifacts"/> names the selected revision of each available role;
    /// review decisions are current only for the revisions named here.
    /// </summary>
    Task<List<WorkflowStepViewModel>?> BuildWorkflowStepsAsync(
        Guid workspaceId,
        bool hasConstitution,
        bool hasSpecification,
        bool hasPlan,
        bool hasTasks,
        bool hasDataModel,
        IReadOnlyList<ArtifactRevisionRef> artifacts);

    /// <summary>
    /// Mark a step as in-progress.
    /// </summary>
    Task MarkStepInProgressAsync(Guid workspaceId, string stepKey);

    /// <summary>
    /// Record that the step's exact artifact revisions were reviewed (no approval implied). Throws with the backend's reason
    /// when the decision is refused (for example, it names no revision).
    /// </summary>
    Task MarkStepReviewedAsync(Guid workspaceId, string stepKey, IReadOnlyList<ArtifactRevisionRef> artifacts, string? comment = null);

    /// <summary>
    /// Approve the step's exact artifact revisions. Throws when the decision is refused.
    /// </summary>
    Task ApproveStepAsync(Guid workspaceId, string stepKey, IReadOnlyList<ArtifactRevisionRef> artifacts, string? comment = null);

    /// <summary>
    /// Return the step's exact artifact revisions for changes. Throws when the decision is refused.
    /// </summary>
    Task RejectStepAsync(Guid workspaceId, string stepKey, IReadOnlyList<ArtifactRevisionRef> artifacts, string? comment = null);

    /// <summary>
    /// Invalidate approvals when artifacts change.
    /// </summary>
    Task InvalidateApprovalsAsync(Guid workspaceId, List<string> changedArtifactTypes, string currentHash);

    /// <summary>
    /// Get workflow readiness breakdown.
    /// </summary>
    Task<WorkflowReadinessBreakdown?> GetReadinessAsync(
        Guid workspaceId,
        bool hasConstitution,
        bool hasSpecification,
        bool hasPlan,
        bool hasTasks,
        bool hasDataModel);
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
        bool hasDataModel,
        IReadOnlyList<ArtifactRevisionRef> artifacts)
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
                    hasDataModel,
                    artifacts
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

    public Task MarkStepReviewedAsync(Guid workspaceId, string stepKey, IReadOnlyList<ArtifactRevisionRef> artifacts, string? comment = null) =>
        DecideAsync("mark-reviewed", new { workspaceId, stepKey, comment, artifacts }, "mark reviewed");

    public Task ApproveStepAsync(Guid workspaceId, string stepKey, IReadOnlyList<ArtifactRevisionRef> artifacts, string? comment = null) =>
        DecideAsync("approve", new { workspaceId, stepKey, comment, artifacts }, "approve");

    public Task RejectStepAsync(Guid workspaceId, string stepKey, IReadOnlyList<ArtifactRevisionRef> artifacts, string? comment = null) =>
        DecideAsync("reject", new { workspaceId, stepKey, comment, artifacts }, "record needs changes");

    /// <summary>A review decision is never silently dropped: a refused or failed request throws with the backend's reason.</summary>
    private async Task DecideAsync(string action, object body, string verb)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/recommended-workflow/{action}", body);
        if (response.IsSuccessStatusCode) return;

        string? reason = null;
        try { reason = (await response.Content.ReadFromJsonAsync<DecisionError>())?.Error; }
        catch (Exception ex) { _logger.LogDebug(ex, "Decision error body could not be read"); }
        _logger.LogWarning("Could not {Verb}: {Status} {Reason}", verb, response.StatusCode, reason);
        throw new InvalidOperationException(reason ?? $"The decision was not recorded ({(int)response.StatusCode}).");
    }

    private sealed record DecisionError(string? Error);

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

    public async Task<WorkflowReadinessBreakdown?> GetReadinessAsync(
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
                "api/recommended-workflow/readiness",
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
                _logger.LogWarning("Get readiness failed with status {StatusCode}", response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<WorkflowReadinessBreakdown>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting readiness");
            return null;
        }
    }
}
