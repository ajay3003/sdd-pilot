using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>Service Bus transport presentation: compact platform summary, topology tree, read-only test result in separate layers.</summary>
public sealed class ServiceBusUiTests : BunitContext
{
    private const string PlatformId = "dev:servicebus:m2lb";
    private readonly FakeIntegrationCatalogApi _api = new() { Readiness = M2lbFixture.Readiness(), Result = M2lbFixture.Result() };

    private static ServiceBusEntityExpectation Queue(string name) => new()
    {
        EntityType = ServiceBusEntityType.Queue, Name = name, Publishers = ["M2LB.Hendelse.Api"], Consumers = ["M2LB.Revisjon.Worker"], MaxDeliveryCount = 10, LockDuration = "PT1M", DefaultMessageTimeToLive = "P14D",
    };

    private static IntegrationPlatform ServiceBusPlatform => new()
    {
        Id = PlatformId, EnvironmentId = "dev", Name = "M2LB DEV Service Bus", Kind = IntegrationKind.ServiceBus, Namespace = "sbns-m2lb-dev-nwe-001", ResourceGroup = "rg-m2lb-dev-shared-nwe",
        ServiceBusTopology = new ServiceBusTopology
        {
            Source = "Developer-side audit of Terraform (seeded)", NamespaceReceivers = ["M2LB.Hendelse.BiRK.Adapter"],
            Entities =
            [
                Queue("leselogg"), Queue("birk-adapter-errors"),
                new() { EntityType = ServiceBusEntityType.Topic, Name = "person.barn", Publishers = ["M2LB.Person.Api"], DefaultMessageTimeToLive = "P14D" },
                new() { EntityType = ServiceBusEntityType.Subscription, Topic = "person.barn", Name = "tjeneste-barnregistrert", Consumers = ["M2LB.Tjeneste.Api"], MaxDeliveryCount = 10 },
            ],
        },
    };

    private static ServiceBusEvidenceCheck Observed(DateTimeOffset captured) => new()
    {
        PlatformId = PlatformId, Namespace = "sbns-m2lb-dev-nwe-001", CompletedAt = captured, OverallState = ServiceBusEvidenceState.IssueDetected, Queues = 2, Topics = 1, Subscriptions = 1, WindowHours = 24,
        Configuration = [new() { CheckId = "sb-topology", Title = "Configured topology", State = ServiceBusCheckState.Configured, Detail = "2 queue(s), 1 topic(s), 1 subscription(s)." }],
        Runtime = new()
        {
            PlatformId = PlatformId, Namespace = "sbns-m2lb-dev-nwe-001", State = IntegrationEvidenceState.Available, CapturedAt = captured, NamespaceStatus = "Active",
            Entities =
            [
                new() { EntityType = ServiceBusEntityType.Queue, Name = "leselogg", ActiveMessageCount = 12, DeadLetterMessageCount = 2, ScheduledMessageCount = 0 },
                new() { EntityType = ServiceBusEntityType.Queue, Name = "birk-adapter-errors", ActiveMessageCount = 0, DeadLetterMessageCount = 0, ScheduledMessageCount = 0 },
                new() { EntityType = ServiceBusEntityType.Topic, Name = "person.barn" },
            ],
        },
        RuntimeChecks =
        [
            new() { CheckId = "sb-entity", EntityType = ServiceBusEntityType.Queue, Entity = "leselogg", State = ServiceBusCheckState.Observed },
            new() { CheckId = "sb-entity", EntityType = ServiceBusEntityType.Queue, Entity = "birk-adapter-errors", State = ServiceBusCheckState.Observed },
            new() { CheckId = "sb-entity", EntityType = ServiceBusEntityType.Topic, Entity = "person.barn", State = ServiceBusCheckState.Observed },
            new() { CheckId = "sb-entity", EntityType = ServiceBusEntityType.Subscription, Entity = "person.barn/tjeneste-barnregistrert", State = ServiceBusCheckState.NotFound, Detail = "not in the namespace" },
            new() { CheckId = "sb-property-maxDeliveryCount", EntityType = ServiceBusEntityType.Queue, Entity = "leselogg", Title = "leselogg MaxDeliveryCount", Expected = "10", Observed = "10", State = ServiceBusCheckState.Pass },
        ],
        Routes =
        [
            new() { Application = "M2LB.Tjeneste.Api", Technology = "Wolverine", Direction = "Publish", MessageType = "LeseloggHendelseEvent", Entity = "leselogg", EntityType = ServiceBusEntityType.Queue,
                EntitySource = "literal (Program.cs:130)", Configuration = ServiceBusCheckState.Matched, Access = ServiceBusCheckState.Matched, Runtime = ServiceBusCheckState.Observed },
        ],
        Findings = ["Expected subscription person.barn/tjeneste-barnregistrert: not in the namespace"],
        Missing = ["Azure Monitor metrics (throughput, oldest-message age, server errors) are not read in this build.", "Application Insights processing evidence for M2LB.Revisjon.Worker."],
    };

