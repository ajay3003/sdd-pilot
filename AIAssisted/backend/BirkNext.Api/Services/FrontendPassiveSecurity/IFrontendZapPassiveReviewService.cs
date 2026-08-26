namespace BirkNext.Api.Services.FrontendPassiveSecurity;

public interface IFrontendZapPassiveReviewService
{
    Task<PassiveSecurityResult> ReviewAsync(PassiveSecurityReviewRequest request, CancellationToken cancellationToken = default);
    Task<PassiveSecurityReadiness> CheckReadinessAsync(CancellationToken cancellationToken = default);
}
