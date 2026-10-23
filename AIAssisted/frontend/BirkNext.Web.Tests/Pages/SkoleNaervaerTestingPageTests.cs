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
        page.FindAll(".snt-nav a").Select(a => a.TextContent).Should().Equal(["Overview", "Journeys", "Security", "Verify", "History"], "five top-level areas, never eight equal tabs");
        // Path-qualified links: with <base href="/"> a bare "?tab" or "#id" would leave the page. An area opens its first view.
        page.FindAll(".snt-nav a").Select(a => a.GetAttribute("href")).Should().Equal("extensions/skolenaervaer", "extensions/skolenaervaer?tab=utdanningsdata",
            "extensions/skolenaervaer?tab=security", "extensions/skolenaervaer?tab=message-verification", "extensions/skolenaervaer?tab=history");
        page.FindAll(".snt-nav a[aria-current=page]").Select(a => a.TextContent).Should().Equal("Overview");
        page.FindAll("[data-testid=snt-subnav]").Should().BeEmpty("the overview has no secondary navigation");
        page.FindAll(".snt-panel").Should().ContainSingle().Which.Id.Should().Be("snt-overview");
        _journeys.Calls.Should().Contain("test-packs", "the navigation comes from the pack's own descriptor");
    }

    [Theory]
    [InlineData("utdanningsdata", "Journeys")]
    [InlineData("rapportmottak", "Journeys")]
    [InlineData("periodeavslutning", "Journeys")]
    [InlineData("message-verification", "Verify")]
    [InlineData("architecture-rules", "Verify")]
    [InlineData("history", "History")]
    public void Old_deep_links_open_their_view_under_its_area(string tab, string area)
    {
        var page = Open(tab);
        page.Find(".snt-nav a[aria-current=page]").TextContent.Should().Be(area);
        if (area == "History") { page.FindAll("[data-testid=snt-subnav]").Should().BeEmpty(); return; }
        page.Find($"[data-testid=snt-subtab][data-tab={tab}]").GetAttribute("aria-current").Should().Be("page");
    }

    [Fact]
    public void Journeys_and_Verify_have_secondary_navigation_and_verification_kinds_are_labelled_apart()
    {
        var journeys = Open("rapportmottak");
        journeys.FindAll("[data-testid=snt-subtab]").Select(a => a.TextContent).Should().Equal("Utdanningsdata", "Rapportmottak", "Periodeavslutning");
        journeys.Find("[data-testid=snt-subnav]").GetAttribute("aria-label").Should().Be("Journeys views");

        var verify = Open("message-verification");
        verify.FindAll("[data-testid=snt-subnav-section]").Select(s => s.GetAttribute("aria-label")).Should().Equal("Runtime verification", "Source-based checks");
        verify.FindAll("[data-testid=snt-subtab]").Select(a => a.TextContent).Should().Equal("Message Verification", "Architecture Rules");
    }

    [Fact]
    public void The_navigation_follows_the_descriptor_not_hard_coded_views()
    {
        _journeys.Descriptor = FakeJourneys.SkolenaervaerDescriptor() with
        {
            Navigation = [.. FakeJourneys.SkolenaervaerDescriptor().Navigation.Where(group => group.Label != "Verify")],
        };
        var page = Open();
        page.FindAll(".snt-nav a").Select(a => a.TextContent).Should().NotContain("Verify");
    }

    [Fact]
    public void ASectionLink_ShowsThatSection_AndMovesFocusToItsHeading()
    {
        var page = Open("utdanningsdata");

        page.FindAll("[data-testid=snt-subtab]").Single(a => a.TextContent == "Rapportmottak").Click();

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
        page.FindAll("nav").Select(n => n.GetAttribute("data-testid")).Should().BeSubsetOf(["snt-nav", "snt-subnav"], "only the page's own area and view navigation");
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
        Cell("utdanningsdata", "Integration").Should().Be("Not applicable", "the sample journey declares no integration requirement");
        Cell("utdanningsdata", "Execution").Should().Be("Blocked");
        Cell("utdanningsdata", "Test Data").Should().Be("Not assessed", "no readiness dimension was reported — never guessed");
        page.Find("[data-journey=rapportmottak] [data-testid=snt-journey-blockers]").TextContent.Should().Be("2 actions", "grouped tester actions, not raw checks");
        page.Find("[data-journey=utdanningsdata] th").TextContent.Should().Contain("BiRK → Debezium → Event Hub → SkoleAdapter → Utdanning");
        page.FindAll("[data-testid=ijp-panel], [data-testid=atp-panel], [data-testid=mvp], [data-testid=act-panel]").Should().BeEmpty("no feature is rendered inline on the overview");
        page.FindAll("[data-testid=snt-journey-open]").Select(a => a.GetAttribute("href")).Should().Equal("extensions/skolenaervaer?tab=utdanningsdata",
            "extensions/skolenaervaer?tab=rapportmottak", "extensions/skolenaervaer?tab=periodeavslutning");
        page.FindAll("[data-testid=snt-journeys] th[role=columnheader]").Select(th => th.TextContent).Should().Equal("Journey", "Integration", "Contract", "Test Data", "Execution", "Verification", "Attention", "Action");
    }

    [Fact]
    public void Overview_matrix_reads_the_shared_readiness_dimensions_and_counts_attention_by_owner()
    {
        WithTarget();
        var pack = IntegrationJourneyPanelTests.SamplePack();
        JourneyReadinessDimension Dim(JourneyDimension d, string label, JourneyDimensionState state, string owner, string key, bool optional = false) => new(d, label, state, $"{label} evidence", owner, key, optional);
        _journeys.Pack = pack with
        {
            Journeys = [.. pack.Journeys.Select(j => j.Journey.JourneyId != "rapportmottak" ? j : j with
            {
                Assessment = JourneyAssessmentState.ExecutedNotVerified,
                Dimensions =
                [
                    Dim(JourneyDimension.Integration, "Integration", JourneyDimensionState.Partial, "Target Environment → Integrations", "integrations"),
                    Dim(JourneyDimension.Contracts, "Contracts", JourneyDimensionState.ReviewRequired, "Integration Quality Review / Contract & Schema Evidence", "source-contracts"),
                    Dim(JourneyDimension.TestData, "Test Data", JourneyDimensionState.Missing, "Test Data", "test-data"),
                    Dim(JourneyDimension.Execution, "Execution", JourneyDimensionState.Blocked, "Target Environment / execution safety", "execution-settings"),
                    Dim(JourneyDimension.Verification, "Verification", JourneyDimensionState.Partial, "Runtime evidence / Verify", "verify"),
                    Dim(JourneyDimension.Delivery, "Delivery", JourneyDimensionState.Partial, "Pipeline Review", "pipeline-review", optional: true),
                ],
            })],
        };
        var page = Open();

        page.WaitForAssertion(() => page.FindAll("[data-testid=snt-journey]").Should().HaveCount(3));
        string Cell(string label) => State(page.Find($"[data-journey=rapportmottak] td[data-label='{label}']"));
        (Cell("Integration"), Cell("Contract"), Cell("Test Data"), Cell("Verification")).Should().Be(("Partial", "Review required", "Missing", "Partial"));
        page.Find("[data-journey=rapportmottak] [data-testid=snt-matrix-assessment]").TextContent.Should().Be("Last run: Executed · not verified", "executed is not verified");
        page.Find("[data-testid=snt-attention-summary]").TextContent.Should().Be("5 things need attention: 1 integration not enabled for active runs, 1 contracts, 1 test data, 1 execution, 1 verification",
            "an optional dimension (delivery) never needs attention");
        page.FindAll("[data-testid=snt-attention-category] a").Select(a => a.GetAttribute("href")).Should().Contain("/test-data")
            .And.Contain("/source-analysis?area=quality-evidence&view=contracts").And.Contain("/admin/system-settings?section=target-environments");
    }

    [Fact]
    public void A_journey_shows_each_dimension_with_its_owner_and_never_edits_shared_configuration()
    {
        WithTarget();
        var pack = IntegrationJourneyPanelTests.SamplePack();
        _journeys.Pack = pack with
        {
            Journeys = [.. pack.Journeys.Select(j => j.Journey.JourneyId != "rapportmottak" ? j : j with
            {
                Dimensions = [new(JourneyDimension.TestData, "Test Data", JourneyDimensionState.Missing, "Approved Test Data is missing for a required role — a journey prerequisite, not a product defect.", "Test Data", "test-data")
                    { Items = ["Reporter: missing"], Provenance = "Test Data catalog (shared)" }],
            })],
        };
        var page = Open("rapportmottak");

        page.WaitForAssertion(() => page.FindAll("[data-testid=journey-dimension]").Should().ContainSingle());
        var row = page.Find("[data-testid=journey-dimension]");
        row.QuerySelector("[data-testid=journey-dimension-state]")!.TextContent.Should().Be("Missing");
        row.QuerySelector("[data-testid=journey-dimension-owner]")!.TextContent.Should().Be("Test Data");
        row.QuerySelector("[data-testid=journey-dimension-action]")!.GetAttribute("href").Should().Be("/test-data");
        row.QuerySelectorAll("input, select, textarea").Should().BeEmpty("the test pack links to the owner instead of editing shared Test Data");
    }

    [Fact]
    public void The_security_area_projects_the_shared_classification_evidence_without_its_own_source_or_context()
    {
        WithTarget();
        var classification = new Mock<IClassificationReviewApiService>();
        classification.Setup(a => a.ConsumersAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new SecurityClassificationConsumerAssessment("skolenaervaer", "Skolenærvær", "Gradert tilgang across the Skolenærvær journeys", null, null,
                [new("bufdata-exclusion", "Bufdata exclusion rule", SecurityRequirementState.NotTested, "The Bufdata dataset must not contain fødselsnummer or DUF.", "No runtime evidence for this domain rule yet.", "Skolenærvær Testing") { DomainSpecific = true }],
                "1 of 1 requirement(s) not verified yet."),
        ]);
        Services.AddSingleton(classification.Object);
        var page = Open("security");

        page.WaitForAssertion(() => page.FindAll("[data-testid=sc-consumer]").Should().ContainSingle().Which.GetAttribute("data-consumer").Should().Be("skolenaervaer"));
        page.Find("#snt-security-heading").TextContent.Should().Be("Security / Gradert tilgang");
        page.Find("[data-testid=sc-open-full-review]").GetAttribute("href").Should().Be("/security-classification-review");
        page.Find("[data-testid=snt-security]").QuerySelectorAll("input, select, textarea").Should().BeEmpty("no duplicate source selection or test context");
        classification.Verify(a => a.SaveContextAsync(It.IsAny<string>(), It.IsAny<ClassificationTestContext>(), It.IsAny<CancellationToken>()), Times.Never);
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
            .Should().Be("1 of 2 configuration values known (0 stored · 1 from canonical source evidence); 1 unresolved. Runtime verification is separate."));
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
        page.WaitForAssertion(() => page.Find("[data-testid=snt-review-apply]").TextContent.Should().Be("Review & apply 1 value",
            "only an auto-applicable, unconfigured suggestion counts"));
        _journeys.Calls.Should().NotContain("apply-source", "nothing is applied by opening the page");
        page.Find("[data-testid=snt-review-apply]").Click();
        page.Find("[data-testid=snt-apply-preview]").TextContent.Should().Contain("buf/skolenaervaer", "exactly what will be persisted is shown first");
        _journeys.Calls.Should().NotContain("apply-source", "reviewing is not applying");
        page.Find("[data-testid=snt-apply-source]").TextContent.Should().Be("Apply 1 value");
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
        cards.Select(card => card.QuerySelector("[data-testid=snt-source-state]")!.TextContent).Should().Equal("Bound", "Available · needs binding", "Missing");
        cards[0].QuerySelector("[data-testid=snt-source-archive]")!.TextContent.Should().Be("Source: Tiltak 2 - Sikkert digitalt økosystem (1).zip");
        cards[0].QuerySelector("[data-testid=snt-source-action]").Should().BeNull("a bound source needs no action");
        cards[1].QuerySelector("[data-testid=snt-source-archive]")!.TextContent.Should().Be("Available: M2LB (2).zip");
        cards[1].QuerySelector("[data-testid=snt-source-action]")!.GetAttribute("href").Should().Be("/admin/system-settings?section=target-environments&tab=integrations");
        cards[2].QuerySelector("[data-testid=snt-source-archive]")!.TextContent.Should().Be("Expected: OpenAPI / Swagger document");
        cards[2].QuerySelector("[data-testid=snt-source-action]")!.GetAttribute("href").Should().Be("/source-analysis");
        page.Find("[data-testid=snt-journey][data-journey=rapportmottak] [data-testid=snt-journey-sources]").TextContent
            .Should().Be("Source: MU (Meldingsutveksleren) bound · M2LB available · needs binding · Altinn Skolenærvær application missing");
        page.Find("[data-testid=snt-journey][data-journey=utdanningsdata] [data-testid=snt-journey-sources]").TextContent.Should().Be("Source: M2LB available · needs binding");
        page.FindAll("[data-testid=snt-workspace]").Should().BeEmpty("no project workspace is needed for this pack, so none is shown as a warning");
        page.Find("[data-testid=snt-source]").TextContent.Should().StartWith("Provided by Integration Quality Review bindings");
        page.FindAll("[data-testid=snt-source-summary]").Select(s => s.QuerySelector("[data-testid=snt-source-summary-state]")!.TextContent).Should().Equal("Bound", "Available · needs binding", "Missing");
        page.Find("[data-testid=snt-source-setup-details-body]").HasAttribute("hidden").Should().BeTrue("source cards are details, not the default view");
        page.Markup.Should().NotContain("skolenaervaer.json", "the BirkNext OpenAPI fixture is never a manual source");
    }

    [Fact]
    public void The_navigation_is_one_compact_tab_bar_without_list_markup()
    {
        WithTarget();
        var page = Open();
        page.WaitForAssertion(() => page.FindAll("[data-testid=snt-nav] a.snt-tab").Should().HaveCount(5));
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
            .Should().Equal("Prepare Rapportmottak run", "Open Altinn run configuration");
        page.Find("[data-testid=snt-open-run]").ClassList.Should().Contain("btn-secondary", "no strong run action while clearly blocked");
        page.Find("[data-testid=snt-configure-altinn]").GetAttribute("href").Should().Be("/integration-quality-review?view=test-verify");

        // Tester actions grouped, each with an owner and one action; the raw checks stay behind a disclosure.
        page.FindAll("[data-testid=snt-blocker-group]").Select(group => group.GetAttribute("data-group")).Should().Equal("source", "authentication");
        page.FindAll("[data-testid=snt-blocker-owner]").Select(o => o.TextContent).Should().Equal("Owner: Import Project", "Owner: Local execution config");
        page.Find("[data-testid=snt-blocker-details-body]").HasAttribute("hidden").Should().BeTrue("the prerequisite checks are collapsed");
        page.FindAll("[data-testid=snt-attention-item]").Select(li => li.GetAttribute("data-key")).Should().Equal("altinn-auth", "source:MM");
        page.FindAll("[data-testid=snt-attention-item] [data-testid=snt-source-badge]").Should().OnlyContain(badge => badge.TextContent == "Source: BirkNext");
        page.FindAll("[data-testid=snt-resolved], [data-testid=snt-resolved-none]").Should().BeEmpty("\"Resolved automatically: 0\" is not shown");

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
    public void Rapportmottak_shows_source_reference_runtime_and_active_execution_as_separate_dimensions()
    {
        WithTarget();
        WithRapportmottakRows("client-secret");
        _journeys.Pack = _journeys.Pack with { EnvironmentTrusted = false, EnvironmentDetail = "Not enrolled for execution." };
        _journeys.Guidance = new TestPackSourceGuidance("skolenaervaer", [])
        {
            Sources =
            [
                new(new TestPackSourceRole("mu", "MU (Meldingsutveksleren)", "") { Journeys = ["rapportmottak"] }, TestPackSourceState.AvailableNotBound)
                    { AvailableArchives = ["Tiltak 2.zip"], Candidates = [new(Guid.NewGuid(), "Tiltak 2.zip", new string('a', 64), DateTimeOffset.UnixEpoch)] },
                new(new TestPackSourceRole("altinn-app", "Altinn application/API", "") { Journeys = ["rapportmottak"], Provider = TestPackSourceProvider.TestPackReference },
                    TestPackSourceState.ReferenceAvailable),
            ],
        };
        var page = Open("rapportmottak");

        page.WaitForAssertion(() => page.FindAll("[data-testid=snt-readiness-dimension]").Should().HaveCount(4));
        page.FindAll("[data-testid=snt-readiness-dimension]").Select(d => d.TextContent.Replace("\n", " ").Split(':').Last().Trim()).Should().Equal(
            "Available · needs binding", "Available", "Not verified", "Not allowed");
        page.Find("[data-testid=snt-readiness-dimensions]").TextContent.Should().Contain("MU (Meldingsutveksleren) source").And.Contain("Altinn application/API reference")
            .And.Contain("Runtime").And.Contain("Active execution").And.NotContain("Missing");
        page.Find("[data-testid=snt-execution-note]").QuerySelector("a")!.GetAttribute("href").Should().Be("/admin/system-settings?section=target-environments");
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
        page.Find("[data-testid=snt-resolved]").GetAttribute("data-count").Should().Be("17");
        page.Find("[data-testid=snt-resolved-item][data-key=idporten-client] .snt-source").TextContent.Should().Be("Integration Quality Review");
        page.Find("[data-testid=snt-resolved-item][data-key=redirect-uri] .snt-source").TextContent.Should().Be("Local execution config");
        page.Find("[data-testid=snt-resolved-item][data-key=synthetic-data] .snt-source").TextContent.Should().Be("Test Data");
        page.FindAll("[data-testid=snt-attention-item]").Select(li => li.GetAttribute("data-key")).Should().Equal("client-secret");
        page.Find("[data-testid=snt-attention-item]").GetAttribute("data-category").Should().Be("Authentication");
        page.Find("[data-testid=snt-blocker-group]").GetAttribute("data-group").Should().Be("authentication");
        page.Find("[data-testid=snt-blocker-group]").TextContent.Should().Contain("ID-porten test setup").And.Contain("1 check incomplete");

        page.Find("[data-testid=snt-attention-item] button").TextContent.Should().Be("Configure local execution");
        page.Find("[data-testid=snt-group-local-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=snt-attention-item] button").Click();
        page.Find("[data-testid=snt-group-local-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        page.Find("[data-testid=snt-local-setup]").TextContent.Should().Contain("configure-skolenaervaer-tt02-local.ps1").And.Contain("The run page stores nothing");
        JSInterop.VerifyInvoke("birknextFocusElement").Arguments.Should().Equal("snt-group-local");
    }

    [Fact]
    public void Rapportmottak_groups_many_low_level_checks_into_at_most_six_owner_aware_actions_without_trusted_wording()
    {
        WithTarget();
        var unresolved = SkoleNaervaerReadinessPresentation.RunInputs.Where(key => key != "public-metadata").Take(15).ToArray();
        WithRapportmottakRows(unresolved);
        _journeys.Pack = _journeys.Pack with { EnvironmentTrusted = false, EnvironmentDetail = "Not in TargetEnvironments:Trusted (technical)." };
        var page = Open("rapportmottak");

        page.WaitForAssertion(() => page.FindAll("[data-testid=snt-blocker-group]").Should().NotBeEmpty());
        var groups = page.FindAll("[data-testid=snt-blocker-group]");
        groups.Count.Should().BeInRange(1, 6, "tester actions, not a wall of checks");
        groups.Sum(g => int.Parse(g.QuerySelector("[data-testid=snt-blocker-checks]")!.TextContent.Split(' ')[0])).Should()
            .Be(page.FindAll("[data-testid=snt-check-list] [data-testid=snt-attention-item]").Count, "every underlying check is still counted and listed in the details");
        groups.Should().OnlyContain(g => g.QuerySelector("[data-testid=snt-blocker-owner]") != null && g.QuerySelector("[data-testid=snt-blocker-action]") != null);
        page.FindAll("[data-testid=snt-blocker-action].btn-primary").Should().HaveCountLessThanOrEqualTo(1, "at most one strong next action");
        var visible = string.Join(" ", page.FindAll("#snt-rapport > *").Where(e => e.Closest("[hidden]") is null && e.GetAttribute("data-testid") != "snt-rapport-details").Select(e => e.TextContent));
        visible.Should().NotContain("Trusted environment").And.NotContain("Not trusted").And.NotContain("TargetEnvironments:Trusted");
    }

    [Fact]
    public void ArchitectureRules_blocked_by_one_missing_binding_are_summarized_once_with_the_rules_behind_a_disclosure()
    {
        WithTarget();
        string[] rules = ["mm-no-person", "adapter-forwards", "utdanning-person", "report-person"];
        _journeys.Rules = new ArchitectureRuleReport("skolenaervaer", null, null,
            [.. rules.Select(id => new ArchitectureRuleFinding(id, id, "A", "B", ArchitectureRuleExpectation.MustDependOn, ArchitectureRuleOutcome.NotAssessed,
                "Not assessed — this rule requires M2LB source evidence. M2LB (2).zip is available but not bound.", [], ""))], [])
        {
            RuleSources = [.. rules.Select(id => new ArchitectureRuleSource(id, "m2lb", "M2LB", null, "M2LB (2).zip", TestPackSourceState.AvailableNotBound))],
        };
        var page = Open("architecture-rules");

        page.WaitForAssertion(() => page.Find("[data-testid=snt-rules-summary-count]").TextContent.Should().Be("4 rules cannot be assessed"));
        page.Find("[data-testid=snt-rules-summary-reason]").TextContent.Should().Be("Reason: M2LB source evidence is available but not bound.");
        page.Find("[data-testid=snt-rules-summary-action]").TextContent.Should().Be("Bind M2LB evidence");
        page.Find("[data-testid=snt-rules-summary-action]").GetAttribute("href").Should().Be("/integration-quality-review?view=setup-evidence");
        page.Find("[data-testid=snt-rules-details-body]").HasAttribute("hidden").Should().BeTrue("the affected rules are behind a disclosure");
        page.FindAll("[data-testid=snt-rule]").Should().HaveCount(4, "each rule stays inspectable");
        page.FindAll("[data-testid=snt-rule-evidence]").Should().OnlyContain(e => e.TextContent.StartsWith("Available · needs binding"), "an unbound source is never \"Missing\"");
        page.FindAll("[data-testid=snt-rule-assessment]").Should().OnlyContain(a => a.TextContent == "Not assessed", "missing evidence is never pass or fail");
    }

    [Fact]
    public void Rapportmottak_HasNoEditableCopyOfConfiguration()
    {
        WithTarget();
        WithRapportmottakRows("idporten-client");
        var page = Open("rapportmottak");

        page.WaitForAssertion(() => page.FindAll("[data-testid=snt-attention-item]").Should().ContainSingle());
        page.Find("[data-testid=snt-attention-item] a").GetAttribute("href").Should().Be("/integration-quality-review?view=test-verify");
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
        page.Find("[data-testid=snt-nothing-unresolved]").TextContent.Should().Contain("Executable is not passed");
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
        page.Find("[data-testid=snt-handoff-readiness]").TextContent.Should().Be("Blocked");
        page.Find("[data-testid=snt-open-active-tests]").GetAttribute("href").Should().Be("/integration-quality-review?view=test-verify");
        page.Find("[data-testid=snt-open-active-tests]").TextContent.Should().Be("Review Active Event Tests setup", "never implies execution can run while blocked");
        page.FindAll("[data-testid=act-panel], [data-testid=ijp-panel]").Should().BeEmpty("the Active Event tests are linked, never embedded");
        page.Find("#snt-education").TextContent.Should().NotContain("Altinn");
    }

    [Fact]
    public void Periodeavslutning_keeps_its_semantics_and_shows_steps_with_separate_state_columns()
    {
        WithTarget();
        var page = Open("periodeavslutning");

        page.Find("[data-testid=snt-period-operator]").TextContent.Should().StartWith("Period closure is operator-controlled.")
            .And.Contain("only postpones the lock").And.Contain("BirkNext never locks a period");
        page.Find("[data-testid=snt-period-semantics]").TextContent.Should().Be("Service Bus accepted is not the same as Bufdata processed.");
        page.WaitForAssertion(() => page.FindAll("#snt-period [data-testid=ijp-steps] th[role=columnheader]").Select(th => th.TextContent)
            .Should().Equal("Step", "Configured", "Observed", "Verified", "Assessment"));
        var step = page.Find("#snt-period [data-testid=ijp-step]");
        step.QuerySelector("[data-testid=ijp-step-configured]")!.TextContent.Should().Be("Not applicable");
        step.QuerySelector("[data-testid=ijp-step-observed]")!.TextContent.Should().Be("No observer");
        step.QuerySelector("[data-testid=ijp-step-verified]")!.TextContent.Should().Be("No verifier");
        step.QuerySelector("[data-testid=ijp-step-assessment]")!.TextContent.Should().Be(IntegrationJourneyPresentation.Step(JourneyStepState.NotAssessed), "a missing observer is Not assessed, never a failure");
        page.Find("[data-testid=snt-blocker-group]").GetAttribute("data-group").Should().Be("integration");
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
        page.Find("#snt-history [data-testid=ijp-export]").HasAttribute("disabled").Should().BeTrue("an empty history has nothing to export");
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
                new("Security", [new("security", "Security / Gradert tilgang", TestPackViewKind.Security, Description: "From the shared Security Classification evidence.")]),
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
