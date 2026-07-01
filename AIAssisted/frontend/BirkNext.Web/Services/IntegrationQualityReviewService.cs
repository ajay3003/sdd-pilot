using System.Net.Http.Json;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IIntegrationQualityReviewService
{
    Task<(IntegrationQualityReport? Report, string? Error)> AnalyzeAsync(
        IntegrationQualityRequest request, CancellationToken ct = default);
}

public sealed class IntegrationQualityReviewService : IIntegrationQualityReviewService
{
    private readonly HttpClient _client;

    public IntegrationQualityReviewService(HttpClient client) => _client = client;

    public async Task<(IntegrationQualityReport? Report, string? Error)> AnalyzeAsync(
        IntegrationQualityRequest request, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.PostAsJsonAsync("api/integration-quality/analyze", request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                return (null, code switch
                {
                    400 => "No integrations are configured in the active Target Environment. Open Target Environments and add at least one integration.",
                    _ => $"Integration quality review failed (HTTP {code}). Check that the backend is running.",
                });
            }

            var report = await response.Content.ReadFromJsonAsync<IntegrationQualityReport>(cancellationToken: ct);
            return (report, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (null, "Could not reach the backend. Check that the server is running.");
        }
    }
}
