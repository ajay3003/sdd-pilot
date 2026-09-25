namespace BirkNext.Api.Models;

// Integration catalog persistence. Scalar columns for what is queried on; a JSON document for the typed record
// (IntegrationPlatform / IntegrationDefinition from shared/IntegrationCatalogContracts.cs). No column ever holds a secret.

/// <summary>One configured messaging platform of a Target Environment. Key: (EnvironmentId, Id).</summary>
public class IntegrationPlatformRecord
{
    public string EnvironmentId { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DocumentJson { get; set; } = string.Empty;
    public bool UserModified { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One configured integration of a Target Environment. Key: (EnvironmentId, Id).</summary>
public class IntegrationDefinitionRecord
{
    public string EnvironmentId { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string? PlatformId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string DocumentJson { get; set; } = string.Empty;
    public bool UserModified { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Per-environment catalog bookkeeping: which seed version was attached and whether browser-stored integrations were imported.
/// Seeding adds missing records only; it never overwrites an existing (possibly user-edited) record.
/// </summary>
public class IntegrationEnvironmentStateRecord
{
    public string EnvironmentId { get; set; } = string.Empty;
    public int SeedVersion { get; set; }
    public string? SeedName { get; set; }
    public DateTimeOffset? LegacyImportedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One Integration Quality Review run, immutable once written. ResultJson holds the full result including its configuration snapshot.</summary>
public class IntegrationReviewRunRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EnvironmentId { get; set; } = string.Empty;
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Outcome { get; set; } = string.Empty;
    public string ResultJson { get; set; } = string.Empty;
}

/// <summary>A trusted event contract (JSON Schema) for one side of one integration. Key: (EnvironmentId, IntegrationId, Role).</summary>
public class IntegrationContractArtifactRecord
{
    public string EnvironmentId { get; set; } = string.Empty;
    public string IntegrationId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string DocumentJson { get; set; } = string.Empty;
    public DateTimeOffset ImportedAt { get; set; } = DateTimeOffset.UtcNow;
}
