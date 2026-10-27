using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

/// <summary>The external system a Target Environment talks to. A new provider adds a value, a handler and (optionally) a UI component.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExternalProviderType { Altinn, RestApi, GraphQl, AzureServiceBus, AzureEventHub, Database, FileSftp, GenericHttp, Other }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProviderFieldKind { Text, Url, List, Boolean, Choice }

/// <summary>
/// One provider setting as the UI renders it and the package schema accepts it. A secret field is never stored or exported: only its
/// availability is shown. Non-portable fields stay with the environment (they are re-detected or re-entered at the destination).
/// </summary>
public sealed record ProviderFieldDefinition(string Key, string Label, string Group, ProviderFieldKind Kind, bool Required, bool Portable,
    string Help = "", IReadOnlyList<string>? Choices = null, bool Secret = false);

/// <summary>A provider: its field definitions, the capabilities an instance can satisfy and what a test pack may require of it.</summary>
public sealed record ExternalProviderDescriptor(ExternalProviderType Type, string DisplayName, string Description, IReadOnlyList<ProviderFieldDefinition> Fields,
    IReadOnlyList<string> Capabilities, int MaxPerEnvironment);

/// <summary>
/// An environment-specific external system instance (e.g. "Altinn – Skolenærvær" in TT02). Provider settings are a validated key/value
/// document described by the provider's field definitions (Altinn keeps its typed settings record); never a secret. Provenance names where
/// each value came from (configured manually, detected, applied from a setup package).
/// </summary>
public sealed record ExternalIntegration
{
    public string Id { get; init; } = "";
    public string TargetEnvironmentId { get; init; } = "";
    public string Name { get; init; } = "";
    public ExternalProviderType ProviderType { get; init; }
    public string? EnvironmentName { get; init; }
    public bool Enabled { get; init; } = true;
    public Dictionary<string, string> Settings { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Provenance { get; init; } = new(StringComparer.Ordinal);
    public List<string> EvidenceReferences { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupReadinessState { Ready, Partial, Blocked, NotAssessed, Observed }

public sealed record SetupReadinessItem(string Key, string Label, SetupReadinessState State, string Reason);

/// <summary>The same five dimensions for every provider; a provider may add detail rows.</summary>
public sealed record ExternalIntegrationSummary(SetupReadinessItem Configuration, SetupReadinessItem Dependencies, SetupReadinessItem Authentication,
    SetupReadinessItem Runtime, SetupReadinessItem Execution);

/// <summary>An integration with its provider's resolved fields (state + provenance), readiness and dependency resolution. No secret value.</summary>
public sealed record ExternalIntegrationView
{
    public ExternalIntegration Integration { get; init; } = new();
    public IReadOnlyList<AltinnSettingField> Fields { get; init; } = [];
    public ExternalIntegrationSummary Summary { get; init; } = new(Empty("configuration"), Empty("dependencies"), Empty("authentication"), Empty("runtime"), Empty("execution"));
    /// <summary>The provider's own summary cards for its detail view (e.g. REST API: Configuration, Authentication, Contract, Runtime,
    /// Execution). Empty means the five <see cref="Summary"/> dimensions.</summary>
    public IReadOnlyList<SetupReadinessItem> Dimensions { get; init; } = [];
    public IReadOnlyList<SetupDependencyResolution> Dependencies { get; init; } = [];
    public AltinnDetectionSummary Detection { get; init; } = new(0, 0, 0, 0, 0, []);
    public bool Detected { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    public static SetupReadinessItem Empty(string key) => new(key, key, SetupReadinessState.NotAssessed, "");
}

/// <summary>An integration BirkNext can propose from deterministic evidence (test pack + IQR + Source Analysis); accepted only explicitly.</summary>
public sealed record ExternalIntegrationProposal(string Name, ExternalProviderType ProviderType, string? EnvironmentName, string DetectedFrom, string Reason,
    Dictionary<string, string> Settings, bool Ambiguous);

public sealed record ExternalIntegrationsView(string EnvironmentId, IReadOnlyList<ExternalIntegrationView> Integrations, IReadOnlyList<ExternalIntegrationProposal> Proposals,
    IReadOnlyList<ExternalProviderDescriptor> Providers);

/// <summary>Create or update an integration. Settings keys must be the provider's non-secret fields.</summary>
public sealed record ExternalIntegrationRequest(string Name, ExternalProviderType ProviderType, string? EnvironmentName, bool Enabled, Dictionary<string, string>? Settings);

// ── Dependencies and preflight ──

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupDependencyKind { ExternalIntegration, ProviderCapability, Integration, Contract, SourceSnapshot, TestData, TestPack, EnvironmentCapability }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupDependencyState { Resolved, Missing, Ambiguous, Incompatible, NotApproved, OptionalMissing }

/// <summary>A dependency a package needs at its destination. References and roles only — never a value, secret or identifier.</summary>
public sealed record SetupDependency(SetupDependencyKind Kind, string Reference, bool Required, string? Role = null, string? Provider = null);

/// <summary>How a dependency resolved at the destination, with its canonical owner to fix it there.</summary>
public sealed record SetupDependencyResolution(SetupDependency Dependency, SetupDependencyState State, string Detail, string Owner, string OwnerHref);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupScope { Integration, Provider, Environment }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupPackageFormat { Yaml, Json }

/// <summary>One system setting the package would set: new, already the same, or a conflict that needs an explicit keep/apply decision.</summary>
public sealed record SetupSystemChange(string Id, string System, ExternalProviderType Provider, string Setting, string? Current, string? Proposed, string Classification, string Reason);

public sealed record SetupPreflightRequest(string Content, string? FileName = null, SetupScope? Scope = null, ExternalProviderType? Provider = null, string? IntegrationId = null);

/// <summary>Apply decisions for conflicts: change id → true (apply the package value) / false (keep current). Undecided conflicts block Apply.</summary>
public sealed record SetupApplyPackageRequest(string Content, string? FileName = null, SetupScope? Scope = null, ExternalProviderType? Provider = null,
    string? IntegrationId = null, Dictionary<string, bool>? ConflictDecisions = null, Guid? LibraryVersionId = null);

/// <summary>The complete, side-effect-free change plan of a package for one destination. Apply is possible only when nothing required is unresolved.</summary>
public sealed record SetupPreflightResult
{
    public bool Valid { get; init; }
    public string? PackageName { get; init; }
    public string? PackageVersion { get; init; }
    public int? SchemaVersion { get; init; }
    public SetupScope? DetectedScope { get; init; }
    public IReadOnlyList<ExternalProviderType> DetectedProviders { get; init; } = [];
    public IReadOnlyList<SetupProfileIssue> Issues { get; init; } = [];
    public IReadOnlyList<SetupSystemChange> Changes { get; init; } = [];
    public IReadOnlyList<SetupDependencyResolution> Dependencies { get; init; } = [];
    public IReadOnlyList<string> BlockReasons { get; init; } = [];
    public bool CanApply => Valid && BlockReasons.Count == 0;
    public int RequiredResolved => Dependencies.Count(d => d.Dependency.Required && d.State == SetupDependencyState.Resolved);
    public int RequiredTotal => Dependencies.Count(d => d.Dependency.Required);
    public int OptionalMissing => Dependencies.Count(d => d.State == SetupDependencyState.OptionalMissing);
    public int Conflicts => Changes.Count(c => c.Classification == "Conflict");
    public int SecretsImported => 0;
    public int IdentitiesImported => 0;
    public int RuntimeEvidenceImported => 0;
}

public sealed record SetupApplyPackageResult(bool Applied, string Outcome, IReadOnlyList<string> AppliedChanges, SetupPreflightResult Preflight);

public sealed record SetupPackageExport(SetupPackageFormat Format, string FileName, string Content, IReadOnlyList<string> Excluded, IReadOnlyList<SetupDependency> Dependencies);

// ── Setup Library ──

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupLibraryStatus { Draft, Ready, NeedsReview, Archived }

public sealed record SetupLibraryVersionSummary(Guid Id, int VersionNumber, int SchemaVersion, string? ChangeSummary, DateTimeOffset CreatedAt,
    IReadOnlyList<SetupDependency> Dependencies, IReadOnlyList<ExternalProviderType> Providers);

public sealed record SetupLibraryItemView
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public SetupScope SetupType { get; init; }
    public string? EnvironmentType { get; init; }
    public string? SourceEnvironmentId { get; init; }
    public IReadOnlyList<ExternalProviderType> Providers { get; init; } = [];
    public IReadOnlyList<string> TestPacks { get; init; } = [];
    public SetupLibraryStatus Status { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public IReadOnlyList<SetupLibraryVersionSummary> Versions { get; init; } = [];
    public string? LastPreflight { get; init; }
    public IReadOnlyList<string> Sanitization { get; init; } = [];
}

/// <summary>Save to the library: from the current environment (scope/provider/integration) or from package content already previewed.</summary>
public sealed record SetupLibrarySaveRequest(string Name, string? Description, string? EnvironmentId = null, SetupScope Scope = SetupScope.Environment,
    ExternalProviderType? Provider = null, string? IntegrationId = null, string? Content = null, string? ChangeSummary = null, IReadOnlyList<string>? TestPacks = null);

public sealed record SetupVersionDifference(string Area, string Item, string? Before, string? After);
