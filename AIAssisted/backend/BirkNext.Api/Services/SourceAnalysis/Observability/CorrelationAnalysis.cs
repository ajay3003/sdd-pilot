using System.Text.RegularExpressions;
using BirkNext.Api.Services.SourceArchitecture;
using BirkNext.SourceArchitecture;
using BirkNext.SourceObservability;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BirkNext.Api.Services.SourceAnalysis.Observability;

/// <summary>
/// Tracing registration, correlation mechanisms and per-boundary propagation (HTTP in/out, message produce/consume), with edges over the
/// Architecture model's HTTP dependencies and messaging channels. A boundary state says what source shows — framework instrumentation,
/// configuration or explicit code — never that context arrives at runtime. Distinct identities (trace context, correlation id, request id,
/// message id) stay distinct unless an assignment in source maps one onto another.
/// </summary>
internal static class CorrelationAnalysis
{
    internal sealed record Result(CorrelationSummary Summary, List<CorrelationMechanism> Mechanisms, List<CorrelationBoundary> Boundaries, List<CorrelationEdge> Edges,
        Dictionary<string, List<string>> TracingByComponent);

    private static Regex R(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Tracing registration and instrumentation (OpenTelemetry .NET, Application Insights SDK, Azure Monitor distro).
    private static readonly Regex OpenTelemetry = R(@"\bAddOpenTelemetry\s*\(|\.WithTracing\s*\(|\bCreateTracerProviderBuilder\s*\(");
    private static readonly Regex AzureMonitorDistro = R(@"\bUseAzureMonitor\s*\(");
    private static readonly Regex AppInsightsSdk = R(@"\bAddApplicationInsightsTelemetry(WorkerService)?\s*\(");
    private static readonly Regex AspNetCoreInstrumentation = R(@"\bAddAspNetCoreInstrumentation\s*\(");
    private static readonly Regex HttpClientInstrumentation = R(@"\bAddHttpClientInstrumentation\s*\(");
    private static readonly Regex ActivitySourceNew = R(@"\bnew\s+ActivitySource\s*\(");
    private static readonly Regex StartActivity = R(@"\.StartActivity\s*\(");
    private static readonly Regex AddSource = R(@"\bAddSource\s*\(\s*""([^""]{1,80})""");
    private static readonly Regex OtherInstrumentation = R(@"\bAdd(EntityFrameworkCore|SqlClient|GrpcClient|Redis|Npgsql|Kafka(Producer|Consumer))Instrumentation\s*\(");
    private static readonly Regex ServiceDefaults = R(@"\bAddServiceDefaults\s*\(");

    // Explicit propagation and correlation identifiers.
    private static readonly Regex Propagator = R(@"\b(Propagators\.DefaultTextMapPropagator|DistributedContextPropagator|TextMapPropagator|Sdk\.SetDefaultTextMapPropagator)\b");
    private static readonly Regex Inject = R(@"\.Inject\s*\(");
    private static readonly Regex Extract = R(@"\.Extract\s*\(|ActivityContext\.(Try)?Parse\s*\(|\.SetParentId\s*\(");
    private static readonly Regex HeaderLiteral = R(@"""((?:[Xx]-)?(?:[A-Za-z][A-Za-z\-]{0,40})?(?:[Cc]orrelation|[Rr]equest|[Tt]race|[Oo]peration)[A-Za-z\-]{0,20}(?:[Ii][Dd]|[Ii]d)?|traceparent|tracestate|Diagnostic-Id|Request-Id)""");
    private static readonly Regex HeaderUse = R(@"\b(Headers|DefaultRequestHeaders|ApplicationProperties|Properties)\b");
    private static readonly Regex HeaderPropagation = R(@"\b(AddHeaderPropagation|UseHeaderPropagation|AddCorrelationId|UseCorrelationId)\s*\(");
    private static readonly Regex TraceIdentifier = R(@"\.TraceIdentifier\b");
    private static readonly Regex ActivityCurrent = R(@"\bActivity\.Current\b");
    private static readonly Regex DelegatingHandler = R(@":\s*DelegatingHandler\b");
    private static readonly Regex Middleware = R(@"\bInvokeAsync\s*\(\s*HttpContext\b|:\s*IMiddleware\b");

    private static readonly string[] Identities = ["CorrelationId", "TraceId", "MessageId", "RequestId", "OperationId", "ConversationId", "TraceIdentifier", "SpanId", "ParentId"];

    /// <summary>Message transports: one adapter per family, recognised by package and API. Propagation defaults are framework facts, not runtime ones.</summary>
    private sealed record Transport(string Name, string Package, Regex Produce, Regex Consume, Regex? FrameworkSource, string? DefaultMechanism, bool InferredByDefault, MessagingChannelType[] Channels);

    private static readonly Transport[] Transports =
    [
        new("Azure Service Bus", "Azure.Messaging.ServiceBus", R(@"\b(SendMessageAsync|SendMessagesAsync|ScheduleMessageAsync)\s*\(|\bnew\s+ServiceBusMessage\s*\("),
            R(@"\bProcessMessageAsync\s*\+=|\bReceiveMessages?Async\s*\(|\bServiceBusProcessor\b|\bProcessSessionMessageAsync\s*\+="),
            R(@"AddSource\s*\(\s*""Azure\.(\*|Messaging\.ServiceBus)"), "Diagnostic-Id application property set by the Azure SDK while tracing is active", true,
            [MessagingChannelType.ServiceBusTopic, MessagingChannelType.ServiceBusQueue, MessagingChannelType.ServiceBusEntity]),
        new("Azure Event Hubs", "Azure.Messaging.EventHubs", R(@"\bEventHubProducerClient\b|\bnew\s+EventData\s*\(|\bEventDataBatch\b"),
            R(@"\bProcessEventAsync\s*\+=|\bEventProcessorClient\b|\bReadEventsAsync\s*\(|:\s*EventProcessor<|\bEventHubConsumerClient\b"),
            R(@"AddSource\s*\(\s*""Azure\.(\*|Messaging\.EventHubs)"), "Diagnostic-Id event property set by the Azure SDK while tracing is active", true, [MessagingChannelType.EventHub]),
        new("Wolverine", "WolverineFx", R(@"\b(IMessageBus|IMessageContext)\b[\s\S]{0,400}?\.(PublishAsync|SendAsync|InvokeAsync)\s*\(|\bbus\.(PublishAsync|SendAsync)\s*\("),
            R(@"\bclass\s+\w+Handler\b[\s\S]{0,2000}?\b(Handle|HandleAsync|Consume|ConsumeAsync)\s*\("),
            R(@"AddSource\s*\(\s*""Wolverine"), "Wolverine envelope correlation (CorrelationId / ConversationId) carried by the framework", true, []),
        new("MassTransit", "MassTransit", R(@"\b(IPublishEndpoint|ISendEndpoint|IBus)\b[\s\S]{0,400}?\.(Publish|Send)\s*[<(]"), R(@":\s*IConsumer<"),
            R(@"AddSource\s*\(\s*""MassTransit"), "MassTransit envelope correlation and diagnostic headers carried by the framework", true, []),
        new("Kafka", "Confluent.Kafka", R(@"\.ProduceAsync\s*\(|\.Produce\s*\(\s*\w"), R(@"\bIConsumer<[^>]+>\s*\w+[\s\S]{0,400}?\.Consume\s*\(|\.Consume\s*\(\s*(cancellationToken|ct|stoppingToken|TimeSpan)"),
            R(@"AddKafka(Producer|Consumer)Instrumentation\s*\("), null, false, [MessagingChannelType.KafkaTopic]),
        new("RabbitMQ", "RabbitMQ.Client", R(@"\.BasicPublish(Async)?\s*\("), R(@"\.BasicConsume(Async)?\s*\(|\bAsyncEventingBasicConsumer\b|\bEventingBasicConsumer\b"),
            R(@"AddSource\s*\(\s*""RabbitMQ"), null, false, [MessagingChannelType.RabbitQueue]),
    ];

    private const string ContextKey = @"""(?i:[^""]{0,30}(correl|trace|diagnostic|operation|conversation)[^""]{0,30})""";
    private static readonly Regex ExplicitProduce = R(@"\bCorrelationId\s*=(?!=)|\b(ApplicationProperties|Properties)\s*(\[|\.Add\s*\(|\.TryAdd\s*\()\s*" + ContextKey + @"|\bHeaders\.Add\s*\(\s*" + ContextKey + @"|\.Inject\s*\(");
    private static readonly Regex ExplicitConsume = R(@"\.CorrelationId\b(?!\s*=[^=])|\b(ApplicationProperties|Properties)\s*(\.TryGetValue\s*\(|\[)\s*" + ContextKey + @"|\bHeaders\.(TryGetLastBytes|GetLastBytes|TryGetValue)\s*\(\s*" + ContextKey + @"|\.Extract\s*\(|ActivityContext\.(Try)?Parse\s*\(");
    private static readonly Regex PositionalMetadata = R(@"\.(PartitionId|Offset|SequenceNumber|PartitionKey)\b");

    public static Result Run(ObservabilitySourceAnalyzer.Analysis a, CancellationToken ct)
    {
        var sink = a.Sink;
        var tracing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var boundaries = new List<CorrelationBoundary>();
        var mechanisms = new Dictionary<string, CorrelationMechanism>(StringComparer.Ordinal);
        var headersByComponent = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        void Mechanism(CorrelationIdentityType identity, string name, string technology, ObsComponent c, ArchitectureEvidenceState state, ObservabilityEvidence? evidence)
        {
            var key = $"{identity}:{name.ToLowerInvariant()}";
            if (!mechanisms.TryGetValue(key, out var m))
                mechanisms[key] = m = new CorrelationMechanism { Id = $"mechanism:{ObsText.Slug(key)}", Identity = identity, Name = name, Technology = technology, EvidenceState = state };
            if (!m.Components.Contains(c.Id)) m.Components.Add(c.Id);
            if (evidence is not null && m.Evidence.Count < a.Options.MaxEvidencePerFinding && !m.Evidence.Any(e => e.File == evidence.File && e.Line == evidence.Line)) m.Evidence.Add(evidence);
            if (state < m.EvidenceState) mechanisms[key] = m with { EvidenceState = state };
        }

        foreach (var c in a.Deployables)
        {
            ct.ThrowIfCancellationRequested();
            var techs = new List<string>();
            var otel = c.Find(OpenTelemetry, "OpenTelemetry tracing registration");
            var distro = c.Find(AzureMonitorDistro, "Azure Monitor OpenTelemetry distro (UseAzureMonitor)");
            var aiSdk = c.Find(AppInsightsSdk, "Application Insights SDK auto-collection");
            var aspnet = c.Find(AspNetCoreInstrumentation, "OpenTelemetry ASP.NET Core instrumentation");
            var http = c.Find(HttpClientInstrumentation, "OpenTelemetry HttpClient instrumentation");
            var source = c.Find(ActivitySourceNew, "Custom ActivitySource");
            var spans = c.Find(StartActivity, "Custom spans (StartActivity)");
            if (otel is not null) techs.Add("OpenTelemetry");
            if (distro is not null) techs.Add("Azure Monitor distro");
            if (aiSdk is not null) techs.Add("Application Insights SDK");
            if (aspnet is not null) techs.Add("ASP.NET Core instrumentation");
            if (http is not null) techs.Add("HttpClient instrumentation");
            if (source is not null || spans is not null) techs.Add("ActivitySource");
            foreach (var f in c.All) foreach (Match m in OtherInstrumentation.Matches(f.Text)) if (!techs.Contains(m.Groups[1].Value + " instrumentation")) techs.Add(m.Groups[1].Value + " instrumentation");
            var sources = c.All.SelectMany(f => AddSource.Matches(f.Text).Select(m => ObsText.Label(m.Groups[1].Value))).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            if (techs.Count > 0) tracing[c.Id] = techs;

            var framework = otel ?? distro ?? aiSdk;
            if (framework is { } fw) Mechanism(CorrelationIdentityType.W3CTraceContext, "W3C trace context (framework)", techs[0], c, fw.State, fw.Evidence);
            if ((source ?? spans) is { } custom) Mechanism(CorrelationIdentityType.ActivityTrace, "System.Diagnostics.Activity (custom spans)", "ActivitySource", c, custom.State, custom.Evidence);
            if (c.Find(Propagator, "Explicit propagator") is { } prop) Mechanism(CorrelationIdentityType.W3CTraceContext, "Explicit text-map propagator", "OpenTelemetry", c, prop.State, prop.Evidence);

            if (otel is not null && distro is null && aiSdk is null && !c.All.Any(f => Regex.IsMatch(f.Text, @"\bAdd(Otlp|AzureMonitorTrace|Jaeger|Zipkin|Console)Exporter\s*\(|\bUseOtlpExporter\s*\(")))
                sink.Add(ObservabilityCategory.Tracing, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, otel.Value.State, c.Id, "OpenTelemetry",
                    "Tracing registered without a trace exporter in source", "OpenTelemetry tracing is registered but no exporter call was found; spans may stay in-process. An exporter may also come from environment configuration.",
                    otel.Value.Evidence, "Exporters configured only through OTEL_* environment variables are not visible in source.");
            if (source is not null && framework is not null && sources.Count == 0)
                sink.Add(ObservabilityCategory.Tracing, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, source.Value.State, c.Id, "ActivitySource",
                    "Custom ActivitySource without AddSource registration", "A custom ActivitySource exists but no AddSource(\"…\") registration was found, so its spans may not be collected.", source.Value.Evidence);
            if (techs.Count > 0)
                sink.Add(ObservabilityCategory.Tracing, ObservabilityFindingKind.Observation, ObservabilitySeverity.Info, (otel ?? distro ?? aiSdk ?? source ?? spans ?? aspnet ?? http)!.Value.State, c.Id,
                    string.Join(", ", techs.Take(3)), "Tracing registration", $"{string.Join(", ", techs)}{(sources.Count > 0 ? $"; sources {string.Join(", ", sources.Take(6))}" : "")}. Configured ≠ telemetry flowing.",
                    (otel ?? distro ?? aiSdk ?? source ?? spans ?? aspnet ?? http)!.Value.Evidence);

            // Correlation headers and identity mappings in code.
            var headers = headersByComponent[c.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in c.All)
                foreach (Match m in HeaderLiteral.Matches(f.Text))
                {
                    var name = ObsText.Label(m.Groups[1].Value);
                    if (name is null || !HeaderUse.IsMatch(f.Text)) continue;
                    var identity = Identity(name);
                    if (identity is null) continue;
                    if (identity is CorrelationIdentityType.CorrelationId or CorrelationIdentityType.RequestId or CorrelationIdentityType.OperationId) headers.Add(name);
                    var state = c.Own.Contains(f) ? ArchitectureEvidenceState.Confirmed : ArchitectureEvidenceState.StronglySupported;
                    Mechanism(identity.Value, identity == CorrelationIdentityType.W3CTraceContext ? $"Explicit {name} header" : $"Header {name}", "HTTP / message header", c, state, ObsText.Evidence(f, m.Index, $"Header name {name}"));
                }
            if (c.Find(TraceIdentifier, "HttpContext.TraceIdentifier") is { } tid)
                Mechanism(CorrelationIdentityType.RequestId, "HttpContext.TraceIdentifier (request id)", "ASP.NET Core", c, tid.State, tid.Evidence);
            Mappings(a, c, Mechanism);
        }

        // Boundaries.
        var arch = a.Architecture;
        foreach (var c in a.Deployables)
        {
            ct.ThrowIfCancellationRequested();
            var t = tracing.GetValueOrDefault(c.Id) ?? [];
            var framework = t.Contains("OpenTelemetry") || t.Contains("Azure Monitor distro") || t.Contains("Application Insights SDK");
            var inboundFramework = t.Contains("ASP.NET Core instrumentation") || t.Contains("Azure Monitor distro") || t.Contains("Application Insights SDK");
            var outboundFramework = t.Contains("HttpClient instrumentation") || t.Contains("Azure Monitor distro") || t.Contains("Application Insights SDK");
            var headers = headersByComponent.GetValueOrDefault(c.Id) ?? [];
            var custom = headers.Count > 0 ? string.Join(", ", headers.Order(StringComparer.OrdinalIgnoreCase)) : null;

            // HTTP inbound: one per component that exposes HTTP.
            var inbound = arch?.Interfaces.Where(i => i.ComponentId == c.Id && i.Direction == "Inbound" && (i.Protocol.StartsWith("HTTP", StringComparison.Ordinal) || i.Protocol.StartsWith("GraphQL", StringComparison.Ordinal))).ToList() ?? [];
            var isWeb = c.Project.Sdk.EndsWith(".Web", StringComparison.OrdinalIgnoreCase) && c.Type != ArchitectureComponentType.Frontend;
            if (inbound.Count > 0 || (arch is null && isWeb))
            {
                var mw = c.All.FirstOrDefault(f => Middleware.IsMatch(f.Text) && HeaderLiteral.IsMatch(f.Text));
                var hp = c.Find(HeaderPropagation, "Header propagation / correlation middleware registration");
                var ev = new List<ObservabilityEvidence>();
                if (mw is not null) ev.Add(ObsText.Evidence(mw, Middleware.Match(mw.Text).Index, "Middleware reading a correlation header"));
                if (hp is not null) ev.Add(hp.Value.Evidence);
                var (state, evidenceState, mechanism, limitation) = mw is not null ? (PropagationState.PropagationExplicit, c.Own.Contains(mw) ? ArchitectureEvidenceState.Confirmed : ArchitectureEvidenceState.StronglySupported, $"Middleware reads {custom ?? "a correlation header"}", (string?)null)
                    : hp is not null ? (PropagationState.PropagationConfigured, hp.Value.State, "Header propagation / correlation middleware registered", null)
                    : inboundFramework ? (PropagationState.FrameworkInstrumentation, ArchitectureEvidenceState.StronglySupported, "W3C traceparent extracted by ASP.NET Core instrumentation", null)
                    : (PropagationState.PropagationInferred, ArchitectureEvidenceState.Inferred, "ASP.NET Core default Activity handling",
                        "ASP.NET Core reads an incoming W3C traceparent into Activity by default, but no tracing instrumentation or exporter was found, so the context may not be recorded.");
                if (mw is not null && inboundFramework) mechanism += "; W3C trace context via instrumentation";
                boundaries.Add(new CorrelationBoundary
                {
                    Id = $"{c.Id}|http-in", Component = c.Id, Type = CorrelationBoundaryType.HttpInbound, Peer = null, Transport = "HTTP",
                    Mechanism = mechanism, Context = Context(inboundFramework || state == PropagationState.PropagationInferred, headers), Propagation = state, EvidenceState = evidenceState,
                    Evidence = ev.Concat(inbound.SelectMany(i => i.Evidence).Take(2).Select(e => new ObservabilityEvidence(e.File, e.Line, e.Symbol, $"HTTP interface: {i_(e)}", c.Id))).Take(4).ToList(),
                    Limitation = limitation,
                });
            }

            // HTTP outbound: one per resolved target (or one unresolved when only HttpClient usage is visible).
            var outbound = arch?.Dependencies.Where(d => d.FromComponentId == c.Id && d.DependencyType is ArchitectureDependencyType.Http or ArchitectureDependencyType.GraphQl).ToList() ?? [];
            var explicitOut = c.All.FirstOrDefault(f => (DelegatingHandler.IsMatch(f.Text) || Regex.IsMatch(f.Text, @"\b(DefaultRequestHeaders|Headers)\.(Add|TryAddWithoutValidation)\s*\(")) && HeaderLiteral.IsMatch(f.Text));
            var configuredOut = c.Find(R(@"\bAddHeaderPropagation\s*\(|\.AddHeaderPropagation\s*\("), "HttpClient header propagation registration");
            var targets = outbound.Count > 0 ? outbound.GroupBy(d => d.ToId ?? d.TargetReference ?? "unresolved").Select(g => (Peer: g.Key == "unresolved" ? null : g.Key, Evidence: g.SelectMany(d => d.Evidence).Take(2).ToList())).ToList()
                : arch is null && c.Find(R(@"\bAddHttpClient\s*[<(]|\bnew\s+HttpClient\s*\("), "HttpClient usage") is { } hc ? [(Peer: (string?)null, Evidence: new List<ArchitectureEvidence>())] : [];
            foreach (var (peer, depEvidence) in targets)
            {
                var (state, evidenceState, mechanism, limitation) = explicitOut is not null
                        ? (PropagationState.PropagationExplicit, c.Own.Contains(explicitOut) ? ArchitectureEvidenceState.Confirmed : ArchitectureEvidenceState.StronglySupported, $"Outgoing {custom ?? "correlation"} header set in code", (string?)null)
                    : configuredOut is not null ? (PropagationState.PropagationConfigured, configuredOut.Value.State, "HttpClient header propagation registered", null)
                    : outboundFramework ? (PropagationState.FrameworkInstrumentation, ArchitectureEvidenceState.StronglySupported, "W3C traceparent injected by HttpClient instrumentation", null)
                    : c.Type == ArchitectureComponentType.Frontend ? (PropagationState.Unresolved, ArchitectureEvidenceState.Unresolved, "Browser HttpClient (fetch)",
                        "HttpClient in the browser runs on fetch and does not inject traceparent by default; no explicit header was found.")
                    : (PropagationState.PropagationInferred, ArchitectureEvidenceState.Inferred, ".NET HttpClient default traceparent injection",
                        ".NET HttpClient injects traceparent when an Activity is current, but no tracing instrumentation was found for this component.");
                var ev = new List<ObservabilityEvidence>();
                if (explicitOut is not null) ev.Add(ObsText.Evidence(explicitOut, HeaderLiteral.Match(explicitOut.Text).Index, "Outgoing correlation header"));
                ev.AddRange(depEvidence.Select(e => new ObservabilityEvidence(e.File, e.Line, e.Symbol, "HTTP dependency", c.Id)));
                boundaries.Add(new CorrelationBoundary
                {
                    Id = $"{c.Id}|http-out|{peer ?? "unresolved"}", Component = c.Id, Type = CorrelationBoundaryType.HttpOutbound, Peer = peer is null ? null : PeerName(arch, peer),
                    Transport = "HTTP", Mechanism = mechanism, Context = Context(outboundFramework || state == PropagationState.PropagationInferred, explicitOut is null ? [] : headers),
                    Propagation = state, EvidenceState = evidenceState, Evidence = ev.Take(4).ToList(),
                    Limitation = peer is null ? limitation is null ? "Target not resolved from source." : limitation + " Target not resolved from source." : limitation,
                });
            }

            // Messaging: one boundary per (transport, role, channel).
            foreach (var transport in Transports)
            {
                if (!c.HasPackage(transport.Package) && !c.All.Any(f => f.Text.Contains(transport.Package, StringComparison.Ordinal))) continue;
                foreach (var role in new[] { ChannelRole.Producer, ChannelRole.Consumer })
                {
                    var pattern = role == ChannelRole.Producer ? transport.Produce : transport.Consume;
                    var files = c.All.Where(f => pattern.IsMatch(f.Text)).ToList();
                    var channels = arch?.MessagingChannels.Where(ch => transport.Channels.Contains(ch.Type) && (role == ChannelRole.Producer ? ch.Producers : ch.Consumers).Any(e => e.ComponentId == c.Id)).ToList() ?? [];
                    if (transport.Name == "Wolverine")
                        channels = arch?.MessagingChannels.Where(ch => (role == ChannelRole.Producer ? ch.Producers : ch.Consumers).Any(e => e.ComponentId == c.Id && e.Framework == "Wolverine")).ToList() ?? [];
                    if (files.Count == 0 && channels.Count == 0) continue;
                    var explicitPattern = role == ChannelRole.Producer ? ExplicitProduce : ExplicitConsume;
                    var explicitFile = files.FirstOrDefault(f => explicitPattern.IsMatch(f.Text));
                    var configured = transport.FrameworkSource is not null && c.All.Any(f => transport.FrameworkSource.IsMatch(f.Text));
                    var azure = transport.Name.StartsWith("Azure", StringComparison.Ordinal) && (t.Contains("Azure Monitor distro") || t.Contains("Application Insights SDK"));
                    var identities = new List<CorrelationIdentityType>();
                    if (explicitFile is not null)
                    {
                        if (Regex.IsMatch(explicitFile.Text, @"\bCorrelationId\b")) identities.Add(CorrelationIdentityType.CorrelationId);
                        if (Regex.IsMatch(explicitFile.Text, @"\bMessageId\s*=")) identities.Add(CorrelationIdentityType.MessageId);
                        if (Regex.IsMatch(explicitFile.Text, @"""(traceparent|Diagnostic-Id)""|\.Inject\s*\(|\.Extract\s*\(|ActivityContext")) identities.Add(CorrelationIdentityType.W3CTraceContext);
                        if (identities.Count == 0) identities.Add(CorrelationIdentityType.CustomContext);
                    }
                    if ((configured || azure || transport.InferredByDefault) && !identities.Contains(CorrelationIdentityType.W3CTraceContext) && transport.DefaultMechanism is not null)
                        identities.Add(transport.Name is "Wolverine" or "MassTransit" ? CorrelationIdentityType.CorrelationId : CorrelationIdentityType.W3CTraceContext);
                    var (state, evidenceState, mechanism, limitation) = explicitFile is not null
                            ? (PropagationState.PropagationExplicit, c.Own.Contains(explicitFile) ? ArchitectureEvidenceState.Confirmed : ArchitectureEvidenceState.StronglySupported,
                                role == ChannelRole.Producer ? "Correlation set on the outgoing message in code" : "Correlation read from the incoming message in code", (string?)null)
                        : configured || azure ? (PropagationState.FrameworkInstrumentation, ArchitectureEvidenceState.StronglySupported, transport.DefaultMechanism ?? $"{transport.Name} instrumentation registered", null)
                        : transport.InferredByDefault ? (PropagationState.PropagationInferred, ArchitectureEvidenceState.Inferred, transport.DefaultMechanism!,
                            $"{transport.Name} carries context by framework design, but no tracing source for it was registered in this component.")
                        : (PropagationState.Unresolved, ArchitectureEvidenceState.Unresolved, "No propagation found",
                            $"{transport.Name} does not propagate trace or correlation context automatically and no explicit header handling was found.");
                    var peers = channels.Count > 0 ? channels.Select(ch => (string?)ch.Name).ToList() : [null];
                    foreach (var peer in peers)
                    {
                        var ev = new List<ObservabilityEvidence>();
                        if (explicitFile is not null) ev.Add(ObsText.Evidence(explicitFile, explicitPattern.Match(explicitFile.Text).Index, role == ChannelRole.Producer ? "Correlation set on message" : "Correlation read from message"));
                        if (files.FirstOrDefault() is { } first) ev.Add(ObsText.Evidence(first, pattern.Match(first.Text).Index, $"{transport.Name} {(role == ChannelRole.Producer ? "produce" : "consume")}"));
                        boundaries.Add(new CorrelationBoundary
                        {
                            Id = $"{c.Id}|{(role == ChannelRole.Producer ? "produce" : "consume")}|{ObsText.Slug(transport.Name)}|{peer ?? "unresolved"}", Component = c.Id,
                            Type = role == ChannelRole.Producer ? CorrelationBoundaryType.MessageProduce : CorrelationBoundaryType.MessageConsume, Peer = peer is null ? null : ArchitectureText.Safe(peer),
                            Transport = transport.Name, Mechanism = mechanism, Context = identities.Distinct().ToList(), Propagation = state, EvidenceState = evidenceState,
                            Evidence = ev.DistinctBy(e => (e.File, e.Line)).ToList(), Limitation = limitation,
                        });
                    }
                    if (transport.Name == "Azure Event Hubs" && role == ChannelRole.Consumer && files.FirstOrDefault(f => PositionalMetadata.IsMatch(f.Text)) is { } positional)
                        sink.Add(ObservabilityCategory.Correlation, ObservabilityFindingKind.Observation, ObservabilitySeverity.Info, ArchitectureEvidenceState.Confirmed, c.Id, transport.Name,
                            "Event Hub partition/offset metadata is positional, not correlation",
                            "Partition, offset and sequence number locate an event in the hub; they do not correlate it with the producer's operation.",
                            ObsText.Evidence(positional, PositionalMetadata.Match(positional.Text).Index, "Event position metadata"));
                }
            }
        }

        // Gap findings.
        foreach (var b in boundaries.Where(b => b.Propagation == PropagationState.Unresolved))
            sink.Add(ObservabilityCategory.Correlation, ObservabilityFindingKind.Unresolved, ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Unresolved, b.Component, b.Transport,
                $"No correlation propagation found at {ObservabilitySnapshot.Label(b.Type)} ({b.Transport})", b.Limitation ?? "No propagation mechanism was found for this boundary.", b.Evidence.FirstOrDefault(), b.Limitation);
        foreach (var b in boundaries.Where(b => b.Propagation == PropagationState.PropagationInferred))
            sink.Add(ObservabilityCategory.Correlation, ObservabilityFindingKind.Finding, ObservabilitySeverity.Info, ArchitectureEvidenceState.Inferred, b.Component, b.Transport,
                $"Propagation only inferred at {ObservabilitySnapshot.Label(b.Type)} ({b.Transport})", b.Limitation ?? "Framework default behaviour; not configured explicitly.", b.Evidence.FirstOrDefault(), b.Limitation);

        // Multiple mechanisms per component (custom correlation id alongside W3C trace context without a mapping).
        var mechanismList = mechanisms.Values.OrderBy(m => m.Identity).ThenBy(m => m.Name, StringComparer.Ordinal).ToList();
        foreach (var c in a.Deployables)
        {
            var own = mechanismList.Where(m => m.Components.Contains(c.Id)).ToList();
            var hasW3C = own.Any(m => m.Identity is CorrelationIdentityType.W3CTraceContext or CorrelationIdentityType.ActivityTrace);
            var customIds = own.Where(m => m.Identity is CorrelationIdentityType.CorrelationId or CorrelationIdentityType.CustomContext).ToList();
            var mapped = own.Any(m => m.Name.StartsWith("Mapping:", StringComparison.Ordinal) && (m.Name.Contains("TraceId", StringComparison.Ordinal) || m.Name.Contains("Activity", StringComparison.Ordinal)));
            if (hasW3C && customIds.Count > 0 && !mapped)
                sink.Add(ObservabilityCategory.Correlation, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.StronglySupported, c.Id, "Correlation",
                    "Multiple correlation mechanisms without a mapping",
                    $"W3C trace context and {string.Join(", ", customIds.Select(m => m.Name).Take(3))} are both present, and no source assignment maps one onto the other. They may identify different things in logs and traces.",
                    customIds[0].Evidence.FirstOrDefault());
        }
        var distinctHeaders = headersByComponent.Values.SelectMany(h => h).Where(h => h.Contains("orrelation", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var conflicts = 0;
        if (distinctHeaders.Count > 1)
        {
            conflicts = distinctHeaders.Count;
            sink.Add(ObservabilityCategory.Correlation, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Conflict, null, "HTTP / message header",
                "Different correlation header names in use", $"{string.Join(", ", distinctHeaders.Order(StringComparer.OrdinalIgnoreCase))} are used across components; a receiver reading one name will not see the other.",
                mechanismList.Where(m => m.Name.StartsWith("Header ", StringComparison.Ordinal)).SelectMany(m => m.Evidence).FirstOrDefault());
        }

        // Edges over Architecture: producer → channel → consumer, and component → component HTTP.
        var edges = new List<CorrelationEdge>();
        if (arch is not null)
        {
            foreach (var ch in arch.MessagingChannels)
                foreach (var p in ch.Producers.Select(e => e.ComponentId).Distinct())
                    foreach (var q in ch.Consumers.Select(e => e.ComponentId).Distinct())
                    {
                        var send = boundaries.FirstOrDefault(b => b.Component == p && b.Type == CorrelationBoundaryType.MessageProduce && b.Peer == ArchitectureText.Safe(ch.Name));
                        var recv = boundaries.FirstOrDefault(b => b.Component == q && b.Type == CorrelationBoundaryType.MessageConsume && b.Peer == ArchitectureText.Safe(ch.Name));
                        edges.Add(Edge(Name(arch, p), Name(arch, q), ArchitectureText.Safe(ch.Name), send, recv, headersByComponent.GetValueOrDefault(p), headersByComponent.GetValueOrDefault(q)));
                    }
            foreach (var d in arch.Dependencies.Where(d => d.DependencyType is ArchitectureDependencyType.Http or ArchitectureDependencyType.GraphQl && d.ToId is not null && arch.Components.Any(x => x.Id == d.ToId)))
            {
                var send = boundaries.FirstOrDefault(b => b.Component == d.FromComponentId && b.Type == CorrelationBoundaryType.HttpOutbound && b.Id.EndsWith("|" + d.ToId, StringComparison.Ordinal));
                var recv = boundaries.FirstOrDefault(b => b.Component == d.ToId && b.Type == CorrelationBoundaryType.HttpInbound);
                edges.Add(Edge(Name(arch, d.FromComponentId), Name(arch, d.ToId!), "HTTP", send, recv, headersByComponent.GetValueOrDefault(d.FromComponentId), headersByComponent.GetValueOrDefault(d.ToId!)));
            }
            edges = edges.DistinctBy(e => (e.From, e.To, e.Via)).ToList();
        }
        foreach (var e in edges.Where(e => e.EvidenceState == ArchitectureEvidenceState.Conflict))
            foreach (var b in boundaries.Where(b => b.Peer == e.Via).ToList())
                boundaries[boundaries.IndexOf(b)] = b with { Propagation = PropagationState.Conflicting, EvidenceState = ArchitectureEvidenceState.Conflict, Limitation = e.Limitation };

        var summary = new CorrelationSummary
        {
            ComponentsWithTracing = tracing.Count,
            HttpBoundaries = boundaries.Count(b => b.Type is CorrelationBoundaryType.HttpInbound or CorrelationBoundaryType.HttpOutbound),
            MessagingBoundaries = boundaries.Count(b => b.Type is CorrelationBoundaryType.MessageProduce or CorrelationBoundaryType.MessageConsume),
            PropagationSupported = boundaries.Count(b => ObservabilitySnapshot.Supported(b.Propagation)),
            PropagationUnresolved = boundaries.Count(b => b.Propagation == PropagationState.Unresolved),
            ConflictingMechanisms = conflicts + boundaries.Count(b => b.Propagation == PropagationState.Conflicting),
        };
        return new Result(summary, mechanismList, boundaries.OrderBy(b => b.Component, StringComparer.Ordinal).ThenBy(b => b.Type).ThenBy(b => b.Peer, StringComparer.Ordinal).ToList(), edges, tracing);

        static string i_(ArchitectureEvidence e) => e.Explanation.Length > 60 ? e.Explanation[..60] : e.Explanation;
    }

    private static CorrelationEdge Edge(string from, string to, string via, CorrelationBoundary? send, CorrelationBoundary? recv, HashSet<string>? sendHeaders, HashSet<string>? recvHeaders)
    {
        if (sendHeaders is { Count: > 0 } && recvHeaders is { Count: > 0 } && !sendHeaders.Overlaps(recvHeaders) && via == "HTTP")
            return new(from, to, via, "Different correlation headers on each side", ArchitectureEvidenceState.Conflict,
                $"Sender uses {string.Join(", ", sendHeaders)}; receiver reads {string.Join(", ", recvHeaders)}.");
        if (send is null || recv is null)
            return new(from, to, via, send?.Mechanism ?? recv?.Mechanism ?? "Not found", ArchitectureEvidenceState.Unresolved, send is null ? "No send-side boundary found in source." : "No receive-side boundary found in source.");
        var state = send.Propagation == PropagationState.Unresolved || recv.Propagation == PropagationState.Unresolved ? ArchitectureEvidenceState.Unresolved
            : send.Propagation == PropagationState.PropagationInferred || recv.Propagation == PropagationState.PropagationInferred ? ArchitectureEvidenceState.Inferred
            : send.Propagation == PropagationState.PropagationExplicit && recv.Propagation == PropagationState.PropagationExplicit && send.Context.Intersect(recv.Context).Any() ? ArchitectureEvidenceState.Confirmed
            : ArchitectureEvidenceState.StronglySupported;
        var mismatch = send.Propagation == PropagationState.PropagationExplicit && recv.Propagation == PropagationState.PropagationExplicit && !send.Context.Intersect(recv.Context).Any();
        return new(from, to, via, send.Mechanism == recv.Mechanism ? send.Mechanism : $"{send.Mechanism} → {recv.Mechanism}", mismatch ? ArchitectureEvidenceState.Conflict : state,
            mismatch ? "Sender and receiver handle different context identities." : send.Limitation ?? recv.Limitation);
    }

    private static string Name(ArchitectureSnapshot arch, string id) => arch.Components.FirstOrDefault(c => c.Id == id)?.Name ?? ArchitectureText.Safe(id);

    private static string PeerName(ArchitectureSnapshot? arch, string id) =>
        arch?.Components.FirstOrDefault(c => c.Id == id)?.Name ?? arch?.ExternalSystems.FirstOrDefault(e => e.Id == id)?.Name ?? ArchitectureText.Safe(id);

    private static List<CorrelationIdentityType> Context(bool w3c, IEnumerable<string> headers)
    {
        var list = new List<CorrelationIdentityType>();
        if (w3c) list.Add(CorrelationIdentityType.W3CTraceContext);
        foreach (var h in headers) if (Identity(h) is { } i && !list.Contains(i)) list.Add(i);
        return list;
    }

    private static CorrelationIdentityType? Identity(string header)
    {
        var h = header.ToLowerInvariant();
        if (h is "traceparent" or "tracestate" or "diagnostic-id") return CorrelationIdentityType.W3CTraceContext;
        if (h.Contains("correlation")) return CorrelationIdentityType.CorrelationId;
        if (h.Contains("request")) return CorrelationIdentityType.RequestId;
        if (h.Contains("operation")) return CorrelationIdentityType.OperationId;
        if (h.Contains("trace")) return CorrelationIdentityType.CustomContext;
        return null;
    }

    /// <summary>Assignments that map one identity onto another (e.g. CorrelationId = Activity.Current.TraceId). MessageId ↔ CorrelationId reuse is a review prompt.</summary>
    private static void Mappings(ObservabilitySourceAnalyzer.Analysis a, ObsComponent c,
        Action<CorrelationIdentityType, string, string, ObsComponent, ArchitectureEvidenceState, ObservabilityEvidence?> mechanism)
    {
        foreach (var f in c.All)
        {
            if (!Identities.Any(i => f.Text.Contains(i, StringComparison.Ordinal))) continue;
            var state = c.Own.Contains(f) ? ArchitectureEvidenceState.Confirmed : ArchitectureEvidenceState.StronglySupported;
            foreach (var assign in f.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                var left = assign.Left switch { MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText, IdentifierNameSyntax i => i.Identifier.ValueText, _ => null };
                if (left is null || !Identities.Contains(left)) continue;
                var right = assign.Right.DescendantNodesAndSelf().Select(n => n switch { IdentifierNameSyntax i => i.Identifier.ValueText, _ => null }).OfType<string>().ToList();
                var source = right.FirstOrDefault(r => Identities.Contains(r) && r != left) ?? (right.Contains("Current") && right.Contains("Activity") ? "Activity" : null)
                    ?? (assign.Right.ToString().Contains("Guid.NewGuid", StringComparison.Ordinal) ? "new Guid" : null);
                if (source is null) continue;
                var evidence = ObsText.Evidence(f, assign, $"{left} ← {source}");
                mechanism(left == "CorrelationId" ? CorrelationIdentityType.CorrelationId : left == "MessageId" ? CorrelationIdentityType.MessageId : CorrelationIdentityType.CustomContext,
                    $"Mapping: {left} ← {source}", "Source assignment", c, state, evidence);
                if ((left, source) is ("CorrelationId", "MessageId") or ("MessageId", "CorrelationId"))
                    a.Sink.Add(ObservabilityCategory.Correlation, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, state, c.Id, "Correlation",
                        $"{left} assigned from {source}", "MessageId identifies one message; CorrelationId groups related work. Reusing one as the other is valid only if intended.", evidence);
                else if (left == "CorrelationId" && source == "new Guid")
                    a.Sink.Add(ObservabilityCategory.Correlation, ObservabilityFindingKind.Observation, ObservabilitySeverity.Info, state, c.Id, "Correlation",
                        "New correlation id generated in code", "A fresh id starts a new correlation chain here; it does not continue an incoming one unless the caller had none.", evidence);
                else if (source == "TraceIdentifier")
                    a.Sink.Add(ObservabilityCategory.Correlation, ObservabilityFindingKind.Observation, ObservabilitySeverity.Info, state, c.Id, "ASP.NET Core",
                        "Request id used as correlation", "HttpContext.TraceIdentifier is a per-request id assigned by the server, not the W3C trace id.", evidence);
            }
        }
    }
}
