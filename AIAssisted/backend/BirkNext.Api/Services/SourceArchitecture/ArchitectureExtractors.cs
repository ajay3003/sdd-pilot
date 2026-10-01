using System.Text.RegularExpressions;
using BirkNext.SourceArchitecture;

namespace BirkNext.Api.Services.SourceArchitecture;

/// <summary>One source-derived fact about one project. Data holds identifiers only (client names, configuration keys, entity names from literals).</summary>
internal sealed record ArchitectureFact(string Kind, string ProjectPath, ArchitectureEvidence Evidence, IReadOnlyDictionary<string, string> Data)
{
    public string? this[string key] => Data.TryGetValue(key, out var v) ? v : null;
}

internal sealed record ArchitectureExtractionResult(string Extractor, string Version, List<ArchitectureFact> Facts, List<string> Diagnostics);

/// <summary>A modular, project-agnostic extractor. It reads the snapshot's projects and returns facts; it never decides targets or merges.</summary>
internal interface IArchitectureExtractor
{
    string Name { get; }
    string Version { get; }
    bool CanAnalyze(ArchitectureInput input);
    ArchitectureExtractionResult Analyze(ArchitectureInput input, CancellationToken ct);
}

/// <summary>Regex scanning over comment-stripped C# of non-test projects, with helpers to read configuration keys out of an expression.</summary>
internal abstract class CodeExtractor : IArchitectureExtractor
{
    public abstract string Name { get; }
    public virtual string Version => "1";
    public virtual bool CanAnalyze(ArchitectureInput input) => input.Projects.Any(p => !p.IsTest && p.Code.Count > 0);

