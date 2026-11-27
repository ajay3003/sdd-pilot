using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.DataQuality;

namespace BirkNext.Web.Services;

/// <summary>System Settings → Data Quality (api/data-quality). Errors come back as a message to show; 403 is reported as missing access.</summary>
public interface IDataQualityApiService
{
    Task<(List<DataQualityProjectOption>? Projects, string? Error, bool Forbidden)> ProjectsAsync(CancellationToken ct = default);
    Task<(DataQualityRun? Run, string? Error)> StartAsync(DataQualityRunRequest request, CancellationToken ct = default);
    Task<(DataQualityRun? Run, string? Error)> GetAsync(string runId, CancellationToken ct = default);
    Task<(DataQualityRun? Run, string? Error)> CancelAsync(string runId, CancellationToken ct = default);
    Task<(List<DataQualityRunSummary>? Runs, string? Error)> RecentAsync(CancellationToken ct = default);
    Task<(DataQualityRun? Run, string? Error)> UiValuesAsync(string runId, List<DataQualityUiValue> values, CancellationToken ct = default);
    /// <summary>"md" or "json": the report as text, for download.</summary>
    Task<(string? Text, string? Error)> ReportAsync(string runId, string format, CancellationToken ct = default);
}

public sealed class DataQualityApiService(HttpClient http) : IDataQualityApiService
{
    private const string Base = "api/data-quality";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task<(List<DataQualityProjectOption>?, string?, bool)> ProjectsAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync($"{Base}/projects", ct);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) return (null, "Du har ikke tilgang til datakvalitetskontroller.", true);
            if (!response.IsSuccessStatusCode) return (null, $"Prosjektene kunne ikke hentes ({(int)response.StatusCode}).", false);
            return (await response.Content.ReadFromJsonAsync<List<DataQualityProjectOption>>(Json, ct), null, false);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or NotSupportedException) { return (null, "Tjenesten svarte ikke.", false); }
    }

    public Task<(DataQualityRun?, string?)> StartAsync(DataQualityRunRequest request, CancellationToken ct = default) =>
        Send<DataQualityRun>(() => http.PostAsJsonAsync($"{Base}/runs", request, Json, ct), ct);
    public Task<(DataQualityRun?, string?)> GetAsync(string runId, CancellationToken ct = default) =>
        Send<DataQualityRun>(() => http.GetAsync($"{Base}/runs/{Uri.EscapeDataString(runId)}", ct), ct);
    public Task<(DataQualityRun?, string?)> CancelAsync(string runId, CancellationToken ct = default) =>
        Send<DataQualityRun>(() => http.PostAsync($"{Base}/runs/{Uri.EscapeDataString(runId)}/cancel", null, ct), ct);
    public Task<(List<DataQualityRunSummary>?, string?)> RecentAsync(CancellationToken ct = default) =>
        Send<List<DataQualityRunSummary>>(() => http.GetAsync($"{Base}/runs", ct), ct);
    public Task<(DataQualityRun?, string?)> UiValuesAsync(string runId, List<DataQualityUiValue> values, CancellationToken ct = default) =>
        Send<DataQualityRun>(() => http.PostAsJsonAsync($"{Base}/runs/{Uri.EscapeDataString(runId)}/ui-values", values, Json, ct), ct);

    public async Task<(string?, string?)> ReportAsync(string runId, string format, CancellationToken ct = default)
    {
        if (format is not ("md" or "json")) return (null, "Ukjent format.");
        try
        {
            using var response = await http.GetAsync($"{Base}/runs/{Uri.EscapeDataString(runId)}/report.{format}", ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadAsStringAsync(ct), null) : (null, $"Rapporten kunne ikke hentes ({(int)response.StatusCode}).");
        }
        catch (HttpRequestException) { return (null, "Tjenesten svarte ikke."); }
    }

    private static async Task<(T? Value, string? Error)> Send<T>(Func<Task<HttpResponseMessage>> call, CancellationToken ct)
    {
        try
        {
            using var response = await call();
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) return (default, "Du har ikke tilgang til datakvalitetskontroller.");
            string? message = null;
            try { if ((await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).TryGetProperty("error", out var e)) message = e.GetString(); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
            return (default, message ?? $"Forespørselen feilet ({(int)response.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or NotSupportedException) { return (default, "Tjenesten svarte ikke."); }
    }
}
