using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Web.Configuration;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

namespace BirkNext.Web.Services;

public sealed record SourceSchemaFile(string Path, string Text, string Fingerprint);
public sealed record SourceSchemaPackage(Guid SnapshotId, string SnapshotFingerprint, string SourceArchiveName,
    DateTimeOffset AnalyzedAt, bool IsComplete, int OmittedFiles, IReadOnlyList<SourceSchemaFile> Files,
    bool IsAvailable = true, string? UnavailableReason = null, string? ProjectImportId = null, string? SourceProjectName = null);
public sealed record SourceSchemaSnapshotSummary(Guid Id, string? ArchiveName, string? Fingerprint,
    DateTimeOffset AnalyzedAt, bool HasSchemaArtifact, bool IsComplete, int OmittedFiles, string? ProjectImportId = null);
public sealed record SourceSchemaSnapshotIndex(IReadOnlyList<SourceSchemaSnapshotSummary> Snapshots, bool IsTruncated);

/// <summary>
/// Source Analysis' read-only XSD artifacts: the snapshot index and the exact XSD package retained with one immutable snapshot.
/// Schema Studio and other schema consumers use this; the evidence document itself never leaves the backend.
/// </summary>
public interface ISourceSchemaApiService
{
    Task<SourceSchemaSnapshotIndex> ListSchemaSnapshotIndexAsync(CancellationToken ct = default);
    /// <summary>Gets XSD-only content captured for this exact immutable Source Analysis snapshot; never substitutes a newer snapshot.</summary>
    Task<SourceSchemaPackage?> SourceSchemasAsync(Guid snapshotId, CancellationToken ct = default);
}

public sealed class SourceSchemaApiService(HttpClient http) : ISourceSchemaApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A registered <see cref="ISourceSchemaApiService"/> wins; otherwise a client for the configured backend address (the same
    /// <c>BackendUrl</c> rule every API client uses). Null when the host has no HTTP client factory or configuration (for example bUnit).
    /// </summary>
    public static ISourceSchemaApiService? Resolve(IServiceProvider services)
    {
        if (services.GetService(typeof(ISourceSchemaApiService)) is ISourceSchemaApiService registered) return registered;
        if (services.GetService(typeof(IConfiguration)) is not IConfiguration configuration
            || services.GetService(typeof(IWebAssemblyHostEnvironment)) is not IWebAssemblyHostEnvironment environment
            || services.GetService(typeof(IHttpClientFactory)) is not IHttpClientFactory factory) return null;
        var backendUrl = configuration["BackendUrl"] ?? "https://localhost:5000";
        BackendUrlValidator.Validate(backendUrl, environment.Environment);
        var client = factory.CreateClient(nameof(SourceSchemaApiService));
        client.BaseAddress = BackendUrlValidator.BaseAddress(backendUrl);
        return new SourceSchemaApiService(client);
    }

    public async Task<SourceSchemaSnapshotIndex> ListSchemaSnapshotIndexAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<SourceSchemaSnapshotIndex>("api/source-analysis/schema-index", Json, ct)
        ?? new SourceSchemaSnapshotIndex([], false);

    public async Task<SourceSchemaPackage?> SourceSchemasAsync(Guid snapshotId, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<SourceSchemaPackage>($"api/source-analysis/{snapshotId:D}/schemas", Json, ct); }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }
}