    public ArchitectureExtractionResult Analyze(ArchitectureInput input, CancellationToken ct)
    {
        var facts = new List<ArchitectureFact>();
        var diagnostics = new List<string>();
        foreach (var project in input.Projects.Where(p => !p.IsTest && !p.IsAspireHost))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var file in project.Code) Scan(input, project, file, facts, diagnostics);
        }
        return new(Name, Version, facts, diagnostics);
    }

    protected abstract void Scan(ArchitectureInput input, ArchProject project, CodeFile file, List<ArchitectureFact> facts, List<string> diagnostics);

    protected ArchitectureFact Fact(string kind, ArchProject project, CodeFile file, int index, string explanation, params (string Key, string? Value)[] data) =>
        new(kind, project.Path, new ArchitectureEvidence(ArchitectureEvidenceKind.ApplicationSource, file.Path, file.Line(index), ArchitectureText.EnclosingType(file.Text, index), Name, explanation),
            data.Where(d => d.Value is not null).ToDictionary(d => d.Key, d => d.Value!, StringComparer.Ordinal));

    /// <summary>
    /// Configuration keys an expression reads: indexer/GetValue/GetSection/GetConnectionString literals, options types bound to a section
    /// (SectionName constants, Configure/Bind/BindConfiguration), and local variables assigned from configuration in the same file.
    /// </summary>
    protected static List<string> ConfigKeys(ArchitectureInput input, ArchProject project, CodeFile file, string expression, int depth = 0) =>
        ConfigReferences(input, project, file, expression, depth).Select(r => r.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Configuration keys with the literal path an interpolation appends to them (e.g. ApiBaseUrl + "person/graphql/").</summary>
    protected static List<(string Key, string? Path)> ConfigReferences(ArchitectureInput input, ArchProject project, CodeFile file, string expression, int depth = 0)
    {
        var refs = ConfigKeysCore(input, project, file, expression, depth).Select(k => (Key: k, Path: (string?)null)).ToList();
        if (depth < 3)
            foreach (Match m in Regex.Matches(expression, @"new\s+Uri\(\s*(\w+)\b|UseSqlServer\(\s*(\w+)\b|UseNpgsql\(\s*(\w+)\b|\(\s*(\w+)\s*,|^\s*(\w+)\s*$"))
            {
                var name = m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value;
                var assignment = Regex.Match(file.Text, $@"(?:var|string\??)\s+{Regex.Escape(name)}\s*=\s*([^;]+);");
                if (assignment.Success) refs.AddRange(ConfigReferences(input, project, file, assignment.Groups[1].Value, depth + 1));
            }
        if (depth < 3)
            foreach (Match m in Regex.Matches(expression, @"\$@?""([^""]*)"""))
                foreach (Match hole in Regex.Matches(m.Groups[1].Value, @"\{(\w+)\}([^{]*)"))
                {
                    var assignment = Regex.Match(file.Text, $@"(?:var|string\??)\s+{Regex.Escape(hole.Groups[1].Value)}\s*=\s*([^;]+);");
                    if (!assignment.Success) continue;
                    foreach (var (key, path) in ConfigReferences(input, project, file, assignment.Groups[1].Value, depth + 1))
                    {
                        var combined = (path ?? "") + hole.Groups[2].Value;
                        refs.Add((key, combined.Length > 0 ? combined : null));
                    }
                }
        return refs.DistinctBy(r => (r.Key.ToLowerInvariant(), r.Path)).ToList();
    }

    private static List<string> ConfigKeysCore(ArchitectureInput input, ArchProject project, CodeFile file, string expression, int depth)
    {
        var keys = new List<string>();
        foreach (Match m in Regex.Matches(expression, @"(?:Configuration|configuration|config|_configuration|cfg)\s*\[\s*""([^""]+)""\s*\]")) keys.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(expression, @"\.(?:GetValue<[^>]+>|GetSection|GetRequiredSection|BindConfiguration)\(\s*""([^""]+)""")) keys.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(expression, @"\.GetConnectionString\(\s*""([^""]+)""")) keys.Add($"ConnectionStrings:{m.Groups[1].Value}");
        foreach (Match m in Regex.Matches(expression, @"IOptions(?:Monitor|Snapshot)?<\s*(\w+)\s*>|GetSection\(\s*(\w+)\.SectionName\s*\)|\b(\w+Options)\s*\.\s*SectionName"))
        {
            var type = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            if (SectionOf(input, project, type) is { } section)
            {
                // The options member read (options.BaseUrl), never the HttpClient property being assigned (client.BaseAddress).
                var member = Regex.Matches(expression, @"\b(\w+)\s*\.\s*(?:Value\s*\.\s*)?(BaseUrl|BaseAddress|Url|Uri|Endpoint|ConnectionString|FQDN|FullyQualifiedNamespace|Namespace)\b")
                    .FirstOrDefault(x => !Regex.IsMatch(x.Groups[1].Value, @"^(client|c|http|httpClient|builder|b)$", RegexOptions.IgnoreCase));
                keys.Add(member is not null ? $"{section}:{member.Groups[2].Value}" : section);
            }
        }

        return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The configuration section an options type is bound to, from its own SectionName constant or a Configure/Bind registration in the closure.</summary>
    protected static string? SectionOf(ArchitectureInput input, ArchProject project, string type)
    {
        foreach (var file in input.Projects.Where(p => !p.IsTest).SelectMany(p => p.Code))
        {
            var declaration = Regex.Match(file.Text, $@"\b(?:class|record)\s+{Regex.Escape(type)}\b");
            if (!declaration.Success) continue;
            var body = ArchitectureText.Statement(file.Text, declaration.Index);
            var constant = Regex.Match(file.Text[declaration.Index..Math.Min(file.Text.Length, declaration.Index + 3000)], @"const\s+string\s+Section(?:Name)?\s*=\s*""([^""]+)""");
            if (constant.Success) return constant.Groups[1].Value;
            _ = body;
        }
        // The binding often lives in the host project, outside the library that declares the options type: search every non-test project,
        // and accept the answer only when it is unambiguous.
        var sections = input.Projects.Where(p => !p.IsTest).SelectMany(p => p.Code)
            .SelectMany(file => Regex.Matches(file.Text, $@"(?:Configure|AddOptions|Bind)<\s*{Regex.Escape(type)}\s*>\s*\(?[^;]*?(?:GetSection|GetRequiredSection|BindConfiguration)\(\s*""([^""]+)""").Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _ = project;
        return sections.Count == 1 ? sections[0] : null;
    }

    /// <summary>A string literal, or a const string resolved from the snapshot (Type.Name or Name); null when the value is computed at runtime.</summary>
    protected static string? Literal(ArchitectureInput input, ArchProject project, string expression)
    {
        expression = expression.Trim();
        var literal = Regex.Match(expression, @"^@?""([^""]+)""$");
        if (literal.Success) return literal.Groups[1].Value;
        var member = Regex.Match(expression, @"^(?:(\w+)\.)?(\w+)$");
        if (!member.Success) return null;
        foreach (var file in input.Closure(project).SelectMany(p => p.Code))
        {
            var scope = member.Groups[1].Success ? Regex.Match(file.Text, $@"\b(?:class|record|struct)\s+{Regex.Escape(member.Groups[1].Value)}\b") : null;
            if (scope is { Success: false }) continue;
            var from = scope?.Index ?? 0;
            var c = Regex.Match(file.Text[from..], $@"const\s+string\s+{Regex.Escape(member.Groups[2].Value)}\s*=\s*""([^""]+)""");
            if (c.Success) return c.Groups[1].Value;
        }
        return null;
    }

    protected static IEnumerable<Match> Find(CodeFile file, string pattern) => Regex.Matches(file.Text, pattern).Cast<Match>();
}

