using System.Text.RegularExpressions;
using BirkNext.IntegrationTopology;

namespace BirkNext.Api.Services.IntegrationTopology;

/// <summary>
/// Merges what every provider reports into one canonical topology. Identity is explicit, never guessed from similar names:
/// <list type="bullet">
/// <item>a component is its normalized name, or an alias a profile declared for it;</item>
/// <item>an interface is component + type + direction + operation + normalized address (REST path parameters collapse to {});</item>
/// <item>a connection is from-component + to-component + type + normalized address.</item>
/// </list>
/// The same element from several providers becomes one element with several provenance entries. Its status is the strongest evidence level
/// (Expected &lt; Configured &lt; Discovered &lt; Observed); a recorded conflict or open question is shown as Conflict / Needs review instead.
/// </summary>
public sealed partial class TopologyBuilder
{
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ComponentState> _components = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InterfaceState> _interfaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConnectionState> _connections = new(StringComparer.Ordinal);
    private readonly List<TopologySourceSummary> _sources = [];
    private readonly Dictionary<string, string> _canonical = new(StringComparer.Ordinal);

    private sealed class ComponentState { public string Name = ""; public string? Type; public string? Location; public TopologyComponentKind Kind; public List<(TopologyStatus Level, TopologyProvenance Provenance)> Evidence = []; }
    private sealed class InterfaceState
    {
        public string ComponentKey = ""; public TopologyInterfaceType Type; public string Protocol = ""; public TopologyDirection Direction; public string Address = "";
        public string? Operation; public string? Contract; public List<(TopologyStatus Level, TopologyProvenance Provenance)> Evidence = []; public bool Conflict; public bool Review; public List<string> Notes = [];
    }
    private sealed class ConnectionState
    {
        public string From = ""; public string To = ""; public string? FromInterface; public string? ToInterface; public TopologyInterfaceType Type; public string Protocol = "";
        public string? Address; public string? Contract; public List<(TopologyStatus Level, TopologyProvenance Provenance)> Evidence = []; public bool Conflict; public bool Review; public List<string> Notes = [];
        public List<TopologyEvidence> Typed = [];
    }

