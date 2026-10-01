using System.Diagnostics;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.DatabaseArchitecture;
using BirkNext.SourceArchitecture;

namespace BirkNext.Api.Services.SourceArchitecture;

/// <summary>
/// Source-derived architecture of one immutable source snapshot. Extractors produce facts per project; this merger attributes library facts to the
/// deployable components that include them, resolves targets only from explicit evidence (application registration + development orchestration or
/// compose wiring of the same key), records unresolved targets as such, and marks disagreement as Conflict. Nothing here is project-specific.
/// </summary>
public static class SourceArchitectureAnalyzer
{
    public const int Version = 1;

    internal static readonly IReadOnlyList<IArchitectureExtractor> Extractors =
    [
        new DotNetProjectArchitectureExtractor(), new AspNetCoreArchitectureExtractor(), new HttpClientArchitectureExtractor(), new GraphQlArchitectureExtractor(),
        new EventHubArchitectureExtractor(), new ServiceBusArchitectureExtractor(), new WolverineArchitectureExtractor(), new DataStoreReferenceExtractor(),
        new StorageArchitectureExtractor(), new AuthenticationArchitectureExtractor(), new ConfigurationDependencyExtractor(),
    ];

    public static ArchitectureSnapshot Analyze(Guid sourceSnapshotId, IqrSourceArchiveReader.Workspace workspace, DateTimeOffset extractedAt,
        DatabaseArchitectureSnapshot? database = null, CancellationToken ct = default)
        => Analyze(sourceSnapshotId, workspace, extractedAt, database, ct, out _, out _);

    internal static ArchitectureSnapshot Analyze(Guid sourceSnapshotId, IqrSourceArchiveReader.Workspace workspace, DateTimeOffset extractedAt,
        DatabaseArchitectureSnapshot? database, CancellationToken ct, out ArchitectureInput input, out List<ArchitectureExtractionResult> results)
    {
        input = ArchitectureInput.From(sourceSnapshotId, workspace);
        var parsedInput = input;
        results = Extractors.Where(e => e.CanAnalyze(parsedInput)).Select(e => e.Analyze(parsedInput, ct)).ToList();
        return new Merger(input, results, database).Build(extractedAt);
    }

    private sealed class Merger(ArchitectureInput input, List<ArchitectureExtractionResult> results, DatabaseArchitectureSnapshot? database)
    {
        private readonly List<ArchitectureFact> _facts = results.SelectMany(r => r.Facts).ToList();
        private readonly List<ArchitectureDiagnostic> _diagnostics = [];
        private readonly Dictionary<string, ArchitectureDependency> _deps = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ChannelBuilder> _channels = new(StringComparer.Ordinal);
        private readonly Dictionary<string, StoreBuilder> _stores = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExternalSystem> _externals = new(StringComparer.Ordinal);
        private readonly List<ArchitectureInterface> _interfaces = [];
        private readonly List<ConfigurationReference> _config = [];
        private readonly Dictionary<ArchProject, ArchitectureComponent> _components = [];

        public ArchitectureSnapshot Build(DateTimeOffset extractedAt)
        {
            var deployables = input.Projects.Where(IsDeployable).ToList();
            var names = DisplayNames(deployables);
            foreach (var project in deployables) _components[project] = Classify(project, names[project.Path]);
            foreach (var (project, component) in _components.ToList()) Wire(project, component);
            var libraries = SharedLibraries();
            var technologies = Technologies();
            var components = _components.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
            var channels = _channels.Values.Select(c => c.Build()).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
            foreach (var channel in channels.Where(c => c.NameUnresolved))
                _diagnostics.Add(new("Channel name unresolved", $"{channel.Name}: the entity name is computed at runtime, so producers and consumers of it are not paired.", channel.Id));
            foreach (var channel in channels.Where(c => !c.NameUnresolved && (c.Producers.Count == 0 || c.Consumers.Count == 0)))
                _diagnostics.Add(new(c(channel), $"{channel.Name}: {(channel.Producers.Count == 0 ? "no producer" : "no consumer")} found in this source snapshot (it may live outside it).", channel.Id));
            foreach (var dep in _deps.Values.Where(d => d.EvidenceState == ArchitectureEvidenceState.Unresolved))
                _diagnostics.Add(new("Unresolved target", $"{Name(dep.FromComponentId)}: {dep.DependencyType} target not identified ({dep.TargetReference}).", dep.Id));
            foreach (var dep in _deps.Values.Where(d => d.EvidenceState == ArchitectureEvidenceState.Conflict))
                _diagnostics.Add(new("Conflict", $"{Name(dep.FromComponentId)}: evidence disagrees on the {dep.DependencyType} target ({string.Join(" vs ", dep.ConflictingTargets.Select(Name))}).", dep.Id));
            if (input.Aspire.Count > 0) _diagnostics.Add(new("Local orchestration only", ".NET Aspire AppHost wiring is development orchestration evidence; it is not the deployed topology."));
            if (input.Compose.Count > 0) _diagnostics.Add(new("Compose evidence", "Docker Compose services are container-composition evidence for local runs; they are not treated as runtime truth."));
            foreach (var p in input.Projects.Where(p => p.HasPackage("Wolverine") || p.HasPackage("WolverineFx")))
                if (!_facts.Any(f => f.Kind == "Wolverine" && input.Closure(p).Any(c => c.Path == f.ProjectPath)))
                    _diagnostics.Add(new("Wolverine configuration not found", $"{ArchitectureText.Safe(p.Name)} references Wolverine, but no host/routing configuration was found in this source."));
            _diagnostics.Add(new("Deployment not assessed", "Source architecture only: deployment and runtime traffic are not assessed."));
            foreach (var r in results) _diagnostics.AddRange(r.Diagnostics.Select(d => new ArchitectureDiagnostic(r.Extractor, d)));
            var limitations = new List<string> { ArchitectureSnapshot.SourceLimitation, "Deployment, runtime traffic and live connections are not assessed.", "No dependency detected does not mean no dependency exists." };
            limitations.AddRange(input.Limitations.Where(l => !l.StartsWith("Configuration values excluded", StringComparison.Ordinal)).Distinct().Take(20));
            var status = input.Projects.Count == 0 ? ArchitectureStatus.Unsupported
                : _deps.Values.Any(d => d.EvidenceState == ArchitectureEvidenceState.Conflict) ? ArchitectureStatus.NeedsReview
                : input.Limitations.Any(l => l.StartsWith("Not analyzed", StringComparison.Ordinal) || l.Contains("could not be parsed", StringComparison.Ordinal) || l.Contains("exceeds", StringComparison.Ordinal)) ? ArchitectureStatus.Partial
                : ArchitectureStatus.Complete;
            return new ArchitectureSnapshot
            {
                SourceSnapshotId = input.SourceSnapshotId, SourceFingerprint = input.Fingerprint, AnalyzerVersion = Version, ExtractedAt = extractedAt, Status = status,
                ExtractorVersions = Extractors.ToDictionary(e => e.Name, e => e.Version),
                Technologies = technologies, Components = components, Interfaces = _interfaces.OrderBy(i => i.ComponentId, StringComparer.Ordinal).ThenBy(i => i.Type, StringComparer.Ordinal).ToList(),
                Dependencies = _deps.Values.OrderBy(d => d.FromComponentId, StringComparer.Ordinal).ThenBy(d => d.Id, StringComparer.Ordinal).ToList(),
                MessagingChannels = channels, DataStores = _stores.Values.Select(s => s.Build(database)).OrderBy(s => s.LogicalName, StringComparer.Ordinal).ToList(),
                ExternalSystems = _externals.Values.OrderBy(e => e.Name, StringComparer.Ordinal).ToList(), SharedLibraries = libraries,
                ConfigurationReferences = _config.OrderBy(c => c.ComponentId, StringComparer.Ordinal).ThenBy(c => c.Key, StringComparer.Ordinal).ToList(),
                Diagnostics = _diagnostics.DistinctBy(d => (d.Kind, d.Message)).ToList(), Limitations = limitations,
            };
            static string c(MessagingChannel ch) => ch.Producers.Count == 0 ? "Producer not found" : "Consumer not found";
        }

