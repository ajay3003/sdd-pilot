using BirkNext.Specifications;

namespace BirkNext.Api.Services.Specifications.OpenSpec;

/// <summary>Where a document of an OpenSpec import belongs. Every Markdown/config document of the import is in exactly one bucket.</summary>
public enum OpenSpecDocumentKind
{
    /// <summary>openspec/specs/&lt;capability&gt;/spec.md — the current baseline.</summary>
    CurrentSpecification,
    /// <summary>openspec/changes/&lt;id&gt;/{proposal,design,tasks}.md — an active change's own documents.</summary>
    ActiveChangeDocument,
    /// <summary>openspec/changes/&lt;id&gt;/specs/&lt;capability&gt;/spec.md — an active change's delta specification.</summary>
    ActiveDeltaSpecification,
    /// <summary>Anything under openspec/changes/archive/&lt;dated-id&gt;/ — history only.</summary>
    ArchivedChangeDocument,
    /// <summary>openspec/changes/archive/&lt;dated-id&gt;/specs/&lt;capability&gt;/spec.md — an archived delta (history only, never reapplied).</summary>
    ArchivedDeltaSpecification,
    /// <summary>Other Markdown inside the openspec/ tree (project.md, AGENTS.md, notes in a capability folder …): context, not requirements.</summary>
    SupportingDocument,
    /// <summary>openspec/config.yaml|yml — the declared schema.</summary>
    Configuration,
    /// <summary>A document outside every openspec/ folder (README.md, docs/ …): not OpenSpec source.</summary>
    NotOpenSpec,
    /// <summary>Inside openspec/ but not a structure BirkNext supports (custom schema files, unsafe/duplicate path, over the limits …).</summary>
    Unsupported,
}

public sealed record OpenSpecDocument(string Path, OpenSpecDocumentKind Kind, string Sha256, int Bytes, string? Root, string? Capability, string? ChangeId,
    string? Reason = null);

/// <summary>One scenario as written (<c>#### Scenario: Name</c>), with its steps exactly as the source states them.</summary>
public sealed record OpenSpecScenario
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Given { get; init; }
    public string? When { get; init; }
    public string? Then { get; init; }
    /// <summary>The step lines as written (keyword + text), in order.</summary>
    public List<string> Steps { get; init; } = [];
    public int Line { get; init; }
    public string TextSha256 { get; init; } = "";
}

/// <summary>One requirement as written (<c>### Requirement: Name</c>). No source identifier is assumed: <see cref="Key"/> is a BirkNext key.</summary>
public sealed record OpenSpecRequirement
{
    public string Key { get; init; } = "";
    public string Capability { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>The requirement body (normative text) as written, whitespace-collapsed; empty when the source states none.</summary>
    public string Statement { get; init; } = "";
    public bool HasNormativeKeyword { get; init; }
    public List<OpenSpecScenario> Scenarios { get; init; } = [];
    public string Path { get; init; } = "";
    public int Line { get; init; }
    public int EndLine { get; init; }
    /// <summary>SHA-256 of the requirement's wording (statement + scenarios): its content revision, separate from its logical identity.</summary>
    public string TextSha256 { get; init; } = "";
}

public sealed record OpenSpecCapability
{
    public string Key { get; init; } = "";
    /// <summary>The capability folder as written below specs/ ("auth", "billing/invoices").</summary>
    public string Folder { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Title { get; init; }
    public string ContentSha256 { get; init; } = "";
    public List<OpenSpecRequirement> Requirements { get; init; } = [];
}

public sealed record OpenSpecDelta
{
    public OpenSpecDeltaOperation Operation { get; init; }
    public string Capability { get; init; } = "";
    public string RequirementName { get; init; } = "";
    public string? RenamedTo { get; init; }
    /// <summary>The full requirement block of an ADDED / MODIFIED operation (null for REMOVED / RENAMED).</summary>
    public OpenSpecRequirement? Content { get; init; }
    public string? Reason { get; init; }
    public string Path { get; init; } = "";
    public int Line { get; init; }
    public string? BaselineRequirementKey { get; init; }
    public string Resolution { get; init; } = "";
}

public sealed record OpenSpecChange
{
    public string Key { get; init; } = "";
    public string ChangeId { get; init; } = "";
    public OpenSpecLifecycleState State { get; init; }
    public string? Title { get; init; }
    public List<string> Documents { get; init; } = [];
    public List<OpenSpecDelta> Deltas { get; init; } = [];
}

/// <summary>
/// Everything the OpenSpec route read from one import: the document inventory, the current baseline (capabilities → requirements → scenarios),
/// active and archived changes with their delta operations, diagnostics, and the original Markdown of the OpenSpec documents (bounded). Stored beside
/// the specification catalog it produced; never applied to the baseline.
/// </summary>
public sealed record OpenSpecSourceModel
{
    public const string CurrentVersion = "openspec-route/1";
    public string Version { get; init; } = CurrentVersion;
    public string ProjectKey { get; init; } = "";
    public string? ApplicationKey { get; init; }
    public string? ImportId { get; init; }
    public string? ArchiveSha256 { get; init; }
    public Guid? SnapshotId { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public string? SchemaName { get; init; }
    /// <summary>"BuiltIn" (no config, or the built-in spec-driven schema) or "Unknown" (a custom schema BirkNext does not verify).</summary>
    public string SchemaState { get; init; } = "BuiltIn";
    public List<string> Roots { get; init; } = [];
    public List<OpenSpecDocument> Documents { get; init; } = [];
    public List<OpenSpecCapability> Capabilities { get; init; } = [];
    public List<OpenSpecChange> ActiveChanges { get; init; } = [];
    public List<OpenSpecChange> ArchivedChanges { get; init; } = [];
    public List<OpenSpecDiagnosticView> Diagnostics { get; init; } = [];
    /// <summary>Requirement/scenario headings of the current baseline that were not taken (each also has a diagnostic), per document — so a
    /// reconciliation can account for every heading.</summary>
    public List<OpenSpecExclusion> Exclusions { get; init; } = [];
    /// <summary>Original Markdown of the OpenSpec documents (current, change, archive, supporting) keyed by path; bounded by <see cref="OpenSpecLimits"/>.</summary>
    public Dictionary<string, string> OriginalMarkdown { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Documents whose original Markdown was not kept (over the size budget); their hash and parse result are kept.</summary>
    public List<string> OriginalMarkdownOmitted { get; init; } = [];

    public IEnumerable<OpenSpecRequirement> CurrentRequirements => Capabilities.SelectMany(c => c.Requirements);
}

public sealed record OpenSpecExclusion(string Path, int Requirements, int Scenarios, string Reason);

/// <summary>Resource limits of the OpenSpec route (untrusted input). Exceeding one is a reported diagnostic, never a silent truncation.</summary>
public static class OpenSpecLimits
{
    public const int MaxDocuments = 5_000;
    public const int MaxDocumentChars = 2 * 1024 * 1024;
    public const int MaxLineChars = 20_000;
    public const int MaxRequirements = SpecificationCatalogService.MaxRequirements;
    public const int MaxScenarios = 50_000;
    public const int MaxStepsPerScenario = 200;
    public const long MaxOriginalMarkdownChars = 24L * 1024 * 1024;
    public const int MaxDiagnostics = 2_000;
}
