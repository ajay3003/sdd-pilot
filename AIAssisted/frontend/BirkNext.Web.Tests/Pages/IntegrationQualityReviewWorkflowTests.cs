using System.Net;
using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The tester workflow of Integration Quality Review: four views, the integration under review first with its separate states (configured,
/// mapping, source, contract/reference, runtime, active execution), contract evidence split into available vs not verified, read-only "Run
/// review", active execution as Allowed / Not allowed, test packs under Test &amp; Verify, and known values shown with their owner, never re-entered.
/// Pack names here are test data; the page itself names no pack.
/// </summary>
public sealed class IntegrationQualityReviewWorkflowTests : BunitContext
{
    private const string IntakeId = "dev:http:intake";
    private readonly FakeIntegrationCatalogApi _api = new() { Result = M2lbFixture.Result() };
    private readonly Mock<IIntegrationJourneysApiService> _journeys = new();
    private bool _trusted;

    private static IntegrationDefinition Intake() => new()
    {
        Id = IntakeId, EnvironmentId = "dev", DisplayName = "Portal – report intake", Kind = IntegrationKind.HttpApi, Enabled = true, SystemName = "Portal → Hub",
        EndpointOrTopic = "https://hub.example.test/api/hook", Producer = "Portal", Consumer = new IntegrationConsumer { DisplayName = "Hub", MappingState = ConsumerMappingState.NeedsConfirmation },
    };

    private static IntegrationReviewEvidenceFacts Facts(string? bound) => new(IntakeId)
    {
        PackName = "Demo pack", ReferenceTitle = "Portal app", SourceRole = "Hub", SourceMatches = 1, BoundArchive = bound,
        SourceState = bound is null ? TestPackSourceState.AvailableNotBound : TestPackSourceState.Bound,
        AuthenticationReference = [new("Authentication scheme", "Bearer JWT")],
        ContractReference = [new("Form data type", "portal_report_v2"), new("Content type", "application/xml"), new("Process task", "Task_1")],
        SchemaName = "portal_report_v2", SchemaFiles = ["xsd/portal_report_v2.xsd"],
    };

    private void Arrange(string? bound = "hub.zip")
    {
        var intake = Intake();
        _api.Catalog = new IntegrationCatalog { EnvironmentId = "dev", Integrations = [intake] };
        _api.Readiness = new IntegrationReviewReadiness
        {
            EnvironmentId = "dev", ConfiguredIntegrations = 1, EnabledIntegrations = 1, CanRun = true, Headline = "Can run with limitations",
            Systems = [new IntegrationSystemScope { SystemName = "Portal → Hub", Kind = IntegrationKind.HttpApi, Topics = 1, Enabled = 1, ConsumersNeedingConfirmation = 1, DomainReviewSupported = true }],
            Domains = [new(IntegrationReviewDomain.Configuration, IntegrationDomainReadiness.Ready, "Reviewed."), new(IntegrationReviewDomain.Contract, IntegrationDomainReadiness.Partial, "Reference and source.", ["Reference"], ["Runtime contract validation: not verified"]),
                new(IntegrationReviewDomain.Performance, IntegrationDomainReadiness.NotAssessable, "No timing.", [], ["Measured response time"])],
            Subjects = [new IntegrationReviewSubject(IntakeId, intake.DisplayName, IntegrationKind.HttpApi, IntegrationReviewCapability.HttpApi)
                { Producer = "Portal", Consumer = "Hub", EndpointOrTopic = intake.EndpointOrTopic, MappingState = ConsumerMappingState.NeedsConfirmation, Facts = Facts(bound) }],
        };
        if (bound is not null) _api.EvidenceBindings = [new IntegrationEvidenceBinding { EnvironmentId = "dev", IntegrationId = IntakeId, SnapshotId = Guid.NewGuid(), ArchiveName = bound, Fingerprint = new string('a', 64) }];
    }

    public IntegrationQualityReviewWorkflowTests()
    {
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext
        {
            ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Development", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://app.example.test/" },
        });
        _journeys.Setup(j => j.PacksAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(() => new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>(
            [new IntegrationJourneyPackView
            {
                PackId = "demo.pack", EnvironmentTrusted = _trusted, EnvironmentDetail = "Not in TargetEnvironments:Trusted (technical).",
                Journeys = [new IntegrationJourneyView { Readiness = JourneyReadinessState.NotReady,
                    MatchedIntegrations = [new JourneyMatchedIntegration("report-intake", IntakeId, "Portal – report intake", IntegrationKind.HttpApi, true, "")] }],
            }], null, HttpStatusCode.OK));
        _journeys.Setup(j => j.TestPacksAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new ActiveEventApiResult<IReadOnlyList<TestPackDescriptor>>(
            [new TestPackDescriptor { PackId = "demo.pack", DisplayName = "Demo Testing", Journeys = [new("intake", "Intake", "Portal → Hub", JourneyExecutionMode.JourneyRunner, null)] }], null, HttpStatusCode.OK));
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        Services.AddSingleton(_journeys.Object);
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
        Services.AddSingleton(context.Object);
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton<IReportExportService, ReportExportService>();
        Services.AddSingleton<RuntimeReviewSessionService>();
        var activeTests = new Mock<IActiveEventsApiService>();
        activeTests.Setup(a => a.TrustAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ActiveEventApiResult<ActiveEventEnvironmentTrust>(new() { Trusted = _trusted, DisplayName = "Development", EnvironmentType = "Development", ExecutionAllowed = _trusted }, null));
        activeTests.Setup(a => a.ProvidersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActiveEventApiResult<IReadOnlyList<ActiveEventProviderSummary>>([], null));
        Services.AddSingleton(activeTests.Object);
        Services.AddSingleton(new ActiveEventAuthenticationState(false, "Not configured (test)."));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<IntegrationQualityReview> Open(string? view = null)
    {
        if (view is not null) Services.GetRequiredService<NavigationManager>().NavigateTo($"integration-quality-review?view={view}");
        return Render<IntegrationQualityReview>();
    }

    private static Dictionary<string, string> States(IRenderedComponent<IntegrationQualityReview> cut) =>
        cut.FindAll("[data-testid=iqr-subject-states] > div").ToDictionary(d => d.GetAttribute("data-key")!, d => d.QuerySelector("[data-testid=iqr-subject-state]")!.TextContent.Trim());

    [Fact]
    public void Overview_LeadsWithTheIntegration_AndKeepsEveryStateSeparate()
    {
        Arrange();
        var cut = Open();
        cut.Find("[data-testid=iqr-subject-name]").TextContent.Should().Be("Portal – report intake");
        cut.Find("[data-testid=iqr-subject-line]").TextContent.Should().Be("HTTP/API · Portal → Hub");
        cut.WaitForAssertion(() => States(cut)["execution"].Should().Be("Not allowed"));
        States(cut).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["configuration"] = "Configured", ["mapping"] = "Needs confirmation", ["source"] = "Bound", ["contract"] = "Partial", ["runtime"] = "Not verified", ["execution"] = "Not allowed",
        });
        cut.Find("[data-testid=iqr-attention-item][data-key=mappings]").TextContent.Should().Contain("Consumer mapping").And.Contain("Needs confirmation");
        cut.Find("[data-testid=iqr-action-mappings]").TextContent.Should().Be("Confirm consumer mapping");
        cut.FindAll("[data-testid=iqr-attention-item][data-key=contracts]").Should().BeEmpty("known reference and source contract evidence is never \"Not configured\"");
        cut.Find("[data-testid=iqr-active-tests-readiness]").TextContent.Should().Be("Blocked");
        cut.Markup.Should().NotContain("Trusted environment").And.NotContain("Not trusted").And.NotContain("Run with limitations");
    }

    [Fact]
    public void UnboundSource_IsAvailableNeedsBinding_NeverMissing()
    {
        Arrange(bound: null);
        var cut = Open();
        States(cut)["source"].Should().Be("Available · needs binding");
    }

    [Fact]
    public void SetupAndEvidence_ShowsKnownValuesWithTheirOwner_AndSplitsContractEvidence()
    {
        Arrange();
        var cut = Open("setup-evidence");
        var known = cut.FindAll("[data-testid=iqr-known]").ToDictionary(k => k.GetAttribute("data-field")!, k => k.TextContent);
        known["Endpoint"].Should().Contain("https://hub.example.test/api/hook").And.Contain("Source: configured integration");
        known["Producer"].Should().Contain("Portal");
        cut.FindAll("[data-testid=iqr-group-evidence] input:not([type=radio]):not([type=checkbox]), [data-testid=iqr-group-evidence] select")
            .Where(e => e.Closest("[data-testid=iqr-source-details]") is null).Should().BeEmpty("known values are never re-entered here");
        cut.Find("[data-testid=iqr-setup-confirm-mapping]").TextContent.Should().Be("Confirm consumer mapping");
        cut.Find("[data-testid=iqr-source-matches]").TextContent.Should().Be("1");
        cut.Find("[data-testid=iqr-source-binding]").TextContent.Should().Be("hub.zip");
        var available = cut.Find("[data-testid=iqr-contract-available]").TextContent;
        available.Should().Contain("Portal app (Test Pack reference)").And.Contain("Content type application/xml").And.Contain("hub.zip (canonical binding)").And.Contain("xsd/portal_report_v2.xsd");
        cut.Find("[data-testid=iqr-contract-missing]").TextContent.Should().Contain("Runtime contract validation: not verified");
        cut.Find("[data-testid=iqr-setup-advanced-body]").HasAttribute("hidden").Should().BeTrue();
        cut.Find("[data-testid=iqr-stage][data-stage=runtime] [data-testid=iqr-evidence-count]").TextContent.Should().StartWith("Not verified");
    }

    [Fact]
    public void Review_NamesTheProfileApplied_AndUsesReviewScopedWording()
    {
        Arrange();
        var cut = Open("review");
        cut.Find("[data-testid=iqr-review-scope]").TextContent.Should().Contain("HTTP/API");
        cut.FindAll("[data-testid=iqr-domain-status]").Select(s => s.TextContent).Should().Equal("Reviewable", "Partially reviewable", "Not assessable");
        cut.Find("[data-testid=iqr-run-from-review]").TextContent.Trim().Should().Be("Run review");
        cut.Find("[data-testid=iqr-group-coverage]").TextContent.Should().Contain("No messages are published, consumed or changed.");
    }

    [Fact]
    public void TestAndVerify_ShowsActiveExecutionAndTestPacks_WithTechnicalDetailCollapsed()
    {
        Arrange();
        var cut = Open("test-verify");
        cut.WaitForAssertion(() => cut.Find("[data-testid=iqr-active-execution-state]").TextContent.Should().Be("Not allowed"));
        cut.Find("[data-testid=iqr-active-execution]").TextContent.Should().Contain("This environment has not been enabled for active execution.");
        cut.Find("[data-testid=iqr-manage-execution]").GetAttribute("href").Should().Be("/admin/system-settings?section=target-environments");
        cut.Find("[data-testid=iqr-execution-details-body]").HasAttribute("hidden").Should().BeTrue("configuration keys stay under Technical details");
        var pack = cut.Find("[data-testid=iqr-test-pack]");
        pack.TextContent.Should().Contain("Demo Testing").And.Contain("Intake");
        cut.Markup.Should().NotContain("Trusted environment").And.NotContain("Environment not trusted");
    }

    [Fact]
    public void ActiveExecutionAllowed_IsReadFromTheBackendGateOnly()
    {
        Arrange();
        _trusted = true;
        var cut = Open("test-verify");
        cut.WaitForAssertion(() => cut.Find("[data-testid=iqr-active-execution-state]").TextContent.Should().Be("Allowed"));
        cut.Find("[data-testid=iqr-active-execution]").GetAttribute("data-allowed").Should().Be("true");
    }

    [Fact]
    public void NoEnabledIntegration_IsOneEmptyState()
    {
        _api.Catalog = new IntegrationCatalog { EnvironmentId = "dev" };
        _api.Readiness = new IntegrationReviewReadiness { Headline = "Cannot run", Reasons = ["No enabled integration is configured."] };
        var cut = Open();
        cut.Find("[data-testid=iqr-zero-integrations]").TextContent.Should().Contain("No connected systems");
        cut.FindAll("[data-testid=iqr-subject], [data-testid=iqr-readiness]").Should().BeEmpty();
    }

    [Fact]
    public void SeveralIntegrations_OfferOneCompactSelector()
    {
        Arrange();
        var second = new IntegrationReviewSubject("dev:sb:closed", "Period closed", IntegrationKind.ServiceBus, IntegrationReviewCapability.MessageBroker) { MappingState = ConsumerMappingState.Confirmed, EndpointOrTopic = "period-closed" };
        _api.Readiness = _api.Readiness! with { Subjects = [.. _api.Readiness!.Subjects, second] };
        var cut = Open();
        // Each integration with its type, and the environment-wide review only as an explicit last choice.
        cut.FindAll("[data-testid=iqr-subject-select] option").Select(o => o.TextContent).Should().Equal("Portal – report intake · HTTP/API", "Period closed · Service Bus", "All enabled integrations (environment-wide review)");
        cut.Find("[data-testid=iqr-subject-select] select").Change("dev:sb:closed");
        cut.Find("[data-testid=iqr-subject-name]").TextContent.Should().Be("Period closed");
    }

    // ── The selected integration IS the review scope ────────────────────────────────────────────────────────────────

    private static readonly IntegrationReviewSubject Closed = new("dev:sb:closed", "Period closed", IntegrationKind.ServiceBus, IntegrationReviewCapability.MessageBroker)
        { MappingState = ConsumerMappingState.Confirmed, EndpointOrTopic = "skole.reports" };

    private void ArrangeScoped()
    {
        Arrange();
        var all = _api.Readiness!;
        _api.ScopedReadiness = id => id switch
        {
            IntakeId => all with { ScopeIntegrationId = IntakeId, Subjects = [.. all.Subjects, Closed] },
            "dev:sb:closed" => all with
            {
                ScopeIntegrationId = "dev:sb:closed", Subjects = [.. all.Subjects, Closed],
                Systems = [new IntegrationSystemScope { SystemName = "Skole → Bufdata", Kind = IntegrationKind.ServiceBus, Topics = 1, Enabled = 1, DomainReviewSupported = true }],
                Domains = [new(IntegrationReviewDomain.MessageFlow, IntegrationDomainReadiness.Partial, "Service Bus topology from configuration.")],
            },
            _ => all with { Subjects = [.. all.Subjects, Closed] },
        };
    }

    [Fact]
    public void SelectingAnIntegration_RecomputesTheReviewPlanForIt_AndTheRunIsScoped()
    {
        ArrangeScoped();
        var cut = Open();
        _api.ScopeRequests.Last().Should().Be(IntakeId, "without a choice the first integration is reviewed — never a silent environment-wide mix");
        cut.Find("[data-testid=iqr-review-scope]").TextContent.Should().StartWith("Review scope: Portal – report intake · HTTP/API").And.NotContain("Service Bus");

        cut.Find("[data-testid=iqr-subject-select] select").Change("dev:sb:closed");
        _api.ScopeRequests.Last().Should().Be("dev:sb:closed", "switching recomputes the plan from the backend");
        cut.Find("[data-testid=iqr-review-scope]").TextContent.Should().StartWith("Review scope: Period closed · Service Bus").And.NotContain("HTTP/API");
        cut.Find("[data-testid=iqr-review-scope]").GetAttribute("data-scope").Should().Be("dev:sb:closed");

        cut.Find("[data-testid=iqr-subject-select] select").Change("all");
        _api.ScopeRequests.Last().Should().BeNull("the environment-wide review is an explicit choice");
        cut.Find("[data-testid=iqr-review-scope]").TextContent.Should().StartWith("Review scope: all 2 enabled integrations (environment-wide review)");

        cut.Find("[data-testid=iqr-subject-select] select").Change("dev:sb:closed");
        cut.Find("[data-testid=iqr-run]").Click();
        _api.RunScopes.Last().Should().Be("dev:sb:closed", "the run reviews the selected integration only");
    }

    [Fact]
    public void SourceEvidence_ShowsTheSelectedIntegrationsOwnBinding_BoundIsNotResolved_OthersSecondary()
    {
        ArrangeScoped();
        var all = _api.ScopedReadiness!;
        _api.ScopedReadiness = id => all(id) with
        {
            SourceScopes = [new IntegrationSourceEvidenceScope(IntakeId) { SnapshotBound = true, ArchiveName = "hub.zip", EvidenceResolved = false,
                Summary = "Source bound · integration-specific evidence unresolved: nothing in hub.zip correlates with this integration's producer, consumer, channel or source resource." }],
        };
        _api.EvidenceBindings = [.. _api.EvidenceBindings, new IntegrationEvidenceBinding { EnvironmentId = "dev", IntegrationId = "dev:sb:closed", SnapshotId = Guid.NewGuid(), ArchiveName = "M2LB.zip" }];
        _api.Catalog = _api.Catalog! with { Integrations = [.. _api.Catalog!.Integrations, new IntegrationDefinition { Id = "dev:sb:closed", EnvironmentId = "dev", DisplayName = "Period closed", Kind = IntegrationKind.ServiceBus, Enabled = true }] };
        var cut = Open(IntegrationReviewPrerunPresentation.SetupView);

        var own = cut.Find("[data-testid=iqr-own-evidence]");
        own.GetAttribute("data-integration").Should().Be(IntakeId);
        own.TextContent.Should().Contain("Canonical source evidence for Portal – report intake");
        cut.Find("[data-testid=iqr-own-binding]").TextContent.Should().Contain("hub.zip").And.Contain("bound");
        cut.Find("[data-testid=iqr-own-correlation]").TextContent.Should().Be("Unresolved — source bound · integration-specific evidence not resolved", "bound ≠ resolved");
        cut.Find("[data-testid=iqr-other-bindings]").TextContent.Should().Contain("Other Development integration bindings (1)");
    }

    [Fact]
    public void ContractPair_ShowsBothExactSides_AndDeepLinksTheExactArtifacts()
    {
        ArrangeScoped();
        var snapshot = Guid.NewGuid();
        _api.Pair = new BirkNext.ContractSchemaEvidence.ContractRelationshipReview
        {
            IntegrationId = IntakeId, State = BirkNext.ContractSchemaEvidence.ContractRelationshipResultState.NotAssessable,
            ProducerReference = "portal_report_v2", Consumer = new(snapshot, "hub.zip", "xsd/portal_report_v2.xsd", "portal_report_v2", "2"),
            Summary = "Matching source schema found · relationship not yet confirmed (the producer artifact is not available for a structural comparison).",
        };
        var cut = Open(IntegrationReviewPrerunPresentation.SetupView);
        cut.Find("[data-testid=iqr-contract-pair-consumer]").TextContent.Should().Be("xsd/portal_report_v2.xsd · hub.zip");
        cut.Find("[data-testid=iqr-contract-pair-producer]").TextContent.Should().Be("portal_report_v2 · no exact artifact");
        cut.Find("[data-testid=iqr-contract-pair-state]").TextContent.Should().Be("Insufficient evidence · not assessable");
        cut.Find("[data-testid=iqr-contract-pair-summary]").TextContent.Should().StartWith("Matching source schema found · relationship not yet confirmed");
        cut.Find("[data-testid=iqr-contract-pair-studio]").GetAttribute("href").Should().Be(
            $"/schema-studio?snapshot={snapshot}&contract=xsd%2Fportal_report_v2.xsd&environment=dev&integration={Uri.EscapeDataString(IntakeId)}&from=iqr");
    }
}
