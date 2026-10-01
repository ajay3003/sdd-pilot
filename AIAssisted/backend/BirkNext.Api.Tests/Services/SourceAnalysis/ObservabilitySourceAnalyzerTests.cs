using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis.Observability;
using BirkNext.Api.Services.SourceArchitecture;
using BirkNext.SourceArchitecture;
using BirkNext.SourceObservability;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.SourceAnalysis;

/// <summary>
/// Source Analysis → Observability over synthetic, generic fixtures read through the real archive reader and Architecture analyzer:
/// correlation boundaries and mechanisms, logging quality, telemetry configuration, secret safety and language capability. Every state is
/// source-derived; nothing here claims that context propagates or telemetry arrives at runtime.
/// </summary>
public sealed class ObservabilitySourceAnalyzerTests
{
    private const string Web = """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>{0}</Project>""";
    private const string Worker = """<Project Sdk="Microsoft.NET.Sdk.Worker"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>{0}</Project>""";
    private const string Test = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="xunit" Version="2.0.0" /></ItemGroup></Project>""";
    private static string Pkg(params string[] packages) => "<ItemGroup>" + string.Concat(packages.Select(p => $"""<PackageReference Include="{p}" Version="1.0.0" />""")) + "</ItemGroup>";

    internal static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        return buffer.ToArray();
    }

    internal static SourceObservabilitySnapshot Analyze(params (string Path, string Content)[] files)
    {
        var (workspace, error) = IqrSourceArchiveReader.Read("fixture.zip", Zip(files));
        error.Should().BeNull();
        var id = Guid.NewGuid();
        var architecture = SourceArchitectureAnalyzer.Analyze(id, workspace!, DateTimeOffset.UtcNow, null, default, out var input, out _);
        return ObservabilitySourceAnalyzer.Analyze(id, workspace!, input, architecture, DateTimeOffset.UtcNow, default);
    }

    // ── Generic .NET fixture: an API with OpenTelemetry + a Service Bus worker ──────────────────────────────────────────────
    private const string ApiProgram = """
        using System.Diagnostics;
        using Azure.Messaging.ServiceBus;
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("shop-api"))
            .WithTracing(t => t.AddSource("Shop.Api").AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter());
        builder.Services.AddHttpClient<StockClient>();
        builder.Services.AddSingleton(new ServiceBusClient(builder.Configuration["Bus"]));
        var app = builder.Build();
        app.MapGet("/orders/{id}", async (string id, OrderService s) => await s.GetAsync(id));
        app.Run();
        """;
    private const string OrderService = """
        using System.Diagnostics;
        using Azure.Messaging.ServiceBus;
        namespace Shop.Api;
        public sealed class OrderService(ILogger<OrderService> logger, StockClient stock, ServiceBusClient bus)
        {
            private static readonly ActivitySource Source = new("Shop.Api");
            public async Task<string> GetAsync(string id)
            {
                using var activity = Source.StartActivity("GetOrder");
                using var scope = logger.BeginScope(new Dictionary<string, object> { ["OrderId"] = id });
                logger.LogInformation("Fetching order {OrderId}", id);
                try
                {
                    var sender = bus.CreateSender("orders");
                    await sender.SendMessageAsync(new ServiceBusMessage("x") { CorrelationId = Activity.Current?.TraceId.ToString() });
                    return await stock.CheckAsync(id);
                }
                catch (HttpRequestException ex)
                {
                    logger.LogError("Stock lookup failed: {Reason}", ex.Message);
                    throw;
                }
            }
            public void Report(string id, int count)
            {
                logger.LogWarning($"Order {id} has {count} lines");
                logger.LogInformation("Order " + id + " done");
            }
        }
        public sealed class StockClient(HttpClient http) { public Task<string> CheckAsync(string id) => http.GetStringAsync("/stock/" + id); }
        """;
    private const string WorkerProgram = """
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddOpenTelemetry().WithTracing(t => t.AddSource("Azure.*").AddSource("Shop.Worker").AddAzureMonitorTraceExporter());
        builder.Services.AddHostedService<Shop.Worker.OrderWorker>();
        builder.Build().Run();
        """;
    private const string OrderWorker = """
        using System.Diagnostics;
        using Azure.Messaging.ServiceBus;
        namespace Shop.Worker;
        public sealed class OrderWorker(ServiceBusClient client, ILogger<OrderWorker> logger) : BackgroundService
        {
            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                var processor = client.CreateProcessor("orders");
                processor.ProcessMessageAsync += OnMessage;
                processor.ProcessErrorAsync += OnError;
                await processor.StartProcessingAsync(stoppingToken);
            }
            private Task OnMessage(ProcessMessageEventArgs args)
            {
                logger.LogInformation("Received {MessageId} for {CorrelationId}", args.Message.MessageId, args.Message.CorrelationId);
                return Task.CompletedTask;
            }
            private Task OnError(ProcessErrorEventArgs args)
            {
                logger.LogError(args.Exception, "Processing failed for {Entity}", args.EntityPath);
                return Task.CompletedTask;
            }
        }
        """;
    private const string TracingTest = """
        using System.Diagnostics;
        public class TracingTests { [Fact] public void Starts() { using var listener = new ActivityListener(); } }
        """;

    private static (string, string)[] Generic() =>
    [
        ("shop/Shop.Api/Shop.Api.csproj", string.Format(Web, Pkg("OpenTelemetry.Extensions.Hosting", "OpenTelemetry.Instrumentation.AspNetCore", "OpenTelemetry.Instrumentation.Http", "Azure.Messaging.ServiceBus"))),
        ("shop/Shop.Api/Program.cs", ApiProgram),
        ("shop/Shop.Api/OrderService.cs", OrderService),
        ("shop/Shop.Api/appsettings.json", """{ "Logging": { "LogLevel": { "Default": "Information" } }, "Bus": "Endpoint=sb://x/;SharedAccessKey=SECRET_SENTINEL_1" }"""),
        ("shop/Shop.Api/appsettings.Development.json", """{ "OpenTelemetry": { "ServiceName": "shop-api-dev" }, "Logging": { "LogLevel": { "Default": "Debug" } } }"""),
        ("shop/Shop.Api/appsettings.Production.json", """{ "OpenTelemetry": { "ServiceName": "shop-api" } }"""),
        ("shop/Shop.Worker/Shop.Worker.csproj", string.Format(Worker, Pkg("OpenTelemetry.Extensions.Hosting", "Azure.Monitor.OpenTelemetry.Exporter", "Azure.Messaging.ServiceBus"))),
        ("shop/Shop.Worker/Program.cs", WorkerProgram),
        ("shop/Shop.Worker/OrderWorker.cs", OrderWorker),
        ("shop/Shop.Tests/Shop.Tests.csproj", Test),
        ("shop/Shop.Tests/TracingTests.cs", TracingTest),
    ];

    [Fact]
    public void BindsToTheSourceSnapshotAndStaysSourceDerived()
    {
        var (workspace, _) = IqrSourceArchiveReader.Read("fixture.zip", Zip(Generic()));
        var id = Guid.NewGuid();
        var architecture = SourceArchitectureAnalyzer.Analyze(id, workspace!, DateTimeOffset.UtcNow, null, default, out var input, out _);
        var at = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        var o = ObservabilitySourceAnalyzer.Analyze(id, workspace!, input, architecture, at, default);

        o.SourceSnapshotId.Should().Be(id);
        o.SourceFingerprint.Should().Be(workspace!.Archive.Sha256);
        o.AnalyzerVersion.Should().Be(ObservabilitySourceAnalyzer.Version);
        o.ExtractedAt.Should().Be(at);
        o.Status.Should().Be(ArchitectureStatus.Complete);
        o.Limitations.Should().Contain(ObservabilitySnapshot.SourceLimitation);
        o.Components.Should().OnlyContain(c => c.Runtime == ObservabilitySnapshot.RuntimeNotAssessed);
        o.Components.Select(c => c.ComponentId).Should().NotContain(n => n.Contains("Tests"));

        var json = JsonSerializer.Serialize(o);
        json.Should().NotContain("\"Pass\"").And.NotContain("\"Fail\"").And.NotContain("Healthy").And.NotContain("Broken").And.NotContain("%");
        json.Should().NotContain("SECRET_SENTINEL").And.NotContain("SharedAccessKey");
    }

    [Fact]
    public void FindsTracingAndPropagationAtEveryBoundary()
    {
        var o = Analyze(Generic());
        var api = o.Components.Single(c => c.ComponentId == "component:Shop.Api");
        api.TracingTechnologies.Should().Contain(["OpenTelemetry", "ASP.NET Core instrumentation", "HttpClient instrumentation", "ActivitySource"]);
        o.Components.Single(c => c.ComponentId == "component:Shop.Worker").TracingTechnologies.Should().Contain("OpenTelemetry");

        var b = o.Boundaries;
        b.Should().Contain(x => x.Component == api.ComponentId && x.Type == CorrelationBoundaryType.HttpInbound && x.Propagation == PropagationState.FrameworkInstrumentation
            && x.Context.Contains(CorrelationIdentityType.W3CTraceContext));
        b.Should().Contain(x => x.Component == api.ComponentId && x.Type == CorrelationBoundaryType.MessageProduce && x.Transport == "Azure Service Bus"
            && x.Propagation == PropagationState.PropagationExplicit && x.Context.Contains(CorrelationIdentityType.CorrelationId));
        b.Should().Contain(x => x.Type == CorrelationBoundaryType.MessageConsume && x.Transport == "Azure Service Bus" && x.Propagation == PropagationState.PropagationExplicit);
        b.Should().NotContain(x => x.Propagation == PropagationState.Unresolved);
        o.Edges.Should().Contain(e => e.Via == "orders" && e.From == "Api" && e.To == "Worker" && e.EvidenceState == ArchitectureEvidenceState.Confirmed);

        o.Mechanisms.Should().Contain(m => m.Identity == CorrelationIdentityType.W3CTraceContext && m.Name == "W3C trace context (framework)");
        o.Mechanisms.Should().Contain(m => m.Name == "Mapping: CorrelationId ← TraceId", "the producer maps the trace id onto the message CorrelationId");
        o.Mechanisms.Should().Contain(m => m.Identity == CorrelationIdentityType.ActivityTrace);
        o.Correlation.PropagationSupported.Should().Be(o.Boundaries.Count);
    }

    [Fact]
    public void ReportsLoggingQualityDimensions()
    {
        var o = Analyze(Generic());
        o.Logging.Frameworks.Should().Contain("Microsoft.Extensions.Logging");
        o.Logging.InterpolatedCalls.Should().Be(1);
        o.Logging.ConcatenatedCalls.Should().BeGreaterThanOrEqualTo(1);
        o.Logging.ExceptionMessageOnly.Should().Be(1);
        o.Logging.ExceptionPreserved.Should().Be(1);
        var f = o.Findings;
        f.Should().Contain(x => x.Category == ObservabilityCategory.ExceptionPreservation && x.Title == "Exception message logged without the exception" && x.Severity == ObservabilitySeverity.Warning
            && x.Evidence.Single().File == "shop/Shop.Api/OrderService.cs" && x.Evidence.Single().Symbol == "OrderService.GetAsync");
        f.Should().Contain(x => x.Title == "Interpolated log message" && x.Kind == ObservabilityFindingKind.Finding);
        f.Should().Contain(x => x.Category == ObservabilityCategory.CorrelationContextLogging && x.Title == "Logging scopes");
        f.Should().Contain(x => x.Category == ObservabilityCategory.DeveloperTests && x.Title == "Correlation / tracing test");
        f.Should().NotContain(x => x.Category == ObservabilityCategory.HandlerObservability && x.Severity >= ObservabilitySeverity.NeedsReview, "both handlers log");
        f.Should().OnlyContain(x => x.Evidence.Count <= ObservabilityAnalyzerOptions.Default.MaxEvidencePerFinding);
        o.Logging.Dimensions.Single(d => d.Id == "exceptions").State.Should().Be(ObservabilityDimensionState.NeedsReview);
        o.Logging.Dimensions.Single(d => d.Id == "tests").State.Should().Be(ObservabilityDimensionState.Detected);
    }

    [Fact]
    public void KeepsEnvironmentSpecificTelemetrySettingsAsVariantsNotConflicts()
    {
        var o = Analyze(Generic());
        o.Telemetry.Exporters.Should().Contain(["OTLP exporter", "Azure Monitor exporter"]);
        o.Telemetry.ServiceNames.Should().Contain(s => s.Value == "shop-api-dev" && s.Environment == "Development")
            .And.Contain(s => s.Value == "shop-api" && s.Environment == "Production");
        o.Findings.Should().NotContain(x => x.Title == "Conflicting service names");
        o.Telemetry.LogLevels.Should().Contain(l => l.Environment == "Development" && l.Value == "Debug");
        o.Findings.Should().NotContain(x => x.Title == "Potentially noisy: verbose default log level", "Debug in Development is expected");
    }

    [Fact]
    public void ConflictingServiceNamesInOneEnvironmentNeedReview()
    {
        var o = Analyze(
            ("a/A.Api/A.Api.csproj", string.Format(Web, "")), ("a/A.Api/Program.cs", "var app = WebApplication.CreateBuilder(args).Build(); app.MapGet(\"/\", () => 1); app.Run();"),
            ("a/A.Api/appsettings.json", """{ "OpenTelemetry": { "ServiceName": "orders" }, "ApplicationInsights": { "CloudRoleName": "orders-api" } }"""),
            ("a/B.Api/B.Api.csproj", string.Format(Web, "")), ("a/B.Api/Program.cs", "var app = WebApplication.CreateBuilder(args).Build(); app.MapGet(\"/\", () => 1); app.Run();"),
            ("a/B.Api/appsettings.json", """{ "OpenTelemetry": { "ServiceName": "orders" } }"""));
        o.Findings.Should().Contain(x => x.Title == "Conflicting service names" && x.EvidenceState == ArchitectureEvidenceState.Conflict && x.Severity == ObservabilitySeverity.NeedsReview);
        o.Findings.Should().Contain(x => x.Title == "Shared service name orders");
        o.Logging.Dimensions.Single(d => d.Id == "service-name").State.Should().Be(ObservabilityDimensionState.NeedsReview);
    }

    // ── Secret safety ───────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void NeverPersistsSecretLiteralValues()
    {
        const string code = """
            namespace Shop.Api;
            public sealed class Login(ILogger<Login> logger)
            {
                public void Run(HttpRequest request)
                {
                    var password = "abc";
                    var connectionString = "Server=db;User Id=sa;Password=hunter2Literal";
                    var token = "eyJhbGciOiJIUzI1NiJ9.payloadLiteral";
                    logger.LogInformation(password);
                    logger.LogDebug("Connecting with {ConnectionString}", connectionString);
                    logger.LogInformation("Bearer {Token}", token);
                    logger.LogInformation("Headers {Headers}", request.Headers);
                    logger.LogInformation("Customer {Email}", "someone@example.org");
                }
            }
            """;
        var o = Analyze(("s/S.Api/S.Api.csproj", string.Format(Web, "")), ("s/S.Api/Login.cs", code));
        // Identifiers and the archive hash are random hex, so compare everything except them.
        var json = JsonSerializer.Serialize(o with { Id = Guid.Empty, SourceSnapshotId = Guid.Empty, SourceFingerprint = "" });
        json.Should().NotContain("abc").And.NotContain("hunter2Literal").And.NotContain("payloadLiteral").And.NotContain("someone@example.org").And.NotContain("eyJ");
        var sensitive = o.Findings.Where(f => f.Category == ObservabilityCategory.SensitiveData).ToList();
        sensitive.Select(f => f.Title).Should().Contain([
            "Possible sensitive data in logs: Password / secret", "Possible sensitive data in logs: Connection string", "Possible sensitive data in logs: Token / bearer credential",
            "Possible sensitive data in logs: HTTP headers (may contain authorization or cookies)", "Possible sensitive data in logs: Personal data (configurable term)"]);
        sensitive.SelectMany(f => f.Evidence).Should().OnlyContain(e => e.File == "s/S.Api/Login.cs" && e.Symbol == "Login.Run" && e.Line > 0);
        o.Findings.Should().Contain(f => f.Title == "Non-constant log message template");
        o.Logging.Dimensions.Single(d => d.Id == "redaction").State.Should().Be(ObservabilityDimensionState.NeedsReview);
    }

    [Fact]
    public void PersonalDataTermsAreConfigurable()
    {
        var (workspace, _) = IqrSourceArchiveReader.Read("fixture.zip", Zip(("s/S.Api/S.Api.csproj", string.Format(Web, "")),
            ("s/S.Api/A.cs", "public class A(ILogger<A> logger) { public void M(string email, string memberNo) { logger.LogInformation(\"{Email} {MemberNo}\", email, memberNo); } }")));
        var architecture = SourceArchitectureAnalyzer.Analyze(Guid.NewGuid(), workspace!, DateTimeOffset.UtcNow, null, default, out var input, out _);
        var o = ObservabilitySourceAnalyzer.Analyze(Guid.NewGuid(), workspace!, input, architecture, DateTimeOffset.UtcNow, default,
            new ObservabilityAnalyzerOptions { PersonalDataTerms = ["memberno"] });
        o.Findings.Where(f => f.Category == ObservabilityCategory.SensitiveData).Should().ContainSingle().Which.Occurrences.Should().Be(1);
    }

    // ── Catch-and-swallow ───────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void CatchAndSwallowExcludesCancellationTryHelpersCleanupAndIntentionalMarkers()
    {
        const string code = """
            namespace S;
            public sealed class Jobs(ILogger<Jobs> logger)
            {
                public void Empty() { try { Work(); } catch (Exception) { } }
                public int Quiet() { try { Work(); return 1; } catch (InvalidOperationException) { return 0; } }
                public async Task Cancel(CancellationToken ct) { try { await Task.Delay(1, ct); } catch (OperationCanceledException) { } }
                public async Task Filtered(CancellationToken ct) { try { await Task.Delay(1, ct); } catch (Exception) when (ct.IsCancellationRequested) { } }
                public bool TryRead(out int v) { v = 0; try { Work(); return true; } catch (FormatException) { return false; } }
                public void Dispose() { try { Work(); } catch (Exception) { } }
                public void Marked() { try { Work(); } catch (IOException) { /* intentionally ignored: best effort */ } }
                public void Logged() { try { Work(); } catch (Exception ex) { logger.LogError(ex, "Work failed"); } }
                public void Rethrown() { try { Work(); } catch (Exception) { throw; } }
                public string Health() { try { Work(); return "ok"; } catch (Exception ex) { return "Unhealthy: " + ex.Message; } }
                private static void Work() { }
            }
            """;
        var o = Analyze(("s/S.Worker/S.Worker.csproj", string.Format(Worker, "")), ("s/S.Worker/Jobs.cs", code));
        var swallow = o.Findings.Where(f => f.Category == ObservabilityCategory.CatchAndSwallow).ToList();
        swallow.Where(f => f.Severity >= ObservabilitySeverity.NeedsReview).SelectMany(f => f.Evidence).Select(e => e.Symbol).Should().BeEquivalentTo(["Jobs.Empty", "Jobs.Quiet"]);
        swallow.Should().Contain(f => f.Title == "Exception returned to the caller as an error result" && f.Severity == ObservabilitySeverity.Info && f.Evidence.Single().Symbol == "Jobs.Health");
        o.Logging.Dimensions.Single(d => d.Id == "swallow").Detail.Should().StartWith("2 catch block(s)").And.Contain("1 more return the exception");
        swallow.Should().Contain(f => f.Title == "Empty catch block" && f.Severity == ObservabilitySeverity.Warning);
        swallow.Should().Contain(f => f.Title == "Exception converted to a return value without logging" && f.Severity == ObservabilitySeverity.NeedsReview);
    }

    [Fact]
    public void BrowserHttpClientIsNotAssumedToPropagate()
    {
        const string blazor = """<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""";
        var o = Analyze(("ui/Ui.Web/Ui.Web.csproj", blazor),
            ("ui/Ui.Web/Program.cs", "var b = WebAssemblyHostBuilder.CreateDefault(args); b.Services.AddHttpClient(\"Api\", c => c.BaseAddress = new Uri(\"https://api.example.org/\")); await b.Build().RunAsync();"),
            ("ui/Ui.Web/Client.cs", "public class Client(IHttpClientFactory f) { public Task<string> Get() { System.Console.WriteLine(\"x\"); return f.CreateClient(\"Api\").GetStringAsync(\"/a\"); } }"));
        o.Boundaries.Where(b => b.Type == CorrelationBoundaryType.HttpOutbound).Should().NotBeEmpty().And.OnlyContain(b => b.Propagation == PropagationState.Unresolved && b.Limitation!.Contains("fetch"));
        o.Findings.Should().Contain(f => f.Category == ObservabilityCategory.FrontendLogging && f.Title == "Browser console output from WebAssembly code");
    }

    // ── Multi-transport fixture ─────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void AppliesTransportAdaptersWithoutAssumingPropagation()
    {
        const string kafka = """
            using Confluent.Kafka;
            namespace T;
            public sealed class Publisher(IProducer<string, string> producer, ILogger<Publisher> logger)
            {
                public async Task Send(string key) { await producer.ProduceAsync("orders", new Message<string, string> { Key = key, Value = "v" }); logger.LogInformation("Sent {Key}", key); }
            }
            """;
        const string rabbit = """
            using RabbitMQ.Client;
            namespace T;
            public sealed class Notifier(IModel channel)
            {
                public void Notify(string correlation)
                {
                    var props = channel.CreateBasicProperties();
                    props.CorrelationId = correlation;
                    channel.BasicPublish("ex", "rk", props, new byte[0]);
                }
            }
            """;
        const string hub = """
            using Azure.Messaging.EventHubs;
            using Azure.Messaging.EventHubs.Processor;
            namespace T;
            public sealed class Reader(EventProcessorClient processor, ILogger<Reader> logger)
            {
                public void Start() { processor.ProcessEventAsync += OnEvent; processor.ProcessErrorAsync += OnError; }
                private Task OnEvent(ProcessEventArgs args) { logger.LogInformation("Event {Partition} {Offset}", args.Partition.PartitionId, args.Data.Offset); return Task.CompletedTask; }
                private Task OnError(ProcessErrorEventArgs args) => Task.CompletedTask;
            }
            """;
        const string wolverine = """
            namespace T;
            public sealed class OrderPlacedHandler { public void Handle(OrderPlaced message) { } }
            public sealed record OrderPlaced(string Id);
            """;
        var o = Analyze(
            ("t/T.Worker/T.Worker.csproj", string.Format(Worker, Pkg("Confluent.Kafka", "RabbitMQ.Client", "Azure.Messaging.EventHubs.Processor", "WolverineFx"))),
            ("t/T.Worker/Publisher.cs", kafka), ("t/T.Worker/Notifier.cs", rabbit), ("t/T.Worker/Reader.cs", hub), ("t/T.Worker/Handlers.cs", wolverine),
            ("t/T.Worker/Program.cs", "var b = Host.CreateApplicationBuilder(args); b.Build().Run();"));
        var b = o.Boundaries;
        b.Should().Contain(x => x.Transport == "Kafka" && x.Type == CorrelationBoundaryType.MessageProduce && x.Propagation == PropagationState.Unresolved && x.Limitation!.Contains("does not propagate"));
        b.Should().Contain(x => x.Transport == "RabbitMQ" && x.Type == CorrelationBoundaryType.MessageProduce && x.Propagation == PropagationState.PropagationExplicit && x.Context.Contains(CorrelationIdentityType.CorrelationId));
        b.Should().Contain(x => x.Transport == "Azure Event Hubs" && x.Type == CorrelationBoundaryType.MessageConsume && x.Propagation == PropagationState.PropagationInferred);
        b.Should().Contain(x => x.Transport == "Wolverine" && x.Type == CorrelationBoundaryType.MessageConsume && x.Propagation == PropagationState.PropagationInferred);
        o.Findings.Should().Contain(f => f.Kind == ObservabilityFindingKind.Unresolved && f.Title == "No correlation propagation found at Message produce (Kafka)");
        o.Findings.Should().Contain(f => f.Title == "Event Hub partition/offset metadata is positional, not correlation");
        o.Findings.Should().Contain(f => f.Title == "Message error handler does not log" && f.Severity == ObservabilitySeverity.Warning);
        o.Findings.Should().Contain(f => f.Title == "Message handler has no logging" && f.Technology == "Wolverine handler" && f.Severity == ObservabilitySeverity.Info);
        o.Correlation.PropagationUnresolved.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void DifferentCorrelationHeaderNamesAndMessageIdReuseNeedReview()
    {
        var o = HeaderFixture();
        o.Findings.Should().Contain(f => f.Title == "Different correlation header names in use" && f.EvidenceState == ArchitectureEvidenceState.Conflict);
        o.Findings.Should().Contain(f => f.Title == "CorrelationId assigned from MessageId" && f.Severity == ObservabilitySeverity.NeedsReview);
        o.Boundaries.Should().Contain(x => x.Type == CorrelationBoundaryType.HttpInbound && x.Propagation == PropagationState.PropagationExplicit);
        o.Boundaries.Should().Contain(x => x.Type == CorrelationBoundaryType.HttpInbound && x.Propagation == PropagationState.PropagationInferred && x.Limitation!.Contains("no tracing instrumentation"));
        o.Correlation.ConflictingMechanisms.Should().BeGreaterThan(0);
    }

    private static SourceObservabilitySnapshot HeaderFixture()
    {
        const string a = """
            namespace A;
            public sealed class Correlation : IMiddleware
            {
                public Task InvokeAsync(HttpContext context, RequestDelegate next) { var id = context.Request.Headers["X-Correlation-ID"]; return next(context); }
            }
            """;
        const string b = """
            using Azure.Messaging.ServiceBus;
            namespace B;
            public sealed class Client(HttpClient http, ServiceBusSender sender)
            {
                public async Task Call(ServiceBusReceivedMessage incoming)
                {
                    http.DefaultRequestHeaders.Add("Correlation-Id", "1");
                    var message = new ServiceBusMessage("x");
                    message.CorrelationId = incoming.MessageId;
                    await sender.SendMessageAsync(message);
                }
            }
            """;
        var o = Analyze(
            ("x/A.Api/A.Api.csproj", string.Format(Web, "")), ("x/A.Api/Correlation.cs", a), ("x/A.Api/Program.cs", "var app = WebApplication.CreateBuilder(args).Build(); app.MapGet(\"/\", () => 1); app.Run();"),
            ("x/B.Api/B.Api.csproj", string.Format(Web, Pkg("Azure.Messaging.ServiceBus"))), ("x/B.Api/Client.cs", b), ("x/B.Api/Program.cs", "var app = WebApplication.CreateBuilder(args).Build(); app.MapGet(\"/b\", () => 2); app.Run();"));
        return o;
    }

    // ── Non-.NET capability ─────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void UnsupportedLanguagesAreReportedNotGuessed()
    {
        var o = Analyze(("web/package.json", """{ "name": "web" }"""), ("web/src/index.js", "console.log('x'); fetch('/api', { headers: { traceparent: '00-x' } });"),
            ("svc/app.py", "import logging\nlogging.info('x')"), ("svc/Main.java", "class Main {}"));
        o.Status.Should().Be(ArchitectureStatus.Unsupported);
        o.UnsupportedEvidence.Should().Contain(["Not analyzed: .js source (unsupported language).", "Not analyzed: .py source (unsupported language).", "Not analyzed: .java source (unsupported language)."]);
        o.Capabilities.Single(c => c.Technology == "JavaScript / TypeScript").Should().Match<AnalyzerCapability>(c => c.Correlation == AnalyzerSupport.Unsupported && c.Detail.StartsWith("Present in this snapshot"));
        o.Capabilities.Single(c => c.Technology == "Go").Detail.Should().StartWith("Not present");
        o.Capabilities.Single(c => c.Technology == "C# / .NET").Correlation.Should().Be(AnalyzerSupport.Unsupported);
        o.Capabilities.Should().OnlyContain(c => c.Correlation == AnalyzerSupport.Unsupported, "nothing is supported without a .NET project");
        o.Findings.Should().Contain(f => f.Kind == ObservabilityFindingKind.Limitation && f.Title == "Frontend console logging and browser trace propagation not analyzed");
        o.Boundaries.Should().BeEmpty();
        o.Logging.StructuredCalls.Should().Be(0);
    }

    [Fact]
    public void MixedSnapshotsArePartial()
    {
        var o = Analyze([.. Generic(), ("web/src/app.ts", "console.log('x')")]);
        o.Status.Should().Be(ArchitectureStatus.Partial);
        o.Capabilities.Single(c => c.Technology == "C# / .NET").Correlation.Should().Be(AnalyzerSupport.Supported);
    }

    [Fact]
    public void ProductionRulesContainNoProjectNameSpecialCases()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BirkNext.Api", "Services", "SourceAnalysis", "Observability");
        var files = Directory.GetFiles(dir, "*.cs");
        files.Should().NotBeEmpty();
        foreach (var f in files)
            File.ReadAllText(f).Should().NotContainAny(["M2LB", "PersonAdapter", "Autorisasjon", "Bufetat"], $"{Path.GetFileName(f)} must stay generic");
    }

    // ── Integration Quality Review consumes the stored evidence (no rescan; runtime stays independent) ───────────────────────
    [Fact]
    public void IntegrationQualityReviewReadsObservabilityFromTheSnapshot()
    {
        var o = Analyze(Generic());
        var snapshot = new BirkNext.Integrations.IqrSourceSnapshot { Id = o.SourceSnapshotId, IntegrationId = "orders", Observability = o };
        var domains = new[] { BirkNext.Integrations.IntegrationReviewDomain.Observability, BirkNext.Integrations.IntegrationReviewDomain.ErrorHandling,
            BirkNext.Integrations.IntegrationReviewDomain.MessageFlow, BirkNext.Integrations.IntegrationReviewDomain.Reliability };
        var result = IqrSourceReview.Augment(new BirkNext.Integrations.IntegrationReviewResult
        {
            Domains = domains.Select(d => new BirkNext.Integrations.IntegrationDomainResult { Domain = d, StateLabel = "Assessed", Observed = ["Runtime: metric observed"] }).ToList(),
        }, [snapshot]);
        var observability = result.Domains.Single(d => d.Domain == BirkNext.Integrations.IntegrationReviewDomain.Observability);
        observability.StateLabel.Should().Be("Partially assessed", "source evidence never makes a domain fully assessed");
        observability.Observed.Should().Contain("Runtime: metric observed", "runtime evidence stays independent")
            .And.Contain(l => l.StartsWith("Source observability (source snapshot ") && l.Contains("tracing registered in 2 component(s)"))
            .And.Contain(l => l.StartsWith("Correlation propagation in source: Strongly supported"));
        observability.Missing.Should().Contain(IqrObservabilityReview.RuntimeLimitation);
        result.Domains.Single(d => d.Domain == BirkNext.Integrations.IntegrationReviewDomain.ErrorHandling).Observed.Should().Contain(l => l.StartsWith("Source exception logging") && l.Contains("1 log only its message"));
        result.Domains.Single(d => d.Domain == BirkNext.Integrations.IntegrationReviewDomain.MessageFlow).Observed.Should().Contain(l => l.StartsWith("Source message correlation") && l.Contains("2 produce/consume boundaries"));

        var readiness = IqrSourceReview.Augment(new BirkNext.Integrations.IntegrationReviewReadiness
        {
            Domains = [new(BirkNext.Integrations.IntegrationReviewDomain.Observability, BirkNext.Integrations.IntegrationDomainReadiness.NotAssessable, "No runtime source")],
        }, [snapshot]);
        readiness.Domains.Single().Readiness.Should().Be(BirkNext.Integrations.IntegrationDomainReadiness.Partial);
        readiness.Domains.Single().Available.Should().Contain(l => l.StartsWith("Source Analysis observability evidence available"));

        IqrSourceReview.Augment(new BirkNext.Integrations.IntegrationReviewResult { Domains = [new() { Domain = BirkNext.Integrations.IntegrationReviewDomain.Observability, StateLabel = "Not assessed" }] },
            [snapshot with { Observability = null }]).Domains.Single().StateLabel.Should().Be("Not assessed", "historical snapshots without Observability add nothing");
    }

    // ── Persistence: analysed once at upload, stored on the snapshot ─────────────────────────────────────────────────────
    [Fact]
    public async Task UploadPersistsObservabilityOnTheSnapshot()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var store = new IqrSourceStore(db);
        var (snapshot, error) = await store.AnalyzeAsync("dev", IqrSourceStore.SourceAnalysisOwner, "shop.zip", Zip(Generic()));
        error.Should().BeNull();
        snapshot!.Observability.Should().NotBeNull();
        snapshot.Observability!.SourceSnapshotId.Should().Be(snapshot.Id);
        var stored = await store.FindSourceAnalysisAsync("dev", snapshot.Id);
        stored!.Observability!.Boundaries.Should().HaveCount(snapshot.Observability.Boundaries.Count);
        stored.Observability.Findings.Select(f => f.Id).Should().Equal(snapshot.Observability.Findings.Select(f => f.Id));
        (await db.IqrSourceSnapshots.SingleAsync()).EvidenceJson.Should().NotContain("SECRET_SENTINEL");
    }
}
