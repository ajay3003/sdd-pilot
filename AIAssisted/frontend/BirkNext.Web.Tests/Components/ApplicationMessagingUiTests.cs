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

/// <summary>Application messaging (Wolverine) presentation: compact, text badges, configuration never shown as runtime, explicit binding.</summary>
public sealed class ApplicationMessagingUiTests : BunitContext
{
    private readonly FakeIntegrationCatalogApi _api = new() { Catalog = M2lbFixture.Catalog(), Readiness = M2lbFixture.Readiness(), Result = M2lbFixture.Result() };

    private static ApplicationMessagingEvidenceSet Set(string? adapterConsumer = null) => new()
    {
        EnvironmentId = "dev", AnalyzedAt = new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero),
        Archives = [new SourceArchive("M2LB.zip", new string('a', 64), 803), new SourceArchive("M2LB.Common.zip", new string('b', 64), 35)],
        Applications =
        [
            new()
            {
                ApplicationId = "M2LB.Hendelse.BiRK.Adapter", ServiceName = "M2LB.HendelseAdapter", Detection = MessagingDetection.Confirmed, DetectionReason = "Wolverine registration in source and a package chain to WolverineFx.",
                HandlerMapping = MessagingFactState.NotApplicable, RetryPolicy = MessagingFactState.NotFound, Outbox = MessagingFactState.NotFound, ErrorHandling = MessagingFactState.NotFound, BoundConsumer = adapterConsumer,
                Facts =
                [
                    new() { Id = "package", Label = "Wolverine package", State = MessagingFactState.Detected, Detail = "M2LB.Common.Messaging 1.0.10 → WolverineFx 6.33.0", Source = IntegrationEvidenceSource.PackageManifest, Locations = [new("HendelseAdapter/src/M2LB.Hendelse.BiRK.Adapter/M2LB.Hendelse.BiRK.Adapter.csproj", 1)] },
                    new() { Id = "event-hub-consumer", Label = "Event Hub consumption", State = MessagingFactState.Detected, Detail = "Event Hub is consumed with the Azure.Messaging.EventHubs SDK (EventProcessorClient), not by a Wolverine listener.", Locations = [new("HendelseAdapter/src/M2LB.Hendelse.BiRK.Adapter/Program.cs", 147)] },
                ],
                Routes = [new() { Direction = MessagingRouteDirection.Publish, MessageType = "BirkErrorMessage", EndpointKind = MessagingEndpointKind.Queue, Endpoint = "configuration ServiceBus:ErrorQueueName, default \"birk-adapter-errors\"", Senders = ["WolverineErrorQueuePublisher"], FailurePath = ["BirkCdcEventHandler (catch) → IErrorQueuePublisher.PublishAsync → WolverineErrorQueuePublisher"] }],
            },
            new()
            {
                ApplicationId = "M2LB.Revisjon.Worker", Detection = MessagingDetection.Confirmed, HandlerMapping = MessagingFactState.Available, RetryPolicy = MessagingFactState.Configured,
                Outbox = MessagingFactState.NotFound, ErrorHandling = MessagingFactState.Configured,
                Handlers = [new() { Type = "LeseloggHendelseHandler", Method = "Handle", MessageType = "LeseloggHendelse", InDiscoveryScope = true }],
            },
            new() { ApplicationId = "M2LB.Person.Api", Detection = MessagingDetection.NotDetected, HandlerMapping = MessagingFactState.NotApplicable },
        ],
    };

    public ApplicationMessagingUiTests()
    {
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<IntegrationsPane> Pane() =>
        Render<IntegrationsPane>(p => p.Add(c => c.Profile, new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development }));

    private static string Overview(IRenderedComponent<IntegrationsPane> cut, string key) => cut.Find($"[data-testid=am-overview-{key}] .am-badge").TextContent;

    [Fact]
    public void WithoutAnalyzedSourceEverythingIsNotAssessedAndNothingIsAbsent()
    {
        var cut = Pane();
        Overview(cut, "source").Should().Be("Not analyzed");
        Overview(cut, "wolverine").Should().Be("Not assessed");
        Overview(cut, "runtime").Should().Be("Not assessed");
        Overview(cut, "transport").Should().Be("Separate");
        cut.Find("[data-testid=am-analysis]").TextContent.Should().Contain("not evidence that Wolverine is absent");
        cut.Find("[data-testid=am-empty]").TextContent.Should().Be("No application source analyzed yet.");
        cut.Find("[data-testid=am-note]").GetAttribute("role").Should().Be("note");
        cut.Find("[data-testid=am-note]").TextContent.Should().Contain("Event Hub and Service Bus transport evidence is separate").And.Contain("not that a handler ran");
        cut.Find("[data-testid=am] .am-overview").TextContent.Should().NotContainAny("Failed", "Absent", "Unsupported");
    }

    [Fact]
    public void SourceComesFromSourceAnalysisSnapshotsNotAnUpload()
    {
        var cut = Pane();
        cut.FindAll("[data-testid=am] input[type=file]").Should().BeEmpty("source is uploaded only in Source Analysis");
        cut.Find("[data-testid=am-source-gate]").TextContent.Should().Contain("Messaging source").And.Contain("Required for source checks");
        cut.Find("[data-testid=am-primary]").Id.Should().Be(ServiceBusPlatformPanel.MessagingUploadId);
        cut.Find("[data-testid=am-use-scope]").HasAttribute("disabled").Should().BeTrue("nothing is chosen until the user picks a snapshot");
        cut.Find("[data-testid=am-use-scope]").TextContent.Should().Be("Use messaging evidence from this snapshot");
        cut.Find("[data-testid=am-analysis]").TextContent.Should().Contain("Wolverine messaging only").And.Contain("Source Analysis snapshots");

        _api.Messaging = Set();
        Pane().Find("[data-testid=am-use-scope]").TextContent.Should().Be("Rebuild messaging evidence from this scope");
    }

    [Fact]
    public void AnalyzedWithWolverineIsDetectedConfigurationNeverHandlerExecution()
    {
        _api.Messaging = Set();
        var cut = Pane();
        Overview(cut, "source").Should().Be("Analyzed");
        Overview(cut, "wolverine").Should().Be("Detected");
        cut.Find("[data-testid=am-analysis]").TextContent.Should().Contain("In 2 application(s)").And.Contain("Configured is not a handled message");
        Overview(cut, "runtime").Should().Be("Not assessed", "source never becomes runtime processing");
        cut.FindAll("[data-testid=am-runtime]").Should().OnlyContain(r => r.TextContent == "Not assessed");
    }

    [Fact]
    public void AnalyzedWithoutWolverineIsNotDetectedNotAbsentOrFailed()
    {
        _api.Messaging = Set() with { Applications = [new() { ApplicationId = "M2LB.Person.Api", Detection = MessagingDetection.NotDetected, HandlerMapping = MessagingFactState.NotApplicable }] };
        var cut = Pane();
        Overview(cut, "source").Should().Be("Analyzed");
        Overview(cut, "wolverine").Should().Be("Not detected");
        cut.Find("[data-testid=am-analysis]").TextContent.Should().Contain("package wiring outside it is not visible");
        cut.Find("[data-testid=am-overview-wolverine] .am-badge").ClassList.Should().Contain("am-badge-muted");
        Overview(cut, "runtime").Should().Be("Not assessed");
    }

    [Fact]
    public void TransportEvidenceNeverBecomesWolverineOrHandlerEvidence()
    {
        // The platforms carry Event Hub and Service Bus transport settings; the messaging section still reports no Wolverine or handler evidence.
        _api.Catalog = M2lbFixture.SeededCatalog(azureEnabled: true);
        var cut = Pane();
        Overview(cut, "transport").Should().Be("Separate");
        cut.Find("[data-testid=am-analysis]").TextContent.Should().Contain("never counts as Wolverine handler evidence");
        Overview(cut, "wolverine").Should().Be("Not assessed");
        Overview(cut, "runtime").Should().Be("Not assessed");
        cut.FindAll("[data-testid=am-app]").Should().BeEmpty();
    }

    [Fact]
    public void CompactBadgesAreTextAndRuntimeIsNeverImpliedByConfiguration()
    {
        _api.Messaging = Set();
        var cut = Pane();
        var apps = cut.FindAll("[data-testid=am-app]");
        apps.Select(a => a.GetAttribute("data-application")).Should().Equal("M2LB.Hendelse.BiRK.Adapter", "M2LB.Person.Api", "M2LB.Revisjon.Worker");
        var revisjon = apps[2];
        revisjon.QuerySelector("[data-testid=am-detection]")!.TextContent.Should().Be("Detected");
        revisjon.QuerySelector("[data-testid=am-mapping]")!.TextContent.Should().Be("Available");

        cut.FindAll("[data-testid=am-runtime]").Should().OnlyContain(r => r.TextContent == "Not assessed");
        apps[1].TextContent.Should().Contain("Not found");
        cut.Find("[data-testid=am-analysis]").TextContent.Should().Contain("M2LB.zip").And.Contain("files");
        cut.Markup.Should().NotContain(">Pass<");
    }

    [Fact]
    public void EvidenceDisclosureShowsProvenanceArchitectureAndMissingRuntime()
    {
        _api.Messaging = Set();
        var cut = Pane();
        var disclosure = cut.Find("[data-testid=am-source]");
        disclosure.QuerySelector(".disclosure-body")!.HasAttribute("hidden").Should().BeTrue();
        disclosure.QuerySelector("button")!.GetAttribute("aria-expanded").Should().Be("false");
        disclosure.QuerySelector("button")!.Click();
        var body = cut.Find("[data-testid=am-detail]");
        body.QuerySelector("[data-testid=am-meaning]")!.TextContent.Should().Contain("Wolverine registration in source");
        body.QuerySelector("[data-testid=am-facts]")!.TextContent.Should().Contain("not by a Wolverine listener").And.Contain("Program.cs:147").And.Contain("Package reference");
        body.QuerySelector("[data-testid=am-routes]")!.TextContent.Should().Contain("BirkErrorMessage").And.Contain("on failure: BirkCdcEventHandler (catch)");
        body.QuerySelector("[data-testid=am-runtime-evidence]")!.TextContent.Should().Contain("Handler execution").And.Contain("Retry activity").And.Contain("Processing duration").And.Contain("Not assessed");
    }

    [Fact]
    public void BindingIsExplicitSavedImmediatelyAndShownOnTheIntegration()
    {
        _api.Messaging = Set();
        var consumer = _api.Catalog.Integrations.First(i => i.Consumer.DisplayName?.Contains("Hendelse", StringComparison.Ordinal) == true).Consumer.DisplayName!;
        var cut = Pane();
        cut.FindAll("[data-testid=am-binding]")[0].Change(consumer);
        _api.Bindings.Should().Equal(("M2LB.Hendelse.BiRK.Adapter", consumer));
        cut.Find("[data-testid=am-status]").TextContent.Should().StartWith("Saved.");
        var row = cut.FindAll("[data-testid=ip-row]").First(r => _api.Catalog.Integrations.Single(i => i.Id == r.GetAttribute("data-integration-id")).Consumer.DisplayName == consumer);
        row.QuerySelector("[data-testid=ip-row-view]")!.Click();
        cut.Find("[data-testid=ip-detail-messaging]").TextContent.Should().Contain("M2LB.Hendelse.BiRK.Adapter · Wolverine Confirmed").And.Contain("Runtime processing Not assessed");
    }

    [Fact]
    public void UsingASnapshotBuildsTheEvidenceFromExactlyThatSnapshot()
    {
        _api.Messaging = null;
        var cut = Pane();
        _api.Messaging = Set();
        var snapshot = _api.SourceOptions.Snapshots[0];
        cut.Find("[data-testid=am-use-current]").Click();
        cut.Find("[data-testid=am-primary-fingerprint]").TextContent.Should().Be("c850a1b2…");
        cut.Find("[data-testid=am-use-scope]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=am-app]").Should().HaveCount(3));
        _api.UsedScopes.Should().ContainSingle(s => s.PrimarySnapshotId == snapshot.SnapshotId && s.RelatedSnapshotIds.Count == 0);
        cut.Find("[data-testid=am-status]").TextContent.Should().StartWith("Saved.");
    }

    [Fact]
    public void MasterDetailShowsAllApplicationsAndOnlySelectedEvidence()
    {
        _api.Messaging = Set();
        var cut = Pane();
        cut.Find("[data-testid=am-count]").TextContent.Should().Be("3");
        cut.FindAll("[data-testid=am-app]").Should().HaveCount(3);
        cut.FindAll("[data-testid=am-detail]").Should().ContainSingle();
        cut.FindAll("[data-testid=am-facts]").Should().ContainSingle();
        cut.Find("[data-testid=am-detail]").TextContent.Should().Contain("Program.cs:147");
        var buttons = cut.FindAll(".am-select");
        buttons[0].GetAttribute("aria-pressed").Should().Be("true");
        buttons[2].Click();
        cut.Find("[data-testid=am-detail]").TextContent.Should().Contain("M2LB.Revisjon.Worker").And.NotContain("Program.cs:147");
        cut.FindAll(".am-select")[2].GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-testid=am-detail-overview-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        foreach (var id in new[] { "am-source", "am-configuration", "am-route-section", "am-transport", "am-runtime-section", "am-limitations" })
            cut.Find($"[data-testid={id}-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("[data-testid=am-search]").Input("person");
        cut.FindAll("[data-testid=am-app]").Should().ContainSingle();
        cut.FindAll("[data-testid=am-detail]").Should().BeEmpty();
        cut.Find(".am-select").Click();
        cut.Find("[data-testid=am-detail]").TextContent.Should().Contain("M2LB.Person.Api");
    }

    [Fact]
    public void LikelyAndNotLinkedPreserveExternalSourceAndRuntimeDistinctions()
    {
        _api.Messaging = Set() with { Applications = [new()
        {
            ApplicationId = "External.Api", Detection = MessagingDetection.Likely,
            DetectionReason = "AddM2LBWolverine is called, but the registration implementation is outside the analyzed source.",
            HandlerMapping = MessagingFactState.NotAssessable,
            Limitations = ["Cross-service message identity: Not established from analyzed source."]
        }] };
        var cut = Pane();
        cut.Find("[data-testid=am-detection]").TextContent.Should().Be("Likely");
        cut.Find("[data-testid=am-mapping]").TextContent.Should().Be("Not assessed");
        cut.Find("[data-testid=am-meaning]").TextContent.Should().Contain("AddM2LBWolverine").And.Contain("outside the analyzed source");
        cut.Find("[data-testid=am-binding]").TextContent.Should().Contain("Not linked").And.NotContain("Not bound");
        _api.Messaging.Applications[0].BoundConsumer.Should().BeNull();
        cut.Find("[data-testid=am-limitations]").TextContent.Should().Contain("Cross-service message identity");
        cut.Find("[data-testid=am-runtime-evidence]").TextContent.Should().NotContainAny("0 executions", "0 retries", "0 ms");
    }

    [Fact]
    public void RouteTablePreservesDirectionAndCompleteSourceProvenance()
    {
        _api.Messaging = Set() with { Applications = [new()
        {
            ApplicationId = "Routes.Api", Detection = MessagingDetection.Confirmed,
            Facts = [new() { Label = "Registration", Locations = [new("a.cs", 1), new("b.cs", 2), new("c.cs", 3), new("d.cs", 4)] }],
            Routes = [
                new() { Direction = MessagingRouteDirection.Publish, Endpoint = "out-topic", MessageType = "Created", Senders = ["Publisher"], Location = new("send.cs", 10) },
                new() { Direction = MessagingRouteDirection.Listen, Endpoint = "in-subscription", MessageType = "Registered", Handlers = ["Consumer.Handle"], Location = new("listen.cs", 20) }]
        }] };
        var cut = Pane();
        var rows = cut.FindAll("[data-testid=am-routes] tbody tr");
        rows[0].TextContent.Should().Contain("Publish").And.Contain("out-topic").And.Contain("Created").And.Contain("Publisher").And.Contain("Source evidence").And.Contain("send.cs:10");
        rows[1].TextContent.Should().Contain("Listen").And.Contain("in-subscription").And.Contain("Registered").And.Contain("Consumer.Handle");
        cut.Find("[data-testid=am-facts]").TextContent.Should().Contain("d.cs:4");
    }

    // ── IQR pre-run / post-run / export ──

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
    public void PreRunShowsApplicationMessagingOutsideTheRuntimeSourceCount()
    {
        _api.Readiness = _api.Readiness with
        {
            ApplicationMessaging = [new() { ApplicationId = "M2LB.Hendelse.BiRK.Adapter", Detection = MessagingDetection.Confirmed, HandlerMapping = MessagingFactState.NotApplicable, RetryPolicy = MessagingFactState.NotFound,
                Outbox = MessagingFactState.NotFound, ErrorHandling = MessagingFactState.NotFound, BoundConsumer = "Hendelse BiRK Adapter", BoundTopics = 2,
                RuntimeState = IntegrationEvidenceState.NotSupported, RuntimeReason = "No Wolverine handler in this application's source; there is no handler processing to observe." }],
        };
        var cut = Iqr();
        var app = cut.Find("[data-testid=iqr-messaging-app]");
        app.TextContent.Should().Contain("M2LB.Hendelse.BiRK.Adapter").And.Contain("Wolverine").And.Contain("Bound to Hendelse BiRK Adapter (2 topics)");
        cut.Find("[data-testid=iqr-messaging-detection]").TextContent.Should().Be("Configured");
        cut.Find("[data-testid=iqr-messaging-copy]").TextContent.Should().StartWith("Source/build evidence is available. Runtime processing evidence is not yet available:");
        cut.Find("[data-testid=iqr-evidence-count]").TextContent.Should().Contain("0 of 4 available", "application messaging is not a transport runtime source");
        cut.FindAll("[data-testid=iqr-domain-readiness-card]").Should().HaveCount(10);
    }

    [Fact]
    public void PostRunAndExportUseTheRunSnapshotWithoutSourceOrSecrets()
    {
        var consumer = _api.Catalog.Integrations.First(i => i.Consumer.DisplayName?.Contains("Hendelse", StringComparison.Ordinal) == true).Consumer.DisplayName!;
        var snapshot = Set(consumer);
        _api.Result = _api.Result! with
        {
            ApplicationMessagingSnapshot = snapshot,
            ApplicationMessagingRuntime = [new() { ApplicationId = "M2LB.Hendelse.BiRK.Adapter", State = IntegrationEvidenceState.NotSupported, Reason = "No Wolverine handler in this application's source." }],
        };
        var cut = Iqr();
        cut.Find("[data-testid=iqr-run]").Click();
        cut.WaitForElement("[data-testid=iqr-result]");
        cut.Find("[data-testid=iqr-result-messaging-app]").TextContent.Should().Contain("Wolverine Confirmed").And.Contain("runtime processing not assessed");

        var html = new ReportExportService().ExportIntegrationReview(_api.Result, "BirkNext");
        html.Should().Contain("Application messaging").And.Contain("Wolverine").And.Contain("Confirmed").And.Contain("Not assessed")
            .And.Contain("Program.cs:147").And.Contain("sha256 aaaaaaaaaaaa");
        html.Should().NotContain("SharedAccessKey").And.NotContain("await bus.SendAsync");
    }
}
