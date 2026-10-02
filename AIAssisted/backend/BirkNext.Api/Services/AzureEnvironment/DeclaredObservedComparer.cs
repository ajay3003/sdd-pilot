using BirkNext.AzureEnvironment;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.AzureEnvironment;

/// <summary>
/// Declared (the Infrastructure evidence of ONE Source Analysis snapshot — never re-parsed) vs Observed (ONE Azure snapshot). Explicit scope:
/// the two snapshots and the environment the Azure scope represents, which picks the per-environment declared name. States are neutral:
/// "Declared only" may be undeployed, deployed elsewhere or invisible with your permissions; "Observed only" may be managed outside this source.
/// A declared name that is computed in source is "Unable to verify", never guessed.
/// </summary>
public static class DeclaredObservedComparer
{
    /// <summary>Declared setting key → observed property key ("@location"/"@sku" = the resource's own field, "!" = inverted boolean).</summary>
    private static readonly (string Declared, string Observed)[] Settings =
    [
        ("location", "@location"), ("sku", "@sku"), ("sku_name", "@sku"), ("minimum_tls_version", "minimumTlsVersion"), ("min_tls_version", "minimumTlsVersion"),
        ("public_network_access_enabled", "publicNetworkAccess"), ("public_network_access", "publicNetworkAccess"), ("https_only", "httpsOnly"),
        ("https_traffic_only_enabled", "supportsHttpsTrafficOnly"), ("enable_https_traffic_only", "supportsHttpsTrafficOnly"),
        ("allow_nested_items_to_be_public", "allowBlobPublicAccess"), ("allow_blob_public_access", "allowBlobPublicAccess"), ("shared_access_key_enabled", "allowSharedKeyAccess"),
        ("local_auth_enabled", "!disableLocalAuth"), ("purge_protection_enabled", "enablePurgeProtection"), ("enable_rbac_authorization", "enableRbacAuthorization"),
        ("rbac_authorization_enabled", "enableRbacAuthorization"), ("partition_count", "partitionCount"), ("message_retention", "messageRetentionInDays"),
        ("max_delivery_count", "maxDeliveryCount"), ("requires_session", "requiresSession"), ("version", "version"), ("site_config.minimum_tls_version", "minTlsVersion"),
        ("site_config.ftps_state", "ftpsState"), ("ftps_state", "ftpsState"), ("site_config.http2_enabled", "http20Enabled"),
    ];

    private static readonly InfrastructureResourceKind[] NotCompared = [InfrastructureResourceKind.RoleAssignment, InfrastructureResourceKind.DiagnosticSetting, InfrastructureResourceKind.Unknown];

