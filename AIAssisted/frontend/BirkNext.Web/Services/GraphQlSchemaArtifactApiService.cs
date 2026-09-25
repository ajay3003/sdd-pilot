using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.ApiReview;

namespace BirkNext.Web.Services;

/// <summary>Result of an upload: the stored artifact, or the validation reason it was rejected for (never stored).</summary>
public sealed record GraphQlSchemaArtifactSaveResult(GraphQlSchemaArtifact? Artifact, string? Error);

public interface IGraphQlSchemaArtifactApiService
{
    Task<IReadOnlyList<GraphQlSchemaArtifact>> ListAsync(string environmentId, CancellationToken ct = default);
    Task<GraphQlSchemaArtifactSaveResult> SaveAsync(GraphQlSchemaArtifactUpload upload, CancellationToken ct = default);
    Task RemoveAsync(string environmentId, string targetId, CancellationToken ct = default);
}

/// <summary>Trusted GraphQL schema artifacts per (Target Environment, API target), stored by the backend. The SDL is sent once on upload and never read back.</summary>
public sealed class GraphQlSchemaArtifactApiService(HttpClient http) : IGraphQlSchemaArtifactApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<GraphQlSchemaArtifact>> ListAsync(string environmentId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<GraphQlSchemaArtifact>>($"api/graphql-schema-artifacts?environmentId={Uri.EscapeDataString(environmentId)}", Json, ct) ?? [];

    public async Task<GraphQlSchemaArtifactSaveResult> SaveAsync(GraphQlSchemaArtifactUpload upload, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync("api/graphql-schema-artifacts", upload, Json, ct);
        if (response.IsSuccessStatusCode) return new(await response.Content.ReadFromJsonAsync<GraphQlSchemaArtifact>(Json, ct), null);
        if ((int)response.StatusCode == 400)
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            return new(null, problem.TryGetProperty("message", out var message) ? message.GetString() : "The schema artifact was rejected.");
        }
        return new(null, $"The schema artifact could not be saved (HTTP {(int)response.StatusCode}).");
    }

    public async Task RemoveAsync(string environmentId, string targetId, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"api/graphql-schema-artifacts?environmentId={Uri.EscapeDataString(environmentId)}&targetId={Uri.EscapeDataString(targetId)}", ct);
        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 404) response.EnsureSuccessStatusCode();
    }
}
