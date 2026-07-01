namespace BirkNext.Api.Services.IntegrationQuality;

public interface IIntegrationQualityReviewService
{
    Task<IntegrationQualityReport> AnalyzeAsync(IntegrationQualityRequest request, CancellationToken ct = default);
}
