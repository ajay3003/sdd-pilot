using AngleSharp.Dom;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Integration Quality Review as a review workflow. Before a run the page answers whether to run:
/// target, scope, runtime evidence, readiness, run action, and what will be reviewed. After a run
/// the findings lead and the inventory moves below them.
///
/// Three separations these tests police. CONFIGURED is not OBSERVED — a saved integration is intent,
/// and only a runtime observation is evidence that it ran. Transport metadata is not a schema.
/// And an absent value is unknown, never zero.
/// </summary>
public sealed class IntegrationQualityReviewWorkflowTests : BunitContext
{
    private readonly Mock<IIntegrationQualityReviewService> _review = new();
    private readonly Mock<IFrontendAnalysisContextFactory> _contextFactory = new();

    private static IntegrationConfig Rest(bool enabled = true) => new()
    {
        Id = "rest-1", Name = "Orders API", Type = IntegrationType.REST, Enabled = enabled,
        Endpoint = "https://application-qa.example.test/api/orders",
        LogicalProducerService = "Orders", LogicalConsumerService = "Frontend",
        ConfigurationSource = IntegrationConfigurationSource.EndpointDiscovery,
        ResourceKind = IntegrationResourceKind.RestEndpoint,
    };

    private static IntegrationConfig EventHub(bool enabled = true) => new()
    {
        Id = "eh-1", Name = "Person CDC", Type = IntegrationType.EventHub, Enabled = enabled,
        Resource = "m2lb-cdc-qa.birk.dbo.person", Consumer = "$Default",
        LogicalProducerService = "BiRK / Debezium", LogicalConsumerService = "PersonBiRKAdapter",
        ConfigurationSource = IntegrationConfigurationSource.CodeSuggested,
        ResourceKind = IntegrationResourceKind.EventHub,
    };

    /// <summary>A queue whose producing service the audit never established.</summary>
    private static IntegrationConfig ServiceBus() => new()
    {
        Id = "sb-1", Name = "Leselogg", Type = IntegrationType.ServiceBus, Enabled = true,
        Endpoint = "ns.servicebus.windows.net", Resource = "leselogg",
        LogicalConsumerService = "Revisjon",
        ConfigurationSource = IntegrationConfigurationSource.CodeSuggested,
        ResourceKind = IntegrationResourceKind.ServiceBusQueue,
    };

