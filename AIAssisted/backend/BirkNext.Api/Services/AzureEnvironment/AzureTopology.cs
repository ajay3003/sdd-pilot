using BirkNext.AzureEnvironment;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.AzureEnvironment;

/// <summary>
/// Relationships between observed resources. Confirmed = an Azure resource id on one resource names the other (containment, hosting plan,
/// subnet, private endpoint target, user-assigned identity, diagnostic destination, telemetry workspace, role assignment by principal id).
/// Inferred = a naming convention only (telemetry component and app sharing a name stem in one resource group), shown as such.
/// </summary>
public static class AzureTopologyBuilder
{
    private static readonly (string Key, ObservedRelationshipKind Kind, string Evidence)[] References =
    [
        ("serverFarmId", ObservedRelationshipKind.HostedOn, "serverFarmId"), ("managedEnvironmentId", ObservedRelationshipKind.HostedOn, "managedEnvironmentId"),
        ("virtualNetworkSubnetId", ObservedRelationshipKind.InSubnet, "virtualNetworkSubnetId (VNet integration)"), ("subnetId", ObservedRelationshipKind.InSubnet, "subnet.id"),
        ("delegatedSubnetResourceId", ObservedRelationshipKind.InSubnet, "network.delegatedSubnetResourceId"), ("infrastructureSubnetId", ObservedRelationshipKind.InSubnet, "vnetConfiguration.infrastructureSubnetId"),
        ("privateLinkServiceId", ObservedRelationshipKind.PrivateEndpointFor, "privateLinkServiceConnections.privateLinkServiceId"),
        ("workspaceResourceId", ObservedRelationshipKind.TelemetryWorkspace, "WorkspaceResourceId"), ("diagnosticsWorkspaceId", ObservedRelationshipKind.SendsDiagnosticsTo, "diagnostic setting workspaceId"),
        ("diagnosticsStorageAccountId", ObservedRelationshipKind.SendsDiagnosticsTo, "diagnostic setting storageAccountId"),
        ("diagnosticsEventHubRuleId", ObservedRelationshipKind.SendsDiagnosticsTo, "diagnostic setting eventHubAuthorizationRuleId"),
    ];

    private static readonly string[] StemPrefixes = ["appi-", "ai-", "appinsights-", "app-", "func-", "fn-", "ca-", "web-", "api-", "wa-"];

