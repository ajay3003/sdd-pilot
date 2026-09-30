using BirkNext.SourceArchitecture;

namespace BirkNext.Web.Services;

public enum SourceArchitectureView { System, Integrations, Messaging, Data }

public sealed record ArchitectureFilters
{
    public string ComponentType { get; init; } = "";
    public string Technology { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string Module { get; init; } = "";
    public bool ConfirmedOnly { get; init; }
    public bool IncludeInferred { get; init; } = true;
    public bool HideUnresolved { get; init; }
    public bool ShowLibraries { get; init; }
}

/// <summary>Node kinds: component, channel, datastore, external, library, unresolved.</summary>
public sealed record ArchitectureGraphNode(string Id, string Name, string Subtitle, string Kind, string State);

public sealed record ArchitectureGraphEdge(string Id, string From, string To, string Label, string State, string DependencyId);

public sealed record ArchitectureGraph(List<ArchitectureGraphNode> Nodes, List<ArchitectureGraphEdge> Edges);

/// <summary>
/// Views over one source-architecture snapshot. Pure: the same snapshot and filters always give the same graph. The System view shows
/// runtime relationships only — project references, packages, configuration and orchestration relationships live in Dependencies.
/// </summary>
public static class ArchitecturePresentation
{
    private static readonly ArchitectureDependencyType[] Messaging =
        [ArchitectureDependencyType.EventHubProduce, ArchitectureDependencyType.EventHubConsume, ArchitectureDependencyType.ServiceBusPublish, ArchitectureDependencyType.ServiceBusConsume];
    private static readonly ArchitectureDependencyType[] Data =
        [ArchitectureDependencyType.DatabaseReadWrite, ArchitectureDependencyType.BlobReadWrite, ArchitectureDependencyType.CheckpointStore, ArchitectureDependencyType.Cache];
    private static readonly ArchitectureDependencyType[] Runtime =
    [
        ArchitectureDependencyType.Http, ArchitectureDependencyType.GraphQl, .. Messaging, .. Data,
        ArchitectureDependencyType.KeyVault, ArchitectureDependencyType.Auth, ArchitectureDependencyType.Observability,
    ];

    public static IReadOnlyList<ArchitectureDependencyType> TypesOf(SourceArchitectureView view) => view switch
    {
        SourceArchitectureView.Messaging => Messaging,
        SourceArchitectureView.Data => Data,
        SourceArchitectureView.Integrations => Runtime.Where(t => t is not (ArchitectureDependencyType.DatabaseReadWrite or ArchitectureDependencyType.Cache)).ToArray(),
        _ => Runtime,
    };

    public static bool IsConsume(ArchitectureDependencyType type) => type is ArchitectureDependencyType.EventHubConsume or ArchitectureDependencyType.ServiceBusConsume;

    public static bool PassesState(ArchitectureEvidenceState state, ArchitectureFilters f) => state switch
    {
        ArchitectureEvidenceState.Conflict => true,
        ArchitectureEvidenceState.Unresolved => !f.HideUnresolved && !f.ConfirmedOnly,
        ArchitectureEvidenceState.Inferred => f.IncludeInferred && !f.ConfirmedOnly,
        _ => true,
    };

