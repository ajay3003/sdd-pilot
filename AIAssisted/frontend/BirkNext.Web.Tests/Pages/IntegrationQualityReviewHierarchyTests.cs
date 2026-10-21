using BirkNext.Integrations;
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
/// IQR pre-run information hierarchy: Review overview → Evidence → Integration areas → Active tests, summary-first. The semantic snapshot
/// (every model-derived value the page states) was captured before the restructuring and must stay identical — presentation only.
/// </summary>
public sealed class IntegrationQualityReviewHierarchyTests : BunitContext
{
    private readonly FakeIntegrationCatalogApi _api = new() { Catalog = M2lbFixture.Catalog(), Result = M2lbFixture.Result() };

    public IntegrationQualityReviewHierarchyTests()
    {
        _api.Readiness = M2lbFixture.Readiness() with
        {
            ServiceBus = [new ServiceBusReadiness { PlatformId = "dev:servicebus:m2lb", PlatformName = "M2LB DEV Service Bus", Queues = 4, Topics = 11, Subscriptions = 8, RoutesMatched = 3, RoutesTotal = 5,
                RuntimeState = IntegrationEvidenceState.NotConfigured, RuntimeReason = "The Azure subscription id of the namespace is not configured." }],
            Scim = [new ScimReadiness { PlatformId = "dev:scim:m2lb", PlatformName = "M2LB Entra SCIM Provisioning", SourceAnalyzed = false, RuntimeState = IntegrationEvidenceState.NotConfigured, RuntimeReason = "No SCIM base URL is configured." }],
            ApplicationMessaging =
            [
                new() { ApplicationId = "M2LB.Tjeneste.Api", Technology = MessagingTechnology.Wolverine, Detection = MessagingDetection.Confirmed, HandlerMapping = MessagingFactState.Available, RetryPolicy = MessagingFactState.NotFound, Outbox = MessagingFactState.Configured, ErrorHandling = MessagingFactState.NotFound, RuntimeState = IntegrationEvidenceState.NotConfigured, RuntimeReason = "Azure runtime evidence is not enabled." },
                new() { ApplicationId = "M2LB.Hendelse.Api", Technology = MessagingTechnology.Wolverine, Detection = MessagingDetection.Likely, HandlerMapping = MessagingFactState.NotApplicable, RetryPolicy = MessagingFactState.NotFound, Outbox = MessagingFactState.NotAssessable, ErrorHandling = MessagingFactState.NotFound, BoundConsumer = "Hendelse BiRK Adapter", BoundTopics = 2, RuntimeState = IntegrationEvidenceState.NotConfigured, RuntimeReason = "Azure runtime evidence is not enabled." },
            ],
        };
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext
        {
            ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.bufetat.no/" },
        });
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        var personCdc = "dev:eventhub:birk-cdc:dbo.Person";
        ActiveEventScenarioDescriptor[] scenarios =
        [
            new() { ExtensionId = "m2lb.person", ScenarioId = "person.normal.create", DisplayName = "Normal Person", RequiredTransportType = "EventHub", ExpectedEventCount = 1 },
            new() { ExtensionId = "m2lb.person", ScenarioId = "person.same-personpk-replay", DisplayName = "Same PersonPK replay", RequiredTransportType = "EventHub", ReplayKind = ActiveEventReplayKind.ExactReplay, ExpectedEventCount = 3 },
            new() { ExtensionId = "m2lb.person", ScenarioId = "person.invalid-then-valid", DisplayName = "Invalid Person → valid Person", RequiredTransportType = "EventHub", ReplayKind = ActiveEventReplayKind.ControlAfterInvalid, ExpectedEventCount = 2 },
        ];
        var activeTests = new Mock<IActiveEventsApiService>();
        activeTests.Setup(a => a.TrustAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActiveEventApiResult<ActiveEventEnvironmentTrust>(new() { Trusted = true, DisplayName = "Dev", EnvironmentType = "Development", ExecutionAllowed = true }, null));
        activeTests.Setup(a => a.ProvidersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActiveEventApiResult<IReadOnlyList<ActiveEventProviderSummary>>(
                [new() { ExtensionId = "m2lb.person", DisplayName = "M2LB Person CDC", Resources = ["dbo.Person"], ApplicableIntegrationIds = [personCdc], Scenarios = scenarios }], null));
        activeTests.Setup(a => a.ScenariosAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActiveEventApiResult<IReadOnlyList<ActiveEventScenarioDescriptor>>(scenarios, null));
        activeTests.Setup(a => a.ReadinessAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActiveEventApiResult<ActiveEventReadiness>(new()
            {
                CanRun = false,
                Checks = [new("environment", "Trusted environment (DEV/QA only)", ActiveEventReadinessState.Ready, "Development", ActiveEventReadinessCategory.TrustedEnvironment),
                          new("transport", "Transport provider", ActiveEventReadinessState.Blocked, "No sender identity is configured.", ActiveEventReadinessCategory.Transport),
                          new("source-contract", "Source contract binding", ActiveEventReadinessState.Blocked, "No source analysis is selected.", ActiveEventReadinessCategory.SourceContract)],
            }, null));
        Services.AddSingleton(activeTests.Object);
        Services.AddSingleton(new ActiveEventAuthenticationState(true, "Configured (test)."));
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
        Services.AddSingleton(context.Object);
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton<IReportExportService, ReportExportService>();
        Services.AddSingleton<RuntimeReviewSessionService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<IntegrationQualityReview> Open() => Render<IntegrationQualityReview>();