    [GeneratedRegex("[^a-z0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphanumeric();
    [GeneratedRegex("^(\\{[^}]*\\}|:[^/]+|[0-9]+|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PathParameter();

    /// <summary>Declares explicit name aliases for one component (from a profile). Only exact normalized names are aliased.</summary>
    public void Alias(string canonical, IEnumerable<string> aliases)
    {
        var key = Normalize(canonical);
        _canonical[key] = canonical.Trim();
        foreach (var alias in aliases.Append(canonical)) _aliases[Normalize(alias)] = key;
    }

    public string ComponentKey(string name) => _aliases.GetValueOrDefault(Normalize(name)) ?? Normalize(name);

    /// <summary>The key of a component by its name or, when the name is not aliased, by one of its other exact identities (for example its
    /// project name) that a profile aliased. Only exact normalized identities are used.</summary>
    public string ComponentKey(string name, IEnumerable<string>? identities) =>
        _aliases.ContainsKey(Normalize(name)) ? ComponentKey(name)
        : identities?.Select(Normalize).FirstOrDefault(_aliases.ContainsKey) is { } alias ? _aliases[alias] : Normalize(name);

    public string Component(string name, TopologyStatus level, TopologyProvenance provenance, string? type = null, string? location = null,
        IEnumerable<string>? identities = null, TopologyComponentKind kind = TopologyComponentKind.Application)
    {
        var key = ComponentKey(name, identities);
        if (!_components.TryGetValue(key, out var state))
            _components[key] = state = new ComponentState { Name = _canonical.GetValueOrDefault(key) ?? name.Trim(), Kind = kind };
        if (kind != TopologyComponentKind.Application) state.Kind = kind;
        state.Type ??= type;
        state.Location ??= location;
        state.Evidence.Add((level, provenance));
        return key;
    }

    /// <summary>An intermediate resource node (queue, topic, event hub, captured table, connector), keyed by its kind and exact name.</summary>
    public string Resource(string kind, string name, string typeLabel, TopologyStatus level, TopologyProvenance provenance)
    {
        var key = ResourceKey(kind, name);
        if (!_components.TryGetValue(key, out var state))
            _components[key] = state = new ComponentState { Name = name.Trim(), Kind = TopologyComponentKind.Resource, Type = typeLabel };
        state.Evidence.Add((level, provenance));
        return key;
    }

    public static string ResourceKey(string kind, string name) => "resource:" + kind + ":" + name.Trim().ToLowerInvariant();

    public bool Has(string key) => _components.ContainsKey(key);
    public string NameOf(string key) => _components.TryGetValue(key, out var state) ? state.Name : key;
    public TopologyComponentKind KindOf(string key) => _components.TryGetValue(key, out var state) ? state.Kind : TopologyComponentKind.Application;
    public IReadOnlyCollection<string> ComponentKeys => _components.Keys;

    /// <summary>Adds independent evidence to a connection already stated (it never creates one).</summary>
    public void Strengthen(string connectionKey, TopologyEvidence evidence)
    {
        if (_connections.TryGetValue(connectionKey, out var state) && !state.Typed.Contains(evidence)) state.Typed.Add(evidence);
    }

    /// <summary>Connections already stated between two components (keys).</summary>
    public IReadOnlyList<string> ConnectionKeys(string from, string to) => [.. _connections.Where(pair => pair.Value.From == from && pair.Value.To == to).Select(pair => pair.Key)];

    /// <summary>The component a host belongs to, when a source stated that component's location.</summary>
    public string? ComponentForHost(string host) => _components.FirstOrDefault(pair => pair.Value.Location is { } location
        && string.Equals(HostOf(location), host, StringComparison.OrdinalIgnoreCase)).Key;

    public string Interface(string componentKey, TopologyInterfaceType type, string protocol, TopologyDirection direction, string address, string? operation,
        TopologyStatus level, TopologyProvenance provenance, string? contract = null, bool conflict = false, bool review = false, string? note = null)
    {
        var key = InterfaceKey(componentKey, type, direction, operation, address);
        if (!_interfaces.TryGetValue(key, out var state))
            _interfaces[key] = state = new InterfaceState { ComponentKey = componentKey, Type = type, Protocol = protocol, Direction = direction, Address = address.Trim(), Operation = operation?.Trim().ToUpperInvariant() is { Length: > 0 } op ? op : null };
        state.Contract ??= contract;
        state.Evidence.Add((level, provenance));
        state.Conflict |= conflict;
        state.Review |= review;
        if (note is not null && !state.Notes.Contains(note)) state.Notes.Add(note);
        return key;
    }

    public string Connection(string fromKey, string toKey, TopologyInterfaceType type, string protocol, string? address, TopologyStatus level, TopologyProvenance provenance,
        string? fromInterface = null, string? toInterface = null, string? contract = null, bool conflict = false, bool review = false, string? note = null, TopologyEvidence? evidence = null)
    {
        var key = $"{fromKey}->{toKey}|{type}|{NormalizeAddress(type, address ?? "")}";
        if (!_connections.TryGetValue(key, out var state))
            _connections[key] = state = new ConnectionState { From = fromKey, To = toKey, Type = type, Protocol = protocol, Address = address };
        state.FromInterface ??= fromInterface;
        state.ToInterface ??= toInterface;
        state.Contract ??= contract;
        state.Evidence.Add((level, provenance));
        state.Conflict |= conflict;
        state.Review |= review;
        if (note is not null && !state.Notes.Contains(note)) state.Notes.Add(note);
        var typed = evidence ?? Implied(level, provenance);
        if (!state.Typed.Contains(typed)) state.Typed.Add(typed);
        return key;
    }

    /// <summary>Evidence a provider stated without typing it: its level decides the strength (discovered or configured = strong, observed =
    /// runtime). Providers that know more pass their own evidence.</summary>
    private static TopologyEvidence Implied(TopologyStatus level, TopologyProvenance provenance) => level switch
    {
        TopologyStatus.Observed => new("runtime-observation", TopologyEvidenceStrength.Strong, "Runtime observation", provenance.Detail, provenance.Reference),
        TopologyStatus.Expected => new("expected", TopologyEvidenceStrength.Supporting, "Expected topology", provenance.Detail, provenance.Reference),
        _ => new("stated", TopologyEvidenceStrength.Strong, SourceFamily(provenance.Source), provenance.Detail, provenance.Reference),
    };

    public static string SourceFamily(TopologySource source) => source switch
    {
        TopologySource.SourceAnalysis => "Application source",
        TopologySource.IntegrationCatalog => "Configured integration",
        TopologySource.TargetEnvironment => "Target Environment",
        TopologySource.OpenApi or TopologySource.GraphQlSchema => "Contract",
        TopologySource.ExternalApplicationMetadata => "Application metadata",
        TopologySource.BrowserObservation or TopologySource.RuntimeEvidence => "Runtime observation",
        _ => "Expected topology",
    };

    /// <summary>
    /// The relationship state the evidence supports: very strong evidence, or strong evidence from two independent source families, resolves;
    /// one strong family is strongly supported; supporting evidence only is a candidate. Runtime evidence on a resolved relationship is Observed.
    /// Weak evidence never counts.
    /// </summary>
    public static TopologyRelationshipState Relationship(IReadOnlyCollection<TopologyEvidence> evidence)
    {
        var strong = evidence.Where(item => item.Strength == TopologyEvidenceStrength.Strong).Select(item => item.Source).Distinct(StringComparer.Ordinal).Count();
        var state = evidence.Any(item => item.Strength == TopologyEvidenceStrength.VeryStrong) || strong >= 2 ? TopologyRelationshipState.Resolved
            : strong == 1 ? TopologyRelationshipState.StronglySupported
            : evidence.Any(item => item.Strength == TopologyEvidenceStrength.Supporting) ? TopologyRelationshipState.Candidate
            : TopologyRelationshipState.ExpectedUnresolved;
        return state == TopologyRelationshipState.Resolved && evidence.Any(item => item.Source == "Runtime observation") ? TopologyRelationshipState.Observed : state;
    }

    public static string Unverified(TopologyRelationshipState state, IReadOnlyCollection<TopologyEvidence> evidence) =>
        (state == TopologyRelationshipState.Observed ? "Seen at runtime; " : "Not observed at runtime; ")
        + "delivery, health and contract compatibility are not verified"
        + (evidence.All(item => item.Source != "Infrastructure as Code") ? "; no infrastructure declaration confirms it" : "") + ".";

    public void Source(TopologySourceSummary summary) => _sources.Add(summary);


    public IntegrationTopologyView Build(string environmentId, string? environmentName, DateTimeOffset now, IReadOnlyList<string>? profiles = null)
    {
        MarkAddressConflicts();
        return BuildView(environmentId, environmentName, now, profiles);
    }

    /// <summary>
    /// The same from → to connection stated by different sources over different addresses (for example /api/report and /api/v2/report) is a
    /// conflict: neither is chosen silently.
    /// </summary>
    private void MarkAddressConflicts()
    {
        foreach (var group in _connections.Values.Where(item => item.Address is { Length: > 0 }).GroupBy(item => (item.From, item.To, item.Type)))
        {
            var addresses = group.Select(item => NormalizeAddress(item.Type, item.Address!)).Distinct(StringComparer.Ordinal).ToList();
            var sources = group.SelectMany(item => item.Evidence.Select(evidence => evidence.Provenance.Source)).Distinct().Count();
            if (addresses.Count < 2 || sources < 2) continue;
            foreach (var item in group)
            {
                item.Conflict = true;
                var note = $"Sources state different addresses for this connection: {string.Join(", ", group.Select(other => other.Address).Distinct())}.";
                if (!item.Notes.Contains(note)) item.Notes.Add(note);
            }
        }
    }

    private IntegrationTopologyView BuildView(string environmentId, string? environmentName, DateTimeOffset now, IReadOnlyList<string>? profiles) => new()
    {
        EnvironmentId = environmentId, EnvironmentName = environmentName, GeneratedAt = now, Sources = [.. _sources], Profiles = [.. profiles ?? []],
        Components = [.. _components.OrderBy(pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase).Select(pair => new TopologyComponent
        {
            Id = pair.Key, Name = pair.Value.Name, Kind = pair.Value.Kind, ComponentType = pair.Value.Type, Location = pair.Value.Location, Status = Level(pair.Value.Evidence, false, false),
            Provenance = Distinct(pair.Value.Evidence),
            Interfaces = [.. _interfaces.Where(item => item.Value.ComponentKey == pair.Key).OrderBy(item => item.Value.Type).ThenBy(item => item.Value.Address, StringComparer.Ordinal)
                .Select(item => new TopologyInterface
                {
                    Id = item.Key, ComponentId = pair.Key, Type = item.Value.Type, Protocol = item.Value.Protocol, Direction = item.Value.Direction, Address = item.Value.Address,
                    Operation = item.Value.Operation, ContractReference = item.Value.Contract, Status = Level(item.Value.Evidence, item.Value.Conflict, item.Value.Review),
                    Provenance = Distinct(item.Value.Evidence), Notes = item.Value.Notes,
                })],
        })],
        Connections = [.. _connections.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new TopologyConnection
        {
            Id = pair.Key, ProducerComponentId = pair.Value.From, ConsumerComponentId = pair.Value.To, ProducerInterfaceId = pair.Value.FromInterface, ConsumerInterfaceId = pair.Value.ToInterface,
            Type = pair.Value.Type, Protocol = pair.Value.Protocol, Address = pair.Value.Address, ContractReference = pair.Value.Contract,
            Status = Level(pair.Value.Evidence, pair.Value.Conflict, pair.Value.Review), Provenance = Distinct(pair.Value.Evidence), Notes = pair.Value.Notes,
            Evidence = [.. pair.Value.Typed], Relationship = Relationship(pair.Value.Typed), Unverified = Unverified(Relationship(pair.Value.Typed), pair.Value.Typed),
        })],
    };

    private static TopologyStatus Level(List<(TopologyStatus Level, TopologyProvenance Provenance)> evidence, bool conflict, bool review) =>
        conflict ? TopologyStatus.Conflict : review ? TopologyStatus.NeedsReview
        : evidence.Count == 0 ? TopologyStatus.NotEvaluated : evidence.Max(item => item.Level);

    private static List<TopologyProvenance> Distinct(List<(TopologyStatus Level, TopologyProvenance Provenance)> evidence) =>
        [.. evidence.Select(item => item.Provenance).DistinctBy(item => (item.Source, item.Reference))];

    public static string Normalize(string name) => NonAlphanumeric().Replace(name.Trim().ToLowerInvariant(), "");

    public static string InterfaceKey(string componentKey, TopologyInterfaceType type, TopologyDirection direction, string? operation, string address)
    {
        var side = direction is TopologyDirection.Provides or TopologyDirection.Consumes ? "in" : direction is TopologyDirection.Calls or TopologyDirection.Produces ? "out" : "unknown";
        return $"{componentKey}|{type}|{side}|{operation?.Trim().ToUpperInvariant()}|{NormalizeAddress(type, address)}";
    }

    /// <summary>REST paths: lower case, no trailing slash, path parameters and ids collapsed to {}. Other addresses: trimmed, lower case.</summary>
    public static string NormalizeAddress(TopologyInterfaceType type, string address)
    {
        var text = address.Trim().ToLowerInvariant();
        if (type is not (TopologyInterfaceType.Rest or TopologyInterfaceType.GraphQl)) return text;
        var path = text.Split('?', '#')[0].TrimEnd('/');
        return string.Join('/', path.Split('/').Select(segment => PathParameter().IsMatch(segment) ? "{}" : segment));
    }

    public static string? HostOf(string location) => Uri.TryCreate(location, UriKind.Absolute, out var uri) ? uri.Host : location.Trim();
}
