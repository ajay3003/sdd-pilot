using System.Text.Json;
using BirkNext.Api.Services.AzureEnvironment;
using BirkNext.AzureEnvironment;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Tests.Services.AzureEnvironment;

/// <summary>
/// A fake Azure Resource Manager / Resource Graph. Every GET is checked against the read-only policy (a refused path fails the test), every
/// request is recorded, and routes can be overridden per test (403, 429, 401, paging). Unknown list routes answer an empty list.
/// </summary>
internal sealed class FakeAzureManagementClient : IAzureManagementClient
{
    public List<string> Gets { get; } = [];
    public List<(string Query, IReadOnlyList<string> Subscriptions, string? SkipToken)> Queries { get; } = [];
    public Dictionary<string, Func<ArmResult>> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(Func<string, bool> When, ArmResult Result)> Overrides { get; } = [];
    public Func<AzureGraphQuery, string?, ArmResult>? Graph { get; set; }

    public Task<ArmResult> GetAsync(string relativeUrl, CancellationToken ct)
    {
        if (AzureReadOnlyPolicy.CheckGet(relativeUrl) is { } refused) throw new InvalidOperationException($"Collector sent a GET outside the read-only policy: {relativeUrl} ({refused})");
        Gets.Add(relativeUrl);
        foreach (var (when, result) in Overrides) if (when(relativeUrl)) return Task.FromResult(result);
        if (Routes.TryGetValue(relativeUrl, out var exact)) return Task.FromResult(exact());
        var path = relativeUrl.Split('?')[0];
        if (Routes.TryGetValue(path, out var route)) return Task.FromResult(route());
        return Task.FromResult(path.EndsWith("/config/web", StringComparison.OrdinalIgnoreCase) ? new ArmResult(404, null, "NotFound") : Ok(new { value = Array.Empty<object>() }));
    }

    public Task<ArmResult> QueryAsync(AzureGraphQuery query, IReadOnlyList<string> subscriptionIds, string? skipToken, CancellationToken ct)
    {
        Queries.Add((query.Name, subscriptionIds, skipToken));
        foreach (var (when, result) in Overrides) if (when("graph:" + query.Name)) return Task.FromResult(result);
        return Task.FromResult(Graph?.Invoke(query, skipToken) ?? Ok(new { data = Array.Empty<object>() }));
    }

    public static ArmResult Ok(object body) => new(200, JsonSerializer.SerializeToElement(body), null);
    public static ArmResult Status(int status, string code = "AuthorizationFailed") => new(status, null, code);
}

internal sealed class FakeSignIn(AzureConnectionState state = AzureConnectionState.SignedIn) : IAzureSignInService
{
    public AzureConnectionState State { get; set; } = state;
    public int Refreshes { get; private set; }
    public bool Expired { get; private set; }
    public AzureConnectionStatus Status() => new() { State = State, Method = AzureSignInMethod.DedicatedEdgeProfile, TenantId = AzureEnvironmentFixtures.Tenant };
    public AzureConnectionStatus StartInteractive() => Status();
    public AzureConnectionStatus StartDeviceCode() => Status();
    public Task<AzureConnectionStatus> RefreshAsync(CancellationToken ct) { Refreshes++; return Task.FromResult(Status()); }
    public Task<AzureConnectionStatus> SignOutAsync() { State = AzureConnectionState.SignedOut; return Task.FromResult(Status()); }
    public Task<string?> AccessTokenAsync(bool forceRefresh, CancellationToken ct) { if (forceRefresh) Refreshes++; return Task.FromResult<string?>(State == AzureConnectionState.SignedIn ? "fake-token" : null); }
    public void MarkExpired(string reason) { Expired = true; State = AzureConnectionState.Expired; }
    public AzureSignInMethod Method => AzureSignInMethod.DedicatedEdgeProfile;
}