        // ── Components ──────────────────────────────────────────────────────────────────────────────────────────────────

        private static bool IsDeployable(ArchProject p) =>
            !p.IsTest && !p.IsAspireHost && (p.Sdk.EndsWith(".Web", StringComparison.OrdinalIgnoreCase) || p.Sdk.EndsWith(".Worker", StringComparison.OrdinalIgnoreCase)
                || p.Sdk.Contains("BlazorWebAssembly", StringComparison.OrdinalIgnoreCase) || p.Sdk.Contains("Functions", StringComparison.OrdinalIgnoreCase)
                || p.OutputType is "Exe" or "WinExe" || p.HasPackage("Microsoft.Azure.Functions.Worker"));

        /// <summary>Project names without the dotted prefix every component shares (e.g. an organisation prefix), so nodes stay short.</summary>
        private static Dictionary<string, string> DisplayNames(List<ArchProject> projects)
        {
            var segments = projects.Select(p => p.Name.Split('.')).ToList();
            var common = 0;
            if (segments.Count > 1)
                while (segments.All(s => s.Length > common + 1) && segments.All(s => s[common] == segments[0][common])) common++;
            return projects.ToDictionary(p => p.Path, p => string.Join('.', p.Name.Split('.').Skip(common)));
        }

        private List<ArchitectureFact> FactsOf(ArchProject project, string kind) =>
            _facts.Where(f => f.Kind == kind && input.Closure(project).Any(c => c.Path == f.ProjectPath)).ToList();

        private IEnumerable<ArchProject> ComponentsIncluding(string projectPath) => _components.Keys.Where(c => input.Closure(c).Any(p => p.Path == projectPath));

        private ArchitectureComponent Classify(ArchProject p, string name)
        {
            bool Has(string kind) => FactsOf(p, kind).Count > 0;
            var evidence = new List<ArchitectureEvidence> { _facts.First(f => f.Kind == "Project" && f.ProjectPath == p.Path).Evidence };
            var state = ArchitectureEvidenceState.Confirmed;
            var confidence = "SDK / registrations";
            ArchitectureComponentType type;
            string runtime;
            var serves = Has("RestEndpoint") || Has("Controllers") || Has("GraphQlEndpoint") || Has("GraphQlServer");
            var background = Has("HostedService") || Has("BackgroundService") || Has("EventHubConsumer") || Has("ServiceBusReceive");
            if (p.Sdk.Contains("BlazorWebAssembly", StringComparison.OrdinalIgnoreCase)) { type = ArchitectureComponentType.Frontend; runtime = "Blazor WebAssembly (browser)"; }
            else if (Has("Function") || p.Sdk.Contains("Functions", StringComparison.OrdinalIgnoreCase)) { type = ArchitectureComponentType.Function; runtime = "Azure Functions"; }
            else if (Has("ReverseProxy")) { type = ArchitectureComponentType.Proxy; runtime = "ASP.NET Core web app"; }
            else if (p.Sdk.EndsWith(".Web", StringComparison.OrdinalIgnoreCase))
            {
                runtime = "ASP.NET Core web app";
                type = serves ? ArchitectureComponentType.Api : background ? ArchitectureComponentType.Worker : ArchitectureComponentType.Unknown;
            }
            else if (p.Sdk.EndsWith(".Worker", StringComparison.OrdinalIgnoreCase)) { type = ArchitectureComponentType.Worker; runtime = ".NET worker"; }
            else if (Has("Scheduled")) { type = ArchitectureComponentType.ScheduledJob; runtime = "Console app"; }
            else { type = Has("GenericHost") && background ? ArchitectureComponentType.Worker : ArchitectureComponentType.Cli; runtime = Has("GenericHost") ? ".NET generic host" : "Console app"; }
            if (Regex.IsMatch(p.Name, @"(?i)adapter") && type is ArchitectureComponentType.Worker or ArchitectureComponentType.Api or ArchitectureComponentType.Unknown)
            {
                type = ArchitectureComponentType.Adapter;
                state = ArchitectureEvidenceState.Inferred;
                confidence = "Adapter by project-name convention (hosting from SDK)";
            }
            var libraries = input.Closure(p).Where(x => x.Path != p.Path && !IsDeployable(x)).Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var tags = new List<string>();
            if (FactsOf(p, "ApiKeyAuth").Count > 0) tags.Add("API key authentication");
            if (FactsOf(p, "AzureIdentity").Count > 0) tags.Add("Azure identity (DefaultAzureCredential/managed identity)");
            if (input.Dockerfiles.Any(d => d.Text.Contains(p.Name + ".dll", StringComparison.Ordinal) || d.Text.Contains(p.Name + ".csproj", StringComparison.Ordinal))) tags.Add("Containerized (Dockerfile)");
            return new ArchitectureComponent
            {
                Id = ComponentId(p), Name = name, LogicalName = p.Name, ComponentType = type, RuntimeType = runtime, SourceProject = p.Path, Module = p.Module, Framework = p.Framework,
                EntryPoint = p.Code.FirstOrDefault(f => f.Path.EndsWith("/Program.cs", StringComparison.Ordinal))?.Path, HostingModel = runtime, EvidenceState = state, Confidence = confidence,
                Evidence = evidence, Tags = tags, IncludedLibraries = libraries, Technologies = [],
            };
        }

        private static string ComponentId(ArchProject p) => "component:" + ArchitectureText.Safe(p.Name);
        private string Name(string id) => _components.Values.FirstOrDefault(c => c.Id == id)?.Name ?? _channels.GetValueOrDefault(id)?.Name ?? _stores.GetValueOrDefault(id)?.Name ?? _externals.GetValueOrDefault(id)?.Name ?? id;

        /// <summary>Evidence from a library shared by several components cannot say which one runs it: resolved targets drop to Inferred.</summary>
        private (ArchitectureEvidenceState Cap, string Note) Attribution(ArchProject component, ArchitectureFact fact)
        {
            if (fact.ProjectPath == component.Path) return (ArchitectureEvidenceState.Confirmed, "");
            var library = input.ByPath[fact.ProjectPath].Name;
            var users = ComponentsIncluding(fact.ProjectPath).Where(c => c != component).Select(c => _components[c].Name).ToList();
            return users.Count == 0 ? (ArchitectureEvidenceState.Confirmed, $" (via library {library})")
                : (ArchitectureEvidenceState.Inferred, $" (via shared library {library}, also included by {string.Join(", ", users)})");
        }

        private static ArchitectureEvidenceState Cap(ArchitectureEvidenceState state, ArchitectureEvidenceState cap) =>
            state is ArchitectureEvidenceState.Unresolved or ArchitectureEvidenceState.Conflict ? state
            : cap == ArchitectureEvidenceState.Inferred ? ArchitectureEvidenceState.Inferred : state;

        // ── Wiring ──────────────────────────────────────────────────────────────────────────────────────────────────────

        private void Wire(ArchProject project, ArchitectureComponent component)
        {
            var aspire = input.Aspire.FirstOrDefault(a => Normalize(a.Resource.ProjectType) == Normalize(project.Name));
            var compose = ComposeServiceOf(project);
            var consumedEnv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Interfaces(project, component);
            ProjectReferences(project, component);
            Http(project, component, aspire, compose, consumedEnv);
            Messaging(project, component, aspire);
            Stores(project, component, aspire);
            Identity(project, component);
            Orchestration(project, component, aspire, compose, consumedEnv);
            Configuration(project, component);
            _components[project] = component with { Technologies = ComponentTechnologies(project) };
        }

