using System.Text.Json.Serialization;

namespace BirkNext.SourceArchitecture;

// Source-derived architecture: components, interfaces, dependencies, messaging channels, datastore references, external systems and shared
// libraries discovered from one immutable source snapshot. It is design/topology evidence only — never deployed architecture, never runtime
// traffic. It has its own model: database tables stay in BirkNext.DatabaseArchitecture and are only ever linked to, never embedded.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArchitectureComponentType { Frontend, Api, Worker, Adapter, BackgroundService, Library, Cli, Gateway, Proxy, ScheduledJob, Function, ExternalSystem, Unknown }

/// <summary>
/// Confirmed = explicit source wiring. StronglySupported = several independent indicators (e.g. application source + development orchestration).
/// Inferred = naming/convention/context only. Unresolved = the dependency exists but its target cannot be identified. Conflict = evidence disagrees.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArchitectureEvidenceState { Confirmed, StronglySupported, Inferred, Unresolved, Conflict }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArchitectureEvidenceKind { ApplicationSource, ProjectFile, Configuration, DevelopmentOrchestration, ContainerCompose, Infrastructure }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArchitectureDependencyType
{
    Http, GraphQl, EventHubProduce, EventHubConsume, ServiceBusPublish, ServiceBusConsume, DatabaseReadWrite, BlobReadWrite, CheckpointStore, Cache,
    KeyVault, Auth, Observability, ProjectReference, PackageDependency, ConfigurationDependency, OrchestrationDependency, Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessagingChannelType { EventHub, ServiceBusTopic, ServiceBusQueue, ServiceBusEntity, KafkaTopic, RabbitQueue, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataStoreType { SqlServer, PostgreSql, Sqlite, BlobStorage, Redis, Cosmos, FileStorage, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArchitectureStatus { Complete, Partial, NeedsReview, Unsupported }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChannelRole { Producer, Consumer }

/// <summary>One piece of source evidence: where, what, which extractor. Never a configuration value that could be a secret.</summary>
public sealed record ArchitectureEvidence(ArchitectureEvidenceKind Kind, string File, int Line, string Symbol, string Extractor, string Explanation);

public sealed record ArchitectureComponent
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string LogicalName { get; init; } = "";
    public ArchitectureComponentType ComponentType { get; init; } = ArchitectureComponentType.Unknown;
    /// <summary>E.g. "ASP.NET Core web app", ".NET worker", "Blazor WebAssembly", "Console app".</summary>
    public string RuntimeType { get; init; } = "";
    public string SourceProject { get; init; } = "";
    /// <summary>Top-level folder of the project — the module/repository area it belongs to.</summary>
    public string Module { get; init; } = "";
    public string Framework { get; init; } = "";
    public List<string> Technologies { get; init; } = [];
    public string? EntryPoint { get; init; }
    public string HostingModel { get; init; } = "";
    /// <summary>How the component type was decided (SDK and registrations are Confirmed; a name convention is Inferred).</summary>
    public ArchitectureEvidenceState EvidenceState { get; init; } = ArchitectureEvidenceState.Confirmed;
    public string Confidence { get; init; } = "";
    public List<ArchitectureEvidence> Evidence { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    /// <summary>Library projects this component includes (transitively); their evidence is attributed to it.</summary>
    public List<string> IncludedLibraries { get; init; } = [];
}

public sealed record ArchitectureInterface
{
    public string Id { get; init; } = "";
    public string ComponentId { get; init; } = "";
    /// <summary>"REST endpoints", "GraphQL endpoint", "Event Hub consumer", "Service Bus publisher", "Health checks", "Hosted service" …</summary>
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public string Protocol { get; init; } = "";
    /// <summary>Inbound (it serves) or Outbound (it calls/sends).</summary>
    public string Direction { get; init; } = "";
    public string? RouteOrTopic { get; init; }
    public string? AuthRequirement { get; init; }
    public string? Framework { get; init; }
    public List<ArchitectureEvidence> Evidence { get; init; } = [];
    public ArchitectureEvidenceState Confidence { get; init; } = ArchitectureEvidenceState.Confirmed;
}

public sealed record ArchitectureDependency
{
    public string Id { get; init; } = "";
    public string FromComponentId { get; init; } = "";
    /// <summary>A component, channel, datastore or external-system id when resolved; null when not.</summary>
    public string? ToId { get; init; }
    /// <summary>What the source names as the target when it cannot be resolved (a configuration key, a client name).</summary>
    public string? TargetReference { get; init; }
    public ArchitectureDependencyType DependencyType { get; init; }
    public string Protocol { get; init; } = "";
    public string Direction { get; init; } = "Outbound";
    public string? ConfigurationReference { get; init; }
    public string? ContractReference { get; init; }
    /// <summary>Transport/framework used on this edge (e.g. "Wolverine", "Strawberry Shake", "YARP") — never a separate node.</summary>
    public string? Framework { get; init; }
    public ArchitectureEvidenceState EvidenceState { get; init; }
    public string Confidence { get; init; } = "";
    public List<ArchitectureEvidence> Evidence { get; init; } = [];
    public bool IsResolved => ToId is not null;
    public List<string> Diagnostics { get; init; } = [];
    /// <summary>Candidate targets when evidence disagrees (Conflict).</summary>
    public List<string> ConflictingTargets { get; init; } = [];
}

public sealed record ChannelEndpoint(string ComponentId, ChannelRole Role, ArchitectureEvidenceState State, string? Framework, List<ArchitectureEvidence> Evidence);

public sealed record MessagingChannel
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public MessagingChannelType Type { get; init; }
    public string? Namespace { get; init; }
    /// <summary>The entity name as the source states it (literal, constant or a safe configuration value); per-environment variants beside it.</summary>
    public string? EntityName { get; init; }
    public Dictionary<string, string> EnvironmentVariants { get; init; } = [];
    public string? Subscription { get; init; }
    public string? ConsumerGroup { get; init; }
    public List<ChannelEndpoint> Producers { get; init; } = [];
    public List<ChannelEndpoint> Consumers { get; init; } = [];
    public List<string> ContractReferences { get; init; } = [];
    public List<string> ConfigurationReferences { get; init; } = [];
    public List<ArchitectureEvidence> Evidence { get; init; } = [];
    public ArchitectureEvidenceState Confidence { get; init; }
    /// <summary>True when the entity name could not be read from source (e.g. computed at runtime) — such a channel is never paired.</summary>
    public bool NameUnresolved { get; init; }
}

public sealed record ArchitectureDataStoreReference
{
    public string Id { get; init; } = "";
    public string LogicalName { get; init; } = "";
    public DataStoreType StoreType { get; init; }
    /// <summary>"Application database", "Application storage", "Event Hub checkpoint storage", "Cache".</summary>
    public string Usage { get; init; } = "";
    public List<string> ReferencedByComponents { get; init; } = [];
    public string? ConnectionReference { get; init; }
    public string? DbContext { get; init; }
    /// <summary>Set only when the same source snapshot's Database analysis has a database for exactly this DbContext.</summary>
    public string? DatabaseModelId { get; init; }
    public List<ArchitectureEvidence> Evidence { get; init; } = [];
    public ArchitectureEvidenceState Confidence { get; init; }
}

public sealed record ExternalSystem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string? ConfigurationReference { get; init; }
    public List<ArchitectureEvidence> Evidence { get; init; } = [];
    public ArchitectureEvidenceState Confidence { get; init; }
}

public sealed record SharedLibrary(string Id, string Name, string SourceProject, List<string> ReferencedByComponents, bool Internal);

/// <summary>A configuration key a component reads (file and purpose). Values are never stored, except entity names that are safe identifiers.</summary>
public sealed record ConfigurationReference(string ComponentId, string Key, string Purpose, List<string> Files, List<string> SafeValues);

public sealed record ArchitectureTechnology(string Name, string Category, List<string> Components);

public sealed record ArchitectureDiagnostic(string Kind, string Message, string? SubjectId = null);

public sealed record ArchitectureSnapshot
{
    public Guid SnapshotId { get; init; } = Guid.NewGuid();
    public Guid SourceSnapshotId { get; init; }
    /// <summary>SHA-256 of the analyzed archive.</summary>
    public string SourceFingerprint { get; init; } = "";
    public int AnalyzerVersion { get; init; } = 1;
    public Dictionary<string, string> ExtractorVersions { get; init; } = [];
    public DateTimeOffset ExtractedAt { get; init; }
    public ArchitectureStatus Status { get; init; }
    public List<ArchitectureTechnology> Technologies { get; init; } = [];
    public List<ArchitectureComponent> Components { get; init; } = [];
    public List<ArchitectureInterface> Interfaces { get; init; } = [];
    public List<ArchitectureDependency> Dependencies { get; init; } = [];
    public List<MessagingChannel> MessagingChannels { get; init; } = [];
    public List<ArchitectureDataStoreReference> DataStores { get; init; } = [];
    public List<ExternalSystem> ExternalSystems { get; init; } = [];
    public List<SharedLibrary> SharedLibraries { get; init; } = [];
    public List<ConfigurationReference> ConfigurationReferences { get; init; } = [];
    public List<ArchitectureDiagnostic> Diagnostics { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    [JsonIgnore] public IEnumerable<ArchitectureDependency> UnresolvedItems => Dependencies.Where(d => !d.IsResolved);

    public const string SourceLimitation = "This architecture is derived from the selected source snapshot. It does not prove that the same topology is deployed or active at runtime.";
}

// ── Source architecture change (snapshot A vs B) ────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArchitectureChangeKind { Added, Removed, Changed }

public sealed record ArchitectureChange(ArchitectureChangeKind Kind, string Area, string Key, string Name, string Detail);

/// <summary>
/// Compares two architecture snapshots by stable keys. The result is a source architecture change — never deployment drift, runtime change or
/// traffic change. Removed items stay removed: nothing from the older snapshot is merged into the newer one.
/// </summary>
public static class ArchitectureDiff
{
    public const string Label = "Source architecture change — source evidence differs between the two snapshots. This is not deployment drift and says nothing about runtime traffic.";

    public static List<ArchitectureChange> Compare(ArchitectureSnapshot previous, ArchitectureSnapshot current)
    {
        var changes = new List<ArchitectureChange>();
        void Diff<T>(string area, IEnumerable<T> before, IEnumerable<T> after, Func<T, string> key, Func<T, string> name, Func<T, string> signature)
        {
            var b = before.GroupBy(key).ToDictionary(g => g.Key, g => g.First());
            var a = after.GroupBy(key).ToDictionary(g => g.Key, g => g.First());
            foreach (var (k, item) in a.Where(x => !b.ContainsKey(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal)) changes.Add(new(ArchitectureChangeKind.Added, area, k, name(item), signature(item)));
            foreach (var (k, item) in b.Where(x => !a.ContainsKey(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal)) changes.Add(new(ArchitectureChangeKind.Removed, area, k, name(item), signature(item)));
            foreach (var (k, item) in a.Where(x => b.ContainsKey(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal))
                if (signature(item) != signature(b[k])) changes.Add(new(ArchitectureChangeKind.Changed, area, k, name(item), $"{signature(b[k])} → {signature(item)}"));
        }
        Diff("Component", previous.Components, current.Components, c => c.Id, c => c.Name, c => $"{c.ComponentType} · {string.Join(", ", c.Technologies.Order(StringComparer.Ordinal))}");
        Diff("Dependency", previous.Dependencies.Where(d => d.DependencyType != ArchitectureDependencyType.PackageDependency), current.Dependencies.Where(d => d.DependencyType != ArchitectureDependencyType.PackageDependency),
            d => d.Id, d => $"{d.FromComponentId} → {d.ToId ?? d.TargetReference}", d => $"{d.DependencyType} · {d.Protocol} · {d.EvidenceState} · {d.ConfigurationReference}");
        Diff("Messaging channel", previous.MessagingChannels, current.MessagingChannels, c => c.Id, c => c.Name,
            c => $"{c.Type} · producers {string.Join(", ", c.Producers.Select(p => p.ComponentId).Order(StringComparer.Ordinal))} · consumers {string.Join(", ", c.Consumers.Select(p => p.ComponentId).Order(StringComparer.Ordinal))}");
        Diff("Datastore", previous.DataStores, current.DataStores, s => s.Id, s => s.LogicalName, s => $"{s.StoreType} · {s.Usage} · {string.Join(", ", s.ReferencedByComponents.Order(StringComparer.Ordinal))}");
        Diff("External system", previous.ExternalSystems, current.ExternalSystems, s => s.Id, s => s.Name, s => $"{s.Type} · {s.Protocol}");
        return changes;
    }
}
