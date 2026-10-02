using System.Text.Json.Serialization;
using BirkNext.SourceDomains;

namespace BirkNext.AzureEnvironment;

// ── Azure Environment Analysis: read-only observed evidence of a deployed Azure environment ─────────────────────────────────
// Observed (control-plane inventory and metadata read with the signed-in person's own Azure permissions) is a fourth kind of evidence,
// next to Declared (Source Analysis), Configured (Target Environment / review settings) and Verified (a behaviour a review checked).
//   Declared ≠ Configured ≠ Observed ≠ Verified.
// Nothing here is a Pass/Fail: inventory is facts with provenance, observations are neutral statements a review may weigh, and a
// capability BirkNext could not read is "Not authorized" or "Not assessed" — never a failed environment.
// BirkNext only reads: GET on Azure Resource Manager plus predefined Azure Resource Graph queries. It never creates, changes or deletes
// a resource, role assignment or setting, never activates PIM, never reads secret values, keys, app settings, blobs or messages.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AzureConnectionState { NotConfigured, SignedOut, SigningIn, AwaitingDeviceCode, SignedIn, Expired, Failed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AzureSignInMethod { None, DedicatedEdgeProfile, DeviceCode }

public sealed record AzureDeviceCodePrompt(string UserCode, string VerificationUrl, DateTimeOffset ExpiresOn);

/// <summary>The sign-in state of this BirkNext instance's Azure session. Tokens live only in the backend's memory; this carries none.</summary>
public sealed record AzureConnectionStatus
{
    public AzureConnectionState State { get; init; }
    public AzureSignInMethod Method { get; init; }
    /// <summary>The signed-in account as Entra reports it (display only; never persisted or logged).</summary>
    public string? Account { get; init; }
    public string? TenantId { get; init; }
    public DateTimeOffset? SignedInAt { get; init; }
    public DateTimeOffset? ExpiresOn { get; init; }
    public string Message { get; init; } = "";
    public AzureDeviceCodePrompt? DeviceCode { get; init; }
    public bool DedicatedEdgeAvailable { get; init; }
    public bool DeviceCodeAllowed { get; init; }
    /// <summary>How the dedicated profile is described (never its path with a user name in it).</summary>
    public string ProfileLabel { get; init; } = AzureEnvironmentText.ProfileLabel;
    /// <summary>What an administrator must configure when the state is NotConfigured.</summary>
    public List<string> Requirements { get; init; } = [];
}

public sealed record AzureSubscription(string Id, string DisplayName, string State, string? TenantId);

