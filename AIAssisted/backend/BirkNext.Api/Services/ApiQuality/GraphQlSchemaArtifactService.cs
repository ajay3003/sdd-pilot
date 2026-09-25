using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.ApiReview;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ContractAnalysis;
using HotChocolate.Language;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ApiQuality;

/// <summary>The outcome of validating an uploaded schema artifact. Invalid artifacts are never stored and never used.</summary>
public sealed record GraphQlSchemaArtifactValidationResult(bool Valid, string? Reason, GraphQlNormalizedContract? Schema, string ContentHash, long SizeBytes);

/// <summary>A configured artifact resolved for a review run: the parsed schema, or why it cannot be used.</summary>
public sealed record ResolvedGraphQlSchemaArtifact(GraphQlSchemaArtifact Artifact, GraphQlNormalizedContract? Schema, string? Problem);

public interface IGraphQlSchemaArtifactStore
{
    Task<IReadOnlyList<GraphQlSchemaArtifact>> ListAsync(string environmentId, CancellationToken ct = default);
    /// <summary>Validates and stores (replaces) the artifact of one target. Returns the stored metadata, or the validation reason.</summary>
    Task<(GraphQlSchemaArtifact? Artifact, string? Error)> SaveAsync(GraphQlSchemaArtifactUpload upload, CancellationToken ct = default);
    Task<bool> DeleteAsync(string environmentId, string targetId, CancellationToken ct = default);
    /// <summary>The artifact bound to exactly this environment and target, parsed. Null when none is configured.</summary>
    Task<ResolvedGraphQlSchemaArtifact?> ResolveAsync(string environmentId, string targetId, CancellationToken ct = default);
}