/// <summary>.NET projects: SDK, output type, framework, project and package references. Project structure alone never implies runtime communication.</summary>
internal sealed class DotNetProjectArchitectureExtractor : IArchitectureExtractor
{
    public string Name => "DotNetProject";
    public string Version => "1";
    public bool CanAnalyze(ArchitectureInput input) => input.Projects.Count > 0;
    public ArchitectureExtractionResult Analyze(ArchitectureInput input, CancellationToken ct)
    {
        var facts = new List<ArchitectureFact>();
        foreach (var p in input.Projects.Where(p => !p.IsTest))
        {
            var evidence = new ArchitectureEvidence(ArchitectureEvidenceKind.ProjectFile, p.Path, 1, p.Name, Name,
                $"SDK {(p.Sdk.Length > 0 ? p.Sdk : "unknown")}{(p.OutputType is { } o ? $", OutputType {o}" : "")}, {p.Framework}");
            facts.Add(new("Project", p.Path, evidence, new Dictionary<string, string> { ["sdk"] = p.Sdk, ["output"] = p.OutputType ?? "", ["aspire"] = p.IsAspireHost ? "true" : "" }));
            foreach (var r in p.ProjectReferences)
                facts.Add(new("ProjectReference", p.Path, evidence with { Explanation = $"ProjectReference to {ArchitectureText.Safe(r)}" }, new Dictionary<string, string> { ["target"] = r }));
        }
        return new(Name, Version, facts, []);
    }
}

/// <summary>ASP.NET Core hosting: web app, controllers, minimal APIs, GraphQL server, health checks, hosted services, reverse proxy (YARP).</summary>
internal sealed class AspNetCoreArchitectureExtractor : CodeExtractor
{
    public override string Name => "AspNetCore";
    protected override void Scan(ArchitectureInput input, ArchProject project, CodeFile file, List<ArchitectureFact> facts, List<string> diagnostics)
    {
        foreach (var m in Find(file, @"WebApplication\.CreateBuilder|WebApplication\.CreateSlimBuilder"))
            facts.Add(Fact("WebHost", project, file, m.Index, "ASP.NET Core WebApplication builder"));
        foreach (var m in Find(file, @"Host\.CreateApplicationBuilder|Host\.CreateDefaultBuilder"))
            facts.Add(Fact("GenericHost", project, file, m.Index, ".NET generic host builder"));
        foreach (var m in Find(file, @"\.Map(Get|Post|Put|Delete|Patch)\(\s*""([^""]*)"""))
            facts.Add(Fact("RestEndpoint", project, file, m.Index, $"Minimal API {m.Groups[1].Value.ToUpperInvariant()} route", ("method", m.Groups[1].Value.ToUpperInvariant()), ("route", ArchitectureText.Safe(m.Groups[2].Value))));
        foreach (var m in Find(file, @"\[ApiController\]|\bMapControllers\(\)"))
            facts.Add(Fact("Controllers", project, file, m.Index, "MVC controllers"));
        foreach (var m in Find(file, @"\.AddGraphQLServer\(\)"))
            facts.Add(Fact("GraphQlServer", project, file, m.Index, "Hot Chocolate GraphQL server registration", ("framework", "Hot Chocolate")));
        foreach (var m in Find(file, @"\.MapGraphQL\(\s*(?:""([^""]*)"")?"))
            facts.Add(Fact("GraphQlEndpoint", project, file, m.Index, "GraphQL endpoint mapped", ("route", m.Groups[1].Success ? ArchitectureText.Safe(m.Groups[1].Value) : "/graphql")));
        foreach (var m in Find(file, @"\.MapHealthChecks\(\s*""([^""]*)"""))
            facts.Add(Fact("HealthChecks", project, file, m.Index, "Health check endpoint", ("route", ArchitectureText.Safe(m.Groups[1].Value))));
        foreach (var m in Find(file, @"\.AddHostedService<\s*(\w+)\s*>"))
            facts.Add(Fact("HostedService", project, file, m.Index, "Hosted service registration", ("type", m.Groups[1].Value)));
        foreach (var m in Find(file, @"class\s+(\w+)\s*(?:\([^)]*\))?\s*:\s*BackgroundService\b"))
            facts.Add(Fact("BackgroundService", project, file, m.Index, "BackgroundService implementation", ("type", m.Groups[1].Value)));
        foreach (var m in Find(file, @"\.AddReverseProxy\(\)"))
        {
            var statement = ArchitectureText.Statement(file.Text, m.Index);
            var section = Regex.Match(statement, @"LoadFromConfig\([^)]*GetSection\(\s*""([^""]+)""");
            facts.Add(Fact("ReverseProxy", project, file, m.Index, "YARP reverse proxy", ("section", section.Success ? section.Groups[1].Value : "ReverseProxy"), ("framework", "YARP")));
        }
        foreach (var m in Find(file, @"\bAddFunctionsWorkerDefaults\(|\[Function\(\s*""([^""]+)"""))
            facts.Add(Fact("Function", project, file, m.Index, "Azure Functions worker"));
        foreach (var m in Find(file, @"\[TimerTrigger\(|\bAddQuartz\(|\bCronExpression\b"))
            facts.Add(Fact("Scheduled", project, file, m.Index, "Scheduled job"));
    }
}

