using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>Manual test runs (api/manual-test-runs). Errors come back as a message, never as an exception. Nothing here executes anything or talks
/// to Azure DevOps: a tester records what happened.</summary>
public interface IManualTestRunsApiService
{
    Task<(List<ManualRunSummary>? Runs, string? Error)> ListAsync(string projectKey, Guid? draftId = null, string? caseId = null, CancellationToken ct = default);
    /// <summary>Whether testers can execute in BirkNext (System settings; off by default). Unknown = off.</summary>
    Task<bool> AvailabilityAsync(CancellationToken ct = default) => Task.FromResult(false);
    Task<(ManualTestRun? Run, string? Error)> GetAsync(string projectKey, Guid id, CancellationToken ct = default);
    /// <summary>One page of the project's runs, newest first, with the exact total. Hosts without paging return no page (callers fall back).</summary>
    Task<(ManualRunPage? Page, string? Error)> PageAsync(string projectKey, int skip, int take, Guid? draftId = null, string? caseId = null, CancellationToken ct = default) =>
        Task.FromResult<(ManualRunPage?, string?)>((null, null));
    Task<(ManualTestRun? Run, string? Error)> StartAsync(StartManualRunRequest request, CancellationToken ct = default);
    Task<(ManualTestRun? Run, string? Error)> SaveAsync(string projectKey, Guid id, SaveManualRunRequest request, CancellationToken ct = default);
    Task<(ManualTestRun? Run, string? Error)> CompleteAsync(string projectKey, Guid id, int baseRevision, CancellationToken ct = default);
    Task<(ManualTestRun? Run, string? Error)> AbandonAsync(string projectKey, Guid id, int baseRevision, string reason, CancellationToken ct = default);

    /// <summary>Follows up a defect of a completed or stopped run (status, note, work item reference, retest run).</summary>
    Task<(ManualTestRun? Run, string? Error)> UpdateDefectAsync(string projectKey, Guid id, string defectId, UpdateManualDefectRequest request, CancellationToken ct = default) =>
        Task.FromResult<(ManualTestRun?, string?)>((null, "Defect follow-up is not available in this host."));
    /// <summary>Every defect of the project's runs.</summary>
    Task<(List<ManualDefectListItem>? Defects, string? Error)> DefectsAsync(string projectKey, CancellationToken ct = default) =>
        Task.FromResult<(List<ManualDefectListItem>?, string?)>((null, "The defect list is not available in this host."));
    /// <summary>Attaches a file as evidence (multipart upload).</summary>
    Task<(ManualTestRun? Run, string? Error)> AttachAsync(string projectKey, Guid id, int baseRevision, int? step, string? label, string fileName, byte[] content, CancellationToken ct = default) =>
        Task.FromResult<(ManualTestRun?, string?)>((null, "Attaching files is not available in this host."));
    /// <summary>The bytes of an attachment (for a download).</summary>
    Task<(byte[]? Content, string? Error)> AttachmentAsync(string projectKey, Guid id, string attachmentId, CancellationToken ct = default) =>
        Task.FromResult<(byte[]?, string?)>((null, "Attachments are not available in this host."));
    /// <summary>The run's attached files as stored: name, size, SHA-256, malware scan state and withdrawal (no content).</summary>
    Task<(List<ManualRunAttachmentInfo>? Files, string? Error)> AttachmentsAsync(string projectKey, Guid id, CancellationToken ct = default) =>
        Task.FromResult<(List<ManualRunAttachmentInfo>?, string?)>((null, "Attachments are not available in this host."));
    /// <summary>Withdraws a file at a person's request: its content is removed, its record stays (who, when, why).</summary>
    Task<(ManualRunAttachmentInfo? File, string? Error)> WithdrawAttachmentAsync(string projectKey, Guid id, string attachmentId, string reason, CancellationToken ct = default) =>
        Task.FromResult<(ManualRunAttachmentInfo?, string?)>((null, "Attachments are not available in this host."));
}

public sealed class ManualTestRunsApiService(HttpClient http) : IManualTestRunsApiService
{
    private const string Base = "api/manual-test-runs";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static string P(string projectKey) => $"projectKey={Uri.EscapeDataString(projectKey)}";

    public async Task<bool> AvailabilityAsync(CancellationToken ct = default)
    {
        var (a, _) = await Send<ManualExecutionAvailability>(() => http.GetAsync($"{Base}/availability", ct), ct);
        return a?.Enabled ?? false;
    }

    public Task<(List<ManualRunSummary>?, string?)> ListAsync(string projectKey, Guid? draftId = null, string? caseId = null, CancellationToken ct = default) =>
        Send<List<ManualRunSummary>>(() => http.GetAsync($"{Base}?{P(projectKey)}{(draftId is { } d ? $"&draftId={d}" : "")}{(caseId is { Length: > 0 } c ? $"&caseId={Uri.EscapeDataString(c)}" : "")}", ct), ct);

