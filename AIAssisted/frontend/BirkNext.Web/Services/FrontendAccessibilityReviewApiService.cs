using System.Net.Http.Json;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed class FrontendAccessibilityReviewApiService(HttpClient httpClient) : IFrontendAccessibilityReviewApiService
{
    public async Task<AccessibilityResultDto> ReviewAsync(
        string targetUrl,
        string environmentType,
        bool requiresAuthentication,
        CancellationToken cancellationToken = default)
    {
        _ = environmentType; // Target trust classification is backend policy, never caller input.
        try
        {
            var response = await httpClient.PostAsJsonAsync("api/frontend-accessibility/review", new
            {
                targetUrl,
                requiresAuthentication
            }, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(AccessibilityExecutionStatusDto.EngineError, RequestedUrl: targetUrl,
                    EngineError: $"Accessibility API returned HTTP {(int)response.StatusCode}.");
            return await response.Content.ReadFromJsonAsync<AccessibilityResultDto>(cancellationToken: cancellationToken)
                ?? new(AccessibilityExecutionStatusDto.EngineError, RequestedUrl: targetUrl, EngineError: "Accessibility API returned no result.");
        }
        catch (Exception ex)
        {
            return new(AccessibilityExecutionStatusDto.EngineError, RequestedUrl: targetUrl, EngineError: ex.Message);
        }
    }
}