/// <summary>HTTP clients: typed/named AddHttpClient, BaseAddress expressions and the configuration keys they read. Targets are resolved later, from evidence only.</summary>
internal sealed class HttpClientArchitectureExtractor : CodeExtractor
{
    public override string Name => "HttpClient";
    protected override void Scan(ArchitectureInput input, ArchProject project, CodeFile file, List<ArchitectureFact> facts, List<string> diagnostics)
    {
        foreach (var m in Find(file, @"\.AddHttpClient(?:<\s*(\w+)\s*(?:,\s*(\w+)\s*)?>)?\(\s*(""[^""]*""|[\w.]+)?"))
        {
            var statement = ArchitectureText.Statement(file.Text, m.Index);
            var typed = m.Groups[1].Success ? m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value : null;
            var nameArg = m.Groups[3].Success ? m.Groups[3].Value : null;
            var literalName = nameArg is not null ? Literal(input, project, nameArg) : null;
            var name = typed ?? literalName ?? nameArg;
            var hasBase = statement.Contains("BaseAddress", StringComparison.Ordinal);
            // AddHttpClient() alone registers the factory; AddHttpClient(someVariable) without a base address only attaches handlers to a client defined elsewhere.
            if (!hasBase && (name is null || (typed is null && literalName is null))) continue;
            var refs = ConfigReferences(input, project, file, statement);
            var keys = refs.Select(r => r.Key).ToList();
            var path = refs.FirstOrDefault(r => r.Path is not null).Path;
            var hosts = Regex.Matches(statement, @"""(https?://[^""/]+)").Select(h => h.Groups[1].Value).ToList();
            var endpoint = ClientEndpoint(input, project, file, statement);
            facts.Add(Fact("HttpClient", project, file, m.Index, typed is not null ? "Typed HttpClient registration" : "Named HttpClient registration",
                ("client", ArchitectureText.Safe(name ?? "(unnamed)")), ("configKey", keys.FirstOrDefault(IsEndpointKey) ?? keys.FirstOrDefault()), ("literalHost", hosts.FirstOrDefault()),
                ("configuredEndpoint", endpoint.Literal), ("endpointConfigKey", endpoint.Key),
                ("kind", typed is not null ? "typed" : "named"), ("path", path is null ? null : ArchitectureText.Safe(path))));
        }
        foreach (var m in Find(file, @"\.AddDownstreamApi\(\s*(""[^""]+""|[\w.]+)\s*,([^;]*)"))
        {
            var name = Literal(input, project, m.Groups[1].Value) ?? m.Groups[1].Value;
            var keys = ConfigKeys(input, project, file, m.Groups[2].Value);
            facts.Add(Fact("HttpClient", project, file, m.Index, "Microsoft.Identity.Web downstream API (token-acquiring HTTP client)",
                ("client", ArchitectureText.Safe(name)), ("configKey", keys.FirstOrDefault() ?? name), ("kind", "downstream"), ("framework", "Microsoft.Identity.Web DownstreamApi")));
        }
    }

    internal static bool IsEndpointKey(string key) => Regex.IsMatch(key, @"(?i)(baseurl|baseaddress|url|uri|endpoint|address)$");
    // Additional projection facts only; existing architecture resolution keeps its original inputs.
    internal static (string? Literal, string? Key) ClientEndpoint(ArchitectureInput input, ArchProject project, CodeFile file, string statement)
    {
        var assignment = Regex.Match(statement, @"\bBaseAddress\s*=\s*([^;]+)", RegexOptions.Singleline);
        if (!assignment.Success) return (null, null);
        var refs = ConfigReferences(input, project, file, assignment.Groups[1].Value);
        var literal = Regex.Match(assignment.Groups[1].Value, @"\bnew\s*(?:(?:global::)?(?:System\.)?Uri\s*)?\(\s*""(https?://[^""]+)""");
        return (literal.Success ? literal.Groups[1].Value : null, refs.FirstOrDefault(r => IsEndpointKey(r.Key)).Key ?? refs.FirstOrDefault().Key);
    }
}

/// <summary>GraphQL: Hot Chocolate server registration is in AspNetCore; here the client side — Strawberry Shake Add{Name}Client().ConfigureHttpClient(...).</summary>
internal sealed class GraphQlArchitectureExtractor : CodeExtractor
{
    public override string Name => "GraphQL";
    public override bool CanAnalyze(ArchitectureInput input) => input.Projects.Any(p => p.HasPackage("StrawberryShake") || p.HasPackage("HotChocolate") || p.HasPackage("GraphQL."));
    protected override void Scan(ArchitectureInput input, ArchProject project, CodeFile file, List<ArchitectureFact> facts, List<string> diagnostics)
    {
        if (!project.HasPackage("StrawberryShake")) return;
        foreach (var m in Find(file, @"\.Add(\w+?)Client\(\s*\)"))
        {
            var statement = ArchitectureText.Statement(file.Text, m.Index);
            if (!statement.Contains("ConfigureHttpClient", StringComparison.Ordinal)) continue;
            var refs = ConfigReferences(input, project, file, statement);
            var path = refs.FirstOrDefault(r => r.Path is not null).Path;
            var endpoint = HttpClientArchitectureExtractor.ClientEndpoint(input, project, file, statement);
            facts.Add(Fact("GraphQlClient", project, file, m.Index, "Strawberry Shake generated GraphQL client with a configured HttpClient",
                ("client", ArchitectureText.Safe(m.Groups[1].Value)), ("configKey", refs.Count > 0 ? refs[0].Key : null), ("framework", "Strawberry Shake"),
                ("configuredEndpoint", endpoint.Literal), ("endpointConfigKey", endpoint.Key),
                ("path", path is null ? null : ArchitectureText.Safe(path))));
        }
    }
}

/// <summary>Event Hubs: producers, consumers (EventProcessorClient / EventHubConsumerClient) and their checkpoint store. Hub identity only from literals/constants.</summary>
internal sealed class EventHubArchitectureExtractor : CodeExtractor
{
    public override string Name => "EventHubs";
    public override bool CanAnalyze(ArchitectureInput input) => input.Projects.Any(p => p.HasPackage("Azure.Messaging.EventHubs"));
    protected override void Scan(ArchitectureInput input, ArchProject project, CodeFile file, List<ArchitectureFact> facts, List<string> diagnostics)
    {
        foreach (var m in Find(file, @"new\s+(EventHub(?:Buffered)?ProducerClient)\(([^;]*)"))
            facts.Add(Fact("EventHubProducer", project, file, m.Index, $"{m.Groups[1].Value} created", ("hub", HubArgument(input, project, m.Groups[2].Value, producer: true))));
        foreach (var m in Find(file, @"new\s+(EventProcessorClient|EventHubConsumerClient)\(([^;]*)"))
        {
            var args = m.Groups[2].Value;
            var checkpoint = m.Groups[1].Value == "EventProcessorClient";
            facts.Add(Fact("EventHubConsumer", project, file, m.Index, $"{m.Groups[1].Value} created{(checkpoint ? " (checkpointed in a blob container)" : "")}",
                ("hub", HubArgument(input, project, args, producer: false)), ("checkpoint", checkpoint ? "true" : null),
                ("consumerGroup", Regex.Match(args, @"""(\$Default|[\w.\-]+)""") is { Success: true } g && !g.Value.Contains('.') ? g.Groups[1].Value : null)));
        }
        foreach (var m in Find(file, @"\.Add(EventProcessorClient|EventHub(?:Producer)Client|EventHubConsumerClient)\(\s*(""[^""]*""|[\w.]+)?"))
            facts.Add(Fact(m.Groups[1].Value.Contains("Producer") ? "EventHubProducer" : "EventHubConsumer", project, file, m.Index, $"Azure client registration {m.Groups[1].Value}",
                ("hub", m.Groups[2].Success ? Literal(input, project, m.Groups[2].Value) : null), ("checkpoint", m.Groups[1].Value == "EventProcessorClient" ? "true" : null)));
    }