        private void Interfaces(ArchProject p, ArchitectureComponent c)
        {
            void Add(string type, string name, string protocol, string direction, List<ArchitectureFact> facts, string? route = null, string? framework = null)
            {
                if (facts.Count == 0) return;
                _interfaces.Add(new ArchitectureInterface
                {
                    Id = $"{c.Id}|{type}|{route ?? name}", ComponentId = c.Id, Type = type, Name = name, Protocol = protocol, Direction = direction, RouteOrTopic = route, Framework = framework,
                    AuthRequirement = FactsOf(p, "Auth").Any(f => f["mode"] == "token validation") ? "Token validation registered" : FactsOf(p, "ApiKeyAuth").Count > 0 ? "API key handler registered" : null,
                    Evidence = facts.Select(f => f.Evidence).Take(10).ToList(), Confidence = ArchitectureEvidenceState.Confirmed,
                });
            }
            var rest = FactsOf(p, "RestEndpoint");
            Add("REST endpoints", $"{rest.Count} minimal-API route{(rest.Count == 1 ? "" : "s")}", "HTTP", "Inbound", rest, string.Join(", ", rest.Select(f => $"{f["method"]} {f["route"]}").Distinct().Take(8)));
            Add("REST controllers", "MVC controllers", "HTTP", "Inbound", FactsOf(p, "Controllers"));
            Add("GraphQL endpoint", "GraphQL server", "GraphQL over HTTP", "Inbound", FactsOf(p, "GraphQlServer").Concat(FactsOf(p, "GraphQlEndpoint")).ToList(),
                FactsOf(p, "GraphQlEndpoint").FirstOrDefault()?["route"], "Hot Chocolate");
            Add("Health checks", "Health endpoint", "HTTP", "Inbound", FactsOf(p, "HealthChecks"), FactsOf(p, "HealthChecks").FirstOrDefault()?["route"]);
            Add("Hosted services", string.Join(", ", FactsOf(p, "HostedService").Concat(FactsOf(p, "BackgroundService")).Select(f => f["type"]).Distinct().Take(6)), "In-process", "Internal",
                FactsOf(p, "HostedService").Concat(FactsOf(p, "BackgroundService")).ToList());
            Add("Event Hub consumer", "Event Hub consumer", "AMQP", "Inbound", FactsOf(p, "EventHubConsumer"));
            Add("Event Hub producer", "Event Hub producer", "AMQP", "Outbound", FactsOf(p, "EventHubProducer"));
            Add("Service Bus publisher", "Service Bus sender", "AMQP", "Outbound", FactsOf(p, "ServiceBusSend"), framework: FactsOf(p, "ServiceBusSend").Any(f => f["framework"] == "Wolverine") ? "Wolverine" : null);
            Add("Service Bus subscriber", "Service Bus processor/receiver", "AMQP", "Inbound", FactsOf(p, "ServiceBusReceive"), framework: FactsOf(p, "ServiceBusReceive").Any(f => f["framework"] == "Wolverine") ? "Wolverine" : null);
            Add("Blob client", "Blob storage client", "HTTPS", "Outbound", FactsOf(p, "Blob"));
            Add("DB access", string.Join(", ", FactsOf(p, "DbContext").Select(f => f["context"]).Distinct()), "Database", "Outbound", FactsOf(p, "DbContext"));
        }

        private void ProjectReferences(ArchProject p, ArchitectureComponent c)
        {
            foreach (var r in p.ProjectReferences.Where(r => input.ByPath.ContainsKey(r) && !input.ByPath[r].IsTest))
            {
                var target = input.ByPath[r];
                var to = IsDeployable(target) ? ComponentId(target) : $"library:{ArchitectureText.Safe(target.Name)}";
                AddDep(new ArchitectureDependency
                {
                    Id = $"{c.Id}|ProjectReference|{to}", FromComponentId = c.Id, ToId = to, DependencyType = ArchitectureDependencyType.ProjectReference, Protocol = "Build-time reference",
                    EvidenceState = ArchitectureEvidenceState.Confirmed, Confidence = "Explicit ProjectReference (build-time; not a runtime call)",
                    Evidence = [_facts.First(f => f.Kind == "ProjectReference" && f.ProjectPath == p.Path && f["target"] == r).Evidence],
                });
            }
        }

        private void Http(ArchProject p, ArchitectureComponent c, AspireProjectWiring? aspire, ComposeService? compose, HashSet<string> consumedEnv)
        {
            foreach (var fact in FactsOf(p, "HttpClient").Concat(FactsOf(p, "GraphQlClient")))
            {
                var (cap, note) = Attribution(p, fact);
                var graphQl = fact.Kind == "GraphQlClient";
                var key = fact["configKey"];
                var client = fact["client"] ?? "(unnamed)";
                if (fact["literalHost"] is { } host && KnownExternal(host) is { } external)
                {
                    AddExternal(external.Id, external.Name, external.Type, "HTTPS", key, fact.Evidence);
                    AddDep(new ArchitectureDependency
                    {
                        Id = $"{c.Id}|Http|{external.Id}", FromComponentId = c.Id, ToId = external.Id, DependencyType = ArchitectureDependencyType.Http, Protocol = "HTTPS", ConfigurationReference = key,
                        EvidenceState = Cap(ArchitectureEvidenceState.Confirmed, cap), Confidence = $"HttpClient with an explicit {external.Name} base address{note}", Evidence = [fact.Evidence],
                    });
                    continue;
                }
                var (targets, evidence, viaName) = ResolveEndpoint(p, key, client, aspire, compose, consumedEnv);
                var type = graphQl ? ArchitectureDependencyType.GraphQl : ArchitectureDependencyType.Http;
                var framework = fact["framework"] ?? (fact["kind"] == "typed" ? "Typed HttpClient" : "Named HttpClient");
                var reference = key is null ? $"HttpClient '{client}'" : fact["path"] is { } path ? $"{key} + {path}" : key;
                AddResolved(c, fact, type, graphQl ? "GraphQL over HTTP" : "HTTP", reference, framework, targets, evidence, cap, note, viaName, key);
            }
            foreach (var fact in FactsOf(p, "ReverseProxy"))
            {
                var (cap, note) = Attribution(p, fact);
                var section = fact["section"] ?? "ReverseProxy";
                var mapped = aspire?.Environment.Where(e => e.Key.StartsWith(section + ":Clusters:", StringComparison.OrdinalIgnoreCase) && e.EndpointOf is not null).ToList() ?? [];
                foreach (var env in mapped)
                {
                    consumedEnv.Add(env.Key);
                    var target = ProjectResourceComponent(env.EndpointOf!);
                    var cluster = Regex.Match(env.Key, @":Clusters:([^:]+)").Groups[1].Value;
                    AddResolved(c, fact, ArchitectureDependencyType.Http, "HTTP (reverse proxy)", $"{section}:Clusters:{cluster}", "YARP", target is null ? [] : [target],
                        [OrchestrationEvidence(aspire!, $"Development orchestration sets {env.Key} to the endpoint of '{env.EndpointOf}'")], cap, note, false);
                }
                var clusters = p.Configuration.SelectMany(f => f.Values.Keys).Select(k => Regex.Match(k, $@"^{Regex.Escape(section)}:Clusters:([^:]+):", RegexOptions.IgnoreCase))
                    .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var cluster in clusters.Where(cl => !mapped.Any(e => e.Key.Contains($":Clusters:{cl}:", StringComparison.OrdinalIgnoreCase))))
                    AddResolved(c, fact, ArchitectureDependencyType.Http, "HTTP (reverse proxy)", $"{section}:Clusters:{cluster}", "YARP", [], [], cap, note, false);
            }
        }