/// <summary>What can be read, by area: the capability matrix. Probed with read operations only; a 403 is NotAuthorized, not Failed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AzureCapabilityArea
{
    Subscriptions, ResourceGraph, ResourceInventory, EffectivePermissions, RoleAssignments, PimRoles, Compute, Messaging, Storage, Databases, KeyVaultMetadata,
    ManagedIdentity, Networking, Monitoring, DiagnosticSettings,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AzureCapabilityState { Available, Partial, NotAuthorized, Throttled, NotFound, Failed, NotAssessed, NotApplicable }

public sealed record AzureCapability(AzureCapabilityArea Area, AzureCapabilityState State, string Detail, string Operation, string? SubscriptionId = null);

/// <summary>A whitelisted, non-secret property of an observed resource ("publicNetworkAccess" = "Disabled"). Area groups it for review.</summary>
public sealed record ObservedProperty(string Key, string Value, string Area);

public sealed record ObservedTag(string Key, string Value);

public sealed record ObservedResource
{
    /// <summary>The Azure resource id (ARM). Case-insensitive; compared with <see cref="AzureIds.Same"/>.</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>ARM resource type, e.g. "microsoft.eventhub/namespaces".</summary>
    public string Type { get; init; } = "";
    public string? ArmKind { get; init; }
    public InfrastructureCategory Category { get; init; } = InfrastructureCategory.Other;
    public string CategoryDetail { get; init; } = "";
    /// <summary>The provider-neutral kind Source Analysis also uses, so declared and observed resources compare on one identity.</summary>
    public InfrastructureResourceKind ResourceKind { get; init; }
    public string? Location { get; init; }
    public string SubscriptionId { get; init; } = "";
    public string? ResourceGroup { get; init; }
    public string? ParentId { get; init; }
    public string? Sku { get; init; }
    public List<ObservedTag> Tags { get; init; } = [];
    public List<ObservedProperty> Properties { get; init; } = [];
    public string? IdentityType { get; init; }
    /// <summary>The system-assigned identity's principal (object) id, used to attribute role assignments to this resource.</summary>
    public string? PrincipalId { get; init; }
    public List<string> UserAssignedIdentityIds { get; init; } = [];
    /// <summary>"Azure Resource Graph" or "Azure Resource Manager" (+ the operation that produced it).</summary>
    public string Source { get; init; } = "";
    /// <summary>The environment the resource's name or tags state, as an inference (never confirmed by Azure).</summary>
    public SourceEnvironmentLabel? Environment { get; init; }
    public string ObservedState { get; init; } = AzureEnvironmentText.ObservedNotVerified;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObservedRelationshipKind { Contains, ChildOf, HostedOn, InSubnet, PrivateEndpointFor, UsesIdentity, SendsDiagnosticsTo, TelemetryWorkspace, RoleAssignedOn, LikelyTelemetryFor }

/// <summary>Confirmed: an Azure resource id on one resource names the other. Inferred: a naming convention suggests it (never presented as fact).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObservedRelationshipConfidence { Confirmed, Inferred }

public sealed record ObservedRelationship(string FromId, string ToId, ObservedRelationshipKind Kind, ObservedRelationshipConfidence Confidence, string Evidence, bool TargetInScope = true);

/// <summary>A role assignment as Azure RBAC reports it at subscription scope or below. Assigned ≠ exercised.</summary>
public sealed record ObservedRoleAssignment
{
    public string Scope { get; init; } = "";
    public string ScopeLevel { get; init; } = "";
    public string RoleName { get; init; } = "";
    public string PrincipalType { get; init; } = "";
    public string PrincipalId { get; init; } = "";
    /// <summary>The observed resource whose managed identity this principal is, when matched by principal id.</summary>
    public string? PrincipalResourceId { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PimRoleStatus { Active, Eligible }

/// <summary>A role of the SIGNED-IN person as PIM reports it ("asTarget"). Eligible ≠ active: an eligible role grants nothing until activated in PIM.</summary>
public sealed record SignedInRole(string Scope, string RoleName, PimRoleStatus Status, string MemberType, DateTimeOffset? EndsOn);

/// <summary>The signed-in person's effective permissions on a subscription, from Azure's permission API (not inferred from group names).</summary>
public sealed record EffectivePermissions(string SubscriptionId, bool CanReadResources, bool IncludesWriteActions, List<string> ExampleActions, string Note);

public sealed record ObservedAccess
{
    public List<ObservedRoleAssignment> Assignments { get; init; } = [];
    public List<SignedInRole> SignedInRoles { get; init; } = [];
    public List<EffectivePermissions> Permissions { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObservationArea { Security, Network, Identity, Observability, Messaging, Data }

/// <summary>A neutral statement about what was observed ("Public network access is enabled"). ForReview marks what a reviewer usually
/// examines; it is not a finding against a policy.</summary>
public sealed record AzureObservation(ObservationArea Area, string ResourceId, string ResourceName, string Key, string Statement, bool ForReview);

public sealed record AzureQueryRecord(string Name, string Operation, int Status, int Pages, int Items, string? SubscriptionId, string Outcome);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AzureAnalysisStatus { Complete, Partial, NotAuthorized, Failed }

public sealed record AzureAnalysisRequest
{
    /// <summary>The BirkNext Target Environment the snapshot belongs to (scopes it like Source Analysis snapshots).</summary>
    public string EnvironmentId { get; init; } = "";
    public List<string> SubscriptionIds { get; init; } = [];
    /// <summary>Optional resource-group names to narrow the scope (exact names; never a query).</summary>
    public List<string> ResourceGroups { get; init; } = [];
    /// <summary>Optional environment the person says this scope represents (dev, qa …); used for declared-vs-observed name resolution.</summary>
    public string? EnvironmentLabel { get; init; }
}

public sealed record AzureAnalysisScope(List<string> SubscriptionIds, List<string> ResourceGroups, SourceEnvironmentLabel? Environment);

public sealed record AzureEnvironmentSnapshot
{
    public Guid Id { get; init; }
    public string EnvironmentId { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
    public string? TenantId { get; init; }
    public AzureSignInMethod Method { get; init; }
    public int CollectorVersion { get; init; } = AzureEnvironmentText.CollectorVersion;
    public AzureAnalysisStatus Status { get; init; }
    public AzureAnalysisScope Scope { get; init; } = new([], [], null);
    public List<AzureSubscription> Subscriptions { get; init; } = [];
    public List<ObservedResource> Resources { get; init; } = [];
    public List<ObservedRelationship> Relationships { get; init; } = [];
    public List<AzureCapability> Capabilities { get; init; } = [];
    public ObservedAccess Access { get; init; } = new();
    public List<AzureObservation> Observations { get; init; } = [];
    public List<AzureQueryRecord> Queries { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    public string Boundary { get; init; } = AzureEnvironmentText.Boundary;
}

/// <summary>Snapshot list row (no resource payload).</summary>
public sealed record AzureEnvironmentSnapshotSummary(Guid Id, string EnvironmentId, DateTimeOffset CapturedAt, AzureAnalysisStatus Status, List<string> SubscriptionNames,
    int Resources, int Relationships, string? Environment);

// ── Declared (Source Analysis) vs Observed (Azure) ─────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeclaredObservedState { DeclaredAndObserved, ConfigurationDiffers, DeclaredOnly, ObservedOnly, AmbiguousMatch, UnableToVerify }

public sealed record DeclaredObservedDifference(string Setting, string Declared, string Observed);

public sealed record DeclaredObservedItem
{
    public DeclaredObservedState State { get; init; }
    public InfrastructureResourceKind Kind { get; init; }
    public InfrastructureCategory Category { get; init; }
    public string Name { get; init; } = "";
    public string? DeclaredId { get; init; }
    public string? DeclaredType { get; init; }
    public string? DeclaredFile { get; init; }
    public int DeclaredLine { get; init; }
    /// <summary>How the declared name was resolved: "literal", "environment file (…)", or why it could not be.</summary>
    public string? NameBasis { get; init; }
    public List<string> ObservedIds { get; init; } = [];
    public string? ObservedType { get; init; }
    public List<DeclaredObservedDifference> Differences { get; init; } = [];
    public string Reason { get; init; } = "";
}

public sealed record DeclaredObservedComparison
{
    public Guid SourceSnapshotId { get; init; }
    public string SourceFingerprint { get; init; } = "";
    public int InfrastructureAnalyzerVersion { get; init; }
    public Guid AzureSnapshotId { get; init; }
    public DateTimeOffset AzureCapturedAt { get; init; }
    public SourceEnvironmentLabel? Environment { get; init; }
    public string EnvironmentBasis { get; init; } = "";
    public List<string> Subscriptions { get; init; } = [];
    public List<DeclaredObservedItem> Items { get; init; } = [];
    public Dictionary<DeclaredObservedState, int> Counts { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    public string Boundary { get; init; } = AzureEnvironmentText.ComparisonBoundary;
}

// ── Consumer reads ──────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObservedLookupState { Observed, NotObserved, MultipleObserved, UnableToVerify, NoSnapshot, Disabled }

/// <summary>Whether a resource (by kind and name) was observed in the newest Azure snapshot of a Target Environment — for IQR, Security and
/// Observability consumers. NotObserved is neutral: the scope may not include it, or it may be named differently.</summary>
public sealed record ObservedResourceLookup
{
    public ObservedLookupState State { get; init; }
    public InfrastructureResourceKind Kind { get; init; }
    public string? Name { get; init; }
    public ObservedResource? Resource { get; init; }
    public List<AzureObservation> Observations { get; init; } = [];
    public List<ObservedRelationship> Relationships { get; init; } = [];
    public Guid? SnapshotId { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>A value observed in Azure that a person may copy into a Target Environment field. Never saved by BirkNext.</summary>
public sealed record AzureTargetSuggestion(string Field, string Value, string ResourceId, string ResourceName, string Basis);

public static class AzureEnvironmentText
{
    public const int CollectorVersion = 1;
    public const string ProfileLabel = "Dedicated BirkNext Azure sign-in profile (Microsoft Edge, separate from your normal browser profile)";
    public const string ObservedNotVerified = "Observed in Azure (control plane); runtime behaviour not verified";
    public const string Boundary = "Read-only inventory and metadata from Azure Resource Manager and predefined Azure Resource Graph queries, with your signed-in permissions. " +
        "Nothing was created, changed or deleted; no secret values, keys, app settings, blobs or messages were read. Observed ≠ verified.";
    public const string ComparisonBoundary = "Infrastructure declared in the selected Source Analysis snapshot compared with resources observed in the selected Azure scope. " +
        "Declared only can mean not deployed, deployed elsewhere, or not visible with your permissions; observed only can mean managed outside this source. Neither is a failure.";
    public const string PimGuidance = "BirkNext never activates PIM roles. If you need a role, activate it yourself in Privileged Identity Management, then use \"Refresh access\" " +
        "so BirkNext requests a new token and re-reads what you can see. An eligible role grants nothing until it is activated.";
    public const string ReadOnlyWithWriteAccess = "Your effective permissions include write actions. BirkNext still only reads: it issues GET requests and predefined Resource Graph queries, nothing else.";

    public static string Label(DeclaredObservedState state) => state switch
    {
        DeclaredObservedState.DeclaredAndObserved => "Declared and observed",
        DeclaredObservedState.ConfigurationDiffers => "Configuration differs",
        DeclaredObservedState.DeclaredOnly => "Declared only",
        DeclaredObservedState.ObservedOnly => "Observed only",
        DeclaredObservedState.AmbiguousMatch => "Ambiguous match",
        _ => "Unable to verify",
    };

    public static string Label(AzureCapabilityState state) => state switch
    {
        AzureCapabilityState.Available => "Available",
        AzureCapabilityState.Partial => "Partial",
        AzureCapabilityState.NotAuthorized => "Not authorized",
        AzureCapabilityState.Throttled => "Throttled",
        AzureCapabilityState.NotFound => "Not found",
        AzureCapabilityState.Failed => "Could not be read",
        AzureCapabilityState.NotApplicable => "Not applicable",
        _ => "Not assessed",
    };

    public static string Label(AzureCapabilityArea area) => area switch
    {
        AzureCapabilityArea.ResourceGraph => "Resource Graph",
        AzureCapabilityArea.ResourceInventory => "Resource inventory",
        AzureCapabilityArea.EffectivePermissions => "Your effective permissions",
        AzureCapabilityArea.RoleAssignments => "Role assignments (RBAC)",
        AzureCapabilityArea.PimRoles => "Your PIM roles",
        AzureCapabilityArea.KeyVaultMetadata => "Key Vault metadata",
        AzureCapabilityArea.ManagedIdentity => "Managed identities",
        AzureCapabilityArea.DiagnosticSettings => "Diagnostic settings",
        _ => area.ToString(),
    };

    public static string Label(ObservedRelationshipKind kind) => kind switch
    {
        ObservedRelationshipKind.Contains => "contains",
        ObservedRelationshipKind.ChildOf => "is part of",
        ObservedRelationshipKind.HostedOn => "is hosted on",
        ObservedRelationshipKind.InSubnet => "is connected to subnet",
        ObservedRelationshipKind.PrivateEndpointFor => "is a private endpoint for",
        ObservedRelationshipKind.UsesIdentity => "uses identity",
        ObservedRelationshipKind.SendsDiagnosticsTo => "sends diagnostics to",
        ObservedRelationshipKind.TelemetryWorkspace => "stores telemetry in",
        ObservedRelationshipKind.RoleAssignedOn => "has a role on",
        _ => "likely sends telemetry for",
    };

    /// <summary>The Azure portal deep link of a resource (a link only; BirkNext never reads the portal).</summary>
    public static string PortalUrl(string resourceId, string? tenantId) =>
        $"https://portal.azure.com/#@{(string.IsNullOrWhiteSpace(tenantId) ? "" : tenantId)}/resource{resourceId}";
}

public static class AzureIds
{
    public static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The last segment of an ARM id (the resource's name).</summary>
    public static string Name(string id) => id.TrimEnd('/').Split('/') is { Length: > 0 } parts ? parts[^1] : id;

    public static string? Segment(string id, string key)
    {
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
            if (string.Equals(parts[i], key, StringComparison.OrdinalIgnoreCase)) return parts[i + 1];
        return null;
    }

    public static string? SubscriptionId(string id) => Segment(id, "subscriptions");
    public static string? ResourceGroup(string id) => Segment(id, "resourceGroups");

    /// <summary>The parent resource id of a child resource ("…/namespaces/ns/eventhubs/hub" → "…/namespaces/ns"); null for a top-level resource.</summary>
    public static string? Parent(string id)
    {
        var parts = id.TrimEnd('/').Split('/');
        var providers = Array.FindLastIndex(parts, p => string.Equals(p, "providers", StringComparison.OrdinalIgnoreCase));
        // …/providers/{namespace}/{type}/{name}[/{childType}/{childName}]…
        if (providers < 0 || parts.Length - providers <= 4) return null;
        return string.Join('/', parts[..^2]);
    }

    public static string ResourceGroupId(string subscriptionId, string resourceGroup) => $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}";
}

/// <summary>A configured review value (IQR catalog field) looked up in one Azure snapshot. Stored with the review run; never re-queried.</summary>
public sealed record ConfiguredObservedComparison(string SubjectId, string SubjectName, string? ItemId, string Field, string? ConfiguredValue, ObservedResourceLookup Lookup);
