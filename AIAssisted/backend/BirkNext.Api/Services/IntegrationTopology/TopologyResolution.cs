using System.Text.RegularExpressions;
using BirkNext.Integrations;
using BirkNext.IntegrationTopology;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.IntegrationTopology;

/// <summary>Which stored source facts the topology reads: the newest snapshot of every archive (separate repositories are separate archives).
/// The archive name is provenance only — never a requirement or an identity.</summary>
public static class TopologySnapshots
{
    public static List<IqrSourceSnapshot> Latest(IEnumerable<IqrSourceSnapshot> snapshots) =>
        [.. snapshots.GroupBy(item => item.Archive.FileName, StringComparer.OrdinalIgnoreCase).Select(group => group.OrderByDescending(item => item.AnalyzedAt).First())];

    /// <summary>"src/Orders.Api/Orders.Api.csproj" → "Orders.Api": a component's project identity (exact; used only for explicit aliases).</summary>
    public static string ProjectName(string? sourceProject) =>
        string.IsNullOrWhiteSpace(sourceProject) ? "" : System.IO.Path.GetFileNameWithoutExtension(sourceProject.Replace('\\', '/').Split('/')[^1]);

    public static string[] Identities(ArchitectureComponent component) =>
        [.. new[] { ProjectName(component.SourceProject), component.LogicalName }.Where(item => item.Length > 0)];
}

/// <summary>
/// Generic cross-source relationship resolution over stored source facts (Source Analysis architecture, configuration, CI/CD and Infrastructure
/// as Code of every archive). It knows evidence patterns, never systems:
/// <list type="bullet">
/// <item>HTTP: a client (named, typed, custom wrapper or a configuration section with a base address) whose configured base address names the
/// host a target deploys under (CI/CD deployment name), or the target's local launch address, or an IaC module that injects the target module's
/// address into exactly that setting;</item>
/// <item>Service Bus / Event Hub: resource-centred — producer → named queue/topic/hub → consumer, from source wiring and from configuration keys
/// that state a direction; infrastructure declarations of the same resource strengthen the hops;</item>
/// <item>CDC: a connector topic prefix (IaC or configuration) and hubs named {prefix}.{database}.{schema}.{table} give connector → hub and captured
/// table → connector; an application data model that maps the table gives application → table;</item>
/// <item>external applications a Test Pack references: a source configuration that names the application (identifier or address); a
/// subscription section with a callback endpoint is inbound.</item>
/// </list>
/// Never: role assignments, shared namespaces or projects, similar names, comments or expected topology. Every connection carries typed evidence;
/// the projection is rebuilt on every read (<see cref="ResolverVersion"/>) and no snapshot is ever written.
/// </summary>
public sealed partial class CrossSourceTopologyProvider(IEnumerable<ITopologyProfile> profiles) : ITopologyProvider
{
    /// <summary>Version of the relationship projection. Raised when resolution rules change; stored snapshots are re-read, never rewritten.</summary>
    public const int ResolverVersion = 2;
    public int Order => 50;

    private const string ApplicationSource = "Application source";
    private const string InfrastructureSource = "Infrastructure as Code";

    [GeneratedRegex(@"(?i)^(containerAppName|containerApp|appServiceName|webAppName|functionAppName|appName|serviceName|deploymentName)$", RegexOptions.CultureInvariant)]
    private static partial Regex DeploymentKey();
    [GeneratedRegex(@"(?i)^(?<section>[A-Za-z0-9_.\-]+):(BaseUrl|BaseAddress)$", RegexOptions.CultureInvariant)]
    private static partial Regex BaseAddressKey();
    [GeneratedRegex(@"(?i)(^|:)(QueueName|TopicName|Queue|Topic|EventHub|EventHubName|EntityName)$", RegexOptions.CultureInvariant)]
    private static partial Regex EntityKey();
    [GeneratedRegex(@"(?i)(inbound|incoming|receive|consume|listen|subscri)", RegexOptions.CultureInvariant)]
    private static partial Regex ConsumesKey();
    [GeneratedRegex(@"(?i)(outbound|outgoing|send|publish|produce)", RegexOptions.CultureInvariant)]
    private static partial Regex ProducesKey();
    [GeneratedRegex(@"(?i)topic[._:\-]?prefix", RegexOptions.CultureInvariant)]
    private static partial Regex TopicPrefixKey();
    [GeneratedRegex(@"(?i)(debezium|kafka[._\-]?connect|cdc)", RegexOptions.CultureInvariant)]
    private static partial Regex CdcTool();
    [GeneratedRegex(@"(?i)(webhook|subscri|callback|hook|event)", RegexOptions.CultureInvariant)]
    private static partial Regex InboundSection();
    [GeneratedRegex(@"(?i)(endpoint|callback|url)$", RegexOptions.CultureInvariant)]
    private static partial Regex CallbackLeaf();
    [GeneratedRegex(@"[<>{}*$]", RegexOptions.CultureInvariant)]
    private static partial Regex Unresolved();

