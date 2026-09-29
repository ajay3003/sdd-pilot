using System.Text.Json.Serialization;

namespace BirkNext.Dependencies;

// Dependency health / supply-chain review over a dependency INVENTORY, so it runs without uploading source. An inventory is a snapshot with
// provenance and a capture time; it may come from a stored source review, an SBOM, a NuGet lock file or deployed evidence. Every observation
// keeps its source and retrieval time. Semantics kept apart on purpose:
//   declared ≠ resolved ≠ packaged ≠ deployed ≠ runtime-loaded; latest published stable ≠ recommended; outdated ≠ vulnerable;
//   no matched advisory ≠ safe; advisory source unavailable ≠ 0 vulnerabilities; registry unavailable ≠ package missing;
//   license detected ≠ approved; Renovate configured ≠ Renovate running; unknown ≠ none; not assessed ≠ passed.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventorySourceType { SourceReview, Sbom, LockFile, Deployment }

/// <summary>Where in the lifecycle a dependency was observed. Nothing in BirkNext currently proves RuntimeObserved.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventoryStage { Declared, Resolved, Packaged, Deployed, RuntimeObserved }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DependencyRelationship { Direct, Transitive, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InventoryFreshness { Current, Stale, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SbomFormat { CycloneDxJson, CycloneDxXml, SpdxJson, NuGetLockFile, Unknown }

/// <summary>What an uploaded SBOM describes: a build artifact (packaged) or what is deployed to an environment.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SbomRole { BuildArtifact, DeployedArtifact }

public sealed record DependencyHash(string Algorithm, string Value);

public sealed record InventoryDependency
{
    public string PackageName { get; init; } = "";
    /// <summary>An exact version; null = Unknown (not declared, a range, or not provided by the source).</summary>
    public string? Version { get; init; }
    /// <summary>The declared range or floating value when the source declares one instead of an exact version.</summary>
    public string? VersionRange { get; init; }
    /// <summary>nuget, npm, docker, assembly … (from the source manager or the purl type).</summary>
    public string PackageManager { get; init; } = "";
    public string? Datasource { get; init; }
    public DependencyRelationship Relationship { get; init; } = DependencyRelationship.Unknown;
    public InventoryStage Stage { get; init; }
    /// <summary>Always null today: no evidence source proves a component was loaded at runtime (an SBOM entry never does).</summary>
    public bool? RuntimeObserved { get; init; }
    /// <summary>File/artifact position the item came from (e.g. Directory.Packages.props:12, SBOM component bom-ref).</summary>
    public string? Location { get; init; }
    public string? Repository { get; init; }
    /// <summary>License identifiers/expressions exactly as the inventory source states them (empty = the source declares none).</summary>
    public List<string> Licenses { get; init; } = [];
    /// <summary>True when the inventory source has a license field at all (an SBOM does, a source manifest does not).</summary>
    public bool LicenseFieldPresent { get; init; }
    public string? Purl { get; init; }
    public string? Cpe { get; init; }
    public List<DependencyHash> Hashes { get; init; } = [];
    /// <summary>Package source/registry when the inventory states it (purl repository_url, lock-file source).</summary>
    public string? Registry { get; init; }
    public string? Publisher { get; init; }
}

public sealed record DependencyInventorySnapshot
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public InventorySourceType SourceType { get; init; }
    /// <summary>Human name of the source: repository archive, SBOM file, target URL.</summary>
    public string SourceName { get; init; } = "";
    public InventoryStage Stage { get; init; }
    /// <summary>When the evidence was produced (source review time, SBOM metadata timestamp, capture time). Null = unknown.</summary>
    public DateTimeOffset? CapturedAt { get; init; }
    /// <summary>When BirkNext recorded it.</summary>
    public DateTimeOffset RecordedAt { get; init; }
    public string? Environment { get; init; }
    public string? BuildId { get; init; }
    public string? Repository { get; init; }
    public string? Commit { get; init; }
    public string? ArtifactId { get; init; }
    public SbomFormat? Format { get; init; }
    public string? FormatVersion { get; init; }
    /// <summary>One line: where this inventory comes from and how it was produced.</summary>
    public string Provenance { get; init; } = "";
    /// <summary>sha256 of the uploaded document / source archive, or of the normalized dependency list.</summary>
    public string ContentSha256 { get; init; } = "";
    public Guid? SourceRunId { get; init; }
    public List<InventoryDependency> Dependencies { get; init; } = [];
    /// <summary>What this inventory cannot tell (e.g. declared only; versions not proven by a boot manifest).</summary>
    public List<string> Limitations { get; init; } = [];
}