    public static ArchitectureGraph Build(ArchitectureSnapshot a, SourceArchitectureView view, ArchitectureFilters f)
    {
        var components = a.Components.Where(c => (f.ComponentType == "" || c.ComponentType.ToString() == f.ComponentType)
            && (f.Technology == "" || c.Technologies.Contains(f.Technology)) && (f.Module == "" || c.Module == f.Module)).ToDictionary(c => c.Id);
        var types = TypesOf(view).ToHashSet();
        if (f.ShowLibraries && view == SourceArchitectureView.System) types.Add(ArchitectureDependencyType.ProjectReference);
        var edges = new List<ArchitectureGraphEdge>();
        var targets = new Dictionary<string, ArchitectureGraphNode>(StringComparer.Ordinal);
        var unresolved = new Dictionary<string, List<ArchitectureDependency>>(StringComparer.Ordinal);
        foreach (var d in a.Dependencies.Where(d => types.Contains(d.DependencyType) && components.ContainsKey(d.FromComponentId) && PassesState(d.EvidenceState, f)
                     && (f.Protocol == "" || d.DependencyType.ToString() == f.Protocol)))
        {
            ArchitectureGraphNode? target;
            if (d.ToId is null)
            {
                // One node per component for everything it names but nobody resolved: the graph stays readable, the list stays complete.
                if (!unresolved.TryGetValue(d.FromComponentId, out var list)) unresolved[d.FromComponentId] = list = [];
                list.Add(d);
                continue;
            }
            if (!Node(a, d.ToId, out target)) continue;
            if (target.Kind == "component" && !components.ContainsKey(target.Id)) continue;
            if (target.Kind == "channel" && a.MessagingChannels.FirstOrDefault(c => c.Id == target.Id) is { NameUnresolved: true } && f.HideUnresolved) continue;
            if (target.Kind == "library" && !f.ShowLibraries) continue;
            targets[target.Id] = target;
            var (from, to) = IsConsume(d.DependencyType) ? (target.Id, d.FromComponentId) : (d.FromComponentId, target.Id);
            edges.Add(new ArchitectureGraphEdge(d.Id, from, to, EdgeLabel(d), d.EvidenceState.ToString(), d.Id));
        }
        foreach (var (component, list) in unresolved)
        {
            var conflict = list.Any(d => d.EvidenceState == ArchitectureEvidenceState.Conflict);
            var id = UnresolvedNodeId(component);
            targets[id] = new(id, $"Unresolved targets ({list.Count})", conflict ? "Includes conflicting targets" : "Named in source, not resolved", "unresolved", conflict ? "Conflict" : "Unresolved");
            edges.Add(new ArchitectureGraphEdge(UnresolvedEdgeId(component), component, id, $"{list.Count} unresolved{(conflict ? " / conflict" : "")}", conflict ? "Conflict" : "Unresolved", list[0].Id));
        }
        var connected = edges.SelectMany(e => new[] { e.From, e.To }).ToHashSet(StringComparer.Ordinal);
        var nodes = components.Values.Where(c => view is SourceArchitectureView.System or SourceArchitectureView.Integrations || connected.Contains(c.Id))
            .Select(c => ComponentNode(c)).Concat(targets.Values.Where(t => t.Kind != "component")).OrderBy(n => n.Kind == "component" ? 0 : 1).ThenBy(n => n.Name, StringComparer.Ordinal).ToList();
        return new ArchitectureGraph(nodes, edges);
    }

    private static ArchitectureGraphNode ComponentNode(ArchitectureComponent c) =>
        new(c.Id, c.Name, $"{Label(c.ComponentType)} · {c.RuntimeType}", "component", c.EvidenceState.ToString());

    public static string UnresolvedNodeId(string componentId) => $"unresolved:{componentId}";
    public static string UnresolvedEdgeId(string componentId) => $"unresolved-edge:{componentId}";

    /// <summary>The unresolved or conflicting dependencies grouped behind an "Unresolved targets" node or edge.</summary>
    public static List<ArchitectureDependency> UnresolvedOf(ArchitectureSnapshot a, string nodeOrEdgeId)
    {
        var component = nodeOrEdgeId.StartsWith("unresolved-edge:", StringComparison.Ordinal) ? nodeOrEdgeId["unresolved-edge:".Length..]
            : nodeOrEdgeId.StartsWith("unresolved:", StringComparison.Ordinal) ? nodeOrEdgeId["unresolved:".Length..] : null;
        return component is null ? [] : a.Dependencies.Where(d => d.FromComponentId == component && d.ToId is null).ToList();
    }

