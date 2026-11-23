using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.ProjectImport;

namespace BirkNext.Web.Services.ProjectImport;

/// <summary>Client of <c>api/project-import</c>. Failures use the Source Analysis upload contract (<see cref="SourceUploadFailure"/>).</summary>
public interface IProjectImportApiService
{
    /// <summary>Uploads the archive once; the backend validates and stages it and reports its documents and source. Nothing is activated.</summary>
    Task<(ProjectImportPreview? Preview, SourceUploadFailure? Error)> PreviewAsync(string fileName, Stream content, CancellationToken ct = default);

    /// <summary>Creates (or reuses) the Source Analysis snapshot from the staged archive. Can be called again to retry the source part.</summary>
    Task<(ProjectImportCommitResult? Result, SourceUploadFailure? Error)> CommitAsync(Guid stagingId, CancellationToken ct = default);

    /// <summary>Same commit pipeline as CommitAsync, with live stage updates from the server.</summary>
    Task<(ProjectImportCommitResult? Result, SourceUploadFailure? Error)> CommitWithProgressAsync(Guid stagingId,
        IProgress<ProjectImportCommitEvent>? progress, CancellationToken ct = default) => CommitAsync(stagingId, ct);

    /// <summary>Releases a staged archive that will not be imported.</summary>
    Task DiscardAsync(Guid stagingId, CancellationToken ct = default);

    /// <summary>The archive's own application identity and the applications already imported (null when unavailable).</summary>
    Task<ProjectImportApplicationOptions?> ApplicationOptionsAsync(Guid stagingId, CancellationToken ct = default) => Task.FromResult<ProjectImportApplicationOptions?>(null);

    /// <summary>Records "Ny applikasjon" / "Ny versjon av …" on the staged upload before the import.</summary>
    Task<(ProjectImportApplicationChoice? Choice, string? Error)> ChooseApplicationAsync(Guid stagingId, ProjectImportApplicationChoice choice, CancellationToken ct = default) =>
        Task.FromResult<(ProjectImportApplicationChoice?, string?)>((null, "Valget er ikke tilgjengelig her."));
}

public sealed class ProjectImportApiService(HttpClient http) : IProjectImportApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ProjectImportApplicationOptions?> ApplicationOptionsAsync(Guid stagingId, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync($"api/project-import/{stagingId}/application", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<ProjectImportApplicationOptions>(Json, ct) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }

    public async Task<(ProjectImportApplicationChoice? Choice, string? Error)> ChooseApplicationAsync(Guid stagingId, ProjectImportApplicationChoice choice, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"api/project-import/{stagingId}/application", choice, Json, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<ProjectImportApplicationChoice>(Json, ct), null);
            var failure = await IntegrationCatalogApiService.ReadSourceUploadFailure(response, ct);
            return (null, failure.Message);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return (null, "BirkNext svarte ikke. Prøv igjen."); }
    }

    public async Task<(ProjectImportPreview? Preview, SourceUploadFailure? Error)> PreviewAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        using var body = new MultipartFormDataContent();
        body.Add(new StreamContent(content), "file", fileName);
        using var response = await http.PostAsync("api/project-import/preview", body, ct);
        if (!response.IsSuccessStatusCode) return (null, await IntegrationCatalogApiService.ReadSourceUploadFailure(response, ct));
        var preview = await response.Content.ReadFromJsonAsync<ProjectImportPreview>(Json, ct);
        return preview is null ? (null, new("IMPORT_RESPONSE_INVALID", "upload", "The archive was uploaded, but no import preview was returned.")) : (preview, null);
    }

    public Task<(ProjectImportCommitResult? Result, SourceUploadFailure? Error)> CommitAsync(Guid stagingId, CancellationToken ct = default) =>
        CommitWithProgressAsync(stagingId, progress: null, ct);

    public async Task<(ProjectImportCommitResult? Result, SourceUploadFailure? Error)> CommitWithProgressAsync(Guid stagingId,
        IProgress<ProjectImportCommitEvent>? progress, CancellationToken ct = default)
    {
        // No Target Environment is sent: a source snapshot does not belong to one.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/project-import/{stagingId}/commit");
        request.Headers.Accept.ParseAdd("application/x-ndjson");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return (null, await IntegrationCatalogApiService.ReadSourceUploadFailure(response, ct));
        if (response.Content.Headers.ContentType?.MediaType != "application/x-ndjson")
        {
            // Compatibility with an older backend that returns the original single JSON result.
            var legacy = await response.Content.ReadFromJsonAsync<ProjectImportCommitResult>(Json, ct);
            return legacy is null ? (null, new("IMPORT_RESPONSE_INVALID", "commit", "The import ended without a result. Retry the import.")) : (legacy, null);
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        ProjectImportCommitResult? result = null;
        SourceUploadFailure? failure = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var update = JsonSerializer.Deserialize<ProjectImportCommitEvent>(line, Json)
                ?? throw new JsonException("Project import progress event was empty.");
            progress?.Report(update);
            if (update.Kind == ProjectImportCommitEventKind.Completed) result = update.Result;
            if (update.Kind == ProjectImportCommitEventKind.Failed)
                failure = new(update.ErrorCode ?? "PROJECT_IMPORT_FAILED", update.ErrorStage ?? "commit",
                    update.ErrorMessage ?? "The import could not be completed. Retry the import.", CorrelationId: update.CorrelationId);
        }
        if (failure is not null) return (null, failure);
        return result is null
            ? (null, new("IMPORT_PROGRESS_INTERRUPTED", "commit", "The import connection ended before the backend returned a result. Retry the import; a completed snapshot will be reused."))
            : (result, null);
    }

    public async Task DiscardAsync(Guid stagingId, CancellationToken ct = default)
    {
        try { using var _ = await http.DeleteAsync($"api/project-import/{stagingId}", ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { /* the staging expires on its own */ }
    }
}