/// <summary>
/// Trusted GraphQL schema artifacts, one per (Target Environment, API target). An artifact exists only because someone explicitly
/// configured it for that target: nothing is scanned, discovered, downloaded or derived from observed operations, and a DEV artifact
/// never applies to another environment. The content is parsed as GraphQL SDL only — never executed.
/// </summary>
public sealed class GraphQlSchemaArtifactService(AppDbContext db, ILogger<GraphQlSchemaArtifactService> logger) : IGraphQlSchemaArtifactStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static GraphQlSchemaArtifactValidationResult Validate(string? fileName, string? content)
    {
        content ??= "";
        var bytes = Encoding.UTF8.GetByteCount(content);
        var hash = Hash(content);
        GraphQlSchemaArtifactValidationResult Invalid(string reason) => new(false, reason, null, hash, bytes);

        if (!GraphQlSchemaArtifactRules.HasAllowedExtension(fileName))
            return Invalid($"Only {string.Join(", ", GraphQlSchemaArtifactRules.AllowedExtensions)} schema files are accepted.");
        if (string.IsNullOrWhiteSpace(content)) return Invalid("The file is empty.");
        if (bytes > GraphQlSchemaArtifactRules.MaxBytes) return Invalid($"The file is larger than the {GraphQlSchemaArtifactRules.MaxBytes / (1024 * 1024)} MB schema artifact limit.");
        if (content.Any(c => c == '\0' || char.IsControl(c) && c is not ('\t' or '\r' or '\n')))
            return Invalid("The file is not GraphQL SDL text (binary content).");

        DocumentNode document;
        try { document = Utf8GraphQLParser.Parse(content); }
        catch (SyntaxException ex) { return Invalid($"Not valid GraphQL: {ex.Message}"); }

        var executable = document.Definitions.Count(d => d is OperationDefinitionNode or FragmentDefinitionNode);
        if (executable > 0 && executable == document.Definitions.Count)
            return Invalid("This is a GraphQL operation document (queries/fragments), not a schema. Upload the server's SDL.");
        if (executable > 0)
            return Invalid("The file mixes operations with type definitions; a schema artifact must contain type definitions only.");

        var schema = GraphQlSdlSchema.FromSdl(content, out var error);
        return schema is null ? Invalid(error ?? "The SDL does not describe a usable schema.") : new(true, null, schema, hash, bytes);
    }

    public static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    public async Task<IReadOnlyList<GraphQlSchemaArtifact>> ListAsync(string environmentId, CancellationToken ct = default) =>
        (await db.GraphQlSchemaArtifacts.AsNoTracking().Where(a => a.EnvironmentId == environmentId).ToListAsync(ct))
            .Select(Metadata).OrderBy(a => a.TargetUrl, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<(GraphQlSchemaArtifact? Artifact, string? Error)> SaveAsync(GraphQlSchemaArtifactUpload upload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(upload.EnvironmentId) || string.IsNullOrWhiteSpace(upload.TargetId))
            return (null, "A schema artifact must be bound to a Target Environment and a GraphQL API target.");
        var fileName = System.IO.Path.GetFileName((upload.FileName ?? "").Replace('\\', '/'));   // never a client path
        var validation = Validate(fileName, upload.Content);
        if (!validation.Valid)
        {
            logger.LogInformation("GraphQL schema artifact rejected for target {TargetId} in {EnvironmentId}: {Reason}", upload.TargetId, upload.EnvironmentId, validation.Reason);
            return (null, validation.Reason);
        }

        var now = DateTimeOffset.UtcNow;
        var record = await db.GraphQlSchemaArtifacts.FirstOrDefaultAsync(a => a.EnvironmentId == upload.EnvironmentId && a.TargetId == upload.TargetId, ct);
        var previous = record is null ? null : Metadata(record);
        var schema = validation.Schema!;
        var artifact = new GraphQlSchemaArtifact
        {
            Id = previous?.Id ?? Guid.NewGuid().ToString("N"),
            EnvironmentId = upload.EnvironmentId, TargetId = upload.TargetId, TargetUrl = upload.TargetUrl, FileName = fileName,
            ContentHash = validation.ContentHash, SizeBytes = validation.SizeBytes, TypeCount = schema.Types.Count,
            QueryType = schema.QueryTypeName ?? "", MutationType = schema.MutationTypeName, SubscriptionType = schema.SubscriptionTypeName,
            ValidationStatus = GraphQlSchemaArtifactValidation.Valid, Notes = string.IsNullOrWhiteSpace(upload.Notes) ? null : upload.Notes.Trim(),
            ImportedAt = previous?.ImportedAt ?? now, UpdatedAt = now,
        };
        if (record is null)
        {
            record = new GraphQlSchemaArtifactRecord { EnvironmentId = upload.EnvironmentId, TargetId = upload.TargetId };
            db.GraphQlSchemaArtifacts.Add(record);
        }
        record.Id = artifact.Id;
        record.ContentHash = artifact.ContentHash;
        record.Content = upload.Content;
        record.DocumentJson = JsonSerializer.Serialize(artifact, Json);
        record.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("GraphQL schema artifact {ArtifactId} ({FileName}, {Hash}, {Types} types) {Action} for target {TargetId} in {EnvironmentId}.",
            artifact.Id, artifact.FileName, artifact.ShortHash, artifact.TypeCount, previous is null ? "configured" : "replaced", artifact.TargetId, artifact.EnvironmentId);
        return (artifact, null);
    }

    public async Task<bool> DeleteAsync(string environmentId, string targetId, CancellationToken ct = default)
    {
        var record = await db.GraphQlSchemaArtifacts.FirstOrDefaultAsync(a => a.EnvironmentId == environmentId && a.TargetId == targetId, ct);
        if (record is null) return false;
        db.GraphQlSchemaArtifacts.Remove(record);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("GraphQL schema artifact {ArtifactId} removed from target {TargetId} in {EnvironmentId}; earlier review results keep their snapshot.", record.Id, targetId, environmentId);
        return true;
    }

    public async Task<ResolvedGraphQlSchemaArtifact?> ResolveAsync(string environmentId, string targetId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || string.IsNullOrWhiteSpace(targetId)) return null;
        var record = await db.GraphQlSchemaArtifacts.AsNoTracking().FirstOrDefaultAsync(a => a.EnvironmentId == environmentId && a.TargetId == targetId, ct);
        if (record is null) return null;
        var artifact = Metadata(record);
        // Re-validated at use: the stored text is the source of truth, and a stored artifact that no longer parses is never used.
        var validation = Validate(artifact.FileName, record.Content);
        return validation.Valid
            ? new ResolvedGraphQlSchemaArtifact(artifact, validation.Schema, null)
            : new ResolvedGraphQlSchemaArtifact(artifact with { ValidationStatus = GraphQlSchemaArtifactValidation.Invalid, ValidationMessage = validation.Reason }, null, validation.Reason);
    }

    private static GraphQlSchemaArtifact Metadata(GraphQlSchemaArtifactRecord record) =>
        JsonSerializer.Deserialize<GraphQlSchemaArtifact>(record.DocumentJson, Json) ?? new GraphQlSchemaArtifact { Id = record.Id, EnvironmentId = record.EnvironmentId, TargetId = record.TargetId, ContentHash = record.ContentHash };
}
