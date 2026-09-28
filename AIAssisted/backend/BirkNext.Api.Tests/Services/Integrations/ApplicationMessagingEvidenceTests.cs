using System.IO.Compression;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// Wolverine application-messaging evidence: source-backed detection (never from transport), exact handler mapping, configured policy kept
/// apart from observed behaviour, the Hendelse adapter's Event Hub path (Wolverine only publishes error metadata), and IQR contribution to
/// existing domains without making any domain ready. Fixtures mirror the audited M2LB source shapes.
/// </summary>
public sealed class ApplicationMessagingEvidenceTests
{
    // ── Fixture sources (shapes from the audited M2LB / M2LB.Common archives) ──────────────────────────────────────

    private const string CommonCsproj = """
        <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><Version>1.0.10</Version></PropertyGroup>
          <ItemGroup><PackageReference Include="WolverineFx" /><PackageReference Include="WolverineFx.AzureServiceBus" /></ItemGroup></Project>
        """;
    private const string CommonPackages = """
        <Project><ItemGroup><PackageVersion Include="WolverineFx" Version="6.33.0" /><PackageVersion Include="WolverineFx.AzureServiceBus" Version="6.33.0" /></ItemGroup></Project>
        """;
    private const string CommonExtensions = """
        using Wolverine;
        using Wolverine.AzureServiceBus;
        namespace M2LB.Common.Messaging;
        public static class WolverineExtensions
        {
            public static IHostBuilder AddM2LbWolverine(this IHostBuilder host, IConfiguration configuration, string serviceName, bool useEntityFrameworkCoreTransactions, Action<WolverineOptions>? configure = null)
            {
                return host.UseWolverine(opts =>
                {
                    opts.ServiceName = serviceName;
                    if (useEntityFrameworkCoreTransactions) { opts.UseEntityFrameworkCoreTransactions(); }
                    var fqdn = configuration["ServiceBus:FQDN"];
                    opts.UseAzureServiceBus(configuration.GetConnectionString("ServiceBus")!).SystemQueuesAreEnabled(false);
                    configure?.Invoke(opts);
                });
            }
        }
        """;

    private static string AppCsproj(string extra = "") => $"""
        <Project Sdk="Microsoft.NET.Sdk.Web"><ItemGroup>
          <PackageReference Include="Azure.Messaging.EventHubs.Processor" Version="5.12.2" />
          <PackageReference Include="Azure.Messaging.ServiceBus" Version="7.20.2" />
          <PackageReference Include="M2LB.Common.Messaging" Version="1.0.10" />{extra}
        </ItemGroup></Project>
        """;

    private const string AdapterProgram = """
        using Wolverine.AzureServiceBus;
        var builder = WebApplication.CreateBuilder(args);
        builder.AddM2LbObservability("M2LB.HendelseAdapter");
        builder.Services.AddSingleton(sp => new EventHubConsumerClient("group", "fqdn", "topic", new DefaultAzureCredential()));
        builder.Services.AddSingleton<IErrorQueuePublisher, WolverineErrorQueuePublisher>();
        builder.Host.AddM2LbWolverine(builder.Configuration, "M2LB.HendelseAdapter", false, options =>
        {
            var queueName = builder.Configuration["ServiceBus:ErrorQueueName"] ?? "birk-adapter-errors";
            options.PublishMessage<BirkErrorMessage>()
                .ToAzureServiceBusQueue(queueName)
                .InteropWith(new BirkErrorMessageEnvelopeMapper());
        });
        var app = builder.Build();
        """;
    private const string AdapterPublisher = """
        using Wolverine;
        public sealed class WolverineErrorQueuePublisher : IErrorQueuePublisher
        {
            public async Task PublishAsync(string id, string tabell, string correlationId, string summary, CancellationToken ct = default)
            {
                var message = new BirkErrorMessage(id, tabell, correlationId, summary, DateTimeOffset.UtcNow);
                await using var scope = _scopeFactory.CreateAsyncScope();
                var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
                await bus.SendAsync(message);
            }
        }
        """;
    private const string AdapterProcessor = """
        public sealed class BirkEventProcessorSetup
        {
            public static EventProcessorClient Create(BlobContainerClient container) => new EventProcessorClient(new BlobCheckpointStore(container), "group", "fqdn", "hub", null);
        }
        public sealed class BirkCdcEventHandler : IBirkCdcEventHandler
        {
            private readonly IErrorQueuePublisher _errorQueuePublisher;
            public async Task HandleAsync(BirkCdcEvent evt, CancellationToken ct)
            {
                try { await Deliver(evt); }
                catch (HttpRequestException ex) { await _errorQueuePublisher.PublishAsync(evt.Id, evt.Tabell, "c", ex.Message, ct); }
            }
        }
        """;

