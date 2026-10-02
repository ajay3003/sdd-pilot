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
/// <summary>One immutable security-classification row: a source analysis, a test context or a run. Ids, labels and derived facts only — no PII, token or payload.</summary>
public class SecurityClassificationEvidenceRecord
{
    public Guid Id { get; set; }
    public string EnvironmentId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string Json { get; set; } = string.Empty;
}

/// <summary>One immutable SCIM evidence row: a source analysis (kind "source") or a safe check (kind "check"). Facts only — never source, tokens or user data.</summary>
public class ScimEvidenceRecord
{
    public Guid Id { get; set; }
    public string EnvironmentId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? PlatformId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Json { get; set; } = string.Empty;
}

/// <summary>One immutable dependency-review run (facts, hashes, redacted normalized config, simulations — never source files or secrets).</summary>
public class DependencyReviewRunRecord
{
    public Guid Id { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string Label { get; set; } = string.Empty;
    public string ResultJson { get; set; } = string.Empty;
}

/// <summary>A stored dependency inventory snapshot (SBOM, lock file or deployed evidence) — dependency facts only, never document secrets.</summary>
public class DependencyInventoryRecord
{
    public Guid Id { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = string.Empty;
}

/// <summary>One immutable dependency-health run: the inventory copy plus every registry/advisory/license/automation observation as retrieved.</summary>
public class DependencyHealthRunRecord
{
    public Guid Id { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string Label { get; set; } = string.Empty;
    public Guid InventoryId { get; set; }
    public string ResultJson { get; set; } = string.Empty;
}

/// <summary>Application messaging evidence set of an environment (facts extracted from uploaded source; never the source itself).</summary>
public class ApplicationMessagingEvidenceRecord
{
    public string EnvironmentId { get; set; } = string.Empty;
    public DateTimeOffset AnalyzedAt { get; set; } = DateTimeOffset.UtcNow;
    public string EvidenceJson { get; set; } = string.Empty;
}

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

/// <summary>Safe documented message-flow and test-readiness configuration per Target Environment. JSON contract deliberately has no secret or payload fields.</summary>
public class IntegrationMessageFlowRecord
{
    public string EnvironmentId { get; set; } = string.Empty;
    public string DocumentJson { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
