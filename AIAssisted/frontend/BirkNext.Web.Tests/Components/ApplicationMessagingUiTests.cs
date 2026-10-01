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
        cut.Find("[data-testid=am-overview-source]").TextContent.Should().Contain("not evidence that Wolverine is absent");
        cut.Find("[data-testid=am-empty]").TextContent.Should().Be("No application source analyzed yet.");
        cut.Find("[data-testid=am-note]").GetAttribute("role").Should().Be("note");
        cut.Find("[data-testid=am-note]").TextContent.Should().Contain("Event Hub and Service Bus transport evidence is separate").And.Contain("not that a handler ran");
        cut.Find("[data-testid=am] .am-overview").TextContent.Should().NotContainAny("Failed", "Absent", "Unsupported");
    }

    [Fact]
    public void TheActionIsNamedForWhatItAnalyzes()
    {
        var cut = Pane();
        var upload = cut.Find("[data-testid=am-upload]");
        upload.GetAttribute("aria-label").Should().Be("Analyze application messaging source archives (.zip)");
        upload.ParentElement!.TextContent.Should().Contain("Analyze messaging source").And.NotContain("Analyze source archives");
        cut.Find("[data-testid=am] .am-upload").TextContent.Should().Contain("Wolverine messaging only");

        _api.Messaging = Set();
        Pane().Find("[data-testid=am-upload]").ParentElement!.TextContent.Should().Contain("Re-analyze messaging source");
    }

    [Fact]
    public void AnalyzedWithWolverineIsDetectedConfigurationNeverHandlerExecution()
    {
        _api.Messaging = Set();
        var cut = Pane();
        Overview(cut, "source").Should().Be("Analyzed");
        Overview(cut, "wolverine").Should().Be("Detected");
        cut.Find("[data-testid=am-overview-wolverine]").TextContent.Should().Contain("In 2 application(s)").And.Contain("Configured is not a handled message");
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
        cut.Find("[data-testid=am-overview-wolverine]").TextContent.Should().Contain("package wiring outside it is not visible");
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
        cut.Find("[data-testid=am-overview-transport]").TextContent.Should().Contain("never counts as Wolverine handler evidence");
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
        apps.Select(a => a.GetAttribute("data-application")).Should().Equal("M2LB.Hendelse.BiRK.Adapter", "M2LB.Revisjon.Worker");
        var revisjon = apps[1];
        revisjon.QuerySelector("[data-testid=am-detection]")!.TextContent.Should().Be("Confirmed");
        revisjon.QuerySelector("[data-testid=am-mapping]")!.TextContent.Should().Be("Available");
        revisjon.QuerySelector("[data-testid=am-retry]")!.TextContent.Should().Be("Configured");
        cut.FindAll("[data-testid=am-runtime]").Should().OnlyContain(r => r.TextContent == "Not assessed");
        cut.Find("[data-testid=am-not-detected]").TextContent.Should().Contain("M2LB.Person.Api");
        cut.Find("[data-testid=am-provenance]").TextContent.Should().Contain("M2LB.zip").And.Contain("sha256 aaaaaaaaaaaa");
        cut.Markup.Should().NotContain(">Pass<");
    }

    [Fact]
    public void EvidenceDisclosureShowsProvenanceArchitectureAndMissingRuntime()
    {
        _api.Messaging = Set();
        var cut = Pane();
        var disclosure = cut.Find("[data-testid=am-evidence-m2lb-hendelse-birk-adapter]");
        disclosure.QuerySelector(".disclosure-body")!.HasAttribute("hidden").Should().BeTrue();
        disclosure.QuerySelector("button")!.GetAttribute("aria-expanded").Should().Be("false");
        disclosure.QuerySelector("button")!.Click();
        var body = cut.Find("[data-testid=am-evidence-m2lb-hendelse-birk-adapter]");
        body.QuerySelector("[data-testid=am-meaning]")!.TextContent.Should().Contain("configured for sending only");
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
    public void UploadSendsArchivesAndShowsTheAnalysis()
    {
        _api.Messaging = null;
        var cut = Pane();
        _api.Messaging = Set();
        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(InputFileContent.CreateFromBinary([0x50, 0x4b, 0x03, 0x04], "M2LB.zip"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=am-app]").Should().HaveCount(2));
        _api.Analyzed.Should().Equal("M2LB.zip");
        cut.Find("[data-testid=am-status]").TextContent.Should().StartWith("Saved.");
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