    public static bool Node(ArchitectureSnapshot a, string id, out ArchitectureGraphNode node)
    {
        if (a.Components.FirstOrDefault(c => c.Id == id) is { } c) { node = ComponentNode(c); return true; }
        if (a.MessagingChannels.FirstOrDefault(x => x.Id == id) is { } ch) { node = new(ch.Id, ch.Name, ChannelLabel(ch.Type), "channel", ch.Confidence.ToString()); return true; }
        if (a.DataStores.FirstOrDefault(x => x.Id == id) is { } s) { node = new(s.Id, s.LogicalName, $"{StoreLabel(s.StoreType)} · {s.Usage}", "datastore", s.Confidence.ToString()); return true; }
        if (a.ExternalSystems.FirstOrDefault(x => x.Id == id) is { } e) { node = new(e.Id, e.Name, $"External · {e.Type}", "external", e.Confidence.ToString()); return true; }
        if (a.SharedLibraries.FirstOrDefault(x => x.Id == id) is { } l) { node = new(l.Id, l.Name, "Library", "library", "Confirmed"); return true; }
        node = new(id, id, "Unknown", "unresolved", "Unresolved");
        return false;
    }

    public static string Name(ArchitectureSnapshot a, string? id) => id is null ? "Unresolved" : Node(a, id, out var n) ? n.Name : id;

    public static string EdgeLabel(ArchitectureDependency d)
    {
        var type = d.DependencyType switch
        {
            ArchitectureDependencyType.Http => "HTTP",
            ArchitectureDependencyType.GraphQl => "GraphQL",
            ArchitectureDependencyType.EventHubProduce => "Event Hub produce",
            ArchitectureDependencyType.EventHubConsume => "Event Hub consume",
            ArchitectureDependencyType.ServiceBusPublish => "Service Bus publish",
            ArchitectureDependencyType.ServiceBusConsume => "Service Bus consume",
            ArchitectureDependencyType.DatabaseReadWrite => "DB read/write",
            ArchitectureDependencyType.BlobReadWrite => "Blob",
            ArchitectureDependencyType.CheckpointStore => "checkpoint store",
            ArchitectureDependencyType.Cache => "cache",
            ArchitectureDependencyType.KeyVault => "secrets",
            ArchitectureDependencyType.Auth => $"auth ({d.Protocol})",
            ArchitectureDependencyType.Observability => "telemetry",
            ArchitectureDependencyType.ProjectReference => "project reference",
            ArchitectureDependencyType.ConfigurationDependency => "configured endpoint",
            ArchitectureDependencyType.OrchestrationDependency => "orchestration",
            _ => d.DependencyType.ToString(),
        };
        var framework = d.Framework is { } fw && fw is not ("Named HttpClient" or "Typed HttpClient") ? $" · {fw}" : "";
        var state = d.EvidenceState switch
        {
            ArchitectureEvidenceState.Inferred => " (inferred)",
            ArchitectureEvidenceState.Unresolved => " (unresolved)",
            ArchitectureEvidenceState.Conflict => " (conflict)",
            _ => "",
        };
        return type + framework + state;
    }

    public static string Label(ArchitectureComponentType type) => type switch
    {
        ArchitectureComponentType.Api => "API",
        ArchitectureComponentType.Cli => "CLI",
        ArchitectureComponentType.BackgroundService => "Background service",
        ArchitectureComponentType.ScheduledJob => "Scheduled job",
        ArchitectureComponentType.ExternalSystem => "External system",
        _ => type.ToString(),
    };

    public static string ChannelLabel(MessagingChannelType type) => type switch
    {
        MessagingChannelType.EventHub => "Event Hub",
        MessagingChannelType.ServiceBusTopic => "Service Bus topic",
        MessagingChannelType.ServiceBusQueue => "Service Bus queue",
        MessagingChannelType.ServiceBusEntity => "Service Bus entity (kind unknown)",
        MessagingChannelType.KafkaTopic => "Kafka topic",
        MessagingChannelType.RabbitQueue => "RabbitMQ queue",
        _ => "Messaging channel",
    };

    public static string StoreLabel(DataStoreType type) => type switch
    {
        DataStoreType.SqlServer => "SQL Server",
        DataStoreType.PostgreSql => "PostgreSQL",
        DataStoreType.BlobStorage => "Blob storage",
        _ => type.ToString(),
    };

