using System.Net;
using Bunit;
using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The Skolenærvær page is a thin domain shell with one section at a time (?tab=). Without a Target Environment it reports Not ready and calls
/// no integration API. With one, it only summarizes the shared journey engine's own readiness and blockers — never recomputed — and hosts the
/// shared panels inside their sections: the run under Rapportmottak, Message Verification in its own tab, history in History.
/// </summary>
public sealed class SkoleNaervaerTestingPageTests : BunitContext
{
    private readonly Mock<IIntegrationCatalogApiService> _integrations = new();
    private readonly FakeJourneys _journeys = new();
    private FrontendAnalysisContext _context = new();

    public SkoleNaervaerTestingPageTests()
    {
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(() => _context);
        Services.AddSingleton(factory.Object);
        Services.AddSingleton(_integrations.Object);
        Services.AddSingleton<IIntegrationJourneysApiService>(_journeys);
        Services.AddSingleton(new Mock<IWorkspaceSessionService>().Object);
        Services.AddSingleton(new Mock<IReportExportService>().Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private void WithTarget(int snapshots = 0)
    {
        _context = new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "DEV" } };
        _integrations.Setup(a => a.GetCatalogAsync(It.IsAny<FrontendAnalysisProfile>(), It.IsAny<CancellationToken>())).ReturnsAsync(new IntegrationCatalog());
        _integrations.Setup(a => a.ListSourceSnapshotsAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync([]);
    }

    private IRenderedComponent<SkoleNaervaerTesting> Open(string? tab = null)
    {
        if (tab is not null) Services.GetRequiredService<NavigationManager>().NavigateTo($"extensions/skolenaervaer?tab={tab}");
        return Render<SkoleNaervaerTesting>();
    }

    [Fact]
    public void WithoutTargetEnvironment_ShowsNotReady_AndCallsNoIntegrationApi()
    {
        var page = Open();

        page.Markup.Should().Contain("Not selected · execution not ready");
        page.Find("[data-testid=snt-journeys-not-ready]").TextContent.Should().Contain("Nothing has been run or assessed");
        page.FindAll("[data-testid=snt-journey-status]").Select(s => s.TextContent).Should().OnlyContain(s => s == "Not ready");
        _integrations.Invocations.Should().BeEmpty();
        _journeys.Calls.Should().BeEmpty();
    }

    [Fact]
    public void WhileTheTargetIsStillLoading_NothingIsCalledNotReady()
    {
        var pending = new TaskCompletionSource<FrontendAnalysisContext>();
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).Returns(pending.Task);
        Services.AddSingleton(factory.Object);

        var page = Render<SkoleNaervaerTesting>();
        page.FindAll("[data-testid=snt-journey-status]").Select(s => s.TextContent).Should().OnlyContain(s => s == "Loading");
        page.FindAll("[data-testid=snt-journeys-not-ready]").Should().BeEmpty("it is not yet known that no Target Environment is selected");

        pending.SetResult(new FrontendAnalysisContext());
        page.WaitForAssertion(() => page.FindAll("[data-testid=snt-journey-status]").Select(s => s.TextContent).Should().OnlyContain(s => s == "Not ready"));
        page.FindAll("[data-testid=snt-journeys-not-ready]").Should().ContainSingle();
    }

    [Fact]
    public void SevenSections_AreDeepLinkable_AndOnlyTheCurrentOneIsShown()
    {
        var page = Open();

        page.FindAll(".snt-tabs a").Select(a => a.TextContent).Should().Equal("Overview", "Utdanningsdata", "Rapportmottak", "Message Verification", "Periodeavslutning", "Architecture Rules", "History");
        // Path-qualified links: with <base href="/"> a bare "?tab" or "#id" would leave the page.
        page.FindAll(".snt-tabs a").Select(a => a.GetAttribute("href")).Should().Equal("extensions/skolenaervaer", "extensions/skolenaervaer?tab=utdanningsdata",
            "extensions/skolenaervaer?tab=rapportmottak", "extensions/skolenaervaer?tab=message-verification", "extensions/skolenaervaer?tab=periodeavslutning",
            "extensions/skolenaervaer?tab=architecture-rules", "extensions/skolenaervaer?tab=history");
        page.FindAll(".snt-tabs a[aria-current=page]").Select(a => a.TextContent).Should().Equal("Overview");
        page.FindAll(".snt-panel").Should().ContainSingle().Which.Id.Should().Be("snt-overview");
        page.FindAll(".snt-links a").Select(a => a.GetAttribute("href")).Should().Equal(
            "/integration-quality-review", "/admin/system-settings?section=target-environments", "/source-analysis", "/test-coverage-review");
    }

