namespace BirkNext.Api.Services.FrontendAccessibility;

public interface IFrontendAccessibilityReviewService
{
    Task<AccessibilityReviewResult> ReviewAsync(
        string targetUrl,
        AccessibilityReviewOptions? options = null,
        bool requiresAuthentication = false,
        CancellationToken cancellationToken = default);

    Task<AccessibilityReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default);
}
