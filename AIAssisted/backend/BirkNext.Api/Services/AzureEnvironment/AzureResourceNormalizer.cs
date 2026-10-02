using System.Globalization;
using System.Text.Json;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.AzureEnvironment;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.AzureEnvironment;

/// <summary>
/// Azure resource JSON (Resource Graph row or ARM GET item) → <see cref="ObservedResource"/>: provider-neutral category and kind, and only
/// WHITELISTED, non-secret properties. The raw properties object is never stored. A value that looks like a secret or connection string is
/// dropped even on a whitelisted path (defence in depth: such values should never be on these paths).
/// </summary>
public static class AzureResourceNormalizer
{
    private static readonly (string Type, InfrastructureCategory Category, string Label)[] Categories =
    [
        ("microsoft.web/sites", InfrastructureCategory.Compute, "App Service app"), ("microsoft.web/serverfarms", InfrastructureCategory.Compute, "App Service plan"),
        ("microsoft.app/containerapps", InfrastructureCategory.Compute, "Container app"), ("microsoft.app/managedenvironments", InfrastructureCategory.Compute, "Container Apps environment"),
        ("microsoft.containerservice/managedclusters", InfrastructureCategory.Compute, "AKS cluster"), ("microsoft.compute/virtualmachines", InfrastructureCategory.Compute, "Virtual machine"),
        ("microsoft.compute/virtualmachinescalesets", InfrastructureCategory.Compute, "VM scale set"), ("microsoft.logic/workflows", InfrastructureCategory.Compute, "Logic app"),
        ("microsoft.eventhub/namespaces/eventhubs/consumergroups", InfrastructureCategory.Messaging, "Event Hub consumer group"),
        ("microsoft.eventhub/namespaces/eventhubs", InfrastructureCategory.Messaging, "Event Hub"), ("microsoft.eventhub/namespaces", InfrastructureCategory.Messaging, "Event Hubs namespace"),
        ("microsoft.servicebus/namespaces/topics/subscriptions", InfrastructureCategory.Messaging, "Service Bus subscription"),
        ("microsoft.servicebus/namespaces/topics", InfrastructureCategory.Messaging, "Service Bus topic"), ("microsoft.servicebus/namespaces/queues", InfrastructureCategory.Messaging, "Service Bus queue"),
        ("microsoft.servicebus/namespaces", InfrastructureCategory.Messaging, "Service Bus namespace"), ("microsoft.eventgrid", InfrastructureCategory.Messaging, "Event Grid"),
        ("microsoft.storage/storageaccounts/blobservices/containers", InfrastructureCategory.Storage, "Blob container"), ("microsoft.storage/storageaccounts", InfrastructureCategory.Storage, "Storage account"),
        ("microsoft.dbforpostgresql/flexibleservers/databases", InfrastructureCategory.Database, "PostgreSQL database"),
        ("microsoft.dbforpostgresql/flexibleservers", InfrastructureCategory.Database, "PostgreSQL flexible server"), ("microsoft.dbformysql", InfrastructureCategory.Database, "MySQL server"),
        ("microsoft.sql/servers/databases", InfrastructureCategory.Database, "SQL database"), ("microsoft.sql/servers", InfrastructureCategory.Database, "SQL server"),
        ("microsoft.documentdb/databaseaccounts", InfrastructureCategory.Database, "Cosmos DB account"), ("microsoft.cache/redis", InfrastructureCategory.Cache, "Azure Cache for Redis"),
        ("microsoft.keyvault/vaults", InfrastructureCategory.SecretStore, "Key Vault"), ("microsoft.managedidentity/userassignedidentities", InfrastructureCategory.Identity, "User-assigned managed identity"),
        ("microsoft.network/virtualnetworks/subnets", InfrastructureCategory.Networking, "Subnet"), ("microsoft.network/virtualnetworks", InfrastructureCategory.Networking, "Virtual network"),
        ("microsoft.network/networksecuritygroups", InfrastructureCategory.Networking, "Network security group"), ("microsoft.network/privateendpoints", InfrastructureCategory.Networking, "Private endpoint"),
        ("microsoft.network/privatednszones", InfrastructureCategory.Dns, "Private DNS zone"), ("microsoft.network/dnszones", InfrastructureCategory.Dns, "DNS zone"),
        ("microsoft.network/publicipaddresses", InfrastructureCategory.Networking, "Public IP address"), ("microsoft.network/applicationgateways", InfrastructureCategory.Networking, "Application gateway"),
        ("microsoft.network/frontdoors", InfrastructureCategory.Networking, "Front Door"), ("microsoft.cdn/profiles", InfrastructureCategory.Networking, "Front Door / CDN profile"),
        ("microsoft.network/networkinterfaces", InfrastructureCategory.Networking, "Network interface"), ("microsoft.network", InfrastructureCategory.Networking, "Network resource"),
        ("microsoft.apimanagement/service", InfrastructureCategory.ApiGateway, "API Management"), ("microsoft.containerregistry/registries", InfrastructureCategory.ContainerRegistry, "Container registry"),
        ("microsoft.insights/components", InfrastructureCategory.Observability, "Application Insights"), ("microsoft.operationalinsights/workspaces", InfrastructureCategory.Observability, "Log Analytics workspace"),
        ("microsoft.insights/actiongroups", InfrastructureCategory.Observability, "Action group"), ("microsoft.insights/metricalerts", InfrastructureCategory.Observability, "Metric alert"),
        ("microsoft.insights/scheduledqueryrules", InfrastructureCategory.Observability, "Log alert"), ("microsoft.insights", InfrastructureCategory.Observability, "Monitoring resource"),
        ("microsoft.alertsmanagement", InfrastructureCategory.Observability, "Alert rule"), ("microsoft.portal/dashboards", InfrastructureCategory.Observability, "Dashboard"),
        ("microsoft.appconfiguration/configurationstores", InfrastructureCategory.Configuration, "App Configuration"),
        ("microsoft.resources/subscriptions/resourcegroups", InfrastructureCategory.ResourceContainer, "Resource group"),
    ];

