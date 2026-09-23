using System.Net.Http.Json;
using BirkNext.HeadlessAuthDiagnostic;

namespace BirkNext.Web.Services;

public interface IHeadlessDiagnosticApiService
{
    Task<HeadlessDiagnosticReport> RunAsync(HeadlessDiagnosticRequest request, CancellationToken ct);
    Task<HeadlessPrerequisite> CheckAsync(HeadlessDiagnosticRequest request, CancellationToken ct);
}
public sealed class HeadlessDiagnosticApiService(HttpClient http) : IHeadlessDiagnosticApiService
{
    public async Task<HeadlessDiagnosticReport> RunAsync(HeadlessDiagnosticRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/headless-auth-diagnostic/run", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HeadlessDiagnosticReport>(cancellationToken: ct) ?? throw new HttpRequestException("No diagnostic report returned.");
    }
    public async Task<HeadlessPrerequisite> CheckAsync(HeadlessDiagnosticRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/headless-auth-diagnostic/prerequisite", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HeadlessPrerequisite>(cancellationToken: ct) ?? new(false, "No prerequisite evidence available.");
    }
}