    /// <summary>Every model-derived value the pre-run page states, in a stable textual form.</summary>
    private static string Semantics(IRenderedComponent<IntegrationQualityReview> cut)
    {
        string T(string id) => string.Join("|", cut.FindAll($"[data-testid={id}]").Select(e => e.TextContent.Trim()));
        string A(string id, string attribute) => string.Join("|", cut.FindAll($"[data-testid={id}]").Select(e => e.GetAttribute(attribute)));
        return string.Join("\n",
            "readiness: review " + T("iqr-review-readiness") + " / active tests " + T("iqr-active-tests-readiness"),
            "headline: " + A("iqr-readiness", "data-headline") + " · " + A("iqr-readiness", "data-state"),
            "attention: " + string.Join(" ; ", cut.FindAll("[data-testid=iqr-attention-item]").Select(i => $"{i.GetAttribute("data-key")}={i.QuerySelector(".iqr-attention-value")!.TextContent.Trim()}")),
            "run: " + T("iqr-run"),
            "domains: " + string.Join(" ; ", cut.FindAll("[data-domain][data-readiness]").Select(d => $"{d.GetAttribute("data-domain")}={d.GetAttribute("data-readiness")}").Distinct()),
            "runtime: " + T("iqr-evidence-count") + " · " + string.Join(",", cut.FindAll("[data-testid=iqr-evidence-source]").Select(s => $"{s.GetAttribute("data-source")}:{s.GetAttribute("data-state")}")),
            "systems: " + T("iqr-system-topics") + " · " + T("iqr-system-consumers") + " · " + T("iqr-system-groups") + " · " + T("iqr-system-contracts"),
            "servicebus: " + T("iqr-servicebus-topology") + " · " + T("iqr-servicebus-runtime"),
            "scim: " + T("iqr-scim-source") + " · " + T("iqr-scim-runtime") + " · " + T("iqr-scim-mutation"),
            "messaging: " + string.Join(",", cut.FindAll("[data-testid=iqr-messaging-app]").Select(a => $"{a.GetAttribute("data-application")}:{a.QuerySelector("[data-testid=iqr-messaging-detection]")!.TextContent.Trim()}")));
    }

    private const string ExpectedSemantics = """
        readiness: review Partially reviewable / active tests Not known
        headline: Can run with limitations · Partially reviewable
        attention: mappings=15 need confirmation ; runtime=0 of 4 available ; contracts=Not configured ; groups=Not configured for 16 topics ; servicebus-runtime=Not configured
        run: Run review
        domains: Configuration=Ready ; Connectivity=Limited ; Contract=NotAssessable ; MessageFlow=NotAssessable ; Reliability=NotAssessable ; ErrorHandling=NotAssessable ; Security=Limited ; Observability=Limited ; Performance=NotAssessable ; DataQuality=NotAssessable
        runtime: 0 of 4 available · AzureMetadata:NotConfigured,AzureResourceManager:NotConfigured,CheckpointStore:NotConfigured,ApplicationInsights:NotConfigured
        systems: 16 · 1 confirmed · 8 suggested · 7 unconfirmed · Not configured for 16 topics · Not configured
        servicebus: Configured · Not configured
        scim: Source not analyzed · Not configured · Not configured
        messaging: M2LB.Tjeneste.Api:Configured,M2LB.Hendelse.Api:Likely
        """;

    [Fact]
    public void SemanticOutputIsIdenticalToBeforeTheRestructuring()
    {
        var cut = Open();
        var semantics = Semantics(cut);
        if (Environment.GetEnvironmentVariable("IQR_SEMANTICS_OUT") is { } path) File.WriteAllText(path, semantics);
        // Line-ending independent: a CRLF checkout must not change the snapshot.
        semantics.Should().Be(ExpectedSemantics.ReplaceLineEndings("\n"), "the restructuring changes presentation only");
    }