        /// <summary>
        /// Target of an endpoint-bound client: the component whose endpoint development orchestration (Aspire WithEnvironment(key, x.GetEndpoint))
        /// or compose (key=http://service…) assigns to exactly this configuration key. Never by name similarity. Several different answers = Conflict.
        /// </summary>
        private (List<string> Targets, List<ArchitectureEvidence> Evidence, bool ViaClientName) ResolveEndpoint(ArchProject p, string? key, string client,
            AspireProjectWiring? aspire, ComposeService? compose, HashSet<string> consumedEnv)
        {
            var targets = new List<string>();
            var evidence = new List<ArchitectureEvidence>();
            var viaName = false;
            bool Matches(string envKey, out bool byName)
            {
                byName = false;
                if (key is not null && (envKey.Equals(key, StringComparison.OrdinalIgnoreCase) || (!key.Contains(':') && envKey.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                    || (key.Contains(':') && envKey.Equals(key[..key.LastIndexOf(':')] + ":BaseUrl", StringComparison.OrdinalIgnoreCase) && HttpClientArchitectureExtractor.IsEndpointKey(key))))
                    return true;
                if (key is null && client != "(unnamed)" && envKey.StartsWith(client + ":", StringComparison.OrdinalIgnoreCase) && HttpClientArchitectureExtractor.IsEndpointKey(envKey)) { byName = true; return true; }
                return false;
            }
            foreach (var env in aspire?.Environment.Where(e => e.EndpointOf is not null) ?? [])
                if (Matches(env.Key, out var byName) && ProjectResourceComponent(env.EndpointOf!) is { } target)
                {
                    consumedEnv.Add(env.Key);
                    targets.Add(target);
                    viaName |= byName;
                    evidence.Add(OrchestrationEvidence(aspire!, $"Development orchestration sets {env.Key} to the endpoint of '{env.EndpointOf}'"));
                }
            foreach (var (envKey, value) in compose?.Environment ?? [])
            {
                var normalized = envKey.Replace("__", ":");
                if (!Matches(normalized, out var byName) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) continue;
                var service = input.Compose.FirstOrDefault(s => s.File == compose!.File && s.Name.Equals(uri.Host, StringComparison.OrdinalIgnoreCase));
                if (service is null || ComponentOfService(service) is not { } target) continue;
                targets.Add(target);
                viaName |= byName;
                evidence.Add(new ArchitectureEvidence(ArchitectureEvidenceKind.ContainerCompose, compose!.File, compose.Line, compose.Name, "Compose",
                    $"Compose service '{compose.Name}' sets {normalized} to service '{service.Name}'"));
            }
            _ = p;
            return (targets.Distinct(StringComparer.Ordinal).ToList(), evidence, viaName);
        }

        private void AddResolved(ArchitectureComponent c, ArchitectureFact fact, ArchitectureDependencyType type, string protocol, string reference, string? framework,
            List<string> targets, List<ArchitectureEvidence> resolution, ArchitectureEvidenceState cap, string note, bool viaName, string? configKey = null)
        {
            var distinct = targets.Distinct(StringComparer.Ordinal).ToList();
            var state = distinct.Count switch
            {
                0 => ArchitectureEvidenceState.Unresolved,
                1 => viaName ? ArchitectureEvidenceState.Inferred : Cap(ArchitectureEvidenceState.StronglySupported, cap),
                _ => ArchitectureEvidenceState.Conflict,
            };
            var to = distinct.Count == 1 ? distinct[0] : null;
            AddDep(new ArchitectureDependency
            {
                Id = $"{c.Id}|{type}|{to ?? reference}", FromComponentId = c.Id, ToId = to, TargetReference = reference, DependencyType = type, Protocol = protocol,
                ConfigurationReference = configKey ?? (reference.Contains(':') ? reference : null), Framework = framework, EvidenceState = state,
                Confidence = state switch
                {
                    ArchitectureEvidenceState.Unresolved => $"Client registered; the target of {reference} is not identified in this source{note}",
                    ArchitectureEvidenceState.Conflict => $"Evidence names {distinct.Count} different targets for {reference}{note}",
                    ArchitectureEvidenceState.Inferred when viaName => $"Target matched because the client name is also the configuration section the orchestration sets ({reference}){note}",
                    _ => $"Application registration + wiring of {reference}{note}",
                },
                Evidence = [fact.Evidence, .. resolution], ConflictingTargets = distinct.Count > 1 ? distinct : [],
                Diagnostics = state == ArchitectureEvidenceState.Unresolved ? ["Target not resolved — no evidence ties this key to a component. Nothing was guessed from names."] : [],
            });
        }

        private void Messaging(ArchProject p, ArchitectureComponent c, AspireProjectWiring? aspire)
        {
            var config = ComponentConfig(p);
            foreach (var fact in FactsOf(p, "EventHubConsumer").Concat(FactsOf(p, "EventHubProducer")))
            {
                var (cap, note) = Attribution(p, fact);
                var consumer = fact.Kind == "EventHubConsumer";
                var role = consumer ? ChannelRole.Consumer : ChannelRole.Producer;
                var depType = consumer ? ArchitectureDependencyType.EventHubConsume : ArchitectureDependencyType.EventHubProduce;
                var orchestration = aspire is null ? [] : aspire.References.Select(r => input.AspireResources.FirstOrDefault(x => x.Variable == r)).Where(r => r?.Method == "AddAzureEventHubs")
                    .Select(r => OrchestrationEvidence(aspire, $"Development orchestration references Event Hubs resource '{r!.Name}'")).ToList();
                if (fact["hub"] is { } literal)
                {
                    Endpoint(c, MessagingChannelType.EventHub, literal, [], role, Cap(ArchitectureEvidenceState.Confirmed, cap), null, [fact.Evidence, .. orchestration], depType,
                        $"Hub named in source{note}", fact["consumerGroup"], null);
                    continue;
                }
                var hubs = EntityValues(config, key => Regex.IsMatch(key, @"(?i)event\s*hub") && Regex.IsMatch(Last(key), @"(?i)^(event)?hub(name)?s?$|^name$|^eventhubnames?$"));
                var group = EntityValues(config, key => Regex.IsMatch(key, @"(?i)event\s*hub") && Regex.IsMatch(Last(key), @"(?i)^consumergroup$")).FirstOrDefault()?.Base;
                if (hubs.Count == 0)
                {
                    Endpoint(c, MessagingChannelType.EventHub, null, [], role, ArchitectureEvidenceState.Unresolved, null, [fact.Evidence], depType,
                        $"Event Hub {(consumer ? "consumer" : "producer")} exists, but its hub name is computed at runtime{note}", group ?? fact["consumerGroup"], $"{c.Name} event hub");
                    continue;
                }
                foreach (var hub in hubs)
                    Endpoint(c, MessagingChannelType.EventHub, hub.Base, hub.Variants, role, ArchitectureEvidenceState.Inferred, null,
                        [fact.Evidence, ConfigEvidence(hub.File, hub.Key, "Hub name listed in this component's configuration"), .. orchestration], depType,
                        $"Hub name from configuration key {hub.Key}; binding of that key to the {(consumer ? "processor" : "producer")} was not traced{note}", group ?? fact["consumerGroup"], null, hub.Key);
            }
            foreach (var fact in FactsOf(p, "ServiceBusSend").Concat(FactsOf(p, "ServiceBusReceive")))
            {
                var (cap, note) = Attribution(p, fact);
                var send = fact.Kind == "ServiceBusSend";
                var kind = fact["kind"];
                var type = kind == "topic" ? MessagingChannelType.ServiceBusTopic : kind == "queue" ? MessagingChannelType.ServiceBusQueue : MessagingChannelType.ServiceBusEntity;
                var depType = send ? ArchitectureDependencyType.ServiceBusPublish : ArchitectureDependencyType.ServiceBusConsume;
                var entity = fact["entity"];
                var state = Cap(ArchitectureEvidenceState.Confirmed, cap);
                var explanation = $"Entity named in source{note}";
                string? configKey = null;
                var extraEvidence = new List<ArchitectureEvidence>();
                if (entity is null && fact["expression"] is { } expression && FromConfiguration(config, expression) is { } resolved)
                {
                    entity = resolved.Value;
                    configKey = resolved.Key;
                    state = ArchitectureEvidenceState.Inferred;
                    explanation = $"Entity name read from configuration key {resolved.Key} (matched to the expression {expression}){note}";
                    extraEvidence.Add(ConfigEvidence(resolved.File, resolved.Key, "Entity name in this component's configuration"));
                }
                if (entity is null)
                {
                    Endpoint(c, type, null, [], send ? ChannelRole.Producer : ChannelRole.Consumer, ArchitectureEvidenceState.Unresolved, fact["framework"], [fact.Evidence], depType,
                        $"Service Bus {(send ? "sender" : "receiver")} exists, but its entity name is computed at runtime ({fact["expression"]}){note}", null,
                        $"{c.Name} Service Bus entity ({fact["expression"]})", subscription: fact["subscription"]);
                    continue;
                }
                Endpoint(c, type, entity, [], send ? ChannelRole.Producer : ChannelRole.Consumer, state, fact["framework"], [fact.Evidence, .. extraEvidence], depType, explanation, null, null, configKey, fact["subscription"]);
            }
        }

        private void Endpoint(ArchitectureComponent c, MessagingChannelType type, string? entity, Dictionary<string, string> variants, ChannelRole role, ArchitectureEvidenceState state,
            string? framework, List<ArchitectureEvidence> evidence, ArchitectureDependencyType depType, string explanation, string? consumerGroup, string? unresolvedName,
            string? configKey = null, string? subscription = null)
        {
            var family = type == MessagingChannelType.EventHub ? "eventhub" : "servicebus";
            var id = entity is null ? $"channel:{family}:unresolved:{ArchitectureText.Safe(c.Id)}:{ArchitectureText.Safe(unresolvedName ?? "")}" : $"channel:{family}:{entity.ToLowerInvariant()}";
            if (!_channels.TryGetValue(id, out var channel))
                _channels[id] = channel = new ChannelBuilder(id, entity ?? unresolvedName ?? "Unresolved channel", type, entity, entity is null);
            channel.Merge(type, variants, consumerGroup, subscription, configKey);
            channel.Endpoints.Add(new ChannelEndpoint(c.Id, role, state, framework, evidence));
            AddDep(new ArchitectureDependency
            {
                Id = $"{c.Id}|{depType}|{id}", FromComponentId = c.Id, ToId = id, DependencyType = depType, Protocol = "AMQP", Direction = role == ChannelRole.Producer ? "Outbound" : "Inbound",
                ConfigurationReference = configKey, Framework = framework, EvidenceState = entity is null ? ArchitectureEvidenceState.Unresolved : state, Confidence = explanation, Evidence = evidence,
                TargetReference = entity ?? unresolvedName,
                Diagnostics = entity is null ? ["Entity name not resolved in source, so this endpoint is not paired with any other component."] : [],
            });
        }

        private void Stores(ArchProject p, ArchitectureComponent c, AspireProjectWiring? aspire)
        {
            var referenced = aspire?.References.Select(r => input.AspireResources.FirstOrDefault(x => x.Variable == r)).OfType<AspireResource>().ToList() ?? [];
            foreach (var fact in FactsOf(p, "DbContext").Concat(FactsOf(p, "AdoConnection")))
            {
                var (cap, note) = Attribution(p, fact);
                var connection = fact["connection"];
                var connectionName = connection?.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase) == true ? connection["ConnectionStrings:".Length..] : null;
                var provider = fact["provider"];
                var storeType = provider switch { "UseSqlServer" or "SqlConnection" => DataStoreType.SqlServer, "UseNpgsql" or "NpgsqlConnection" => DataStoreType.PostgreSql,
                    "UseSqlite" => DataStoreType.Sqlite, "UseCosmos" => DataStoreType.Cosmos, _ => DataStoreType.Unknown };
                var resource = connectionName is null ? null : referenced.FirstOrDefault(r => r.Name.Equals(connectionName, StringComparison.OrdinalIgnoreCase) && r.Method is "AddDatabase" or "AddSqlServer" or "AddPostgres" or "AddAzureSqlServer" or "AddCosmosDB");
                var context = fact["context"];
                var id = resource is not null ? $"datastore:orchestrated:{resource.Name.ToLowerInvariant()}" : $"datastore:{ArchitectureText.Safe(c.Id)}:{ArchitectureText.Safe(context ?? connectionName ?? provider ?? "db")}";
                var store = Store(id, resource?.Name ?? context ?? connectionName ?? "Database", storeType, "Application database", connection, context);
                var evidence = new List<ArchitectureEvidence> { fact.Evidence };
                if (resource is not null) evidence.Add(OrchestrationEvidence(aspire!, $"Development orchestration references database resource '{resource.Name}' (connection name {connectionName})"));
                store.Add(c.Id, evidence, resource is not null ? ArchitectureEvidenceState.StronglySupported : ArchitectureEvidenceState.Confirmed);
                AddDep(new ArchitectureDependency
                {
                    Id = $"{c.Id}|DatabaseReadWrite|{id}", FromComponentId = c.Id, ToId = id, DependencyType = ArchitectureDependencyType.DatabaseReadWrite,
                    Protocol = storeType == DataStoreType.Unknown ? "Database" : storeType.ToString(), ConfigurationReference = connection,
                    EvidenceState = Cap(resource is not null ? ArchitectureEvidenceState.StronglySupported : ArchitectureEvidenceState.Confirmed, cap),
                    Confidence = $"{(context is not null ? $"DbContext {context}" : "ADO.NET connection")}{(provider is not null ? $" with {provider}" : "")}{note}", Evidence = evidence,
                });
            }
            foreach (var fact in FactsOf(p, "Redis"))
            {
                var (cap, note) = Attribution(p, fact);
                var resource = referenced.FirstOrDefault(r => r.Method == "AddRedis");
                var id = resource is not null ? $"datastore:orchestrated:{resource.Name.ToLowerInvariant()}" : $"datastore:{ArchitectureText.Safe(c.Id)}:redis";
                var evidence = new List<ArchitectureEvidence> { fact.Evidence };
                if (resource is not null) evidence.Add(OrchestrationEvidence(aspire!, $"Development orchestration references Redis resource '{resource.Name}'"));
                Store(id, resource?.Name ?? "Redis", DataStoreType.Redis, "Cache", fact["connection"], null).Add(c.Id, evidence, ArchitectureEvidenceState.Confirmed);
                AddDep(new ArchitectureDependency { Id = $"{c.Id}|Cache|{id}", FromComponentId = c.Id, ToId = id, DependencyType = ArchitectureDependencyType.Cache, Protocol = "Redis",
                    ConfigurationReference = fact["connection"], EvidenceState = Cap(ArchitectureEvidenceState.Confirmed, cap), Confidence = $"Redis connection registration{note}", Evidence = evidence });
            }
            foreach (var group in FactsOf(p, "Blob").GroupBy(f => f["usage"] ?? "application"))
            {
                var fact = group.First();
                var (cap, note) = Attribution(p, fact);
                var checkpoint = group.Key == "checkpoint";
                var resource = referenced.FirstOrDefault(r => r.Method is "AddBlobs" or "AddAzureStorage");
                var id = resource is not null ? $"datastore:orchestrated:{resource.Name.ToLowerInvariant()}" : $"datastore:{ArchitectureText.Safe(c.Id)}:blob:{group.Key}";
                var evidence = group.Select(f => f.Evidence).Take(5).ToList();
                if (resource is not null) evidence.Add(OrchestrationEvidence(aspire!, $"Development orchestration references storage resource '{resource.Name}'"));
                Store(id, resource?.Name ?? (checkpoint ? $"{c.Name} checkpoint storage" : $"{c.Name} blob storage"), DataStoreType.BlobStorage,
                    checkpoint ? "Event Hub checkpoint storage" : "Application storage", fact["configKey"], null).Add(c.Id, evidence, ArchitectureEvidenceState.Confirmed, checkpoint ? "Event Hub checkpoint storage" : "Application storage");
                AddDep(new ArchitectureDependency
                {
                    Id = $"{c.Id}|{(checkpoint ? "CheckpointStore" : "BlobReadWrite")}|{id}", FromComponentId = c.Id, ToId = id,
                    DependencyType = checkpoint ? ArchitectureDependencyType.CheckpointStore : ArchitectureDependencyType.BlobReadWrite, Protocol = "HTTPS (Blob)", ConfigurationReference = fact["configKey"],
                    EvidenceState = Cap(ArchitectureEvidenceState.Confirmed, cap),
                    Confidence = checkpoint ? $"Blob container used by an Event Hub processor as its checkpoint store — not business storage{note}" : $"Blob storage client{note}", Evidence = evidence,
                });
            }
        }

