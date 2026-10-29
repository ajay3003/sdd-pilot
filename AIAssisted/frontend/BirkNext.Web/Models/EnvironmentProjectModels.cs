using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

/// <summary>What a piece of imported project evidence can provide. A package can provide several; none is required of every package.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectEvidenceCapability
{
    /// <summary>Application or service source with a Source Analysis snapshot.</summary>
    ApplicationSource,
    /// <summary>Authored project documents (Constitution, Specification, Plan, Tasks, Data Model, Research).</summary>
    Documentation,
    /// <summary>Contract or schema evidence (XSD, OpenAPI, GraphQL …) found by Source Analysis.</summary>
    ContractSchema,
    /// <summary>Infrastructure as Code (Terraform …) found by Source Analysis.</summary>
    Infrastructure,
    /// <summary>Production code without a web/service host project: a shared library or utility package.</summary>
    SupportingSource,
    /// <summary>Generated (implementation-derived) documentation captured with an import.</summary>
    GeneratedDocumentation,
    /// <summary>Source of another system the project integrates with (a Test Pack logical source).</summary>
    IntegrationSource,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectEvidenceRequirement { Required, Optional }

/// <summary>Single: one source satisfies the role (application source). Multiple: several sources aggregate (documentation, supporting sources).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectEvidenceCardinality { Single, Multiple }

/// <summary>Where an evidence role comes from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectEvidenceRoleOrigin { ProjectDefinition, TestPack, UserAdded, Expected }

/// <summary>
/// The logical project a Target Environment tests, and which already imported packages provide its evidence. Owned by the Target Environment;
/// it stores references only — imported documents stay in the artifact repository, source evidence in Source Analysis snapshots.
/// </summary>
public sealed class EnvironmentProject
{
    /// <summary>The Project Import (canonical import id) that is the main application/system. A project is a logical system, not this ZIP.</summary>
    [JsonPropertyName("projectImportId")] public string? ProjectImportId { get; set; }
    [JsonPropertyName("projectName")] public string ProjectName { get; set; } = "";
    /// <summary>The main import's archive fingerprint: a portable reference that survives another workspace (ids are workspace-local).</summary>
    [JsonPropertyName("archiveSha256")] public string? ArchiveSha256 { get; set; }
    [JsonPropertyName("selectedAt")] public DateTimeOffset SelectedAt { get; set; }
    [JsonPropertyName("evidence")] public List<EnvironmentProjectEvidenceBinding> Evidence { get; set; } = [];
    /// <summary>Test Packs whose logical source roles the user activated for this project (by pack id). Roles of other packs are not required.</summary>
    [JsonPropertyName("testPacks")] public List<string> TestPacks { get; set; } = [];
    /// <summary>Optional description of the logical project (a manually created project may have no package yet).</summary>
    [JsonPropertyName("description")] public string? Description { get; set; }
    /// <summary>The project was created by name in Project setup, not from a detected application package.</summary>
    [JsonPropertyName("manual")] public bool Manual { get; set; }
    /// <summary>Suggested evidence the user deselected (role + import): later detection never reselects it silently.</summary>
    [JsonPropertyName("declined")] public List<ProjectEvidenceDecline> Declined { get; set; } = [];
}

/// <summary>An explicit "not this package for this role" decision from the Project evidence checklist.</summary>
public sealed class ProjectEvidenceDecline
{
    [JsonPropertyName("roleKey")] public string RoleKey { get; set; } = "";
    [JsonPropertyName("importId")] public string ImportId { get; set; } = "";
    [JsonPropertyName("at")] public DateTimeOffset At { get; set; }
}

/// <summary>One evidence role of the project and the imported sources bound to it (references only, never content).</summary>
public sealed class EnvironmentProjectEvidenceBinding
{
    [JsonPropertyName("roleKey")] public string RoleKey { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("capability")] public ProjectEvidenceCapability Capability { get; set; }
    [JsonPropertyName("requirement")] public ProjectEvidenceRequirement Requirement { get; set; }
    [JsonPropertyName("cardinality")] public ProjectEvidenceCardinality Cardinality { get; set; }
    [JsonPropertyName("origin")] public ProjectEvidenceRoleOrigin Origin { get; set; }
    /// <summary>"Recommended Workflow document reviews", "Skolenærvær Testing Test Pack", "Added in Project setup" …</summary>
    [JsonPropertyName("requiredBy")] public string RequiredBy { get; set; } = "";
    [JsonPropertyName("sources")] public List<ProjectEvidenceSourceReference> Sources { get; set; } = [];
}

/// <summary>
/// A reference to imported evidence. Documentation needs only the import identity; source evidence also names the exact immutable Source
/// Analysis snapshot. Nothing here is a copy of the evidence.
/// </summary>
public sealed class ProjectEvidenceSourceReference
{
    [JsonPropertyName("importId")] public string? ImportId { get; set; }
    [JsonPropertyName("archiveFileName")] public string ArchiveFileName { get; set; } = "";
    [JsonPropertyName("archiveSha256")] public string? ArchiveSha256 { get; set; }
    /// <summary>Exact snapshot for source-backed capabilities; null for documentation-only evidence (never a fake snapshot).</summary>
    [JsonPropertyName("snapshotId")] public Guid? SnapshotId { get; set; }
    [JsonPropertyName("boundAt")] public DateTimeOffset BoundAt { get; set; }
    /// <summary>"User" (chosen in Project setup) or "ProjectSelection" (the main project's own evidence, when the project was selected).</summary>
    [JsonPropertyName("boundBy")] public string BoundBy { get; set; } = "User";
}