    /// <summary>(type prefix, JSON path, key, area). "[]" flattens an array. Area "Reference" holds resource ids the topology builder links.</summary>
    private static readonly (string Type, string Path, string Key, string Area)[] Whitelist =
    [
        ("", "properties.provisioningState", "provisioningState", "Configuration"),
        ("", "properties.publicNetworkAccess", "publicNetworkAccess", "Network"),
        ("", "properties.minimumTlsVersion", "minimumTlsVersion", "Security"),
        ("", "properties.minimalTlsVersion", "minimumTlsVersion", "Security"),
        ("", "properties.privateEndpointConnections", "privateEndpointConnections", "Network"),
        // Compute
        ("microsoft.web/sites", "properties.state", "state", "Configuration"), ("microsoft.web/sites", "properties.httpsOnly", "httpsOnly", "Security"),
        ("microsoft.web/sites", "properties.defaultHostName", "defaultHostName", "Configuration"), ("microsoft.web/sites", "properties.clientCertEnabled", "clientCertEnabled", "Security"),
        ("microsoft.web/sites", "properties.serverFarmId", "serverFarmId", "Reference"), ("microsoft.web/sites", "properties.virtualNetworkSubnetId", "virtualNetworkSubnetId", "Reference"),
        ("microsoft.web/sites", "properties.siteConfig.minTlsVersion", "minTlsVersion", "Security"), ("microsoft.web/sites", "properties.siteConfig.ftpsState", "ftpsState", "Security"),
        ("microsoft.web/sites", "properties.siteConfig.http20Enabled", "http20Enabled", "Configuration"), ("microsoft.web/sites", "properties.siteConfig.alwaysOn", "alwaysOn", "Configuration"),
        ("microsoft.web/sites", "properties.siteConfig.linuxFxVersion", "runtime", "Configuration"), ("microsoft.web/sites", "properties.siteConfig.netFrameworkVersion", "netFrameworkVersion", "Configuration"),
        ("microsoft.web/sites", "properties.siteConfig.healthCheckPath", "healthCheckPath", "Configuration"), ("microsoft.web/sites", "properties.siteConfig.vnetRouteAllEnabled", "vnetRouteAllEnabled", "Network"),
        ("microsoft.web/sites", "properties.siteConfig.scmMinTlsVersion", "scmMinTlsVersion", "Security"),
        ("microsoft.web/serverfarms", "properties.numberOfWorkers", "numberOfWorkers", "Capacity"), ("microsoft.web/serverfarms", "properties.reserved", "linux", "Configuration"),
        ("microsoft.app/containerapps", "properties.managedEnvironmentId", "managedEnvironmentId", "Reference"), ("microsoft.app/containerapps", "properties.configuration.ingress.external", "ingressExternal", "Network"),
        ("microsoft.app/containerapps", "properties.configuration.ingress.fqdn", "fqdn", "Configuration"), ("microsoft.app/containerapps", "properties.configuration.ingress.allowInsecure", "allowInsecure", "Security"),
        ("microsoft.app/managedenvironments", "properties.vnetConfiguration.infrastructureSubnetId", "infrastructureSubnetId", "Reference"),
        ("microsoft.app/managedenvironments", "properties.vnetConfiguration.internal", "internal", "Network"),
        ("microsoft.containerservice/managedclusters", "properties.kubernetesVersion", "kubernetesVersion", "Configuration"),
        ("microsoft.containerservice/managedclusters", "properties.apiServerAccessProfile.enablePrivateCluster", "privateCluster", "Network"),
        // Messaging
        ("microsoft.eventhub/namespaces", "properties.kafkaEnabled", "kafkaEnabled", "Messaging"), ("microsoft.eventhub/namespaces", "properties.disableLocalAuth", "disableLocalAuth", "Security"),
        ("microsoft.eventhub/namespaces", "properties.zoneRedundant", "zoneRedundant", "Capacity"), ("microsoft.eventhub/namespaces", "properties.isAutoInflateEnabled", "autoInflate", "Capacity"),
        ("microsoft.eventhub/namespaces", "properties.maximumThroughputUnits", "maximumThroughputUnits", "Capacity"), ("microsoft.eventhub/namespaces", "properties.serviceBusEndpoint", "endpoint", "Configuration"),
        ("microsoft.eventhub/namespaces/eventhubs", "properties.partitionCount", "partitionCount", "Messaging"), ("microsoft.eventhub/namespaces/eventhubs", "properties.messageRetentionInDays", "messageRetentionInDays", "Messaging"),
        ("microsoft.eventhub/namespaces/eventhubs", "properties.retentionDescription.retentionTimeInHours", "retentionTimeInHours", "Messaging"), ("microsoft.eventhub/namespaces/eventhubs", "properties.status", "status", "Messaging"),
        ("microsoft.eventhub/namespaces/eventhubs", "properties.captureDescription.enabled", "captureEnabled", "Messaging"),
        ("microsoft.servicebus/namespaces", "properties.disableLocalAuth", "disableLocalAuth", "Security"), ("microsoft.servicebus/namespaces", "properties.zoneRedundant", "zoneRedundant", "Capacity"),
        ("microsoft.servicebus/namespaces", "properties.serviceBusEndpoint", "endpoint", "Configuration"),
        ("microsoft.servicebus/namespaces/topics", "properties.status", "status", "Messaging"), ("microsoft.servicebus/namespaces/topics", "properties.maxSizeInMegabytes", "maxSizeInMegabytes", "Messaging"),
        ("microsoft.servicebus/namespaces/topics", "properties.requiresDuplicateDetection", "requiresDuplicateDetection", "Messaging"), ("microsoft.servicebus/namespaces/topics", "properties.enablePartitioning", "enablePartitioning", "Messaging"),
        ("microsoft.servicebus/namespaces/queues", "properties.status", "status", "Messaging"), ("microsoft.servicebus/namespaces/queues", "properties.requiresSession", "requiresSession", "Messaging"),
        ("microsoft.servicebus/namespaces/queues", "properties.maxDeliveryCount", "maxDeliveryCount", "Messaging"), ("microsoft.servicebus/namespaces/queues", "properties.deadLetteringOnMessageExpiration", "deadLetteringOnMessageExpiration", "Messaging"),
        ("microsoft.servicebus/namespaces/topics/subscriptions", "properties.maxDeliveryCount", "maxDeliveryCount", "Messaging"), ("microsoft.servicebus/namespaces/topics/subscriptions", "properties.requiresSession", "requiresSession", "Messaging"),
        ("microsoft.servicebus/namespaces/topics/subscriptions", "properties.deadLetteringOnMessageExpiration", "deadLetteringOnMessageExpiration", "Messaging"), ("microsoft.servicebus/namespaces/topics/subscriptions", "properties.status", "status", "Messaging"),
        // Storage
        ("microsoft.storage/storageaccounts", "properties.supportsHttpsTrafficOnly", "supportsHttpsTrafficOnly", "Security"), ("microsoft.storage/storageaccounts", "properties.allowBlobPublicAccess", "allowBlobPublicAccess", "Security"),
        ("microsoft.storage/storageaccounts", "properties.allowSharedKeyAccess", "allowSharedKeyAccess", "Security"), ("microsoft.storage/storageaccounts", "properties.networkAcls.defaultAction", "networkDefaultAction", "Network"),
        ("microsoft.storage/storageaccounts", "properties.isHnsEnabled", "hierarchicalNamespace", "Configuration"), ("microsoft.storage/storageaccounts", "properties.accessTier", "accessTier", "Capacity"),
        ("microsoft.storage/storageaccounts/blobservices/containers", "properties.publicAccess", "publicAccess", "Security"),
        // Databases
        ("microsoft.dbforpostgresql/flexibleservers", "properties.version", "version", "Data"), ("microsoft.dbforpostgresql/flexibleservers", "properties.state", "state", "Data"),
        ("microsoft.dbforpostgresql/flexibleservers", "properties.storage.storageSizeGB", "storageSizeGB", "Capacity"), ("microsoft.dbforpostgresql/flexibleservers", "properties.highAvailability.mode", "highAvailability", "Data"),
        ("microsoft.dbforpostgresql/flexibleservers", "properties.network.publicNetworkAccess", "publicNetworkAccess", "Network"),
        ("microsoft.dbforpostgresql/flexibleservers", "properties.network.delegatedSubnetResourceId", "delegatedSubnetResourceId", "Reference"),
        ("microsoft.dbforpostgresql/flexibleservers", "properties.authConfig.activeDirectoryAuth", "entraAuthentication", "Security"),
        ("microsoft.dbforpostgresql/flexibleservers", "properties.authConfig.passwordAuth", "passwordAuthentication", "Security"),
        ("microsoft.dbforpostgresql/flexibleservers", "properties.backup.geoRedundantBackup", "geoRedundantBackup", "Data"),
        ("microsoft.sql/servers", "properties.version", "version", "Data"), ("microsoft.sql/servers", "properties.administrators.azureADOnlyAuthentication", "entraOnlyAuthentication", "Security"),
        ("microsoft.sql/servers/databases", "properties.status", "status", "Data"), ("microsoft.sql/servers/databases", "properties.zoneRedundant", "zoneRedundant", "Capacity"),
        ("microsoft.sql/servers/databases", "properties.requestedBackupStorageRedundancy", "backupStorageRedundancy", "Data"),
        ("microsoft.documentdb/databaseaccounts", "properties.disableLocalAuth", "disableLocalAuth", "Security"),
        ("microsoft.cache/redis", "properties.enableNonSslPort", "enableNonSslPort", "Security"),
        // Key Vault (metadata only — never secrets, keys or certificates)
        ("microsoft.keyvault/vaults", "properties.enableRbacAuthorization", "enableRbacAuthorization", "Security"), ("microsoft.keyvault/vaults", "properties.enableSoftDelete", "enableSoftDelete", "Security"),
        ("microsoft.keyvault/vaults", "properties.enablePurgeProtection", "enablePurgeProtection", "Security"), ("microsoft.keyvault/vaults", "properties.softDeleteRetentionInDays", "softDeleteRetentionInDays", "Security"),
        ("microsoft.keyvault/vaults", "properties.networkAcls.defaultAction", "networkDefaultAction", "Network"), ("microsoft.keyvault/vaults", "properties.vaultUri", "vaultUri", "Configuration"),
        ("microsoft.keyvault/vaults", "properties.accessPolicies", "accessPolicies", "Security"),
        // Identity
        ("microsoft.managedidentity/userassignedidentities", "properties.principalId", "principalId", "Identity"),
        // Networking
        ("microsoft.network/virtualnetworks", "properties.addressSpace.addressPrefixes", "addressPrefixes", "Network"),
        ("microsoft.network/virtualnetworks/subnets", "properties.addressPrefix", "addressPrefix", "Network"),
        ("microsoft.network/virtualnetworks/subnets", "properties.networkSecurityGroup.id", "networkSecurityGroupId", "Reference"),
        ("microsoft.network/virtualnetworks/subnets", "properties.delegations[].properties.serviceName", "delegation", "Network"),
        ("microsoft.network/virtualnetworks/subnets", "properties.privateEndpointNetworkPolicies", "privateEndpointNetworkPolicies", "Network"),
        ("microsoft.network/privateendpoints", "properties.subnet.id", "subnetId", "Reference"),
        ("microsoft.network/privateendpoints", "properties.privateLinkServiceConnections[].properties.privateLinkServiceId", "privateLinkServiceId", "Reference"),
        ("microsoft.network/privateendpoints", "properties.manualPrivateLinkServiceConnections[].properties.privateLinkServiceId", "privateLinkServiceId", "Reference"),
        ("microsoft.network/privateendpoints", "properties.privateLinkServiceConnections[].properties.groupIds", "groupIds", "Network"),
        ("microsoft.network/privateendpoints", "properties.privateLinkServiceConnections[].properties.privateLinkServiceConnectionState.status", "connectionStatus", "Network"),
        ("microsoft.network/networksecuritygroups", "properties.securityRules", "securityRules", "Network"),
        ("microsoft.network/publicipaddresses", "properties.publicIPAllocationMethod", "allocation", "Network"),
        // Observability
        ("microsoft.insights/components", "properties.WorkspaceResourceId", "workspaceResourceId", "Reference"), ("microsoft.insights/components", "properties.IngestionMode", "ingestionMode", "Observability"),
        ("microsoft.insights/components", "properties.DisableLocalAuth", "disableLocalAuth", "Security"), ("microsoft.insights/components", "properties.RetentionInDays", "retentionInDays", "Observability"),
        ("microsoft.insights/components", "properties.Application_Type", "applicationType", "Observability"),
        ("microsoft.operationalinsights/workspaces", "properties.retentionInDays", "retentionInDays", "Observability"), ("microsoft.operationalinsights/workspaces", "properties.sku.name", "pricingTier", "Capacity"),
        ("microsoft.operationalinsights/workspaces", "properties.publicNetworkAccessForIngestion", "publicIngestion", "Network"),
        ("microsoft.operationalinsights/workspaces", "properties.publicNetworkAccessForQuery", "publicQuery", "Network"),
        ("microsoft.insights/metricalerts", "properties.enabled", "enabled", "Observability"), ("microsoft.insights/metricalerts", "properties.scopes", "scopes", "Reference"),
        ("microsoft.insights/scheduledqueryrules", "properties.enabled", "enabled", "Observability"), ("microsoft.insights/scheduledqueryrules", "properties.scopes", "scopes", "Reference"),
    ];