    [Fact]
    public void ASectionLink_ShowsThatSection_AndMovesFocusToItsHeading()
    {
        var page = Open();

        page.FindAll(".snt-tabs a").Single(a => a.TextContent == "Rapportmottak").Click();

        page.FindAll(".snt-panel").Should().ContainSingle().Which.Id.Should().Be("snt-rapport");
        JSInterop.VerifyInvoke("birknextFocusElement").Arguments.Should().Equal("snt-rapport-heading");
        page.Find("#snt-rapport-heading").GetAttribute("tabindex").Should().Be("-1");
    }

    [Fact]
    public void Overview_SummarizesTheBackendsReadiness_WithoutRepeatingTheJourneyPanel()
    {
        WithTarget();
        var page = Open();

        page.WaitForAssertion(() => page.FindAll("[data-testid=snt-journey]").Should().HaveCount(4));
        var rows = page.FindAll("[data-testid=snt-journey]").ToDictionary(r => r.GetAttribute("data-journey")!, r => r.QuerySelector("[data-testid=snt-journey-status]")!.TextContent);
        rows.Should().Equal(new Dictionary<string, string>
        {
            ["utdanningsdata"] = "Partial", ["rapportmottak"] = "Not ready", ["message-verification"] = "Not available", ["periodeavslutning"] = "Not configured",
        });
        page.Find("[data-journey=rapportmottak] [data-testid=snt-journey-blockers]").TextContent.Should().Be("2 blockers");
        page.FindAll("[data-testid=snt-top-blockers] li").Select(l => l.TextContent).Should().Equal(
            "Utdanningsdata: SkoleAdapter source · Not ready", "Rapportmottak: Altinn authentication · Not configured", "Rapportmottak: MM source · Not ready",
            "Periodeavslutning: Skolenærvær – Period Closed · Not configured");
        page.FindAll("[data-testid=ijp-panel]").Should().BeEmpty("the overview no longer repeats the shared journey panel");
        page.FindAll("[data-testid=atp-panel], [data-testid=mvp], [data-testid=act-panel]").Should().BeEmpty("no feature is rendered inline on the overview");
        page.FindAll("[data-testid=snt-journey-open]").Select(a => a.GetAttribute("href")).Should().Equal("extensions/skolenaervaer?tab=utdanningsdata",
            "extensions/skolenaervaer?tab=rapportmottak", "extensions/skolenaervaer?tab=message-verification", "extensions/skolenaervaer?tab=periodeavslutning");
    }

    [Fact]
    public void MessageVerification_IsAvailableOnTheOverview_ExactlyWhenTheBackendListsItAsRunnable()
    {
        WithTarget();
        var pack = IntegrationJourneyPanelTests.SamplePack();
        _journeys.Pack = pack with
        {
            Journeys = [.. pack.Journeys.Select(view => view.Journey.JourneyId == "rapportmottak" ? view with { RunnableScenarios = ["message-verification"] } : view)],
        };
        var page = Open();

        page.WaitForAssertion(() => page.Find("[data-journey=message-verification] [data-testid=snt-journey-status]").TextContent.Should().Be("Available (read-only)"));
        page.Find("[data-journey=rapportmottak] [data-testid=snt-journey-status]").TextContent.Should().Be("Not ready", "the read-only scenario does not make the journey ready");
    }

