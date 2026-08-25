using System.Net.Http.Json;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed class FrontendLighthouseReviewApiService(HttpClient httpClient) : IFrontendLighthouseReviewApiService
{
    public async Task<LighthouseResultDto> ReviewAsync(string targetUrl, bool requiresAuthentication, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync("api/frontend-lighthouse/review", new { targetUrl, requiresAuthentication }, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(LighthouseExecutionStatusDto.EngineError, RequestedUrl: targetUrl, EngineError: $"Lighthouse API returned HTTP {(int)response.StatusCode}.");
            return await response.Content.ReadFromJsonAsync<LighthouseResultDto>(cancellationToken: cancellationToken)
                ?? new(LighthouseExecutionStatusDto.EngineError, RequestedUrl: targetUrl, EngineError: "Lighthouse API returned no result.");
        }
        catch (Exception ex)
        {
            return new(LighthouseExecutionStatusDto.EngineError, RequestedUrl: targetUrl, EngineError: ex.Message);
        }
    }
}