public sealed record InventorySummary
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public InventorySourceType SourceType { get; init; }
    public string SourceName { get; init; } = "";
    public InventoryStage Stage { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
    public string? Environment { get; init; }
    public string? BuildId { get; init; }
    public string? Repository { get; init; }
    /// <summary>The stored source review an inventory was derived from (its Renovate snapshot can be used for the security-fix cross-check).</summary>
    public Guid? SourceRunId { get; init; }
    public int Dependencies { get; init; }
    public InventoryFreshness Freshness { get; init; }
    public string FreshnessDetail { get; init; } = "";
}

public sealed record SbomValidation
{
    public SbomFormat Format { get; init; } = SbomFormat.Unknown;
    public string? SpecVersion { get; init; }
    public bool Valid { get; init; }
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public int Components { get; init; }
    public int Direct { get; init; }
    public int Transitive { get; init; }
    public int UnknownRelationship { get; init; }
    public Dictionary<string, int> Ecosystems { get; init; } = [];
    public int MissingVersions { get; init; }
    public int MissingIdentifiers { get; init; }
    public int MissingLicenses { get; init; }
    public int WithHashes { get; init; }
    public bool HasDependencyGraph { get; init; }
    /// <summary>Document content read but not assessed by BirkNext (services, vulnerabilities section, signatures …).</summary>
    public List<string> NotAssessed { get; init; } = [];
}

public sealed record InventoryImportResult(SbomValidation? Validation, InventorySummary? Inventory, string? Error);

public sealed record DeployedCaptureRequest
{
    public string TargetUrl { get; init; } = "";
    public string? Environment { get; init; }
}

// ── Registry ───────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Registry result for one observed version. Unavailable/Timeout/Unauthorized/RateLimited never mean the package is missing.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RegistryState { Observed, Unlisted, VersionNotFound, PackageNotFound, Unauthorized, RateLimited, Timeout, RegistryUnavailable, ProviderError, NotAssessed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VersionStatus { Current, NewerThanLatestStable, PatchBehind, MinorBehind, MajorBehind, VersionUnavailable, RegistryUnavailable, NotComparable, NotAssessed }

/// <summary>NuGet's deprecation metadata, in NuGet's own terms (reasons Legacy / CriticalBugs / Other).</summary>
public sealed record DeprecationEvidence(List<string> Reasons, string? Message, string? AlternatePackage);

public sealed record RegistryObservation
{
    public string Registry { get; init; } = "";
    public RegistryState State { get; init; } = RegistryState.NotAssessed;
    public string Detail { get; init; } = "";
    /// <summary>Highest listed stable version the registry publishes — never a recommendation.</summary>
    public string? LatestStable { get; init; }
    public string? LatestPrerelease { get; init; }
    public DateTimeOffset? ObservedPublished { get; init; }
    public DateTimeOffset? LatestStablePublished { get; init; }
    public bool? ObservedListed { get; init; }
    public DeprecationEvidence? Deprecation { get; init; }
    public string? LicenseExpression { get; init; }
    public string? LicenseUrl { get; init; }
    public DateTimeOffset? RetrievedAt { get; init; }
    public bool FromCache { get; init; }
}

// ── Security advisories ──────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdvisoryState { Affected, NotAffectedByMatchedAdvisory, NoMatchedAdvisoryObserved, AdvisorySourceUnavailable, VersionNotComparable, NotAssessed }

public sealed record AdvisoryObservation
{
    public string Id { get; init; } = "";
    public List<string> Aliases { get; init; } = [];
    public string? Summary { get; init; }
    /// <summary>Affected ranges as the source states them, e.g. "introduced 0, fixed 1.0.5".</summary>
    public List<string> AffectedRanges { get; init; } = [];
    /// <summary>Severity exactly as the source provides it (e.g. GHSA "HIGH"); null when the source gives none — never computed.</summary>
    public string? SourceSeverity { get; init; }
    public List<string> SeverityVectors { get; init; } = [];
    public List<string> FixedVersions { get; init; } = [];
    /// <summary>True/false = the observed version is/is not inside an affected range; null = not comparable.</summary>
    public bool? AffectsObservedVersion { get; init; }
    public bool Withdrawn { get; init; }
    public string Source { get; init; } = "";
    public string? Url { get; init; }
    public DateTimeOffset? Modified { get; init; }
}

