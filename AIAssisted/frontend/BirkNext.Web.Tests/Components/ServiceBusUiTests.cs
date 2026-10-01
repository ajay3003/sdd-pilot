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

    private void UsePlatform(Func<IntegrationPlatform, IntegrationPlatform> change, bool azureEnabled = false) =>
        _api.Catalog = M2lbFixture.Catalog() with { Platforms = [.. M2lbFixture.Catalog().Platforms, change(ServiceBusPlatform)], AzureRuntimeEnabled = azureEnabled };

    private static ServiceBusTopology WideTopology() => ServiceBusPlatform.ServiceBusTopology! with
    {
        Notes = ["Values are the Terraform-declared expectation for DEV, not deployment-verified; runtime metadata confirms or contradicts them."],
        Entities =
        [
            .. ServiceBusPlatform.ServiceBusTopology!.Entities,
            new() { EntityType = ServiceBusEntityType.Queue, Name = "operatorkontroll.varsler", Publishers = ["M2LB.Hendelse.Api"], MaxDeliveryCount = 10, LockDuration = "PT1M", DefaultMessageTimeToLive = "P14D", RequiresSession = false, DeadLetteringOnMessageExpiration = true },
            new() { EntityType = ServiceBusEntityType.Subscription, Topic = "person.barn", Name = "hendelse-barn", Consumers = [] },
            new() { EntityType = ServiceBusEntityType.Topic, Name = "person.person", Publishers = ["M2LB.Person.Api"] },
        ],
    };

    private static string Text(IRenderedComponent<IntegrationsPane> cut, string id) => cut.Find($"[data-testid={id}]").TextContent.Trim();

    private static ApplicationMessagingEvidenceSet Messaging(params MessagingRoute[] routes) => new()
    {
        EnvironmentId = "dev", AnalyzedAt = DateTimeOffset.UtcNow, AnalyzerVersion = 2, Archives = [new SourceArchive("M2LB.zip", new string('a', 64), 10)],
        Applications = [new() { ApplicationId = "M2LB.Tjeneste.Api", Detection = MessagingDetection.Confirmed, HandlerMapping = MessagingFactState.NotApplicable, Routes = [.. routes] }],
    };

    [Fact]
    public void OverviewSeparatesDeclaredTopologyMessagingAndRuntimeAccess()
    {
        var cut = Pane();
        var platforms = cut.FindAll("[data-testid=ip-platform]");
        platforms.Should().HaveCount(2);
        Text(cut, "sb-configuration").Should().Be("Configured");
        Text(cut, "sb-queues").Should().Be("2");
        Text(cut, "sb-topics").Should().Be("1");
        Text(cut, "sb-subscriptions").Should().Be("1");
        Text(cut, "sb-messaging-source").Should().Be("Not analyzed");
        Text(cut, "sb-runtime").Should().Be("Not available");
        var summary = cut.Find("[data-testid=sb-summary]");
        summary.QuerySelectorAll("dt").Select(d => d.TextContent).Should().Equal("Namespace", "Declared topology", "Queues", "Topics", "Subscriptions", "Application messaging source", "Runtime access");
        summary.TextContent.Should().NotContain("Runtime evidence", "the topology is source-declared and runtime access is its own state");
        cut.Find("[data-testid=sb]").TextContent.Should().NotContain("Test Service Bus").And.NotContain("Configure runtime evidence");
        platforms[1].TextContent.Should().NotContain("Topic prefix").And.NotContain("Checkpoint store", "no Event Hub concepts on a Service Bus platform");
        cut.FindAll("[data-testid=ip-row]").Should().HaveCount(16, "Service Bus entities are not business-topic rows");
        cut.FindAll("[data-testid^=ip-runtime-dev-servicebus]").Should().BeEmpty("the old mixed runtime-evidence-sources box is gone");
    }

    [Fact]
    public void RunServiceBusReviewIsTheMixedReadOnlyReviewAndStaysEnabledWithoutRuntimeAccess()
    {
        var cut = Pane();
        var run = cut.Find("[data-testid=sb-test]");
        run.TextContent.Should().Be("Run Service Bus review");
        run.GetAttribute("aria-label").Should().Be("Run Service Bus review for M2LB DEV Service Bus (read-only)");
        run.HasAttribute("disabled").Should().BeFalse("configuration and route checks run without Azure");
        Text(cut, "sb-review-scope").Should().Contain("Compares the declared topology with analyzed application routes").And.Contain("Azure metadata is not read while runtime access is not available").And.Contain("Nothing is sent, received, peeked or changed");
        _api.ServiceBusCheck = _ => Observed(DateTimeOffset.UtcNow);
        run.Click();
        _api.Calls.Should().Contain("servicebus-evidence:" + PlatformId);
    }

    [Fact]
    public void RuntimeAccessAvailableNeedsTheSubscriptionAndTheInstanceGate()
    {
        UsePlatform(p => p with { RuntimeEvidence = new() { SubscriptionId = "2fcdb9d0-22eb-43b0-b95e-7cbba08c34b0" } }, azureEnabled: true);
        var cut = Pane();
        Text(cut, "sb-runtime").Should().Be("Available");
        Text(cut, "sb-access-status").Should().Be("Available");
        cut.FindAll("[data-testid=sb-access-reason]").Should().BeEmpty();
        cut.FindAll("[data-testid=sb-access-impact]").Should().BeEmpty();
        Text(cut, "sb-access-subscription").Should().Be("2fcdb9d0-22eb-43b0-b95e-7cbba08c34b0");
        Text(cut, "sb-review-scope").Should().Contain("reads Azure management metadata and metrics");
        Text(cut, "sb-configuration").Should().Be("Configured", "runtime access never turns declared topology into observed topology");
        Text(cut, "sb-observations-state").Should().Be("No runtime metadata collected yet. Run the Service Bus review to read it.");

        UsePlatform(p => p with { RuntimeEvidence = new() { SubscriptionId = "2fcdb9d0-22eb-43b0-b95e-7cbba08c34b0" } }, azureEnabled: false);
        var gated = Pane();
        Text(gated, "sb-access-status").Should().Be("Not available");
        Text(gated, "sb-access-reason").Should().Contain("Azure access is not enabled for this BirkNext instance");
    }

    [Fact]
    public void MissingSubscriptionIsARuntimeAccessBlockerNeverAServiceBusFailure()
    {
        var cut = Pane();
        var access = cut.Find("[data-testid=sb-runtime-access]");
        Text(cut, "sb-access-status").Should().Be("Not available");
        cut.Find("[data-testid=sb-access-status]").ClassList.Should().Contain("sb-badge-attention").And.NotContain("sb-badge-fail");
        cut.FindAll("[data-testid=sb-access-reason] li").Select(l => l.TextContent).Should().Equal(
            "Azure subscription is not configured for this Service Bus namespace.", "Azure access is not enabled for this BirkNext instance (IntegrationReview:Azure:Enabled).");

        UsePlatform(p => p, azureEnabled: true);
        Text(Pane(), "sb-access-reason").Should().Be("Azure subscription is not configured for this Service Bus namespace.", "with the instance gate open, only the subscription blocks");
        Text(cut, "sb-access-subscription").Should().Be("Not configured");
        Text(cut, "sb-access-rg").Should().Be("rg-m2lb-dev-shared-nwe");
        Text(cut, "sb-access-namespace").Should().Be("sbns-m2lb-dev-nwe-001");
        Text(cut, "sb-access-impact").Should().Contain("namespace metadata").And.Contain("queues").And.Contain("subscriptions").And.Contain("message counts");
        access.QuerySelectorAll(".sb-badge-fail").Should().BeEmpty();
        cut.Find("[data-testid=sb]").TextContent.Should().NotContainAny("Failed", "Misconfigured");
        Text(cut, "sb-configuration").Should().Be("Configured", "the topology itself is configured");
        cut.Find("[data-testid=sb-access-details-toggle]").GetAttribute("aria-expanded").Should().Be("false");
    }

    [Fact]
    public void NoRuntimeObservationIsEverRenderedAsZero()
    {
        var cut = Pane();
        Text(cut, "sb-observations-state").Should().Be("No runtime metadata collected.");
        cut.Find("[data-testid=sb-observations]").TextContent.Should().NotContain("0");
        _api.ServiceBusCheck = _ => Observed(DateTimeOffset.UtcNow) with
        {
            RuntimeChecks = [], Runtime = new() { State = IntegrationEvidenceState.NotConfigured, Reason = "The Azure subscription id of the namespace is not configured.", CapturedAt = DateTimeOffset.UtcNow },
        };
        cut.Find("[data-testid=sb-test]").Click();
        cut.WaitForElement("[data-testid=sb-result]");
        Text(cut, "sb-observations-state").Should().Be("No runtime metadata collected in the last review: The Azure subscription id of the namespace is not configured.");
        cut.FindAll("[data-testid=sb-result-counts]").Should().BeEmpty("no counts without runtime evidence — never zeros");
    }

    [Fact]
    public void DeclaredTopologyShowsItsSourceAndNeverLooksDeployed()
    {
        UsePlatform(p => p with { ServiceBusTopology = WideTopology() with { AuditedAt = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero) } });
        var cut = Pane();
        var section = cut.Find("[data-testid=sb-topology-source]");
        Text(cut, "sb-topology-provenance").Should().Be("Developer-side audit of Terraform (seeded)");
        Text(cut, "sb-topology-recorded").Should().Be("2026-09-28");
        Text(cut, "sb-topology-counts").Should().Be("3 queues · 2 topics · 2 subscriptions");
        Text(cut, "sb-topology-boundary").Should().Contain("not proof of what is deployed").And.Contain("may confirm or contradict it");
        section.TextContent.Should().NotContainAny("Observed", "exists at runtime", "Current Azure");
        cut.Find("[data-testid=sb-topology-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("[data-testid=sb-topology-toggle]").TextContent.Should().Contain("Show topology").And.Contain("3 queues · 2 topics · 2 subscriptions");
        cut.Find("[data-testid=sb-topology-notes-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("[data-testid=sb-topology-notes-body]").TextContent.Should().Contain("not deployment-verified");
    }

    [Fact]
    public void QueuesShowPublishersEntityConsumersNamespaceRightsAndCompactExpectedProperties()
    {
        UsePlatform(p => p with { ServiceBusTopology = WideTopology() });
        var cut = Pane();
        cut.Find("[data-testid=sb-topology-toggle]").Click();
        var queues = cut.FindAll("[data-testid=sb-queue]");
        queues.Select(q => q.GetAttribute("data-queue")).Should().Equal("leselogg", "birk-adapter-errors", "operatorkontroll.varsler");
        var leselogg = queues[0];
        leselogg.QuerySelector("th")!.GetAttribute("scope").Should().Be("row");
        leselogg.TextContent.Should().Contain("M2LB.Hendelse.Api");
        leselogg.QuerySelector("[data-testid=sb-queue-consumers]")!.TextContent.Should().Be("M2LB.Revisjon.Worker");
        leselogg.QuerySelector("[data-testid=sb-queue-expected]")!.TextContent.Should().Be("max 10 deliveries · 1 min lock · 14 d TTL");
        var operator_ = queues[2];
        operator_.QuerySelector("[data-testid=sb-queue-consumers]")!.TextContent.Should().Contain("No entity-specific consumer").And.Contain("Namespace-wide receive rights exist (not a confirmed mapping)");
        operator_.QuerySelector("[data-testid=sb-queue-expected]")!.TextContent.Should().Be("max 10 deliveries · 1 min lock · 14 d TTL · no sessions · DLQ on expiry");
        Text(cut, "sb-namespace-receivers").Should().Contain("M2LB.Hendelse.BiRK.Adapter").And.Contain("not an entity-specific consumer mapping");
        cut.Find("[data-testid=sb-queue-table]").TextContent.Should().NotContainAny("Observed", "exists", "Current");
        cut.Find("[data-testid=sb-exact-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("[data-testid=sb-exact-table]").TextContent.Should().Contain("MaxDeliveryCount 10").And.Contain("LockDuration PT1M").And.Contain("DefaultMessageTimeToLive P14D")
            .And.Contain("RequiresSession false").And.Contain("DeadLetteringOnMessageExpiration true");
        cut.Find("[data-testid=sb-topology] .sb-tablewrap").GetAttribute("tabindex").Should().Be("0");
    }

    [Fact]
    public void TopicsGroupTheirSubscriptionsAndANoSubscriptionTopicStaysNeutral()
    {
        UsePlatform(p => p with { ServiceBusTopology = WideTopology() });
        var cut = Pane();
        cut.Find("[data-testid=sb-topology-toggle]").Click();
        var topics = cut.FindAll("[data-testid=sb-topic]");
        topics.Select(t => t.GetAttribute("data-topic")).Should().Equal("person.barn", "person.person");

        var barn = topics[0];
        barn.QuerySelectorAll("[data-testid=sb-subscription]").Select(s => s.GetAttribute("data-subscription")).Should().Equal("tjeneste-barnregistrert", "hendelse-barn");
        var topicCell = barn.QuerySelector("th")!;
        topicCell.GetAttribute("scope").Should().Be("rowgroup");
        topicCell.GetAttribute("rowspan").Should().Be("2", "topic metadata is shown once for all its subscriptions");
        barn.TextContent.Should().Contain("M2LB.Person.Api").And.Contain("M2LB.Tjeneste.Api");
        barn.QuerySelectorAll("[data-testid=sb-mapping-state]").Select(s => s.TextContent).Should().Equal("Configured", "No consumer configured");
        barn.QuerySelectorAll("[data-testid=sb-subscription]")[1].TextContent.Should().Contain("No entity-specific consumer");

        var person = topics[1];
        person.QuerySelectorAll("[data-testid=sb-subscription]").Should().BeEmpty();
        var state = person.QuerySelector("[data-testid=sb-mapping-state]")!;
        state.TextContent.Should().Be("No subscription configured");
        state.ClassList.Should().Contain("sb-badge-muted").And.NotContain("sb-badge-fail");
        cut.Find("[data-testid=sb-topic-table]").TextContent.Should().NotContainAny("Missing", "Failed", "Broken", "Observed");
    }

    [Fact]
    public void ApplicationMessagingIsSeparateAndNoSourceIsNotAbsence()
    {
        var cut = Pane();
        Text(cut, "sb-messaging-analysis").Should().Be("Not analyzed");
        cut.Find("[data-testid=sb-messaging]").TextContent.Should().Contain("not evidence that Wolverine is absent").And.Contain("does not prove that a Wolverine handler is configured or executed");
        Text(cut, "sb-routes").Should().Be("Not assessed");
        Text(cut, "sb-messaging-runtime").Should().Be("Not assessed");
        cut.Find("[data-testid=sb-analyze-messaging]").Click();
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextFocusElement" && (string)i.Arguments[0]! == ServiceBusPlatformPanel.MessagingUploadId);
        cut.Find("[data-testid=am-upload]").Id.Should().Be(ServiceBusPlatformPanel.MessagingUploadId, "the action points at the one real upload");
    }

    [Fact]
    public void AnalyzedRoutesAreSourceFindingsAndHandlerRuntimeStaysNotAssessed()
    {
        _api.Messaging = Messaging(new MessagingRoute { Direction = MessagingRouteDirection.Publish, EntityName = "leselogg", EndpointKind = MessagingEndpointKind.Queue, Endpoint = "leselogg", MessageType = "LeseloggHendelse" });
        var cut = Pane();
        Text(cut, "sb-messaging-source").Should().Be("Analyzed");
        Text(cut, "sb-routes").Should().Be("1 of 1 name a declared entity");
        cut.Find("[data-testid=sb-messaging]").TextContent.Should().Contain("Source findings, not executed handlers");
        Text(cut, "sb-messaging-runtime").Should().Be("Not assessed");
        Text(cut, "sb-analyze-messaging").Should().Be("Re-analyze messaging source");

        _api.Messaging = Messaging();
        var none = Pane();
        Text(none, "sb-routes").Should().Be("No routes found in source");
        Text(none, "sb-messaging-runtime").Should().Be("Not assessed", "transport topology never becomes handler execution");
    }

    [Fact]
    public void EvaluationPolicyIsSeparateAndWithoutThresholdsObservedOnly()
    {
        var cut = Pane();
        var policy = cut.Find("[data-testid=sb-policy]");
        Text(cut, "sb-policy-window").Should().Be("24 h");
        Text(cut, "sb-policy-thresholds").Should().Be("Not configured");
        Text(cut, "sb-policy-semantics").Should().Contain("Observed only — never judged Pass or Fail").And.Contain("never zero").And.Contain("not an evidence source");
        cut.Find("[data-testid=sb-runtime-access]").TextContent.Should().NotContain("Review window", "policy is not runtime access configuration");
        policy.TextContent.Should().NotContain("Azure subscription");

        UsePlatform(p => p with { RuntimeEvidence = new() { ReviewWindowHours = 12 } });
        Text(Pane(), "sb-policy-window").Should().Be("12 h");
    }

    [Fact]
    public void ConfigureAzureRuntimeAccessOpensTheFormInsideRuntimeAccess()
    {
        var cut = Pane();
        var configure = cut.Find("[data-testid=sb-runtime-access] [data-testid=sb-configure-runtime]");
        configure.TextContent.Should().Be("Configure Azure runtime access");
        configure.TagName.Should().Be("BUTTON");
        configure.Click();
        cut.Find("[data-testid=sb-runtime-access] [data-testid=ip-runtime-form] [data-testid=ip-runtime-subscription]").Should().NotBeNull();
        cut.Find("[data-testid=ip-runtime-save]").TextContent.Should().Be("Save runtime access");
        _api.SavedPlatforms.Should().BeEmpty();
    }

    [Fact]
    public void DurationsAreCompactAndUnknownPropertiesAreOmitted()
    {
        ServiceBusPresentation.Duration("PT1M").Should().Be("1 min");
        ServiceBusPresentation.Duration("P14D").Should().Be("14 d");
        ServiceBusPresentation.Duration("PT2H").Should().Be("2 h");
        ServiceBusPresentation.Duration("not-iso").Should().Be("not-iso");
        ServiceBusPresentation.ExpectedCompact(new ServiceBusEntityExpectation()).Should().Be("None declared");
        ServiceBusPresentation.ExactProperties(new ServiceBusEntityExpectation { MaxDeliveryCount = 5 }).Should().Equal(("MaxDeliveryCount", "5"));
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
        cut.Find("[data-testid=iqr-servicebus-details-body]").HasAttribute("hidden").Should().BeTrue("topology counts are detail, collapsed by default");
        cut.Find("[data-testid=iqr-servicebus-counts]").TextContent.Should().Contain("4 queue(s) · 11 topic(s) · 8 subscription(s)").And.Contain("code routes 12 of 12 matched");
        cut.Find("[data-testid=iqr-servicebus-manage]").GetAttribute("href").Should().EndWith("focus=servicebus");
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