    private const string RevisjonProgram = """
        using Wolverine;
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddM2LbObservability("M2LB.Revisjon");
        var sbQueueName = builder.Configuration["ServiceBus:QueueName"]!;
        builder.Host.AddM2LbWolverine(builder.Configuration, "M2LB.Revisjon", false, options =>
        {
            options.Discovery.IncludeAssembly(typeof(LeseloggHendelseHandler).Assembly);
            options.ListenToAzureServiceBusQueue(sbQueueName)
                .ProcessInline()
                .DefaultIncomingMessage<LeseloggHendelse>();
            options.OnException<RequestFailedException>(ex => ex.Status >= 500 || ex.Status == 429)
                .RetryWithCooldown(100.Milliseconds(), 500.Milliseconds(), 2.Seconds(), 10.Seconds());
            options.OnException<Exception>()
                .MoveToErrorQueue();
        });
        """;
    private const string RevisjonHandler = """
        using Wolverine;
        // C# 12 primary constructor: must not hide the handler from the syntax-only parse.
        public sealed class LeseloggHendelseHandler(IBlobLeseLoggCreator lager)
        {
            public async Task Handle(LeseloggHendelse hendelse, Envelope envelope, ILogger<LeseloggHendelseHandler> logger)
            {
                logger.LogInformation("Processed {HendelsesId}", hendelse.HendelsesId);
            }
        }
        """;