    /// <summary>Paths that are never read even if a future whitelist entry matched them (instrumentation keys, connection strings, credentials).</summary>
    private static readonly string[] NeverRead = ["InstrumentationKey", "ConnectionString", "connectionString", "administratorLoginPassword", "primaryKey", "secondaryKey", "customerId", "sharedKey"];

    /// <summary>The non-secret site configuration (GET sites/{name}/config/web). App settings and connection strings are separate,
    /// POST-only "list" operations that BirkNext never calls.</summary>
    private static readonly (string Path, string Key, string Area)[] SiteConfigPaths =
    [
        ("properties.minTlsVersion", "minTlsVersion", "Security"), ("properties.scmMinTlsVersion", "scmMinTlsVersion", "Security"), ("properties.ftpsState", "ftpsState", "Security"),
        ("properties.http20Enabled", "http20Enabled", "Configuration"), ("properties.alwaysOn", "alwaysOn", "Configuration"), ("properties.linuxFxVersion", "runtime", "Configuration"),
        ("properties.netFrameworkVersion", "netFrameworkVersion", "Configuration"), ("properties.healthCheckPath", "healthCheckPath", "Configuration"),
        ("properties.vnetRouteAllEnabled", "vnetRouteAllEnabled", "Network"), ("properties.publicNetworkAccess", "publicNetworkAccess", "Network"),
        ("properties.ipSecurityRestrictionsDefaultAction", "ipRestrictionDefaultAction", "Network"), ("properties.remoteDebuggingEnabled", "remoteDebuggingEnabled", "Security"),
    ];

