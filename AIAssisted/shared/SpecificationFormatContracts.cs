using System.Text.Json.Serialization;

namespace BirkNext.Specifications;

/// <summary>
/// The specification format of one application (System Settings → Spesifikasjonsformat, 2026-11-27). Chosen explicitly per application; the
/// default is <see cref="SpecKit"/> (the existing route, unchanged). The format decides how FUTURE imports of the application's specifications
/// are processed; it never reinterprets a catalog already captured (each catalog records the route that produced it).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SpecificationFormat
{
    SpecKit = 0,
    OpenSpec = 1,
}

/// <summary>The route marker a captured specification catalog carries in its fingerprint. Immutable: set when the catalog is written.</summary>
public static class SpecificationFormatMarker
{
    /// <summary>Fingerprint prefix of a catalog produced by the OpenSpec route ("os1-" + 60 hex). Spec Kit fingerprints are plain 64-hex SHA-256.</summary>
    public const string OpenSpecPrefix = "os1-";

    public static bool IsOpenSpec(string? catalogFingerprint) => catalogFingerprint is not null && catalogFingerprint.StartsWith(OpenSpecPrefix, StringComparison.Ordinal);

    public static SpecificationFormat FormatOf(string? catalogFingerprint) => IsOpenSpec(catalogFingerprint) ? SpecificationFormat.OpenSpec : SpecificationFormat.SpecKit;

    public static string Label(SpecificationFormat format) => format switch
    {
        SpecificationFormat.OpenSpec => "OpenSpec",
        _ => "Spec Kit",
    };

    /// <summary>Prefix of every identity the OpenSpec route generates. These are BirkNext keys, never identifiers written in the source.</summary>
    public const string OpenSpecKeyPrefix = "os:";

