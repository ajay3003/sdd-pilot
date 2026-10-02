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
/// Event Hub runtime evidence inside Integration Quality Review: evidence-driven domain cards, configured sources kept apart from Azure
/// execution, the configured-vs-observed snapshot of a run ($Default observed ≠ mapping confirmed, checkpoint configuration ≠ checkpoint
/// runtime evidence, no Log Analytics workspace required), What was tested / not assessed, and the export.
/// </summary>
public sealed class IntegrationQualityReviewEventHubRuntimeTests : BunitContext
{
    private static readonly DateTimeOffset Captured = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);
    private readonly FakeIntegrationCatalogApi _api = new() { Catalog = M2lbFixture.SeededCatalog() };

    public IntegrationQualityReviewEventHubRuntimeTests()
    {
        _api.Readiness = Readiness(azure: false);
        _api.Result = Result();
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext
        {
            ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.bufetat.no/" },
        });
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        Services.AddSingleton(new IntegrationMappingEvidenceSession());
        Services.AddSingleton(context.Object);
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton<IReportExportService, ReportExportService>();
        Services.AddSingleton(new RuntimeReviewSessionService());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static IntegrationReviewReadiness Readiness(bool azure) => M2lbFixture.Readiness() with
    {
        AzureRuntimeEnabled = azure, AzureRuntimeReason = azure ? null : M2lbFixture.AzureDisabled, RuntimeSourcesConfigured = 4, RuntimeSourcesTotal = 4,
        Domains =
        [
            new(IntegrationReviewDomain.Configuration, IntegrationDomainReadiness.Ready, "16 enabled integrations will be reviewed against the configured expectation; the comparison with Azure needs runtime evidence.",
                ["16 configured integrations: expected Event Hubs, partitions, retention, consumer group, checkpoint store, authentication and monitoring"],
                ["Comparison with Azure runtime metadata: configured, not read — Azure runtime is not enabled for this instance"]),
            new(IntegrationReviewDomain.MessageFlow, IntegrationDomainReadiness.Partial, "Transport progression evidence can be read; application processing is separate evidence and end-to-end flow is never proven by transport evidence.",
                ["Producer configured (Debezium SQL Server CDC)", "Expected consumer group $Default compared with the groups Azure lists (configured assumption — mapping needs confirmation)"],
                ["Application-processing telemetry: not configured", "End-to-end processing: never proven by transport evidence"]),
            new(IntegrationReviewDomain.Observability, IntegrationDomainReadiness.Limited,
                "Monitoring provider configured; runtime telemetry is not read (Azure runtime is not enabled). Container App logs go to Azure Monitor. Dedicated Log Analytics workspace: Not configured — not required for this platform configuration.",
                ["Monitoring provider: Application Insights", "Application Insights resource appi-m2lb-dev-nwe-001", "Container App logs: Azure Monitor"],
                ["Runtime telemetry: configured, not read — Azure runtime is not enabled for this instance"]),
        ],
    };

    private static EventHubRuntimeSnapshot Snapshot()
    {
        var catalog = M2lbFixture.SeededCatalog();
        var business = catalog.Integrations.Select(i => new EventHubHubComparison
        {
            Hub = i.EndpointOrTopic!, IntegrationId = i.Id, State = i.Id.EndsWith("dbo.Person") ? EventHubComparisonState.MissingInAzure : EventHubComparisonState.ObservedMatch,
            ConfiguredPartitions = 1, ConfiguredRetentionHours = 168, ObservedPartitions = i.Id.EndsWith("dbo.Person") ? null : 1, ObservedRetentionHours = i.Id.EndsWith("dbo.Person") ? null : 168,
            ObservedStatus = i.Id.EndsWith("dbo.Person") ? null : "Active", Detail = "test",
        });
        return new EventHubRuntimeSnapshot
        {
            PlatformId = M2lbFixture.PlatformId, PlatformName = "M2LB DEV Event Hubs", Namespace = "evhns-m2lb-dev-nwe-001", AzureRuntimeEnabled = true, CapturedAt = Captured,
            NamespaceObservation = new() { State = IntegrationEvidenceState.Available, Reason = "read", CapturedAt = Captured, Status = "Active", Sku = "Premium", HubListState = IntegrationEvidenceState.Available,
                Hubs = Enumerable.Range(0, 21).Select(i => new EventHubObservedHub { Name = $"hub{i}" }).ToList() },
            Metrics = new() { State = IntegrationEvidenceState.Available, Reason = "Azure Monitor", CapturedAt = Captured, WindowHours = 24,
                Metrics = [new("IncomingMessages", "Total", 86400, "Count"), new("OutgoingMessages", "Total", 86000, "Count"), new("ServerErrors", "Total", 0, "Count"), new("UserErrors", "Total", 3, "Count"), new("ThrottledRequests", "Total", 0, "Count")] },
            Hubs = [.. business,
                new() { Hub = "connect-status", Technical = true, State = EventHubComparisonState.TechnicalObserved, ObservedPartitions = 1, ObservedStatus = "Active" },
                new() { Hub = "m2lb-cdc-dev.birkm2lb.dbo.unconfigured", State = EventHubComparisonState.AdditionalObserved, ObservedPartitions = 1, ObservedStatus = "Active" }],
            ConsumerGroups = catalog.Integrations.Select(i => new EventHubConsumerGroupComparison
            {
                IntegrationId = i.Id, Hub = i.EndpointOrTopic!, Expected = "$Default", ExpectedProvenance = "Configured assumption", Observed = ["$Default"],
                State = EventHubComparisonState.ObservedMatch, Mapping = "Needs confirmation",
            }).ToList(),
            Checkpoints = catalog.Integrations.Select(i => new EventHubCheckpointSummary
            {
                IntegrationId = i.Id, Hub = i.EndpointOrTopic!, ConsumerGroup = "$Default", GroupAssumed = true, Configuration = "Verified",
                Runtime = i.Id.EndsWith("dbo.Person") ? IntegrationEvidenceState.Available : IntegrationEvidenceState.NotAuthorized, LastUpdated = i.Id.EndsWith("dbo.Person") ? Captured.AddMinutes(-17) : null,
            }).ToList(),
            ApplicationInsights = "appi-m2lb-dev-nwe-001", TelemetryState = IntegrationEvidenceState.NotAuthorized, ContainerAppsLogDestination = "azure-monitor",
            LogAnalytics = "Not configured — not required for this platform configuration",
        };
    }

    private static IntegrationReviewResult Result() => M2lbFixture.Result() with
    {
        EventHubSnapshot = [Snapshot()],
        WhatWasTested = ["Namespace DNS/TCP/TLS — evhns-m2lb-dev-nwe-001.servicebus.windows.net: reachable (Tls13)", "Event Hub namespace metadata — evhns-m2lb-dev-nwe-001 (Azure Resource Manager, GET only)",
            "Consumer group existence — 16 hub(s) (Azure Resource Manager)"],
        WhatWasNotAssessed = ["Business payload correctness — events are never read or consumed.", "End-to-end message processing — transport evidence never proves that the application processed each event.",
            "Consumer checkpoints · M2LB DEV Event Hubs: Not authorized — Checkpoint store could not be read (RequestFailedException (HTTP 403, AuthorizationPermissionMismatch))."],
        Domains = M2lbFixture.Result().Domains.Select(d => d.Domain == IntegrationReviewDomain.MessageFlow
            ? d with { Observed = ["Expected consumer group observed: Observed", "Producer activity: Observed"], Missing = ["End-to-end message flow — Transport progression evidence exists, but application processing has not been observed; end-to-end flow is not proven."] }
            : d).ToList(),
    };

    private IRenderedComponent<IntegrationQualityReview> RunReview()
    {
        var cut = Render<IntegrationQualityReview>();
        cut.Find("[data-testid=iqr-run]").Click();
        cut.WaitForElement("[data-testid=iqr-result]");
        return cut;
    }

    // ── Pre-run ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PreRun_SeparatesConfiguredSourcesFromAzureExecution_AndNeverCallsItAMisconfiguration()
    {
        var cut = Render<IntegrationQualityReview>();
        var line = cut.Find("[data-testid=iqr-azure-runtime]");
        line.GetAttribute("data-azure").Should().Be("disabled");
        line.TextContent.Should().Contain("Runtime evidence sources: Configured (4 of 4) · Azure runtime: Not configured").And.Contain("IntegrationReview:Azure:Enabled is not true").And.Contain("Not an integration misconfiguration");
        cut.FindAll("[data-testid=iqr-evidence-common-reason]").Should().BeEmpty("the instance-level reason is stated once, on the Azure runtime line");
        var runtime = cut.FindAll("[data-testid=iqr-attention-item]").Single(i => i.GetAttribute("data-key") == "runtime");
        runtime.TextContent.Should().Contain("Sources configured (4 of 4) · Azure runtime not enabled for this instance").And.Contain("View runtime evidence");
        runtime.TextContent.Should().NotContain("Configure runtime evidence", "the sources are configured; only Azure execution is off");
    }

    [Fact]
    public void PreRun_WithAzureEnabled_SaysEnabled()
    {
        _api.Readiness = Readiness(azure: true);
        var cut = Render<IntegrationQualityReview>();
        cut.Find("[data-testid=iqr-azure-runtime]").TextContent.Should().Be("Runtime evidence sources: Configured (4 of 4) · Azure runtime: Enabled");
    }

    [Fact]
    public void DomainCards_ExplainWhy_WithTheEvidenceTheyReadAndWhatIsMissing()
    {
        var cut = Render<IntegrationQualityReview>();
        AngleSharp.Dom.IElement Row(string domain) => cut.FindAll("[data-testid=iqr-domain-readiness-card]").Single(c => c.GetAttribute("data-domain") == domain);
        AngleSharp.Dom.IElement Detail(string domain) { Row(domain).QuerySelector("[data-testid=iqr-domain-toggle]")!.Click(); return cut.Find("[data-testid=iqr-domain-detail]"); }
        var flow = Row("MessageFlow");
        flow.GetAttribute("data-readiness").Should().Be("Partial");
        flow.QuerySelector(".iqr-pill")!.TextContent.Should().Be("Partial", "status is a text label, never colour alone");
        flow.QuerySelector(".iqr-pill")!.ClassList.Should().Contain("iqr-pill-observed");
        flow.QuerySelector("[data-testid=iqr-domain-limitation]")!.TextContent.Should().Be("Application-processing telemetry: not configured", "the main limitation is the first missing item");
        var flowDetail = Detail("MessageFlow");
        flowDetail.QuerySelector("[data-testid=iqr-domain-available]")!.TextContent.Should().Contain("$Default").And.Contain("configured assumption — mapping needs confirmation");
        flowDetail.QuerySelector("[data-testid=iqr-domain-missing]")!.TextContent.Should().Contain("End-to-end processing: never proven by transport evidence");
        var observability = Detail("Observability");
        observability.TextContent.Should().Contain("Dedicated Log Analytics workspace: Not configured — not required for this platform configuration");
        observability.QuerySelector("[data-testid=iqr-domain-missing]")!.TextContent.Should().NotContain("workspace");
        Row("Configuration").QuerySelector("[data-testid=iqr-domain-row-action]").Should().BeNull("a Ready domain has no corrective action");
        Detail("Configuration").QuerySelector("[data-testid=iqr-domain-action]").Should().BeNull();
    }

    // ── Post-run ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Result_ShowsConfiguredVsObserved_WithDefaultAsAnAssumptionAndCheckpointsSplit()
    {
        var cut = RunReview();
        var section = cut.Find("[data-testid=iqr-result-eventhub]");
        cut.Find("[data-testid=iqr-eh-azure]").TextContent.Should().Be("Enabled");
        cut.Find("[data-testid=iqr-eh-namespace]").TextContent.Should().Contain("evhns-m2lb-dev-nwe-001 — Observed match · Active · Premium · 21 hub(s) listed");
        cut.Find("[data-testid=iqr-eh-groups]").TextContent.Should().Be("Expected $Default (Configured assumption): Observed match on 16 of 16 hub(s) · Application mapping: Needs confirmation");
        var checkpoints = cut.Find("[data-testid=iqr-eh-checkpoints]").TextContent;
        checkpoints.Should().Contain("Checkpoint configuration: Verified (16 hub(s))");
        checkpoints.Should().Contain("Checkpoint runtime evidence: Observed (1 hub(s)), Not authorized (15 hub(s)) · latest checkpoint 2026-09-30 08:43 UTC");
        var monitoring = cut.FindAll("[data-testid=iqr-eh-monitoring]").Select(m => m.TextContent).ToList();
        monitoring.Should().Equal("appi-m2lb-dev-nwe-001 — Configured", "Not authorized", "Azure Monitor", "Not configured — not required for this platform configuration");
        cut.Find("[data-testid=iqr-eh-metrics]").TextContent.Should().Be("Incoming 86,400 · outgoing 86,000 · server errors 0 · user errors 3 · throttled 0 over 24 h — Observed, no threshold");
        var rows = cut.FindAll("[data-testid=iqr-eh-hub-row]");
        rows.Should().HaveCount(18);
        rows.Select(r => r.GetAttribute("data-state")).Should().Contain(["MissingInAzure", "ObservedMatch", "AdditionalObserved", "TechnicalObserved"]);
        rows.Last().TextContent.Should().Contain("connect-status").And.Contain("Technical / support hub", "technical hubs are listed last and labelled, never as errors");
        rows.Single(r => r.GetAttribute("data-state") == "MissingInAzure").TextContent.Should().Contain("Not found").And.Contain("Configured but not found in Azure");
        section.TextContent.Should().Contain("opening an old run never re-reads Azure");
    }

    [Fact]
    public void Result_ListsWhatWasTestedAndWhatWasNotAssessed()
    {
        var cut = RunReview();
        cut.FindAll("[data-testid=iqr-tested-item]").Select(i => i.TextContent).Should().Equal(Result().WhatWasTested);
        cut.FindAll("[data-testid=iqr-not-assessed-item]").Select(i => i.TextContent).Should().Contain("Business payload correctness — events are never read or consumed.");
        var flow = cut.FindAll("[data-testid=iqr-domain-card]").Single(c => c.GetAttribute("data-domain") == "MessageFlow");
        flow.QuerySelector("[data-testid=iqr-domain-observed]")!.TextContent.Should().Contain("Expected consumer group observed: Observed");
        flow.QuerySelector("[data-testid=iqr-domain-missing]")!.TextContent.Should().Contain("application processing has not been observed");
    }

    [Fact]
    public void ConfiguredVsDeclaredInSource_IsShownFromTheRunsOwnSnapshot_WithoutRuntimeClaims()
    {
        var comparison = new BirkNext.SourceDomains.SourceInfrastructureComparison
        {
            Field = "Event Hubs namespace", ConfiguredValue = "evhns-x", State = BirkNext.SourceDomains.SourceComparisonState.Differs, SourceFingerprint = "abcdef0123456789", AnalyzerVersion = 2,
            EnvironmentBasis = "explicit environment variable file",
            Candidates = [new("infra/azurerm_eventhub_namespace.a", "evhns-y", BirkNext.SourceDomains.InfrastructureResourceKind.EventHubNamespace, "azurerm_eventhub_namespace", null, "tfvars qa.tfvars", "infra/main.tf", 3, BirkNext.SourceArchitecture.ArchitectureEvidenceState.Confirmed)],
        };
        _api.Result = M2lbFixture.Result() with { SourceInfrastructureComparisons = [new("eh", "Payments Event Hubs", null, comparison)] };
        var cut = RunReview();
        var section = cut.Find("[data-testid=iqr-result-source-infra]");
        section.TextContent.Should().Contain("abcdef01").And.Contain("analyzer v2").And.Contain("nothing was changed from source");
        section.QuerySelector("[data-state=Differs] dd")!.TextContent.Should().Be("1");
        cut.Find("[data-testid=iqr-source-infra-row]").TextContent.Should().Contain("evhns-x").And.Contain("Source differs — needs review").And.Contain("evhns-y").And.Contain("Assessed separately (Observed)");
    }

    [Fact]
    public void AnOlderRunWithoutTheseListsSaysSo_AndHasNoEventHubSection()
    {
        _api.Result = M2lbFixture.Result();
        var cut = RunReview();
        cut.Find("[data-testid=iqr-tested]").TextContent.Should().Contain("Not recorded for this run");
        cut.FindAll("[data-testid=iqr-tested-item]").Should().BeEmpty();
        cut.FindAll("[data-testid=iqr-result-eventhub]").Should().BeEmpty();
    }

    // ── Export ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Export_CarriesConfiguredObservedComparisonsProvenanceAndScope_WithoutSecrets()
    {
        var html = new ReportExportService().ExportIntegrationReview(Result(), "BirkNext");
        html.Should().Contain("What was tested").And.Contain("What was not assessed").And.Contain("Event Hubs · configured vs observed · M2LB DEV Event Hubs");
        html.Should().Contain("Expected $Default (Configured assumption): Observed match on 16 of 16 hub(s) · Application mapping: Needs confirmation");
        html.Should().Contain("Checkpoint configuration: Verified (16 hub(s))").And.Contain("Not configured — not required for this platform configuration");
        html.Should().Contain("Configured but not found in Azure").And.Contain("Technical / support hub").And.Contain("2026-09-30 09:00:00Z", "the evidence timestamp is exported");
        html.Should().Contain("Observed, no threshold");
        html.Should().Contain("Consumer group existence — 16 hub(s) (Azure Resource Manager)");
        html.Should().NotContainAny("SharedAccessKey", "SharedAccessSignature", "sig=", "Bearer ", "AccountKey", "Endpoint=sb://", "InstrumentationKey");
    }

    // ── Presentation (pure) ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Presentation_KeepsVocabularyAndNeverJudgesWithoutAThreshold()
    {
        IntegrationReviewResultPresentation.ReadinessTone(IntegrationDomainReadiness.Partial).Should().Be("observed");
        IntegrationReviewResultPresentation.ComparisonTone(EventHubComparisonState.ObservedMatch).Should().Be("observed", "an observed match is not a pass");
        IntegrationReviewResultPresentation.ComparisonTone(EventHubComparisonState.TechnicalObserved).Should().Be("muted");
        IntegrationReviewResultPresentation.ComparisonTone(EventHubComparisonState.NotAuthorized).Should().Be("attention", "not authorized is a limitation, never a failure");
        IntegrationReviewResultPresentation.MetricsLine(Snapshot() with { Metrics = null }).Should().Be("Not read in this run");
        IntegrationReviewResultPresentation.MetricsLine(Snapshot() with { Metrics = new() { State = IntegrationEvidenceState.NotAuthorized, Reason = "Event Hub metrics could not be read (RequestFailedException (HTTP 403))." } })
            .Should().Be("Not authorized — Event Hub metrics could not be read (RequestFailedException (HTTP 403)).");
        var off = Snapshot() with
        {
            AzureRuntimeEnabled = false, TelemetryState = IntegrationEvidenceState.NotConfigured,
            Checkpoints = Snapshot().Checkpoints.Select(c => c with { Runtime = IntegrationEvidenceState.NotConfigured, LastUpdated = null }).ToList(),
        };
        IntegrationReviewResultPresentation.CheckpointLines(off).Should().Equal("Checkpoint configuration: Verified (16 hub(s))", "Checkpoint runtime evidence: Not read (Azure runtime not enabled) (16 hub(s))");
        IntegrationReviewResultPresentation.MonitoringRows(off)[1].Value.Should().Be("Not read (Azure runtime not enabled)", "a configured source that was not read is never 'Not configured'");
        IntegrationReviewPrerunPresentation.AzureRuntimeLine(Readiness(false) with { RuntimeSourcesConfigured = 2 }).Should().Be("Runtime evidence sources: Partially configured (2 of 4) · Azure runtime: Not configured");
        IntegrationReviewPrerunPresentation.AzureRuntimeOff(Readiness(false) with { RuntimeSourcesConfigured = 0 }).Should().BeFalse("with nothing configured the action is to configure, not to enable Azure");
    }
}
