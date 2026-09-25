using BirkNext.ApiReview;
using BirkNext.Web.Services;

namespace BirkNext.Web.Tests.Integration;

/// <summary>In-memory <see cref="IGraphQlSchemaArtifactApiService"/>: stores metadata per (environment, target), validates like the backend's first gates.</summary>
public sealed class FakeGraphQlSchemaArtifactApi : IGraphQlSchemaArtifactApiService
{
    public Dictionary<(string Env, string Target), GraphQlSchemaArtifact> Stored { get; } = [];
    public List<GraphQlSchemaArtifactUpload> Uploads { get; } = [];
    public string? RejectWith { get; set; }
    public Exception? ListFailure { get; set; }

    public Task<IReadOnlyList<GraphQlSchemaArtifact>> ListAsync(string environmentId, CancellationToken ct = default) =>
        ListFailure is not null ? Task.FromException<IReadOnlyList<GraphQlSchemaArtifact>>(ListFailure)
        : Task.FromResult<IReadOnlyList<GraphQlSchemaArtifact>>(Stored.Where(s => s.Key.Env == environmentId).Select(s => s.Value).ToList());

    public Task<GraphQlSchemaArtifactSaveResult> SaveAsync(GraphQlSchemaArtifactUpload upload, CancellationToken ct = default)
    {
        Uploads.Add(upload);
        if (RejectWith is not null) return Task.FromResult(new GraphQlSchemaArtifactSaveResult(null, RejectWith));
        var artifact = Artifact(upload.EnvironmentId, upload.TargetId, upload.FileName, upload.Content);
        Stored[(upload.EnvironmentId, upload.TargetId)] = artifact;
        return Task.FromResult(new GraphQlSchemaArtifactSaveResult(artifact, null));
    }

    public Task RemoveAsync(string environmentId, string targetId, CancellationToken ct = default)
    {
        Stored.Remove((environmentId, targetId));
        return Task.CompletedTask;
    }

    public static GraphQlSchemaArtifact Artifact(string env, string target, string fileName = "m2lb-schema.graphql", string content = "type Query { roles: [Role!]! } type Role { id: ID! }") => new()
    {
        Id = "a-" + target, EnvironmentId = env, TargetId = target, TargetUrl = "https://api-dev.example.test/graphql", FileName = fileName,
        ContentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
        SizeBytes = content.Length, TypeCount = 2, QueryType = "Query", ValidationStatus = GraphQlSchemaArtifactValidation.Valid,
        ImportedAt = new DateTimeOffset(2026, 9, 25, 11, 0, 0, TimeSpan.Zero), UpdatedAt = new DateTimeOffset(2026, 9, 25, 11, 10, 0, TimeSpan.Zero),
    };
}
