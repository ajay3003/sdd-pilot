namespace BirkNext.Api.Services.ApiQuality;

public interface IApiQualityReviewService
{
    Task<ApiQualityReviewReport> AnalyzeAsync(ApiQualityReviewRequest request, CancellationToken ct = default);
}