    public static IEnumerable<ObservedProperty> SiteConfig(JsonElement body)
    {
        foreach (var (path, key, area) in SiteConfigPaths)
            foreach (var value in Values(body, path, key)) yield return new(key, value, area);
    }

    public static (InfrastructureCategory Category, string Label) Categorize(string type)
    {
        var t = type.ToLowerInvariant();
        foreach (var (prefix, category, label) in Categories)
            if (t.StartsWith(prefix, StringComparison.Ordinal)) return (category, label);
        return (InfrastructureCategory.Other, type);
    }

    public static IEnumerable<ObservedResource> Normalize(JsonElement item, string source, SourceEnvironmentLabel? scopeEnvironment)
    {
        if (item.ValueKind != JsonValueKind.Object || Str(item, "id") is not { Length: > 0 } id || Str(item, "type") is not { Length: > 0 } type) yield break;
        var resource = Build(item, id, type, source);
        yield return resource with { Environment = EnvironmentOf(resource, scopeEnvironment) };
        // Subnets are a virtual network's child objects: surface them as resources so endpoints, apps and servers can be linked to them.
        if (type.Equals("microsoft.network/virtualnetworks", StringComparison.OrdinalIgnoreCase) && Path(item, "properties.subnets") is { ValueKind: JsonValueKind.Array } subnets)
            foreach (var subnet in subnets.EnumerateArray())
                if (Str(subnet, "id") is { Length: > 0 } subnetId)
                {
                    var child = Build(subnet, subnetId, "microsoft.network/virtualnetworks/subnets", source) with
                    {
                        SubscriptionId = resource.SubscriptionId, ResourceGroup = resource.ResourceGroup, Location = resource.Location, ParentId = id,
                    };
                    yield return child with { Environment = resource.Environment };
                }
    }