    private static int Position(IRenderedComponent<IntegrationQualityReview> cut, string testId)
    {
        var all = cut.FindAll("[data-testid]").Select(e => e.GetAttribute("data-testid")).ToList();
        all.Should().Contain(testId);
        return all.IndexOf(testId);
    }

    [Fact]
    public void FourViews_OverviewFirst_EachGroupInItsOwnView_InTheSameOrder()
    {
        var cut = Open();
        cut.FindAll("[data-testid=iqr-views] a").Select(a => a.TextContent).Should().Equal("Overview", "Setup & Evidence", "Review", "Test & Verify");
        cut.FindAll("[data-testid=iqr-views] a").Select(a => a.TextContent).Should().NotContain(["Extensions", "Review setup", "Review coverage"], "no technical or duplicate top-level views");
        cut.Find("[data-testid=iqr-view-overview]").GetAttribute("aria-current").Should().Be("page");
        cut.FindAll("[data-testid=iqr-views] a[aria-current]").Should().ContainSingle("only the current view is marked");
        cut.Find("[data-testid=iqr-group-overview]").HasAttribute("hidden").Should().BeFalse();
        cut.Find("[data-testid=iqr-group-evidence]").HasAttribute("hidden").Should().BeTrue("one view at a time");
        cut.Find("[data-testid=iqr-group-coverage]").HasAttribute("hidden").Should().BeTrue();
        var order = new[]
        {
            "iqr-group-overview", "iqr-readiness",
            "iqr-group-evidence", "iqr-evidence-stages", "iqr-source-evidence", "iqr-contracts", "iqr-evidence-sources",
            "iqr-setup-advanced", "iqr-systems", "iqr-servicebus", "iqr-scim", "iqr-messaging",
            "iqr-group-coverage", "iqr-domain-readiness",
        }.Select(id => Position(cut, id)).ToList();
        order.Should().BeInAscendingOrder();
        cut.Find("[data-testid=iqr-readiness]").GetAttribute("data-state").Should().Be("Partially reviewable");
        cut.Find("[data-testid=iqr-review-readiness]").TextContent.Should().Be("Partially reviewable", "review-scoped wording, never \"Ready\"");
        cut.Find("[data-testid=iqr-active-tests-readiness]").Should().NotBeNull("active-test readiness is a separate answer");
        cut.Find("[data-testid=iqr-run]").TextContent.Trim().Should().Be("Run review", "the run gate stays in the overview");
        cut.Find("[data-testid=iqr-group-overview]").TextContent.Should().Contain("Read-only review. No messages are published, consumed or changed.");
        cut.FindAll("[data-testid=iqr-group-overview] [data-testid=iqr-run]").Should().HaveCount(1);
        cut.Find("[data-testid=iqr-setup-advanced-body]").HasAttribute("hidden").Should().BeTrue("systems, transports and provisioning detail is advanced");
        cut.FindAll("[data-testid=act-panel]").Should().BeEmpty("the test modules are not mounted, and load nothing, outside Test & Verify");
        cut.FindAll("[data-testid=mfr-panel]").Should().BeEmpty();
    }