    public static string StateLabel(ArchitectureEvidenceState state) => state == ArchitectureEvidenceState.StronglySupported ? "Strongly supported" : state.ToString();

    /// <summary>Nodes reachable from <paramref name="id"/> along edge direction (downstream) or against it (upstream), up to <paramref name="depth"/> levels.</summary>
    public static HashSet<string> Reachable(ArchitectureGraph g, string id, bool downstream, int depth)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        var frontier = new HashSet<string>(StringComparer.Ordinal) { id };
        for (var level = 0; level < depth && frontier.Count > 0; level++)
        {
            var next = g.Edges.Where(e => frontier.Contains(downstream ? e.From : e.To)).Select(e => downstream ? e.To : e.From).Where(n => seen.Add(n)).ToHashSet(StringComparer.Ordinal);
            frontier = next;
        }
        return seen;
    }

    /// <summary>Shortest directed path over the graph's extracted edges only; null when none — which never means that no path exists.</summary>
    public static List<string>? FindPath(ArchitectureGraph g, string from, string to)
    {
        if (from == to) return [from];
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<string>([from]);
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in g.Edges.Where(e => e.From == current).Select(e => e.To))
            {
                if (!seen.Add(next)) continue;
                previous[next] = current;
                if (next == to)
                {
                    var path = new List<string> { to };
                    while (path[0] != from) path.Insert(0, previous[path[0]]);
                    return path;
                }
                queue.Enqueue(next);
            }
        }
        return null;
    }

    public const string NoPath = "No source-derived path found.";

    /// <summary>Architecture search: components, channels, datastores, external systems and configuration keys — never arbitrary source code.</summary>
    public static List<ArchitectureGraphNode> Search(ArchitectureSnapshot a, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        bool Hit(string? v) => v?.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase) == true;
        var ids = a.Components.Where(c => Hit(c.Name) || Hit(c.LogicalName) || Hit(c.SourceProject)).Select(c => c.Id)
            .Concat(a.MessagingChannels.Where(c => Hit(c.Name) || c.EnvironmentVariants.Values.Any(Hit)).Select(c => c.Id))
            .Concat(a.DataStores.Where(s => Hit(s.LogicalName) || Hit(s.DbContext) || Hit(s.ConnectionReference)).Select(s => s.Id))
            .Concat(a.ExternalSystems.Where(e => Hit(e.Name)).Select(e => e.Id))
            .Concat(a.ConfigurationReferences.Where(c => Hit(c.Key)).Select(c => c.ComponentId))
            .Distinct(StringComparer.Ordinal).ToList();
        return ids.Select(id => Node(a, id, out var n) ? n : null).OfType<ArchitectureGraphNode>().ToList();
    }

    public sealed record OverviewCounts(int Components, int Apis, int Workers, int Adapters, int Frontends, int Channels, int DataStores, int ExternalSystems,
        int Resolved, int Unresolved, int Inferred, int Conflicts);

    public static OverviewCounts Overview(ArchitectureSnapshot a)
    {
        var runtime = a.Dependencies.Where(d => Runtime.Contains(d.DependencyType)).ToList();
        return new(a.Components.Count, a.Components.Count(c => c.ComponentType == ArchitectureComponentType.Api), a.Components.Count(c => c.ComponentType == ArchitectureComponentType.Worker),
            a.Components.Count(c => c.ComponentType == ArchitectureComponentType.Adapter), a.Components.Count(c => c.ComponentType == ArchitectureComponentType.Frontend),
            a.MessagingChannels.Count, a.DataStores.Count, a.ExternalSystems.Count,
            runtime.Count(d => d.IsResolved && d.EvidenceState is ArchitectureEvidenceState.Confirmed or ArchitectureEvidenceState.StronglySupported),
            runtime.Count(d => d.EvidenceState == ArchitectureEvidenceState.Unresolved), runtime.Count(d => d.EvidenceState == ArchitectureEvidenceState.Inferred),
            runtime.Count(d => d.EvidenceState == ArchitectureEvidenceState.Conflict));
    }
}