/// <summary>
/// Generic "Contoso Orders" DEV environment in one subscription and resource group: App Service app on a plan with a user-assigned identity and
/// VNet integration, Event Hubs (hub + consumer group), Service Bus (topic + subscription), storage (+ container), PostgreSQL flexible server
/// (+ database) behind a private endpoint, Key Vault, Application Insights on a Log Analytics workspace, an NSG with one open inbound rule,
/// diagnostic settings on the Event Hubs namespace, role assignments and the signed-in person's PIM roles. No project-specific names.
/// Secret-shaped values are planted (SECRET_SENTINEL_*) on paths a careless reader might copy; none may reach the snapshot.
/// </summary>
internal static class AzureEnvironmentFixtures
{
    public const string Tenant = "aaaaaaaa-0000-0000-0000-000000000001";
    public const string Sub = "11111111-1111-1111-1111-111111111111";
    public const string Sub2 = "22222222-2222-2222-2222-222222222222";
    public const string Rg = "rg-orders-dev";
    public const string IdentityPrincipal = "33333333-3333-3333-3333-333333333333";
    public const string SitePrincipal = "44444444-4444-4444-4444-444444444444";
    public const string Sentinel = "SECRET_SENTINEL";

    public static string RgId(string sub = Sub, string rg = Rg) => $"/subscriptions/{sub}/resourceGroups/{rg}";
    public static string Id(string type, string name, string sub = Sub, string rg = Rg) => $"{RgId(sub, rg)}/providers/{type}/{name}";

    public static string Site => Id("Microsoft.Web/sites", "app-orders-dev");
    public static string Plan => Id("Microsoft.Web/serverfarms", "plan-orders-dev");
    public static string EventHubs => Id("Microsoft.EventHub/namespaces", "evhns-orders-dev");
    public static string Hub => $"{EventHubs}/eventhubs/order-events";
    public static string ServiceBus => Id("Microsoft.ServiceBus/namespaces", "sbns-orders-dev");
    public static string Topic => $"{ServiceBus}/topics/order-placed";
    public static string Storage => Id("Microsoft.Storage/storageAccounts", "stordersdevckpt");
    public static string Postgres => Id("Microsoft.DBforPostgreSQL/flexibleServers", "psql-orders-dev");
    public static string Vault => Id("Microsoft.KeyVault/vaults", "kv-orders-dev");
    public static string Identity => Id("Microsoft.ManagedIdentity/userAssignedIdentities", "id-orders-dev");
    public static string Vnet => Id("Microsoft.Network/virtualNetworks", "vnet-orders-dev");
    public static string AppSubnet => $"{Vnet}/subnets/snet-app";
    public static string PeSubnet => $"{Vnet}/subnets/snet-pe";
    public static string PrivateEndpoint => Id("Microsoft.Network/privateEndpoints", "pe-orders-psql-dev");
    public static string Insights => Id("Microsoft.Insights/components", "appi-orders-dev");
    public static string Workspace => Id("Microsoft.OperationalInsights/workspaces", "log-orders-dev");
    public static string Nsg => Id("Microsoft.Network/networkSecurityGroups", "nsg-orders-dev");