    private static ObservedResource Build(JsonElement item, string id, string type, string source)
    {
        var (category, label) = Categorize(type);
        var lowerType = type.ToLowerInvariant();
        var properties = new List<ObservedProperty>();
        foreach (var (prefix, path, key, area) in Whitelist)
        {
            if (prefix.Length > 0 && lowerType != prefix) continue;
            if (NeverRead.Any(n => path.EndsWith("." + n, StringComparison.Ordinal))) continue;
            foreach (var value in Values(item, path, key))
                if (!properties.Any(p => p.Key == key && p.Value == value)) properties.Add(new(key, value, area));
        }
        if (lowerType == "microsoft.network/networksecuritygroups" && Path(item, "properties.securityRules") is { ValueKind: JsonValueKind.Array } rules)
        {
            // Inbound Allow rules whose source is any address: a count only (rule bodies are not stored).
            var open = rules.EnumerateArray().Count(r => Path(r, "properties") is { } p && Str(p, "direction") == "Inbound" && Str(p, "access") == "Allow"
                && Str(p, "sourceAddressPrefix") is "*" or "Internet" or "0.0.0.0/0" or "Any");
            properties.Add(new("inboundAllowFromAny", open.ToString(CultureInfo.InvariantCulture), "Network"));
        }
        var identity = item.TryGetProperty("identity", out var idn) && idn.ValueKind == JsonValueKind.Object ? idn : (JsonElement?)null;
        var userAssigned = identity is { } i && i.TryGetProperty("userAssignedIdentities", out var ua) && ua.ValueKind == JsonValueKind.Object
            ? ua.EnumerateObject().Select(p => p.Name).ToList() : [];
        return new ObservedResource
        {
            Id = id, Name = Str(item, "name") ?? AzureIds.Name(id), Type = lowerType, ArmKind = Str(item, "kind"), Category = category, CategoryDetail = KindLabel(label, lowerType, Str(item, "kind")),
            ResourceKind = InfrastructureIdentity.KindOfType(type), Location = Str(item, "location"),
            SubscriptionId = Str(item, "subscriptionId") ?? AzureIds.SubscriptionId(id) ?? "", ResourceGroup = Str(item, "resourceGroup") ?? AzureIds.ResourceGroup(id),
            ParentId = AzureIds.Parent(id), Sku = Sku(item), Tags = Tags(item), Properties = properties,
            IdentityType = identity is { } j ? Str(j, "type") : null, PrincipalId = identity is { } k ? Str(k, "principalId") : null, UserAssignedIdentityIds = userAssigned,
            Source = source,
        };
    }

