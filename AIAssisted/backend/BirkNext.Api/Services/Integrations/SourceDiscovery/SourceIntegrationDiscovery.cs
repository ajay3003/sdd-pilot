using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.DatabaseArchitecture;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;

namespace BirkNext.Api.Services.Integrations.SourceDiscovery;

/// <summary>
/// Source-only signals the architecture snapshot does not carry, read once while the archive is analyzed: implementation markers of change
/// capture (type and literal names — e.g. a consumer deserializing a Debezium envelope) and channels declared by development orchestration
/// (an Event Hubs emulator configuration). Identifiers and safe entity names only — no source text, no configuration value.
/// </summary>
public static class SourceIntegrationSignalExtractor
{
    public const string Version = "1";
    public const string CaptureTechnology = "CaptureTechnology";
    public const string ChangeCaptureHandling = "ChangeCaptureHandling";
    public const string DeclaredChannel = "DeclaredChannel";

    /// <summary>Known capture technologies and the source markers that indicate them. Extensible; nothing else depends on the names.</summary>
    private static readonly (string Technology, Regex Marker)[] Technologies =
    [
        ("Debezium", new(@"\b\w*Debezium\w*\b|io\.debezium", RegexOptions.Compiled)),
        ("Kafka Connect", new(@"org\.apache\.kafka\.connect|""connector\.class""", RegexOptions.Compiled)),
        ("SQL Server CDC", new(@"sp_cdc_enable_table|fn_cdc_get_all_changes|cdc\.change_tables", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    ];

    /// <summary>Change-capture handling by name (CdcEvent, ChangeCapturePublisher, ChangeDataCaptureRouter …), technology-neutral.</summary>
    private static readonly Regex ChangeCapture = new(@"\b(?:class|record|interface|struct)\s+(\w*(?:Cdc|ChangeDataCapture|ChangeCapture)\w*)", RegexOptions.Compiled);

    private static readonly Regex Comments = new(@"//[^\n]*|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    public static List<SourceIntegrationSignal> Extract(IqrSourceArchiveReader.Workspace workspace, ArchitectureSnapshot? architecture)
    {
        var signals = new List<SourceIntegrationSignal>();
        if (architecture is not null)
        {
            var roots = ComponentRoots(architecture);
            foreach (var file in workspace.Files.Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var owner = roots.Where(r => file.Path.StartsWith(r.Root, StringComparison.Ordinal)).OrderByDescending(r => r.Root.Length).FirstOrDefault();
                if (owner.ComponentId is null) continue;
                var code = Comments.Replace(file.Content, m => new string('\n', m.Value.Count(c => c == '\n')));
                foreach (var (technology, marker) in Technologies)
                    if (marker.Match(code) is { Success: true } m && !signals.Any(s => s.Kind == CaptureTechnology && s.ComponentId == owner.ComponentId && s.Value == technology))
                        signals.Add(new(CaptureTechnology, owner.ComponentId, technology,
                            new(ArchitectureEvidenceKind.ApplicationSource, file.Path, Line(code, m.Index), IqrSourceArchiveReader.SafeLabel(m.Value), "capture-technology",
                                $"{technology} marker in the component's source (consumer or producer payload handling).")));
                foreach (Match m in ChangeCapture.Matches(code))
                    if (!signals.Any(s => s.Kind == ChangeCaptureHandling && s.ComponentId == owner.ComponentId))
                        signals.Add(new(ChangeCaptureHandling, owner.ComponentId, m.Groups[1].Value,
                            new(ArchitectureEvidenceKind.ApplicationSource, file.Path, Line(code, m.Index), IqrSourceArchiveReader.SafeLabel(m.Groups[1].Value), "change-capture-handling",
                                "Change-capture handling type in the component's source.")));
            }
        }
        // Event Hubs emulator configuration (development orchestration): UserConfig.NamespaceConfig[].Entities[].Name.
        foreach (var file in workspace.ConfigurationFiles ?? [])
        {
            if (!file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !file.Content.Contains("NamespaceConfig", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(file.Content);
                if (!doc.RootElement.TryGetProperty("UserConfig", out var user) || !user.TryGetProperty("NamespaceConfig", out var namespaces) || namespaces.ValueKind != JsonValueKind.Array) continue;
                foreach (var ns in namespaces.EnumerateArray())
                {
                    var type = ns.TryGetProperty("Type", out var t) ? t.GetString() : null;
                    if (!ns.TryGetProperty("Entities", out var entities) || entities.ValueKind != JsonValueKind.Array) continue;
                    foreach (var entity in entities.EnumerateArray())
                        if (entity.TryGetProperty("Name", out var name) && name.GetString() is { } value && SafeEntity.IsMatch(value))
                            signals.Add(new(DeclaredChannel, null, value, new(ArchitectureEvidenceKind.DevelopmentOrchestration, file.Path, 1, $"{type ?? "EventHub"} emulator entity",
                                "emulator-config", "Channel declared by the development orchestration's emulator configuration (not deployed infrastructure).")));
                }
            }
            catch (JsonException) { }
        }
        return signals;
    }

    internal static readonly Regex SafeEntity = new(@"^[A-Za-z0-9][A-Za-z0-9._\-/]{0,259}$", RegexOptions.Compiled);

    private static int Line(string text, int index) => text.AsSpan(0, Math.Min(index, text.Length)).Count('\n') + 1;

    /// <summary>Directory of each deployable component and of the libraries it includes, so a file is attributed to the component that runs it.</summary>
    private static List<(string Root, string ComponentId)> ComponentRoots(ArchitectureSnapshot architecture)
    {
        static string Dir(string project) => project.Contains('/') ? project[..(project.LastIndexOf('/') + 1)] : "";
        var roots = new List<(string, string)>();
        foreach (var component in architecture.Components.Where(c => c.ComponentType is not (ArchitectureComponentType.Library or ArchitectureComponentType.ExternalSystem)))
        {
            if (Dir(component.SourceProject) is { Length: > 0 } dir) roots.Add((dir, component.Id));
            foreach (var library in architecture.SharedLibraries.Where(l => component.IncludedLibraries.Contains(l.Name) && Dir(l.SourceProject).Length > 0))
                roots.Add((Dir(library.SourceProject), component.Id));
        }
        return roots;
    }
}

/// <summary>Everything an extractor may read: one snapshot's architecture, its database analysis, and the persisted source signals.</summary>
public sealed class SourceDiscoveryContext(Guid snapshotId, ArchitectureSnapshot architecture, DatabaseArchitectureSnapshot? database, IReadOnlyList<SourceIntegrationSignal> signals)
{
    public Guid SnapshotId { get; } = snapshotId;
    public ArchitectureSnapshot Architecture { get; } = architecture;
    public DatabaseArchitectureSnapshot? Database { get; } = database;
    public IReadOnlyList<SourceIntegrationSignal> Signals { get; } = signals;
    public List<SourceIntegrationCandidate> Candidates { get; } = [];
    public List<TechnicalIntegrationChannel> TechnicalChannels { get; } = [];
    public List<string> Unresolved { get; } = [];
    public List<string> Diagnostics { get; } = [];
    /// <summary>Channel declarations collected by the bridge: one per transport + name, with every endpoint and its evidence.</summary>
    public List<ChannelDeclaration> Channels { get; } = [];
    public HashSet<ChannelDeclaration> Claimed { get; } = [];

    public ArchitectureComponent? Component(string? id) => id is null ? null : Architecture.Components.FirstOrDefault(c => c.Id == id);
    public string ComponentName(string? id) => Component(id)?.Name ?? id ?? "";
    public IEnumerable<SourceIntegrationSignal> SignalsOf(string? componentId, string kind) => Signals.Where(s => s.ComponentId == componentId && s.Kind == kind);
}

/// <summary>A channel the source names, with producers and consumers (each with its evidence), before it becomes a candidate.</summary>
public sealed class ChannelDeclaration
{
    public IntegrationTransport Transport { get; init; }
    public string Name { get; init; } = "";
    public List<string> Variants { get; } = [];
    public string? Namespace { get; set; }
    public List<(string ComponentId, ArchitectureEvidenceState State, List<ArchitectureEvidence> Evidence)> Producers { get; } = [];
    public List<(string ComponentId, ArchitectureEvidenceState State, List<ArchitectureEvidence> Evidence)> Consumers { get; } = [];
    public List<ArchitectureEvidence> Evidence { get; } = [];
}

/// <summary>One pluggable source-integration extractor. Extractors only read the context and add candidates/diagnostics.</summary>
public interface ISourceIntegrationExtractor
{
    string Name { get; }
    string Version { get; }
    void Extract(SourceDiscoveryContext context);
}

/// <summary>
/// Generic source-integration discovery over a stored source snapshot. Pure and re-runnable: the same snapshot gives the same result; a new
/// snapshot gives a new result; history is never mutated. Candidates are at most suggestions — the engine has no confirmation concept.
/// </summary>
public static class SourceIntegrationDiscoveryEngine
{
    public const int EngineVersion = 1;

    public static IReadOnlyList<ISourceIntegrationExtractor> Extractors { get; } =
    [
        new ArchitectureChannelBridge(), new ChangeDataCaptureExtractor(), new MessagingExtractor(), new HttpIntegrationExtractor(), new GraphQlIntegrationExtractor(),
    ];

    public static SourceIntegrationDiscoveryResult Discover(IqrSourceSnapshot snapshot)
    {
        if (snapshot.Architecture is not { } architecture)
            return new()
            {
                SourceSnapshotId = snapshot.Id, ArchiveName = snapshot.Archive.FileName, AnalyzedAt = snapshot.AnalyzedAt, EngineVersion = EngineVersion, Status = SourceDiscoveryStatus.Partial,
                Limitations = ["This source snapshot was analyzed before architecture extraction; re-analyze the source to discover integrations."],
            };
        var context = new SourceDiscoveryContext(snapshot.Id, architecture, snapshot.DatabaseArchitecture, snapshot.IntegrationSignals);
        foreach (var extractor in Extractors) extractor.Extract(context);
        var limitations = new List<string>();
        if (snapshot.IntegrationSignals.Count == 0) limitations.Add("No capture-technology signals are stored for this snapshot (analyzed before source integration discovery); capture technology shows as Not detected until the source is re-analyzed.");
        limitations.Add(ArchitectureSnapshot.SourceLimitation);
        var technologies = context.Candidates.Select(c => c.CaptureTechnology).Concat(context.Candidates.Select(c => c.CallerTechnology)).Concat(context.Candidates.Select(c => c.ServerTechnology))
            .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new()
        {
            SourceSnapshotId = snapshot.Id, SourceFingerprint = architecture.SourceFingerprint, ArchiveName = snapshot.Archive.FileName, AnalyzedAt = snapshot.AnalyzedAt,
            ArchitectureAnalyzerVersion = architecture.AnalyzerVersion, EngineVersion = EngineVersion,
            ExtractorVersions = Extractors.Select(e => KeyValuePair.Create(e.Name, e.Version)).Append(KeyValuePair.Create("signals", SourceIntegrationSignalExtractor.Version)).ToDictionary(),
            Status = architecture.Status == ArchitectureStatus.Complete && context.Unresolved.Count == 0 ? SourceDiscoveryStatus.Complete : SourceDiscoveryStatus.Partial,
            Candidates = [.. context.Candidates.OrderBy(c => c.IsBusinessIntegration ? 0 : 1).ThenBy(c => c.Pattern).ThenBy(c => c.SourceEntity ?? c.ChannelName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal)],
            TechnicalChannels = [.. context.TechnicalChannels.DistinctBy(t => (t.Transport, t.Name.ToLowerInvariant())).OrderBy(t => t.Name, StringComparer.Ordinal)],
            DetectedTechnologies = technologies,
            Unresolved = [.. context.Unresolved.Distinct(StringComparer.Ordinal)],
            Diagnostics = [.. context.Diagnostics.Distinct(StringComparer.Ordinal)],
            Limitations = limitations,
        };
    }

    // ── shared helpers ──

    /// <summary>Known support channels of change-capture platforms (schema history, Kafka Connect internals). Excluded from business counts.</summary>
    public static string? TechnicalPurpose(string channel)
    {
        foreach (var hub in EventHubRuntimeSources.KnownTechnicalHubs)
            if (channel.Equals(hub, StringComparison.OrdinalIgnoreCase) || channel.EndsWith("-" + hub, StringComparison.OrdinalIgnoreCase) || channel.EndsWith("." + hub, StringComparison.OrdinalIgnoreCase))
                return hub switch
                {
                    "connect-configs" => "Kafka Connect connector configuration",
                    "connect-offsets" => "Kafka Connect source offsets",
                    "connect-status" => "Kafka Connect connector status",
                    _ => "Schema history",
                };
        return null;
    }

    /// <summary>The Kafka-Connect-style change topic name &lt;prefix&gt;.&lt;database&gt;.&lt;schema&gt;.&lt;table&gt; (used by Debezium and others) — a naming convention, not proof of a technology.</summary>
    public static readonly Regex ChangeTopicName = new(@"^(?<prefix>[A-Za-z0-9_\-]+)\.(?<database>[A-Za-z0-9_]+)\.(?<schema>[A-Za-z0-9_]+)\.(?<table>[A-Za-z0-9_]+)$", RegexOptions.Compiled);

    public static string Key(IntegrationTransport transport, string channel, string? consumer = null) =>
        $"{transport}:{channel}{(consumer is null ? "" : "→" + consumer)}".ToLowerInvariant();

    public static ArchitectureEvidenceState Weakest(IEnumerable<ArchitectureEvidenceState> states)
    {
        var order = new[] { ArchitectureEvidenceState.Conflict, ArchitectureEvidenceState.Unresolved, ArchitectureEvidenceState.Inferred, ArchitectureEvidenceState.StronglySupported, ArchitectureEvidenceState.Confirmed };
        return states.OrderBy(s => Array.IndexOf(order, s)).DefaultIfEmpty(ArchitectureEvidenceState.Inferred).First();
    }

    public static ArchitectureEvidenceState Strongest(IEnumerable<ArchitectureEvidenceState> states)
    {
        var order = new[] { ArchitectureEvidenceState.Confirmed, ArchitectureEvidenceState.StronglySupported, ArchitectureEvidenceState.Inferred, ArchitectureEvidenceState.Unresolved, ArchitectureEvidenceState.Conflict };
        return states.OrderBy(s => Array.IndexOf(order, s)).DefaultIfEmpty(ArchitectureEvidenceState.Inferred).First();
    }

    public static SourceFieldEvidence Field(string field, string value, ArchitectureEvidenceState state, ArchitectureEvidence e, string? explanation = null) =>
        new(field, value, state, e.Kind, e.File, e.Line, e.Symbol, e.Extractor, explanation ?? e.Explanation);
}

/// <summary>
/// Architecture evidence bridge: turns the snapshot's messaging channels, plus messaging configuration references whose safe values name
/// channels for components the architecture could not pair (an unresolved consume/produce edge), plus orchestration-declared channels, into
/// channel declarations. An architecture relationship is evidence for a suggestion — never a confirmed mapping.
/// </summary>
public sealed class ArchitectureChannelBridge : ISourceIntegrationExtractor
{
    public string Name => "architecture-bridge";
    public string Version => "1";

    public void Extract(SourceDiscoveryContext context)
    {
        var a = context.Architecture;
        ChannelDeclaration Declare(IntegrationTransport transport, string name)
        {
            static bool ServiceBus(IntegrationTransport t) => t is IntegrationTransport.ServiceBus or IntegrationTransport.ServiceBusTopic or IntegrationTransport.ServiceBusQueue;
            var existing = context.Channels.FirstOrDefault(c => (c.Transport == transport || ServiceBus(c.Transport) && ServiceBus(transport)) && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                // A specific topic/queue declaration refines a generic Service Bus one of the same name.
                if (existing.Transport == IntegrationTransport.ServiceBus && transport != IntegrationTransport.ServiceBus)
                {
                    var refined = new ChannelDeclaration { Transport = transport, Name = existing.Name, Namespace = existing.Namespace };
                    refined.Variants.AddRange(existing.Variants); refined.Producers.AddRange(existing.Producers); refined.Consumers.AddRange(existing.Consumers); refined.Evidence.AddRange(existing.Evidence);
                    context.Channels[context.Channels.IndexOf(existing)] = refined;
                    return refined;
                }
                return existing;
            }
            var created = new ChannelDeclaration { Transport = transport, Name = name };
            context.Channels.Add(created);
            return created;
        }
        static void Add(List<(string ComponentId, ArchitectureEvidenceState State, List<ArchitectureEvidence> Evidence)> list, string component, ArchitectureEvidenceState state, IEnumerable<ArchitectureEvidence> evidence)
        {
            var index = list.FindIndex(e => e.ComponentId == component);
            if (index < 0) list.Add((component, state, evidence.ToList()));
            else list[index] = (component, SourceIntegrationDiscoveryEngine.Strongest([list[index].State, state]), [.. list[index].Evidence, .. evidence]);
        }

        foreach (var channel in a.MessagingChannels)
        {
            if (channel.NameUnresolved || channel.EntityName is not { Length: > 0 } entity)
            {
                context.Unresolved.Add($"{channel.Name}: channel name is computed at runtime; producers and consumers are not paired.");
                continue;
            }
            var declared = Declare(Transport(channel.Type), entity);
            declared.Namespace ??= channel.Namespace;
            declared.Evidence.AddRange(channel.Evidence);
            foreach (var v in channel.EnvironmentVariants.Values.Where(v => !v.Equals(entity, StringComparison.OrdinalIgnoreCase) && !declared.Variants.Contains(v))) declared.Variants.Add(v);
            foreach (var p in channel.Producers) Add(declared.Producers, p.ComponentId, p.State, p.Evidence);
            foreach (var c in channel.Consumers) Add(declared.Consumers, c.ComponentId, c.State, c.Evidence);
        }

        // Components with an UNRESOLVED consume/produce edge: their messaging configuration's safe values name the channels they use.
        foreach (var dependency in a.Dependencies.Where(d => !d.IsResolved || d.ToId?.Contains(":unresolved:", StringComparison.Ordinal) == true))
        {
            var (transport, consumer) = dependency.DependencyType switch
            {
                ArchitectureDependencyType.EventHubConsume => (IntegrationTransport.EventHub, true),
                ArchitectureDependencyType.EventHubProduce => (IntegrationTransport.EventHub, false),
                ArchitectureDependencyType.ServiceBusConsume => (IntegrationTransport.ServiceBus, true),
                ArchitectureDependencyType.ServiceBusPublish => (IntegrationTransport.ServiceBus, false),
                _ => (IntegrationTransport.Unknown, false),
            };
            if (transport == IntegrationTransport.Unknown) continue;
            var keyword = transport == IntegrationTransport.EventHub ? "EventHub" : "ServiceBus";
            var references = a.ConfigurationReferences.Where(r => r.ComponentId == dependency.FromComponentId && r.Purpose == "Messaging"
                && r.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase) && r.SafeValues.Count > 0
                && !r.Key.Contains("ConsumerGroup", StringComparison.OrdinalIgnoreCase) && !r.Key.Contains("FQDN", StringComparison.OrdinalIgnoreCase) && !r.Key.Contains("Namespace", StringComparison.OrdinalIgnoreCase)).ToList();
            if (references.Count == 0) continue;
            foreach (var reference in references)
            {
                // Values of one key across environment files: the first is the base file's; the others are per-environment variants of the same channel.
                var family = reference.SafeValues.Where(v => SourceIntegrationSignalExtractor.SafeEntity.IsMatch(v)).ToList();
                foreach (var group in Families(family))
                {
                    var declared = Declare(transport, group[0]);
                    foreach (var v in group.Skip(1).Where(v => !declared.Variants.Contains(v, StringComparer.OrdinalIgnoreCase))) declared.Variants.Add(v);
                    var evidence = new List<ArchitectureEvidence>
                    {
                        new(ArchitectureEvidenceKind.Configuration, reference.Files.FirstOrDefault() ?? "", 1, reference.Key, Name,
                            $"Configuration key {reference.Key} of the component names this channel; the component's {dependency.DependencyType} edge reads it at runtime."),
                    };
                    evidence.AddRange(dependency.Evidence.Take(1));
                    Add(consumer ? declared.Consumers : declared.Producers, dependency.FromComponentId, ArchitectureEvidenceState.Inferred, evidence);
                }
            }
        }

        foreach (var signal in context.Signals.Where(s => s.Kind == SourceIntegrationSignalExtractor.DeclaredChannel))
        {
            var declared = Declare(IntegrationTransport.EventHub, signal.Value);
            declared.Evidence.Add(signal.Evidence);
        }

        // Unresolved HTTP/GraphQL targets are reported, never guessed.
        foreach (var dependency in a.Dependencies.Where(d => !d.IsResolved && d.DependencyType is ArchitectureDependencyType.Http or ArchitectureDependencyType.GraphQl))
            context.Unresolved.Add($"{context.ComponentName(dependency.FromComponentId)} → {dependency.TargetReference ?? "unknown target"} ({dependency.DependencyType}): target not resolved from explicit wiring.");
    }

    /// <summary>Groups per-environment variants of the same channel (same schema.table under a change-topic name; otherwise exact names).</summary>
    private static List<List<string>> Families(List<string> values)
    {
        var groups = new List<List<string>>();
        foreach (var value in values)
        {
            var m = SourceIntegrationDiscoveryEngine.ChangeTopicName.Match(value);
            var key = m.Success ? $"{m.Groups["schema"].Value}.{m.Groups["table"].Value}".ToLowerInvariant() : value.ToLowerInvariant();
            var group = groups.FirstOrDefault(g =>
            {
                var gm = SourceIntegrationDiscoveryEngine.ChangeTopicName.Match(g[0]);
                return (gm.Success ? $"{gm.Groups["schema"].Value}.{gm.Groups["table"].Value}".ToLowerInvariant() : g[0].ToLowerInvariant()) == key;
            });
            if (group is null) groups.Add([value]);
            else if (!group.Contains(value, StringComparer.OrdinalIgnoreCase)) group.Add(value);
        }
        return groups;
    }

    private static IntegrationTransport Transport(MessagingChannelType type) => type switch
    {
        MessagingChannelType.EventHub => IntegrationTransport.EventHub,
        MessagingChannelType.ServiceBusTopic => IntegrationTransport.ServiceBusTopic,
        MessagingChannelType.ServiceBusQueue => IntegrationTransport.ServiceBusQueue,
        MessagingChannelType.ServiceBusEntity => IntegrationTransport.ServiceBus,
        MessagingChannelType.KafkaTopic => IntegrationTransport.Kafka,
        _ => IntegrationTransport.Unknown,
    };
}

/// <summary>
/// Change Data Capture: a channel whose consumer or producer handles change capture in source (by name, or a capture-technology marker), or
/// whose producer in source is a change-capture publisher. The capture technology comes only from markers (Debezium, Kafka Connect, SQL Server
/// CDC) or, for a producer in source without one, "Custom publisher". The source entity comes from the channel's change-topic name or the
/// configuration key that maps an entity to it; the database model only corroborates an entity — it never decides a consumer.
/// </summary>
public sealed class ChangeDataCaptureExtractor : ISourceIntegrationExtractor
{
    public string Name => "change-data-capture";
    public string Version => "1";

    public void Extract(SourceDiscoveryContext context)
    {
        foreach (var channel in context.Channels.Where(c => !context.Claimed.Contains(c)))
        {
            if (SourceIntegrationDiscoveryEngine.TechnicalPurpose(channel.Name) is { } purpose)
            {
                context.TechnicalChannels.Add(new(channel.Name, channel.Transport, purpose, "Source", "Platform support channel — not a business integration."));
                context.Claimed.Add(channel);
                continue;
            }
            var name = SourceIntegrationDiscoveryEngine.ChangeTopicName.Match(channel.Name);
            // A producer in source captures changes only when it handles change capture and consumes nothing itself (a component that
            // consumes change events and publishes elsewhere is a consumer, not a capture publisher).
            var capturingProducers = channel.Producers.Select(p => p.ComponentId).Where(p =>
                (context.SignalsOf(p, SourceIntegrationSignalExtractor.ChangeCaptureHandling).Any() || context.SignalsOf(p, SourceIntegrationSignalExtractor.CaptureTechnology).Any())
                && !context.Architecture.Dependencies.Any(d => d.FromComponentId == p && d.DependencyType is ArchitectureDependencyType.EventHubConsume or ArchitectureDependencyType.ServiceBusConsume)).ToList();
            // Consumer-side change-capture handling counts only for a channel named like a change topic.
            var consumerSide = name.Success ? channel.Consumers.Select(c => c.ComponentId).ToList() : [];
            var parties = capturingProducers.Concat(consumerSide).ToList();
            var markers = (capturingProducers.Count > 0 ? capturingProducers : consumerSide).SelectMany(p => context.SignalsOf(p, SourceIntegrationSignalExtractor.CaptureTechnology)).ToList();
            var handling = parties.SelectMany(p => context.SignalsOf(p, SourceIntegrationSignalExtractor.ChangeCaptureHandling)).ToList();
            var producerHandling = capturingProducers.SelectMany(p => context.SignalsOf(p, SourceIntegrationSignalExtractor.ChangeCaptureHandling)).ToList();
            if (capturingProducers.Count == 0 && (consumerSide.Count == 0 || markers.Count == 0 && handling.Count == 0)) continue;

            context.Claimed.Add(channel);
            var technology = markers.Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList();
            var captureTechnology = technology.Count > 0 ? string.Join(" / ", technology) : producerHandling.Count > 0 ? "Custom publisher" : null;
            var producer = channel.Producers.FirstOrDefault(p => capturingProducers.Contains(p.ComponentId));
            if (producer.ComponentId is null) producer = channel.Producers.FirstOrDefault();
            var evidence = new List<SourceFieldEvidence>();
            evidence.AddRange(channel.Evidence.Take(2).Select(e => SourceIntegrationDiscoveryEngine.Field("Channel", channel.Name, ArchitectureEvidenceState.Confirmed, e)));
            foreach (var m in markers.DistinctBy(m => (m.ComponentId, m.Value)))
                evidence.Add(SourceIntegrationDiscoveryEngine.Field("Capture technology", m.Value, ArchitectureEvidenceState.Inferred, m.Evidence,
                    $"{m.Value} payload handling in {context.ComponentName(m.ComponentId)} — the capture technology is inferred from the {(channel.Producers.Any(p => p.ComponentId == m.ComponentId) ? "producer" : "consumer")}'s source."));
            foreach (var h in handling.DistinctBy(h => h.ComponentId).Take(2))
                evidence.Add(SourceIntegrationDiscoveryEngine.Field("Integration pattern", "Change Data Capture", ArchitectureEvidenceState.Inferred, h.Evidence));
            if (producer.ComponentId is not null && producerHandling.Count > 0 && technology.Count == 0)
                evidence.Add(SourceIntegrationDiscoveryEngine.Field("Capture technology", "Custom publisher", ArchitectureEvidenceState.Inferred, producerHandling[0].Evidence,
                    $"{context.ComponentName(producer.ComponentId)} publishes changes itself; no known capture-technology marker was found."));

            string? database = null, schema = null, table = null, system = null, systemType = null;
            if (name.Success)
            {
                database = name.Groups["database"].Value; schema = name.Groups["schema"].Value; table = name.Groups["table"].Value; system = database; systemType = "Database";
                evidence.Add(new("Source entity", $"{schema}.{table}", ArchitectureEvidenceState.Inferred, ArchitectureEvidenceKind.Configuration, channel.Evidence.FirstOrDefault()?.File ?? "", channel.Evidence.FirstOrDefault()?.Line ?? 1,
                    channel.Name, Name, "Source database, schema and table read from the change-topic name <prefix>.<database>.<schema>.<table>."));
            }
            else if (producer.ComponentId is not null && context.Architecture.DataStores.FirstOrDefault(s => s.ReferencedByComponents.Contains(producer.ComponentId) && s.StoreType is not (DataStoreType.BlobStorage or DataStoreType.Redis)) is { } store)
            {
                system = store.LogicalName; systemType = "Database";
                evidence.AddRange(store.Evidence.Take(1).Select(e => SourceIntegrationDiscoveryEngine.Field("Source system", store.LogicalName, store.Confidence, e, "The capturing producer reads this datastore.")));
            }
            if (table is not null && context.Database?.Databases.SelectMany(d => d.Schemas).SelectMany(s => s.Tables)
                    .FirstOrDefault(t => string.Equals(t.PhysicalName ?? t.LogicalName, table, StringComparison.OrdinalIgnoreCase) && (t.SchemaName is null || string.Equals(t.SchemaName, schema, StringComparison.OrdinalIgnoreCase))) is { } dbTable)
                evidence.Add(new("Source entity", $"{dbTable.SchemaName ?? schema}.{dbTable.PhysicalName ?? dbTable.LogicalName}", ArchitectureEvidenceState.Inferred, ArchitectureEvidenceKind.ApplicationSource,
                    dbTable.SourceProject, 1, dbTable.EntityTypeName ?? dbTable.LogicalName, "database-model",
                    $"A table with the same name exists in this snapshot's database model ({dbTable.SourceProject}); it may belong to a different database than the captured source. Entity evidence only — never consumer evidence."));

            foreach (var consumer in channel.Consumers.DefaultIfEmpty())
                context.Candidates.Add(Candidate(context, channel, consumer, IntegrationPattern.ChangeDataCapture, captureTechnology, system, systemType, table, table is null ? null : "Table", schema, database, evidence,
                    producer.ComponentId is null ? $"No producer of {channel.Name} is in the analyzed source; the change capture runs outside it." : null));
        }

        // Change topics declared in source (configuration or orchestration) without a consumer or producer there, in the same
        // <prefix>.<database> family as a detected change-capture channel: CDC candidates without a consumer candidate (Unassigned).
        var families = context.Candidates.Where(c => c.Pattern == IntegrationPattern.ChangeDataCapture && c.ChannelName is { } n && ChangeFamily(n) is not null)
            .GroupBy(c => ChangeFamily(c.ChannelName!)!).ToDictionary(g => g.Key, g => g.First());
        foreach (var channel in context.Channels.Where(c => !context.Claimed.Contains(c) && c.Producers.Count == 0 && c.Consumers.Count == 0))
        {
            if (ChangeFamily(channel.Name) is not { } family || !families.TryGetValue(family, out var sibling)) continue;
            var name = SourceIntegrationDiscoveryEngine.ChangeTopicName.Match(channel.Name);
            context.Claimed.Add(channel);
            var evidence = channel.Evidence.Take(2).Select(e => SourceIntegrationDiscoveryEngine.Field("Channel", channel.Name, ArchitectureEvidenceState.Confirmed, e)).ToList();
            evidence.Add(new("Integration pattern", "Change Data Capture", ArchitectureEvidenceState.Inferred, ArchitectureEvidenceKind.Configuration, channel.Evidence.FirstOrDefault()?.File ?? "", 1, channel.Name, Name,
                $"Same change-topic family ({family}) as {sibling.ChannelName}, which has change-capture evidence."));
            if (sibling.CaptureTechnology is { } inherited)
                evidence.Add(new("Capture technology", inherited, ArchitectureEvidenceState.Inferred, ArchitectureEvidenceKind.Configuration, channel.Evidence.FirstOrDefault()?.File ?? "", 1, channel.Name, Name,
                    $"Inferred from the same change-topic family as {sibling.ChannelName}; no consumer of this channel is in the analyzed source."));
            evidence.Add(new("Source entity", $"{name.Groups["schema"].Value}.{name.Groups["table"].Value}", ArchitectureEvidenceState.Inferred, ArchitectureEvidenceKind.Configuration, channel.Evidence.FirstOrDefault()?.File ?? "", 1,
                channel.Name, Name, "Source database, schema and table read from the change-topic name <prefix>.<database>.<schema>.<table>."));
            context.Candidates.Add(Candidate(context, channel, default, IntegrationPattern.ChangeDataCapture, sibling.CaptureTechnology, name.Groups["database"].Value, "Database",
                name.Groups["table"].Value, "Table", name.Groups["schema"].Value, name.Groups["database"].Value, evidence,
                $"{channel.Name} is declared in source but neither produced nor consumed there."));
        }
    }

    private static string? ChangeFamily(string channel) =>
        SourceIntegrationDiscoveryEngine.ChangeTopicName.Match(channel) is { Success: true } m ? $"{m.Groups["prefix"].Value}.{m.Groups["database"].Value}".ToLowerInvariant() : null;

    internal static SourceIntegrationCandidate Candidate(SourceDiscoveryContext context, ChannelDeclaration channel,
        (string ComponentId, ArchitectureEvidenceState State, List<ArchitectureEvidence> Evidence) consumer, IntegrationPattern pattern, string? captureTechnology,
        string? system, string? systemType, string? entity, string? entityType, string? schema, string? database, List<SourceFieldEvidence> evidence, string? diagnostic)
    {
        var producer = channel.Producers.FirstOrDefault();
        var fields = new List<SourceFieldEvidence>(evidence);
        if (producer.ComponentId is not null)
            fields.AddRange(producer.Evidence.Take(1).Select(e => SourceIntegrationDiscoveryEngine.Field("Producer", context.ComponentName(producer.ComponentId), producer.State, e)));
        if (consumer.ComponentId is not null)
            fields.AddRange(consumer.Evidence.Take(2).Select(e => SourceIntegrationDiscoveryEngine.Field("Consumer candidate", context.ComponentName(consumer.ComponentId), consumer.State, e,
                $"{context.ComponentName(consumer.ComponentId)} reads this channel in source. A source relationship suggests the mapping; it never confirms it.")));
        var diagnostics = new List<string>();
        if (diagnostic is not null) diagnostics.Add(diagnostic);
        if (consumer.ComponentId is null) diagnostics.Add("No consumer candidate was found in the analyzed source — the consumer may live outside it. Not a failure.");
        var states = fields.Where(f => f.Field is "Channel" or "Consumer candidate" or "Producer").Select(f => f.State).ToList();
        return new()
        {
            // Keyed by the stable component id: display names are shortened differently as the set of components changes.
            Id = SourceIntegrationDiscoveryEngine.Key(channel.Transport, channel.Name, consumer.ComponentId),
            SourceSnapshotId = context.SnapshotId,
            SourceSystem = system ?? (producer.ComponentId is null ? null : context.ComponentName(producer.ComponentId)),
            SourceSystemType = systemType ?? (producer.ComponentId is null ? null : "Application component"),
            SourceEntity = entity, SourceEntityType = entityType, SourceSchema = schema, SourceDatabase = database,
            Pattern = pattern, CaptureTechnology = captureTechnology,
            ProducerComponent = producer.ComponentId is null ? null : context.ComponentName(producer.ComponentId),
            Transport = channel.Transport, ChannelName = channel.Name, ChannelVariants = [.. channel.Variants], NamespaceName = channel.Namespace,
            DestinationType = SourceIntegrationLabels.Transport(channel.Transport),
            ConsumerCandidate = consumer.ComponentId is null ? null : context.ComponentName(consumer.ComponentId), ConsumerComponentId = consumer.ComponentId,
            ConsumerEvidence = consumer.ComponentId is null ? ArchitectureEvidenceState.Unresolved : consumer.State,
            EvidenceState = SourceIntegrationDiscoveryEngine.Weakest(states.DefaultIfEmpty(ArchitectureEvidenceState.Inferred)),
            Evidence = fields, Diagnostics = diagnostics,
        };
    }
}

/// <summary>Event Hub and Service Bus channels that are not change capture: event-driven or messaging integrations between source components.</summary>
public sealed class MessagingExtractor : ISourceIntegrationExtractor
{
    public string Name => "messaging";
    public string Version => "1";

    public void Extract(SourceDiscoveryContext context)
    {
        foreach (var channel in context.Channels.Where(c => !context.Claimed.Contains(c)))
        {
            context.Claimed.Add(channel);
            if (SourceIntegrationDiscoveryEngine.TechnicalPurpose(channel.Name) is { } purpose)
            {
                context.TechnicalChannels.Add(new(channel.Name, channel.Transport, purpose, "Source", "Platform support channel — not a business integration."));
                continue;
            }
            var pattern = channel.Transport is IntegrationTransport.EventHub or IntegrationTransport.Kafka ? IntegrationPattern.EventDriven : IntegrationPattern.Messaging;
            var evidence = channel.Evidence.Take(2).Select(e => SourceIntegrationDiscoveryEngine.Field("Channel", channel.Name, ArchitectureEvidenceState.Confirmed, e)).ToList();
            var diagnostic = channel.Producers.Count == 0 ? $"No producer of {channel.Name} is in the analyzed source (it may live outside it)." : null;
            if (channel.Producers.Count == 0 && channel.Consumers.Count == 0)
            {
                context.Unresolved.Add($"{channel.Name}: declared by {string.Join(", ", channel.Evidence.Select(e => e.Extractor).Distinct())} only — no producer or consumer in the analyzed source.");
                continue;
            }
            foreach (var consumer in channel.Consumers.DefaultIfEmpty())
            {
                // The same component inferred (from configuration only) as both producer and consumer: the role cannot be determined from source.
                if (consumer.ComponentId is not null && channel.Producers.Count == 1 && channel.Producers[0].ComponentId == consumer.ComponentId
                    && consumer.State == ArchitectureEvidenceState.Inferred && channel.Producers[0].State == ArchitectureEvidenceState.Inferred)
                {
                    context.Unresolved.Add($"{channel.Name}: {context.ComponentName(consumer.ComponentId)} both publishes and consumes it according to configuration only; the producer/consumer roles cannot be determined from source.");
                    continue;
                }
                context.Candidates.Add(ChangeDataCaptureExtractor.Candidate(context, channel, consumer, pattern, null, null, null, null, null, null, null, evidence, diagnostic));
            }
        }
    }
}

/// <summary>HTTP calls between source components (or to a resolved external system): the caller is the source, the callee the consumer.</summary>
public class HttpIntegrationExtractor : ISourceIntegrationExtractor
{
    public virtual string Name => "http";
    public string Version => "1";
    protected virtual ArchitectureDependencyType Type => ArchitectureDependencyType.Http;
    protected virtual IntegrationTransport Transport => IntegrationTransport.Http;

    public void Extract(SourceDiscoveryContext context)
    {
        foreach (var dependency in context.Architecture.Dependencies.Where(d => d.DependencyType == Type && d.IsResolved && !d.ToId!.Contains(":unresolved:", StringComparison.Ordinal)))
        {
            var target = context.Component(dependency.ToId);
            var external = target is null ? context.Architecture.ExternalSystems.FirstOrDefault(s => s.Id == dependency.ToId) : null;
            if (target is null && external is null) continue;
            var caller = context.ComponentName(dependency.FromComponentId);
            var callee = target?.Name ?? external!.Name;
            var channel = dependency.ContractReference ?? (Transport == IntegrationTransport.GraphQl ? "/graphql" : dependency.TargetReference ?? dependency.ConfigurationReference ?? callee);
            var serverTechnology = target?.Technologies.FirstOrDefault(t => Transport == IntegrationTransport.GraphQl ? t.Contains("GraphQL server", StringComparison.OrdinalIgnoreCase) : t.StartsWith("ASP.NET", StringComparison.Ordinal));
            var callerTechnology = dependency.Framework ?? context.Component(dependency.FromComponentId)?.Technologies.FirstOrDefault(t => t.Contains("GraphQL client", StringComparison.OrdinalIgnoreCase) && Transport == IntegrationTransport.GraphQl);
            var fields = dependency.Evidence.Take(2).Select(e => SourceIntegrationDiscoveryEngine.Field("Consumer candidate", callee, dependency.EvidenceState, e,
                $"{caller} calls {callee} ({dependency.Protocol}) in source; explicit wiring resolved the target. A source call suggests the mapping; it never confirms it.")).ToList();
            if (callerTechnology is not null && dependency.Evidence.FirstOrDefault() is { } first)
                fields.Add(SourceIntegrationDiscoveryEngine.Field("Caller technology", callerTechnology, dependency.EvidenceState, first, "Client implementation technology on this edge — not an integration node."));
            context.Candidates.Add(new()
            {
                Id = SourceIntegrationDiscoveryEngine.Key(Transport, $"{dependency.FromComponentId}→{channel}", dependency.ToId), SourceSnapshotId = context.SnapshotId,
                SourceSystem = caller, SourceSystemType = "Application component", SourceEntity = caller, SourceEntityType = "Component",
                Pattern = IntegrationPattern.ApiCall, ProducerComponent = caller, CallerTechnology = callerTechnology, ServerTechnology = serverTechnology,
                Transport = Transport, ChannelName = channel, DestinationType = target is null ? "External system" : "Application component",
                ConsumerCandidate = callee, ConsumerComponentId = dependency.ToId, ConsumerEvidence = dependency.EvidenceState, EvidenceState = dependency.EvidenceState,
                Evidence = fields, Diagnostics = [.. dependency.Diagnostics],
            });
        }
    }
}

/// <summary>GraphQL calls: the client technology (e.g. Strawberry Shake) and the server technology (e.g. Hot Chocolate) stay attributes of the edge.</summary>
public sealed class GraphQlIntegrationExtractor : HttpIntegrationExtractor
{
    public override string Name => "graphql";
    protected override ArchitectureDependencyType Type => ArchitectureDependencyType.GraphQl;
    protected override IntegrationTransport Transport => IntegrationTransport.GraphQl;
}
