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
/// review readiness ≠ runtime verification ≠ active execution; static reviewability is said as such; a Connected System without a catalog
/// binding is explained, not reported as "no integration"; Review areas get precise capability-aware states instead of blanket "Not assessable".
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

    [Fact]
    public void A_Connected_System_without_a_catalog_binding_is_explained_not_reported_as_no_integration()
    {
        _api.Catalog = new IntegrationCatalog();
        _api.Readiness = Readiness(false);
        var cut = Render<IntegrationQualityReview>();
        cut.Find("[data-testid=iqr-run-decision]").GetAttribute("data-decision").Should().Be(nameof(ReviewDecision.NeedsReview), "a binding to add is a decision, not a blocker");
        cut.Find("[data-testid=iqr-run-decision-title]").TextContent.Should().Be("Connected System available, but no Integration Catalog binding exists");
        cut.Find("[data-testid=iqr-run-decision-detail]").TextContent.Should().Contain("Altinn — buf/skolenaervaer");
        cut.Find("[data-testid=iqr-decision-action]").TextContent.Should().Be("Open Integration Catalog");
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
        IntegrationReviewDecisions.Of(null, loading: true, error: null, [], null, "dev").Decision.Should().Be(ReviewDecision.Assessing);
        IntegrationReviewDecisions.Of(Readiness(false), false, null, [], null, "dev").Should().Match<IqrRunDecision>(d => d.Decision == ReviewDecision.Blocked && d.Blockers.Count == 1);
    }

    [Fact]
    public void Active_execution_unavailable_is_a_limitation_of_the_review_never_its_blocker()
    {
        var d = IntegrationReviewDecisions.Of(Readiness(true, new IntegrationDomainReadinessRow(IntegrationReviewDomain.Configuration, IntegrationDomainReadiness.Ready, "")), false, null, [], (3, 0), "dev");
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
