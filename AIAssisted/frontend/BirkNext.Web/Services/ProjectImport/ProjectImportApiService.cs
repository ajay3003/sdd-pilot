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
    Task<(ProjectImportCommitResult? Result, SourceUploadFailure? Error)> CommitAsync(Guid stagingId, string? environmentId, CancellationToken ct = default);

    /// <summary>Releases a staged archive that will not be imported.</summary>
    Task DiscardAsync(Guid stagingId, CancellationToken ct = default);
}

public sealed class ProjectImportApiService(HttpClient http) : IProjectImportApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(ProjectImportPreview? Preview, SourceUploadFailure? Error)> PreviewAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        using var body = new MultipartFormDataContent();
        body.Add(new StreamContent(content), "file", fileName);
        using var response = await http.PostAsync("api/project-import/preview", body, ct);
        if (!response.IsSuccessStatusCode) return (null, await IntegrationCatalogApiService.ReadSourceUploadFailure(response, ct));
        var preview = await response.Content.ReadFromJsonAsync<ProjectImportPreview>(Json, ct);
        return preview is null ? (null, new("IMPORT_RESPONSE_INVALID", "upload", "The archive was uploaded, but no import preview was returned.")) : (preview, null);
    }

    public async Task<(ProjectImportCommitResult? Result, SourceUploadFailure? Error)> CommitAsync(Guid stagingId, string? environmentId, CancellationToken ct = default)
    {
        var query = string.IsNullOrWhiteSpace(environmentId) ? "" : $"?environmentId={Uri.EscapeDataString(environmentId)}";
        using var response = await http.PostAsync($"api/project-import/{stagingId}/commit{query}", content: null, ct);
        if (!response.IsSuccessStatusCode) return (null, await IntegrationCatalogApiService.ReadSourceUploadFailure(response, ct));
        var result = await response.Content.ReadFromJsonAsync<ProjectImportCommitResult>(Json, ct);
        return result is null ? (null, new("IMPORT_RESPONSE_INVALID", "persistence", "The import completed, but no result was returned.")) : (result, null);
    }

    public async Task DiscardAsync(Guid stagingId, CancellationToken ct = default)
    {
        try { using var _ = await http.DeleteAsync($"api/project-import/{stagingId}", ct); }
        catch (HttpRequestException) { /* the staging expires on its own */ }
    }
}