        private void Identity(ArchProject p, ArchitectureComponent c)
        {
            foreach (var fact in FactsOf(p, "Auth"))
            {
                var (cap, note) = Attribution(p, fact);
                var entra = fact["provider"] == "Microsoft Entra ID";
                var id = entra ? "external:microsoft-entra-id" : $"external:oidc:{ArchitectureText.Safe(fact["configKey"] ?? "issuer")}";
                AddExternal(id, entra ? "Microsoft Entra ID" : $"Token issuer ({fact["configKey"] ?? "configured"})", "Identity provider", "OIDC / OAuth 2.0", fact["configKey"], fact.Evidence);
                AddDep(new ArchitectureDependency
                {
                    Id = $"{c.Id}|Auth|{id}|{fact["mode"]}", FromComponentId = c.Id, ToId = id, DependencyType = ArchitectureDependencyType.Auth, Protocol = fact["mode"] ?? "auth",
                    ConfigurationReference = fact["configKey"], EvidenceState = Cap(entra ? ArchitectureEvidenceState.Confirmed : ArchitectureEvidenceState.Inferred, cap),
                    Confidence = $"{fact.Evidence.Explanation}{note}", Evidence = [fact.Evidence],
                });
            }
            foreach (var fact in FactsOf(p, "KeyVault"))
            {
                var (cap, note) = Attribution(p, fact);
                AddExternal("external:azure-key-vault", "Azure Key Vault", "Secret store", "HTTPS", fact["configKey"], fact.Evidence);
                AddDep(new ArchitectureDependency { Id = $"{c.Id}|KeyVault|external:azure-key-vault", FromComponentId = c.Id, ToId = "external:azure-key-vault", DependencyType = ArchitectureDependencyType.KeyVault,
                    Protocol = "HTTPS", ConfigurationReference = fact["configKey"], EvidenceState = Cap(ArchitectureEvidenceState.Confirmed, cap), Confidence = $"Secret/configuration dependency (no secret names or values kept){note}", Evidence = [fact.Evidence] });
            }
            foreach (var fact in FactsOf(p, "Observability"))
            {
                var (cap, note) = Attribution(p, fact);
                var id = $"external:{ArchitectureText.Safe((fact["provider"] ?? "telemetry").ToLowerInvariant().Replace(' ', '-'))}";
                AddExternal(id, fact["provider"] ?? "Telemetry backend", "Observability", "HTTPS", null, fact.Evidence);
                AddDep(new ArchitectureDependency { Id = $"{c.Id}|Observability|{id}", FromComponentId = c.Id, ToId = id, DependencyType = ArchitectureDependencyType.Observability, Protocol = "Telemetry export",
                    EvidenceState = Cap(ArchitectureEvidenceState.Confirmed, cap), Confidence = $"Telemetry exporter registration{note}", Evidence = [fact.Evidence] });
            }
            foreach (var fact in FactsOf(p, "ExternalApi"))
            {
                var (cap, note) = Attribution(p, fact);
                var id = $"external:{ArchitectureText.Safe((fact["name"] ?? "api").ToLowerInvariant().Replace(' ', '-'))}";
                AddExternal(id, fact["name"] ?? "External API", fact["type"] ?? "External API", "HTTPS", null, fact.Evidence);
                AddDep(new ArchitectureDependency { Id = $"{c.Id}|Http|{id}", FromComponentId = c.Id, ToId = id, DependencyType = ArchitectureDependencyType.Http, Protocol = "HTTPS",
                    EvidenceState = Cap(ArchitectureEvidenceState.Confirmed, cap), Confidence = $"{fact["name"]} named explicitly in source{note}", Evidence = [fact.Evidence] });
            }
        }