    public static object[] GraphResources(string sub = Sub) =>
    [
        R(Site, "microsoft.web/sites", "app-orders-dev", kind: "app,linux", identity: new { type = "SystemAssigned, UserAssigned", principalId = SitePrincipal,
                userAssignedIdentities = new Dictionary<string, object> { [Identity] = new { principalId = IdentityPrincipal } } },
            properties: new { state = "Running", httpsOnly = true, defaultHostName = "app-orders-dev.azurewebsites.net", serverFarmId = Plan, virtualNetworkSubnetId = AppSubnet,
                siteConfig = new { appSettings = new[] { new { name = "Db", value = $"Host=x;Password={Sentinel}_APPSETTING" } } } },
            tags: new Dictionary<string, string> { ["environment"] = "dev", ["owner-token"] = $"{Sentinel}_TAG_eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abc" }),
        R(Plan, "microsoft.web/serverfarms", "plan-orders-dev", sku: new { name = "P1v3", tier = "PremiumV3", capacity = 1 }, properties: new { numberOfWorkers = 1, reserved = true }),
        R(EventHubs, "microsoft.eventhub/namespaces", "evhns-orders-dev", sku: new { name = "Standard", tier = "Standard", capacity = 1 },
            properties: new { disableLocalAuth = false, publicNetworkAccess = "Enabled", minimumTlsVersion = "1.2", serviceBusEndpoint = "https://evhns-orders-dev.servicebus.windows.net:443/", kafkaEnabled = true }),
        R(ServiceBus, "microsoft.servicebus/namespaces", "sbns-orders-dev", sku: new { name = "Standard", tier = "Standard" },
            properties: new { disableLocalAuth = true, publicNetworkAccess = "Disabled", minimumTlsVersion = "1.2", serviceBusEndpoint = "https://sbns-orders-dev.servicebus.windows.net:443/" }),
        R(Storage, "microsoft.storage/storageaccounts", "stordersdevckpt", kind: "StorageV2", sku: new { name = "Standard_LRS", tier = "Standard" },
            properties: new { supportsHttpsTrafficOnly = true, allowBlobPublicAccess = false, allowSharedKeyAccess = true, minimumTlsVersion = "TLS1_2", publicNetworkAccess = "Enabled",
                networkAcls = new { defaultAction = "Allow" }, primaryEndpoints = new { blob = "https://stordersdevckpt.blob.core.windows.net/" } }),
        R(Postgres, "microsoft.dbforpostgresql/flexibleservers", "psql-orders-dev", sku: new { name = "Standard_B1ms", tier = "Burstable" },
            properties: new { version = "16", state = "Ready", administratorLogin = "pgadmin", administratorLoginPassword = $"{Sentinel}_PG",
                network = new { publicNetworkAccess = "Disabled" }, authConfig = new { activeDirectoryAuth = "Enabled", passwordAuth = "Disabled" }, storage = new { storageSizeGB = 32 } }),
        R(Vault, "microsoft.keyvault/vaults", "kv-orders-dev", properties: new { enableRbacAuthorization = true, enableSoftDelete = true, softDeleteRetentionInDays = 90,
            vaultUri = "https://kv-orders-dev.vault.azure.net/", publicNetworkAccess = "Enabled", networkAcls = new { defaultAction = "Deny" } }),
        R(Identity, "microsoft.managedidentity/userassignedidentities", "id-orders-dev", properties: new { principalId = IdentityPrincipal, clientId = "55555555-5555-5555-5555-555555555555" }),
        R(Vnet, "microsoft.network/virtualnetworks", "vnet-orders-dev", properties: new
        {
            addressSpace = new { addressPrefixes = new[] { "10.20.0.0/16" } },
            subnets = new object[]
            {
                new { id = AppSubnet, name = "snet-app", properties = new { addressPrefix = "10.20.1.0/24", networkSecurityGroup = new { id = Nsg }, delegations = new[] { new { properties = new { serviceName = "Microsoft.Web/serverFarms" } } } } },
                new { id = PeSubnet, name = "snet-pe", properties = new { addressPrefix = "10.20.2.0/24", privateEndpointNetworkPolicies = "Disabled" } },
            },
        }),
        R(PrivateEndpoint, "microsoft.network/privateendpoints", "pe-orders-psql-dev", properties: new
        {
            subnet = new { id = PeSubnet },
            privateLinkServiceConnections = new[] { new { name = "psql", properties = new { privateLinkServiceId = Postgres, groupIds = new[] { "postgresqlServer" }, privateLinkServiceConnectionState = new { status = "Approved" } } } },
        }),
        R(Insights, "microsoft.insights/components", "appi-orders-dev", kind: "web",
            properties: new { WorkspaceResourceId = Workspace, IngestionMode = "LogAnalytics", InstrumentationKey = $"{Sentinel}_IKEY", ConnectionString = $"InstrumentationKey={Sentinel}_CS;IngestionEndpoint=https://x/" }),
        R(Workspace, "microsoft.operationalinsights/workspaces", "log-orders-dev", properties: new { retentionInDays = 30, sku = new { name = "PerGB2018" }, customerId = "66666666-6666-6666-6666-666666666666" }),
        R(Nsg, "microsoft.network/networksecuritygroups", "nsg-orders-dev", properties: new
        {
            securityRules = new object[]
            {
                new { name = "allow-https", properties = new { direction = "Inbound", access = "Allow", sourceAddressPrefix = "Internet", destinationPortRange = "443" } },
                new { name = "allow-vnet", properties = new { direction = "Inbound", access = "Allow", sourceAddressPrefix = "VirtualNetwork", destinationPortRange = "*" } },
            },
        }),
    ];

