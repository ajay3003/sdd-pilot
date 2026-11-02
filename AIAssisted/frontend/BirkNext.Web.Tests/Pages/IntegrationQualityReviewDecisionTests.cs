using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// IQR on the shared readiness model: one Run decision (Assessing / Ready / Ready with limitations / Needs review / Blocked / Failed to assess);
/// review readiness ≠ runtime verification ≠ active execution; static reviewability is said as such; integrations come from one canonical evidence
/// snapshot (Project Evidence, Integration Topology, Connected Systems, Integration Catalog, contracts, runtime) — a discovered integration is
/// reviewed without a catalog entry; Review areas get precise capability-aware states instead of blanket "Not assessable".
/// </summary>
public sealed class IntegrationQualityReviewDecisionTests : BunitContext
{
    private readonly FakeIntegrationCatalogApi _api = new() { Catalog = M2lbFixture.Catalog(), Readiness = M2lbFixture.Readiness(), Result = M2lbFixture.Result() };
    private readonly Mock<IExternalIntegrationsApiService> _external = new();

    public IntegrationQualityReviewDecisionTests()
    {
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext
        {
            ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://app.example.test/" },
        });
        _external.Setup(e => e.ListAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(new ActiveEventApiResult<ExternalIntegrationsView>(
            new("dev", [new() { Integration = new() { Id = "altinn", Name = "Altinn", EnvironmentName = "buf/skolenaervaer", Enabled = true } }], [], []), null));
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
        Services.AddSingleton(context.Object);
        Services.AddSingleton(_external.Object);
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton<IReportExportService, ReportExportService>();
        Services.AddSingleton<RuntimeReviewSessionService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static IntegrationReviewReadiness Readiness(bool canRun, params IntegrationDomainReadinessRow[] domains) => new()
    {
        CanRun = canRun, EnabledIntegrations = canRun ? 3 : 0, ConfiguredIntegrations = canRun ? 3 : 0, Headline = canRun ? "Can run with limitations" : "Cannot run", Domains = [.. domains],
    };

    [Fact]
    public void A_configured_integration_with_static_evidence_only_is_ready_for_static_review_with_runtime_unavailable()
    {
        var cut = Render<IntegrationQualityReview>();
        var decision = cut.Find("[data-testid=iqr-run-decision]");
        decision.GetAttribute("data-decision").Should().Be(nameof(ReviewDecision.ReadyWithLimitations));
        cut.Find("[data-testid=iqr-run-decision-title]").TextContent.Should().Be(IntegrationReviewDecisions.StaticTitle);
        cut.Find("[data-testid=iqr-decision-runtime]").TextContent.Should().Be("Not verified");
        cut.Find("[data-testid=iqr-decision-limitations]").TextContent.Should().Contain("none blocks the read-only review").And.Contain("Runtime transport evidence unavailable");
        cut.Find("[data-testid=iqr-decision-active]").TextContent.Should().Be("Not known", "active verification is its own answer, never part of the review decision");
    }

    private static IntegrationReviewReadiness Discovered() => Readiness(true, new IntegrationDomainReadinessRow(IntegrationReviewDomain.Configuration, IntegrationDomainReadiness.Ready, "")) with
    {
        EnabledIntegrations = 1, ConfiguredIntegrations = 0,
        Subjects = [new("discovered:connected-altinn", "Dev → Altinn", IntegrationKind.HttpApi, IntegrationReviewCapability.HttpApi)
            { Producer = "Dev", Consumer = "Altinn", Origin = IntegrationRecordOrigin.Discovered, Sources = [IntegrationEvidenceFamily.ConnectedSystems] }],
        Candidates =
        [
            new("discovered:connected-altinn", "Dev → Altinn", IntegrationKind.HttpApi, IntegrationRecordOrigin.Discovered, IntegrationCandidateState.Reviewed)
                { Sources = [IntegrationEvidenceFamily.ConnectedSystems], Reason = "Registered under Connected Systems — reviewed without a catalog entry" },
            new("discovered:portal-unknown", "Portal → Unknown consumer", IntegrationKind.ServiceBus, IntegrationRecordOrigin.Discovered, IntegrationCandidateState.NeedsConfirmation)
                { Sources = [IntegrationEvidenceFamily.IntegrationTopology], Relationship = "Candidate", Reason = "one side is not established" },
        ],
        Evidence =
        [
            new(IntegrationEvidenceFamily.ProjectEvidence, IntegrationEvidenceFamilyState.Available, 2, "2 source snapshots · 0 bound to an integration") { CapturedAt = DateTimeOffset.UtcNow.AddHours(-1) },
            new(IntegrationEvidenceFamily.IntegrationTopology, IntegrationEvidenceFamilyState.Available, 1, "1 connection · 0 established"),
            new(IntegrationEvidenceFamily.ConnectedSystems, IntegrationEvidenceFamilyState.Available, 1, "Altinn"),
            new(IntegrationEvidenceFamily.IntegrationCatalog, IntegrationEvidenceFamilyState.None, 0, "No integration is defined — not required"),
            new(IntegrationEvidenceFamily.Contracts, IntegrationEvidenceFamilyState.None, 0, "No trusted contract is stored"),
            new(IntegrationEvidenceFamily.RuntimeEvidence, IntegrationEvidenceFamilyState.None, 0, "No runtime evidence source is configured — the review is static"),
        ],
        EvaluatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void A_registered_Connected_System_is_reviewed_from_the_evidence_without_a_catalog_entry()
    {
        _api.Catalog = new IntegrationCatalog();
        _api.Readiness = Discovered();
        var cut = Render<IntegrationQualityReview>();

        var decision = cut.Find("[data-testid=iqr-run-decision]");
        decision.GetAttribute("data-decision").Should().Be(nameof(ReviewDecision.ReadyWithLimitations), "a discovered integration is reviewable; nothing has to be defined by hand");
        decision.GetAttribute("data-freshness").Should().Be(nameof(EvidenceFreshness.Recent));
        cut.Find("[data-testid=iqr-run-decision-detail]").TextContent.Should().Contain("1 of 1 integration is discovered from evidence — no catalog entry needed");
        cut.Find("[data-testid=iqr-decision-limitations]").TextContent.Should().Contain("1 discovered integration needs confirmation and is not reviewed");
        cut.FindAll("[data-testid=iqr-evidence-family]").Select(f => f.GetAttribute("data-family")).Should().Equal(
            [nameof(IntegrationEvidenceFamily.ProjectEvidence), nameof(IntegrationEvidenceFamily.IntegrationTopology), nameof(IntegrationEvidenceFamily.ConnectedSystems),
             nameof(IntegrationEvidenceFamily.IntegrationCatalog), nameof(IntegrationEvidenceFamily.Contracts), nameof(IntegrationEvidenceFamily.RuntimeEvidence)]);
        cut.FindAll("[data-testid=iqr-evidence-candidate]").Select(c => c.GetAttribute("data-state")).Should().Equal([nameof(IntegrationCandidateState.Reviewed), nameof(IntegrationCandidateState.NeedsConfirmation)]);
        cut.Find("[data-testid=iqr-subject-states]").TextContent.Should().Contain("Discovered · Connected Systems").And.NotContain("Needs configuration");
        cut.Find("[data-testid=iqr-setup-integration]").GetAttribute("data-origin").Should().Be("discovered");
        cut.Markup.Should().NotContain("no Integration Catalog binding exists");
        _external.Verify(e => e.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never, "Connected Systems are read once, by the backend's evidence composition");
    }

    [Fact]
    public void Discovered_but_unestablished_integrations_need_review_with_the_topology_as_the_next_step()
    {
        var d = IntegrationReviewDecisions.Of(Readiness(false) with { Candidates = [.. Discovered().Candidates.Skip(1)] }, false, null, null, "dev");
        d.Decision.Should().Be(ReviewDecision.NeedsReview);
        d.Title.Should().Be("Needs review · integrations discovered, but not established yet");
        d.Detail.Should().Contain("Portal → Unknown consumer");
        d.ActionHref.Should().Be(IntegrationReviewDecisions.TopologyHref);
    }

    [Fact]
    public void An_evidence_family_that_could_not_be_read_is_failed_to_assess_never_no_integration()
    {
        var r = Readiness(false) with { Evidence = [new(IntegrationEvidenceFamily.IntegrationTopology, IntegrationEvidenceFamilyState.Failed, 0, "Integration Topology could not be read.")] };
        IntegrationReviewDecisions.Of(r, false, null, null, "dev").Decision.Should().Be(ReviewDecision.FailedToAssess);
        // When the review can run, a family that failed is a limitation only.
        IntegrationReviewDecisions.Of(Discovered() with { Evidence = r.Evidence }, false, null, null, "dev").Limitations
            .Should().Contain("Integration Topology: could not be read — integrations it would add are not in this review");
    }

    [Fact]
    public void The_snapshot_carries_the_decision_freshness_and_generation()
    {
        var r = Discovered();
        var d = IntegrationReviewDecisions.Of(r, false, null, null, "dev");
        var snapshot = IntegrationReviewDecisions.Snapshot(d, r, 4, [], DateTimeOffset.UtcNow);
        snapshot.Should().Match<ReviewReadinessSnapshot<IntegrationReviewReadiness>>(s => s.CanRun && s.Generation == 4 && s.Freshness == EvidenceFreshness.Recent && s.EvaluatedAt == r.EvaluatedAt);
        IntegrationReviewDecisions.Snapshot(IntegrationReviewDecisions.Of(null, true, null, null, "dev"), null, 1, [], DateTimeOffset.UtcNow).Freshness.Should().Be(EvidenceFreshness.Unknown);
    }

    [Fact]
    public void A_backend_failure_is_failed_to_assess_with_retry_never_blocked()
    {
        _api.LoadFailure = new HttpRequestException("down");
        var cut = Render<IntegrationQualityReview>();
        cut.Find("[data-testid=iqr-run-decision]").GetAttribute("data-decision").Should().Be(nameof(ReviewDecision.FailedToAssess));
        cut.Find("[data-testid=iqr-decision-retry]").Should().NotBeNull();
    }

    [Fact]
    public void Loading_is_assessing_and_nothing_and_no_one_integration_is_blocked()
    {
        IntegrationReviewDecisions.Of(null, loading: true, error: null, null, "dev").Decision.Should().Be(ReviewDecision.Assessing);
        IntegrationReviewDecisions.Of(Readiness(false), false, null, null, "dev").Should().Match<IqrRunDecision>(d => d.Decision == ReviewDecision.Blocked && d.Blockers.Count == 1);
    }

    [Fact]
    public void Active_execution_unavailable_is_a_limitation_of_the_review_never_its_blocker()
    {
        var d = IntegrationReviewDecisions.Of(Readiness(true, new IntegrationDomainReadinessRow(IntegrationReviewDomain.Configuration, IntegrationDomainReadiness.Ready, "")), false, null, (3, 0), "dev");
        d.Decision.Should().NotBe(ReviewDecision.Blocked);
        d.ActiveExecution.Should().Be("Unavailable — 0 / 3 journeys executable");
        d.Limitations.Should().Contain(l => l.StartsWith("Active execution unavailable"));
    }

    [Theory]
    [InlineData(IntegrationReviewDomain.Configuration, IntegrationDomainReadiness.Ready, false, false, "Ready")]
    [InlineData(IntegrationReviewDomain.Connectivity, IntegrationDomainReadiness.NotAssessable, false, false, "Not verified at runtime")]
    [InlineData(IntegrationReviewDomain.MessageFlow, IntegrationDomainReadiness.NotAssessable, true, false, "Static only · not verified at runtime")]
    [InlineData(IntegrationReviewDomain.MessageFlow, IntegrationDomainReadiness.Partial, true, true, "Reviewable with limitations")]
    [InlineData(IntegrationReviewDomain.Reliability, IntegrationDomainReadiness.Limited, true, false, "Limited · static evidence only")]
    [InlineData(IntegrationReviewDomain.Security, IntegrationDomainReadiness.Limited, true, false, "Needs review · authorization not proven")]
    [InlineData(IntegrationReviewDomain.Contract, IntegrationDomainReadiness.NotAssessable, false, false, "No contract evidence")]
    [InlineData(IntegrationReviewDomain.DataQuality, IntegrationDomainReadiness.NotAssessable, true, false, "Limited · declared structure only")]
    [InlineData(IntegrationReviewDomain.Observability, IntegrationDomainReadiness.NotAssessable, false, false, "No monitoring evidence")]
    public void Review_areas_get_precise_capability_aware_states(IntegrationReviewDomain domain, IntegrationDomainReadiness readiness, bool staticEvidence, bool runtime, string expected) =>
        IntegrationReviewDecisions.DomainLabel(new(domain, readiness, "", staticEvidence ? ["source fact"] : [], []), runtime).Should().Be(expected);

    [Fact]
    public void Provenance_separates_static_from_runtime_evidence() =>
        IntegrationReviewDecisions.Provenance(new(IntegrationReviewDomain.MessageFlow, IntegrationDomainReadiness.NotAssessable, "", ["Producer configured"], []), false)
            .Should().Be("Static evidence: Available · Runtime evidence: Not verified");
}