    public Task<(ManualRunPage?, string?)> PageAsync(string projectKey, int skip, int take, Guid? draftId = null, string? caseId = null, CancellationToken ct = default) =>
        Send<ManualRunPage>(() => http.GetAsync($"{Base}/page?{P(projectKey)}&skip={skip}&take={take}{(draftId is { } d ? $"&draftId={d}" : "")}{(caseId is { Length: > 0 } c ? $"&caseId={Uri.EscapeDataString(c)}" : "")}", ct), ct);

    public Task<(ManualTestRun?, string?)> GetAsync(string projectKey, Guid id, CancellationToken ct = default) =>
        Send<ManualTestRun>(() => http.GetAsync($"{Base}/{id}?{P(projectKey)}", ct), ct);

    public Task<(ManualTestRun?, string?)> StartAsync(StartManualRunRequest request, CancellationToken ct = default) =>
        Send<ManualTestRun>(() => http.PostAsJsonAsync(Base, request, Json, ct), ct);

    public Task<(ManualTestRun?, string?)> SaveAsync(string projectKey, Guid id, SaveManualRunRequest request, CancellationToken ct = default) =>
        Send<ManualTestRun>(() => http.PutAsJsonAsync($"{Base}/{id}?{P(projectKey)}", request, Json, ct), ct);

    public Task<(ManualTestRun?, string?)> CompleteAsync(string projectKey, Guid id, int baseRevision, CancellationToken ct = default) =>
        Send<ManualTestRun>(() => http.PostAsJsonAsync($"{Base}/{id}/complete?{P(projectKey)}", new CompleteManualRunRequest(baseRevision), Json, ct), ct);

    public Task<(ManualTestRun?, string?)> AbandonAsync(string projectKey, Guid id, int baseRevision, string reason, CancellationToken ct = default) =>
        Send<ManualTestRun>(() => http.PostAsJsonAsync($"{Base}/{id}/abandon?{P(projectKey)}", new AbandonManualRunRequest(baseRevision, reason), Json, ct), ct);

    public Task<(ManualTestRun?, string?)> UpdateDefectAsync(string projectKey, Guid id, string defectId, UpdateManualDefectRequest request, CancellationToken ct = default) =>
        Send<ManualTestRun>(() => http.PostAsJsonAsync($"{Base}/{id}/defects/{Uri.EscapeDataString(defectId)}?{P(projectKey)}", request, Json, ct), ct);

    public Task<(List<ManualDefectListItem>?, string?)> DefectsAsync(string projectKey, CancellationToken ct = default) =>
        Send<List<ManualDefectListItem>>(() => http.GetAsync($"{Base}/defects?{P(projectKey)}", ct), ct);

    public Task<(ManualTestRun?, string?)> AttachAsync(string projectKey, Guid id, int baseRevision, int? step, string? label, string fileName, byte[] content, CancellationToken ct = default) =>
        Send<ManualTestRun>(() =>
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(content);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", fileName);
            if (step is { } n) form.Add(new StringContent(n.ToString(System.Globalization.CultureInfo.InvariantCulture)), "step");
            if (!string.IsNullOrWhiteSpace(label)) form.Add(new StringContent(label), "label");
            return http.PostAsync($"{Base}/{id}/attachments?{P(projectKey)}&baseRevision={baseRevision}", form, ct);
        }, ct);

    public async Task<(byte[]?, string?)> AttachmentAsync(string projectKey, Guid id, string attachmentId, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync($"{Base}/{id}/attachments/{Uri.EscapeDataString(attachmentId)}?{P(projectKey)}", ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadAsByteArrayAsync(ct), null);
            if ((int)response.StatusCode == 410)
                try { return (null, (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("message").GetString()); } catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { }
            return (null, (int)response.StatusCode switch { 404 => "The attachment no longer exists.", 410 => "The file was withdrawn.", var c => $"The attachment could not be downloaded (HTTP {c})." });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return (null, "The attachment could not be downloaded."); }
    }

    public Task<(List<ManualRunAttachmentInfo>?, string?)> AttachmentsAsync(string projectKey, Guid id, CancellationToken ct = default) =>
        Send<List<ManualRunAttachmentInfo>>(() => http.GetAsync($"{Base}/{id}/attachments?{P(projectKey)}", ct), ct);

    public Task<(ManualRunAttachmentInfo?, string?)> WithdrawAttachmentAsync(string projectKey, Guid id, string attachmentId, string reason, CancellationToken ct = default) =>
        Send<ManualRunAttachmentInfo>(() => http.PostAsJsonAsync($"{Base}/{id}/attachments/{Uri.EscapeDataString(attachmentId)}/withdraw?{P(projectKey)}", new WithdrawAttachmentRequest(reason), Json, ct), ct);

    private static async Task<(T? Value, string? Error)> Send<T>(Func<Task<HttpResponseMessage>> call, CancellationToken ct)
    {
        try
        {
            using var response = await call();
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
            if ((int)response.StatusCode is 401 or 403) return (default, "You do not have permission to record test runs.");
            string? message = null;
            try { message = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("message").GetString(); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
            return (default, message ?? $"The request failed (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return (default, "Test runs are not reachable.");
        }
    }
}