    /// <summary>True for a requirement/scenario key the OpenSpec route generated (not an id authored in the specification).</summary>
    public static bool IsGeneratedOpenSpecKey(string? id) => id is not null && id.StartsWith(OpenSpecKeyPrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One application's configured format, as System Settings shows it.</summary>
public sealed record SpecificationFormatApplication
{
    public string ApplicationKey { get; init; } = "";
    public string ApplicationName { get; init; } = "";
    public SpecificationFormat Format { get; init; }
    /// <summary>True when the format was never chosen explicitly (the default <see cref="SpecificationFormat.SpecKit"/> applies).</summary>
    public bool IsDefault { get; init; } = true;
    public DateTimeOffset? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }
    public int Imports { get; init; }
    public DateTimeOffset? LatestImportedAt { get; init; }
    /// <summary>The project keys («import:…») of the application's imports, newest first (for the read-only OpenSpec views).</summary>
    public List<string> Projects { get; init; } = [];
}

public sealed record SpecificationFormatSettingsView
{
    public List<SpecificationFormatApplication> Applications { get; init; } = [];
    public bool CanWrite { get; init; }
    public SpecificationFormat DefaultFormat { get; init; } = SpecificationFormat.SpecKit;
}

/// <summary>What changing an application's format affects, shown before saving.</summary>
public sealed record SpecificationFormatImpact
{
    public string ApplicationKey { get; init; } = "";
    public string ApplicationName { get; init; } = "";
    public SpecificationFormat Current { get; init; }
    public SpecificationFormat Requested { get; init; }
    public bool Changes { get; init; }
    /// <summary>The application already has imports (snapshots/specification catalogs): a confirmation is required to change the format.</summary>
    public bool HasImportedData { get; init; }
    public int Imports { get; init; }
    public int SpecificationCatalogs { get; init; }
    public int SpecKitCatalogs { get; init; }
    public int OpenSpecCatalogs { get; init; }
    public int TestPlans { get; init; }
    public int TestCases { get; init; }
    /// <summary>Plain-language consequences (Norwegian), in order.</summary>
    public List<string> Consequences { get; init; } = [];
    public bool RequiresConfirmation { get; init; }
}

public sealed record SaveSpecificationFormatRequest
{
    public string ApplicationKey { get; init; } = "";
    public SpecificationFormat Format { get; init; }
    /// <summary>The user confirmed the impact preview (required when the application already has imported data).</summary>
    public bool ConfirmImpact { get; init; }
}

/// <summary>How well each BirkNext module is verified for a format.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FormatConsumerSupport { Supported, PreviewOnly, NotVerified, NotSupported }

public sealed record FormatConsumerStatus(string Module, FormatConsumerSupport SpecKit, FormatConsumerSupport OpenSpec, string Basis);

// ── OpenSpec source status (read model for Testoversikt, System Settings and Data Quality) ───────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OpenSpecLifecycleState { CurrentBaseline, ProposedChange, ArchivedChange }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OpenSpecDeltaOperation { Added, Modified, Removed, Renamed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OpenSpecDiagnosticSeverity { Info, Warning, Error }

public sealed record OpenSpecDiagnosticView(string Code, OpenSpecDiagnosticSeverity Severity, string Message, string? Path, int? Line, string? ChangeId = null);

/// <summary>One delta operation of a change, with its resolution against the current baseline.</summary>
public sealed record OpenSpecDeltaView
{
    public OpenSpecDeltaOperation Operation { get; init; }
    public string Capability { get; init; } = "";
    public string RequirementName { get; init; } = "";
    public string? RenamedTo { get; init; }
    /// <summary>The current-baseline requirement key this operation targets, when deterministic and unambiguous; null otherwise.</summary>
    public string? BaselineRequirementKey { get; init; }
    /// <summary>"Resolved", "NewRequirement", "Unresolved", "Conflict", "TargetExists".</summary>
    public string Resolution { get; init; } = "";
    public int Scenarios { get; init; }
    public string Path { get; init; } = "";
    public int? Line { get; init; }
}

public sealed record OpenSpecChangeView
{
    public string ChangeKey { get; init; } = "";
    public string ChangeId { get; init; } = "";
    public OpenSpecLifecycleState State { get; init; }
    public string? Title { get; init; }
    public List<string> Documents { get; init; } = [];
    public List<OpenSpecDeltaView> Deltas { get; init; } = [];
}

public sealed record OpenSpecCapabilityView(string Capability, string Path, int Requirements, int Scenarios, int RequirementsWithoutScenarios);

/// <summary>Document inventory of one import as the OpenSpec route classified it (every Markdown/config file is in exactly one bucket).</summary>
public sealed record OpenSpecDocumentInventory
{
    public int Discovered { get; init; }
    public int CurrentSpecifications { get; init; }
    public int ActiveChangeDocuments { get; init; }
    public int ActiveDeltaSpecifications { get; init; }
    public int ArchivedChangeDocuments { get; init; }
    public int SupportingDocuments { get; init; }
    public int Configuration { get; init; }
    public int NotOpenSpec { get; init; }
    public int Unsupported { get; init; }
}

public sealed record OpenSpecSourceStatus
{
    public string ProjectKey { get; init; } = "";
    /// <summary>The format of the project's current catalog (the route that produced it).</summary>
    public SpecificationFormat CatalogFormat { get; init; }
    /// <summary>The application's configured format now (may differ: a switch affects future imports only).</summary>
    public SpecificationFormat? ConfiguredFormat { get; init; }
    public string? ApplicationKey { get; init; }
    public int? CatalogRevision { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    /// <summary>True when the OpenSpec source model of the catalog is available (false = NOT VERIFIED: only the catalog is held).</summary>
    public bool ModelAvailable { get; init; }
    public string? SchemaName { get; init; }
    /// <summary>"BuiltIn" (no config / spec-driven), "Unknown" (a custom schema BirkNext does not verify).</summary>
    public string? SchemaState { get; init; }
    public OpenSpecDocumentInventory? Inventory { get; init; }
    public int CurrentRequirements { get; init; }
    public int CurrentScenarios { get; init; }
    public int CurrentRequirementsWithoutScenarios { get; init; }
    public List<OpenSpecCapabilityView> Capabilities { get; init; } = [];
    public List<OpenSpecChangeView> ActiveChanges { get; init; } = [];
    public List<OpenSpecChangeView> ArchivedChanges { get; init; } = [];
    public List<OpenSpecDiagnosticView> Diagnostics { get; init; } = [];
    /// <summary>The last OpenSpec route outcome of this project when it produced no catalog (e.g. no openspec/ folder): shown, never hidden.</summary>
    public string? RouteProblem { get; init; }
    public List<string> Notes { get; init; } = [];
}

// ── OpenSpec source changes (Phase 11) ──────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OpenSpecChangeKind { NewRequirement, ModifiedRequirement, RemovedRequirement, RenamedRequirement, NewScenario, ModifiedScenario, RemovedScenario }

public sealed record OpenSpecAffectedCase(Guid PlanId, string PlanName, string CaseId, string Title, string Evidence);

public sealed record OpenSpecSourceChange
{
    public OpenSpecChangeKind Kind { get; init; }
    public string Capability { get; init; } = "";
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string? PreviousKey { get; init; }
    public string Evidence { get; init; } = "";
    /// <summary>True for a proposal's prospective impact (not in the baseline); false for an accepted baseline change.</summary>
    public bool Prospective { get; init; }
    public string? ChangeId { get; init; }
    public List<OpenSpecAffectedCase> AffectedCases { get; init; } = [];
}

public sealed record OpenSpecSourceChangeReport
{
    public string ProjectKey { get; init; } = "";
    public int? PreviousRevision { get; init; }
    public int? CurrentRevision { get; init; }
    /// <summary>Accepted baseline changes: previous effective baseline → current effective baseline.</summary>
    public List<OpenSpecSourceChange> BaselineChanges { get; init; } = [];
    /// <summary>Prospective impact of active proposals on the current baseline (never applied).</summary>
    public List<OpenSpecSourceChange> ProposedImpact { get; init; } = [];
    public List<string> Notes { get; init; } = [];
}

// ── OpenSpec reconciliation (Data Quality, Phase 13) ───────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReconciliationStatus { Pass, Fail, NotVerified, NotApplicable }

public sealed record ReconciliationCheck(string Id, string Title, ReconciliationStatus Status, string Expected, string Observed, string Detail);

public sealed record OpenSpecReconciliationReport
{
    public string ProjectKey { get; init; } = "";
    public SpecificationFormat CatalogFormat { get; init; }
    public int? CatalogRevision { get; init; }
    public DateTimeOffset ComputedAt { get; init; }
    public ReconciliationStatus Overall { get; init; }
    public List<ReconciliationCheck> Checks { get; init; } = [];
    public List<string> Notes { get; init; } = [];
}
