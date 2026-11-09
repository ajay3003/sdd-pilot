using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Parquet;

namespace BirkNext.Web.Services;

/// <summary>Parquet data explorer client: upload into a temporary read-only session, rows/profile/validation/compare, durable evidence.</summary>
public interface IParquetApiService
{
    Task<(ParquetOverview? Overview, string? Error)> OpenAsync(string fileName, Stream content, CancellationToken ct = default);
    Task<(ParquetOverview? Overview, string? Error)> OverviewAsync(Guid session, CancellationToken ct = default);
    Task<(ParquetRowsResult? Result, string? Error)> RowsAsync(Guid session, ParquetRowsRequest request, CancellationToken ct = default);
    Task<(ParquetProfile? Profile, string? Error)> ProfileAsync(Guid session, CancellationToken ct = default);
    Task<(ParquetValidation? Validation, string? Error)> ValidateAsync(Guid session, ParquetRuleSet rules, CancellationToken ct = default);
    Task<(ParquetComparison? Comparison, string? Error)> CompareAsync(ParquetCompareRequest request, CancellationToken ct = default);
    Task CloseAsync(Guid session, CancellationToken ct = default);
    Task<(ParquetInspectionEvidence? Evidence, string? Error)> SaveEvidenceAsync(ParquetEvidenceRequest request, CancellationToken ct = default);
    Task<(List<ParquetInspectionEvidence>? Evidence, string? Error)> EvidenceAsync(string environmentId, CancellationToken ct = default);
    Task<(byte[]? Bytes, string? Error)> ExportEvidenceAsync(string environmentId, Guid id, CancellationToken ct = default);
    Task<string?> DeleteEvidenceAsync(string environmentId, Guid id, CancellationToken ct = default);
}

public sealed class ParquetApiService(HttpClient http) : IParquetApiService
{
    public const long MaxUploadBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private const string Base = "api/parquet";

    public async Task<(ParquetOverview? Overview, string? Error)> OpenAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            var file = new StreamContent(content);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", fileName);
            using var response = await http.PostAsync($"{Base}/sessions", form, ct);
            return await Read<ParquetOverview>(response, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, Fail(ex)); }
    }

    public Task<(ParquetOverview? Overview, string? Error)> OverviewAsync(Guid session, CancellationToken ct = default) => Get<ParquetOverview>($"{Base}/sessions/{session}", ct);
    public Task<(ParquetRowsResult? Result, string? Error)> RowsAsync(Guid session, ParquetRowsRequest request, CancellationToken ct = default) => Post<ParquetRowsResult>($"{Base}/sessions/{session}/rows", request, ct);
    public Task<(ParquetProfile? Profile, string? Error)> ProfileAsync(Guid session, CancellationToken ct = default) => Post<ParquetProfile>($"{Base}/sessions/{session}/profile", new List<string>(), ct);
    public Task<(ParquetValidation? Validation, string? Error)> ValidateAsync(Guid session, ParquetRuleSet rules, CancellationToken ct = default) => Post<ParquetValidation>($"{Base}/sessions/{session}/validate", rules, ct);
    public Task<(ParquetComparison? Comparison, string? Error)> CompareAsync(ParquetCompareRequest request, CancellationToken ct = default) => Post<ParquetComparison>($"{Base}/compare", request, ct);

    public async Task CloseAsync(Guid session, CancellationToken ct = default)
    {
        try { using var _ = await http.DeleteAsync($"{Base}/sessions/{session}", ct); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
    }

    public Task<(ParquetInspectionEvidence? Evidence, string? Error)> SaveEvidenceAsync(ParquetEvidenceRequest request, CancellationToken ct = default) =>
        Post<ParquetInspectionEvidence>($"{Base}/evidence", request, ct);

    public Task<(List<ParquetInspectionEvidence>? Evidence, string? Error)> EvidenceAsync(string environmentId, CancellationToken ct = default) =>
        Get<List<ParquetInspectionEvidence>>($"{Base}/evidence?environmentId={Uri.EscapeDataString(environmentId)}", ct);

    public async Task<(byte[]? Bytes, string? Error)> ExportEvidenceAsync(string environmentId, Guid id, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync($"{Base}/evidence/{id}/export?environmentId={Uri.EscapeDataString(environmentId)}", ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadAsByteArrayAsync(ct), null) : (null, await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (null, Fail(ex)); }
    }

    public async Task<string?> DeleteEvidenceAsync(string environmentId, Guid id, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.DeleteAsync($"{Base}/evidence/{id}?environmentId={Uri.EscapeDataString(environmentId)}", ct);
            return response.IsSuccessStatusCode ? null : response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? "You do not have permission to delete evidence." : await Message(response, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return Fail(ex); }
    }

    private async Task<(T? Value, string? Error)> Get<T>(string url, CancellationToken ct)
    {
        try { using var response = await http.GetAsync(url, ct); return await Read<T>(response, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (default, Fail(ex)); }
    }

    private async Task<(T? Value, string? Error)> Post<T>(string url, object body, CancellationToken ct)
    {
        try { using var response = await http.PostAsJsonAsync(url, body, Json, ct); return await Read<T>(response, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (default, Fail(ex)); }
    }

    private static async Task<(T? Value, string? Error)> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) return (default, "You do not have permission for this action.");
        return (default, await Message(response, ct));
    }

    private static string Fail(Exception ex) => BackendRequestClassifier.FromException("Parquet explorer", ex).UserMessage;

    private static async Task<string> Message(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try { if (JsonDocument.Parse(text).RootElement is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("message", out var m)) return m.GetString() ?? text; }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(text) ? $"The request failed (HTTP {(int)response.StatusCode})." : text.Trim('"');
    }
}
