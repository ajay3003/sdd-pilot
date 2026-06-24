using System.Net.Http.Json;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;


public sealed class ImplementationTraceabilityApiService
{
    private readonly HttpClient _client;
    private ImplementationTraceabilityReport? _cached;

    public ImplementationTraceabilityApiService(HttpClient client)
    {
        _client = client;
    }

    public async Task<ProviderStatus> GetStatusAsync()
    {
        try
        {
            var result = await _client.GetFromJsonAsync<ProviderStatus>(
                "api/implementation-traceability/status");
            return result ?? new ProviderStatus { Configured = false, UsingMock = true, Message = "Status unavailable." };
        }
        catch
        {
            return new ProviderStatus { Configured = false, UsingMock = true, Message = "Backend unavailable." };
        }
    }

    public async Task<(ImplementationTraceabilityReport? Report, string? Error)> FetchAsync(
        IReadOnlyList<int> workItemIds,
        string? repositoryId = null,
        string? branch = null)
    {
        try
        {
            var request = new { workItemIds, repositoryId, branch };
            var response = await _client.PostAsJsonAsync(
                "api/implementation-traceability/fetch", request);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                return (null, statusCode switch
                {
                    401 or 403 => "Azure DevOps returned an authentication error. Check that your PAT has Read access to Work Items, Code, and Pull Requests.",
                    404 => "One or more work items were not found.",
                    _ => $"Fetch failed (HTTP {statusCode}). Please try again.",
                });
            }

            var report = await response.Content.ReadFromJsonAsync<ImplementationTraceabilityReport>();
            _cached = report;
            return (report, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, "Failed to connect to the backend. Please check that the server is running.");
        }
    }

    public ImplementationTraceabilityReport? GetCached() => _cached;

    public void ClearCache() => _cached = null;

    public async Task<AzureDevOpsConnectionTestResultDto> TestConnectionAsync()
    {
        try
        {
            var response = await _client.PostAsync(
                "api/implementation-traceability/test-connection", null);

            if (!response.IsSuccessStatusCode)
                return new AzureDevOpsConnectionTestResultDto
                {
                    OverallSuccess = false,
                    ErrorMessage   = $"Test failed (HTTP {(int)response.StatusCode}).",
                };

            return await response.Content.ReadFromJsonAsync<AzureDevOpsConnectionTestResultDto>()
                   ?? new AzureDevOpsConnectionTestResultDto { OverallSuccess = false, ErrorMessage = "Empty response." };
        }
        catch
        {
            return new AzureDevOpsConnectionTestResultDto
            {
                OverallSuccess = false,
                ErrorMessage   = "Could not reach the backend. Check that the server is running.",
            };
        }
    }
}