    private static string KindLabel(string label, string type, string? kind) =>
        type == "microsoft.web/sites" && kind is not null && kind.Contains("functionapp", StringComparison.OrdinalIgnoreCase) ? "Function app"
        : type == "microsoft.web/sites" && kind is not null && kind.Contains("workflowapp", StringComparison.OrdinalIgnoreCase) ? "Logic app (Standard)"
        : label;

    private static SourceEnvironmentLabel? EnvironmentOf(ObservedResource r, SourceEnvironmentLabel? scope)
    {
        foreach (var tag in r.Tags.Where(t => t.Key.ToLowerInvariant() is "environment" or "env" or "stage" or "miljo" or "miljø"))
            if (SourceEnvironments.Normalize(tag.Value) is { Kind: not (SourceEnvironmentKind.Default or SourceEnvironmentKind.Custom) } tagged) return tagged;
        return SourceEnvironments.FromName(r.Name) ?? SourceEnvironments.FromName(r.ResourceGroup) ?? scope;
    }

    private static string? Sku(JsonElement item) =>
        item.TryGetProperty("sku", out var sku) && sku.ValueKind == JsonValueKind.Object
            ? string.Join(" ", new[] { Str(sku, "name"), Str(sku, "tier") is { } t && !string.Equals(t, Str(sku, "name"), StringComparison.OrdinalIgnoreCase) ? t : null, Num(sku, "capacity") is { } c ? $"×{c}" : null }
                .Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } label ? label : null
            : null;

