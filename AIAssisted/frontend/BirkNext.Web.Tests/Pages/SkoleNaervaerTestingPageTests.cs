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
/// The Skolenærvær page is the reference test pack's page, rendered from the pack's generic descriptor: one grouped navigation (Overview ·
/// Journeys · Verify · History), one view at a time (?tab=). Without a Target Environment it reports Not ready and calls no environment-scoped
/// API. With one, it summarizes the shared engines' own readiness — every status naming its dimension — groups blockers by their category
/// metadata, hands Utdanningsdata off to the shared Active Event tests, embeds the shared journey panel only as a nav-less view, and reads the
/// canonical Integration Quality Review evidence binding instead of choosing a snapshot.
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

    private void WithTarget(params IntegrationEvidenceBinding[] bindings)
    {
        _context = new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "DEV" } };
        _integrations.Setup(a => a.GetCatalogAsync(It.IsAny<FrontendAnalysisProfile>(), It.IsAny<CancellationToken>())).ReturnsAsync(new IntegrationCatalog());
        _integrations.Setup(a => a.ListSourceSnapshotsAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _integrations.Setup(a => a.ListEvidenceBindingsAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(bindings);
    }

    private IRenderedComponent<SkoleNaervaerTesting> Open(string? tab = null)
    {
        if (tab is not null) Services.GetRequiredService<NavigationManager>().NavigateTo($"extensions/skolenaervaer?tab={tab}");
        return Render<SkoleNaervaerTesting>();
    }

    private static string State(AngleSharp.Dom.IElement cell) => cell.QuerySelector(".snt-pill")!.TextContent;

    // ── Navigation ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void One_grouped_navigation_from_the_pack_descriptor_with_deep_links()
    {
        var page = Open();

        page.FindAll("[data-testid=snt-nav]").Should().ContainSingle("the page has exactly one primary navigation");
        page.FindAll("[data-testid=snt-nav-group]").Select(group => group.GetAttribute("aria-label")).Should().Equal("Overview", "Journeys", "Verify", "History");
        page.FindAll(".snt-nav-caption").Should().OnlyContain(caption => caption.TagName == "SPAN" && caption.GetAttribute("aria-hidden") == "true" && !caption.HasAttribute("href"));
        page.FindAll(".snt-nav a").Select(a => a.TextContent).Should().Equal("Overview", "Utdanningsdata", "Rapportmottak", "Periodeavslutning", "Message Verification", "Architecture Rules", "History");
        // Path-qualified links: with <base href="/"> a bare "?tab" or "#id" would leave the page.
        page.FindAll(".snt-nav a").Select(a => a.GetAttribute("href")).Should().Equal("extensions/skolenaervaer", "extensions/skolenaervaer?tab=utdanningsdata",
            "extensions/skolenaervaer?tab=rapportmottak", "extensions/skolenaervaer?tab=periodeavslutning", "extensions/skolenaervaer?tab=message-verification",
            "extensions/skolenaervaer?tab=architecture-rules", "extensions/skolenaervaer?tab=history");
        page.FindAll(".snt-nav a[aria-current=page]").Select(a => a.TextContent).Should().Equal("Overview");
        page.FindAll(".snt-panel").Should().ContainSingle().Which.Id.Should().Be("snt-overview");
        _journeys.Calls.Should().Contain("test-packs", "the navigation comes from the pack's own descriptor");
    }

    [Fact]
    public void The_navigation_follows_the_descriptor_not_hard_coded_views()
    {
        _journeys.Descriptor = FakeJourneys.SkolenaervaerDescriptor() with
        {
            Navigation = [.. FakeJourneys.SkolenaervaerDescriptor().Navigation.Where(group => group.Label != "Verify")],
        };
        var page = Open();
        page.FindAll(".snt-nav a").Select(a => a.TextContent).Should().NotContain(["Message Verification", "Architecture Rules"]);
    }

    [Fact]
    public void ASectionLink_ShowsThatSection_AndMovesFocusToItsHeading()
    {
        var page = Open();

        page.FindAll(".snt-nav a").Single(a => a.TextContent == "Rapportmottak").Click();

        page.FindAll(".snt-panel").Should().ContainSingle().Which.Id.Should().Be("snt-rapport");
        JSInterop.VerifyInvoke("birknextFocusElement").Arguments.Should().Equal("snt-rapport-heading");
        page.Find("#snt-rapport-heading").GetAttribute("tabindex").Should().Be("-1");
    }

    [Theory]
    [InlineData("rapportmottak")]
    [InlineData("periodeavslutning")]
    [InlineData("history")]
    public void No_nested_journey_navigation_is_embedded(string tab)
    {
        WithTarget();
        var page = Open(tab);
        if (tab == "rapportmottak") { page.WaitForAssertion(() => page.Find("[data-testid=snt-open-run]")); page.Find("[data-testid=snt-open-run]").Click(); }

        page.WaitForAssertion(() => page.FindAll("[data-testid=ijp-panel]").Should().ContainSingle());
        page.Find("[data-testid=ijp-panel]").GetAttribute("data-embedded").Should().Be("true");
        page.FindAll("[data-testid=ijp-tabs], [data-testid=ijp-packs], [role=tablist], #ijp-heading").Should().BeEmpty("the shared journey panel is hosted without its own navigation");
        page.FindAll("nav").Should().ContainSingle("one navigation only");
    }

    // ── Without / while loading a Target Environment ────────────────────────────────────────────────────────────────

    [Fact]
    public void WithoutTargetEnvironment_ShowsNotReady_AndCallsNoEnvironmentScopedApi()
    {
        var page = Open();

        page.Markup.Should().Contain("Not selected · execution not ready");
        page.Find("[data-testid=snt-journeys-not-ready]").TextContent.Should().Contain("Nothing has been run or assessed");
        page.FindAll("[data-testid=snt-journey-status]").Select(State).Should().OnlyContain(s => s == "Not ready");
        page.FindAll("[data-testid=snt-journey-status]").Should().OnlyContain(cell => cell.TextContent.StartsWith("Execution readiness:"), "a status always names its dimension");
        _integrations.Invocations.Should().BeEmpty();
        _journeys.Calls.Should().Equal("test-packs");
    }

    [Fact]
    public void WhileTheTargetIsStillLoading_NothingIsCalledNotReady()
    {
        var pending = new TaskCompletionSource<FrontendAnalysisContext>();
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).Returns(pending.Task);
        Services.AddSingleton(factory.Object);

        var page = Render<SkoleNaervaerTesting>();
        page.FindAll("[data-testid=snt-journey-status]").Select(State).Should().OnlyContain(s => s == "Loading");
        page.FindAll("[data-testid=snt-journeys-not-ready]").Should().BeEmpty("it is not yet known that no Target Environment is selected");

        pending.SetResult(new FrontendAnalysisContext());
        page.WaitForAssertion(() => page.FindAll("[data-testid=snt-journey-status]").Select(State).Should().OnlyContain(s => s == "Not ready"));
        page.FindAll("[data-testid=snt-journeys-not-ready]").Should().ContainSingle();
    }

    // ── Overview ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Overview_summarizes_each_journey_by_dimension_without_embedding_other_features()
    {
        WithTarget();
        var page = Open();

        page.WaitForAssertion(() => page.FindAll("[data-testid=snt-journey]").Should().HaveCount(3));
        string Cell(string journey, string label) => State(page.Find($"[data-journey={journey}] td[data-label='{label}']"));
        Cell("utdanningsdata", "Configuration").Should().Be("Not applicable", "the sample journey declares no integration requirement");
        Cell("utdanningsdata", "Source evidence").Should().Be("Missing");
        Cell("utdanningsdata", "Execution readiness").Should().Be("Blocked");
        Cell("periodeavslutning", "Source evidence").Should().Be("Not applicable", "it declares no source-evidence prerequisite");
        page.Find("[data-journey=rapportmottak] [data-testid=snt-journey-blockers]").TextContent.Should().Be("2");
        page.Find("[data-journey=utdanningsdata] th").TextContent.Should().Contain("BiRK → Debezium → Event Hub → SkoleAdapter → Utdanning");
        page.FindAll("[data-testid=ijp-panel], [data-testid=atp-panel], [data-testid=mvp], [data-testid=act-panel]").Should().BeEmpty("no feature is rendered inline on the overview");
        page.FindAll("[data-testid=snt-journey-open]").Select(a => a.GetAttribute("href")).Should().Equal("extensions/skolenaervaer?tab=utdanningsdata",
            "extensions/skolenaervaer?tab=rapportmottak", "extensions/skolenaervaer?tab=periodeavslutning");
        page.FindAll("[data-testid=snt-journeys] th[role=columnheader]").Select(th => th.TextContent).Should().Equal("Journey", "Configuration", "Source evidence", "Execution readiness", "Blockers", "Action");
    }

    private sealed class FakeTopology(IReadOnlyList<BirkNext.IntegrationTopology.TopologyPathResult> paths) : IIntegrationTopologyApiService
    {
        public Task<(BirkNext.IntegrationTopology.IntegrationTopologyView? View, string? Error)> GetAsync(string environmentId, IReadOnlyList<BirkNext.IntegrationTopology.TopologyObservation> observations, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(BirkNext.IntegrationTopology.IntegrationTopologyView? View, string? Error)> DiscoverAsync(string environmentId, IReadOnlyList<BirkNext.IntegrationTopology.TopologyObservation> observations, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(IReadOnlyList<BirkNext.IntegrationTopology.TopologyPathResult>? Paths, string? Error)> PathsAsync(string environmentId, string profileId, IReadOnlyList<BirkNext.IntegrationTopology.TopologyObservation> observations, CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<BirkNext.IntegrationTopology.TopologyPathResult>?, string?)>((paths, null));
    }

    [Fact]
    public void Expected_topology_is_collapsed_to_counts_and_expected_is_never_observed()
    {
        static BirkNext.IntegrationTopology.TopologyPathStep Step(string label, BirkNext.IntegrationTopology.TopologyPathState state, bool observed) =>
            new(label, "A", "B", state, default, observed, null, "detail");
        Services.AddSingleton<IIntegrationTopologyApiService>(new FakeTopology(
        [
            new("skolenaervaer", "rapport", "Rapportmottak", [Step("Altinn → MU", BirkNext.IntegrationTopology.TopologyPathState.Unresolved, false), Step("MU → MM", BirkNext.IntegrationTopology.TopologyPathState.Unresolved, false)]),
            new("skolenaervaer", "education", "Utdanningsdata", [Step("Event Hub → SkoleAdapter", BirkNext.IntegrationTopology.TopologyPathState.Resolved, false)]),
        ]));
        WithTarget();
        var page = Open();
        page.WaitForAssertion(() => page.Find("[data-testid=tp-summary]").TextContent.Should().Be("3 expected connections · 2 unresolved"));
        page.Find("[data-testid=snt-topology] h3").TextContent.Should().Be("Expected topology");
        page.Find("[data-testid=tp-details-toggle]").GetAttribute("aria-expanded").Should().Be("false", "the expected topology is collapsed by default");
        page.Find("[data-testid=tp-open]").GetAttribute("href").Should().Be("/integration-topology");
        page.Find("[data-testid=tp-details-toggle]").Click();
        page.FindAll("[data-testid=tp-step][data-state=Unresolved]").Should().HaveCount(2).And.OnlyContain(step => !step.TextContent.Contains("observed: yes"), "expected-only connections are never shown as observed");
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

        page.WaitForAssertion(() => State(page.Find("[data-testid=snt-verify-item][data-view=message-verification]")).Should().Be("Available (read-only)"));
        State(page.Find("[data-journey=rapportmottak] [data-testid=snt-journey-status]")).Should().Be("Ready", "a runnable scenario is execution readiness, never a pass");

        _journeys.Pack = IntegrationJourneyPanelTests.SamplePack();
        var notRunnable = Render<SkoleNaervaerTesting>();
        notRunnable.WaitForAssertion(() => State(notRunnable.Find("[data-testid=snt-verify-item]")).Should().Be("Not available"));
    }

    [Fact]
    public void Source_evidence_is_the_canonical_IQR_binding_never_a_selection_of_its_own()
    {
        var binding = new IntegrationEvidenceBinding { EnvironmentId = "dev", IntegrationId = "dev:altinn:report-intake", SnapshotId = Guid.NewGuid(), ArchiveName = "skole.zip", Fingerprint = new string('a', 64) };
        WithTarget(binding);
        var pack = IntegrationJourneyPanelTests.SamplePack();
        _journeys.Pack = pack with
        {
            Journeys = [.. pack.Journeys.Select(view => view.Journey.JourneyId == "rapportmottak"
                ? view with { MatchedIntegrations = [new("report-intake", "dev:altinn:report-intake", "Report intake", IntegrationKind.HttpApi, true, "Configured.")] } : view)],
        };
        var page = Open();
        page.WaitForAssertion(() => page.Find("[data-testid=snt-source]").TextContent.Should().Contain("skole.zip").And.Contain("bound in Integration Quality Review"));
        page.FindAll("select").Should().BeEmpty("no snapshot is selected on the pack page");
    }

    private static TestPackSourceGuidance AltinnGuidance() => new("skolenaervaer",
    [
        new(new BirkNext.SourceDomains.SourceConfigurationRequirement("altinn-app-route", "Altinn app (org/app)", "Application.Route", "Altinn Skolenærvær app", "OpenAPI / Swagger document", "Which app.", "Integration Quality Review → Extensions → Altinn")
            { PrerequisiteKey = "altinn-app", ConsumerField = "App identifier" }, BirkNext.SourceDomains.SourceSuggestionState.Suggested)
        {
            Value = "buf/skolenaervaer", BoundSources = ["skole.zip"],
            Candidates = [new("buf/skolenaervaer", [new BirkNext.SourceDomains.SourceConfigurationValue { SemanticKey = "Application.Route", Value = "buf/skolenaervaer", Artifact = "skolenaervaer.json", Location = "paths" }])],
        },
        new(new BirkNext.SourceDomains.SourceConfigurationRequirement("altinn-test-server", "Altinn test server", "Environment.Server.Test", "Altinn Skolenærvær app", "OpenAPI / Swagger document", "TT02 host.", "Backend Altinn test endpoints")
            { PrerequisiteKey = "altinn-environment" }, BirkNext.SourceDomains.SourceSuggestionState.AvailableNotBound) { UnboundSources = ["newer.zip"] },
    ]);

    [Fact]
    public void The_overview_summarizes_source_requirements_by_state_and_never_claims_runtime()
    {
        WithTarget();
        _journeys.Guidance = AltinnGuidance();
        var page = Open();
        page.WaitForAssertion(() => page.Find("[data-testid=snt-source-requirements-summary]").TextContent
            .Should().Be("1 of 2 source requirements cannot yet be assessed from bound sources · 1 suggested · 0 configured. Runtime verification is separate."));
        page.Find("[data-testid=snt-source-values]").Should().NotBeNull("the values stay collapsed");
    }

    [Fact]
    public void A_blocker_says_what_kind_of_input_it_needs_and_shows_its_source_guidance_collapsed()
    {
        WithTarget();
        _journeys.Guidance = new("skolenaervaer",
        [
            new(new BirkNext.SourceDomains.SourceConfigurationRequirement("altinn-auth-scheme", "Authentication scheme", "Authentication.Scheme", "Altinn Skolenærvær app", "OpenAPI / Swagger document",
                "How callers authenticate.", "Local execution config") { PrerequisiteKey = "altinn-auth" }, BirkNext.SourceDomains.SourceSuggestionState.Suggested)
            {
                Value = "Bearer JWT", BoundSources = ["skole.zip"],
                Candidates = [new("Bearer JWT", [new BirkNext.SourceDomains.SourceConfigurationValue { SemanticKey = "Authentication.Scheme", Value = "Bearer JWT", Artifact = "skolenaervaer.json", Location = "components.securitySchemes.AltinnToken" }])],
            },
        ]);
        var page = Open("rapportmottak");
        page.WaitForAssertion(() => page.FindAll("[data-testid=snt-attention-item]").Should().HaveCount(2));
        page.FindAll("[data-testid=snt-attention-item] [data-testid=snt-kind]").Select(kind => kind.TextContent).Should().Equal("Needs: Source-derived configuration", "Needs: Source evidence");
        var auth = page.Find("[data-testid=snt-attention-item][data-key=altinn-auth]");
        auth.QuerySelector("[data-testid=snt-blocker-guidance] .disclosure-toggle")!.GetAttribute("aria-expanded").Should().Be("false");
        auth.QuerySelector("[data-testid=snt-blocker-guidance] .disclosure-toggle")!.Click();
        page.Find("[data-testid=snt-attention-item][data-key=altinn-auth] [data-testid=sgl-value]").TextContent.Should().Be("Bearer JWT");
        page.Find("[data-testid=snt-attention-item][data-key=altinn-auth] [data-testid=sgl-runtime]").TextContent.Should().Be("No — declared in source only");
        page.FindAll("[data-testid=snt-attention-item][data-key='source:MM'] [data-testid=snt-blocker-guidance]").Should().BeEmpty();
    }

    [Fact]
    public void Rapportmottak_offers_to_apply_safe_bound_values_only_on_an_explicit_click_and_reports_what_was_stored()
    {
        WithTarget();
        _journeys.Guidance = new("skolenaervaer",
        [
            new(new BirkNext.SourceDomains.SourceConfigurationRequirement("altinn-app-route", "Altinn app (org/app)", "Application.Route", "App", "Configuration", "p", "IQR")
                { ConsumerField = "App identifier", AutoApply = true }, BirkNext.SourceDomains.SourceSuggestionState.Suggested) { Value = "buf/skolenaervaer", BoundSources = ["hub.zip"] },
            new(new BirkNext.SourceDomains.SourceConfigurationRequirement("altinn-event-webhook", "Report intake webhook", "Endpoint.EventWebhook", "MU", "Configuration", "p", "IQR")
                { ConsumerField = "Endpoint reference", AutoApply = true }, BirkNext.SourceDomains.SourceSuggestionState.Suggested) { Value = "https://hub.invalid/hook", ConfiguredValue = "https://explicit.invalid" },
            new(new BirkNext.SourceDomains.SourceConfigurationRequirement("altinn-test-server", "Altinn test server", "Environment.Server.Test", "App", "Configuration", "p", "Backend"),
                BirkNext.SourceDomains.SourceSuggestionState.Suggested) { Value = "https://x.invalid" },
        ]);
        _journeys.ApplyResult = new([new("altinn-app-route", "Altinn app (org/app)", "buf/skolenaervaer", "Configuration hub/appsettings.Development.json → Altinn:StorageAppId (line 14)")],
            [new("altinn-event-webhook", "Report intake webhook", "A value is already configured; it is kept.")]);
        var page = Open("rapportmottak");
        page.WaitForAssertion(() => page.Find("[data-testid=snt-apply-source]").TextContent.Should().Be("Apply safe source-derived configuration (1)",
            "only an auto-applicable, unconfigured suggestion counts"));
        _journeys.Calls.Should().NotContain("apply-source", "nothing is applied by opening the page");
        page.Find("[data-testid=snt-apply-source]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=snt-applied-item]").TextContent.Should().Contain("buf/skolenaervaer").And.Contain("Altinn:StorageAppId"));
        page.Find("[data-testid=snt-apply-result]").TextContent.Should().Contain("not runtime verified");
        page.Find("[data-testid=snt-not-applied-item]").TextContent.Should().Contain("already configured; it is kept");
    }

    private static TestPackSourceGuidance ThreeSources() => new("skolenaervaer", [])
    {
        Sources =
        [
            new(new TestPackSourceRole("mu", "MU (Meldingsutveksleren)", "hub") { Capabilities = ["Altinn webhook"], Journeys = ["rapportmottak"], ComponentTokens = ["Meldingsutveksler"] },
                TestPackSourceState.Bound) { BoundArchives = ["Tiltak 2 - Sikkert digitalt økosystem (1).zip"], NextAction = "Open IQR Evidence", NextActionTarget = "iqr-evidence" },
            new(new TestPackSourceRole("m2lb", "M2LB", "downstream") { Capabilities = ["MM", "Skoletjenesten"], Journeys = ["utdanningsdata", "rapportmottak", "periodeavslutning"],
                ComponentTokens = ["Skoletjenesten", "MM"] }, TestPackSourceState.AvailableNotBound)
                { AvailableArchives = ["M2LB (2).zip"], NextAction = "Configure the integration in Target Environment → Integrations, then bind it in IQR Evidence", NextActionTarget = "target-environment-integrations" },
            new(new TestPackSourceRole("altinn-app", "Altinn Skolenærvær application", "app") { RequiresApiDocument = true, ExpectedArtifact = "OpenAPI / Swagger document", Journeys = ["rapportmottak"] },
                TestPackSourceState.Missing) { NextAction = "Import it in Source Analysis", NextActionTarget = "source-analysis" },
        ],
    };

    [Fact]
    public void The_overview_shows_each_logical_source_with_its_state_snapshot_and_owner_action()
    {
        WithTarget();
        _journeys.Guidance = ThreeSources();
        var page = Open();
        page.WaitForAssertion(() => page.FindAll("[data-testid=snt-source-card]").Should().HaveCount(3));
        var cards = page.FindAll("[data-testid=snt-source-card]");
        cards.Select(card => card.QuerySelector("[data-testid=snt-source-state]")!.TextContent).Should().Equal("Bound", "Available, not bound", "Missing");
        cards[0].QuerySelector("[data-testid=snt-source-archive]")!.TextContent.Should().Be("Source: Tiltak 2 - Sikkert digitalt økosystem (1).zip");
        cards[0].QuerySelector("[data-testid=snt-source-action]").Should().BeNull("a bound source needs no action");
        cards[1].QuerySelector("[data-testid=snt-source-archive]")!.TextContent.Should().Be("Available: M2LB (2).zip");
        cards[1].QuerySelector("[data-testid=snt-source-action]")!.GetAttribute("href").Should().Be("/admin/system-settings?section=target-environments&tab=integrations");
        cards[2].QuerySelector("[data-testid=snt-source-archive]")!.TextContent.Should().Be("Expected: OpenAPI / Swagger document");
        cards[2].QuerySelector("[data-testid=snt-source-action]")!.GetAttribute("href").Should().Be("/source-analysis");
        page.Find("[data-testid=snt-journey][data-journey=rapportmottak] [data-testid=snt-journey-sources]").TextContent
            .Should().Be("MU (Meldingsutveksleren) bound · M2LB available, not bound · Altinn Skolenærvær application missing");
        page.Find("[data-testid=snt-journey][data-journey=utdanningsdata] [data-testid=snt-journey-sources]").TextContent.Should().Be("M2LB available, not bound");
        page.Find("[data-testid=snt-workspace]").TextContent.Should().Contain("source evidence comes from Integration Quality Review bindings");
        page.Markup.Should().NotContain("skolenaervaer.json", "the BirkNext OpenAPI fixture is never a manual source");
    }

    [Fact]
    public void The_navigation_is_one_compact_tab_bar_without_list_markup()
    {
        WithTarget();
        var page = Open();
        page.WaitForAssertion(() => page.FindAll("[data-testid=snt-nav] a.snt-tab").Should().HaveCount(7));
        page.FindAll("[data-testid=snt-nav] ul, [data-testid=snt-nav] li").Should().BeEmpty("no bullets, even before styles load");
    }

    [Fact]
    public void A_rule_names_the_source_it_needs_and_what_is_available_for_it()
    {
        WithTarget();
        _journeys.Rules = new ArchitectureRuleReport("skolenaervaer", null, null,
            [new("mm-no-person", "MM performs no Person lookup", "MM", "Person", ArchitectureRuleExpectation.MustNotDependOn, ArchitectureRuleOutcome.NotAssessed,
                "Not assessed — this rule requires M2LB source evidence. The currently bound source is MU (Meldingsutveksleren). M2LB (2).zip is available but not bound.", [], "")],
            ["Each rule is assessed against the bound source of the logical source it needs."])
        {
            RuleSources = [new("mm-no-person", "m2lb", "M2LB", null, "M2LB (2).zip", TestPackSourceState.AvailableNotBound)],
        };
        var page = Open("architecture-rules");
        page.WaitForAssertion(() => page.Find("[data-testid=snt-rule-source]").TextContent.Should().Be("Needs M2LB · available: M2LB (2).zip (not bound)"));
        page.Find("[data-testid=snt-rule-reason]").TextContent.Should().StartWith("Not assessed — this rule requires M2LB source evidence").And.NotContain("not found");
        page.Find("[data-testid=snt-rules-provenance]").TextContent.Should().Contain("per rule");
    }

    [Fact]
    public void Without_a_binding_the_overview_says_so()
    {
        WithTarget();
        var page = Open();
        page.WaitForAssertion(() => page.Find("[data-testid=snt-source]").TextContent.Should().Contain("No source evidence bound to this pack's integrations"));
        page.FindAll("[data-testid=snt-journeys-not-ready]").Should().BeEmpty();
    }

    // ── Rapportmottak ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rapportmottak_is_action_first_then_categorized_blockers_then_the_run_and_collapsed_details()
    {
        WithTarget();
        var page = Open("rapportmottak");

        page.WaitForAssertion(() => page.Find("[data-testid=snt-status]").TextContent.Should().Contain("Execution readiness").And.Contain("Blocked"));
        var order = new[] { "snt-status", "snt-rapport-actions", "snt-needs-attention", "snt-rapport-run", "snt-rapport-details" }
            .Select(id => page.Markup.IndexOf($"data-testid=\"{id}\"", StringComparison.Ordinal)).ToList();
        order.Should().OnlyContain(position => position >= 0).And.BeInAscendingOrder("status and actions first, then blockers, then the run, then technical details");
        page.FindAll("[data-testid=snt-rapport-actions] button, [data-testid=snt-rapport-actions] a").Select(e => e.TextContent)
            .Should().Equal("Open Rapportmottak run", "Configure Altinn", "Open Target Environment");
        page.Find("[data-testid=snt-configure-altinn]").GetAttribute("href").Should().Be("/integration-quality-review?view=extensions");

        // Blockers grouped by their category metadata, each once, each with its source.
        page.FindAll("[data-testid=snt-blocker-group]").Select(group => group.GetAttribute("data-group")).Should().Equal("Authentication", "Source evidence");
        page.FindAll("[data-testid=snt-attention-item]").Select(li => li.GetAttribute("data-key")).Should().Equal("altinn-auth", "source:MM");
        page.FindAll("[data-testid=snt-attention-item] [data-testid=snt-source-badge]").Should().OnlyContain(badge => badge.TextContent == "Source: BirkNext");
        page.Find("[data-testid=snt-resolved-none]").TextContent.Should().Be("Resolved automatically: 0", "zero resolved stays one compact line");
        page.FindAll("[data-testid=snt-resolved-details]").Should().BeEmpty();

        var expected = IntegrationJourneyPanelTests.SamplePack().Journeys.Single(view => view.Journey.JourneyId == "rapportmottak").Prerequisites;
        page.FindAll("[data-testid=snt-prerequisite]").Select(li => $"{li.GetAttribute("data-key")}={li.GetAttribute("data-state")}")
            .Should().BeEquivalentTo(expected.Select(p => $"{p.Key}={p.State}"), "every prerequisite appears once in the technical details, with the backend's state");
        page.FindAll("[data-testid=snt-rapport-details] .disclosure-toggle").Should().OnlyContain(t => t.GetAttribute("aria-expanded") == "false", "technical details are collapsed");

        page.Find("[data-testid=snt-rapport-run-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=snt-open-run]").Click();
        page.Find("[data-testid=snt-rapport-run-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        page.FindAll("#snt-rapport [data-testid=ijp-panel][data-embedded=true]").Should().ContainSingle();
        page.FindAll("#snt-rapport [data-testid=ijp-prerequisites]").Should().BeEmpty("the page presents the prerequisites once, grouped");
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
    public void Rapportmottak_shows_what_was_resolved_and_from_where_and_only_the_unresolved_input()
    {
        WithTarget();
        WithRapportmottakRows("client-secret");
        var page = Open("rapportmottak");

        page.WaitForAssertion(() => page.Find("[data-testid=snt-tt02-decision]").GetAttribute("data-code").Should().Be("B"));
        page.Find("[data-testid=snt-tt02-decision]").TextContent.Should().Contain("ID-porten configuration incomplete").And.Contain("TT02 submission readiness")
            .And.NotContain("B.", "the internal root-cause order is not a formal classification, so no letter is shown");
        page.Find("[data-testid=snt-resolved-details-toggle]").TextContent.Should().Contain("Resolved automatically: 17");
        page.Find("[data-testid=snt-resolved-details-toggle]").Click();
        page.Find("[data-testid=snt-resolved-item][data-key=idporten-client] .snt-source").TextContent.Should().Be("Integration Quality Review");
        page.Find("[data-testid=snt-resolved-item][data-key=redirect-uri] .snt-source").TextContent.Should().Be("Local execution config");
        page.Find("[data-testid=snt-resolved-item][data-key=synthetic-data] .snt-source").TextContent.Should().Be("Test Data");
        page.FindAll("[data-testid=snt-attention-item]").Select(li => li.GetAttribute("data-key")).Should().Equal("client-secret");
        page.Find("[data-testid=snt-attention-item]").GetAttribute("data-category").Should().Be("Authentication");
        page.Find("[data-testid=snt-blocker-group]").GetAttribute("data-group").Should().Be("Authentication");

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
        page.Find("[data-testid=snt-attention-item] a").GetAttribute("href").Should().Be("/integration-quality-review?view=extensions");
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
        page.Find("[data-testid=snt-nothing-unresolved]").TextContent.Should().Contain("Ready to run is not passed");
        _journeys.Started.Should().BeEmpty("ready never starts a submission");
    }

    // ── Utdanningsdata / Periodeavslutning ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Utdanningsdata_hands_off_to_the_shared_Active_Event_tests_without_embedding_them()
    {
        WithTarget();
        var page = Open("utdanningsdata");

        page.Find("#snt-education [data-testid=snt-flow]").TextContent.Should().Contain("BiRK → Debezium → Event Hub → SkoleAdapter → Utdanning");
        page.WaitForAssertion(() => page.Find("[data-testid=snt-provider]").TextContent.Should().Contain("Shared Active Event Tests").And.Contain("skolenaervaer.cdc"));
        page.Find("[data-testid=snt-handoff-readiness]").TextContent.Should().Be("Blocked — 1 prerequisite");
        page.Find("[data-testid=snt-open-active-tests]").GetAttribute("href").Should().Be("/integration-quality-review?view=active-tests");
        page.FindAll("[data-testid=act-panel], [data-testid=ijp-panel]").Should().BeEmpty("the Active Event tests are linked, never embedded");
        page.Find("#snt-education").TextContent.Should().NotContain("Altinn");
    }

    [Fact]
    public void Periodeavslutning_keeps_its_semantics_and_shows_steps_with_separate_state_columns()
    {
        WithTarget();
        var page = Open("periodeavslutning");

        page.Find("[data-testid=snt-period-operator]").TextContent.Should().StartWith("Period closure is an operator action")
            .And.Contain("only postpones the lock").And.Contain("BirkNext never locks a period");
        page.Find("[data-testid=snt-period-semantics]").TextContent.Should().Be("Service Bus accepted is not the same as Bufdata processed.");
        page.WaitForAssertion(() => page.FindAll("#snt-period [data-testid=ijp-steps] th[role=columnheader]").Select(th => th.TextContent)
            .Should().Equal("Step", "Configured", "Observed", "Verified", "Assessment"));
        var step = page.Find("#snt-period [data-testid=ijp-step]");
        step.QuerySelector("[data-testid=ijp-step-configured]")!.TextContent.Should().Be("Not applicable");
        step.QuerySelector("[data-testid=ijp-step-observed]")!.TextContent.Should().Be("No observer");
        step.QuerySelector("[data-testid=ijp-step-verified]")!.TextContent.Should().Be("No verifier");
        step.QuerySelector("[data-testid=ijp-step-assessment]")!.TextContent.Should().Be(IntegrationJourneyPresentation.Step(JourneyStepState.NotAssessed), "a missing observer is Not assessed, never a failure");
        page.Find("[data-testid=snt-blocker-group]").GetAttribute("data-group").Should().Be("Integration");
    }

    // ── Verify ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MessageVerification_HasItsOwnView_WithAProperEmptyStateWithoutATarget()
    {
        var without = Open("message-verification");
        without.Find("#snt-verification-heading").TextContent.Should().Be("Message Verification");
        without.Find("[data-testid=snt-verification-not-ready]").TextContent.Should().Contain("Nothing has been read");
        without.Find("[data-testid=snt-verification-not-ready] a").GetAttribute("href").Should().Be("/admin/system-settings?section=target-environments");
        without.FindAll("[data-testid=mvp]").Should().BeEmpty("nothing is loaded without a Target Environment");
    }

    [Fact]
    public void MessageVerificationView_RendersTheFeature_WithATarget()
    {
        WithTarget();
        var page = Open("message-verification");
        page.WaitForAssertion(() => page.FindAll("#snt-verification [data-testid=mvp]").Should().ContainSingle());
        page.Find("#snt-verification").TextContent.Should().Contain("Read-only: nothing is sent");
    }

    [Fact]
    public void ArchitectureRules_show_rule_evidence_source_evidence_assessment_and_reason_separately()
    {
        WithTarget();
        var page = Open("architecture-rules");

        page.WaitForAssertion(() => page.FindAll("[data-testid=snt-rule]").Should().HaveCount(2));
        page.FindAll("[data-testid=snt-rule-results] th[role=columnheader]").Select(th => th.TextContent).Should().Equal("Rule", "Required source", "Evidence", "Assessment", "Reason");
        page.FindAll("[data-testid=snt-rule]").Select(r => string.Join("|", r.QuerySelectorAll("td").Select(td => td.TextContent))).Should().Equal(
            "source.zip|Inconclusive — component not found in source|Not assessed|Not source verified.",
            "source.zip|1 dependency reference in source|Potential deviation|Potential deviation.");
        page.Find("[data-testid=snt-rules-provenance]").TextContent.Should().Be("Evidence source: Source Analysis snapshot source.zip");
        page.Find("[data-testid=snt-rules-limitations]").TextContent.Should().Contain("source structure only");
        page.Find("[data-testid=snt-rule-results]").ParentElement!.GetAttribute("role").Should().Be("region");
        page.FindAll("[data-testid=snt-person-lookup]").Should().BeEmpty("rules come from the pack's metadata, not a static list");
    }

    [Fact]
    public void ArchitectureRules_without_a_binding_are_not_assessed_and_name_the_candidate_only_as_non_authoritative()
    {
        WithTarget();
        _journeys.Rules = new ArchitectureRuleReport("skolenaervaer", null, null,
        [
            new("mm-no-person", "MM performs no Person lookup", "MM", "Person", ArchitectureRuleExpectation.MustNotDependOn, ArchitectureRuleOutcome.NotAssessed,
                "Not assessed: source evidence not bound. Bind a Source Analysis snapshot to one of this pack's integrations in Integration Quality Review → Evidence.", [], ""),
        ],
        [
            "Architecture rules are assessed only against the Source Analysis snapshot bound in Integration Quality Review → Evidence.",
            "Not authoritative: newest.zip (SHA-256 abcdef012345) is a stored snapshot with architecture evidence, but it is not bound, so it was not assessed.",
        ]);
        var page = Open("architecture-rules");

        page.WaitForAssertion(() => page.FindAll("[data-testid=snt-rule]").Should().ContainSingle());
        page.Find("[data-testid=snt-rule]").QuerySelectorAll("td").Select(td => td.TextContent).Should().StartWith(["Not bound"]).And.Contain("Not assessed");
        page.Find("[data-testid=snt-rules-provenance]").TextContent.Should().Contain("no Source Analysis snapshot is bound").And.NotContain("newest.zip");
        page.Find("[data-testid=snt-rules-limitations]").TextContent.Should().Contain("Not authoritative: newest.zip");
        page.Markup.Should().NotContain("Conforms").And.NotContain("Potential deviation");
    }

    // ── History ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void History_is_filters_runs_and_export_only()
    {
        WithTarget();
        var page = Open("history");

        page.WaitForAssertion(() => page.FindAll("#snt-history [data-testid=ijp-history-filters]").Should().ContainSingle());
        page.Find("#snt-history [data-testid=ijp-history-empty]").TextContent.Should().Contain("No journey runs");
        page.FindAll("#snt-history [data-testid=ijp-export]").Should().ContainSingle();
        page.FindAll("#snt-history [data-testid=ijp-tabs], #snt-history [data-testid=ijp-overview], #snt-history [data-testid=ijp-journey]").Should().BeEmpty("History embeds no journey navigation or views");
        _journeys.Calls.Should().Contain("history");
    }

    [Fact]
    public void ReturningFromIdPortenTest_OpensTheRapportmottakRun()
    {
        WithTarget();
        Services.GetRequiredService<NavigationManager>().NavigateTo("extensions/skolenaervaer?altinn-sign-in=done");
        var page = Render<SkoleNaervaerTesting>();

        page.FindAll(".snt-panel").Should().ContainSingle().Which.Id.Should().Be("snt-rapport");
        page.WaitForAssertion(() => page.Find("[data-testid=snt-rapport-run-toggle]").GetAttribute("aria-expanded").Should().Be("true"));
    }

    private sealed class FakeJourneys : IIntegrationJourneysApiService
    {
        public IntegrationJourneyPackView Pack { get; set; } = IntegrationJourneyPanelTests.SamplePack();
        public TestPackDescriptor Descriptor { get; set; } = SkolenaervaerDescriptor();
        public ArchitectureRuleReport? Rules { get; set; }
        public TestPackSourceGuidance? Guidance { get; set; }
        public BirkNext.SourceDomains.SourceConfigurationApplyResult? ApplyResult { get; set; }

        public Task<ActiveEventApiResult<BirkNext.SourceDomains.SourceConfigurationApplyResult>> ApplySourceGuidanceAsync(string environmentId, string packId, CancellationToken ct = default)
        {
            Calls.Add("apply-source");
            return Task.FromResult(new ActiveEventApiResult<BirkNext.SourceDomains.SourceConfigurationApplyResult>(ApplyResult, ApplyResult is null ? "refused" : null, HttpStatusCode.OK));
        }

        public Task<ActiveEventApiResult<TestPackSourceGuidance>> SourceGuidanceAsync(string environmentId, string packId, CancellationToken ct = default)
        {
            Calls.Add("source-guidance");
            return Task.FromResult(new ActiveEventApiResult<TestPackSourceGuidance>(Guidance, Guidance is null ? "none" : null, HttpStatusCode.OK));
        }
        public List<string> Calls { get; } = [];
        public List<IntegrationJourneyRunRequest> Started { get; } = [];

        /// <summary>The descriptor the backend's generic describer produces for the Skolenærvær pack (see TestPackArchitectureTests).</summary>
        public static TestPackDescriptor SkolenaervaerDescriptor() => new()
        {
            PackId = "skolenaervaer", PackVersion = "1", DisplayName = "Skolenærvær Testing",
            Journeys =
            [
                new("utdanningsdata", "Utdanningsdata", "BiRK → Debezium → Event Hub → SkoleAdapter → Utdanning", JourneyExecutionMode.DelegatedToActiveEvent, "skolenaervaer.cdc"),
                new("rapportmottak", "Rapportmottak", "Altinn → MU → MM → Skolenærværsrapport", JourneyExecutionMode.JourneyRunner, null),
                new("periodeavslutning", "Periodeavslutning", "Skolenærværsrapport → Service Bus → Bufdata", JourneyExecutionMode.JourneyRunner, null),
            ],
            VerificationViews = [new("message-verification", "Message Verification", TestPackViewKind.Verification, "rapportmottak", "message-verification", "Read-only verification of what happened to one message.")],
            RuleIds = ["mm-no-person", "rapport-person"], SupportsHistory = true,
            Navigation =
            [
                new("Overview", [new("overview", "Overview", TestPackViewKind.Overview)]),
                new("Journeys", [new("utdanningsdata", "Utdanningsdata", TestPackViewKind.Journey, "utdanningsdata"), new("rapportmottak", "Rapportmottak", TestPackViewKind.Journey, "rapportmottak"),
                    new("periodeavslutning", "Periodeavslutning", TestPackViewKind.Journey, "periodeavslutning")]),
                new("Verify", [new("message-verification", "Message Verification", TestPackViewKind.Verification, "rapportmottak", "message-verification"), new("architecture-rules", "Architecture Rules", TestPackViewKind.Rules)]),
                new("History", [new("history", "History", TestPackViewKind.History)]),
            ],
        };

        public Task<ActiveEventApiResult<IReadOnlyList<TestPackDescriptor>>> TestPacksAsync(CancellationToken ct = default)
        {
            Calls.Add("test-packs");
            return Task.FromResult(new ActiveEventApiResult<IReadOnlyList<TestPackDescriptor>>([Descriptor], null, HttpStatusCode.OK));
        }

        public Task<ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>> PacksAsync(string environmentId, CancellationToken ct = default)
        {
            Calls.Add("packs");
            return Task.FromResult(new ActiveEventApiResult<IReadOnlyList<IntegrationJourneyPackView>>([Pack], null, HttpStatusCode.OK));
        }

        public Task<ActiveEventApiResult<ArchitectureRuleReport>> RulesAsync(string environmentId, string packId, CancellationToken ct = default)
        {
            Calls.Add("rules");
            if (Rules is { } rules) return Task.FromResult(new ActiveEventApiResult<ArchitectureRuleReport>(rules, null, HttpStatusCode.OK));
            return Task.FromResult(new ActiveEventApiResult<ArchitectureRuleReport>(new ArchitectureRuleReport(packId, Guid.NewGuid(), "source.zip",
            [
                new("rapport-person", "Skolenærværsrapport owns the Person lookup", "Skolenærværsrapport", "Person", ArchitectureRuleExpectation.MustDependOn, ArchitectureRuleOutcome.NotAssessed,
                    "Not source verified.", [], "Source Analysis snapshot source.zip"),
                new("mm-no-person", "MM performs no Person lookup", "MM", "Person", ArchitectureRuleExpectation.MustNotDependOn, ArchitectureRuleOutcome.PotentialDeviation,
                    "Potential deviation.", [new("MM/Program.cs", 42, "AddHttpClient<IPersonClient>", "Http dependency")], "Source Analysis snapshot source.zip"),
            ], ["Rules are evaluated on source structure only. They do not prove which calls happen at runtime."]), null, HttpStatusCode.OK));
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