    public ServiceBusUiTests()
    {
        _api.Catalog = M2lbFixture.Catalog() with { Platforms = [.. M2lbFixture.Catalog().Platforms, ServiceBusPlatform] };
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<IntegrationsPane> Pane(string? focus = null) =>
        Render<IntegrationsPane>(p => p.Add(c => c.Profile, new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development }).Add(c => c.Focus, focus));

    [Fact]
    public void PlatformSummaryIsCompactAndSeparateFromEventHub()
    {
        var cut = Pane();
        var platforms = cut.FindAll("[data-testid=ip-platform]");
        platforms.Should().HaveCount(2);
        var sb = cut.Find("[data-testid=sb]");
        sb.QuerySelector("[data-testid=sb-configuration]")!.TextContent.Should().Be("Configured");
        sb.QuerySelector("[data-testid=sb-queues]")!.TextContent.Should().Be("2");
        sb.QuerySelector("[data-testid=sb-topics]")!.TextContent.Should().Be("1");
        sb.QuerySelector("[data-testid=sb-subscriptions]")!.TextContent.Should().Be("1");
        sb.QuerySelector("[data-testid=sb-runtime]")!.TextContent.Should().Be("Not configured");
        sb.QuerySelector("[data-testid=sb-test]")!.GetAttribute("aria-label").Should().Be("Test Service Bus M2LB DEV Service Bus (read-only)");
        platforms[1].TextContent.Should().NotContain("Topic prefix").And.NotContain("Checkpoint store", "no Event Hub concepts on a Service Bus platform");
        cut.FindAll("[data-testid=ip-row]").Should().HaveCount(16, "Service Bus entities are not business-topic rows");
    }

    [Fact]
    public void TopologyTreeIsCollapsedAndKeyboardReachable()
    {
        var cut = Pane();
        var toggle = cut.Find("[data-testid=sb-topology] > button");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.Click();
        cut.Find("[data-testid=sb-queue-table]").TextContent.Should().Contain("leselogg").And.Contain("MaxDeliveryCount 10").And.Contain("M2LB.Revisjon.Worker");
        cut.Find("[data-testid=sb-topic-tree]").TextContent.Should().Contain("person.barn").And.Contain("tjeneste-barnregistrert").And.Contain("consumer M2LB.Tjeneste.Api");
        cut.Find("[data-testid=sb-topology] .sb-tablewrap").GetAttribute("tabindex").Should().Be("0");
    }

    [Fact]
    public void TestResultShowsConfigurationRuntimeOperationalRoutesAndMissingSeparately()
    {
        _api.ServiceBusCheck = _ => Observed(DateTimeOffset.UtcNow);
        var cut = Pane();
        cut.Find("[data-testid=sb-test]").Click();
        cut.WaitForElement("[data-testid=sb-result]");
        _api.Calls.Should().Contain("servicebus-evidence:" + PlatformId);
        cut.Find("[data-testid=sb-overall]").TextContent.Should().Be("Issue detected");
        cut.Find("[data-testid=sb-result-configuration]").TextContent.Should().Contain("Configured topology").And.Contain("1 of 1 match");
        var runtime = cut.Find("[data-testid=sb-result-runtime]").TextContent;
        runtime.Should().Contain("2 of 2 queues observed").And.Contain("not found: person.barn/tjeneste-barnregistrert");
        var counts = cut.Find("[data-testid=sb-result-counts]").TextContent;
        counts.Should().Contain("Observed: 12").And.Contain("Observed: 2").And.Contain("Dead-letter messages observed").And.NotContain("Fail");
        var route = cut.Find("[data-testid=sb-route]");
        route.TextContent.Should().Contain("Matched").And.Contain("Observed").And.Contain("Not assessed");
        cut.Find("[data-testid=sb-result-findings]").TextContent.Should().Contain("tjeneste-barnregistrert");
        cut.Find("[data-testid=sb-result-missing]").TextContent.Should().Contain("Application Insights processing evidence");
        cut.FindAll("[data-testid=sb-stale]").Should().BeEmpty();
    }

