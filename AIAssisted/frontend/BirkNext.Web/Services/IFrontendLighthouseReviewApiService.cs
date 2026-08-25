using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IFrontendLighthouseReviewApiService
{
    Task<LighthouseResultDto> ReviewAsync(string targetUrl, bool requiresAuthentication, CancellationToken cancellationToken = default);
}