    /// <summary>A hub name only when an argument is a literal or resolvable const that looks like a hub (never a namespace or connection string).</summary>
    private static string? HubArgument(ArchitectureInput input, ArchProject project, string args, bool producer)
    {
        foreach (var arg in args.Split(',').Select(a => a.Trim().TrimEnd(')').Trim()))
            if (Literal(input, project, arg) is { } value && !value.Contains("servicebus.windows.net", StringComparison.OrdinalIgnoreCase) && !value.Contains('=') && value != "$Default"
                && Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._\-]{0,255}$"))
                return value;
        _ = producer;
        return null;
    }
}

/// <summary>Azure Service Bus: senders (topic or queue) and processors/receivers (queue, or topic + subscription). Entity names only from literals/constants.</summary>
internal sealed class ServiceBusArchitectureExtractor : CodeExtractor
{
    public override string Name => "ServiceBus";
    public override bool CanAnalyze(ArchitectureInput input) => input.Projects.Any(p => p.HasPackage("Azure.Messaging.ServiceBus"));
    protected override void Scan(ArchitectureInput input, ArchProject project, CodeFile file, List<ArchitectureFact> facts, List<string> diagnostics)
    {
        if (!file.Text.Contains("ServiceBus", StringComparison.Ordinal) && !file.Text.Contains("CreateSender", StringComparison.Ordinal)) return;
        foreach (var m in Find(file, @"\.CreateSender\(\s*([^)]*)\)"))
        {
            var expr = m.Groups[1].Value.Trim();
            facts.Add(Fact("ServiceBusSend", project, file, m.Index, "ServiceBusSender created", ("entity", Literal(input, project, expr)),
                ("expression", ArchitectureText.Safe(expr)), ("kind", KindHint(expr))));
        }
        foreach (var m in Find(file, @"\.Create(Processor|SessionProcessor|Receiver)\(\s*([^,)]+)(?:,\s*([^,)]+))?"))
        {
            var first = m.Groups[2].Value.Trim();
            var second = m.Groups[3].Success ? m.Groups[3].Value.Trim() : null;
            var secondIsOptions = second is not null && (second.StartsWith("new ", StringComparison.Ordinal) || second.EndsWith("options", StringComparison.OrdinalIgnoreCase) || second.EndsWith("Options", StringComparison.Ordinal));
            var subscription = second is not null && !secondIsOptions ? second : null;
            facts.Add(Fact("ServiceBusReceive", project, file, m.Index, $"Service Bus {m.Groups[1].Value.ToLowerInvariant()} created{(subscription is not null ? " for a topic subscription" : "")}",
                ("entity", Literal(input, project, first)), ("expression", ArchitectureText.Safe(first)),
                ("subscription", subscription is null ? null : Literal(input, project, subscription) ?? ArchitectureText.Safe(subscription)),
                ("kind", subscription is not null ? "topic" : KindHint(first))));
        }
    }

