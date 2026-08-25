using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IFrontendAccessibilityReviewApiService
{
    Task<AccessibilityResultDto> ReviewAsync(
        string targetUrl,
        string environmentType,
        bool requiresAuthentication,
        CancellationToken cancellationToken = default);
}