    private static List<ObservedTag> Tags(JsonElement item)
    {
        if (!item.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object) return [];
        return tags.EnumerateObject().Take(40).Select(t =>
        {
            var key = SourceEvidenceRedaction.Safe(t.Name);
            var raw = t.Value.ValueKind == JsonValueKind.String ? t.Value.GetString() : t.Value.ToString();
            var (_, sensitivity, preview) = SourceEvidenceRedaction.Classify(t.Name, raw);
            return new ObservedTag(key, sensitivity == ConfigurationSensitivity.None && preview is not null ? preview : "[value not shown]");
        }).ToList();
    }

    private static IEnumerable<string> Values(JsonElement item, string path, string key)
    {
        foreach (var element in Elements(item, path.Split('.'), 0))
        {
            var value = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.Array when element.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String) =>
                    string.Join(", ", element.EnumerateArray().Select(e => e.GetString()).Take(8)) is { Length: > 0 } joined ? joined : "(none)",
                JsonValueKind.Array => $"{element.GetArrayLength()} item(s)",
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(value)) continue;
            value = value.Trim();
            if (SourceEvidenceRedaction.ConnectionShaped(value) || (SourceEvidenceRedaction.SecretShaped(value) && !value.StartsWith('/'))) continue;
            yield return value.Length > 300 ? value[..300] : value;
        }
    }

    private static IEnumerable<JsonElement> Elements(JsonElement current, string[] parts, int index)
    {
        if (index == parts.Length) { yield return current; yield break; }
        var part = parts[index];
        var flatten = part.EndsWith("[]", StringComparison.Ordinal);
        var name = flatten ? part[..^2] : part;
        if (current.ValueKind != JsonValueKind.Object || !TryGetIgnoreCase(current, name, out var next)) yield break;
        if (flatten && next.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in next.EnumerateArray())
                foreach (var e in Elements(child, parts, index + 1)) yield return e;
        }
        else if (!flatten)
            foreach (var e in Elements(next, parts, index + 1)) yield return e;
    }

    private static bool TryGetIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value)) return true;
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    public static JsonElement? Path(JsonElement item, string path) => Elements(item, path.Split('.'), 0).Cast<JsonElement?>().FirstOrDefault();

    public static string? Str(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && TryGetIgnoreCase(item, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Num(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble().ToString(CultureInfo.InvariantCulture) : null;
}