    private static string? KindHint(string expression) =>
        Regex.IsMatch(expression, @"(?i)topic") ? "topic" : Regex.IsMatch(expression, @"(?i)(queue|koe)") ? "queue" : null;
}

/// <summary>Wolverine over Azure Service Bus: transport/framework evidence on the edge, never a component of its own.</summary>
internal sealed class WolverineArchitectureExtractor : CodeExtractor
{
    public override string Name => "Wolverine";
    protected override void Scan(ArchitectureInput input, ArchProject project, CodeFile file, List<ArchitectureFact> facts, List<string> diagnostics)
    {
        if (!file.Text.Contains("Wolverine", StringComparison.Ordinal) && !file.Text.Contains("AzureServiceBus", StringComparison.Ordinal)) return;
        foreach (var m in Find(file, @"\.UseWolverine\("))
            facts.Add(Fact("Wolverine", project, file, m.Index, "Wolverine host"));
        foreach (var m in Find(file, @"\.To(AzureServiceBusTopic|AzureServiceBusQueue)\(\s*([^)]+)\)"))
            facts.Add(Fact("ServiceBusSend", project, file, m.Index, $"Wolverine publish rule to {m.Groups[1].Value}", ("entity", Literal(input, project, m.Groups[2].Value)),
                ("expression", ArchitectureText.Safe(m.Groups[2].Value)), ("kind", m.Groups[1].Value.EndsWith("Topic") ? "topic" : "queue"), ("framework", "Wolverine")));
        foreach (var m in Find(file, @"\.ListenToAzureServiceBusQueue\(\s*([^)]+)\)"))
            facts.Add(Fact("ServiceBusReceive", project, file, m.Index, "Wolverine listener on an Azure Service Bus queue", ("entity", Literal(input, project, m.Groups[1].Value)),
                ("expression", ArchitectureText.Safe(m.Groups[1].Value)), ("kind", "queue"), ("framework", "Wolverine")));
        foreach (var m in Find(file, @"\.ListenToAzureServiceBusSubscription\(\s*([^)]+)\)\s*\.FromTopic\(\s*([^)]+)\)"))
            facts.Add(Fact("ServiceBusReceive", project, file, m.Index, "Wolverine listener on an Azure Service Bus topic subscription", ("entity", Literal(input, project, m.Groups[2].Value)),
                ("expression", ArchitectureText.Safe(m.Groups[2].Value)), ("subscription", Literal(input, project, m.Groups[1].Value)), ("kind", "topic"), ("framework", "Wolverine")));
    }
}

/// <summary>Datastore references: EF Core DbContext + provider + connection name, ADO.NET connections, Redis. Tables are the Database feature's, not ours.</summary>
internal sealed class DataStoreReferenceExtractor : CodeExtractor
{
    public override string Name => "DataStores";
    protected override void Scan(ArchitectureInput input, ArchProject project, CodeFile file, List<ArchitectureFact> facts, List<string> diagnostics)
    {
        foreach (var m in Find(file, @"\.Add(?:DbContext|DbContextPool|DbContextFactory|PooledDbContextFactory)<\s*(\w+)\s*>"))
        {
            var statement = ArchitectureText.Statement(file.Text, m.Index);
            var provider = Regex.Match(statement, @"\.(UseSqlServer|UseNpgsql|UseSqlite|UseCosmos|UseInMemoryDatabase)\(");
            if (provider.Success && provider.Groups[1].Value == "UseInMemoryDatabase") continue;
            var keys = ConfigKeys(input, project, file, statement);
            facts.Add(Fact("DbContext", project, file, m.Index, $"EF Core DbContext registration{(provider.Success ? $" with {provider.Groups[1].Value}" : "")}",
                ("context", m.Groups[1].Value), ("provider", provider.Success ? provider.Groups[1].Value : null), ("connection", keys.FirstOrDefault(k => k.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)) ?? keys.FirstOrDefault())));
        }
        foreach (var m in Find(file, @"new\s+(SqlConnection|NpgsqlConnection)\(([^;]*)"))
            facts.Add(Fact("AdoConnection", project, file, m.Index, $"{m.Groups[1].Value} opened", ("provider", m.Groups[1].Value), ("connection", ConfigKeys(input, project, file, m.Groups[2].Value).FirstOrDefault())));
        foreach (var m in Find(file, @"ConnectionMultiplexer\.Connect(?:Async)?\(|\.AddStackExchangeRedisCache\("))
            facts.Add(Fact("Redis", project, file, m.Index, "Redis connection/cache registration", ("connection", ConfigKeys(input, project, file, ArchitectureText.Statement(file.Text, m.Index)).FirstOrDefault())));
    }
}

