using System.Net.Http.Json;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IApiQualityReviewService
{
    Task<(ApiQualityReviewReport? Report, string? Error)> AnalyzeAsync(
        ApiQualityReviewRequest request, CancellationToken ct = default);
}

public sealed class ApiQualityReviewService : IApiQualityReviewService
{
    private readonly HttpClient _client;

    public ApiQualityReviewService(HttpClient client) => _client = client;

    public async Task<(ApiQualityReviewReport? Report, string? Error)> AnalyzeAsync(
        ApiQualityReviewRequest request, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.PostAsJsonAsync("api/api-quality/analyze", request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                return (null, code switch
                {
                    400 => "No endpoint URLs are configured. Open Target Environments in System Settings and add at least one URL.",
                    _ => $"API quality review failed (HTTP {code}). Check that the backend is running.",
                });
            }

            var report = await response.Content.ReadFromJsonAsync<ApiQualityReviewReport>(cancellationToken: ct);
            return (report, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, "Could not reach the backend. Check that the server is running.");
        }
    }
}
