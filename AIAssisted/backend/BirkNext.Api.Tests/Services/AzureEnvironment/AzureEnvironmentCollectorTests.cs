using System.Text.Json;
using BirkNext.Api.Services.AzureEnvironment;
using BirkNext.AzureEnvironment;
using BirkNext.SourceDomains;
using FluentAssertions;
using static BirkNext.Api.Tests.Services.AzureEnvironment.AzureEnvironmentFixtures;

namespace BirkNext.Api.Tests.Services.AzureEnvironment;

/// <summary>
/// The collector over a fake Azure: full, Reader-only, partial (403 per area), Resource Graph unavailable (ARM fallback), throttled, expired
/// sign-in and paging. Observed is inventory and metadata — never Pass/Fail — and a 403 is Not authorized, never Failed.
/// </summary>
public sealed class AzureEnvironmentCollectorTests
{
    private static async Task<AzureEnvironmentSnapshot> Collect(FakeAzureManagementClient fake, FakeSignIn? signIn = null, params string[] groups) =>
        await Collector(fake, signIn).CollectAsync(Request(groups), default);

    private static ObservedResource One(AzureEnvironmentSnapshot s, string id) => s.Resources.Single(r => AzureIds.Same(r.Id, id));
    private static string? P(ObservedResource r, string key) => r.Properties.FirstOrDefault(p => p.Key == key)?.Value;

    [Fact]
    public async Task Full_access_yields_a_complete_inventory_with_provider_neutral_kinds_and_child_resources()
    {
        var s = await Collect(Full());

        s.Status.Should().Be(AzureAnalysisStatus.Complete);
        s.Subscriptions.Should().ContainSingle(x => x.DisplayName == "Contoso Orders Dev");
        One(s, Site).Should().Match<ObservedResource>(r => r.ResourceKind == InfrastructureResourceKind.ComputeApp && r.Category == InfrastructureCategory.Compute && r.CategoryDetail == "App Service app");
        One(s, EventHubs).ResourceKind.Should().Be(InfrastructureResourceKind.EventHubNamespace);
        One(s, ServiceBus).ResourceKind.Should().Be(InfrastructureResourceKind.ServiceBusNamespace);
        One(s, Postgres).ResourceKind.Should().Be(InfrastructureResourceKind.DatabaseServer);
        One(s, Vault).Category.Should().Be(InfrastructureCategory.SecretStore);
        One(s, AppSubnet).Should().Match<ObservedResource>(r => r.ParentId == Vnet && r.Category == InfrastructureCategory.Networking);

        // Detail reads: hub + consumer group, topic + subscription, container, database (system database skipped), site config.
        One(s, Hub).Should().Match<ObservedResource>(r => r.ResourceKind == InfrastructureResourceKind.EventHub && P(r, "partitionCount") == "2" && r.ParentId == EventHubs);
        s.Resources.Should().Contain(r => r.ResourceKind == InfrastructureResourceKind.ConsumerGroup && r.Name == "fulfillment-dev" && AzureIds.Same(r.ParentId, Hub));
        s.Resources.Should().Contain(r => r.ResourceKind == InfrastructureResourceKind.ServiceBusSubscription && r.Name == "billing");
        s.Resources.Should().Contain(r => r.ResourceKind == InfrastructureResourceKind.BlobContainer && r.Name == "checkpoints" && AzureIds.Same(r.ParentId, Storage));
        s.Resources.Should().Contain(r => r.ResourceKind == InfrastructureResourceKind.Database && r.Name == "orders").And.NotContain(r => r.Name == "azure_maintenance");
        P(One(s, Site), "minTlsVersion").Should().Be("1.2");
        P(One(s, Site), "runtime").Should().Be("DOTNETCORE|8.0");
        One(s, Site).Environment!.Kind.Should().Be(SourceEnvironmentKind.Development);

        s.Capabilities.Where(c => c.Area is AzureCapabilityArea.Messaging or AzureCapabilityArea.Storage or AzureCapabilityArea.Databases or AzureCapabilityArea.Compute
            or AzureCapabilityArea.ResourceGraph or AzureCapabilityArea.RoleAssignments or AzureCapabilityArea.PimRoles).Should().OnlyContain(c => c.State == AzureCapabilityState.Available);
        s.Capabilities.Single(c => c.Area == AzureCapabilityArea.KeyVaultMetadata).Detail.Should().Contain("secrets, keys and certificates are never read");
        s.Resources.Should().OnlyContain(r => r.ObservedState == AzureEnvironmentText.ObservedNotVerified);
    }