    public static List<ObservedRelationship> Build(IReadOnlyList<ObservedResource> resources, IReadOnlyList<ObservedRoleAssignment> assignments)
    {
        var byId = resources.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<ObservedRelationship>();
        void Add(string from, string to, ObservedRelationshipKind kind, ObservedRelationshipConfidence confidence, string evidence)
        {
            if (AzureIds.Same(from, to)) return;
            var target = Normalize(to, byId);
            if (!result.Any(r => AzureIds.Same(r.FromId, from) && AzureIds.Same(r.ToId, target) && r.Kind == kind))
                result.Add(new(from, target, kind, confidence, evidence, byId.ContainsKey(target)));
        }

        foreach (var r in resources)
        {
            if (r.ResourceGroup is { } rg && r.Type != "microsoft.resources/subscriptions/resourcegroups" && r.ParentId is null)
            {
                var rgId = AzureIds.ResourceGroupId(r.SubscriptionId, rg);
                if (byId.ContainsKey(rgId)) Add(rgId, r.Id, ObservedRelationshipKind.Contains, ObservedRelationshipConfidence.Confirmed, "resource id");
            }
            if (r.ParentId is { } parent) Add(r.Id, parent, ObservedRelationshipKind.ChildOf, ObservedRelationshipConfidence.Confirmed, "resource id");
            foreach (var (key, kind, evidence) in References)
                foreach (var p in r.Properties.Where(p => p.Key == key && p.Value.StartsWith('/')))
                    Add(r.Id, p.Value, kind, ObservedRelationshipConfidence.Confirmed, evidence);
            foreach (var identity in r.UserAssignedIdentityIds) Add(r.Id, identity, ObservedRelationshipKind.UsesIdentity, ObservedRelationshipConfidence.Confirmed, "identity.userAssignedIdentities");
        }

        // A principal id that is a resource's managed identity (system-assigned on the resource, or a user-assigned identity resource).
        var principals = resources.Where(r => r.PrincipalId is not null).Select(r => (r.PrincipalId!, r.Id))
            .Concat(resources.Where(r => r.ResourceKind == InfrastructureResourceKind.ManagedIdentity).SelectMany(r => r.Properties.Where(p => p.Key == "principalId").Select(p => (p.Value, r.Id))))
            .GroupBy(p => p.Item1, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.OrdinalIgnoreCase);
        foreach (var a in assignments)
            if ((a.PrincipalResourceId ?? (principals.TryGetValue(a.PrincipalId, out var owner) ? owner : null)) is { } from)
                Add(from, a.Scope, ObservedRelationshipKind.RoleAssignedOn, ObservedRelationshipConfidence.Confirmed, $"role assignment: {a.RoleName}");

        // Inferred: a telemetry component and an app in the same resource group with the same name stem, unless a confirmed link exists.
        foreach (var component in resources.Where(r => r.ResourceKind == InfrastructureResourceKind.TelemetryComponent))
        {
            var stem = Stem(component.Name);
            if (stem.Length < 3) continue;
            foreach (var app in resources.Where(r => r.ResourceKind == InfrastructureResourceKind.ComputeApp && string.Equals(r.ResourceGroup, component.ResourceGroup, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(Stem(r.Name), stem, StringComparison.OrdinalIgnoreCase)))
                Add(component.Id, app.Id, ObservedRelationshipKind.LikelyTelemetryFor, ObservedRelationshipConfidence.Inferred,
                    $"names share \"{stem}\" in one resource group; app settings are not read, so the connection is not confirmed");
        }
        return result;
    }

    /// <summary>Normalizes a referenced id to the observed resource's own casing when it is in scope.</summary>
    private static string Normalize(string id, Dictionary<string, ObservedResource> byId) => byId.TryGetValue(id.TrimEnd('/'), out var r) ? r.Id : id.TrimEnd('/');

    internal static string Stem(string name)
    {
        var n = name.ToLowerInvariant();
        foreach (var prefix in StemPrefixes)
            if (n.StartsWith(prefix, StringComparison.Ordinal)) { n = n[prefix.Length..]; break; }
        foreach (var suffix in new[] { "-appi", "-ai", "-insights", "-app", "-func", "-web", "-api" })
            if (n.EndsWith(suffix, StringComparison.Ordinal)) { n = n[..^suffix.Length]; break; }
        return n;
    }
}

/// <summary>Neutral observations a review weighs (public exposure, local auth, TLS, purge protection, missing diagnostics). Never Pass/Fail.</summary>
public static class AzureObservations
{
    private static readonly InfrastructureResourceKind[] Monitorable =
    [
        InfrastructureResourceKind.ComputeApp, InfrastructureResourceKind.EventHubNamespace, InfrastructureResourceKind.ServiceBusNamespace, InfrastructureResourceKind.StorageAccount,
        InfrastructureResourceKind.DatabaseServer, InfrastructureResourceKind.SecretStore, InfrastructureResourceKind.ApiGateway, InfrastructureResourceKind.Cache,
    ];

    public static bool IsMonitorable(ObservedResource r) => Monitorable.Contains(r.ResourceKind) && r.ParentId is null;