        /// <summary>Orchestration-only relationships (WaitFor/WithReference to other projects, compose depends_on) and endpoint env vars not tied to a traced client.</summary>
        private void Orchestration(ArchProject p, ArchitectureComponent c, AspireProjectWiring? aspire, ComposeService? compose, HashSet<string> consumedEnv)
        {
            if (aspire is not null)
            {
                foreach (var env in aspire.Environment.Where(e => e.EndpointOf is not null && !consumedEnv.Contains(e.Key)))
                    if (ProjectResourceComponent(env.EndpointOf!) is { } target)
                        AddDep(new ArchitectureDependency
                        {
                            Id = $"{c.Id}|ConfigurationDependency|{target}|{env.Key}", FromComponentId = c.Id, ToId = target, TargetReference = env.Key, DependencyType = ArchitectureDependencyType.ConfigurationDependency,
                            Protocol = "HTTP endpoint (configuration)", ConfigurationReference = env.Key, EvidenceState = ArchitectureEvidenceState.Inferred,
                            Confidence = $"Development orchestration supplies the endpoint of '{env.EndpointOf}' to {env.Key}; the application's use of that key was not traced",
                            Evidence = [OrchestrationEvidence(aspire, $"WithEnvironment({env.Key}, {env.EndpointOf}.GetEndpoint)")],
                        });
                foreach (var reference in aspire.References.Concat(aspire.WaitFor).Distinct())
                    if (ProjectResourceComponent(reference) is { } target && target != c.Id)
                        AddDep(new ArchitectureDependency
                        {
                            Id = $"{c.Id}|OrchestrationDependency|{target}", FromComponentId = c.Id, ToId = target, DependencyType = ArchitectureDependencyType.OrchestrationDependency,
                            Protocol = "Development orchestration", EvidenceState = ArchitectureEvidenceState.Inferred,
                            Confidence = "AppHost references/waits for this component locally; not a runtime call and not deployed topology",
                            Evidence = [OrchestrationEvidence(aspire, $"WithReference/WaitFor({reference})")],
                        });
            }
            foreach (var dependsOn in compose?.DependsOn ?? [])
                if (input.Compose.FirstOrDefault(s => s.File == compose!.File && s.Name == dependsOn) is { } service && ComponentOfService(service) is { } target && target != c.Id)
                    AddDep(new ArchitectureDependency
                    {
                        Id = $"{c.Id}|OrchestrationDependency|{target}|compose", FromComponentId = c.Id, ToId = target, DependencyType = ArchitectureDependencyType.OrchestrationDependency,
                        Protocol = "Container composition", EvidenceState = ArchitectureEvidenceState.Inferred, Confidence = "Compose depends_on (start order), not a runtime call",
                        Evidence = [new ArchitectureEvidence(ArchitectureEvidenceKind.ContainerCompose, compose!.File, compose.Line, compose.Name, "Compose", $"depends_on {dependsOn}")],
                    });
        }

        private void Configuration(ArchProject p, ArchitectureComponent c)
        {
            var files = input.Closure(p).SelectMany(x => x.Configuration).ToList();
            foreach (var group in files.SelectMany(f => f.Values.Select(v => (File: f, Key: Regex.Replace(v.Key, @":\d+(?=:|$)", ":*"), Raw: v.Key, v.Value))).GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var purpose = Purpose(group.Key);
                if (purpose is null) continue;
                var safe = group.Select(x => ArchitectureText.SafeValue(x.Raw, x.Value)).OfType<string>().Distinct(StringComparer.Ordinal).Take(10).ToList();
                _config.Add(new ConfigurationReference(c.Id, ArchitectureText.Safe(group.Key), purpose, group.Select(x => ArchitectureText.Safe(x.File.Path)).Distinct().ToList(), safe));
            }
        }