    public static DeclaredObservedComparison Compare(InfrastructureEvidence? infrastructure, Guid sourceSnapshotId, string sourceFingerprint, AzureEnvironmentSnapshot observed,
        SourceEnvironmentLabel? environment, string environmentBasis)
    {
        var limitations = new List<string>();
        var items = new List<DeclaredObservedItem>();
        var comparable = observed.Resources.Where(r => !NotCompared.Contains(r.ResourceKind)).ToList();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declared = (infrastructure?.Resources ?? []).Where(r => r.Kind == "resource").ToList();
        if (infrastructure is null || infrastructure.Status is SourceDomainStatus.NotDetected or SourceDomainStatus.Unsupported or SourceDomainStatus.FailedAnalysis)
            limitations.Add("The selected source snapshot has no infrastructure evidence, so nothing declared can be compared; every observed resource is listed as observed only.");

        var foreign = declared.Count(r => !Azure(r));
        if (foreign > 0) limitations.Add($"{foreign} declared resource(s) belong to other providers or Kubernetes and are not compared with Azure.");
        var skipped = declared.Count(r => Azure(r) && NotCompared.Contains(InfrastructureIdentity.Kind(r)));
        if (skipped > 0) limitations.Add($"{skipped} declared role assignment(s), diagnostic setting(s) or unclassified resource(s) are not compared here; see the Access and Monitoring views.");
        var otherEnvironment = 0;

        var azureDeclared = declared.Where(r => Azure(r) && !NotCompared.Contains(InfrastructureIdentity.Kind(r))).ToList();
        foreach (var r in azureDeclared)
        {
            var kind = InfrastructureIdentity.Kind(r);
            if (environment is { } env && r.Environment is { Kind: not SourceEnvironmentKind.Default } declaredEnv && declaredEnv.Kind != env.Kind) { otherEnvironment++; continue; }
            var baseItem = new DeclaredObservedItem
            {
                Kind = kind, Category = r.Category, Name = r.DeclaredName ?? r.LogicalName, DeclaredId = r.Id, DeclaredType = r.ResourceType, DeclaredFile = r.File, DeclaredLine = r.Line,
            };
            var (name, basis) = DeclaredName(r, environment);
            if (name is null) { items.Add(baseItem with { State = DeclaredObservedState.UnableToVerify, NameBasis = basis, Reason = basis }); continue; }
            baseItem = baseItem with { Name = name, NameBasis = basis };

            var parentName = InfrastructureIdentity.Parent(r) is { } parentId && azureDeclared.FirstOrDefault(d => d.Id == parentId) is { } parent ? DeclaredName(parent, environment).Name : null;
            var candidates = comparable.Where(o => InfrastructureIdentity.Compatible(o.ResourceKind, kind) && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)
                && (parentName is null || o.ParentId is null || string.Equals(AzureIds.Name(o.ParentId), parentName, StringComparison.OrdinalIgnoreCase))).ToList();

            if (candidates.Count == 0)
            {
                var area = AzureEnvironmentCollector.AreaOf(r.Category);
                var capability = observed.Capabilities.Where(c => c.Area == area || c.Area == AzureCapabilityArea.ResourceInventory).Select(c => c.State).ToList();
                // Child resources (hubs, groups, topics, containers, databases) come from detail reads: a partially readable area cannot prove absence.
                var parentUnread = IsChildKind(kind) && capability.Any(s => s is AzureCapabilityState.Partial or AzureCapabilityState.NotAuthorized);
                var blocked = capability.Any(s => s is AzureCapabilityState.NotAuthorized or AzureCapabilityState.Failed or AzureCapabilityState.Throttled or AzureCapabilityState.NotAssessed);
                items.Add(baseItem with
                {
                    State = blocked || parentUnread ? DeclaredObservedState.UnableToVerify : DeclaredObservedState.DeclaredOnly,
                    Reason = blocked || parentUnread
                        ? $"Not observed, but {AzureEnvironmentText.Label(area)} could not be fully read with your permissions, so absence is not established."
                        : observed.Scope.ResourceGroups.Count > 0
                            ? $"Not observed in the selected subscriptions and resource groups ({string.Join(", ", observed.Scope.ResourceGroups)})."
                            : "Not observed in the selected subscriptions.",
                });
                continue;
            }
            foreach (var c in candidates) matched.Add(c.Id);
            if (candidates.Count > 1)
            {
                items.Add(baseItem with
                {
                    State = DeclaredObservedState.AmbiguousMatch, ObservedIds = candidates.Select(c => c.Id).ToList(), ObservedType = candidates[0].Type,
                    Reason = $"{candidates.Count} observed resources have this name (different subscriptions or resource groups). Narrow the scope to compare one.",
                });
                continue;
            }
            var match = candidates[0];
            var (differences, uncomparable) = Differences(r, match);
            items.Add(baseItem with
            {
                State = differences.Count > 0 ? DeclaredObservedState.ConfigurationDiffers : DeclaredObservedState.DeclaredAndObserved,
                ObservedIds = [match.Id], ObservedType = match.Type, Differences = differences,
                Reason = differences.Count > 0 ? $"{differences.Count} declared setting(s) differ from what Azure reports."
                    : $"Observed with the declared name{(uncomparable > 0 ? $"; {uncomparable} declared setting(s) are computed in source and were not compared" : "")}.",
            });
        }
        if (otherEnvironment > 0) limitations.Add($"{otherEnvironment} declared resource(s) belong to another environment's folder and are not compared with this scope.");
        var unclassified = observed.Resources.Count(o => o.ResourceKind == InfrastructureResourceKind.Unknown && o.Type != "microsoft.resources/subscriptions/resourcegroups");
        if (unclassified > 0) limitations.Add($"{unclassified} observed resource(s) of types without a provider-neutral kind (e.g. plans, security groups) are in the inventory but not compared.");

        foreach (var o in comparable.Where(o => !matched.Contains(o.Id) && o.Type != "microsoft.resources/subscriptions/resourcegroups"))
            items.Add(new DeclaredObservedItem
            {
                State = DeclaredObservedState.ObservedOnly, Kind = o.ResourceKind, Category = o.Category, Name = o.Name, ObservedIds = [o.Id], ObservedType = o.Type,
                Reason = "Observed in Azure; no matching declaration in this source snapshot (it may be managed elsewhere).",
            });

