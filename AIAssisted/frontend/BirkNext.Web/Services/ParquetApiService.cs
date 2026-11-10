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
    // ── Quality Review ──
    Task<(List<ParquetOverview>? Sessions, string? Error)> SessionsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    Task<(ParquetManifestSummary? Manifest, string? Error)> AttachManifestAsync(Guid session, string json, CancellationToken ct = default);
    Task<(ContractDiscovery? Discovery, string? Error)> DiscoverAsync(Guid session, string? project, CancellationToken ct = default);
    Task<(List<ContractCandidate>? Contracts, string? Error)> ContractsAsync(string? project, CancellationToken ct = default);
    Task<(ContractAssociation? Association, string? Error)> AssociateAsync(Guid session, ContractAssociation association, CancellationToken ct = default);
    Task<(RuleGeneration? Generation, string? Error)> GenerateAsync(Guid session, ContractAssociation? association, CancellationToken ct = default);
    Task<(ParquetQualityResult? Result, string? Error)> ReviewAsync(Guid session, ParquetQualityRequest request, CancellationToken ct = default);
    Task<(ParquetBaselineResult? Result, string? Error)> BaselineAsync(ParquetBaselineRequest request, CancellationToken ct = default);
    Task<(List<ParquetRuleSetVersion>? Versions, string? Error)> RuleSetsAsync(string project, string contract, string version, CancellationToken ct = default);
    Task<(ParquetRuleSetVersion? Version, string? Error)> ApproveRulesAsync(Guid session, ParquetApprovalRequest request, CancellationToken ct = default);
    Task<(ParquetInspectionEvidence? Evidence, string? Error)> EvidenceDetailAsync(string environmentId, Guid id, CancellationToken ct = default);
    Task<(byte[]? Bytes, string? Error)> ReportAsync(string environmentId, Guid id, CancellationToken ct = default);
    Task<(ParquetInspectionEvidence? Evidence, string? Error)> ApproveBaselineAsync(string environmentId, Guid id, CancellationToken ct = default);
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

    public Task<(List<ParquetOverview>? Sessions, string? Error)> SessionsAsync(IEnumerable<Guid> ids, CancellationToken ct = default) =>
        Get<List<ParquetOverview>>($"{Base}/sessions?ids={string.Join(',', ids.Take(20))}", ct);

    public async Task<(ParquetManifestSummary? Manifest, string? Error)> AttachManifestAsync(Guid session, string json, CancellationToken ct = default)
    {
        try
        {
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var response = await http.PostAsync($"{Base}/sessions/{session}/manifest", content, ct);
            return await Read<ParquetManifestSummary>(response, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, Fail(ex)); }
    }

    public Task<(ContractDiscovery? Discovery, string? Error)> DiscoverAsync(Guid session, string? project, CancellationToken ct = default) =>
        Get<ContractDiscovery>($"{Base}/sessions/{session}/contracts{(string.IsNullOrWhiteSpace(project) ? "" : "?project=" + Uri.EscapeDataString(project))}", ct);
    public Task<(List<ContractCandidate>? Contracts, string? Error)> ContractsAsync(string? project, CancellationToken ct = default) =>
        Get<List<ContractCandidate>>($"{Base}/contracts{(string.IsNullOrWhiteSpace(project) ? "" : "?project=" + Uri.EscapeDataString(project))}", ct);
    public Task<(ContractAssociation? Association, string? Error)> AssociateAsync(Guid session, ContractAssociation association, CancellationToken ct = default) =>
        Post<ContractAssociation>($"{Base}/sessions/{session}/association", association, ct);
    public Task<(RuleGeneration? Generation, string? Error)> GenerateAsync(Guid session, ContractAssociation? association, CancellationToken ct = default) =>
        Post<RuleGeneration>($"{Base}/sessions/{session}/rules", association ?? new ContractAssociation(), ct);
    public Task<(ParquetQualityResult? Result, string? Error)> ReviewAsync(Guid session, ParquetQualityRequest request, CancellationToken ct = default) =>
        Post<ParquetQualityResult>($"{Base}/sessions/{session}/review", request, ct);
    public Task<(ParquetBaselineResult? Result, string? Error)> BaselineAsync(ParquetBaselineRequest request, CancellationToken ct = default) =>
        Post<ParquetBaselineResult>($"{Base}/baseline", request, ct);
    public Task<(List<ParquetRuleSetVersion>? Versions, string? Error)> RuleSetsAsync(string project, string contract, string version, CancellationToken ct = default) =>
        Get<List<ParquetRuleSetVersion>>($"{Base}/rule-sets?project={Uri.EscapeDataString(project)}&contract={Uri.EscapeDataString(contract)}&version={Uri.EscapeDataString(version)}", ct);
    public Task<(ParquetRuleSetVersion? Version, string? Error)> ApproveRulesAsync(Guid session, ParquetApprovalRequest request, CancellationToken ct = default) =>
        Post<ParquetRuleSetVersion>($"{Base}/sessions/{session}/rule-sets", request, ct);
    public Task<(ParquetInspectionEvidence? Evidence, string? Error)> EvidenceDetailAsync(string environmentId, Guid id, CancellationToken ct = default) =>
        Get<ParquetInspectionEvidence>($"{Base}/evidence/{id}?environmentId={Uri.EscapeDataString(environmentId)}", ct);
    public async Task<(byte[]? Bytes, string? Error)> ReportAsync(string environmentId, Guid id, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync($"{Base}/evidence/{id}/report?environmentId={Uri.EscapeDataString(environmentId)}", ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadAsByteArrayAsync(ct), null) : (null, await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return (null, Fail(ex)); }
    }
    public Task<(ParquetInspectionEvidence? Evidence, string? Error)> ApproveBaselineAsync(string environmentId, Guid id, CancellationToken ct = default) =>
        Post<ParquetInspectionEvidence>($"{Base}/evidence/{id}/approve-baseline?environmentId={Uri.EscapeDataString(environmentId)}", new { }, ct);

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