/// <summary>Blob storage: BlobServiceClient/BlobContainerClient; a container used as an Event Hub checkpoint store is told apart from application storage.</summary>
internal sealed class StorageArchitectureExtractor : CodeExtractor
{
    public override string Name => "Storage";
    public override bool CanAnalyze(ArchitectureInput input) => input.Projects.Any(p => p.HasPackage("Azure.Storage.Blobs") || p.HasPackage("Azure.Messaging.EventHubs.Processor"));
    protected override void Scan(ArchitectureInput input, ArchProject project, CodeFile file, List<ArchitectureFact> facts, List<string> diagnostics)
    {
        var checkpointFile = file.Text.Contains("new EventProcessorClient(", StringComparison.Ordinal) || file.Text.Contains("BlobCheckpointStore", StringComparison.Ordinal)
            || Regex.IsMatch(file.Text, @"(?i)checkpoint");
        foreach (var m in Find(file, @"new\s+(BlobServiceClient|BlobContainerClient|BlobClient)\(|\.AddBlobServiceClient\("))
        {
            var keys = ConfigKeys(input, project, file, ArchitectureText.Statement(file.Text, m.Index));
            facts.Add(Fact("Blob", project, file, m.Index, checkpointFile ? "Blob container used by an Event Hub processor (checkpoint store)" : "Blob storage client",
                ("usage", checkpointFile ? "checkpoint" : "application"), ("configKey", keys.FirstOrDefault())));
        }
    }
}