    [Fact]
    public void RuntimeNotConfiguredIsNotAFailureAndOldSnapshotsAreStale()
    {
        _api.ServiceBusCheck = _ => Observed(DateTimeOffset.UtcNow) with
        {
            OverallState = ServiceBusEvidenceState.Partial, Findings = [], RuntimeChecks = [],
            Runtime = new() { State = IntegrationEvidenceState.NotConfigured, Reason = "Azure runtime evidence is disabled for this BirkNext instance.", CapturedAt = DateTimeOffset.UtcNow },
        };
        var cut = Pane();
        cut.Find("[data-testid=sb-test]").Click();
        cut.WaitForElement("[data-testid=sb-result]");
        cut.Find("[data-testid=sb-overall]").TextContent.Should().Be("Partial");
        cut.Find("[data-testid=sb-result-runtime]").TextContent.Should().Contain("Not configured").And.Contain("disabled");
        cut.FindAll("[data-testid=sb-result-counts]").Should().BeEmpty("no counts are shown without runtime evidence — never zeros");

        ServiceBusPresentation.IsStale(Observed(DateTimeOffset.UtcNow.AddDays(-3)), DateTimeOffset.UtcNow).Should().BeTrue();
    }

    [Fact]
    public void ConfigureRuntimeShowsOnlyServiceBusFieldsAndDeepLinkOpensIt()
    {
        var cut = Pane(ServiceBusPresentation.Focus);
        var form = cut.Find("[data-testid=ip-runtime-form]");
        form.QuerySelector("[data-testid=ip-runtime-subscription]").Should().NotBeNull();
        form.QuerySelector("[data-testid=ip-runtime-metadata]").Should().BeNull("Event Hub metadata is not a Service Bus field");
        form.QuerySelector("[data-testid=ip-runtime-checkpoint]").Should().BeNull();
        form.TextContent.Should().Contain("sbns-m2lb-dev-nwe-001").And.Contain("no message is read");
        _api.SavedPlatforms.Should().BeEmpty();
    }

