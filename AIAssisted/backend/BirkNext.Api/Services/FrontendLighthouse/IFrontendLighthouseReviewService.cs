namespace BirkNext.Api.Services.FrontendLighthouse;

public interface IFrontendLighthouseReviewService
{
    Task<LighthouseReviewResult> ReviewAsync(string targetUrl, LighthouseReviewOptions? options = null, bool requiresAuthentication = false, CancellationToken cancellationToken = default);
    Task<LighthouseReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default);
}
