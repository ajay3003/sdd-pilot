using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceObservability;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Source Analysis → Observability: summary first, correlation boundaries with filters and collapsed evidence, logging findings behind a
/// disclosure with filters, telemetry configuration per environment, capability matrix, provenance, changes and export. Generic fixture.
/// </summary>
public sealed class ObservabilityWorkspaceTests : BunitContext
{
    public ObservabilityWorkspaceTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static ObservabilityEvidence Ev(string file, int line, string symbol, string pattern) => new(file, line, symbol, pattern, "component:Orders.Api");

    internal static SourceObservabilitySnapshot Fixture() => new()
    {
        Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), SourceSnapshotId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"),
        SourceFingerprint = "d34db33f" + new string('0', 56), AnalyzerVersion = 1, ExtractedAt = DateTimeOffset.Parse("2026-10-01T10:00:00Z"), Status = ArchitectureStatus.Partial,
        Correlation = new() { ComponentsWithTracing = 1, HttpBoundaries = 2, MessagingBoundaries = 2, PropagationSupported = 2, PropagationUnresolved = 1 },
        Logging = new()
        {
            Frameworks = ["Microsoft.Extensions.Logging"], StructuredCalls = 12, InterpolatedCalls = 2, ConcatenatedCalls = 1, ExceptionPreserved = 4, ExceptionMessageOnly = 1,
            Dimensions =
            [
                new("tracing", "Tracing configuration", ObservabilityDimensionState.Detected, "Tracing registered in source for 1 component(s)."),
                new("exceptions", "Exception preservation", ObservabilityDimensionState.NeedsReview, "4 pass the exception; 1 log only its message."),
                new("sensitive", "Sensitive-data risk", ObservabilityDimensionState.NotFound, "No sensitive-named values."),
            ],
        },
        Telemetry = new()
        {
            Exporters = ["OTLP exporter"], Sinks = ["Console"],
            ServiceNames = [new("component:Orders.Api", "OpenTelemetry:ServiceName", "orders-api-dev", "src/Orders.Api/appsettings.Development.json", "Development"),
                new("component:Orders.Api", "OpenTelemetry:ServiceName", "orders-api", "src/Orders.Api/appsettings.Production.json", "Production")],
            LogLevels = [new("component:Orders.Api", "Logging:LogLevel:Default", "Information", "src/Orders.Api/appsettings.json", "Default")],
        },
        Components =
        [
            new() { ComponentId = "component:Orders.Api", Name = "Orders.Api", ComponentType = "Api", TracingTechnologies = ["OpenTelemetry"], LoggingFrameworks = ["Microsoft.Extensions.Logging"], TelemetryExporters = ["OTLP exporter"], LogCalls = 10, Correlation = ObservabilityDimensionState.Partial, Logging = ObservabilityDimensionState.NeedsReview },
            new() { ComponentId = "component:Billing.Worker", Name = "Billing.Worker", ComponentType = "Worker", LogCalls = 5, Correlation = ObservabilityDimensionState.NeedsReview, Logging = ObservabilityDimensionState.Detected },
        ],
        Mechanisms = [new() { Id = "m1", Identity = CorrelationIdentityType.W3CTraceContext, Name = "W3C trace context (framework)", Technology = "OpenTelemetry", Components = ["component:Orders.Api"], EvidenceState = ArchitectureEvidenceState.Confirmed }],
        Boundaries =
        [
            new() { Id = "b1", Component = "component:Orders.Api", Type = CorrelationBoundaryType.HttpInbound, Transport = "HTTP", Mechanism = "W3C traceparent extracted by ASP.NET Core instrumentation", Context = [CorrelationIdentityType.W3CTraceContext], Propagation = PropagationState.FrameworkInstrumentation, EvidenceState = ArchitectureEvidenceState.StronglySupported, Evidence = [Ev("src/Orders.Api/Program.cs", 4, "(top-level statements)", "OpenTelemetry ASP.NET Core instrumentation")] },
            new() { Id = "b2", Component = "component:Orders.Api", Type = CorrelationBoundaryType.MessageProduce, Peer = "orders", Transport = "Azure Service Bus", Mechanism = "Correlation set on the outgoing message in code", Context = [CorrelationIdentityType.CorrelationId], Propagation = PropagationState.PropagationExplicit, EvidenceState = ArchitectureEvidenceState.Confirmed },
            new() { Id = "b3", Component = "component:Billing.Worker", Type = CorrelationBoundaryType.MessageConsume, Peer = "orders", Transport = "Kafka", Mechanism = "No propagation found", Propagation = PropagationState.Unresolved, EvidenceState = ArchitectureEvidenceState.Unresolved, Limitation = "Kafka does not propagate trace or correlation context automatically." },
            new() { Id = "b4", Component = "component:Billing.Worker", Type = CorrelationBoundaryType.HttpOutbound, Transport = "HTTP", Mechanism = ".NET HttpClient default traceparent injection", Propagation = PropagationState.PropagationInferred, EvidenceState = ArchitectureEvidenceState.Inferred },
        ],
        Edges = [new("Orders.Api", "Billing.Worker", "orders", "Correlation set on the outgoing message in code → No propagation found", ArchitectureEvidenceState.Unresolved, "Kafka does not propagate.")],
        Findings =
        [
            new() { Id = "ExceptionPreservation:component:Orders.Api:x", Kind = ObservabilityFindingKind.Finding, Category = ObservabilityCategory.ExceptionPreservation, Component = "component:Orders.Api", Technology = "Microsoft.Extensions.Logging",
                Title = "Exception message logged without the exception", Detail = "Only ex.Message is logged.", EvidenceState = ArchitectureEvidenceState.Confirmed, Severity = ObservabilitySeverity.Warning,
                Evidence = [Ev("src/Orders.Api/OrderService.cs", 21, "OrderService.GetAsync", "ex.Message in log call")] },
            new() { Id = "StructuredLogging:component:Orders.Api:i", Kind = ObservabilityFindingKind.Finding, Category = ObservabilityCategory.StructuredLogging, Component = "component:Orders.Api", Technology = "Microsoft.Extensions.Logging",
                Title = "Interpolated log message", Detail = "Interpolation.", EvidenceState = ArchitectureEvidenceState.Confirmed, Severity = ObservabilitySeverity.NeedsReview, Occurrences = 2 },
            new() { Id = "LogLevel:component:Billing.Worker:l", Kind = ObservabilityFindingKind.Finding, Category = ObservabilityCategory.LogLevel, Component = "component:Billing.Worker", Technology = "Microsoft.Extensions.Logging",
                Title = "Potentially noisy: logging inside a loop", Detail = "Advisory.", EvidenceState = ArchitectureEvidenceState.Inferred, Severity = ObservabilitySeverity.Info },
            new() { Id = "Correlation:component:Billing.Worker:u", Kind = ObservabilityFindingKind.Unresolved, Category = ObservabilityCategory.Correlation, Component = "component:Billing.Worker", Technology = "Kafka",
                Title = "No correlation propagation found at Message consume (Kafka)", Detail = "Kafka.", EvidenceState = ArchitectureEvidenceState.Unresolved, Severity = ObservabilitySeverity.NeedsReview },
        ],
        Capabilities = [new("C# / .NET", AnalyzerSupport.Supported, AnalyzerSupport.Supported, "Syntax analysis."), new("JavaScript / TypeScript", AnalyzerSupport.Unsupported, AnalyzerSupport.Unsupported, "Present in this snapshot, not analyzed.")],
        Limitations = [ObservabilitySnapshot.SourceLimitation],
        UnsupportedEvidence = ["Not analyzed: .js source (unsupported language)."],
    };

    private IRenderedComponent<ObservabilityWorkspace> Workspace(SourceObservabilitySnapshot? snapshot = null, SourceObservabilitySnapshot? previous = null) =>
        Render<ObservabilityWorkspace>(p => p.Add(x => x.Snapshot, snapshot ?? Fixture()).Add(x => x.Previous, previous));

    [Fact]
    public void OverviewIsSummaryFirstWithProvenanceAndNoScore()
    {
        var cut = Workspace();
        cut.Find("[data-testid=obs-status]").TextContent.Should().Be("Partial");
        cut.Find("[data-testid=obs-limitation]").TextContent.Should().Be(SourceObservabilityPresentation.Boundary);
        cut.Find("[data-testid=obs-fingerprint]").TextContent.Should().Be("d34db33f…");
        cut.Find("[data-testid=obs-provenance]").TextContent.Should().Contain("analyzer v1").And.Contain("2026-10-01 10:00:00Z");
        cut.Find("[data-testid=obs-needs-review]").TextContent.Should().Be("4", "Warning + 2 interpolated + unresolved; Info advisories are not counted");
        cut.FindAll("[data-testid=obs-dimension]").Select(d => d.GetAttribute("data-state")).Should().Equal("Detected", "NeedsReview", "NotFound");
        cut.FindAll("[data-testid=obs-component-row]").Should().HaveCount(2);
        cut.FindAll("[data-testid=obs-component-row] td.obs-muted").Should().OnlyContain(td => td.TextContent == "Not assessed in Source Analysis");
        cut.Find("[data-testid=obs-unsupported]").TextContent.Should().Contain(".js source");
        cut.Find("[data-testid=obs-capability-detail] .disclosure-body").HasAttribute("hidden").Should().BeTrue("coverage detail is collapsed");
        var text = cut.Markup;
        text.Should().NotContain("Pass").And.NotContain("Fail").And.NotContain("%");
        cut.Find("[data-testid=obs-dimensions]").TextContent.Should().Contain("There is no overall score");
    }

    [Fact]
    public void BoundariesShowDirectionMechanismContextAndStatusWithFiltersAndCollapsedEvidence()
    {
        var cut = Workspace();
        cut.Find("[data-testid=obs-tab-correlation]").Click();
        cut.FindAll("[data-testid=obs-boundary-table] thead th").Select(t => t.TextContent).Should().Equal("From", "Boundary", "To", "Mechanism", "Context", "Evidence", "Status");
        var rows = cut.FindAll("[data-testid=obs-boundary-row]");
        rows.Should().HaveCount(4);
        rows[0].QuerySelectorAll("th, td").Select(c => c.TextContent.Trim()).Take(3).Should().Equal("Caller (not resolved)", "HTTP inHTTP", "Orders.Api");
        rows[0].TextContent.Should().Contain("Framework instrumentation detected").And.Contain("W3C trace context");
        cut.FindAll("[data-testid=obs-boundary-evidence]").Should().BeEmpty("evidence is collapsed until asked for");

        cut.Find("[data-testid=obs-boundary-state]").Change(nameof(PropagationState.Unresolved));
        cut.FindAll("[data-testid=obs-boundary-row]").Should().ContainSingle().Which.GetAttribute("data-propagation").Should().Be("Unresolved");
        cut.Find("[data-testid=obs-boundaries-shown]").TextContent.Should().Be("1 of 4 shown");
        cut.Find("[data-testid=obs-boundary-evidence-toggle]").Click();
        cut.Find("[data-testid=obs-boundary-evidence]").TextContent.Should().Contain("does not propagate");

        cut.Find("[data-testid=obs-boundary-state]").Change("");
        cut.Find("[data-testid=obs-boundary-component]").Change("component:Orders.Api");
        cut.FindAll("[data-testid=obs-boundary-row]").Should().HaveCount(2);
        cut.Find("[data-testid=obs-edges]").TextContent.Should().Contain("Flows between components");
        cut.Find("[data-testid=obs-corr-findings] [data-testid=obs-corr-groups]").TextContent.Should().Contain("Correlation");
    }

    [Fact]
    public void LoggingFindingsAreCollapsedFilterableAndShowLocationsOnly()
    {
        var cut = Workspace();
        cut.Find("[data-testid=obs-tab-logging]").Click();
        cut.Find("[data-testid=obs-logging-summary]").TextContent.Should().Contain("Exception text only").And.Contain("12");
        cut.Find("[data-testid=obs-exporters]").TextContent.Should().Be("OTLP exporter");
        cut.FindAll("[data-testid=obs-log-table]").Should().BeEmpty("findings are behind a disclosure");
        cut.FindAll("[data-testid=obs-log-groups] > div").Select(d => d.GetAttribute("data-category")).Should().Equal("StructuredLogging", "ExceptionPreservation", "LogLevel");

        cut.Find("[data-testid=obs-log-findings-detail-toggle]").Click();
        cut.FindAll("[data-testid=obs-finding-row]").Should().HaveCount(3);
        cut.Find("[data-testid=obs-log-severity]").Change(nameof(ObservabilitySeverity.Warning));
        var row = cut.FindAll("[data-testid=obs-finding-row]").Should().ContainSingle().Subject;
        row.TextContent.Should().Contain("Exception message logged without the exception").And.Contain("Source finding").And.Contain("Warning").And.Contain("Confirmed");
        cut.Find("[data-testid=obs-finding-evidence-toggle]").Click();
        cut.Find("[data-testid=obs-finding-evidence]").TextContent.Should().Contain("src/Orders.Api/OrderService.cs:21").And.Contain("OrderService.GetAsync");

        cut.Find("[data-testid=obs-log-severity]").Change("");
        cut.Find("[data-testid=obs-log-search]").Input("loop");
        cut.FindAll("[data-testid=obs-finding-row]").Should().ContainSingle().Which.TextContent.Should().Contain("Potentially noisy");
        cut.Find("[data-testid=obs-log-shown]").TextContent.Should().Be("1 of 3 shown");

        cut.Find("[data-testid=obs-config-detail-toggle]").Click();
        cut.FindAll("[data-testid=obs-config-row]").Select(r => r.QuerySelectorAll("td")[2].TextContent).Should().Contain(["Development", "Production"], "environment variants are listed, not merged");
    }

    [Fact]
    public void UnsupportedSnapshotsShowNotAssessedNotZero()
    {
        var cut = Workspace(Fixture() with { Status = ArchitectureStatus.Unsupported });
        cut.Find("[data-testid=obs-not-assessed]").TextContent.Should().Contain("not assessed (not zero)");
        cut.FindAll("[data-testid=obs-overview] dd").Should().OnlyContain(d => d.TextContent == "Not assessed");
    }

    [Fact]
    public void ChangesCompareFindingsAndBoundaryStatesOrExplainTheEmptyState()
    {
        var cut = Workspace();
        cut.Find("[data-testid=obs-tab-changes]").Click();
        cut.Find("[data-testid=obs-no-previous]").TextContent.Should().Contain("No previous snapshot available");

        var previous = Fixture() with
        {
            SourceFingerprint = "0ld" + new string('1', 61),
            Findings = Fixture().Findings.Skip(1).Append(new() { Id = "CatchAndSwallow:component:Billing.Worker:e", Category = ObservabilityCategory.CatchAndSwallow, Kind = ObservabilityFindingKind.Finding, Title = "Empty catch block", Component = "component:Billing.Worker" }).ToList(),
            Boundaries = Fixture().Boundaries.Select(b => b.Id == "b3" ? b with { Propagation = PropagationState.PropagationExplicit } : b).ToList(),
        };
        var changes = SourceObservabilityPresentation.Compare(previous, Fixture());
        changes.Should().Contain(c => c.Kind == "Added" && c.Detail == "Exception message logged without the exception")
            .And.Contain(c => c.Kind == "No longer found" && c.Detail == "Empty catch block" && c.Subject == "Billing.Worker")
            .And.Contain(c => c.Kind == "Changed" && c.Area == "Correlation boundary" && c.Detail.Contains("Propagation explicit → Unresolved"));
        cut = Workspace(previous: previous);
        cut.Find("[data-testid=obs-tab-changes]").Click();
        cut.FindAll("[data-testid=obs-change-row]").Should().HaveCount(changes.Count);
    }

    [Fact]
    public void ExportsTheStoredSnapshotAsJson()
    {
        var cut = Workspace();
        cut.Find("[data-testid=obs-export-json]").Click();
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "downloadJsonFile" && (string)i.Arguments[0]! == "source-observability.json" && ((string)i.Arguments[1]!).Contains("\"SourceFingerprint\""));
    }

    // ── Page integration: area tab and overview card from the same selected snapshot; historical snapshots ───────────────
    [Fact]
    public void SourceAnalysisPageOpensObservabilityFromTheSameSnapshot()
    {
        var api = new Mock<IIntegrationCatalogApiService>();
        var id = Guid.NewGuid();
        var observability = Fixture() with { SourceSnapshotId = id };
        var snapshots = new List<IqrSourceSnapshot>
        {
            new() { Id = id, IntegrationId = "source-analysis", Archive = new SourceArchive("orders.zip", observability.SourceFingerprint, 40), AnalyzedAt = DateTimeOffset.Parse("2026-10-01T10:00:00Z"), Observability = observability },
            new() { Id = Guid.NewGuid(), IntegrationId = "source-analysis", Archive = new SourceArchive("legacy.zip", new string('9', 64), 40), AnalyzedAt = DateTimeOffset.Parse("2026-09-01T10:00:00Z") },
            new() { Id = Guid.NewGuid(), IntegrationId = "source-analysis", Archive = new SourceArchive("other-repo.zip", new string('8', 64), 40), AnalyzedAt = DateTimeOffset.Parse("2026-09-15T10:00:00Z"), Observability = Fixture() },
        };
        api.Setup(a => a.ListSourceSnapshotsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(snapshots);
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev" } });
        Services.AddSingleton(api.Object);
        Services.AddSingleton(context.Object);
        Services.AddSingleton<IReportExportService, ReportExportService>();
        var cut = Render<SourceAnalysis>();

        var card = cut.Find("[data-testid=sa-area-card][data-area=Observability]");
        card.GetAttribute("data-analysis").Should().Be(observability.Id.ToString());
        card.QuerySelector("[data-testid=sa-area-status]")!.TextContent.Should().Be("Partial");
        card.QuerySelectorAll("[data-testid=sa-metric]").Select(m => (m.GetAttribute("data-metric"), m.QuerySelector("dd")!.TextContent)).Should().Equal(
            ("Components with tracing", "1"), ("Correlation boundaries", "4"), ("Log calls", "15"), ("Findings needing review", "4"), ("Unresolved boundaries", "1"));
        card.QuerySelector("[data-testid=sa-area-limitation]")!.TextContent.Should().Be(SourceObservabilityPresentation.CardLimitation);
        cut.Find("[data-testid=sa-open-observability]").Click();
        cut.Find("[data-testid=area-observability]").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-testid=obs-fingerprint]").GetAttribute("title").Should().Be(observability.SourceFingerprint);
        api.Verify(a => a.AnalyzeSourceSnapshotAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never, "opening Observability never re-analyzes");

        cut.Find("[data-testid=source-snapshot]").Change(snapshots[1].Id.ToString());
        cut.Find("[data-testid=area-observability]").Click();
        cut.Find("[data-testid=observability-missing]").TextContent.Should().Contain("predates observability analysis");
        cut.Find("[data-testid=area-overview]").Click();
        cut.Find("[data-testid=sa-area-card][data-area=Observability] [data-testid=sa-area-status]").TextContent.Should().Be("Not analyzed");
        cut.Find("[data-testid=source-snapshot]").Change(id.ToString());
        cut.Find("[data-testid=area-observability]").Click();
        cut.Find("[data-testid=obs-tab-changes]").Click();
        cut.Find("[data-testid=obs-no-previous]").Should().NotBeNull("another repository's snapshot is never the comparison baseline");
        cut.FindAll("[data-testid=sa-area-card][data-area=Observability] [data-testid=sa-metric]").Should().BeEmpty();
    }
}