        private static string? Purpose(string key) =>
            key.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase) ? "Datastore connection (value never shown)"
            : Regex.IsMatch(key, @"(?i)(event\s*hub|servicebus|topic|queue|koe|subscription|consumergroup)") ? "Messaging"
            : Regex.IsMatch(key, @"(?i)^(azuread|entra|authentication|jwt|oidc)|:(authority|clientid|tenantid|instance|audience)$") ? "Authentication"
            : Regex.IsMatch(key, @"(?i)keyvault|vaulturi") ? "Secrets (Key Vault)"
            : Regex.IsMatch(key, @"(?i)(storage|blob|container)") ? "Storage"
            : Regex.IsMatch(key, @"(?i)(baseurl|baseaddress|:url$|:uri$|endpoint|reverseproxy:clusters)") ? "Endpoint"
            : null;

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

        private void AddDep(ArchitectureDependency dep)
        {
            if (_deps.TryGetValue(dep.Id, out var existing))
                _deps[dep.Id] = existing with { Evidence = [.. existing.Evidence, .. dep.Evidence.Where(e => !existing.Evidence.Contains(e))] };
            else _deps[dep.Id] = dep;
        }

        private void AddExternal(string id, string name, string type, string protocol, string? key, ArchitectureEvidence evidence)
        {
            if (_externals.TryGetValue(id, out var existing)) { if (!existing.Evidence.Contains(evidence) && existing.Evidence.Count < 20) existing.Evidence.Add(evidence); return; }
            _externals[id] = new ExternalSystem { Id = id, Name = name, Type = type, Protocol = protocol, ConfigurationReference = key, Evidence = [evidence], Confidence = ArchitectureEvidenceState.Confirmed };
        }

        private StoreBuilder Store(string id, string name, DataStoreType type, string usage, string? connection, string? context)
        {
            if (!_stores.TryGetValue(id, out var store)) _stores[id] = store = new StoreBuilder(id, name, type, connection, context);
            store.Usages.Add(usage);
            return store;
        }

        private static (string Id, string Name, string Type)? KnownExternal(string host) =>
            host.Contains("graph.microsoft.com", StringComparison.OrdinalIgnoreCase) ? ("external:microsoft-graph", "Microsoft Graph", "Microsoft cloud API")
            : host.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ? ("external:microsoft-entra-id", "Microsoft Entra ID", "Identity provider")
            : null;

        private string? ProjectResourceComponent(string variable)
        {
            var resource = input.AspireResources.FirstOrDefault(r => r.Variable == variable && r.ProjectType is not null);
            if (resource is null) return null;
            var project = _components.Keys.FirstOrDefault(p => Normalize(p.Name) == Normalize(resource.ProjectType));
            return project is null ? null : ComponentId(project);
        }

        private ComposeService? ComposeServiceOf(ArchProject p) => input.Compose.FirstOrDefault(s => ComponentOfServiceProject(s) == p);
        private string? ComponentOfService(ComposeService s) => ComponentOfServiceProject(s) is { } p && _components.ContainsKey(p) ? ComponentId(p) : null;

        /// <summary>The compose service's build context, resolved to exactly one deployable project (or the one its Dockerfile names).</summary>
        private ArchProject? ComponentOfServiceProject(ComposeService s)
        {
            if (s.BuildContext is null) return null;
            var baseDir = s.File.Contains('/') ? s.File[..(s.File.LastIndexOf('/') + 1)] : "";
            var context = ArchitectureInput.Normalize(baseDir + s.BuildContext);
            var candidates = input.Projects.Where(IsDeployable).Where(p => context.Length == 0 || p.Directory.StartsWith(context + "/", StringComparison.Ordinal)).ToList();
            if (candidates.Count == 1) return candidates[0];
            var dockerfile = input.Dockerfiles.FirstOrDefault(d => d.Path.StartsWith(context, StringComparison.Ordinal));
            return dockerfile.Text is null ? null : candidates.FirstOrDefault(p => dockerfile.Text.Contains(p.Name + ".dll", StringComparison.Ordinal));
        }

        private static string Normalize(string? name) => Regex.Replace(name ?? "", @"[^A-Za-z0-9]", "_").ToLowerInvariant();
        private static string Last(string key) { var k = Regex.Replace(key, @":\d+$", ""); return k.Contains(':') ? k[(k.LastIndexOf(':') + 1)..] : k; }

        private List<ConfigFile> ComponentConfig(ArchProject p) => input.Closure(p).SelectMany(x => x.Configuration).Where(f => !f.Path.EndsWith(".graphqlrc.json", StringComparison.OrdinalIgnoreCase)).ToList();

        private sealed record EntityValue(string Key, string File, string Base, Dictionary<string, string> Variants);

        /// <summary>Entity names under matching keys, one per array index/comma item, with per-environment variants (appsettings.{Env}.json).</summary>
        private static List<EntityValue> EntityValues(List<ConfigFile> files, Func<string, bool> keyMatch)
        {
            var slots = new Dictionary<string, Dictionary<string, (string Value, string File)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
                foreach (var (key, value) in file.Values.Where(v => keyMatch(v.Key)))
                {
                    if (ArchitectureText.SafeValue(key, value) is not { } safe) continue;
                    var items = safe.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                    for (var i = 0; i < items.Count; i++)
                    {
                        var slot = items.Count > 1 ? $"{key}:{i}" : key;
                        if (!slots.TryGetValue(slot, out var envs)) slots[slot] = envs = new(StringComparer.OrdinalIgnoreCase);
                        envs[file.Environment] = (items[i], file.Path);
                    }
                }
            return slots.Select(s =>
            {
                var baseEnv = s.Value.ContainsKey("") ? "" : s.Value.Keys.Order(StringComparer.OrdinalIgnoreCase).First();
                return new EntityValue(ArchitectureText.Safe(Regex.Replace(s.Key, @":\d+$", ":*")), ArchitectureText.Safe(s.Value[baseEnv].File), s.Value[baseEnv].Value,
                    s.Value.Where(v => v.Key.Length > 0).ToDictionary(v => v.Key, v => v.Value.Value));
            }).ToList();
        }

        /// <summary>An entity name read from configuration when the source expression ends with the same member path as a key (e.g. opts.Leselogg.KoeName ↔ …:Leselogg:KoeName).</summary>
        private static (string Key, string Value, string File)? FromConfiguration(List<ConfigFile> files, string expression)
        {
            var parts = expression.Split('.').Skip(1).Where(x => Regex.IsMatch(x, @"^\w+$")).ToList();
            if (parts.Count == 0) return null;
            var suffix = ":" + string.Join(':', parts);
            foreach (var file in files.OrderBy(f => f.Environment.Length))
                foreach (var (key, value) in file.Values.Where(v => (":" + v.Key).EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                    if (ArchitectureText.SafeValue(key, value) is { } safe && !safe.Contains(','))
                        return (ArchitectureText.Safe(key), safe, ArchitectureText.Safe(file.Path));
            return null;
        }

        private static ArchitectureEvidence ConfigEvidence(string file, string key, string explanation) =>
            new(ArchitectureEvidenceKind.Configuration, file, 1, key, "Configuration", explanation);

        private static ArchitectureEvidence OrchestrationEvidence(AspireProjectWiring aspire, string explanation) =>
            new(ArchitectureEvidenceKind.DevelopmentOrchestration, aspire.File, aspire.Line, aspire.Resource.Name, "Aspire", explanation + " (local development orchestration)");

        private List<SharedLibrary> SharedLibraries()
        {
            var libraries = input.Projects.Where(p => !p.IsTest && !IsDeployable(p) && !p.IsAspireHost)
                .Select(p => new SharedLibrary($"library:{ArchitectureText.Safe(p.Name)}", p.Name, p.Path, ComponentsIncluding(p.Path).Select(ComponentId).Order(StringComparer.Ordinal).ToList(), true)).ToList();
            var packages = _components.Keys.SelectMany(c => input.Closure(c).SelectMany(p => p.Packages).Distinct().Select(pkg => (pkg, c)))
                .GroupBy(x => x.pkg, StringComparer.OrdinalIgnoreCase)
                .Select(g => new SharedLibrary($"package:{ArchitectureText.Safe(g.Key)}", ArchitectureText.Safe(g.Key), "", g.Select(x => ComponentId(x.c)).Distinct().Order(StringComparer.Ordinal).ToList(), false));
            return [.. libraries.OrderBy(l => l.Name, StringComparer.Ordinal), .. packages.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)];
        }

        private static readonly (string Name, string Category, Func<ArchProject, bool> Match)[] PackageTechnologies =
        [
            ("ASP.NET Core", "Hosting", p => p.Sdk.EndsWith(".Web", StringComparison.OrdinalIgnoreCase)),
            ("Blazor WebAssembly", "Frontend", p => p.Sdk.Contains("BlazorWebAssembly", StringComparison.OrdinalIgnoreCase)),
            (".NET Worker", "Hosting", p => p.Sdk.EndsWith(".Worker", StringComparison.OrdinalIgnoreCase)),
            ("Hot Chocolate (GraphQL server)", "API", p => p.HasPackage("HotChocolate")),
            ("Strawberry Shake (GraphQL client)", "API", p => p.HasPackage("StrawberryShake")),
            ("Azure Event Hubs", "Messaging", p => p.HasPackage("Azure.Messaging.EventHubs")),
            ("Azure Service Bus", "Messaging", p => p.HasPackage("Azure.Messaging.ServiceBus")),
            ("Wolverine", "Messaging", p => p.HasPackage("Wolverine") || p.HasPackage("WolverineFx")),
            ("EF Core SQL Server", "Data", p => p.HasPackage("Microsoft.EntityFrameworkCore.SqlServer")),
            ("EF Core PostgreSQL", "Data", p => p.HasPackage("Npgsql.EntityFrameworkCore")),
            ("Azure Blob Storage", "Data", p => p.HasPackage("Azure.Storage.Blobs")),
            ("Redis", "Data", p => p.HasPackage("StackExchange.Redis") || p.HasPackage("Microsoft.Extensions.Caching.StackExchangeRedis")),
            ("Microsoft Identity Web", "Identity", p => p.HasPackage("Microsoft.Identity.Web")),
            ("MSAL (WebAssembly)", "Identity", p => p.HasPackage("Microsoft.Authentication.WebAssembly.Msal")),
            ("JWT bearer", "Identity", p => p.HasPackage("Microsoft.AspNetCore.Authentication.JwtBearer")),
            ("YARP", "Gateway", p => p.HasPackage("Yarp.ReverseProxy")),
            ("Azure Key Vault", "Secrets", p => p.HasPackage("Azure.Security.KeyVault") || p.HasPackage("Azure.Extensions.AspNetCore.Configuration.Secrets")),
            ("OpenTelemetry / Azure Monitor", "Observability", p => p.HasPackage("OpenTelemetry") || p.HasPackage("Azure.Monitor.OpenTelemetry") || p.HasPackage("Microsoft.ApplicationInsights")),
        ];

        private List<string> ComponentTechnologies(ArchProject p)
        {
            var closure = input.Closure(p);
            var techs = PackageTechnologies.Where(t => closure.Any(t.Match)).Select(t => t.Name).ToList();
            if (_facts.Any(f => f.Kind == "ReverseProxy" && closure.Any(c => c.Path == f.ProjectPath)) && !techs.Contains("YARP")) techs.Add("YARP");
            if (_facts.Any(f => f.Kind == "Wolverine" && closure.Any(c => c.Path == f.ProjectPath)) && !techs.Contains("Wolverine")) techs.Add("Wolverine");
            return techs;
        }

        private List<ArchitectureTechnology> Technologies()
        {
            var list = PackageTechnologies.Select(t => new ArchitectureTechnology(t.Name, t.Category, _components.Where(c => c.Value.Technologies.Contains(t.Name)).Select(c => c.Value.Id).Order(StringComparer.Ordinal).ToList()))
                .Where(t => t.Components.Count > 0).ToList();
            if (input.Aspire.Count > 0) list.Add(new(".NET Aspire (development orchestration)", "Orchestration", []));
            if (input.Compose.Count > 0) list.Add(new("Docker Compose", "Orchestration", []));
            if (input.Dockerfiles.Count > 0) list.Add(new("Dockerfile", "Container", []));
            return list;
        }
    }

    private sealed class ChannelBuilder(string id, string name, MessagingChannelType type, string? entity, bool unresolved)
    {
        private MessagingChannelType _type = type;
        private readonly Dictionary<string, string> _variants = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _config = new(StringComparer.OrdinalIgnoreCase);
        private string? _group, _subscription;
        public List<ChannelEndpoint> Endpoints { get; } = [];
        public string Name => name;

        public void Merge(MessagingChannelType type, Dictionary<string, string> variants, string? group, string? subscription, string? configKey)
        {
            // A Service Bus entity only named as "topic" or "queue" by one side stays the more specific kind; disagreeing kinds stay generic.
            if (_type == MessagingChannelType.ServiceBusEntity && type is MessagingChannelType.ServiceBusTopic or MessagingChannelType.ServiceBusQueue) _type = type;
            else if (_type != type && type != MessagingChannelType.ServiceBusEntity && _type != MessagingChannelType.EventHub) _type = MessagingChannelType.ServiceBusEntity;
            foreach (var (k, v) in variants) _variants[k] = v;
            _group ??= group;
            _subscription ??= subscription;
            if (configKey is not null) _config.Add(configKey);
        }

        public MessagingChannel Build()
        {
            var producers = Endpoints.Where(e => e.Role == ChannelRole.Producer).DistinctBy(e => e.ComponentId).ToList();
            var consumers = Endpoints.Where(e => e.Role == ChannelRole.Consumer).DistinctBy(e => e.ComponentId).ToList();
            var states = Endpoints.Select(e => e.State).ToList();
            return new MessagingChannel
            {
                Id = id, Name = name, Type = _type, EntityName = entity, EnvironmentVariants = new(_variants), ConsumerGroup = _group, Subscription = _subscription,
                Producers = producers, Consumers = consumers, ConfigurationReferences = [.. _config], NameUnresolved = unresolved,
                Evidence = Endpoints.SelectMany(e => e.Evidence).Distinct().Take(20).ToList(),
                Confidence = unresolved ? ArchitectureEvidenceState.Unresolved : states.Contains(ArchitectureEvidenceState.Confirmed) ? ArchitectureEvidenceState.Confirmed : states.Min(),
            };
        }
    }

    private sealed class StoreBuilder(string id, string name, DataStoreType type, string? connection, string? context)
    {
        private readonly HashSet<string> _components = new(StringComparer.Ordinal);
        private readonly List<ArchitectureEvidence> _evidence = [];
        private ArchitectureEvidenceState _state = ArchitectureEvidenceState.Confirmed;
        public HashSet<string> Usages { get; } = new(StringComparer.Ordinal);
        public string Name => name;

        public void Add(string componentId, List<ArchitectureEvidence> evidence, ArchitectureEvidenceState state, string? usage = null)
        {
            _components.Add(componentId);
            _evidence.AddRange(evidence.Where(e => !_evidence.Contains(e)).Take(10));
            if (state == ArchitectureEvidenceState.StronglySupported) _state = state;
            if (usage is not null) Usages.Add(usage);
        }

        public ArchitectureDataStoreReference Build(DatabaseArchitectureSnapshot? database)
        {
            // Database drilldown only for the same source snapshot and exactly this DbContext — never by a similar name.
            var model = context is null ? null : database?.Databases.FirstOrDefault(d => d.DbContext == context);
            return new ArchitectureDataStoreReference
            {
                Id = id, LogicalName = name, StoreType = type, Usage = string.Join(" + ", Usages.Order(StringComparer.Ordinal)), ReferencedByComponents = [.. _components.Order(StringComparer.Ordinal)],
                ConnectionReference = connection is null ? null : ArchitectureText.Safe(connection), DbContext = context, DatabaseModelId = model?.Id, Evidence = _evidence, Confidence = _state,
            };
        }
    }

    internal static long Measure(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        return watch.ElapsedMilliseconds;
    }
}
