using System.Text.Json.Serialization;

namespace BirkNext.ApiReview;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GraphQlSchemaArtifactValidation { Valid, Invalid }

/// <summary>
/// A trusted GraphQL schema (SDL) explicitly configured for ONE GraphQL API target in ONE Target Environment. Used by API Quality
/// Review only as the fallback schema for client/server compatibility when runtime introspection is unavailable. Never discovered,
/// never inferred from observed operations, never shared across targets or environments. The content itself is not part of this
/// record; it stays in the backend store.
/// </summary>
public sealed record GraphQlSchemaArtifact
{
    public string Id { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    /// <summary>Stable API target identity (<see cref="ApiReviewTarget.TargetId"/>), never a display name.</summary>
    public string TargetId { get; init; } = "";
    /// <summary>The target endpoint when the artifact was configured (display and audit only; binding is <see cref="TargetId"/>).</summary>
    public string TargetUrl { get; init; } = "";
    public string FileName { get; init; } = "";
    /// <summary>SHA-256 of the UTF-8 content, lowercase hex (64 chars).</summary>
    public string ContentHash { get; init; } = "";
    public long SizeBytes { get; init; }
    public int TypeCount { get; init; }
    public string QueryType { get; init; } = "";
    public string? MutationType { get; init; }
    public string? SubscriptionType { get; init; }
    public GraphQlSchemaArtifactValidation ValidationStatus { get; init; } = GraphQlSchemaArtifactValidation.Valid;
    public string? ValidationMessage { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset ImportedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    [JsonIgnore] public string ShortHash => GraphQlSchemaArtifactRules.ShortHash(ContentHash);
}

public sealed record GraphQlSchemaArtifactUpload
{
    public string EnvironmentId { get; init; } = "";
    public string TargetId { get; init; } = "";
    public string TargetUrl { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Content { get; init; } = "";
    public string? Notes { get; init; }
}

/// <summary>What a review run used (or had available): captured into the result so a historical run never reads the current artifact.</summary>
public sealed record GraphQlSchemaArtifactSnapshot
{
    public string ArtifactId { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ContentHash { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>True when this run validated against the artifact; false when runtime introspection won and the artifact was only the fallback.</summary>
    public bool UsedForCompatibility { get; init; }
    [JsonIgnore] public string ShortHash => GraphQlSchemaArtifactRules.ShortHash(ContentHash);
}

public static class GraphQlSchemaArtifactRules
{
    public static readonly string[] AllowedExtensions = [".graphql", ".graphqls", ".gql"];

    /// <summary>10 MB: the same cap the review already applies when reading a remote schema artifact. Real SDL files are far below it.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    public static string ShortHash(string? hash) => string.IsNullOrEmpty(hash) ? "" : hash[..Math.Min(12, hash.Length)];

    public static bool HasAllowedExtension(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) && AllowedExtensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}