        var ordered = items.OrderBy(i => i.State switch
        {
            DeclaredObservedState.ConfigurationDiffers => 0, DeclaredObservedState.AmbiguousMatch => 1, DeclaredObservedState.DeclaredOnly => 2,
            DeclaredObservedState.UnableToVerify => 3, DeclaredObservedState.DeclaredAndObserved => 4, _ => 5,
        }).ThenBy(i => i.Category).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new DeclaredObservedComparison
        {
            SourceSnapshotId = sourceSnapshotId, SourceFingerprint = sourceFingerprint, InfrastructureAnalyzerVersion = infrastructure?.AnalyzerVersion ?? 0,
            AzureSnapshotId = observed.Id, AzureCapturedAt = observed.CapturedAt, Environment = environment, EnvironmentBasis = environmentBasis,
            Subscriptions = observed.Subscriptions.Select(s => s.DisplayName.Length > 0 ? s.DisplayName : s.Id).ToList(), Items = ordered,
            Counts = Enum.GetValues<DeclaredObservedState>().ToDictionary(s => s, s => ordered.Count(i => i.State == s)), Limitations = limitations,
        };
    }

    private static bool IsChildKind(InfrastructureResourceKind kind) => kind is InfrastructureResourceKind.EventHub or InfrastructureResourceKind.ConsumerGroup
        or InfrastructureResourceKind.ServiceBusTopic or InfrastructureResourceKind.ServiceBusQueue or InfrastructureResourceKind.ServiceBusSubscription
        or InfrastructureResourceKind.BlobContainer or InfrastructureResourceKind.Database;

    private static bool Azure(InfrastructureResource r) => r.Format is InfrastructureFormat.Bicep or InfrastructureFormat.Arm
        || r.Provider.Equals("azurerm", StringComparison.OrdinalIgnoreCase) || r.Provider.Equals("azapi", StringComparison.OrdinalIgnoreCase)
        || r.ResourceType.StartsWith("azurerm_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The name to look for in this environment: the per-environment name (from that environment's variable file), else a literal.</summary>
    internal static (string? Name, string Basis) DeclaredName(InfrastructureResource r, SourceEnvironmentLabel? environment)
    {
        if (environment is { } env && r.EnvironmentNames.FirstOrDefault(n => n.Environment.Kind == env.Kind) is { } named) return (named.Name, $"environment file ({named.Basis})");
        if (r.DeclaredNameResolved && !string.IsNullOrWhiteSpace(r.DeclaredName)) return (r.DeclaredName, "literal in source");
        if (r.EnvironmentNames.Count > 0)
            return (null, environment is null
                ? "The name differs per environment in source; choose the environment this Azure scope represents."
                : $"The name differs per environment, but no {environment.Raw} variable file resolves it.");
        return (null, "The name is computed in source and could not be resolved statically.");
    }

    private static (List<DeclaredObservedDifference> Differences, int Uncomparable) Differences(InfrastructureResource declared, ObservedResource observed)
    {
        var differences = new List<DeclaredObservedDifference>();
        var uncomparable = 0;
        foreach (var setting in declared.Settings)
        {
            var map = Settings.FirstOrDefault(s => s.Declared == setting.Key);
            if (map.Declared is null) continue;
            if (!setting.Resolved) { uncomparable++; continue; }
            var invert = map.Observed.StartsWith('!');
            var key = map.Observed.TrimStart('!');
            var value = key switch
            {
                "@location" => observed.Location,
                "@sku" => observed.Sku,
                _ => observed.Properties.FirstOrDefault(p => p.Key == key)?.Value,
            };
            if (value is null) continue; // not observed (not read, or Azure omits defaults): no claim
            var expected = Normalize(setting.Value);
            var actual = Normalize(value);
            if (invert && actual is "true" or "false") actual = actual == "true" ? "false" : "true";
            var equal = key == "@sku" ? actual.Split(' ', '×').Contains(expected) || actual.StartsWith(expected, StringComparison.Ordinal) : expected == actual;
            if (!equal) differences.Add(new(setting.Key, setting.Value, invert ? $"{value} ({key})" : value));
        }
        return (differences, uncomparable);
    }

    internal static string Normalize(string value)
    {
        var v = value.Trim().Trim('"').ToLowerInvariant().Replace(" ", "");
        v = v switch { "enabled" => "true", "disabled" => "false", _ => v };
        if (v.StartsWith("tls", StringComparison.Ordinal)) v = v[3..].Replace('_', '.');
        return v;
    }
}