public sealed record SecurityEvidence
{
    public AdvisoryState State { get; init; } = AdvisoryState.NotAssessed;
    public List<AdvisoryObservation> Advisories { get; init; } = [];
    public string Source { get; init; } = "";
    public DateTimeOffset? RetrievedAt { get; init; }
    public string Detail { get; init; } = "";
    /// <summary>Lowest fixed version above the observed one, from an affecting advisory — evidence, not an upgrade recommendation.</summary>
    public string? FixedIn { get; init; }
}

// ── License ──────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LicenseState { Detected, Multiple, Unknown, NotDeclared }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LicensePolicyState { NotConfigured, Allowed, Denied, NotInPolicy, NotAssessed }

public sealed record LicenseEvidence
{
    public LicenseState State { get; init; } = LicenseState.Unknown;
    public List<string> Licenses { get; init; } = [];
    /// <summary>Where each license statement came from ("SBOM", "nuget.org registry").</summary>
    public List<string> Sources { get; init; } = [];
    public LicensePolicyState Policy { get; init; } = LicensePolicyState.NotConfigured;
    public string Detail { get; init; } = "";
}

// ── Renovate policy cross-check ──────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RemediationPolicyState { PermittedByPolicy, PermittedWithinSchedule, RequiresApproval, BlockedByPolicy, BlockedByVersionConstraint, IgnoredByRenovate, NotProposed, NotAssessable, NotAssessed }

/// <summary>What the stored Renovate policy would do with the advisory's fixed version. Nothing is ever changed in the configuration.</summary>
public sealed record RemediationPolicyCheck
{
    public RemediationPolicyState State { get; init; } = RemediationPolicyState.NotAssessed;
    public string? FixedVersion { get; init; }
    public DependencyUpdateType UpdateType { get; init; } = DependencyUpdateType.NotAssessable;
    public string Explanation { get; init; } = "";
    public List<int> MatchedRules { get; init; } = [];
    public string PolicySource { get; init; } = "";
}

// ── Lifecycle stages, comparisons and drift ──────────────────────────────────────────────────────────────────────────

/// <summary>One lifecycle stage for one package: the version seen there (null = not observed/not proven) and by which source.</summary>
public sealed record StageObservation(InventoryStage Stage, string State, string? Version, string? Source);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ComparisonKind { SourceVsSbom, ExpectedVsDeployed, Baseline }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ComparisonState
{
    // Source ↔ SBOM
    DeclaredAndPresent, EvidenceConflict, DeclaredNotInSbom, PresentNotDeclaredDirectly,
    // Expected ↔ deployed
    Matched, DifferentVersion, MissingInDeployment, AdditionalInDeployment,
    // Baseline drift
    Added, Removed, VersionChanged, LicenseChanged, SourceChanged, MetadataChanged,
    NotComparable,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HashComparison { Matched, Mismatch, NotAvailable }

public sealed record ComparisonEntry
{
    public string PackageName { get; init; } = "";
    public string PackageManager { get; init; } = "";
    public ComparisonState State { get; init; }
    public string? LeftValue { get; init; }
    public string? RightValue { get; init; }
    public HashComparison Hash { get; init; } = HashComparison.NotAvailable;
    public string Detail { get; init; } = "";
}

public sealed record InventoryComparison
{
    public ComparisonKind Kind { get; init; }
    public Guid LeftInventoryId { get; init; }
    public string LeftName { get; init; } = "";
    public Guid RightInventoryId { get; init; }
    public string RightName { get; init; } = "";
    public List<ComparisonEntry> Entries { get; init; } = [];
    public int Unchanged { get; init; }
    public List<string> Limitations { get; init; } = [];
}

// ── Automation runtime (distinct from Renovate configuration) ────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AutomationState { NotConfigured, Observed, NoRunsObserved, Unauthorized, ProviderUnavailable, NotAssessed }

public sealed record AutomationRun(string Id, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string Result, string? Url);

public sealed record DependencyPullRequest
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string SourceBranch { get; init; } = "";
    public string Status { get; init; } = "";
    public DateTimeOffset? CreatedAt { get; init; }
    public int? AgeDays { get; init; }
    /// <summary>Only when the provider supplies it (Azure DevOps mergeStatus); never inferred.</summary>
    public string? MergeStatus { get; init; }
    public string? PackageName { get; init; }
    public string? FromVersion { get; init; }
    public string? ToVersion { get; init; }
    /// <summary>From the inventory's observed version to the PR's proposed version; null when either is unknown.</summary>
    public DependencyUpdateType? UpdateType { get; init; }
    /// <summary>True only when the PR itself says so (Renovate's "[SECURITY]" title marker or a security label).</summary>
    public bool SecurityMarked { get; init; }
    /// <summary>"Stale" only when a threshold is configured; otherwise null and the age is shown.</summary>
    public bool? Stale { get; init; }
    public string? Url { get; init; }
}

