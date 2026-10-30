using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.DeliveryReporting;

namespace BirkNext.Web.Services;

/// <summary>Delivery Reporting calls (api/delivery-reporting). Everything here is owned by Delivery Reporting: no workspace, project, target or
/// Source Analysis state is read or written.</summary>
public interface IDeliveryReportingApiService
{
    Task<(List<DeliveryProject> Projects, string? Error)> ProjectsAsync(CancellationToken ct = default);
    Task<(DeliveryProject? Project, string? Error)> CreateProjectAsync(string name, string? description, CancellationToken ct = default);
    Task<(DeliveryImportPreview? Preview, string? Error)> PreviewAsync(Guid? projectId, string fileName, Stream content, CancellationToken ct = default);
    Task<DeliveryImportCommitResult> CommitAsync(Guid stagingId, DeliveryImportCommitRequest request, CancellationToken ct = default);
    Task DiscardAsync(Guid stagingId, CancellationToken ct = default);
    Task<List<DeliverySnapshotSummary>> SnapshotsAsync(Guid projectId, CancellationToken ct = default);
    Task<DeliverySnapshotDetail?> SnapshotAsync(Guid projectId, Guid snapshotId, CancellationToken ct = default);
    Task<(DeliverySnapshotSummary? Snapshot, string? Error)> UpdateSnapshotAsync(Guid projectId, Guid snapshotId, UpdateDeliverySnapshotRequest request, CancellationToken ct = default);
    Task<string?> DeleteSnapshotAsync(Guid projectId, Guid snapshotId, CancellationToken ct = default);
    Task<DeliveryBaselineSuggestion?> SuggestBaselineAsync(Guid projectId, Guid currentId, CancellationToken ct = default);
    Task<List<DeliveryReportSummaryRow>> ReportsAsync(Guid projectId, CancellationToken ct = default);
    Task<(WeeklyDeliveryReport? Report, string? Error)> GenerateAsync(GenerateWeeklyReportRequest request, CancellationToken ct = default);
    Task<WeeklyDeliveryReport?> ReportAsync(Guid projectId, Guid reportId, CancellationToken ct = default);
    Task<(DeliveryReportExport? Export, string? Error)> ExportAsync(Guid projectId, Guid reportId, DeliveryExportFormat format, bool appendix, bool unchanged, CancellationToken ct = default);
    Task<string?> DeleteReportAsync(Guid projectId, Guid reportId, CancellationToken ct = default);
}