    [Fact]
    public async Task Topology_confirms_relationships_from_resource_ids_and_marks_naming_links_inferred()
    {
        var s = await Collect(Full());
        bool Has(string from, string to, ObservedRelationshipKind kind, ObservedRelationshipConfidence c = ObservedRelationshipConfidence.Confirmed) =>
            s.Relationships.Any(r => AzureIds.Same(r.FromId, from) && AzureIds.Same(r.ToId, to) && r.Kind == kind && r.Confidence == c);

        Has(RgId(), Site, ObservedRelationshipKind.Contains).Should().BeTrue();
        Has(Site, Plan, ObservedRelationshipKind.HostedOn).Should().BeTrue();
        Has(Site, AppSubnet, ObservedRelationshipKind.InSubnet).Should().BeTrue();
        Has(Site, Identity, ObservedRelationshipKind.UsesIdentity).Should().BeTrue();
        Has(PrivateEndpoint, Postgres, ObservedRelationshipKind.PrivateEndpointFor).Should().BeTrue();
        Has(PrivateEndpoint, PeSubnet, ObservedRelationshipKind.InSubnet).Should().BeTrue();
        Has(Insights, Workspace, ObservedRelationshipKind.TelemetryWorkspace).Should().BeTrue();
        Has(EventHubs, Workspace, ObservedRelationshipKind.SendsDiagnosticsTo).Should().BeTrue();
        Has(Hub, EventHubs, ObservedRelationshipKind.ChildOf).Should().BeTrue();
        Has(Identity, EventHubs, ObservedRelationshipKind.RoleAssignedOn).Should().BeTrue("the identity's principal id holds Azure Event Hubs Data Receiver on the namespace");
        s.Relationships.Single(r => r.Kind == ObservedRelationshipKind.RoleAssignedOn).Evidence.Should().Contain("Azure Event Hubs Data Receiver");
        Has(Insights, Site, ObservedRelationshipKind.LikelyTelemetryFor, ObservedRelationshipConfidence.Inferred).Should().BeTrue();
        s.Relationships.Where(r => r.Confidence == ObservedRelationshipConfidence.Inferred).Should().OnlyContain(r => r.Evidence.Contains("not confirmed"));
    }