    public static object[] GraphGroups(string sub = Sub) =>
        [new { id = RgId(sub), name = Rg, type = "microsoft.resources/subscriptions/resourcegroups", location = "norwayeast", resourceGroup = Rg, subscriptionId = sub, tags = new { environment = "dev" } }];

    private static object R(string id, string type, string name, string? kind = null, object? sku = null, object? identity = null, object? properties = null, object? tags = null) =>
        new { id, name, type, kind, location = "norwayeast", resourceGroup = Rg, subscriptionId = AzureIds.SubscriptionId(id), tags, sku, identity, properties };

    /// <summary>A fully readable environment: the signed-in person is a Reader (or Owner when <paramref name="owner"/>) with one eligible and one active PIM role.</summary>
    public static FakeAzureManagementClient Full(bool owner = false)
    {
        var fake = new FakeAzureManagementClient();
        fake.Graph = (q, _) => FakeAzureManagementClient.Ok(new { data = q == AzureGraphQuery.Resources ? GraphResources() : GraphGroups() });
        fake.Routes["/subscriptions"] = () => FakeAzureManagementClient.Ok(new { value = new[] { new { subscriptionId = Sub, displayName = "Contoso Orders Dev", state = "Enabled", tenantId = Tenant } } });
        fake.Routes[$"/subscriptions/{Sub}/providers/Microsoft.Authorization/permissions"] = () => FakeAzureManagementClient.Ok(new
        { value = new[] { new { actions = owner ? new[] { "*" } : new[] { "*/read" }, notActions = Array.Empty<string>() } } });
        fake.Routes[$"{EventHubs}/eventhubs"] = () => FakeAzureManagementClient.Ok(new { value = new[]
            { new { id = Hub, name = "order-events", type = "Microsoft.EventHub/namespaces/eventhubs", properties = new { partitionCount = 2, messageRetentionInDays = 1, status = "Active" } } } });
        fake.Routes[$"{Hub}/consumergroups"] = () => FakeAzureManagementClient.Ok(new { value = new[]
            { new { id = $"{Hub}/consumergroups/fulfillment-dev", name = "fulfillment-dev", type = "Microsoft.EventHub/namespaces/eventhubs/consumergroups", properties = new { } } } });
        fake.Routes[$"{ServiceBus}/topics"] = () => FakeAzureManagementClient.Ok(new { value = new[]
            { new { id = Topic, name = "order-placed", type = "Microsoft.ServiceBus/namespaces/topics", properties = new { status = "Active", maxSizeInMegabytes = 1024 } } } });
        fake.Routes[$"{Topic}/subscriptions"] = () => FakeAzureManagementClient.Ok(new { value = new[]
            { new { id = $"{Topic}/subscriptions/billing", name = "billing", type = "Microsoft.ServiceBus/namespaces/topics/subscriptions", properties = new { maxDeliveryCount = 10, status = "Active" } } } });
        fake.Routes[$"{Storage}/blobServices/default/containers"] = () => FakeAzureManagementClient.Ok(new { value = new[]
            { new { id = $"{Storage}/blobServices/default/containers/checkpoints", name = "checkpoints", type = "Microsoft.Storage/storageAccounts/blobServices/containers", properties = new { publicAccess = "None" } } } });
        fake.Routes[$"{Postgres}/databases"] = () => FakeAzureManagementClient.Ok(new { value = new object[]
        {
            new { id = $"{Postgres}/databases/orders", name = "orders", type = "Microsoft.DBforPostgreSQL/flexibleServers/databases", properties = new { charset = "UTF8" } },
            new { id = $"{Postgres}/databases/azure_maintenance", name = "azure_maintenance", type = "Microsoft.DBforPostgreSQL/flexibleServers/databases", properties = new { } },
        } });
        fake.Routes[$"{Site}/config/web"] = () => FakeAzureManagementClient.Ok(new { id = $"{Site}/config/web", properties = new { minTlsVersion = "1.2", ftpsState = "FtpsOnly", alwaysOn = true, linuxFxVersion = "DOTNETCORE|8.0",
            publishingUsername = "$app-orders-dev", appSettings = (object?)null } });
        fake.Routes[$"{EventHubs}/providers/Microsoft.Insights/diagnosticSettings"] = () => FakeAzureManagementClient.Ok(new { value = new[]
            { new { id = $"{EventHubs}/providers/microsoft.insights/diagnosticSettings/to-log", name = "to-log", properties = new { workspaceId = Workspace, logs = new[] { new { category = "OperationalLogs", enabled = true } } } } } });
        fake.Routes[$"/subscriptions/{Sub}/providers/Microsoft.Authorization/roleDefinitions"] = () => FakeAzureManagementClient.Ok(new { value = new[]
        {
            new { id = $"/subscriptions/{Sub}/providers/Microsoft.Authorization/roleDefinitions/a638d3c7-ab3a-418d-83e6-5f17a39d4fde", properties = new { roleName = "Azure Event Hubs Data Receiver" } },
            new { id = $"/subscriptions/{Sub}/providers/Microsoft.Authorization/roleDefinitions/acdd72a7-3385-48ef-bd42-f606fba81ae7", properties = new { roleName = "Reader" } },
        } });
        fake.Routes[$"/subscriptions/{Sub}/providers/Microsoft.Authorization/roleAssignments"] = () => FakeAzureManagementClient.Ok(new { value = new[]
        {
            new { id = "ra1", properties = new { scope = EventHubs, roleDefinitionId = "/providers/Microsoft.Authorization/roleDefinitions/a638d3c7-ab3a-418d-83e6-5f17a39d4fde", principalId = IdentityPrincipal, principalType = "ServicePrincipal" } },
            new { id = "ra2", properties = new { scope = $"/subscriptions/{Sub}", roleDefinitionId = "/providers/Microsoft.Authorization/roleDefinitions/acdd72a7-3385-48ef-bd42-f606fba81ae7", principalId = "77777777-7777-7777-7777-777777777777", principalType = "Group" } },
        } });
        fake.Routes[$"/subscriptions/{Sub}/providers/Microsoft.Authorization/roleAssignmentScheduleInstances?api-version=2020-10-01&$filter=asTarget()"] = () => FakeAzureManagementClient.Ok(new { value = new[]
            { new { properties = new { scope = $"/subscriptions/{Sub}", memberType = "Group", expandedProperties = new { roleDefinition = new { displayName = "Reader" } } } } } });
        fake.Routes[$"/subscriptions/{Sub}/providers/Microsoft.Authorization/roleEligibilityScheduleInstances?api-version=2020-10-01&$filter=asTarget()"] = () => FakeAzureManagementClient.Ok(new { value = new[]
            { new { properties = new { scope = $"/subscriptions/{Sub}", memberType = "Group", endDateTime = "2027-01-01T00:00:00Z", expandedProperties = new { roleDefinition = new { displayName = "Owner" } } } } } });
        return fake;
    }

    public static AzureEnvironmentCollector Collector(FakeAzureManagementClient fake, FakeSignIn? signIn = null, AzureEnvironmentOptions? options = null) =>
        new(fake, signIn ?? new FakeSignIn(), Options.Create(options ?? new AzureEnvironmentOptions { Enabled = true, ClientId = "00000000-0000-0000-0000-00000000c11e" }),
            NullLogger<AzureEnvironmentCollector>.Instance, new FixedClock());

    public static AzureAnalysisRequest Request(params string[] groups) => new() { EnvironmentId = "orders-dev", SubscriptionIds = [Sub], ResourceGroups = [.. groups], EnvironmentLabel = "dev" };

    internal sealed class FixedClock : TimeProvider
    {
        public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-02T09:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