    private IRenderedComponent<IntegrationQualityReview> Landing(params IntegrationConfig[] integrations)
    {
        var profile = new FrontendAnalysisProfile
        {
            Id = "qa", Name = "M2LB QA", EnvironmentType = FrontendEnvironmentType.QA,
            TargetUrl = "https://application-qa.example.test/",
        };
        profile.Integrations.AddRange(integrations);

        _contextFactory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = profile.TargetUrl, Integrations = integrations.ToList(),
        });

        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_contextFactory.Object);
        Services.AddSingleton(_review.Object);
        Services.AddSingleton(Mock.Of<IReportExportService>());
        Services.AddSingleton<RuntimeReviewSessionService>();
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton(Mock.Of<IEndpointDiscoveryService>());

        var page = Render<IntegrationQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=iqr-decide]"));
        return page;
    }

    private static IReadOnlyList<string> PreRunOrder(IRenderedComponent<IntegrationQualityReview> page) =>
        page.FindAll("[data-testid=iqr-target], [data-testid=iqr-scope], [data-testid=iqr-evidence], [data-testid=iqr-readiness], [data-testid=iqr-run-row], [data-testid=iqr-domains], [data-testid=iqr-details]")
            .Select(e => e.GetAttribute("data-testid")!).ToList();

    private static IElement Domain(IRenderedComponent<IntegrationQualityReview> page, string key) =>
        page.Find($"[data-testid=iqr-domain][data-domain='{key}']");

    private static string DomainState(IRenderedComponent<IntegrationQualityReview> page, string key) =>
        Domain(page, key).QuerySelector("[data-testid=iqr-domain-state]")!.TextContent.Trim();

    /// <summary>Text the reader can actually see: a collapsed disclosure body is hidden and does not count.</summary>
    private static string VisibleText(IElement element)
    {
        var clone = (IElement)element.Clone(true);
        foreach (var hidden in clone.QuerySelectorAll("[hidden]").ToList()) hidden.Remove();
        return clone.TextContent;
    }

    private static bool Collapsed(IRenderedComponent<IntegrationQualityReview> page, string id) =>
        page.Find($"[data-testid={id}-toggle]").GetAttribute("aria-expanded") == "false"
        && page.Find($"[data-testid={id}-body]").HasAttribute("hidden");

    // ── §3, §19. The pre-run decision area ───────────────────────────────────────────────────

    [Fact]
    public void PreRunReadsTargetThenScopeThenRuntimeEvidenceThenReadinessThenRun()
    {
        var page = Landing(Rest(), EventHub());

        PreRunOrder(page).Should().Equal(
            "iqr-target", "iqr-scope", "iqr-evidence", "iqr-readiness", "iqr-run-row", "iqr-domains", "iqr-details");

        var run = page.Find("[data-testid=iqr-run-row] [data-testid=iqr-run-review]");
        run.TextContent.Trim().Should().Be("Run Integration Quality Review");
        run.Closest(".disclosure-body").Should().BeNull("Run is reachable without expanding anything");
    }

    // §4. The Target card no longer repeats the review state.
    [Fact]
    public void TargetCardCarriesNoReviewStatus()
    {
        var page = Landing(Rest());

        page.FindAll("[data-testid=iqr-readiness-status]").Should().BeEmpty();
        page.FindAll("[data-testid=iqr-readiness]").Should().ContainSingle();
        VisibleText(page.Find("[data-testid=iqr-target]")).Should().NotContainAny("Ready to review", "Ready with limitations");
        // One way to the Target Environment from the decision area, not three.
        page.Find("[data-testid=iqr-configure-target]").TextContent.Should().Be("Open Target Environment");
    }

    // §5. Scope counts come from the configured integrations.
    [Fact]
    public void ReviewScopeCountsTheConfiguredIntegrationsByTransport()
    {
        var page = Landing(Rest(), EventHub(), ServiceBus(), EventHub(enabled: false));

        // Target owns the configured total; Review scope owns what is enabled, and counts transports of that scope only.
        page.Find("[data-testid=iqr-integration-count]").TextContent.Should().Be("4");
        page.Find("[data-testid=iqr-scope-headline]").TextContent.Should().Be("3 integrations enabled for review");
        page.Find("[data-testid=iqr-scope-enabled]").TextContent.Should().Be("1 more configured but not enabled");
        page.Find("[data-testid=iqr-scope-transports]").TextContent
            .Should().Contain("1 Event Hub").And.Contain("1 REST").And.Contain("1 Service Bus").And.NotContain("2 Event Hub");
    }

    // ── §7, §8, §9. Configured is not observed ───────────────────────────────────────────────

    [Fact]
    public void ConfiguredIntegrationsAreNeverCountedAsRuntimeEvidence()
    {
        var page = Landing(Rest(), EventHub(), ServiceBus());

        // Three enabled integrations are configured; none was observed running.
        page.Find("[data-testid=iqr-runtime-headline]").TextContent.Should().Be("No runtime evidence observed");
        // Only the REST integration can be observed; messaging runtime evidence is a build limit, stated as such.
        page.Find("[data-testid=iqr-runtime-missing]").TextContent.Should().Be("1 REST/GraphQL integration has no runtime evidence observed");
        page.Find("[data-testid=iqr-runtime-messaging]").TextContent.Should().Be("Messaging runtime evidence is not collected in this build (2 messaging integrations)");

        var evidence = VisibleText(page.Find("[data-testid=iqr-evidence]"));
        evidence.Should().NotContainAny("Connected", "Active", "Observed running");
    }

    // §8. Reachability and health are not runtime evidence, so the placeholder that implied them is gone.
    [Fact]
    public void RuntimeEvidenceIsNotInferredFromReachabilityOrConfiguration()
    {
        var page = Landing(Rest());

        VisibleText(page.Find("[data-testid=iqr-decide]")).Should().NotContain("Depends on environment activity");
        IntegrationReviewPresentation
            .Evidence([Rest()], observations: null).Observed
            .Should().Be(0, "a configured endpoint is intent, not evidence");
    }

    // ── §10, §11. Readiness ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadinessIsStatedOnceWithItsLimitationsCollapsed()
    {
        var page = Landing(Rest(), EventHub());

        page.Find("[data-testid=iqr-readiness]").GetAttribute("data-readiness").Should().Be("Limited");
        page.Find("#iqr-readiness-heading").TextContent.Should().Be("Review can run with limitations");
        Collapsed(page, "iqr-readiness-limitations").Should().BeTrue();
        page.Find("[data-testid=iqr-run-review]").HasAttribute("disabled").Should().BeFalse();

        page.Find("[data-testid=iqr-readiness-limitations-toggle]").Click();
        var body = page.Find("[data-testid=iqr-readiness-limitations-body]");
        body.TextContent.Should().Contain("no runtime evidence observed").And.Contain("Messaging runtime evidence is not collected");
        // None of it reads as a failure.
        body.TextContent.Should().NotContainAny("failed", "error", "broken");
    }

    [Fact]
    public void NoEnabledIntegrationBlocksTheReviewAndKeepsItsReasonVisible()
    {
        var page = Landing(EventHub(enabled: false));

        page.Find("[data-testid=iqr-readiness]").GetAttribute("data-readiness").Should().Be("Blocked");
        page.Find("[data-testid=iqr-readiness]").GetAttribute("role").Should().Be("alert");
        page.Find("[data-testid=iqr-run-review]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=iqr-readiness-message]").TextContent.Should().Be("No configured integration is enabled for review. Enable one in the Target Environment's integrations.");
        page.Find("[data-testid=iqr-go-configure]").TextContent.Should().Be("Configure integrations");
    }

    // ── §12–§18. The six review domains ──────────────────────────────────────────────────────

    [Fact]
    public void AllSixDomainsRenderWithScopeVocabularyOnly()
    {
        var page = Landing(Rest(), EventHub(), ServiceBus());

        page.FindAll("[data-testid=iqr-domain]").Select(d => d.GetAttribute("data-domain"))
            .Should().Equal("relationships", "runtime", "contracts", "compatibility", "drift", "performance");
        page.Find("#iqr-domains-heading").TextContent.Should().Be("What will be reviewed");

        page.FindAll("[data-testid=iqr-domain-state]").Select(s => s.TextContent.Trim())
            .Should().OnlyContain(s => s == "Included" || s == "Limited" || s == "Not assessed" || s == "Unavailable" || s == "Not included");
        // Configuration and connection words are never a domain state.
        page.FindAll("[data-testid=iqr-domain-state]").Select(s => s.TextContent.Trim())
            .Should().NotIntersectWith(["Configured", "Connected", "Enabled", "Available"]);
    }

    // §13. Unknown ownership limits the domain and is never inferred.
    [Fact]
    public void RelationshipsReportsWhatIsRecordedAndInfersNothing()
    {
        var page = Landing(Rest(), ServiceBus());

        DomainState(page, "relationships").Should().Be("Limited");
        var limitation = Domain(page, "relationships").QuerySelector("[data-testid=iqr-domain-limitation]")!.TextContent;
        limitation.Should().Contain("1 of 2 enabled integrations have both producer and consumer recorded");
        limitation.Should().Contain("not inferred from names, paths or resources");
    }

    [Fact]
    public void FullyRecordedRelationshipsAreIncluded()
    {
        var page = Landing(Rest(), EventHub());

        DomainState(page, "relationships").Should().Be("Included");
    }

    // §14. Runtime evidence reports what was observed, and says why messaging cannot be judged.
    [Fact]
    public void RuntimeEvidenceDomainReportsPartialEvidenceRatherThanZero()
    {
        var page = Landing(Rest(), EventHub());

        DomainState(page, "runtime").Should().Be("Not assessed", "missing evidence is not an exclusion");
        var limitation = Domain(page, "runtime").QuerySelector("[data-testid=iqr-domain-limitation]")!.TextContent;
        limitation.Should().Contain("No runtime evidence observed yet");
        limitation.Should().Contain("No runtime evidence observed yet").And.Contain("exercised in the environment");
    }

    // §15. Transport metadata is not a schema.
    [Fact]
    public void ContractsSeparatesTransportConfigurationFromSchemaEvidence()
    {
        var page = Landing(Rest(), EventHub());

        DomainState(page, "contracts").Should().Be("Limited");
        Domain(page, "contracts").QuerySelector("[data-testid=iqr-domain-limitation]")!.TextContent
            .Should().Contain("Transport configuration is not a schema");
    }

    // §16. Compatibility never claims compatibility without comparable evidence.
    [Fact]
    public void CompatibilityNeverClaimsCompatibilityWithoutComparableEvidence()
    {
        var page = Landing(Rest(), EventHub());

        var compatibility = Domain(page, "compatibility").TextContent;
        DomainState(page, "compatibility").Should().Be("Not assessed");
        compatibility.Should().Contain("Insufficient contract evidence");
        compatibility.Should().NotContainAny("Fully compatible", "Compatible.");
    }

    // §17. A first review is not "no drift".
    [Fact]
    public void DriftDistinguishesAFirstBaselineFromNoDrift()
    {
        var schema = EventHub();
        schema.ContractName = "PersonChanged";
        schema.ContractSourceType = ContractSourceType.Auto;
        var page = Landing(schema);

        DomainState(page, "drift").Should().Be("Limited");
        Domain(page, "drift").QuerySelector("[data-testid=iqr-domain-limitation]")!.TextContent
            .Should().Contain("records the baseline").And.Contain("not the same as no drift");
        Domain(page, "drift").TextContent.Should().NotContain("No drift detected");
    }

    // §18. Performance is derived from observation only.
    [Fact]
    public void PerformanceIsNeverDerivedFromConfiguration()
    {
        var page = Landing(Rest(), EventHub());

        DomainState(page, "performance").Should().Be("Not assessed");
        var limitation = Domain(page, "performance").QuerySelector("[data-testid=iqr-domain-limitation]")!.TextContent;
        limitation.Should().Contain("No timing evidence");
        limitation.Should().Contain("Configured timeouts and reachability are not performance evidence");
        // No fabricated measurement anywhere in the domain cards.
        page.Find("[data-testid=iqr-domains]").TextContent.Should().NotContainAny("0 ms", "0 req/s", "0 requests");
    }

    // ── §6, §20. The inventory is supporting detail ──────────────────────────────────────────

    [Fact]
    public void ConfiguredIntegrationsAndRuntimeObservationsAreCollapsedBelowTheDomains()
    {
        var page = Landing(Rest(), EventHub());

        var details = page.Find("[data-testid=iqr-details]");
        details.QuerySelector("[data-testid=iqr-scope-disclosure]").Should().NotBeNull();
        details.QuerySelector("[data-testid=iqr-runtime-disclosure]").Should().NotBeNull();

        Collapsed(page, "iqr-scope-disclosure").Should().BeTrue();
        Collapsed(page, "iqr-runtime-disclosure").Should().BeTrue();
        page.Find("[data-testid=iqr-scope-disclosure-toggle]").TextContent
            .Should().Contain("Configured integrations").And.Contain("2 configured").And.Contain("2 enabled");

        page.Find("[data-testid=iqr-scope-disclosure-toggle]").Click();
        page.Find("[data-testid=iqr-scope-disclosure-body]").QuerySelectorAll("[data-testid=iqr-integration-item]")
            .Should().HaveCount(2, "the inventory is moved, not removed");
    }

    // ── §42. Accessibility of the restructured page ──────────────────────────────────────────

    [Fact]
    public void HeadingsAreHierarchicalAndDisclosuresExposeTheirState()
    {
        var page = Landing(Rest(), EventHub());

        page.FindAll("h1").Should().ContainSingle();
        var h2 = page.FindAll("h2").Select(h => h.TextContent.Trim()).ToList();
        h2.Should().Contain(["Target", "Review scope", "Runtime evidence", "What will be reviewed", "Review details"]);
        h2.Should().OnlyHaveUniqueItems();

        foreach (var toggle in page.FindAll(".disclosure-toggle"))
        {
            toggle.TagName.Should().Be("BUTTON");
            toggle.GetAttribute("aria-expanded").Should().BeOneOf("true", "false");
            page.Find($"#{toggle.GetAttribute("aria-controls")}").Should().NotBeNull();
            toggle.TextContent.Trim().Should().NotBeEmpty();
        }
        page.FindAll(".iqr-pill").Should().OnlyContain(p => p.TextContent.Trim().Length > 0, "state chips carry text");
    }

    // ── Returning to the setup view ───────────────────────────────────────────────────────────

    /// <summary>
    /// Drives the page to a completed result, the way a user does: the service returns a report and Run is clicked.
    /// </summary>
    private IRenderedComponent<IntegrationQualityReview> Result(params IntegrationConfig[] integrations)
    {
        _review.Setup(r => r.AnalyzeAsync(It.IsAny<IntegrationQualityRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IntegrationQualityReport?)new IntegrationQualityReport
            {
                EnvironmentName = "M2LB QA", GeneratedAt = DateTime.UtcNow, OverallScore = 72,
                IntegrationCount = integrations.Length, EnabledCount = integrations.Count(i => i.Enabled),
                Findings =
                [
                    new IntegrationFinding
                    {
                        Severity = IntegrationFindingSeverity.High, Title = "No runtime evidence",
                        Description = "The integration is configured but was never observed.",
                    },
                ],
            }, (string?)null));

        var page = Landing(integrations.Length > 0 ? integrations : [Rest()]);
        page.Find("[data-testid=iqr-run-review]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=iqr-results]"));
        return page;
    }

    /// <summary>
    /// The result page offered no way back to the setup view: only Export HTML at the top, and an in-result row whose
    /// buttons all either start work or leave the page. The Frontend Quality and API Quality Review results already
    /// had the pattern, so this is the same one.
    /// </summary>
    [Fact]
    public void TheResultOffersBackToTheSetupViewBesideExport()
    {
        var page = Result();

        var actions = page.Find(".page-header").QuerySelectorAll("button")
            .Select(b => b.TextContent.Trim()).ToList();

        // Navigate first, then export — the order the other two review results use.
        actions.Should().Equal("Back to Integration Quality Review", "Export HTML");
        // Reachable by its accessible name, and never an icon alone.
        page.Find("[data-testid=iqr-back]").TextContent.Trim().Should().Be("Back to Integration Quality Review");
    }

    // Back navigates. It does not start a review, and the run is never re-issued.
    [Fact]
    public void BackReturnsToSetupWithoutRunningAnything()
    {
        var page = Result();
        _review.Invocations.Clear();

        page.Find("[data-testid=iqr-back]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=iqr-decide]"));
        page.FindAll("[data-testid=iqr-results]").Should().BeEmpty();
        _review.Verify(r => r.AnalyzeAsync(It.IsAny<IntegrationQualityRequest>(), It.IsAny<CancellationToken>()), Times.Never,
            "Back is navigation; Run Review Again is the action that starts work");
    }

    // The completed run survives Back: the runtime session still holds it, so re-entering the page shows it again.
    [Fact]
    public void BackPreservesTheCompletedRun()
    {
        var page = Result();
        var session = Services.GetRequiredService<RuntimeReviewSessionService>();
        var before = session.IntegrationQualityReview.Report;
        before.Should().NotBeNull();

        page.Find("[data-testid=iqr-back]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=iqr-decide]"));

        // Nothing was discarded or reset: it is the same report the run recorded.
        session.IntegrationQualityReview.Report.Should().BeSameAs(before);
        var reopened = Render<IntegrationQualityReview>();
        reopened.WaitForAssertion(() => reopened.Find("[data-testid=iqr-results]"));
    }

    // The rerun action is unchanged and still runs a review.
    [Fact]
    public void RunAgainStillStartsAReview()
    {
        var page = Result();
        _review.Invocations.Clear();

        page.Find("[data-testid=iqr-rerun-btn]").Click();

        page.WaitForAssertion(() =>
            _review.Verify(r => r.AnalyzeAsync(It.IsAny<IntegrationQualityRequest>(), It.IsAny<CancellationToken>()), Times.Once));
        page.Find("[data-testid=iqr-results]").Should().NotBeNull();
    }

    // Export is unchanged and still exports the completed result.
    [Fact]
    public void ExportStillExportsTheResult()
    {
        var page = Result();
        var export = Services.GetRequiredService<IReportExportService>();

        page.Find("[data-testid=iqr-export]").Click();

        Mock.Get(export).Verify(e => e.ExportIntegrationQualityReview(It.IsAny<IntegrationQualityReport>(), It.IsAny<string>()), Times.Once);
    }
}