public sealed class DeliveryReportingApiService(HttpClient http) : IDeliveryReportingApiService
{
    private const string Base = "api/delivery-reporting";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(List<DeliveryProject> Projects, string? Error)> ProjectsAsync(CancellationToken ct = default)
    {
        try { return (await http.GetFromJsonAsync<List<DeliveryProject>>($"{Base}/projects", Json, ct) ?? [], null); }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return ([], BackendRequestClassifier.FromException(DeliveryReportingText.Title, e, $"GET {Base}/projects", ct).UserMessage); }
    }

    public async Task<(DeliveryProject? Project, string? Error)> CreateProjectAsync(string name, string? description, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/projects", new CreateDeliveryProjectRequest(name, description), Json, ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<DeliveryProject>(Json, ct), null) : (null, await MessageAsync(response, ct));
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(DeliveryReportingText.Title, e, $"POST {Base}/projects", ct).UserMessage); }
    }

    public async Task<(DeliveryImportPreview? Preview, string? Error)> PreviewAsync(Guid? projectId, string fileName, Stream content, CancellationToken ct = default)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            var file = new StreamContent(content);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(file, "file", fileName);
            using var response = await http.PostAsync($"{Base}/imports/preview{(projectId is { } id ? $"?projectId={id}" : "")}", form, ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<DeliveryImportPreview>(Json, ct), null) : (null, await MessageAsync(response, ct));
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(DeliveryReportingText.Title, e, $"POST {Base}/imports/preview", ct).UserMessage); }
    }

    public async Task<DeliveryImportCommitResult> CommitAsync(Guid stagingId, DeliveryImportCommitRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/imports/{stagingId}/commit", request, Json, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            try { if (JsonSerializer.Deserialize<DeliveryImportCommitResult>(text, Json) is { } result) return result; } catch (JsonException) { }
            return new(null, null, $"The snapshot could not be imported (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return new(null, null, BackendRequestClassifier.FromException(DeliveryReportingText.Title, e, $"POST {Base}/imports/commit", ct).UserMessage); }
    }

    public async Task DiscardAsync(Guid stagingId, CancellationToken ct = default)
    {
        try { using var _ = await http.DeleteAsync($"{Base}/imports/{stagingId}", ct); }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { }
    }

    public async Task<List<DeliverySnapshotSummary>> SnapshotsAsync(Guid projectId, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<List<DeliverySnapshotSummary>>($"{Base}/projects/{projectId}/snapshots", Json, ct) ?? []; }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return []; }
    }

    public async Task<DeliverySnapshotDetail?> SnapshotAsync(Guid projectId, Guid snapshotId, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<DeliverySnapshotDetail>($"{Base}/projects/{projectId}/snapshots/{snapshotId}", Json, ct); }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return null; }
    }

    public async Task<(DeliverySnapshotSummary? Snapshot, string? Error)> UpdateSnapshotAsync(Guid projectId, Guid snapshotId, UpdateDeliverySnapshotRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PatchAsJsonAsync($"{Base}/projects/{projectId}/snapshots/{snapshotId}", request, Json, ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<DeliverySnapshotSummary>(Json, ct), null) : (null, await MessageAsync(response, ct));
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(DeliveryReportingText.Title, e, $"PATCH {Base}/snapshots", ct).UserMessage); }
    }

    public async Task<string?> DeleteSnapshotAsync(Guid projectId, Guid snapshotId, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.DeleteAsync($"{Base}/projects/{projectId}/snapshots/{snapshotId}", ct);
            return response.IsSuccessStatusCode ? null : await MessageAsync(response, ct);
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return BackendRequestClassifier.FromException(DeliveryReportingText.Title, e, $"DELETE {Base}/snapshots", ct).UserMessage; }
    }

    public async Task<DeliveryBaselineSuggestion?> SuggestBaselineAsync(Guid projectId, Guid currentId, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<DeliveryBaselineSuggestion>($"{Base}/projects/{projectId}/snapshots/{currentId}/baseline-suggestion", Json, ct); }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return null; }
    }

    public async Task<List<DeliveryReportSummaryRow>> ReportsAsync(Guid projectId, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<List<DeliveryReportSummaryRow>>($"{Base}/projects/{projectId}/reports", Json, ct) ?? []; }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return []; }
    }

    public async Task<(WeeklyDeliveryReport? Report, string? Error)> GenerateAsync(GenerateWeeklyReportRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/reports", request, Json, ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<WeeklyDeliveryReport>(Json, ct), null) : (null, await MessageAsync(response, ct));
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(DeliveryReportingText.Title, e, $"POST {Base}/reports", ct).UserMessage); }
    }

    public async Task<WeeklyDeliveryReport?> ReportAsync(Guid projectId, Guid reportId, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<WeeklyDeliveryReport>($"{Base}/projects/{projectId}/reports/{reportId}", Json, ct); }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return null; }
    }

    public async Task<(DeliveryReportExport? Export, string? Error)> ExportAsync(Guid projectId, Guid reportId, DeliveryExportFormat format, bool appendix, bool unchanged, CancellationToken ct = default)
    {
        try
        {
            var url = $"{Base}/projects/{projectId}/reports/{reportId}/export?format={format}&appendix={(appendix ? "true" : "false")}&unchanged={(unchanged ? "true" : "false")}";
            using var response = await http.GetAsync(url, ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<DeliveryReportExport>(Json, ct), null) : (null, await MessageAsync(response, ct));
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(DeliveryReportingText.Title, e, $"GET {Base}/export", ct).UserMessage); }
    }

    public async Task<string?> DeleteReportAsync(Guid projectId, Guid reportId, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.DeleteAsync($"{Base}/projects/{projectId}/reports/{reportId}", ct);
            return response.IsSuccessStatusCode ? null : await MessageAsync(response, ct);
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return BackendRequestClassifier.FromException(DeliveryReportingText.Title, e, $"DELETE {Base}/reports", ct).UserMessage; }
    }

    private static async Task<string> MessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } message) return message;
        }
        catch (JsonException) { }
        return response.StatusCode == HttpStatusCode.NotFound ? "Not found. It may have been deleted." : $"The request failed (HTTP {(int)response.StatusCode}).";
    }
}
