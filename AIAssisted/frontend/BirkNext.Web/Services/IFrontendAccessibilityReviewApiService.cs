using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IFrontendAccessibilityReviewApiService
{
    Task<AccessibilityResultDto> ReviewAsync(
        string targetUrl,
        string environmentType,
        bool requiresAuthentication,
        CancellationToken cancellationToken = default);

    Task<AccessibilityResultDto> ReviewAsync(
        AccessibilityApiExecutionRequest request,
        CancellationToken cancellationToken = default) =>
        ReviewAsync(request.TargetUrl, request.EnvironmentType, request.ExecutionMode == AccessibilityExecutionModeDto.AuthenticatedSessionPage, cancellationToken);
}

public sealed record AccessibilityApiExecutionRequest(
    string TargetUrl,
    AccessibilityExecutionModeDto ExecutionMode,
    string? ReviewSessionId = null,
    string? ProfileId = null,
    string? SessionId = null,
    string EnvironmentType = "Public",
    int NavigationTimeoutMs = 30000,
    int StabilizationMs = 1000);
