using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.AzureEnvironment;
using BirkNext.SourceDomains;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.AzureEnvironment;

public sealed class AzureAnalysisRequestException(string message) : Exception(message);
public sealed class AzureNotSignedInException() : Exception("Sign in to Azure before analyzing an environment.");

public interface IAzureEnvironmentCollector
{
    Task<(List<AzureSubscription> Subscriptions, AzureCapability Capability)> SubscriptionsAsync(CancellationToken ct);
    Task<AzureEnvironmentSnapshot> CollectAsync(AzureAnalysisRequest request, CancellationToken ct);
}

/// <summary>
/// Reads one Azure scope (selected subscriptions, optionally narrowed to resource groups) into an <see cref="AzureEnvironmentSnapshot"/>:
/// subscriptions → your effective permissions → inventory (Resource Graph; ARM list fallback) → bounded detail reads (Event Hubs, Service Bus,
/// blob containers, databases, site config, diagnostic settings) → role assignments → your PIM roles → topology and observations → the
/// capability matrix. Every read is a GET or a predefined Resource Graph query; a 403 marks that area Not authorized and analysis continues.
/// </summary>
public sealed class AzureEnvironmentCollector(IAzureManagementClient arm, IAzureSignInService signIn, IOptions<AzureEnvironmentOptions> options,
    ILogger<AzureEnvironmentCollector> logger, TimeProvider? clock = null) : IAzureEnvironmentCollector
{
    private const int MaxPages = 50;
    private static readonly Regex SubscriptionId = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);
    private static readonly Regex ResourceGroupName = new(@"^[\w\-.()]{1,90}$", RegexOptions.Compiled);
    private static readonly string[] SystemDatabases = ["azure_maintenance", "azure_sys", "master"];

    /// <summary>GET api-versions for reading a resource by id (ARM-list fallback, where list items carry no properties).</summary>
    private static readonly Dictionary<string, string> ApiVersions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["microsoft.storage/storageaccounts"] = "2023-01-01", ["microsoft.keyvault/vaults"] = "2023-07-01", ["microsoft.eventhub/namespaces"] = "2024-01-01",
        ["microsoft.servicebus/namespaces"] = "2021-11-01", ["microsoft.web/sites"] = "2022-09-01", ["microsoft.dbforpostgresql/flexibleservers"] = "2022-12-01",
        ["microsoft.network/virtualnetworks"] = "2023-09-01", ["microsoft.network/privateendpoints"] = "2023-09-01", ["microsoft.network/networksecuritygroups"] = "2023-09-01",
        ["microsoft.insights/components"] = "2020-02-02", ["microsoft.operationalinsights/workspaces"] = "2022-10-01", ["microsoft.sql/servers"] = "2021-11-01",
        ["microsoft.managedidentity/userassignedidentities"] = "2023-01-31", ["microsoft.app/containerapps"] = "2023-05-01",
    };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<(List<AzureSubscription> Subscriptions, AzureCapability Capability)> SubscriptionsAsync(CancellationToken ct)
    {
        EnsureSignedIn();
        var run = new Run(options.Value.MaxDeepReads);
        return await SubscriptionsCore(run, ct);
    }

    private void EnsureSignedIn()
    {
        if (signIn.Status().State is not (AzureConnectionState.SignedIn or AzureConnectionState.Expired)) throw new AzureNotSignedInException();
    }

    public async Task<AzureEnvironmentSnapshot> CollectAsync(AzureAnalysisRequest request, CancellationToken ct)
    {
        EnsureSignedIn();
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(request.EnvironmentId)) throw new AzureAnalysisRequestException("A Target Environment is required.");
        var requested = request.SubscriptionIds.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (requested.Count == 0) throw new AzureAnalysisRequestException("Select at least one subscription.");
        if (requested.Any(s => !SubscriptionId.IsMatch(s))) throw new AzureAnalysisRequestException("Subscription ids must be GUIDs.");
        if (requested.Count > o.MaxSubscriptions) throw new AzureAnalysisRequestException($"Select at most {o.MaxSubscriptions} subscriptions.");
        var groups = request.ResourceGroups.Select(g => g.Trim()).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (groups.Any(g => !ResourceGroupName.IsMatch(g))) throw new AzureAnalysisRequestException("Resource group names may contain letters, digits, '-', '_', '.', '(' and ')' only.");
        var environment = string.IsNullOrWhiteSpace(request.EnvironmentLabel) ? null : SourceEnvironments.Normalize(request.EnvironmentLabel);

        var started = Stopwatch.StartNew();
        var run = new Run(o.MaxDeepReads);
        var (visible, subscriptionCapability) = await SubscriptionsCore(run, ct);
        var selected = visible.Where(s => requested.Contains(s.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var missing in requested.Where(r => selected.All(s => !s.Id.Equals(r, StringComparison.OrdinalIgnoreCase))))
            run.Limitations.Add($"Subscription {missing} is not visible to the signed-in account (no access, another tenant, or not activated); it was not analyzed.");
        var status = signIn.Status();
        AzureEnvironmentSnapshot Snapshot(AzureAnalysisStatus s, List<ObservedResource>? resources = null, List<ObservedRelationship>? relationships = null,
            ObservedAccess? access = null, List<AzureObservation>? observations = null) => new()
        {
            Id = Guid.NewGuid(), EnvironmentId = request.EnvironmentId.Trim(), CapturedAt = _clock.GetUtcNow(), TenantId = status.TenantId, Method = status.Method, Status = s,
            Scope = new([.. selected.Select(x => x.Id)], groups, environment), Subscriptions = selected, Resources = resources ?? [], Relationships = relationships ?? [],
            Capabilities = run.Capabilities, Access = access ?? new(), Observations = observations ?? [], Queries = run.Queries, Limitations = run.Limitations,
        };
        if (selected.Count == 0)
            return Snapshot(subscriptionCapability.State is AzureCapabilityState.Failed or AzureCapabilityState.Throttled ? AzureAnalysisStatus.Failed : AzureAnalysisStatus.NotAuthorized);
        var subscriptionIds = selected.Select(s => s.Id).ToList();

        var permissions = new List<EffectivePermissions>();
        foreach (var sub in subscriptionIds) permissions.Add(await PermissionsAsync(run, sub, ct));

        var (resources, inventoryState, viaFallback) = await InventoryAsync(run, subscriptionIds, environment, ct);
        if (groups.Count > 0)
            resources = resources.Where(r => r.ResourceGroup is { } g && groups.Contains(g, StringComparer.OrdinalIgnoreCase)
                || (r.Type == "microsoft.resources/subscriptions/resourcegroups" && groups.Contains(r.Name, StringComparer.OrdinalIgnoreCase))).ToList();
        if (resources.Count >= o.MaxResources) run.Limitations.Add($"The inventory stopped at {o.MaxResources} resources; narrow the scope with resource groups to see the rest.");

        if (viaFallback) resources = await FillFallbackPropertiesAsync(run, resources, environment, ct);
        resources = await DeepReadsAsync(run, resources, environment, ct);

        var assignments = await RoleAssignmentsAsync(run, subscriptionIds, groups, resources, ct);
        var roles = await SignedInRolesAsync(run, subscriptionIds, ct);
        var relationships = AzureTopologyBuilder.Build(resources, assignments);
        var observations = AzureObservations.Build(resources, relationships);
        AreaCapabilities(run, resources, inventoryState, viaFallback);
        if (run.SessionExpired) run.Limitations.Add("Your Azure sign-in expired during the analysis; later reads were not authorized. Sign in again and re-run.");
        if (run.BudgetExhausted) run.Limitations.Add($"Detail reads stopped at the configured budget ({o.MaxDeepReads}); some child resources and diagnostic settings were not read.");
        run.Limitations.Add("App settings, connection strings, Key Vault secrets/keys/certificates, blob contents and messages are never read, so connections configured only there are not visible.");

        var overall = inventoryState switch
        {
            AzureCapabilityState.NotAuthorized => AzureAnalysisStatus.NotAuthorized,
            AzureCapabilityState.Failed or AzureCapabilityState.Throttled when resources.Count == 0 => AzureAnalysisStatus.Failed,
            _ => run.Capabilities.Any(c => c.State is AzureCapabilityState.Partial or AzureCapabilityState.NotAuthorized or AzureCapabilityState.Throttled or AzureCapabilityState.Failed)
                 || run.Limitations.Count > 1 ? AzureAnalysisStatus.Partial : AzureAnalysisStatus.Complete,
        };
        logger.LogInformation("Azure environment analysis: {Subscriptions} subscription(s), {Resources} resource(s), {Relationships} relationship(s), {Status} in {Elapsed} ms.",
            subscriptionIds.Count, resources.Count, relationships.Count, overall, started.ElapsedMilliseconds);
        return Snapshot(overall, resources, relationships, new ObservedAccess { Assignments = assignments, SignedInRoles = roles, Permissions = permissions }, observations);
    }

    // ── Subscriptions and permissions ────────────────────────────────────────────────────────────────────────────────────

    private async Task<(List<AzureSubscription>, AzureCapability)> SubscriptionsCore(Run run, CancellationToken ct)
    {
        var (items, last, pages) = await GetAllAsync(run, "/subscriptions?api-version=2022-12-01", 500, ct);
        Track(run, "subscriptions", "GET /subscriptions", last, pages, items.Count, null);
        var list = items.Select(i => new AzureSubscription(AzureResourceNormalizer.Str(i, "subscriptionId") ?? "", AzureResourceNormalizer.Str(i, "displayName") ?? "",
                AzureResourceNormalizer.Str(i, "state") ?? "", AzureResourceNormalizer.Str(i, "tenantId")))
            .Where(s => s.Id.Length > 0).OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        var state = items.Count > 0 || last.Ok ? AzureCapabilityState.Available : StateOf(last);
        var capability = new AzureCapability(AzureCapabilityArea.Subscriptions, state,
            state == AzureCapabilityState.Available ? $"{list.Count} subscription(s) visible to the signed-in account." : Explain(last, "Subscriptions"), "GET /subscriptions");
        run.Capabilities.Add(capability);
        return (list, capability);
    }

    private async Task<EffectivePermissions> PermissionsAsync(Run run, string sub, CancellationToken ct)
    {
        var (items, last, pages) = await GetAllAsync(run, $"/subscriptions/{sub}/providers/Microsoft.Authorization/permissions?api-version=2022-04-01", 200, ct);
        Track(run, "effective-permissions", "GET …/providers/Microsoft.Authorization/permissions", last, pages, items.Count, sub);
        if (items.Count == 0 && !last.Ok)
        {
            run.Capabilities.Add(new(AzureCapabilityArea.EffectivePermissions, StateOf(last), Explain(last, "Effective permissions"), "GET Microsoft.Authorization/permissions", sub));
            return new(sub, false, false, [], "Your effective permissions could not be read.");
        }
        var actions = items.SelectMany(i => Strings(i, "actions")).ToList();
        var notActions = items.SelectMany(i => Strings(i, "notActions")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var canRead = actions.Any(a => a is "*" or "*/read" || a.EndsWith("/read", StringComparison.OrdinalIgnoreCase));
        var write = actions.Any(a => !a.EndsWith("/read", StringComparison.OrdinalIgnoreCase) && !notActions.Contains(a));
        run.Capabilities.Add(new(AzureCapabilityArea.EffectivePermissions, AzureCapabilityState.Available,
            write ? AzureEnvironmentText.ReadOnlyWithWriteAccess : "Read access. BirkNext only reads.", "GET Microsoft.Authorization/permissions", sub));
        return new(sub, canRead, write, actions.Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList(),
            write ? AzureEnvironmentText.ReadOnlyWithWriteAccess : "Read-only effective permissions.");
    }

    // ── Inventory ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<(List<ObservedResource>, AzureCapabilityState, bool ViaFallback)> InventoryAsync(Run run, List<string> subs, SourceEnvironmentLabel? environment, CancellationToken ct)
    {
        var max = options.Value.MaxResources;
        var resources = new List<ObservedResource>();
        var graph = await GraphAsync(run, AzureGraphQuery.Resources, subs, max, ct);
        var groups = await GraphAsync(run, AzureGraphQuery.ResourceGroups, subs, 2000, ct);
        if (graph.Last.Ok || graph.Items.Count > 0)
        {
            foreach (var item in graph.Items.Concat(groups.Items)) resources.AddRange(AzureResourceNormalizer.Normalize(item, "Azure Resource Graph", environment));
            run.Capabilities.Add(new(AzureCapabilityArea.ResourceGraph, graph.Last.Ok ? AzureCapabilityState.Available : AzureCapabilityState.Partial,
                graph.Last.Ok ? $"{graph.Items.Count} resource(s) from predefined queries." : $"Stopped after {graph.Items.Count} resource(s): {Explain(graph.Last, "Resource Graph")}", "POST Microsoft.ResourceGraph/resources (predefined query)"));
            run.Capabilities.Add(new(AzureCapabilityArea.ResourceInventory, graph.Last.Ok ? AzureCapabilityState.Available : AzureCapabilityState.Partial,
                $"{resources.Count} resource(s) incl. resource groups and subnets.", "Resource Graph"));
            return (Distinct(resources), graph.Last.Ok ? AzureCapabilityState.Available : AzureCapabilityState.Partial, false);
        }

        run.Capabilities.Add(new(AzureCapabilityArea.ResourceGraph, StateOf(graph.Last), Explain(graph.Last, "Resource Graph") + " Falling back to Azure Resource Manager lists.",
            "POST Microsoft.ResourceGraph/resources (predefined query)"));
        var states = new List<AzureCapabilityState>();
        foreach (var sub in subs)
        {
            var (items, last, pages) = await GetAllAsync(run, $"/subscriptions/{sub}/resources?api-version=2021-04-01", max - resources.Count, ct);
            Track(run, "resources", "GET /subscriptions/{id}/resources", last, pages, items.Count, sub);
            var (rgs, rgLast, rgPages) = await GetAllAsync(run, $"/subscriptions/{sub}/resourcegroups?api-version=2021-04-01", 2000, ct);
            Track(run, "resource-groups", "GET /subscriptions/{id}/resourcegroups", rgLast, rgPages, rgs.Count, sub);
            foreach (var item in items.Concat(rgs)) resources.AddRange(AzureResourceNormalizer.Normalize(item, "Azure Resource Manager", environment));
            states.Add(items.Count > 0 || last.Ok ? (last.Ok ? AzureCapabilityState.Available : AzureCapabilityState.Partial) : StateOf(last));
        }
        var state = states.All(s => s == AzureCapabilityState.Available) ? AzureCapabilityState.Partial // properties are missing from ARM lists
            : states.All(s => s == AzureCapabilityState.NotAuthorized) ? AzureCapabilityState.NotAuthorized
            : states.Any(s => s is AzureCapabilityState.Available or AzureCapabilityState.Partial) ? AzureCapabilityState.Partial
            : states.FirstOrDefault(AzureCapabilityState.Failed);
        run.Capabilities.Add(new(AzureCapabilityArea.ResourceInventory, state,
            state == AzureCapabilityState.NotAuthorized ? "Resources could not be listed with your permissions." : $"{resources.Count} resource(s) from ARM lists; properties read individually for key types.",
            "GET /subscriptions/{id}/resources"));
        if (state != AzureCapabilityState.NotAuthorized) run.Limitations.Add("Resource Graph was unavailable, so inventory came from ARM lists; properties were read individually for key resource types only.");
        return (Distinct(resources), state, true);
    }

    private static List<ObservedResource> Distinct(List<ObservedResource> resources) =>
        resources.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

    private async Task<(List<JsonElement> Items, ArmResult Last)> GraphAsync(Run run, AzureGraphQuery query, List<string> subs, int max, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        string? skip = null;
        ArmResult last;
        var pages = 0;
        do
        {
            last = await arm.QueryAsync(query, subs, skip, ct);
            pages++;
            Expired(run, last);
            if (!last.Ok) break;
            if (last.Body!.Value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array) items.AddRange(data.EnumerateArray());
            skip = last.Body.Value.TryGetProperty("$skipToken", out var token) && token.ValueKind == JsonValueKind.String ? token.GetString() : null;
        } while (skip is not null && items.Count < max && pages < MaxPages);
        Track(run, $"resource-graph:{query.Name}", "POST Microsoft.ResourceGraph/resources", last, pages, items.Count, null);
        return (items.Take(max).ToList(), last);
    }

    private async Task<List<ObservedResource>> FillFallbackPropertiesAsync(Run run, List<ObservedResource> resources, SourceEnvironmentLabel? environment, CancellationToken ct)
    {
        var result = new List<ObservedResource>(resources.Count);
        foreach (var r in resources)
        {
            if (r.Properties.Count > 0 || !ApiVersions.TryGetValue(r.Type, out var version) || !run.TakeBudget()) { result.Add(r); continue; }
            var got = await arm.GetAsync($"{r.Id}?api-version={version}", ct);
            Expired(run, got);
            run.Tally(Area(r), got);
            var read = got.Ok ? AzureResourceNormalizer.Normalize(got.Body!.Value, "Azure Resource Manager", environment).Select(n => n with { SubscriptionId = r.SubscriptionId, ResourceGroup = r.ResourceGroup }).ToList() : [];
            result.AddRange(read.Any(n => AzureIds.Same(n.Id, r.Id)) ? read : [r, .. read]);
        }
        return Distinct(result);
    }

    // ── Detail reads (children, site configuration, diagnostic settings) ───────────────────────────────────────────────

    private async Task<List<ObservedResource>> DeepReadsAsync(Run run, List<ObservedResource> resources, SourceEnvironmentLabel? environment, CancellationToken ct)
    {
        var added = new List<ObservedResource>();
        async Task Children(ObservedResource parent, string relative, AzureCapabilityArea area, Func<ObservedResource, Task>? each = null)
        {
            if (!run.TakeBudget()) return;
            var (items, last, pages) = await GetAllAsync(run, $"{parent.Id}/{relative}", 1000, ct);
            run.Tally(area, last);
            Track(run, relative.Split('?')[0], $"GET {{{parent.CategoryDetail}}}/{relative.Split('?')[0]}", last, pages, items.Count, parent.SubscriptionId);
            foreach (var item in items)
                foreach (var child in AzureResourceNormalizer.Normalize(item, "Azure Resource Manager", environment))
                {
                    if (SystemDatabases.Contains(child.Name, StringComparer.OrdinalIgnoreCase) && child.Category == InfrastructureCategory.Database) continue;
                    var c = child with
                    {
                        SubscriptionId = parent.SubscriptionId, ResourceGroup = parent.ResourceGroup, Location = child.Location ?? parent.Location, Environment = child.Environment ?? parent.Environment,
                        ParentId = child.Type == "microsoft.storage/storageaccounts/blobservices/containers" ? parent.Id : child.ParentId,
                    };
                    added.Add(c);
                    if (each is not null) await each(c);
                }
        }

        foreach (var ns in resources.Where(r => r.Type == "microsoft.eventhub/namespaces").ToList())
            await Children(ns, "eventhubs?api-version=2024-01-01", AzureCapabilityArea.Messaging, hub => Children(hub, "consumergroups?api-version=2024-01-01", AzureCapabilityArea.Messaging));
        foreach (var ns in resources.Where(r => r.Type == "microsoft.servicebus/namespaces").ToList())
        {
            await Children(ns, "topics?api-version=2021-11-01", AzureCapabilityArea.Messaging, topic => Children(topic, "subscriptions?api-version=2021-11-01", AzureCapabilityArea.Messaging));
            await Children(ns, "queues?api-version=2021-11-01", AzureCapabilityArea.Messaging);
        }
        foreach (var account in resources.Where(r => r.Type == "microsoft.storage/storageaccounts").ToList())
            await Children(account, "blobServices/default/containers?api-version=2023-01-01", AzureCapabilityArea.Storage);
        foreach (var server in resources.Where(r => r.Type == "microsoft.dbforpostgresql/flexibleservers").ToList())
            await Children(server, "databases?api-version=2022-12-01", AzureCapabilityArea.Databases);

        var all = Distinct([.. resources, .. added.Where(a => resources.All(r => !AzureIds.Same(r.Id, a.Id)))]);
        var updated = new List<ObservedResource>(all.Count);
        foreach (var r in all)
        {
            var current = r;
            if (r.Type == "microsoft.web/sites" && run.TakeBudget())
            {
                var config = await arm.GetAsync($"{r.Id}/config/web?api-version=2022-09-01", ct);
                Expired(run, config);
                run.Tally(AzureCapabilityArea.Compute, config);
                if (config.Ok) current = current with { Properties = Merge(current.Properties, AzureResourceNormalizer.SiteConfig(config.Body!.Value)) };
            }
            if (AzureObservations.IsMonitorable(r) && run.TakeBudget())
            {
                var (settings, last, _) = await GetAllAsync(run, $"{r.Id}/providers/Microsoft.Insights/diagnosticSettings?api-version=2021-05-01-preview", 50, ct);
                run.Tally(AzureCapabilityArea.DiagnosticSettings, last);
                if (last.Ok || settings.Count > 0) current = current with { Properties = Merge(current.Properties, Diagnostics(settings)) };
            }
            updated.Add(current);
        }
        return updated;
    }

    private static List<ObservedProperty> Merge(List<ObservedProperty> existing, IEnumerable<ObservedProperty> more) =>
        [.. existing.Where(e => more.All(m => m.Key != e.Key)), .. more];

    private static IEnumerable<ObservedProperty> Diagnostics(List<JsonElement> settings)
    {
        yield return new("diagnosticSettings", settings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "Observability");
        foreach (var s in settings)
        {
            if (AzureResourceNormalizer.Path(s, "properties") is not { } p) continue;
            if (AzureResourceNormalizer.Str(p, "workspaceId") is { } w) yield return new("diagnosticsWorkspaceId", w, "Reference");
            if (AzureResourceNormalizer.Str(p, "storageAccountId") is { } sa) yield return new("diagnosticsStorageAccountId", sa, "Reference");
            if (AzureResourceNormalizer.Str(p, "eventHubAuthorizationRuleId") is { } eh)
            {
                // The rule id names a namespace authorization rule; the namespace is the destination (no key is read).
                var cut = eh.IndexOf("/authorizationrules/", StringComparison.OrdinalIgnoreCase);
                yield return new("diagnosticsEventHubRuleId", cut > 0 ? eh[..cut] : eh, "Reference");
            }
            var categories = p.TryGetProperty("logs", out var logs) && logs.ValueKind == JsonValueKind.Array
                ? logs.EnumerateArray().Where(l => l.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True)
                    .Select(l => AzureResourceNormalizer.Str(l, "category") ?? AzureResourceNormalizer.Str(l, "categoryGroup")).Where(c => c is not null).Take(12).ToList()
                : [];
            if (categories.Count > 0) yield return new("diagnosticLogCategories", string.Join(", ", categories), "Observability");
        }
    }

    // ── RBAC and PIM ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<List<ObservedRoleAssignment>> RoleAssignmentsAsync(Run run, List<string> subs, List<string> groups, List<ObservedResource> resources, CancellationToken ct)
    {
        var result = new List<ObservedRoleAssignment>();
        var states = new List<AzureCapabilityState>();
        foreach (var sub in subs)
        {
            var (definitions, defLast, defPages) = await GetAllAsync(run, $"/subscriptions/{sub}/providers/Microsoft.Authorization/roleDefinitions?api-version=2022-04-01", 3000, ct);
            Track(run, "role-definitions", "GET …/Microsoft.Authorization/roleDefinitions", defLast, defPages, definitions.Count, sub);
            var names = definitions.Select(d => (Id: AzureIds.Name(AzureResourceNormalizer.Str(d, "id") ?? ""), Name: AzureResourceNormalizer.Path(d, "properties") is { } p ? AzureResourceNormalizer.Str(p, "roleName") : null))
                .Where(d => d.Name is not null).GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name!, StringComparer.OrdinalIgnoreCase);
            var (items, last, pages) = await GetAllAsync(run, $"/subscriptions/{sub}/providers/Microsoft.Authorization/roleAssignments?api-version=2022-04-01", 3000, ct);
            Track(run, "role-assignments", "GET …/Microsoft.Authorization/roleAssignments", last, pages, items.Count, sub);
            states.Add(items.Count > 0 || last.Ok ? AzureCapabilityState.Available : StateOf(last));
            foreach (var item in items)
            {
                if (AzureResourceNormalizer.Path(item, "properties") is not { } p) continue;
                var scope = AzureResourceNormalizer.Str(p, "scope") ?? "";
                var rg = AzureIds.ResourceGroup(scope);
                if (groups.Count > 0 && rg is not null && !groups.Contains(rg, StringComparer.OrdinalIgnoreCase)) continue;
                var roleId = AzureIds.Name(AzureResourceNormalizer.Str(p, "roleDefinitionId") ?? "");
                var principal = AzureResourceNormalizer.Str(p, "principalId") ?? "";
                result.Add(new ObservedRoleAssignment
                {
                    Scope = scope, ScopeLevel = ScopeLevel(scope), RoleName = names.TryGetValue(roleId, out var n) ? n : $"Role {roleId[..Math.Min(8, roleId.Length)]}…",
                    PrincipalType = AzureResourceNormalizer.Str(p, "principalType") ?? "Unknown", PrincipalId = principal,
                    PrincipalResourceId = resources.FirstOrDefault(r => string.Equals(r.PrincipalId, principal, StringComparison.OrdinalIgnoreCase)
                        || r.Properties.Any(x => x.Key == "principalId" && string.Equals(x.Value, principal, StringComparison.OrdinalIgnoreCase)))?.Id,
                });
            }
        }
        run.Capabilities.Add(new(AzureCapabilityArea.RoleAssignments, Combine(states),
            Combine(states) is AzureCapabilityState.Available ? $"{result.Count} role assignment(s) at subscription scope and below." : "Role assignments could not be read for every subscription.",
            "GET Microsoft.Authorization/roleAssignments"));
        return result.DistinctBy(a => (a.Scope.ToLowerInvariant(), a.RoleName, a.PrincipalId)).ToList();
    }

    private async Task<List<SignedInRole>> SignedInRolesAsync(Run run, List<string> subs, CancellationToken ct)
    {
        var roles = new List<SignedInRole>();
        var states = new List<AzureCapabilityState>();
        foreach (var sub in subs)
            foreach (var (path, status) in new[] { ("roleAssignmentScheduleInstances", PimRoleStatus.Active), ("roleEligibilityScheduleInstances", PimRoleStatus.Eligible) })
            {
                var (items, last, pages) = await GetAllAsync(run, $"/subscriptions/{sub}/providers/Microsoft.Authorization/{path}?api-version=2020-10-01&$filter=asTarget()", 500, ct);
                Track(run, path, $"GET …/Microsoft.Authorization/{path} (asTarget)", last, pages, items.Count, sub);
                states.Add(items.Count > 0 || last.Ok ? AzureCapabilityState.Available : last.Status is 400 ? AzureCapabilityState.NotAssessed : StateOf(last));
                foreach (var item in items)
                {
                    if (AzureResourceNormalizer.Path(item, "properties") is not { } p) continue;
                    var role = AzureResourceNormalizer.Path(p, "expandedProperties.roleDefinition") is { } rd ? AzureResourceNormalizer.Str(rd, "displayName") : null;
                    var end = AzureResourceNormalizer.Str(p, "endDateTime") is { } e && DateTimeOffset.TryParse(e, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null;
                    roles.Add(new(AzureResourceNormalizer.Str(p, "scope") ?? $"/subscriptions/{sub}", role ?? "Role", status, AzureResourceNormalizer.Str(p, "memberType") ?? "Direct", end));
                }
            }
        var state = Combine(states);
        run.Capabilities.Add(new(AzureCapabilityArea.PimRoles, state,
            state == AzureCapabilityState.Available ? $"{roles.Count(r => r.Status == PimRoleStatus.Active)} active and {roles.Count(r => r.Status == PimRoleStatus.Eligible)} eligible role(s) for you. {AzureEnvironmentText.PimGuidance}"
            : state == AzureCapabilityState.NotAssessed ? "PIM role schedules are not available in this tenant (PIM may not be licensed or enabled)." : "Your PIM roles could not be read.",
            "GET Microsoft.Authorization/role*ScheduleInstances?$filter=asTarget()"));
        return roles.DistinctBy(r => (r.Scope.ToLowerInvariant(), r.RoleName, r.Status)).ToList();
    }

    private static string ScopeLevel(string scope) =>
        scope is "/" ? "Root"
        : scope.StartsWith("/providers/Microsoft.Management/managementGroups/", StringComparison.OrdinalIgnoreCase) ? "Management group"
        : scope.Contains("/providers/", StringComparison.OrdinalIgnoreCase) ? "Resource"
        : AzureIds.ResourceGroup(scope) is not null ? "Resource group" : "Subscription";

    // ── Capability matrix by area ─────────────────────────────────────────────────────────────────────────────────────

    private static AzureCapabilityArea Area(ObservedResource r) => AreaOf(r.Category);

    /// <summary>The capability area that covers a provider-neutral category (shared with the declared-vs-observed comparison).</summary>
    public static AzureCapabilityArea AreaOf(InfrastructureCategory category) => category switch
    {
        InfrastructureCategory.Compute => AzureCapabilityArea.Compute,
        InfrastructureCategory.Messaging => AzureCapabilityArea.Messaging,
        InfrastructureCategory.Storage => AzureCapabilityArea.Storage,
        InfrastructureCategory.Database or InfrastructureCategory.Cache => AzureCapabilityArea.Databases,
        InfrastructureCategory.SecretStore => AzureCapabilityArea.KeyVaultMetadata,
        InfrastructureCategory.Identity => AzureCapabilityArea.ManagedIdentity,
        InfrastructureCategory.Networking or InfrastructureCategory.Dns => AzureCapabilityArea.Networking,
        InfrastructureCategory.Observability => AzureCapabilityArea.Monitoring,
        _ => AzureCapabilityArea.ResourceInventory,
    };

    private static void AreaCapabilities(Run run, List<ObservedResource> resources, AzureCapabilityState inventory, bool viaFallback)
    {
        foreach (var area in new[] { AzureCapabilityArea.Compute, AzureCapabilityArea.Messaging, AzureCapabilityArea.Storage, AzureCapabilityArea.Databases, AzureCapabilityArea.KeyVaultMetadata,
                     AzureCapabilityArea.ManagedIdentity, AzureCapabilityArea.Networking, AzureCapabilityArea.Monitoring, AzureCapabilityArea.DiagnosticSettings })
        {
            var tally = run.Tallies.GetValueOrDefault(area) ?? new Tally();
            var count = area == AzureCapabilityArea.DiagnosticSettings ? resources.Count(AzureObservations.IsMonitorable) : resources.Count(r => Area(r) == area);
            AzureCapability C(AzureCapabilityState s, string detail) => new(area, s, detail, Operation(area));
            run.Capabilities.Add(
                inventory is AzureCapabilityState.NotAuthorized or AzureCapabilityState.Failed ? C(inventory, "The inventory could not be read, so this area was not assessed.")
                : count == 0 ? C(AzureCapabilityState.NotApplicable, "None observed in the selected scope.")
                : tally.NotAuthorized > 0 && tally.Ok == 0 && tally.Total > 0 ? C(area == AzureCapabilityArea.DiagnosticSettings ? AzureCapabilityState.NotAuthorized : AzureCapabilityState.Partial,
                    area == AzureCapabilityArea.DiagnosticSettings ? "Diagnostic settings are not readable with your permissions." : $"{count} observed; detail reads were not authorized.")
                : tally.NotAuthorized > 0 || tally.Failed > 0 || tally.Throttled > 0 ? C(AzureCapabilityState.Partial,
                    $"{count} observed; {tally.Ok} detail read(s) succeeded, {tally.NotAuthorized} not authorized, {tally.Throttled} throttled, {tally.Failed} failed.")
                : area == AzureCapabilityArea.DiagnosticSettings && tally.Total < count ? C(tally.Total == 0 ? AzureCapabilityState.NotAssessed : AzureCapabilityState.Partial,
                    $"{tally.Total} of {count} monitorable resource(s) read (detail-read budget).")
                : viaFallback && tally.Total == 0 && area != AzureCapabilityArea.ManagedIdentity ? C(AzureCapabilityState.Partial, $"{count} observed (inventory fallback; properties not read).")
                : C(AzureCapabilityState.Available, area == AzureCapabilityArea.KeyVaultMetadata
                    ? $"{count} vault(s): control-plane metadata only; secrets, keys and certificates are never read."
                    : $"{count} observed{(tally.Total > 0 ? $"; {tally.Ok} detail read(s)" : "")}."));
        }
    }

    private static string Operation(AzureCapabilityArea area) => area switch
    {
        AzureCapabilityArea.Messaging => "Inventory + GET eventhubs/consumergroups/topics/queues/subscriptions",
        AzureCapabilityArea.Storage => "Inventory + GET blobServices/default/containers (names only)",
        AzureCapabilityArea.Databases => "Inventory + GET flexibleServers/databases",
        AzureCapabilityArea.Compute => "Inventory + GET sites/config/web",
        AzureCapabilityArea.DiagnosticSettings => "GET providers/Microsoft.Insights/diagnosticSettings",
        _ => "Inventory",
    };

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<(List<JsonElement> Items, ArmResult Last, int Pages)> GetAllAsync(Run run, string url, int max, CancellationToken ct)
    {
        var items = new List<JsonElement>();
        var next = url;
        ArmResult last = ArmResult.Refused("No request");
        var pages = 0;
        while (next is not null && pages < MaxPages && items.Count < max)
        {
            last = await arm.GetAsync(next, ct);
            pages++;
            Expired(run, last);
            if (!last.Ok) break;
            var body = last.Body!.Value;
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array) items.AddRange(value.EnumerateArray());
            else if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("id", out _)) items.Add(body);
            var link = body.ValueKind == JsonValueKind.Object ? AzureResourceNormalizer.Str(body, "nextLink") : null;
            next = AzureReadOnlyPolicy.RelativeNextLink(link);
            if (link is not null && next is null) run.Limitations.Add("A paging link pointed outside Azure Resource Manager and was not followed.");
        }
        return (items.Take(max).ToList(), last, pages);
    }

    private void Expired(Run run, ArmResult result)
    {
        if (result.Status == 401) run.SessionExpired = true;
    }

    private static AzureCapabilityState StateOf(ArmResult r) => r switch
    {
        { Ok: true } => AzureCapabilityState.Available,
        { NotAuthorized: true } => AzureCapabilityState.NotAuthorized,
        { Throttled: true } => AzureCapabilityState.Throttled,
        { Status: 404 } => AzureCapabilityState.NotFound,
        { Status: 0 } => AzureCapabilityState.NotAssessed,
        _ => AzureCapabilityState.Failed,
    };

    private static AzureCapabilityState Combine(List<AzureCapabilityState> states) =>
        states.Count == 0 ? AzureCapabilityState.NotAssessed
        : states.All(s => s == AzureCapabilityState.Available) ? AzureCapabilityState.Available
        : states.All(s => s == states[0]) ? states[0]
        : states.Any(s => s == AzureCapabilityState.Available) ? AzureCapabilityState.Partial
        : states.Contains(AzureCapabilityState.NotAuthorized) ? AzureCapabilityState.NotAuthorized : states[0];

    private static string Explain(ArmResult r, string what) => StateOf(r) switch
    {
        AzureCapabilityState.NotAuthorized => r.ErrorCode == "NotSignedIn" ? $"{what}: not signed in." : $"{what}: not authorized with your current permissions (HTTP {r.Status}{(r.ErrorCode is null ? "" : $", {r.ErrorCode}")}). If you need access, activate an eligible role in PIM yourself and refresh access.",
        AzureCapabilityState.Throttled => $"{what}: Azure throttled the request (HTTP 429). Try again later.",
        AzureCapabilityState.NotFound => $"{what}: not found.",
        AzureCapabilityState.NotAssessed => $"{what}: not read ({r.ErrorCode}).",
        _ => $"{what}: could not be read (HTTP {r.Status}{(r.ErrorCode is null ? "" : $", {r.ErrorCode}")}).",
    };

    private static void Track(Run run, string name, string operation, ArmResult last, int pages, int items, string? sub) =>
        run.Queries.Add(new(name, operation, last.Status, pages, items, sub, last.Ok ? "Read" : AzureEnvironmentText.Label(StateOf(last))));

    private static IEnumerable<string> Strings(JsonElement item, string name) =>
        item.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!) : [];

    private sealed class Tally { public int Ok, NotAuthorized, Throttled, Failed; public int Total => Ok + NotAuthorized + Throttled + Failed; }

    private sealed class Run(int budget)
    {
        private int _budget = budget;
        public List<AzureCapability> Capabilities { get; } = [];
        public List<AzureQueryRecord> Queries { get; } = [];
        public List<string> Limitations { get; } = [];
        public Dictionary<AzureCapabilityArea, Tally> Tallies { get; } = [];
        public bool SessionExpired { get; set; }
        public bool BudgetExhausted { get; private set; }

        public bool TakeBudget()
        {
            if (_budget <= 0) { BudgetExhausted = true; return false; }
            _budget--;
            return true;
        }

        public void Tally(AzureCapabilityArea area, ArmResult result)
        {
            if (!Tallies.TryGetValue(area, out var t)) Tallies[area] = t = new Tally();
            if (result.Ok) t.Ok++;
            else if (result.NotAuthorized) t.NotAuthorized++;
            else if (result.Throttled) t.Throttled++;
            else if (result.Status != 404) t.Failed++;
            else t.Ok++; // the parent exists but has no such child collection
        }
    }
}