public sealed record AutomationEvidence
{
    public AutomationState State { get; init; } = AutomationState.NotConfigured;
    public string Provider { get; init; } = "";
    public string? Repository { get; init; }
    public string Detail { get; init; } = "";
    public AutomationRun? LastRun { get; init; }
    public AutomationRun? LastSuccessfulRun { get; init; }
    public int? FailedRunsInWindow { get; init; }
    public List<DependencyPullRequest> OpenPullRequests { get; init; } = [];
    public string DependencyDashboard { get; init; } = "Not assessed";
    public int? StalePullRequestThresholdDays { get; init; }
    /// <summary>Static Renovate configuration state from a source review, shown beside (never merged with) runtime evidence.</summary>
    public string? StaticConfiguration { get; init; }
    public DateTimeOffset? RetrievedAt { get; init; }
}

// ── Health run ───────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Test-lead category of a check.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CheckCategory { SourceConfiguration, Inventory, Registry, Security, License, SupplyChain, Automation, Deployment }

/// <summary>Observation ≠ finding: only a policy/advisory match is a Finding; missing evidence and limitations are named as such.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObservationKind { Finding, EvidenceConflict, MissingEvidence, Limitation, Observed }

public sealed record HealthObservation
{
    public string Id { get; init; } = "";
    public CheckCategory Category { get; init; }
    public ObservationKind Kind { get; init; }
    public DependencyFindingSeverity Severity { get; init; } = DependencyFindingSeverity.Info;
    /// <summary>Severity as stated by the advisory source, when there is one.</summary>
    public string? SourceSeverity { get; init; }
    public string? PackageName { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public List<string> Evidence { get; init; } = [];
}

public sealed record EvidenceSourceStatus
{
    public CheckCategory Category { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Used / Partial / Unavailable / Not configured / Not assessed.</summary>
    public string State { get; init; } = "";
    public DateTimeOffset? RetrievedAt { get; init; }
    public string Detail { get; init; } = "";
}

public sealed record DependencyHealthItem
{
    public string Key { get; init; } = "";
    /// <summary>The first declaration; identical package@version declarations (e.g. one package in 16 csproj files) are one item.</summary>
    public InventoryDependency Dependency { get; init; } = new();
    /// <summary>Every inventory location of this package@version.</summary>
    public List<string> Locations { get; init; } = [];
    public RegistryObservation Registry { get; init; } = new();
    public VersionStatus VersionStatus { get; init; } = VersionStatus.NotAssessed;
    public string VersionDetail { get; init; } = "";
    /// <summary>Days between the observed version's publish date and the run — evidence only, no threshold.</summary>
    public int? ObservedAgeDays { get; init; }
    public SecurityEvidence Security { get; init; } = new();
    public LicenseEvidence License { get; init; } = new();
    public RemediationPolicyCheck? Remediation { get; init; }
    public List<StageObservation> Stages { get; init; } = [];
    public List<string> Conflicts { get; init; } = [];
    /// <summary>Versions of this package in earlier inventories of the same source (from the baseline), newest first.</summary>
    public List<string> History { get; init; } = [];
}

public sealed record DependencyHealthSummary
{
    /// <summary>Distinct package@version items.</summary>
    public int Dependencies { get; init; }
    /// <summary>Inventory entries (declarations) behind those items.</summary>
    public int Declarations { get; init; }
    public int RegistryObserved { get; init; }
    public int RegistryNotFound { get; init; }
    public int RegistryUnavailable { get; init; }
    public int RegistryNotAssessed { get; init; }
    public int Current { get; init; }
    public int PatchBehind { get; init; }
    public int MinorBehind { get; init; }
    public int MajorBehind { get; init; }
    public int VersionNotComparable { get; init; }
    public int AffectedDependencies { get; init; }
    public int AdvisorySourceUnavailable { get; init; }
    public int NoMatchedAdvisory { get; init; }
    public int SecurityNotAssessed { get; init; }
    public int DeprecatedOrUnlisted { get; init; }
    public int LicenseDetected { get; init; }
    public int LicenseUnknown { get; init; }
}

public sealed record DependencyHealthRequest
{
    public Guid InventoryId { get; init; }
    public Guid? BaselineInventoryId { get; init; }
    /// <summary>A second inventory of another source (e.g. SBOM vs source) to compare against — never merged silently.</summary>
    public Guid? ComparisonInventoryId { get; init; }
    public Guid? DeployedInventoryId { get; init; }
    /// <summary>Source review run whose stored Renovate snapshot is used for the security-fix cross-check.</summary>
    public Guid? PolicyRunId { get; init; }
    public string? PolicyRepository { get; init; }
    public string? Label { get; init; }
}

public sealed record DependencyHealthRun
{
    public Guid RunId { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public string Label { get; init; } = "";
    /// <summary>Set when this run is a refresh of an earlier run (which stays unchanged).</summary>
    public Guid? RefreshOf { get; init; }
    public DependencyHealthRequest Request { get; init; } = new();
    /// <summary>Full copy of the reviewed inventory, so the run stays interpretable if the inventory is later removed.</summary>
    public DependencyInventorySnapshot Inventory { get; init; } = new();
    public InventoryFreshness Freshness { get; init; }
    public string FreshnessDetail { get; init; } = "";
    public List<EvidenceSourceStatus> Sources { get; init; } = [];
    public List<DependencyHealthItem> Items { get; init; } = [];
    public DependencyHealthSummary Summary { get; init; } = new();
    public List<ReviewCategory> Categories { get; init; } = [];
    public List<InventoryComparison> Comparisons { get; init; } = [];
    public AutomationEvidence Automation { get; init; } = new();
    public string? PolicySource { get; init; }
    public List<HealthObservation> Observations { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

public sealed record DependencyHealthRunSummary(Guid RunId, DateTimeOffset CompletedAt, string Label, string InventoryName, int Dependencies, int Findings, Guid? RefreshOf);

public static class DependencyHealthLabels
{
    public static string Source(InventorySourceType type) => type switch
    {
        InventorySourceType.SourceReview => "Repository source",
        InventorySourceType.Sbom => "SBOM",
        InventorySourceType.LockFile => "Lock file",
        _ => "Deployment",
    };

    public static string Stage(InventoryStage stage) => stage switch
    {
        InventoryStage.RuntimeObserved => "Runtime loaded",
        _ => stage.ToString(),
    };

    public static string Relationship(DependencyRelationship relationship) => relationship == DependencyRelationship.Unknown ? "Unknown relationship" : relationship.ToString();

    public static string Format(SbomFormat format) => format switch
    {
        SbomFormat.CycloneDxJson => "CycloneDX JSON",
        SbomFormat.CycloneDxXml => "CycloneDX XML",
        SbomFormat.SpdxJson => "SPDX JSON",
        SbomFormat.NuGetLockFile => "NuGet packages.lock.json",
        _ => "Unknown format",
    };

    public static string Registry(RegistryState state) => state switch
    {
        RegistryState.VersionNotFound => "Version not found",
        RegistryState.PackageNotFound => "Package not found",
        RegistryState.RateLimited => "Rate limited",
        RegistryState.RegistryUnavailable => "Registry unavailable",
        RegistryState.ProviderError => "Provider error",
        RegistryState.NotAssessed => "Not assessed",
        _ => state.ToString(),
    };

    public static string Version(VersionStatus status) => status switch
    {
        VersionStatus.NewerThanLatestStable => "Newer than latest stable",
        VersionStatus.PatchBehind => "Patch behind",
        VersionStatus.MinorBehind => "Minor behind",
        VersionStatus.MajorBehind => "Major behind",
        VersionStatus.VersionUnavailable => "Version unavailable",
        VersionStatus.RegistryUnavailable => "Registry unavailable",
        VersionStatus.NotComparable => "Not comparable",
        VersionStatus.NotAssessed => "Not assessed",
        _ => status.ToString(),
    };

    /// <summary>"No matched advisories observed" — never "Safe"; source unavailable is "Security evidence unavailable", never 0.</summary>
    public static string Advisory(AdvisoryState state) => state switch
    {
        AdvisoryState.Affected => "Affected",
        AdvisoryState.NotAffectedByMatchedAdvisory => "Not affected by matched advisory",
        AdvisoryState.NoMatchedAdvisoryObserved => "No matched advisories observed",
        AdvisoryState.AdvisorySourceUnavailable => "Security evidence unavailable",
        AdvisoryState.VersionNotComparable => "Version not comparable",
        _ => "Not assessed",
    };

    public static string License(LicenseState state) => state switch
    {
        LicenseState.NotDeclared => "Not declared",
        _ => state.ToString(),
    };

    public static string LicensePolicy(LicensePolicyState state) => state switch
    {
        LicensePolicyState.NotConfigured => "Policy not configured",
        LicensePolicyState.NotInPolicy => "Not in configured policy",
        LicensePolicyState.NotAssessed => "Not assessed",
        _ => state.ToString(),
    };

    public static string Remediation(RemediationPolicyState state) => state switch
    {
        RemediationPolicyState.PermittedByPolicy => "Security fix is compatible with current Renovate policy",
        RemediationPolicyState.PermittedWithinSchedule => "Security fix is permitted by Renovate policy, within its schedule",
        RemediationPolicyState.RequiresApproval => "Security fix requires Dependency Dashboard approval under current Renovate policy",
        RemediationPolicyState.BlockedByPolicy => "Security remediation requires a version currently blocked by Renovate policy",
        RemediationPolicyState.BlockedByVersionConstraint => "Security remediation requires a version outside Renovate allowedVersions",
        RemediationPolicyState.IgnoredByRenovate => "Renovate ignores this dependency — remediation needs manual action",
        RemediationPolicyState.NotProposed => "Renovate would not propose the fixed version (unstable/prerelease)",
        RemediationPolicyState.NotAssessable => "Renovate policy for the fixed version is not assessable",
        _ => "Renovate policy not assessed",
    };

    public static string Comparison(ComparisonState state) => state switch
    {
        ComparisonState.DeclaredAndPresent => "Declared and present",
        ComparisonState.EvidenceConflict => "Evidence conflict",
        ComparisonState.DeclaredNotInSbom => "Declared, not in SBOM",
        ComparisonState.PresentNotDeclaredDirectly => "In SBOM, not declared directly",
        ComparisonState.DifferentVersion => "Different version",
        ComparisonState.MissingInDeployment => "Missing in deployment",
        ComparisonState.AdditionalInDeployment => "Additional in deployment",
        ComparisonState.VersionChanged => "Version changed",
        ComparisonState.LicenseChanged => "License changed",
        ComparisonState.SourceChanged => "Source/registry changed",
        ComparisonState.MetadataChanged => "Metadata changed",
        ComparisonState.NotComparable => "Not comparable",
        _ => state.ToString(),
    };

    public static string ComparisonKindName(ComparisonKind kind) => kind switch
    {
        ComparisonKind.SourceVsSbom => "Source ↔ SBOM",
        ComparisonKind.ExpectedVsDeployed => "Expected ↔ deployed",
        _ => "Baseline drift",
    };

    public static string Automation(AutomationState state) => state switch
    {
        AutomationState.NotConfigured => "Not configured",
        AutomationState.NoRunsObserved => "No runs observed",
        AutomationState.ProviderUnavailable => "Provider unavailable",
        AutomationState.NotAssessed => "Not assessed",
        _ => state.ToString(),
    };

    public static string Category(CheckCategory category) => category switch
    {
        CheckCategory.SourceConfiguration => "Source/configuration",
        CheckCategory.SupplyChain => "Supply chain",
        _ => category.ToString(),
    };

    public static string Kind(ObservationKind kind) => kind switch
    {
        ObservationKind.EvidenceConflict => "Evidence conflict",
        ObservationKind.MissingEvidence => "Missing evidence",
        _ => kind.ToString(),
    };

    public const string LatestStable = "Latest published stable";
}