    [Fact]
    public async Task Observations_are_neutral_statements_and_absent_reads_claim_nothing()
    {
        var s = await Collect(Full());
        s.Observations.Should().Contain(o => o.ResourceName == "evhns-orders-dev" && o.Key == "publicNetworkAccess" && o.ForReview);
        s.Observations.Should().Contain(o => o.ResourceName == "evhns-orders-dev" && o.Key == "disableLocalAuth");
        s.Observations.Should().Contain(o => o.ResourceName == "psql-orders-dev" && o.Key == "privateEndpoint" && !o.ForReview);
        s.Observations.Should().Contain(o => o.ResourceName == "nsg-orders-dev" && o.Statement.StartsWith("1 inbound rule"));
        s.Observations.Should().Contain(o => o.ResourceName == "kv-orders-dev" && o.Key == "enablePurgeProtection");
        s.Observations.Should().Contain(o => o.ResourceName == "evhns-orders-dev" && o.Statement == "1 diagnostic setting(s) observed.");
        s.Observations.Should().Contain(o => o.ResourceName == "sbns-orders-dev" && o.Statement == "No diagnostic setting was observed.");
        s.Observations.Select(o => o.Statement).Should().NotContain(t => t.Contains("Pass", StringComparison.Ordinal) || t.Contains("Fail", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Effective_permissions_come_from_Azure_and_write_access_never_changes_read_only_behaviour()
    {
        var reader = await Collect(Full());
        reader.Access.Permissions.Single().Should().Match<EffectivePermissions>(p => p.CanReadResources && !p.IncludesWriteActions);

        var ownerFake = Full(owner: true);
        var owner = await Collect(ownerFake);
        owner.Access.Permissions.Single().IncludesWriteActions.Should().BeTrue();
        owner.Capabilities.Single(c => c.Area == AzureCapabilityArea.EffectivePermissions).Detail.Should().Be(AzureEnvironmentText.ReadOnlyWithWriteAccess);
        ownerFake.Gets.Should().OnlyContain(g => AzureReadOnlyPolicy.CheckGet(g) == null, "only GETs inside the read-only policy are sent, also with Owner");
        ownerFake.Queries.Select(q => q.Query).Distinct().Should().BeEquivalentTo(["resources", "resource-groups"]);
    }

    [Fact]
    public async Task Pim_roles_keep_eligible_and_active_apart()
    {
        var s = await Collect(Full());
        s.Access.SignedInRoles.Should().ContainSingle(r => r.Status == PimRoleStatus.Eligible && r.RoleName == "Owner" && r.MemberType == "Group");
        s.Access.SignedInRoles.Should().ContainSingle(r => r.Status == PimRoleStatus.Active && r.RoleName == "Reader");
        s.Capabilities.Single(c => c.Area == AzureCapabilityArea.PimRoles).Detail.Should().Contain("1 active and 1 eligible").And.Contain("never activates");
        s.Access.Assignments.Should().Contain(a => a.RoleName == "Azure Event Hubs Data Receiver" && AzureIds.Same(a.PrincipalResourceId, Identity) && a.ScopeLevel == "Resource");
        s.Access.Assignments.Should().Contain(a => a.RoleName == "Reader" && a.ScopeLevel == "Subscription" && a.PrincipalResourceId == null);
    }

    [Fact]
    public async Task Forbidden_areas_are_not_authorized_and_the_analysis_continues_as_partial()
    {
        var fake = Full();
        fake.Overrides.Add((u => u.Contains("/roleAssignments", StringComparison.OrdinalIgnoreCase) && !u.Contains("ScheduleInstances"), FakeAzureManagementClient.Status(403)));
        fake.Overrides.Add((u => u.Contains("diagnosticSettings", StringComparison.OrdinalIgnoreCase), FakeAzureManagementClient.Status(403)));
        fake.Overrides.Add((u => u.Contains("/eventhubs", StringComparison.OrdinalIgnoreCase), FakeAzureManagementClient.Status(403)));
        var s = await Collect(fake);

        s.Status.Should().Be(AzureAnalysisStatus.Partial);
        s.Capabilities.Single(c => c.Area == AzureCapabilityArea.RoleAssignments).State.Should().Be(AzureCapabilityState.NotAuthorized);
        s.Capabilities.Single(c => c.Area == AzureCapabilityArea.DiagnosticSettings).State.Should().Be(AzureCapabilityState.NotAuthorized);
        s.Capabilities.Single(c => c.Area == AzureCapabilityArea.Messaging).State.Should().Be(AzureCapabilityState.Partial);
        s.Capabilities.Should().NotContain(c => c.State == AzureCapabilityState.Failed);
        s.Observations.Should().NotContain(o => o.Key == "diagnosticSettings", "diagnostic settings that could not be read are neither present nor absent");
        s.Resources.Should().Contain(r => AzureIds.Same(r.Id, ServiceBus)).And.NotContain(r => r.ResourceKind == InfrastructureResourceKind.EventHub);
        s.Queries.Should().Contain(q => q.Name == "role-assignments" && q.Status == 403 && q.Outcome == "Not authorized");
    }

    [Fact]
    public async Task Resource_Graph_unavailable_falls_back_to_ARM_lists_and_reads_key_types_individually()
    {
        var fake = Full();
        fake.Overrides.Add((u => u.StartsWith("graph:", StringComparison.Ordinal), FakeAzureManagementClient.Status(403)));
        var arm = GraphResources().Select(r => JsonSerializer.SerializeToElement(r)).Select(e => new
        {
            id = e.GetProperty("id").GetString(), name = e.GetProperty("name").GetString(), type = e.GetProperty("type").GetString(), location = "norwayeast",
        }).ToArray();
        fake.Routes[$"/subscriptions/{Sub}/resources"] = () => FakeAzureManagementClient.Ok(new { value = arm });
        fake.Routes[$"/subscriptions/{Sub}/resourcegroups"] = () => FakeAzureManagementClient.Ok(new { value = new[] { new { id = RgId(), name = Rg, type = "Microsoft.Resources/resourceGroups", location = "norwayeast" } } });
        fake.Routes[Storage] = () => FakeAzureManagementClient.Ok(GraphResources().Select(r => JsonSerializer.SerializeToElement(r)).Single(e => e.GetProperty("id").GetString() == Storage));
        var s = await Collect(fake);

        s.Capabilities.Single(c => c.Area == AzureCapabilityArea.ResourceGraph).State.Should().Be(AzureCapabilityState.NotAuthorized);
        s.Capabilities.Single(c => c.Area == AzureCapabilityArea.ResourceInventory).State.Should().Be(AzureCapabilityState.Partial);
        s.Status.Should().Be(AzureAnalysisStatus.Partial);
        s.Limitations.Should().Contain(l => l.Contains("Resource Graph was unavailable"));
        P(One(s, Storage), "minimumTlsVersion").Should().Be("TLS1_2", "the storage account was read by id because the ARM list has no properties");
        s.Resources.Should().Contain(r => AzureIds.Same(r.Id, Site));
    }

    [Fact]
    public async Task Paging_follows_management_next_links_and_skip_tokens_only()
    {
        var fake = Full();
        var page2 = $"/subscriptions/{Sub}/providers/Microsoft.Authorization/roleAssignments?api-version=2022-04-01&$skiptoken=p2";
        fake.Routes[$"/subscriptions/{Sub}/providers/Microsoft.Authorization/roleAssignments?api-version=2022-04-01"] = () => FakeAzureManagementClient.Ok(new
        {
            value = new[] { new { id = "ra1", properties = new { scope = EventHubs, roleDefinitionId = "/x/a638d3c7-ab3a-418d-83e6-5f17a39d4fde", principalId = IdentityPrincipal, principalType = "ServicePrincipal" } } },
            nextLink = "https://management.azure.com" + page2,
        });
        fake.Routes[page2] = () => FakeAzureManagementClient.Ok(new
        {
            value = new[] { new { id = "ra3", properties = new { scope = Storage, roleDefinitionId = "/x/acdd72a7-3385-48ef-bd42-f606fba81ae7", principalId = SitePrincipal, principalType = "ServicePrincipal" } } },
            nextLink = "https://attacker.example/steal?api-version=1",
        });
        fake.Graph = (q, skip) => q != AzureGraphQuery.Resources ? FakeAzureManagementClient.Ok(new { data = GraphGroups() })
            : skip is null ? FakeAzureManagementClient.Ok(new Dictionary<string, object> { ["data"] = GraphResources().Take(5).ToArray(), ["$skipToken"] = "page-2" })
            : FakeAzureManagementClient.Ok(new { data = GraphResources().Skip(5).ToArray() });
        var s = await Collect(fake);

        fake.Queries.Should().Contain(q => q.Query == "resources" && q.SkipToken == "page-2");
        s.Resources.Should().Contain(r => AzureIds.Same(r.Id, Nsg), "the second Resource Graph page was read");
        fake.Gets.Should().Contain(page2).And.NotContain(g => g.Contains("attacker", StringComparison.OrdinalIgnoreCase));
        s.Access.Assignments.Should().Contain(a => a.RoleName == "Reader" && AzureIds.Same(a.PrincipalResourceId, Site));
        s.Limitations.Should().Contain(l => l.Contains("paging link pointed outside"));
    }

    [Fact]
    public async Task An_expired_sign_in_mid_analysis_is_stated_and_later_reads_are_not_authorized()
    {
        var fake = Full();
        fake.Overrides.Add((u => u.Contains("/providers/Microsoft.Authorization/", StringComparison.OrdinalIgnoreCase) && !u.Contains("/permissions"), FakeAzureManagementClient.Status(401, "ExpiredAuthenticationToken")));
        var s = await Collect(fake);
        s.Limitations.Should().Contain(l => l.Contains("sign-in expired during the analysis"));
        s.Capabilities.Single(c => c.Area == AzureCapabilityArea.RoleAssignments).State.Should().Be(AzureCapabilityState.NotAuthorized);
    }

    [Fact]
    public async Task Throttling_is_reported_as_throttled_not_failed()
    {
        var fake = Full();
        fake.Overrides.Add((u => u.Contains("/topics", StringComparison.OrdinalIgnoreCase) || u.Contains("/queues", StringComparison.OrdinalIgnoreCase), FakeAzureManagementClient.Status(429, "TooManyRequests")));
        var s = await Collect(fake);
        s.Capabilities.Single(c => c.Area == AzureCapabilityArea.Messaging).Should().Match<AzureCapability>(c => c.State == AzureCapabilityState.Partial && c.Detail.Contains("throttled"));
        s.Queries.Should().Contain(q => q.Status == 429 && q.Outcome == "Throttled");
    }

    [Fact]
    public async Task Subscriptions_not_visible_to_the_account_are_stated_and_no_access_is_not_authorized()
    {
        var fake = Full();
        var s = await Collector(fake).CollectAsync(Request() with { SubscriptionIds = [Sub, Sub2] }, default);
        s.Limitations.Should().Contain(l => l.Contains(Sub2) && l.Contains("not visible"));
        s.Subscriptions.Should().ContainSingle();

        var none = Full();
        none.Routes["/subscriptions"] = () => FakeAzureManagementClient.Ok(new { value = Array.Empty<object>() });
        var empty = await Collector(none).CollectAsync(Request(), default);
        empty.Status.Should().Be(AzureAnalysisStatus.NotAuthorized);
        none.Queries.Should().BeEmpty("nothing is queried outside the visible subscriptions");
    }

    [Fact]
    public async Task Resource_group_scope_narrows_inventory_and_role_assignments()
    {
        var fake = Full();
        fake.Graph = (q, _) => FakeAzureManagementClient.Ok(new { data = q == AzureGraphQuery.Resources ? GraphResources().Concat(new object[]
            { new { id = Id("Microsoft.Storage/storageAccounts", "stother", rg: "rg-other"), name = "stother", type = "microsoft.storage/storageaccounts", resourceGroup = "rg-other", subscriptionId = Sub, location = "norwayeast" } }).ToArray()
            : GraphGroups().Concat(new object[] { new { id = RgId(rg: "rg-other"), name = "rg-other", type = "microsoft.resources/subscriptions/resourcegroups", resourceGroup = "rg-other", subscriptionId = Sub } }).ToArray() });
        var s = await Collect(fake, null, Rg);
        s.Resources.Should().NotContain(r => r.Name == "stother" || r.Name == "rg-other");
        s.Scope.ResourceGroups.Should().Equal(Rg);
    }

    [Fact]
    public async Task Invalid_requests_and_signed_out_sessions_are_refused_before_any_Azure_call()
    {
        var fake = Full();
        await FluentActions.Awaiting(() => Collector(fake).CollectAsync(Request() with { SubscriptionIds = ["not-a-guid"] }, default)).Should().ThrowAsync<AzureAnalysisRequestException>();
        await FluentActions.Awaiting(() => Collector(fake).CollectAsync(Request("rg' | where 1==1") , default)).Should().ThrowAsync<AzureAnalysisRequestException>();
        await FluentActions.Awaiting(() => Collector(fake, new FakeSignIn(AzureConnectionState.SignedOut)).CollectAsync(Request(), default)).Should().ThrowAsync<AzureNotSignedInException>();
        fake.Gets.Should().BeEmpty();
        fake.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task No_secret_or_token_reaches_the_snapshot()
    {
        var s = await Collect(Full());
        var json = JsonSerializer.Serialize(s);
        json.Should().NotContain(Sentinel).And.NotContain("fake-token").And.NotContain("pgadmin").And.NotContain("publishingUsername").And.NotContain("66666666-6666-6666-6666-666666666666");
        One(s, Site).Tags.Should().Contain(t => t.Key == "owner-token" && t.Value == "[value not shown]").And.Contain(t => t.Key == "environment" && t.Value == "dev");
        One(s, Insights).Properties.Select(p => p.Key).Should().NotContain(k => k.Contains("Instrumentation", StringComparison.OrdinalIgnoreCase) || k.Contains("Connection", StringComparison.OrdinalIgnoreCase));
    }
}