    private sealed record ComponentFact(IqrSourceSnapshot Snapshot, ArchitectureComponent Component, string Key);
    private sealed record EntryFact(IqrSourceSnapshot Snapshot, ConfigurationEntry Entry, string? ComponentKey);
    private sealed record NameFact(string Name, SourceEnvironmentLabel Environment, string File, IqrSourceSnapshot Snapshot);

    private sealed class Index
    {
        public List<ComponentFact> Components { get; } = [];
        public List<EntryFact> Entries { get; } = [];
        public Dictionary<string, List<NameFact>> DeploymentNames { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<(string Authority, IqrSourceSnapshot Snapshot, string File)>> LaunchAddresses { get; } = new(StringComparer.Ordinal);
        public List<(IqrSourceSnapshot Snapshot, InfrastructureEvidence Infrastructure)> Infrastructure { get; } = [];
    }

    public Task ContributeAsync(TopologyContext context, TopologyBuilder builder, CancellationToken ct)
    {
        var index = Build(TopologySnapshots.Latest(context.Snapshots), builder);
        Http(index, builder);
        Messaging(index, builder);
        Cdc(index, builder);
        InfrastructureResources(index, builder);
        External(index, builder, [.. profiles.SelectMany(profile => profile.References)]);
        return Task.CompletedTask;
    }

    private static TopologyProvenance Provenance(IqrSourceSnapshot snapshot, string detail) => new(TopologySource.SourceAnalysis, snapshot.Archive.FileName, detail, snapshot.AnalyzedAt);

    private static string Env(SourceEnvironmentLabel? label) => label is null || label.Kind == SourceEnvironmentKind.Default ? "default" : label.Raw.Length > 0 ? label.Raw : label.Kind.ToString();

    /// <summary>The source family of a configuration entry, from the technology Source Analysis recorded for it (never re-parsed here).</summary>
    private static string Family(ConfigurationEntry entry) =>
        new[] { "Terraform", "Bicep", "ARM", "Helm", "Kubernetes" }.Any(token => entry.Technology.Contains(token, StringComparison.OrdinalIgnoreCase)) ? InfrastructureSource : ApplicationSource;

    private static Index Build(List<IqrSourceSnapshot> snapshots, TopologyBuilder builder)
    {
        var index = new Index();
        foreach (var snapshot in snapshots)
        {
            var components = snapshot.Architecture?.Components ?? [];
            var byName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var component in components)
            {
                var key = builder.ComponentKey(component.Name, TopologySnapshots.Identities(component));
                byName.TryAdd(component.Name, key);
                index.Components.Add(new(snapshot, component, key));
            }
            if (snapshot.EvidenceDomains is not { } domains) continue;
            if (domains.Infrastructure.Formats.Count > 0) index.Infrastructure.Add((snapshot, domains.Infrastructure));
            var entries = domains.Configuration.Entries;
            foreach (var entry in entries)
            {
                var componentKey = entry.Component is { } name && byName.TryGetValue(name, out var found) ? found : null;
                index.Entries.Add(new(snapshot, entry, componentKey));
                var leaf = entry.Key.Split(':')[^1];
                if (componentKey is not null && leaf.Equals("applicationUrl", StringComparison.OrdinalIgnoreCase) && entry.ValueKind == ConfigurationValueKind.Url
                    && entry.ValuePreviewSafe is { } launch && Uri.TryCreate(launch, UriKind.Absolute, out var launchUri))
                    Add(index.LaunchAddresses, componentKey, ($"{launchUri.Host}:{launchUri.Port}".ToLowerInvariant(), snapshot, entry.File));
                if (entry.Component is null && DeploymentKey().IsMatch(leaf) && entry.ValuePreviewSafe is { } deployment && !Unresolved().IsMatch(deployment)
                    && DeploymentOwner(snapshot, entries, entry, byName) is { } owner)
                    Add(index.DeploymentNames, owner, new NameFact(deployment.Trim(), entry.Environment, entry.File, snapshot));
            }
        }
        return index;
    }