/// <summary>Authentication and secrets: Entra ID (Microsoft.Identity.Web, MSAL), JWT bearer, API-key handlers, Key Vault. Never a secret name or value.</summary>
internal sealed class AuthenticationArchitectureExtractor : CodeExtractor
{
    public override string Name => "Authentication";
    protected override void Scan(ArchitectureInput input, ArchProject project, CodeFile file, List<ArchitectureFact> facts, List<string> diagnostics)
    {
        foreach (var m in Find(file, @"\.(AddMicrosoftIdentityWebApi(?:Authentication)?|AddMicrosoftIdentityWebApp(?:Authentication)?)\(([^;]*)"))
            facts.Add(Fact("Auth", project, file, m.Index, $"{m.Groups[1].Value}: Microsoft Entra ID token {(m.Groups[1].Value.Contains("Api") ? "validation" : "sign-in")}",
                ("provider", "Microsoft Entra ID"), ("mode", m.Groups[1].Value.Contains("Api") ? "token validation" : "sign-in"),
                ("configKey", Regex.Match(m.Groups[2].Value, @"""([A-Za-z]\w*)""") is { Success: true } s ? s.Groups[1].Value : "AzureAd")));
        foreach (var m in Find(file, @"\.AddMsalAuthentication\("))
            facts.Add(Fact("Auth", project, file, m.Index, "MSAL sign-in (Microsoft Entra ID)", ("provider", "Microsoft Entra ID"), ("mode", "sign-in"), ("configKey", "AzureAd")));
        foreach (var m in Find(file, @"\.AddJwtBearer\(([^;]*)"))
        {
            var statement = ArchitectureText.Statement(file.Text, m.Index);
            var keys = ConfigKeys(input, project, file, statement);
            var entra = Regex.IsMatch(statement, @"login\.microsoftonline\.com|AzureAd|Entra", RegexOptions.IgnoreCase);
            facts.Add(Fact("Auth", project, file, m.Index, "JWT bearer token validation", ("provider", entra ? "Microsoft Entra ID" : "OIDC/JWT issuer"), ("mode", "token validation"),
                ("configKey", keys.FirstOrDefault(k => Regex.IsMatch(k, @"(?i)authority|issuer|instance|azuread")) ?? keys.FirstOrDefault())));
        }
        foreach (var m in Find(file, @"class\s+(\w*ApiKey\w*)\s*(?:\([^)]*\))?\s*:\s*AuthenticationHandler"))
            facts.Add(Fact("ApiKeyAuth", project, file, m.Index, "API key authentication handler", ("type", m.Groups[1].Value)));
        foreach (var m in Find(file, @"new\s+SecretClient\(|\.AddAzureKeyVault\(|\.AddSecretClient\("))
            facts.Add(Fact("KeyVault", project, file, m.Index, "Azure Key Vault secret/configuration client", ("configKey", ConfigKeys(input, project, file, ArchitectureText.Statement(file.Text, m.Index)).FirstOrDefault())));
        foreach (var m in Find(file, @"new\s+(DefaultAzureCredential|ManagedIdentityCredential|WorkloadIdentityCredential)\("))
            facts.Add(Fact("AzureIdentity", project, file, m.Index, $"{m.Groups[1].Value} (Azure identity, no secret in source)"));
    }
}

/// <summary>Observability exporters and well-known external APIs named explicitly in code (Microsoft Graph, Entra authority). Hostnames alone are not enough elsewhere.</summary>
internal sealed class ConfigurationDependencyExtractor : CodeExtractor
{
    public override string Name => "ExternalAndConfiguration";
    private static readonly (string Pattern, string Name, string Type)[] KnownApis =
    [
        (@"graph\.microsoft\.com|GraphServiceClient", "Microsoft Graph", "Microsoft cloud API"),
    ];
    protected override void Scan(ArchitectureInput input, ArchProject project, CodeFile file, List<ArchitectureFact> facts, List<string> diagnostics)
    {
        foreach (var m in Find(file, @"\.UseAzureMonitor\(|\.AddApplicationInsightsTelemetry(?:WorkerService)?\(|\.AddAzureMonitorTraceExporter\(|\.AddOtlpExporter\("))
            facts.Add(Fact("Observability", project, file, m.Index, "Telemetry exporter registration",
                ("provider", m.Value.Contains("Otlp") ? "OpenTelemetry collector (OTLP)" : "Azure Monitor / Application Insights")));
        foreach (var (pattern, name, type) in KnownApis)
            foreach (var m in Find(file, pattern).Take(1))
                facts.Add(Fact("ExternalApi", project, file, m.Index, $"{name} called from source", ("name", name), ("type", type)));
    }
}