    [Theory]
    [InlineData("setup-evidence", "setup-evidence", "iqr-group-evidence")]
    [InlineData("integrations", "setup-evidence", "iqr-group-evidence")]
    [InlineData("evidence", "setup-evidence", "iqr-group-evidence")]
    [InlineData("review", "review", "iqr-group-coverage")]
    [InlineData("coverage", "review", "iqr-group-coverage")]
    [InlineData("setup", "overview", "iqr-group-overview")]
    public void AViewLink_AndItsLegacyName_ShowOnlyThatView(string requested, string view, string group)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"integration-quality-review?view={requested}");
        var cut = Open();
        cut.Find($"[data-testid=iqr-view-{view}]").GetAttribute("aria-current").Should().Be("page");
        cut.Find($"[data-testid={group}]").HasAttribute("hidden").Should().BeFalse();
        foreach (var other in new[] { "iqr-group-overview", "iqr-group-evidence", "iqr-group-coverage" }.Where(g => g != group))
            cut.Find($"[data-testid={other}]").HasAttribute("hidden").Should().BeTrue();
    }

    [Theory]
    [InlineData("test-verify")]
    [InlineData("active-tests")]
    [InlineData("test-configuration")]
    [InlineData("extensions")]
    public void TestAndVerify_HoldsExecutionPacksJourneysAndActiveTests_SeparateFromReviewReadiness(string view)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"integration-quality-review?view={view}");
        var cut = Open();
        cut.Find("[data-testid=iqr-view-test-verify]").GetAttribute("aria-current").Should().Be("page", "older Active tests / Extensions links open Test & Verify");
        new[] { "iqr-group-active-tests-view", "iqr-active-execution", "iqr-active-test-readiness-note", "iqr-test-packs", "iqr-journeys-summary", "act-panel", "iqr-group-test-configuration" }
            .Select(id => Position(cut, id)).Should().BeInAscendingOrder();
        cut.Find("[data-testid=iqr-active-test-readiness-note]").TextContent.Should().Contain("Active-test readiness").And.Contain("separate from").And.Contain("review readiness");
        cut.Find("[data-testid=iqr-active-execution]").TextContent.Should().NotContain("Trusted environment").And.NotContain("Not trusted");
        cut.Find("[data-testid=iqr-provider-configuration-toggle]").GetAttribute("aria-expanded").Should().Be(view == "extensions" ? "true" : "false",
            "provider configuration is secondary; an old Extensions link opens it");
        cut.Find("#act-heading").TextContent.Should().Be("Active tests");
        cut.Find("[data-testid=act-summary-scenarios]").TextContent.Should().Be("3 available");
        cut.Find("[data-testid=act-summary-scenario]").TextContent.Should().Be("Normal Person");
        cut.Find("[data-testid=act-summary-execution]").TextContent.Should().Be("Blocked", "the backend readiness decides — never inferred here");
        cut.FindAll("[data-testid=act-summary-blockers] li").Select(l => l.TextContent).Should().Equal("Transport provider", "Source contract binding", "Authorization (ActiveEventExecute)");
        var toggle = cut.Find("[data-testid=act-open]");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.TextContent.Should().Be("Open active tests");
        cut.Find("[data-testid=act-runner]").HasAttribute("hidden").Should().BeTrue();
        cut.Find("[data-testid=act-notice]").Should().NotBeNull("the safety notice and every guard stay in the runner");
        toggle.Click();
        cut.Find("[data-testid=act-open]").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("[data-testid=act-runner]").HasAttribute("hidden").Should().BeFalse();
        cut.Find("[data-testid=iqr-open-journeys-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.FindAll("[data-testid=ijp-panel]").Should().BeEmpty("the journey module mounts only when opened");
    }

    [Fact]
    public void TestPacks_AndProviderConfiguration_LiveInTestAndVerify_NeverAsExtensions()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("integration-quality-review?view=test-verify");
        var cut = Open();
        new[] { "iqr-test-packs", "iqr-group-test-configuration", "mfr-panel" }.Select(id => Position(cut, id)).Should().BeInAscendingOrder();
        cut.Markup.Should().NotContain(">Extensions<");
        // Provider wording belongs to the provider only: the generic Setup & Evidence view names contract/reference evidence, not a provider.
        cut.Find("[data-testid=iqr-group-evidence]").TextContent.Should().NotContain("Altinn");
        cut.Find("[data-testid=iqr-contracts]").TextContent.Should().Contain("Contract/reference evidence");
    }

    [Fact]
    public void BrowserBackAndForward_FollowTheViewInTheUrl()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("integration-quality-review?view=review");
        var cut = Open();
        cut.Find("[data-testid=iqr-view-review]").GetAttribute("aria-current").Should().Be("page");
        navigation.NavigateTo("integration-quality-review?view=setup-evidence");
        cut.WaitForAssertion(() => cut.Find("[data-testid=iqr-view-setup-evidence]").GetAttribute("aria-current").Should().Be("page"));
        navigation.NavigateTo("integration-quality-review?view=review");
        cut.WaitForAssertion(() => cut.Find("[data-testid=iqr-group-coverage]").HasAttribute("hidden").Should().BeFalse("going back restores the earlier view"));
        cut.Find("[data-testid=iqr-view-review]").GetAttribute("href").Should().EndWith("view=review", "each view is a real link, so history works");
    }

    [Theory]
    [InlineData("iqr-source-details")]
    [InlineData("iqr-evidence-details")]
    [InlineData("iqr-servicebus-details")]
    [InlineData("iqr-scim-details")]
    [InlineData("iqr-messaging-details")]
    [InlineData("iqr-limitation-details")]
    public void DetailIsCollapsedByDefaultButReachable(string disclosure)
    {
        var cut = Open();
        cut.Find($"[data-testid={disclosure}-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find($"[data-testid={disclosure}-body]").HasAttribute("hidden").Should().BeTrue();
        cut.Find($"[data-testid={disclosure}-toggle]").Click();
        cut.Find($"[data-testid={disclosure}-body]").HasAttribute("hidden").Should().BeFalse();
    }

    [Fact]
    public void DetailListsLiveInsideTheirDisclosures_NotOnTheOverview()
    {
        var cut = Open();
        cut.Find("[data-testid=iqr-source-details-body] [data-testid=iqr-source-setup]").Should().NotBeNull();
        cut.FindAll("[data-testid=iqr-source-setup] h2").Should().BeEmpty("the summary card carries the Source evidence heading");
        cut.FindAll("[data-testid=iqr-evidence-details-body] [data-testid=iqr-evidence-source]").Should().HaveCount(4);
        cut.FindAll("[data-testid=iqr-messaging-details-body] [data-testid=iqr-messaging-app]").Should().HaveCount(2);
        cut.FindAll("[data-testid=iqr-group-overview] table").Should().BeEmpty("Review setup holds the run state only; the domain table is Review coverage");
        cut.FindAll("[data-testid=iqr-group-coverage] table").Should().ContainSingle().Which.GetAttribute("data-testid").Should().Be("iqr-domain-table");
        cut.FindAll("[data-testid=iqr-group-overview] [data-testid=iqr-evidence-detail-table]").Should().BeEmpty();
    }

    [Fact]
    public void WhatCanBeReviewed_IsMasterDetail_OneOpenAtATime()
    {
        var cut = Open();
        cut.FindAll("[data-testid=iqr-domain-table] thead th").Select(h => h.TextContent).Should().Equal("Review area", "Status", "Main limitation", "Action");
        cut.FindAll("[data-testid=iqr-domain-readiness-card]").Should().HaveCount(10);
        cut.Find("[data-testid=iqr-domain-detail]").HasAttribute("hidden").Should().BeTrue("no detail is open by default");
        cut.FindAll("[data-testid=iqr-domain-toggle]").Should().OnlyContain(t => t.GetAttribute("aria-expanded") == "false");

        IReadOnlyList<string?> Expanded() => cut.FindAll("[data-testid=iqr-domain-toggle]").Select(t => t.GetAttribute("aria-expanded")).ToList();
        void Toggle(string domain) => cut.FindAll("[data-testid=iqr-domain-readiness-card]").Single(r => r.GetAttribute("data-domain") == domain).QuerySelector("[data-testid=iqr-domain-toggle]")!.Click();

        Toggle("Connectivity");
        Expanded().Count(e => e == "true").Should().Be(1);
        cut.Find("[data-testid=iqr-domain-detail-title]").TextContent.Should().StartWith("Connectivity");
        Toggle("Security");
        Expanded().Count(e => e == "true").Should().Be(1, "opening another domain closes the previous one");
        cut.Find("[data-testid=iqr-domain-detail-title]").TextContent.Should().StartWith("Security");
        Toggle("Security");
        cut.Find("[data-testid=iqr-domain-detail]").HasAttribute("hidden").Should().BeTrue();
        cut.FindAll("[data-testid=iqr-domain-toggle]").Should().OnlyContain(t => t.GetAttribute("aria-controls") == "iqr-domain-detail");
    }

    [Fact]
    public void IntegrationAreasSummarise_WithLinksToTheirDetail()
    {
        var cut = Open();
        cut.Find("[data-testid=iqr-system-review-mappings]").GetAttribute("href").Should().EndWith("focus=mappings");
        cut.Find("[data-testid=iqr-servicebus-manage]").GetAttribute("href").Should().EndWith("focus=servicebus");
        cut.Find("[data-testid=iqr-scim-manage]").GetAttribute("href").Should().EndWith("focus=scim");
        cut.Find("[data-testid=iqr-servicebus-reason]").TextContent.Should().Be("The Azure subscription id of the namespace is not configured.");
        cut.Find("[data-testid=iqr-scim-reason]").TextContent.Should().Be("No SCIM base URL is configured.");
        cut.Find("[data-testid=iqr-messaging-summary-apps]").TextContent.Should().Be("2");
        cut.Find("[data-testid=iqr-messaging-summary-detection]").TextContent.Should().Be("1 configured · 1 not confirmed");
        cut.Find("[data-testid=iqr-messaging-summary-bound]").TextContent.Should().Be("1 of 2");
        cut.Find("[data-testid=iqr-messaging-summary-runtime]").TextContent.Should().Be("0 of 2 available");
        cut.Find("[data-testid=iqr-source-summary]").TextContent.Should().Be("No source analysis yet · none bound to an integration.");
    }
}