    private static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(value);
    }

    /// <summary>
    /// The component a deployment setting (e.g. a pipeline's containerAppName) belongs to: the project the same file names (projectPath), or
    /// the only component under the file's top-level folder. Anything ambiguous belongs to nobody.
    /// </summary>
    private static string? DeploymentOwner(IqrSourceSnapshot snapshot, List<ConfigurationEntry> entries, ConfigurationEntry entry, Dictionary<string, string> byName)
    {
        var components = snapshot.Architecture?.Components ?? [];
        var project = entries.FirstOrDefault(item => item.File == entry.File && Regex.IsMatch(item.Key.Split(':')[^1], "(?i)^project(Path|File)?$") && item.ValuePreviewSafe is { Length: > 0 })?.ValuePreviewSafe;
        if (project is not null && components.Where(c => TopologySnapshots.ProjectName(c.SourceProject).Equals(TopologySnapshots.ProjectName(project), StringComparison.OrdinalIgnoreCase)).ToList() is [var named])
            return byName.GetValueOrDefault(named.Name);
        var folder = entry.File.Replace('\\', '/').Split('/') is { Length: > 1 } parts ? parts[0] + "/" : null;
        if (folder is null) return null;
        return components.Where(c => c.SourceProject.Replace('\\', '/').StartsWith(folder, StringComparison.OrdinalIgnoreCase)
            && c.ComponentType is not (ArchitectureComponentType.Library or ArchitectureComponentType.Cli or ArchitectureComponentType.ExternalSystem)).ToList() is [var only]
            ? byName.GetValueOrDefault(only.Name) : null;
    }

    // ── HTTP ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The client sections a component uses: from source clients (named/typed/custom: the client name or its configuration key) and from
    /// configuration sections that declare a base address. Code-level clients are marked so a configuration-only section is only supporting.</summary>
    private static IEnumerable<(string Section, bool InCode)> ClientSections(ComponentFact fact, Index index)
    {
        var sections = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in fact.Snapshot.Architecture?.Dependencies.Where(item => item.FromComponentId == fact.Component.Id
            && item.DependencyType is ArchitectureDependencyType.Http or ArchitectureDependencyType.GraphQl) ?? [])
        {
            var named = Regex.Match(dependency.TargetReference ?? "", "^HttpClient '([^']+)'$");
            var section = named.Success ? named.Groups[1].Value
                : dependency.ConfigurationReference is { } key && BaseAddressKey().Match(key) is { Success: true } match ? match.Groups["section"].Value : null;
            if (section is not null) sections[section] = true;
        }
        foreach (var entry in index.Entries.Where(item => item.ComponentKey == fact.Key && item.Snapshot == fact.Snapshot))
            if (BaseAddressKey().Match(entry.Entry.Key) is { Success: true } match) sections.TryAdd(match.Groups["section"].Value, false);
        return sections.Select(pair => (pair.Key, pair.Value));
    }

    private void Http(Index index, TopologyBuilder builder)
    {
        foreach (var fact in index.Components)
            foreach (var (section, inCode) in ClientSections(fact, index))
            {
                var addresses = index.Entries.Where(item => item.ComponentKey == fact.Key && item.Snapshot == fact.Snapshot && item.Entry.ValueKind == ConfigurationValueKind.Url
                    && BaseAddressKey().Match(item.Entry.Key) is { Success: true } match && match.Groups["section"].Value.Equals(section, StringComparison.OrdinalIgnoreCase)).ToList();
                var found = new Dictionary<string, List<TopologyEvidence>>(StringComparer.Ordinal);
                foreach (var address in addresses)
                {
                    if (address.Entry.ValuePreviewSafe is not { } value || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) continue;
                    var label = uri.Host.Split('.')[0];
                    foreach (var (target, names) in index.DeploymentNames.Where(pair => pair.Key != fact.Key))
                        foreach (var name in names.Where(name => name.Name.Equals(label, StringComparison.OrdinalIgnoreCase)))
                            Add(found, target, new TopologyEvidence("http-client-target-host", TopologyEvidenceStrength.VeryStrong, ApplicationSource,
                                $"{fact.Component.Name} sets {section} base address ({Env(address.Entry.Environment)}) to host {label}; {builder.NameOf(target)} deploys as {name.Name} ({name.File}).",
                                address.Snapshot.Archive.FileName));
                    if (uri.IsLoopback)
                    {
                        var authority = $"{uri.Host}:{uri.Port}".ToLowerInvariant();
                        foreach (var (target, launches) in index.LaunchAddresses.Where(pair => pair.Key != fact.Key))
                            foreach (var launch in launches.Where(item => item.Authority == authority).Take(1))
                                Add(found, target, new TopologyEvidence("http-client-local-address", launch.Snapshot == fact.Snapshot ? TopologyEvidenceStrength.VeryStrong : TopologyEvidenceStrength.Strong,
                                    ApplicationSource, $"{fact.Component.Name} sets {section} base address ({Env(address.Entry.Environment)}) to {authority}, the launch address of {builder.NameOf(target)} ({launch.File}).",
                                    address.Snapshot.Archive.FileName));
                    }
                }
                foreach (var (target, evidence) in Injections(index, builder, fact, section)) Add(found, target, evidence);
                if (found.Count == 0)
                {
                    if (!inCode && addresses.Count == 0 && index.Entries.Any(item => item.ComponentKey == fact.Key && BaseAddressKey().Match(item.Entry.Key) is { Success: true } m
                        && m.Groups["section"].Value.Equals(section, StringComparison.OrdinalIgnoreCase)))
                        builder.Interface(fact.Key, TopologyInterfaceType.Rest, "HTTP", TopologyDirection.Calls, section, null, TopologyStatus.Discovered,
                            Provenance(fact.Snapshot, $"Configuration declares a base address section {section}"), review: true,
                            note: $"The base address of {section} is not a concrete value in source (empty or a placeholder); the called component is not resolved.");
                    continue;
                }
                if (found.Count > 1)
                {
                    builder.Interface(fact.Key, TopologyInterfaceType.Rest, "HTTP", TopologyDirection.Calls, section, null, TopologyStatus.Discovered, Provenance(fact.Snapshot, $"Client {section}"),
                        conflict: true, note: $"Evidence names {found.Count} different targets for {section}: {string.Join(", ", found.Keys.Select(builder.NameOf))}. No connection is chosen.");
                    continue;
                }
                var (to, typed) = found.Single();
                var existing = builder.ConnectionKeys(fact.Key, to).FirstOrDefault(key => key.Contains("|Rest|", StringComparison.Ordinal) || key.Contains("|GraphQl|", StringComparison.Ordinal));
                if (existing is not null) { foreach (var item in typed) builder.Strengthen(existing, item); continue; }
                var calls = builder.Interface(fact.Key, TopologyInterfaceType.Rest, "HTTP", TopologyDirection.Calls, section, null, TopologyStatus.Discovered,
                    Provenance(fact.Snapshot, $"Client {section}{(inCode ? "" : " (declared by configuration)")}"));
                var connection = builder.Connection(fact.Key, to, TopologyInterfaceType.Rest, "HTTP", section, TopologyStatus.Discovered,
                    Provenance(fact.Snapshot, $"Client {section} resolved across sources"), fromInterface: calls, evidence: typed[0]);
                foreach (var item in typed.Skip(1)) builder.Strengthen(connection, item);
                if (!inCode) builder.Strengthen(connection, new("configuration-client", TopologyEvidenceStrength.Supporting, ApplicationSource,
                    $"No client registration for {section} was found in code; the section is declared by configuration.", fact.Snapshot.Archive.FileName));
            }
    }

    /// <summary>IaC endpoint injections into exactly this client setting: the injecting module and the injected module must each map to one
    /// component through their deployment names (a template with '*' for per-environment parts).</summary>
    private static IEnumerable<(string Target, TopologyEvidence Evidence)> Injections(Index index, TopologyBuilder builder, ComponentFact fact, string section)
    {
        foreach (var (snapshot, infrastructure) in index.Infrastructure)
        {
            var modules = infrastructure.Modules.ToDictionary(module => module.Id, StringComparer.Ordinal);
            foreach (var injection in infrastructure.EndpointInjections.Where(item => item.Key.Split(':')[0].Equals(section, StringComparison.OrdinalIgnoreCase)))
            {
                if (!modules.TryGetValue(injection.FromId, out var from) || !Deploys(index, from, fact.Key)) continue;
                if (!modules.TryGetValue(injection.ToId, out var to) || index.DeploymentNames.Where(pair => pair.Key != fact.Key && Deploys(index, to, pair.Key)).Select(pair => pair.Key).ToList() is not [var target])
                    continue;
                yield return (target, new TopologyEvidence("iac-address-injection", TopologyEvidenceStrength.VeryStrong, InfrastructureSource,
                    $"{from.Name} sets {injection.Key} from {to.Name}.{injection.Attribute} ({injection.File}); {from.Name} deploys {fact.Component.Name} and {to.Name} deploys {builder.NameOf(target)} (deployment names {from.DeploymentName} / {to.DeploymentName}).",
                    snapshot.Archive.FileName));
            }
        }
    }

    private static bool Deploys(Index index, InfrastructureModule module, string componentKey) =>
        module.DeploymentName is { } template && index.DeploymentNames.TryGetValue(componentKey, out var names) && names.Any(name => TemplateMatches(template, name.Name));

    /// <summary>"ca-orders-*-001" matches "ca-orders-dev-001" ('*' = one or more characters without dots); literal parts must match exactly.</summary>
    public static bool TemplateMatches(string template, string name) =>
        Regex.IsMatch(name, "^" + string.Join("[^.]+", template.Split('*').Select(Regex.Escape)) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ── Messaging (resource-centred) ────────────────────────────────────────────────────────────────────────────────────

    public static (string Kind, string Label) ResourceKind(string text) =>
        text.Contains("eventhub", StringComparison.OrdinalIgnoreCase) || text.Contains("event hub", StringComparison.OrdinalIgnoreCase) || text.Contains("kafka", StringComparison.OrdinalIgnoreCase)
            ? ("eventhub", "Event Hub")
            : text.Contains("topic", StringComparison.OrdinalIgnoreCase) ? ("servicebus", "Service Bus topic")
            : text.Contains("queue", StringComparison.OrdinalIgnoreCase) ? ("servicebus", "Service Bus queue") : ("servicebus", "Service Bus entity");

    /// <summary>Configuration that names a queue/topic/hub under a key stating the direction (inbound/receive/consume … or outbound/send/publish …)
    /// becomes a hop to or from that resource. A key without a direction, a placeholder or an empty value adds nothing.</summary>
    private static void Messaging(Index index, TopologyBuilder builder)
    {
        foreach (var item in index.Entries.Where(item => item.ComponentKey is not null && EntityKey().IsMatch(item.Entry.Key)
            && item.Entry.ValueKind is ConfigurationValueKind.EntityName or ConfigurationValueKind.Text && item.Entry.ValuePreviewSafe is { Length: > 0 } value && !Unresolved().IsMatch(value)))
        {
            var key = item.Entry.Key;
            var consumes = ConsumesKey().IsMatch(key);
            var produces = ProducesKey().IsMatch(key);
            if (consumes == produces) continue;
            foreach (var entity in item.Entry.ValuePreviewSafe!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var (kind, label) = ResourceKind(key);
                var provenance = Provenance(item.Snapshot, $"Configuration {key} ({Env(item.Entry.Environment)})");
                var resource = builder.Resource(kind, entity, label, TopologyStatus.Discovered, provenance);
                var evidence = new TopologyEvidence(consumes ? "configured-consumer" : "configured-producer", TopologyEvidenceStrength.Strong, Family(item.Entry),
                    $"{builder.NameOf(item.ComponentKey!)} configuration names {label.ToLowerInvariant()} {entity} as {(consumes ? "inbound" : "outbound")} ({key}, {Env(item.Entry.Environment)}).",
                    item.Snapshot.Archive.FileName);
                var own = builder.Interface(item.ComponentKey!, TopologyInterfaceType.Messaging, label, consumes ? TopologyDirection.Consumes : TopologyDirection.Produces, entity, null,
                    TopologyStatus.Discovered, provenance);
                if (consumes) builder.Connection(resource, item.ComponentKey!, TopologyInterfaceType.Messaging, label, entity, TopologyStatus.Discovered, provenance, toInterface: own, evidence: evidence);
                else builder.Connection(item.ComponentKey!, resource, TopologyInterfaceType.Messaging, label, entity, TopologyStatus.Discovered, provenance, fromInterface: own, evidence: evidence);
            }
        }
    }

    // ── CDC ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void Cdc(Index index, TopologyBuilder builder)
    {
        var prefixes = index.Entries.Where(item => TopicPrefixKey().IsMatch(item.Entry.Key) && CdcTool().IsMatch(item.Entry.Key + " " + item.Entry.File)
            && item.Entry.ValuePreviewSafe is { Length: > 0 } value && !Unresolved().IsMatch(value)).ToList();
        if (prefixes.Count == 0) return;
        var hubs = builder.ComponentKeys.Where(key => key.StartsWith(TopologyBuilder.ResourceKey("eventhub", ""), StringComparison.Ordinal)).Select(key => (Key: key, Name: builder.NameOf(key))).ToList();
        var models = index.Components.Select(item => item.Snapshot).Distinct().Where(item => item.DatabaseArchitecture is not null).ToList();
        foreach (var prefix in prefixes)
        {
            var value = prefix.Entry.ValuePreviewSafe!.Trim();
            var tool = CdcTool().Match(prefix.Entry.Key + " " + prefix.Entry.File).Value.ToLowerInvariant() switch
            {
                "debezium" => "Debezium",
                var other when other.StartsWith("kafka", StringComparison.Ordinal) => "Kafka Connect",
                _ => "CDC",
            };
            var family = Family(prefix.Entry);
            var declared = $"{tool} topic prefix {value} ({prefix.Entry.Key}, {Env(prefix.Entry.Environment)}, {prefix.Entry.File})";
            foreach (var hub in hubs.Where(hub => hub.Name.StartsWith(value + ".", StringComparison.OrdinalIgnoreCase)))
            {
                var parts = hub.Name[(value.Length + 1)..].Split('.');
                if (parts.Length < 2) continue;
                var database = parts[0];
                var table = string.Join('.', parts[1..]);
                var connector = builder.Resource("connector", $"{tool} connector", "CDC connector", TopologyStatus.Discovered, Provenance(prefix.Snapshot, declared));
                var toHub = builder.Connection(connector, hub.Key, TopologyInterfaceType.Messaging, "Event Hub", hub.Name, TopologyStatus.Discovered, Provenance(prefix.Snapshot, declared),
                    evidence: new("cdc-topic-prefix", TopologyEvidenceStrength.Strong, family, $"{declared} names the hub {hub.Name} for captured table {database}.{table}.", prefix.Snapshot.Archive.FileName));
                var consumers = builder.ComponentKeys.Where(key => builder.ConnectionKeys(hub.Key, key).Count > 0).ToList();
                if (consumers.Count > 0)
                    builder.Strengthen(toHub, new("cdc-topic-consumer", TopologyEvidenceStrength.Strong, ApplicationSource,
                        $"{string.Join(", ", consumers.Select(builder.NameOf))} consume{(consumers.Count == 1 ? "s" : "")} {hub.Name}, the connector topic of {database}.{table}."));
                // The consumer's hub name and the connector's declared prefix are independent sources that agree on the same hub.
                foreach (var consumer in consumers)
                    foreach (var hop in builder.ConnectionKeys(hub.Key, consumer))
                        builder.Strengthen(hop, new("cdc-topic-prefix", TopologyEvidenceStrength.Strong, family,
                            $"{declared}: {hub.Name} is the connector topic of {database}.{table}.", prefix.Snapshot.Archive.FileName));
                var captured = builder.Resource("table", $"{database}.{table}", "Captured table", TopologyStatus.Discovered, Provenance(prefix.Snapshot, declared));
                var fromTable = builder.Connection(captured, connector, TopologyInterfaceType.Database, "CDC", $"{database}.{table}", TopologyStatus.Discovered, Provenance(prefix.Snapshot, declared),
                    evidence: new("cdc-captured-table", TopologyEvidenceStrength.Strong, family, $"{declared}: topic {hub.Name} follows {{prefix}}.{{database}}.{{schema}}.{{table}}, so the connector captures {database}.{table}.",
                        prefix.Snapshot.Archive.FileName));
                if (consumers.Count > 0)
                    builder.Strengthen(fromTable, new("cdc-topic-consumer", TopologyEvidenceStrength.Strong, ApplicationSource,
                        $"{string.Join(", ", consumers.Select(builder.NameOf))} consume{(consumers.Count == 1 ? "s" : "")} {hub.Name}, the topic of {database}.{table}."));
                foreach (var (owner, evidence) in TableOwners(index, models, database, table))
                    builder.Connection(owner, captured, TopologyInterfaceType.Database, "Database", $"{database}.{table}", TopologyStatus.Discovered,
                        new(TopologySource.SourceAnalysis, evidence.Reference ?? "", evidence.Detail), evidence: evidence);
            }
        }
    }

    /// <summary>Applications whose data model maps the captured table: database name and schema.table match = very strong; table only = strong.</summary>
    private static IEnumerable<(string Owner, TopologyEvidence Evidence)> TableOwners(Index index, List<IqrSourceSnapshot> models, string database, string table)
    {
        foreach (var snapshot in models)
            foreach (var model in snapshot.DatabaseArchitecture!.Databases)
            {
                var sameDatabase = new[] { model.ConfiguredDatabaseName, model.PhysicalName, model.LogicalName }.Any(name => string.Equals(name, database, StringComparison.OrdinalIgnoreCase));
                foreach (var mapped in model.Schemas.SelectMany(schema => schema.Tables.Select(t => (Schema: t.SchemaName ?? schema.Name ?? "dbo", Table: t)))
                    .Where(item => string.Equals($"{item.Schema}.{item.Table.PhysicalName ?? item.Table.LogicalName}", table, StringComparison.OrdinalIgnoreCase)))
                {
                    var project = mapped.Table.SourceProject.Length > 0 ? mapped.Table.SourceProject : model.SourceProject;
                    if (index.Components.FirstOrDefault(item => item.Snapshot == snapshot && item.Component.SourceProject == project) is not { } owner) continue;
                    yield return (owner.Key, new TopologyEvidence("data-model-table", sameDatabase ? TopologyEvidenceStrength.VeryStrong : TopologyEvidenceStrength.Strong, ApplicationSource,
                        $"{owner.Component.Name}'s data model maps {mapped.Schema}.{mapped.Table.PhysicalName ?? mapped.Table.LogicalName}{(sameDatabase ? $" in database {database}" : " (database name not stated)")}"
                        + $"{(model.DbContext is { } context ? $" ({context})" : "")}.", snapshot.Archive.FileName));
                }
            }
    }

    // ── Infrastructure declarations of messaging resources ──────────────────────────────────────────────────────────────

    /// <summary>A queue, topic or hub the IaC declares by the same name strengthens every hop through that resource node. It never creates a
    /// hop: who produces or consumes is application evidence (a role assignment alone proves nothing).</summary>
    private static void InfrastructureResources(Index index, TopologyBuilder builder)
    {
        foreach (var (snapshot, infrastructure) in index.Infrastructure)
            foreach (var resource in infrastructure.Resources)
            {
                var kind = InfrastructureIdentity.KindOfType(resource.ResourceType);
                var token = kind switch
                {
                    InfrastructureResourceKind.EventHub => "eventhub",
                    InfrastructureResourceKind.ServiceBusQueue or InfrastructureResourceKind.ServiceBusTopic => "servicebus",
                    _ => null,
                };
                if (token is null) continue;
                foreach (var name in resource.EnvironmentNames.Select(item => item.Name).Prepend(resource.DeclaredName ?? "").Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var key = TopologyBuilder.ResourceKey(token, name);
                    if (!builder.Has(key)) continue;
                    var evidence = new TopologyEvidence("iac-declares-resource", TopologyEvidenceStrength.Strong, InfrastructureSource,
                        $"{resource.CategoryDetail} {name} is declared ({resource.File}:{resource.Line}).", snapshot.Archive.FileName);
                    foreach (var other in builder.ComponentKeys.ToList())
                    {
                        foreach (var hop in builder.ConnectionKeys(other, key)) builder.Strengthen(hop, evidence);
                        foreach (var hop in builder.ConnectionKeys(key, other)) builder.Strengthen(hop, evidence);
                    }
                }
            }
    }

    // ── External applications a Test Pack references ────────────────────────────────────────────────────────────────────

    private static void External(Index index, TopologyBuilder builder, List<TopologyExternalReference> references)
    {
        foreach (var reference in references)
        {
            TopologyReferenceFacts? facts;
            try { facts = reference.Load(); } catch (Exception ex) when (ex is not OutOfMemoryException) { facts = null; }
            if (facts is null || (facts.Identifiers.Count == 0 && facts.Addresses.Count == 0)) continue;
            string? system = null;
            foreach (var item in index.Entries.Where(item => item.ComponentKey is not null && item.Entry.ValuePreviewSafe is { Length: > 0 }))
            {
                var value = item.Entry.ValuePreviewSafe!;
                var identifier = facts.Identifiers.FirstOrDefault(id => value.Equals(id, StringComparison.OrdinalIgnoreCase) || value.EndsWith("/" + id, StringComparison.OrdinalIgnoreCase));
                var address = facts.Addresses.FirstOrDefault(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (identifier is null && address is null) continue;
                system ??= builder.Component(reference.System, TopologyStatus.Expected, new(TopologySource.DomainProfile, reference.RoleId, facts.Detail), kind: TopologyComponentKind.External);
                var section = item.Entry.Key.Contains(':') ? item.Entry.Key[..item.Entry.Key.LastIndexOf(':')] : "";
                var inbound = InboundSection().IsMatch(section);
                var callback = inbound && index.Entries.Any(other => other.ComponentKey == item.ComponentKey && other.Entry.File == item.Entry.File && other.Entry != item.Entry
                    && other.Entry.Key.StartsWith(section + ":", StringComparison.OrdinalIgnoreCase) && CallbackLeaf().IsMatch(other.Entry.Key) && other.Entry.ValueKind == ConfigurationValueKind.Url);
                var named = identifier ?? address!;
                var evidence = new TopologyEvidence(callback ? "configured-subscription" : "configuration-names-application", callback ? TopologyEvidenceStrength.VeryStrong : TopologyEvidenceStrength.Strong,
                    ApplicationSource, callback
                        ? $"{builder.NameOf(item.ComponentKey!)} configures a subscription ({section}, {Env(item.Entry.Environment)}) whose source names {named} and whose callback endpoint is its own."
                        : $"{builder.NameOf(item.ComponentKey!)} configuration names {named} ({item.Entry.Key}, {Env(item.Entry.Environment)}).", item.Snapshot.Archive.FileName);
                var (from, to) = inbound ? (system, item.ComponentKey!) : (item.ComponentKey!, system);
                var connection = builder.Connection(from, to, TopologyInterfaceType.ExternalApplication, "HTTP", named, TopologyStatus.Discovered,
                    Provenance(item.Snapshot, evidence.Detail), evidence: evidence);
                builder.Strengthen(connection, new("reference-identity", TopologyEvidenceStrength.Supporting, "Built-in reference", $"{reference.System}: {facts.Detail}"));
            }
        }
    }
}