    [Fact]
    public void Rapportmottak_IsActionFirst_BlockersNext_AndEveryPrerequisiteInCollapsedGroups()
    {
        WithTarget();
        var page = Open("rapportmottak");

        page.WaitForAssertion(() => page.Find("[data-testid=snt-status]").TextContent.Should().Contain("Not ready").And.Contain("2 blockers"));
        var order = new[] { "snt-status", "snt-rapport-actions", "snt-rapport-run", "snt-rapport-blockers", "snt-rapport-details" }
            .Select(id => page.Markup.IndexOf($"data-testid=\"{id}\"", StringComparison.Ordinal)).ToList();
        order.Should().OnlyContain(position => position >= 0).And.BeInAscendingOrder("status and actions first, then blockers, then technical details");
        page.FindAll("[data-testid=snt-rapport-actions] button, [data-testid=snt-rapport-actions] a").Select(e => e.TextContent)
            .Should().Equal("Open Rapportmottak run", "Configure Altinn", "Open Target Environment");
        page.Find("[data-testid=snt-configure-altinn]").GetAttribute("href").Should().Be("/integration-quality-review?view=test-configuration");
        page.Find("#snt-rapport").TextContent.Should().Contain("Opening the run page sends nothing");

        var expected = IntegrationJourneyPanelTests.SamplePack().Journeys.Single(view => view.Journey.JourneyId == "rapportmottak").Prerequisites;
        page.FindAll("[data-testid=snt-rapport-blockers] li").Select(li => li.GetAttribute("data-key")).Should().Equal(expected.Where(p => p.Mandatory && p.State != JourneyReadinessState.Ready).Select(p => p.Key));
        page.FindAll("[data-testid=snt-prerequisite]").Select(li => $"{li.GetAttribute("data-key")}={li.GetAttribute("data-state")}")
            .Should().BeEquivalentTo(expected.Select(p => $"{p.Key}={p.State}"), "every prerequisite appears once, with the backend's state");
        page.FindAll("[data-testid=snt-rapport-details] .disclosure-toggle").Should().OnlyContain(t => t.GetAttribute("aria-expanded") == "false", "technical details are collapsed");
        page.Find("[data-testid=snt-group-authentication-toggle]").TextContent.Should().Contain("Authentication").And.Contain("1 of 1 not ready");

        page.Find("[data-testid=snt-rapport-run-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=snt-open-run]").Click();
        page.Find("[data-testid=snt-rapport-run-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        page.FindAll("#snt-rapport [data-testid=ijp-panel]").Should().ContainSingle();
        _journeys.Started.Should().BeEmpty("opening the run page starts nothing");
    }

    private void WithRapportmottakRows(params string[] unresolved)
    {
        var pack = IntegrationJourneyPanelTests.SamplePack();
        _journeys.Pack = pack with
        {
            Journeys = [.. pack.Journeys.Select(view => view.Journey.JourneyId != "rapportmottak" ? view
                : view with { Prerequisites = [.. BirkNext.Web.Tests.Services.SkoleNaervaerReadinessPresentationTests.Rows(unresolved)] })],
        };
    }

    [Fact]
    public void Rapportmottak_ShowsWhatWasResolvedAndFromWhere_AndOnlyTheUnresolvedInput()
    {
        WithTarget();
        WithRapportmottakRows("client-secret");
        var page = Open("rapportmottak");

        page.WaitForAssertion(() => page.Find("[data-testid=snt-tt02-decision]").GetAttribute("data-code").Should().Be("B"));
        page.Find("[data-testid=snt-tt02-decision]").TextContent.Should().Contain("ID-porten configuration incomplete");
        var resolved = page.FindAll("[data-testid=snt-resolved-item]");
        resolved.Should().HaveCount(16, "every other run input was resolved (including the contract change impact)");
        page.Find("[data-testid=snt-resolved-item][data-key=idporten-client] .snt-source").TextContent.Should().Be("Integration Quality Review");
        page.Find("[data-testid=snt-resolved-item][data-key=redirect-uri] .snt-source").TextContent.Should().Be("Local execution config");
        page.Find("[data-testid=snt-resolved-item][data-key=synthetic-data] .snt-source").TextContent.Should().Be("Approved test data");
        page.FindAll("[data-testid=snt-attention-item]").Select(li => li.GetAttribute("data-key")).Should().Equal("client-secret");
        page.Find("[data-testid=snt-status]").TextContent.Should().Contain("1 blocker").And.NotContain("2 blockers", "resolved inputs are not counted");

        page.Find("[data-testid=snt-attention-item] button").TextContent.Should().Be("Configure local execution");
        page.Find("[data-testid=snt-group-local-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=snt-attention-item] button").Click();
        page.Find("[data-testid=snt-group-local-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        page.Find("[data-testid=snt-local-setup]").TextContent.Should().Contain("configure-skolenaervaer-tt02-local.ps1").And.Contain("The run page stores nothing");
        JSInterop.VerifyInvoke("birknextFocusElement").Arguments.Should().Equal("snt-group-local");
    }

    [Fact]
    public void Rapportmottak_HasNoEditableCopyOfConfiguration()
    {
        WithTarget();
        WithRapportmottakRows("idporten-client");
        var page = Open("rapportmottak");

        page.WaitForAssertion(() => page.FindAll("[data-testid=snt-attention-item]").Should().ContainSingle());
        page.Find("[data-testid=snt-attention-item] a").GetAttribute("href").Should().Be("/integration-quality-review?view=test-configuration");
        var outsideRun = page.FindAll("#snt-rapport input, #snt-rapport select, #snt-rapport textarea").Where(field => field.Closest("[data-testid=ijp-panel]") is null && field.Closest("[data-testid=atp-panel]") is null);
        outsideRun.Should().BeEmpty("client id, scopes, redirect, secret, reporter and child are resolved from their owners, never re-entered here");
    }

    [Fact]
    public void Rapportmottak_AllResolved_IsReadyForASupervisedFirstSubmission_Only()
    {
        WithTarget();
        WithRapportmottakRows();
        var page = Open("rapportmottak");

        page.WaitForAssertion(() => page.Find("[data-testid=snt-tt02-decision]").GetAttribute("data-code").Should().Be("A"));
        page.Find("[data-testid=snt-tt02-decision]").TextContent.Should().Contain("Ready for first supervised TT02 submission").And.Contain("explicit confirmation");
        page.Find("[data-testid=snt-nothing-unresolved]").Should().NotBeNull();
        _journeys.Started.Should().BeEmpty("ready never starts a submission");
    }

    [Fact]
    public void MessageVerification_HasItsOwnTab_WithAProperEmptyStateWithoutATarget()
    {
        var without = Open("message-verification");
        without.Find("#snt-verification-heading").TextContent.Should().Be("Message Verification");
        without.Find("[data-testid=snt-verification-not-ready]").TextContent.Should().Contain("Nothing has been read");
        without.Find("[data-testid=snt-verification-not-ready] a").GetAttribute("href").Should().Be("/admin/system-settings?section=target-environments");
        without.FindAll("[data-testid=mvp]").Should().BeEmpty("nothing is loaded without a Target Environment");
    }

    [Fact]
    public void MessageVerificationTab_RendersTheFeature_WithATarget()
    {
        WithTarget();
        var page = Open("message-verification");
        page.WaitForAssertion(() => page.FindAll("#snt-verification [data-testid=mvp]").Should().ContainSingle());
    }

    [Fact]
    public void PeriodClosure_IsAnOperatorAction_AndTheJourneyDetailMountsOnlyWhenOpened()
    {
        WithTarget();
        var page = Open("periodeavslutning");

        page.Find("[data-testid=snt-period-operator]").TextContent.Should().StartWith("Period closure is an operator action")
            .And.Contain("only postpones the lock").And.Contain("BirkNext never locks a period");
        page.Find("#snt-period").TextContent.Should().Contain("Service Bus accepted is not the same as Bufdata processed");
        page.WaitForAssertion(() => page.Find("[data-testid=snt-status]").TextContent.Should().Contain("Not configured"));
        page.FindAll("[data-testid=ijp-panel]").Should().BeEmpty();
        page.Find("[data-testid=snt-open-period-toggle]").Click();
        page.FindAll("#snt-period [data-testid=ijp-panel]").Should().ContainSingle();
    }

    [Fact]
    public void Utdanningsdata_IsConcise_AndUsesTheSharedActiveTestsSummary()
    {
        WithTarget();
        var page = Open("utdanningsdata");

        page.Find("#snt-education .snt-flow").TextContent.Should().Be("BiRK → Debezium → Event Hub → SkoleAdapter → Utdanning");
        page.Find("#snt-education").TextContent.Should().NotContain("Altinn");
        page.WaitForAssertion(() => page.Find("[data-testid=snt-status]").TextContent.Should().Contain("Partial"));
    }

    [Fact]
    public void ArchitectureRules_ShowRuleEvidenceSourceAndStatus_AndThePersonLookupResponsibilities()
    {
        WithTarget();
        var page = Open("architecture-rules");

        var lookup = page.Find("[data-testid=snt-person-lookup]").TextContent;
        lookup.Should().Contain("MM performs transport and intake only; it does not perform the Person lookup.");
        lookup.Should().Contain("Skolenærværsrapport performs the Person lookup for submitted reports.");
        lookup.Should().Contain("Utdanning performs the Person lookup for BiRK education data.");
        page.WaitForAssertion(() => page.FindAll("[data-testid=snt-rule]").Should().HaveCount(2));
        page.FindAll("[data-testid=snt-rule]").Select(r => string.Join("|", r.QuerySelectorAll("td").Select(td => td.TextContent))).Should().Equal(
            "Skolenærværsrapport owns the Person lookup|source.zip|Not assessed", "MM performs no Person lookup|source.zip|Potential deviation");
        page.Find("[data-testid=snt-rule-results]").ParentElement!.GetAttribute("role").Should().Be("region");
    }

    [Fact]
    public void WithTargetButNoSourceSnapshot_ArchitectureRulesAreNotAssessed()
    {
        WithTarget();
        var page = Open();

        page.WaitForAssertion(() => page.Markup.Should().Contain("No source snapshot · architecture rules Not assessed"));
        page.FindAll("[data-testid=snt-journeys-not-ready]").Should().BeEmpty();
    }

    [Fact]
    public void History_IsItsOwnTab_OpeningTheSharedJourneyHistory()
    {
        WithTarget();
        var page = Open("history");

        page.WaitForAssertion(() => page.FindAll("#snt-history [data-testid=ijp-panel]").Should().ContainSingle());
        page.WaitForAssertion(() => page.Find("[data-testid=ijp-tab-history]").GetAttribute("aria-selected").Should().Be("true"));
        _journeys.Calls.Should().Contain("history");
    }

    [Fact]
    public void ReturningFromIdPortenTest_OpensTheRapportmottakRun()
    {
        WithTarget();
        Services.GetRequiredService<NavigationManager>().NavigateTo("extensions/skolenaervaer?altinn-sign-in=done");
        var page = Render<SkoleNaervaerTesting>();

        page.FindAll(".snt-panel").Should().ContainSingle().Which.Id.Should().Be("snt-rapport");
        page.Find("[data-testid=snt-rapport-run-toggle]").GetAttribute("aria-expanded").Should().Be("true");
    }

    private sealed class FakeJourneys : IIntegrationJourneysApiService
    {
        public IntegrationJourneyPackView Pack { get; set; } = IntegrationJourneyPanelTests.SamplePack();
        public List<string> Calls { get; } = [];
        public List<IntegrationJourneyRunRequest> Started { get; } = [];

        public Task<ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>> PacksAsync(string environmentId, CancellationToken ct = default)
        {
            Calls.Add("packs");
            return Task.FromResult(new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>([Pack], null, HttpStatusCode.OK));
        }

        public Task<ActiveEventApiResult<ArchitectureRuleReport>> RulesAsync(string environmentId, string packId, CancellationToken ct = default)
        {
            Calls.Add("rules");
            return Task.FromResult(new ActiveEventApiResult<ArchitectureRuleReport>(new ArchitectureRuleReport(packId, Guid.NewGuid(), "source.zip",
            [
                new("rapport-person", "Skolenærværsrapport owns the Person lookup", "Skolenærværsrapport", "Person", ArchitectureRuleExpectation.MustDependOn, ArchitectureRuleOutcome.NotAssessed,
                    "Not source verified.", [], "Source Analysis snapshot source.zip"),
                new("mm-no-person", "MM performs no Person lookup", "MM", "Person", ArchitectureRuleExpectation.MustNotDependOn, ArchitectureRuleOutcome.PotentialDeviation,
                    "Potential deviation.", [], "Source Analysis snapshot source.zip"),
            ], []), null, HttpStatusCode.OK));
        }

        public Task<ActiveEventApiResult<IntegrationJourneyRun>> StartAsync(IntegrationJourneyRunRequest request, CancellationToken ct = default)
        {
            Started.Add(request);
            return Task.FromResult(new ActiveEventApiResult<IntegrationJourneyRun>(null, "Not in this test.", HttpStatusCode.BadRequest));
        }

        public Task<ActiveEventApiResult<IntegrationJourneyRun>> GetAsync(Guid runId, CancellationToken ct = default) =>
            Task.FromResult(new ActiveEventApiResult<IntegrationJourneyRun>(null, "Not found.", HttpStatusCode.NotFound));

        public Task<ActiveEventApiResult<IReadOnlyList<IntegrationJourneyRunSummary>>> HistoryAsync(string environmentId, string packId, string? journeyId, JourneyRunState? state, CancellationToken ct = default)
        {
            Calls.Add("history");
            return Task.FromResult(new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyRunSummary>>([], null, HttpStatusCode.OK));
        }
    }
}