    private static byte[] Zip(Dictionary<string, string> files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(content);
            }
        return buffer.ToArray();
    }

    private static Dictionary<string, string> Common => new()
    {
        ["M2LB.Common/Directory.Packages.props"] = CommonPackages,
        ["M2LB.Common/src/M2LB.Common.Messaging/M2LB.Common.Messaging.csproj"] = CommonCsproj,
        ["M2LB.Common/src/M2LB.Common.Messaging/WolverineExtensions.cs"] = CommonExtensions,
    };

    private static Dictionary<string, string> Adapter => new()
    {
        ["HendelseAdapter/src/M2LB.Hendelse.BiRK.Adapter/M2LB.Hendelse.BiRK.Adapter.csproj"] = AppCsproj(),
        ["HendelseAdapter/src/M2LB.Hendelse.BiRK.Adapter/Program.cs"] = AdapterProgram,
        ["HendelseAdapter/src/M2LB.Hendelse.BiRK.Adapter/WolverineErrorQueuePublisher.cs"] = AdapterPublisher,
        ["HendelseAdapter/src/M2LB.Hendelse.BiRK.Adapter/BirkCdcEventHandler.cs"] = AdapterProcessor,
        ["HendelseAdapter/src/M2LB.Hendelse.BiRK.Adapter/appsettings.Local.json"] = """{ "ConnectionStrings": { "ServiceBus": "Endpoint=sb://localhost;SharedAccessKey=SECRET_SAS_VALUE" } }""",
    };

    private static Dictionary<string, string> Revisjon => new()
    {
        ["Revisjon/src/M2LB.Revisjon.Worker/M2LB.Revisjon.Worker.csproj"] = AppCsproj(),
        ["Revisjon/src/M2LB.Revisjon.Worker/Program.cs"] = RevisjonProgram,
        ["Revisjon/src/M2LB.Revisjon.Worker/LeseloggHendelseHandler.cs"] = RevisjonHandler,
    };

    private static ApplicationMessagingEvidenceSet Analyze(params Dictionary<string, string>[] archives)
    {
        var metadata = new List<SourceArchive>();
        var files = new List<SourceFile>();
        for (var i = 0; i < archives.Length; i++)
        {
            var (archive, read, error) = SourceArchiveReader.Read($"archive{i}.zip", Zip(archives[i]));
            error.Should().BeNull();
            metadata.Add(archive!);
            files.AddRange(read);
        }
        return WolverineSourceAnalyzer.Analyze("dev", metadata, files, DateTimeOffset.UtcNow);
    }

    private static ApplicationMessagingEvidence App(ApplicationMessagingEvidenceSet set, string id) => set.Applications.Single(a => a.ApplicationId == id);

    // ── Detection ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WolverineIsConfirmedFromRegistrationPlusPackageChain_WithVersionCaveat()
    {
        var app = App(Analyze(Adapter, Common), "M2LB.Hendelse.BiRK.Adapter");
        app.Detection.Should().Be(MessagingDetection.Confirmed);
        app.Fact("package")!.Detail.Should().Contain("M2LB.Common.Messaging 1.0.10").And.Contain("WolverineFx 6.33.0");
        app.Fact("package")!.Source.Should().Be(IntegrationEvidenceSource.PackageManifest);
        app.Fact("registration")!.Locations.Should().Contain(l => l.File.EndsWith("Program.cs"));
        app.ServiceName.Should().Be("M2LB.HendelseAdapter");
    }

    [Fact]
    public void WithoutTheWrapperSourceDetectionIsOnlyLikely()
    {
        var app = App(Analyze(Adapter), "M2LB.Hendelse.BiRK.Adapter");
        app.Detection.Should().Be(MessagingDetection.Likely);
        app.DetectionReason.Should().Contain("AddM2LbWolverine").And.Contain("not in the analyzed archives");
        app.Fact("registration")!.State.Should().Be(MessagingFactState.NotAssessable);
        app.Fact("transport")!.State.Should().Be(MessagingFactState.NotAssessable);
    }

    [Fact]
    public void ServiceBusOrEventHubAloneNeverDetectsWolverine()
    {
        var set = Analyze(new Dictionary<string, string>
        {
            ["Person/src/Worker/Worker.csproj"] = """<Project Sdk="Microsoft.NET.Sdk.Worker"><ItemGroup><PackageReference Include="Azure.Messaging.ServiceBus" Version="7.20.2" /><PackageReference Include="Azure.Messaging.EventHubs.Processor" Version="5.12.2" /></ItemGroup></Project>""",
            ["Person/src/Worker/Program.cs"] = "var processor = client.CreateProcessor(\"queue\"); var hub = new EventProcessorClient(store, \"g\", \"ns\", \"hub\", null);",
        });
        var app = set.Applications.Single();
        app.Detection.Should().Be(MessagingDetection.NotDetected);
        app.DetectionReason.Should().Contain("Service Bus or Event Hub usage alone is not Wolverine evidence");
        app.Fact("service-bus-sdk-consumer")!.Detail.Should().Contain("not by a Wolverine listener");
    }

    // ── Hendelse adapter architecture ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HendelseAdapterEventHubIsConsumedBySdk_WolverineOnlyPublishesErrorMetadata()
    {
        var app = App(Analyze(Adapter, Common), "M2LB.Hendelse.BiRK.Adapter");
        app.Fact("event-hub-consumer")!.Detail.Should().Contain("EventProcessorClient").And.Contain("not by a Wolverine listener");
        app.Routes.Should().ContainSingle().Which.Should().Match<MessagingRoute>(r => r.Direction == MessagingRouteDirection.Publish && r.MessageType == "BirkErrorMessage");
        app.Routes.Should().NotContain(r => r.Direction == MessagingRouteDirection.Listen, "Wolverine is not the Event Hub consumer");
        var route = app.Routes.Single();
        route.Endpoint.Should().Be("configuration ServiceBus:ErrorQueueName, default \"birk-adapter-errors\"");
        route.Senders.Should().Equal("WolverineErrorQueuePublisher");
        route.FailurePath.Should().ContainSingle().Which.Should().Contain("BirkCdcEventHandler (catch").And.Contain("IErrorQueuePublisher.PublishAsync → WolverineErrorQueuePublisher");
        app.Handlers.Should().BeEmpty("BirkCdcEventHandler is an Event Hub processor handler, not a Wolverine handler");
        app.HandlerMapping.Should().Be(MessagingFactState.NotApplicable);
        app.Fact("transport")!.Detail.Should().Contain("ServiceBus:FQDN").And.Contain("ConnectionStrings:ServiceBus").And.Contain("not read");
    }

    // ── Handler mapping, retry, outbox, error handling ────────────────────────────────────────────────────────────

    [Fact]
    public void ExactHandlerMappingRetryAndErrorPolicyAreConfiguredNotObserved()
    {
        var app = App(Analyze(Revisjon, Common), "M2LB.Revisjon.Worker");
        var listen = app.Routes.Single(r => r.Direction == MessagingRouteDirection.Listen);
        listen.Endpoint.Should().Be("configuration ServiceBus:QueueName");
        listen.MessageType.Should().Be("LeseloggHendelse");
        listen.HandlerMapping.Should().Be(MessagingFactState.Available);
        listen.Handlers.Should().Equal("LeseloggHendelseHandler.Handle");
        listen.Options.Should().Contain("ProcessInline");
        app.Handlers.Single().Location!.Line.Should().Be(5, "primary constructors are blanked without moving lines");
        var retry = app.FailureRules.Single(r => r.ExceptionType == "RequestFailedException");
        retry.Condition.Should().Be("ex => ex.Status >= 500 || ex.Status == 429");
        retry.Delays.Should().Equal("100.Milliseconds()", "500.Milliseconds()", "2.Seconds()", "10.Seconds()");
        app.RetryPolicy.Should().Be(MessagingFactState.Configured);
        app.ErrorHandling.Should().Be(MessagingFactState.Configured);
        app.Fact("error-policy")!.Detail.Should().Contain("MoveToErrorQueue").And.Contain("native dead-letter queue");
        app.Outbox.Should().Be(MessagingFactState.NotFound);
        app.Fact("telemetry-export")!.State.Should().Be(MessagingFactState.NotAssessable, "the observability setup is not in these fixtures");
    }

    [Fact]
    public void AmbiguousOrMissingHandlersAreNeverResolvedByFirstMatch()
    {
        var files = Revisjon;
        files["Revisjon/src/M2LB.Revisjon.Worker/SecondHandler.cs"] = "using Wolverine; public class LeseloggArchiveHandler { public Task Handle(LeseloggHendelse h) => Task.CompletedTask; }";
        var ambiguous = App(Analyze(files, Common), "M2LB.Revisjon.Worker").Routes.Single(r => r.Direction == MessagingRouteDirection.Listen);
        ambiguous.HandlerMapping.Should().Be(MessagingFactState.NotAssessable);
        ambiguous.MappingReason.Should().Contain("Ambiguous: 2 handlers").And.Contain("none is chosen");
        ambiguous.Handlers.Should().HaveCount(2);

        var none = Revisjon;
        none.Remove("Revisjon/src/M2LB.Revisjon.Worker/LeseloggHendelseHandler.cs");
        App(Analyze(none, Common), "M2LB.Revisjon.Worker").Routes.Single(r => r.Direction == MessagingRouteDirection.Listen).HandlerMapping.Should().Be(MessagingFactState.NotFound);
    }

    [Fact]
    public void HandlerOutsideEstablishedDiscoveryIsNotAssessable()
    {
        var files = Revisjon;
        files["Revisjon/src/M2LB.Revisjon.Worker/Program.cs"] = RevisjonProgram.Replace("options.Discovery.IncludeAssembly(typeof(LeseloggHendelseHandler).Assembly);", "");
        var app = App(Analyze(files, Common), "M2LB.Revisjon.Worker");
        app.Handlers.Single().InDiscoveryScope.Should().BeNull();
        app.Handlers.Single().DiscoveryReason.Should().Contain("Discovery scope not established");
        app.HandlerMapping.Should().Be(MessagingFactState.NotAssessable);
    }

    [Fact]
    public void DurableStorageIsConfiguredButNeverCalledWorking()
    {
        var files = Revisjon;
        files["Revisjon/src/M2LB.Revisjon.Worker/Program.cs"] = RevisjonProgram.Replace("options.OnException<Exception>()", "if (!string.IsNullOrWhiteSpace(connStr)) { options.PersistMessagesWithSqlServer(connStr, schema: \"wolverine\"); }\n    options.OnException<Exception>()");
        var app = App(Analyze(files, Common), "M2LB.Revisjon.Worker");
        app.Outbox.Should().Be(MessagingFactState.Configured);
        app.Fact("outbox")!.Detail.Should().Contain("SQL Server message storage (schema wolverine)").And.Contain("when !string.IsNullOrWhiteSpace(connStr)")
            .And.Contain("not evidence that the outbox is used successfully");
    }

    [Fact]
    public void AnalysisNeverReadsConfigurationFilesOrStoresSourceText()
    {
        var set = Analyze(Adapter, Common);
        var json = JsonSerializer.Serialize(set);
        json.Should().NotContain("SECRET_SAS_VALUE").And.NotContain("SharedAccessKey").And.NotContain("await bus.SendAsync(message)");
        SourceArchiveReader.IsAnalyzable("x/src/App/appsettings.Local.json").Should().BeFalse();
        SourceArchiveReader.IsAnalyzable("x/src/App/bin/Debug/App.cs").Should().BeFalse();
        set.Archives.Should().OnlyContain(a => a.Sha256.Length == 64);
    }

    [Fact]
    public void EntityNamesResolveFromSourceAndBaseAppsettingsButNeverFromSecrets()
    {
        var files = Revisjon;
        files["Revisjon/src/M2LB.Revisjon.Worker/appsettings.json"] = """
            { "ConnectionStrings": { "ServiceBus": "Endpoint=sb://x;SharedAccessKey=SECRET" }, "ServiceBus": { "QueueName": "leselogg", "SasKey": "should-not-be-read" } }
            """;
        files["Revisjon/src/M2LB.Revisjon.Worker/Sdk.cs"] = """
            using Azure.Messaging.ServiceBus;
            public sealed class LeseloggOptions { public string KoeName { get; set; } = "leselogg-audit"; }
            public sealed class Sender
            {
                public void Send(ServiceBusClient client, LeseloggOptions opts)
                {
                    var registrering = client.CreateSender("operasjonsregistrering");
                    var audit = client.CreateSender(opts.KoeName);
                    var processor = client.CreateProcessor("person.barn", "tjeneste-barnregistrert");
                }
            }
            """;
        var app = App(Analyze(files, Common), "M2LB.Revisjon.Worker");
        var listen = app.Routes.Single(r => r.Direction == MessagingRouteDirection.Listen);
        listen.EntityName.Should().Be("leselogg");
        listen.EntityNameSource.Should().StartWith("appsettings.json ServiceBus:QueueName").And.Contain("deployment settings may override");
        app.SdkRoutes.Should().Contain(r => r.Direction == MessagingRouteDirection.Publish && r.EntityName == "operasjonsregistrering" && r.EntityNameSource!.StartsWith("literal"));
        app.SdkRoutes.Should().Contain(r => r.EntityName == "leselogg-audit" && r.EntityNameSource!.Contains("default of LeseloggOptions.KoeName"));
        app.SdkRoutes.Should().Contain(r => r.Direction == MessagingRouteDirection.Listen && r.EntityName == "tjeneste-barnregistrert" && r.TopicName == "person.barn");
        app.SdkRoutes.Should().OnlyContain(r => r.Technology == "Azure SDK");
        System.Text.Json.JsonSerializer.Serialize(app).Should().NotContain("SECRET").And.NotContain("should-not-be-read");
    }

    // ── IQR contribution ──────────────────────────────────────────────────────────────────────────────────────────

    private sealed class NoSources : IEventHubMetadataSource, IEventHubConsumerGroupSource, ICheckpointEvidenceSource, ITelemetryEvidenceSource, IIntegrationNamespaceProbe
    {
        private static IntegrationEvidenceAdapterStatus Off() => NotConfiguredEvidence.Status("off", IntegrationEvidenceSource.AzureMetadata, IntegrationEvidenceState.NotConfigured, "Not configured (test).");
        public IntegrationEvidenceAdapterStatus Describe(IntegrationPlatform platform) => Off();
        public Task<EvidenceResult<EventHubRuntimeMetadata>> GetHubAsync(IntegrationPlatform p, string h, CancellationToken ct) => Task.FromResult(EvidenceResult<EventHubRuntimeMetadata>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.AzureMetadata, "off"));
        public Task<EvidenceResult<ConsumerGroupList>> ListAsync(IntegrationPlatform p, string h, CancellationToken ct) => Task.FromResult(EvidenceResult<ConsumerGroupList>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.AzureResourceManager, "off"));
        public Task<EvidenceResult<CheckpointEvidence>> GetAsync(IntegrationPlatform p, string h, string g, CancellationToken ct) => Task.FromResult(EvidenceResult<CheckpointEvidence>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.CheckpointStore, "off"));
        public Task<EvidenceResult<ConsumerTelemetry>> GetConsumerAsync(IntegrationPlatform p, string r, int w, CancellationToken ct) => Task.FromResult(EvidenceResult<ConsumerTelemetry>.Missing(IntegrationEvidenceState.NotConfigured, IntegrationEvidenceSource.ApplicationInsights, "off"));
        public Task<NamespaceProbeResult> ProbeAsync(string fqdn, CancellationToken ct) => Task.FromResult(new NamespaceProbeResult(true, true, true, "ok (test)", 1, DateTimeOffset.UtcNow, "Tls13"));
    }

    private sealed class Runtime(Func<ApplicationMessagingEvidence, ApplicationMessagingRuntime> respond) : IApplicationMessagingTelemetrySource
    {
        public int Calls;
        public Task<ApplicationMessagingRuntime> GetAsync(IntegrationPlatform? platform, ApplicationMessagingEvidence application, int windowHours, CancellationToken ct)
        { Calls++; return Task.FromResult(respond(application)); }
    }

    private static IntegrationAzureCredential Azure(bool enabled) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["IntegrationReview:Azure:Enabled"] = enabled.ToString() }).Build());

    private static IntegrationReviewEngine Engine(IApplicationMessagingTelemetrySource? runtime = null, bool azure = false)
    {
        var none = new NoSources();
        return new IntegrationReviewEngine(none, none, none, none, none, new HttpClient(), NullLogger<IntegrationReviewEngine>.Instance, runtime, Azure(azure));
    }

    private static IntegrationCatalog Catalog() => new()
    {
        EnvironmentId = "dev", Platforms = [M2lbDevIntegrationSeed.Platform("dev", DateTimeOffset.UtcNow)],
        Integrations = M2lbDevIntegrationSeed.Integrations("dev", DateTimeOffset.UtcNow).ToList(),
    };

    /// <summary>The adapter's analyzed evidence, explicitly bound (by a person) to the "Hendelse BiRK Adapter" consumer of the seed.</summary>
    private static ApplicationMessagingEvidenceSet Bound(params Dictionary<string, string>[] archives)
    {
        var set = Analyze(archives);
        var consumer = Catalog().Integrations.First(i => i.Consumer.DisplayName?.Contains("Hendelse", StringComparison.Ordinal) == true).Consumer.DisplayName;
        return set with { Applications = set.Applications.Select(a => a.ApplicationId is "M2LB.Hendelse.BiRK.Adapter" or "M2LB.Revisjon.Worker" ? a with { BoundConsumer = consumer } : a).ToList() };
    }

    private static readonly IntegrationReviewRunRequest Request = new() { EnvironmentId = "dev", EnvironmentName = "Dev" };

    [Fact]
    public async Task WolverineContributesConfigurationEvidenceButMakesNoRuntimeDomainReady()
    {
        var engine = Engine();
        var catalog = Catalog();
        var withMessaging = engine.Readiness(catalog, IntegrationContractSet.Empty, Bound(Adapter, Common));
        withMessaging.Domains.Should().BeEquivalentTo(engine.Readiness(catalog, IntegrationContractSet.Empty).Domains, "a package or configuration never changes domain readiness");
        var summary = withMessaging.ApplicationMessaging.Single(s => s.ApplicationId == "M2LB.Hendelse.BiRK.Adapter");
        summary.Detection.Should().Be(MessagingDetection.Confirmed);
        summary.BoundTopics.Should().BeGreaterThan(0);
        withMessaging.EvidenceAdapters.Should().NotContain(a => a.Adapter.Contains("messaging", StringComparison.OrdinalIgnoreCase), "application messaging is not a transport runtime source");

        var result = await engine.RunAsync(catalog, Request, IntegrationContractSet.Empty, [], Bound(Adapter, Common), CancellationToken.None);
        var checks = result.AllChecks.Where(c => c.CheckId.StartsWith("am-", StringComparison.Ordinal)).ToList();
        checks.Should().NotBeEmpty();
        checks.Should().NotContain(c => c.Status == IntegrationCheckStatus.Pass, "configuration is Detected/Configured, never Pass");
        checks.Single(c => c.CheckId == "am-detected" && c.SubjectId.EndsWith("TvangsProtokoll")).Status.Should().Be(IntegrationCheckStatus.Detected);
        checks.Where(c => c.CheckId == "am-event-hub-consumer").Should().OnlyContain(c => c.Domain == IntegrationReviewDomain.Configuration && c.Evidence.Contains("EventProcessorClient"));
        checks.Where(c => c.CheckId == "am-failure-publishing").Should().OnlyContain(c => c.Domain == IntegrationReviewDomain.ErrorHandling && c.Evidence.Contains("BirkCdcEventHandler"));
        checks.Where(c => c.Domain is IntegrationReviewDomain.MessageFlow or IntegrationReviewDomain.Performance).Should().OnlyContain(c => c.Status == IntegrationCheckStatus.NotAssessed);
        checks.Where(c => c.CheckId is "am-retry-observed" or "am-processing-duration").Should().OnlyContain(c => c.Status == IntegrationCheckStatus.NotAssessed);
        checks.Where(c => c.Provenance == IntegrationEvidenceSource.SourceCode).Should().OnlyContain(c => c.Status != IntegrationCheckStatus.Observed);
        var baseline = await engine.RunAsync(catalog, Request, IntegrationContractSet.Empty, [], null, CancellationToken.None);
        result.Freshness.Should().Be(baseline.Freshness, "source facts are not runtime evidence and never change run freshness");
        result.EvidenceSources.Should().Contain(IntegrationEvidenceSource.SourceCode);
    }

    [Fact]
    public async Task UnboundApplicationsContributeNothingToTopics()
    {
        var result = await Engine().RunAsync(Catalog(), Request, IntegrationContractSet.Empty, [], Analyze(Adapter, Common), CancellationToken.None);
        result.AllChecks.Should().NotContain(c => c.CheckId.StartsWith("am-", StringComparison.Ordinal), "bindings are explicit; names are never matched automatically");
    }

    [Fact]
    public async Task MissingRuntimeTelemetryIsNotAssessedNeverZeroOrPass()
    {
        var runtime = new Runtime(a => new() { ApplicationId = a.ApplicationId, State = IntegrationEvidenceState.NotConfigured, Reason = "Azure runtime evidence is disabled for this BirkNext instance.", WindowHours = 24 });
        var result = await Engine(runtime).RunAsync(Catalog(), Request, IntegrationContractSet.Empty, [], Bound(Revisjon, Common), CancellationToken.None);
        var execution = result.AllChecks.Where(c => c.CheckId == "am-handler-execution").ToList();
        execution.Should().NotBeEmpty().And.OnlyContain(c => c.Status == IntegrationCheckStatus.NotAssessed && c.Explanation.Contains("disabled") && c.Evidence == "");
        result.AllChecks.Where(c => c.CheckId == "am-handler-errors").Should().OnlyContain(c => c.Status == IntegrationCheckStatus.NotAssessed);
        result.ApplicationMessagingRuntime.Single().HandlerInformationLogs.Should().BeNull("unknown is never zero");
        runtime.Calls.Should().Be(1, "telemetry is read once per application, not per topic");
    }

    [Fact]
    public async Task ObservedHandlerLogsShowActivityNotCompletion_StaleIsNotCurrent()
    {
        ApplicationMessagingRuntime Observed(ApplicationMessagingEvidence a, DateTimeOffset last) => new()
        {
            ApplicationId = a.ApplicationId, State = IntegrationEvidenceState.Available, Reason = "test", CapturedAt = DateTimeOffset.UtcNow, WindowHours = 24,
            HandlerInformationLogs = 40, HandlerWarningLogs = 1, HandlerErrorLogs = 2, LastHandlerLog = last,
            Freshness = IntegrationReviewLabels.FreshnessOf(last, DateTimeOffset.UtcNow, 24),
        };
        var current = await Engine(new Runtime(a => Observed(a, DateTimeOffset.UtcNow.AddMinutes(-5)))).RunAsync(Catalog(), Request, IntegrationContractSet.Empty, [], Bound(Revisjon, Common), CancellationToken.None);
        var execution = current.AllChecks.First(c => c.CheckId == "am-handler-execution");
        execution.Status.Should().Be(IntegrationCheckStatus.Observed);
        execution.Explanation.Should().Contain("do not prove completion or business success");
        current.AllChecks.First(c => c.CheckId == "am-handler-errors").Status.Should().Be(IntegrationCheckStatus.Observed);
        current.AllChecks.First(c => c.CheckId == "am-processing-duration").Status.Should().Be(IntegrationCheckStatus.NotAssessed, "no measured duration exists");

        var stale = await Engine(new Runtime(a => Observed(a, DateTimeOffset.UtcNow.AddDays(-3)))).RunAsync(Catalog(), Request, IntegrationContractSet.Empty, [], Bound(Revisjon, Common), CancellationToken.None);
        var staleExecution = stale.AllChecks.First(c => c.CheckId == "am-handler-execution");
        staleExecution.Status.Should().Be(IntegrationCheckStatus.NoRecentEvidence);
        staleExecution.Freshness.Should().Be(IntegrationEvidenceItemFreshness.Historical);
    }

    [Fact]
    public async Task RunSnapshotIsImmutableWhenEvidenceIsReanalyzedOrRebound()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var store = new ApplicationMessagingStore(db, NullLogger<ApplicationMessagingStore>.Instance);
        (await store.AnalyzeAsync("dev", [("adapter.zip", Zip(Adapter)), ("common.zip", Zip(Common))])).Error.Should().BeNull();
        var consumer = Catalog().Integrations.First(i => i.Consumer.DisplayName?.Contains("Hendelse", StringComparison.Ordinal) == true).Consumer.DisplayName;
        await store.BindAsync("dev", "M2LB.Hendelse.BiRK.Adapter", consumer);
        var result = await Engine().RunAsync(Catalog(), Request, IntegrationContractSet.Empty, [], await store.GetAsync("dev"), CancellationToken.None);
        var stored = JsonSerializer.Serialize(result);

        await store.BindAsync("dev", "M2LB.Hendelse.BiRK.Adapter", null);
        (await store.AnalyzeAsync("dev", [("adapter.zip", Zip(Adapter))])).Error.Should().BeNull();
        var reloaded = JsonSerializer.Deserialize<IntegrationReviewResult>(stored)!;
        reloaded.ApplicationMessagingSnapshot!.Applications.Single(a => a.ApplicationId == "M2LB.Hendelse.BiRK.Adapter").Detection.Should().Be(MessagingDetection.Confirmed);
        reloaded.ApplicationMessagingSnapshot.Applications.Single(a => a.ApplicationId == "M2LB.Hendelse.BiRK.Adapter").BoundConsumer.Should().Be(consumer);
        (await store.GetAsync("dev"))!.Applications.Single(a => a.ApplicationId == "M2LB.Hendelse.BiRK.Adapter").Detection.Should().Be(MessagingDetection.Likely, "the current evidence changed; the run did not");
    }

    [Fact]
    public async Task ReanalysisKeepsExplicitBindingsAndRejectsInvalidArchives()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var store = new ApplicationMessagingStore(db, NullLogger<ApplicationMessagingStore>.Instance);
        await store.AnalyzeAsync("dev", [("adapter.zip", Zip(Adapter))]);
        await store.BindAsync("dev", "M2LB.Hendelse.BiRK.Adapter", "Hendelse BiRK Adapter");
        await store.AnalyzeAsync("dev", [("adapter.zip", Zip(Adapter)), ("common.zip", Zip(Common))]);
        (await store.GetAsync("dev"))!.Applications.Single(a => a.ApplicationId == "M2LB.Hendelse.BiRK.Adapter").BoundConsumer.Should().Be("Hendelse BiRK Adapter");
        (await store.AnalyzeAsync("dev", [("notes.zip", "not a zip"u8.ToArray())])).Error.Should().Contain("not a valid zip");
    }

    [Fact]
    public void TelemetryQueryIsBoundedAggregateAndEscaped()
    {
        var query = LogAnalyticsApplicationMessagingSource.HandlerLogQuery("M2LB.\"Revisjon", ["LeseloggHendelseHandler"]);
        query.Should().Contain("summarize").And.Contain("AppRoleName == \"M2LB.\\\"Revisjon\"").And.Contain("endswith \".LeseloggHendelseHandler\"");
        query.Should().NotContain("project Message").And.NotContain("take");
    }

    [Fact]
    public void ApplicationMessagingSourcesHaveNoSendReceiveOrMutationPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend/BirkNext.Api/Services/Integrations/ApplicationMessaging"))) dir = dir.Parent;
        foreach (var file in Directory.GetFiles(Path.Combine(dir!.FullName, "backend/BirkNext.Api/Services/Integrations/ApplicationMessaging"), "*.cs"))
        {
            var source = File.ReadAllText(file);
            source.Should().NotContainAny(["ServiceBusSender", "ServiceBusReceiver", "CreateSender(", "EventHubProducerClient", "ReceiveMessagesAsync", "DeadLetterMessageAsync", "UploadAsync", "ExtractToDirectory"],
                $"{Path.GetFileName(file)} is read-only");
        }
    }
}