    private IRenderedComponent<IntegrationQualityReview> Iqr()
    {
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development } });
        Services.AddSingleton(context.Object);
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton<IReportExportService, ReportExportService>();
        Services.AddSingleton<RuntimeReviewSessionService>();
        return Render<IntegrationQualityReview>();
    }

    [Fact]
    public void PreRunShowsServiceBusTransportAndLinksToItsRuntimeConfiguration()
    {
        _api.Readiness = _api.Readiness with
        {
            ServiceBus = [new() { PlatformId = PlatformId, PlatformName = "M2LB DEV Service Bus", Queues = 4, Topics = 11, Subscriptions = 8, RoutesMatched = 12, RoutesTotal = 12,
                RuntimeState = IntegrationEvidenceState.NotConfigured, RuntimeReason = "Azure runtime evidence is disabled for this BirkNext instance." }],
        };
        var cut = Iqr();
        var platform = cut.Find("[data-testid=iqr-servicebus-platform]");
        platform.TextContent.Should().Contain("4 queue(s) · 11 topic(s) · 8 subscription(s)").And.Contain("code routes 12 of 12 matched");
        cut.Find("[data-testid=iqr-servicebus-topology]").TextContent.Should().Be("Configured");
        cut.Find("[data-testid=iqr-servicebus-runtime]").TextContent.Should().Be("Not configured");
        cut.Find("[data-testid=iqr-action-servicebus-runtime]").GetAttribute("href").Should().EndWith("focus=servicebus");
        cut.Find("[data-testid=iqr-evidence-count]").TextContent.Should().Contain("0 of 4 available", "Service Bus is not an Event Hub runtime source");
        cut.Find("[data-testid=iqr-run]").HasAttribute("disabled").Should().BeFalse("limitations never block the run");
    }

    [Fact]
    public void PostRunAndExportUseTheSnapshotWithoutSecrets()
    {
        _api.Result = _api.Result! with { ServiceBusSnapshot = [Observed(new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero))] };
        var cut = Iqr();
        cut.Find("[data-testid=iqr-run]").Click();
        cut.WaitForElement("[data-testid=iqr-result]");
        cut.Find("[data-testid=iqr-result-servicebus-platform]").TextContent.Should().Contain("Issue detected").And.Contain("1 of 1 code route(s) matched").And.Contain("dead-letter 2");
        var html = new ReportExportService().ExportIntegrationReview(_api.Result, "BirkNext");
        html.Should().Contain("Service Bus · sbns-m2lb-dev-nwe-001").And.Contain("leselogg MaxDeliveryCount").And.Contain("Handler execution");
        html.Should().NotContain("SharedAccessKey").And.NotContain("Endpoint=sb://");
    }

    // ── Azure Monitor metrics and route-analysis state on the evidence card ──────────────────────────────────────

    [Fact]
    public void ObservedMetricsShowValuesProvenanceAndNoPass()
    {
        _api.ServiceBusCheck = _ => Observed(DateTimeOffset.UtcNow) with
        {
            RouteAnalysis = ServiceBusRouteAnalysis.Current,
            Metrics = new ServiceBusMetricsEvidence
            {
                State = IntegrationEvidenceState.Available, WindowHours = 24, CapturedAt = DateTimeOffset.UtcNow,
                Metrics = [new("IncomingMessages", "Total", 120, "Count"), new("OutgoingMessages", "Total", 118, "Count"), new("ServerErrors", "Total", 0, "Count"),
                           new("ActiveMessages", "Average", 3.5, "Count"), new("ActiveMessages", "Maximum", 9, "Count")],
            },
        };
        var cut = Pane();
        cut.Find("[data-testid=sb-test]").Click();
        cut.WaitForElement("[data-testid=sb-result]");

        cut.Find("[data-testid=sb-result-metrics-provenance]").TextContent.Should().Contain("Azure Monitor").And.Contain("last 24 h");
        var metrics = cut.Find("[data-testid=sb-result-metrics]").TextContent;
        metrics.Should().Contain("Observed: 120").And.Contain("Observed: 0").And.Contain("Observed: 3.5 / 9").And.Contain("Not reported").And.Contain("Oldest-message age").And.Contain("Not available");
        metrics.Should().NotContain("Pass").And.NotContain("Fail");
        cut.Find("[data-testid=sb-result-provenance]").TextContent.Should().Contain("Azure Resource Manager management metadata (GET only)");
        cut.Find("[data-testid=sb-result-route-status]").TextContent.Should().Contain("Matched");
    }

    [Fact]
    public void OldAnalysisShowsNeedsReanalysisAndUnreadMetricsShowTheirState()
    {
        _api.ServiceBusCheck = _ => Observed(DateTimeOffset.UtcNow) with
        {
            RouteAnalysis = ServiceBusRouteAnalysis.NeedsReanalysis, Routes = [],
            Metrics = new ServiceBusMetricsEvidence { State = IntegrationEvidenceState.NotConfigured, Reason = "Azure runtime evidence is disabled for this BirkNext instance (IntegrationReview:Azure:Enabled is not true)." },
        };
        var cut = Pane();
        cut.Find("[data-testid=sb-test]").Click();
        cut.WaitForElement("[data-testid=sb-result]");

        var routes = cut.Find("[data-testid=sb-result-route-status]").TextContent;
        routes.Should().Contain("Needs re-analysis").And.Contain("re-analyze it").And.NotContain("0 of 0");
        cut.Find("[data-testid=sb-result-metrics]").TextContent.Should().Contain("Not configured").And.Contain("IntegrationReview:Azure:Enabled");
    }
}
