using System.Net.Http.Json;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed class WasmSecurityApiService
{
    private readonly HttpClient _client;
    private WasmSecurityReviewReport? _cached;

    public WasmSecurityApiService(HttpClient client) => _client = client;

    public async Task<(WasmSecurityReviewReport? Report, string? Error)> ScanAsync(WasmScanRequest request)
    {
        try
        {
            var response = await _client.PostAsJsonAsync("api/wasm-security/scan", request);

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                return (null, code switch
                {
                    400 => "Invalid URL. Enter a full https:// address.",
                    _ => $"Scan failed (HTTP {code}). Check that the backend is running.",
                });
            }

            var report = await response.Content.ReadFromJsonAsync<WasmSecurityReviewReport>();
            _cached = report;
            return (report, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, "Could not reach the backend. Check that the server is running.");
        }
    }

    public WasmSecurityReviewReport? GetCached() => _cached;
    public void ClearCache() => _cached = null;
}
