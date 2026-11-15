using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>Azure DevOps Test Plans export (api/test-plan-suggestions/ado-export): offline XLSX/CSV import files from saved test cases. Errors come
/// back as a message. Nothing here connects to Azure DevOps.</summary>
public interface IAdoExportApiService
{
    Task<(AdoExportProfile? Profile, string? Error)> ProfileAsync(string projectKey, CancellationToken ct = default);
    Task<(AdoExportProfile? Profile, string? Error)> SaveProfileAsync(string projectKey, AdoExportProfile profile, CancellationToken ct = default);
    Task<(AdoExportPreview? Preview, string? Error)> PreviewAsync(AdoExportRequest request, CancellationToken ct = default);
    Task<(AdoExportFile? File, string? Error)> FileAsync(AdoExportRequest request, CancellationToken ct = default);
    Task<(AdoExportFile? File, string? Error)> ReportAsync(AdoExportRequest request, CancellationToken ct = default);
    Task<(List<AdoExportHistoryEntry>? History, string? Error)> HistoryAsync(string projectKey, CancellationToken ct = default);
}

public sealed class AdoExportApiService(HttpClient http) : IAdoExportApiService
{
    private const string Base = "api/test-plan-suggestions/ado-export";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string P(string projectKey) => $"projectKey={Uri.EscapeDataString(projectKey)}";

    public Task<(AdoExportProfile?, string?)> ProfileAsync(string projectKey, CancellationToken ct = default) =>
        Send<AdoExportProfile>(() => http.GetAsync($"{Base}/profile?{P(projectKey)}", ct), ct);
    public Task<(AdoExportProfile?, string?)> SaveProfileAsync(string projectKey, AdoExportProfile profile, CancellationToken ct = default) =>
        Send<AdoExportProfile>(() => http.PutAsJsonAsync($"{Base}/profile?{P(projectKey)}", profile, Json, ct), ct);
    public Task<(AdoExportPreview?, string?)> PreviewAsync(AdoExportRequest request, CancellationToken ct = default) =>
        Send<AdoExportPreview>(() => http.PostAsJsonAsync($"{Base}/preview", request, Json, ct), ct);
    public Task<(AdoExportFile?, string?)> FileAsync(AdoExportRequest request, CancellationToken ct = default) =>
        Send<AdoExportFile>(() => http.PostAsJsonAsync($"{Base}/file", request, Json, ct), ct);
    public Task<(AdoExportFile?, string?)> ReportAsync(AdoExportRequest request, CancellationToken ct = default) =>
        Send<AdoExportFile>(() => http.PostAsJsonAsync($"{Base}/report", request, Json, ct), ct);
    public Task<(List<AdoExportHistoryEntry>?, string?)> HistoryAsync(string projectKey, CancellationToken ct = default) =>
        Send<List<AdoExportHistoryEntry>>(() => http.GetAsync($"{Base}/history?{P(projectKey)}", ct), ct);

    private static async Task<(T? Value, string? Error)> Send<T>(Func<Task<HttpResponseMessage>> call, CancellationToken ct)
    {
        try
        {
            using var response = await call();
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
            if ((int)response.StatusCode is 401 or 403) return (default, "You do not have permission to do this.");
            string? message = null;
            try { message = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("message").GetString(); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
            return (default, message ?? $"The request failed (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return (default, "The Azure DevOps export is not reachable.");
        }
    }
}