    public static List<AzureObservation> Build(IReadOnlyList<ObservedResource> resources, IReadOnlyList<ObservedRelationship> relationships)
    {
        var list = new List<AzureObservation>();
        void Add(ObservedResource r, ObservationArea area, string key, string statement, bool review) => list.Add(new(area, r.Id, r.Name, key, statement, review));
        string? P(ObservedResource r, string key) => r.Properties.FirstOrDefault(p => p.Key == key)?.Value;
        bool Is(ObservedResource r, string key, string value) => string.Equals(P(r, key), value, StringComparison.OrdinalIgnoreCase);

        foreach (var r in resources)
        {
            var hasPrivateEndpoint = relationships.Any(x => x.Kind == ObservedRelationshipKind.PrivateEndpointFor && AzureIds.Same(x.ToId, r.Id));
            if (Is(r, "publicNetworkAccess", "Enabled") && r.Category is InfrastructureCategory.Database or InfrastructureCategory.Messaging or InfrastructureCategory.SecretStore or InfrastructureCategory.Storage or InfrastructureCategory.Cache)
                Add(r, ObservationArea.Network, "publicNetworkAccess", hasPrivateEndpoint ? "Public network access is enabled alongside a private endpoint." : "Public network access is enabled.", true);
            if (hasPrivateEndpoint) Add(r, ObservationArea.Network, "privateEndpoint", "Reachable through a private endpoint.", false);
            if (Is(r, "allowBlobPublicAccess", "true")) Add(r, ObservationArea.Security, "allowBlobPublicAccess", "Anonymous blob access can be enabled on containers (allowBlobPublicAccess).", true);
            if (Is(r, "publicAccess", "Blob") || Is(r, "publicAccess", "Container")) Add(r, ObservationArea.Security, "publicAccess", $"Container allows anonymous read ({P(r, "publicAccess")}).", true);
            if (Is(r, "allowSharedKeyAccess", "true")) Add(r, ObservationArea.Security, "allowSharedKeyAccess", "Shared-key (account key) authorization is allowed.", false);
            if (Is(r, "supportsHttpsTrafficOnly", "false") || Is(r, "httpsOnly", "false")) Add(r, ObservationArea.Security, "httpsOnly", "HTTP (non-TLS) traffic is accepted.", true);
            if (Is(r, "disableLocalAuth", "false")) Add(r, ObservationArea.Security, "disableLocalAuth", "Local (SAS / key) authentication is enabled.", false);
            if (Is(r, "passwordAuthentication", "Enabled")) Add(r, ObservationArea.Security, "passwordAuthentication", "Password authentication is enabled.", false);
            if (Is(r, "enableNonSslPort", "true")) Add(r, ObservationArea.Security, "enableNonSslPort", "The non-TLS port is enabled.", true);
            if (Is(r, "allowInsecure", "true")) Add(r, ObservationArea.Security, "allowInsecure", "Ingress allows insecure (HTTP) connections.", true);
            if (Is(r, "ftpsState", "AllAllowed")) Add(r, ObservationArea.Security, "ftpsState", "FTP (unencrypted) deployment is allowed.", true);
            foreach (var key in new[] { "minimumTlsVersion", "minTlsVersion" })
                if (P(r, key) is { } tls && TlsBelow12(tls)) Add(r, ObservationArea.Security, key, $"Minimum TLS version is {tls}.", true);
            if (r.ResourceKind == InfrastructureResourceKind.SecretStore)
            {
                if (Is(r, "enablePurgeProtection", "false") || (r.Properties.All(p => p.Key != "enablePurgeProtection") && r.Properties.Count > 0))
                    Add(r, ObservationArea.Security, "enablePurgeProtection", "Purge protection is not enabled.", false);
                if (Is(r, "enableRbacAuthorization", "false")) Add(r, ObservationArea.Identity, "enableRbacAuthorization", "Uses vault access policies rather than Azure RBAC.", false);
            }
            if (P(r, "inboundAllowFromAny") is { } open && open != "0") Add(r, ObservationArea.Network, "inboundAllowFromAny", $"{open} inbound rule(s) allow traffic from any source.", true);
            if (r.ResourceKind == InfrastructureResourceKind.TelemetryComponent && P(r, "workspaceResourceId") is null && r.Properties.Count > 0)
                Add(r, ObservationArea.Observability, "workspace", "Not linked to a Log Analytics workspace (classic Application Insights).", false);
            if (r.IdentityType is { } identity && !identity.Equals("None", StringComparison.OrdinalIgnoreCase))
                Add(r, ObservationArea.Identity, "identity", $"Managed identity: {identity}.", false);
            // Read per resource: "0" = read and none found; absent = not read (not authorized, not monitorable or over the read budget) — nothing is claimed.
            if (P(r, "diagnosticSettings") is { } count)
                Add(r, ObservationArea.Observability, "diagnosticSettings", count == "0" ? "No diagnostic setting was observed." : $"{count} diagnostic setting(s) observed.", count == "0");
        }
        return list;
    }

    private static bool TlsBelow12(string value)
    {
        var v = value.Replace("TLS", "", StringComparison.OrdinalIgnoreCase).Replace('_', '.').Trim('.', ' ');
        return v is "1.0" or "1" or "1.1";
    }
}
