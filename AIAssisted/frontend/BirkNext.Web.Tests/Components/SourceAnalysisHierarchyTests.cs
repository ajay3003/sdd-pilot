using BirkNext.DatabaseArchitecture;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using BirkNext.SourceObservability;
using BirkNext.Web.Components;
using BirkNext.Web.Components.SourceDomains;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Source Analysis information hierarchy: exactly four top-level areas (Overview, Architecture, Data, Quality Evidence) as real links, legacy
/// area links mapped onto them, every area using the same second-level view navigation, one Export menu outside it, the source-only context
/// stated once, Observability grouped by concept, Configuration summary-first, CI/CD rows compact and grouped by name only where names establish
/// it, Contracts grouped by type with parsing in plain words, and the four status dimensions visually distinct. Presentation only: counts are unchanged.
/// </summary>
public sealed class SourceAnalysisHierarchyTests : BunitContext
{
    private readonly Mock<IIntegrationCatalogApiService> _api = new();
    private List<IqrSourceSnapshot> _snapshots = [];
    private static readonly Guid SnapshotId = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private const string Fp = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    public SourceAnalysisHierarchyTests()
    {
        _api.Setup(a => a.ListSourceSnapshotsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _snapshots.ToList());
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext());
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(context.Object);
        Services.AddSingleton(Mock.Of<ITechnologyCoverageApiService>());
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddScoped<ProjectApplicabilityState>();
        Services.AddScoped<SourceAnalysisSnapshotState>();
        Services.AddSingleton<IReportExportService, ReportExportService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>Opens a quality-evidence domain the way a user does: Quality Evidence, then the domain in its view navigation.</summary>
    private static void OpenDomain(IRenderedComponent<SourceAnalysis> cut, string slug)
    {
        cut.Find("[data-testid=area-qualityevidence]").Click();
        cut.Find($"[data-testid=sa-quality-{slug}]").Click();
    }

    private IRenderedComponent<SourceAnalysis> Page()
    {
        _snapshots =
        [
            new()
            {
                Id = SnapshotId, IntegrationId = "source-analysis", Archive = new SourceArchive("shop.zip", Fp, 40), AnalyzedAt = DateTimeOffset.Parse("2026-10-02T10:00:00Z"), Status = SourceAnalysisStatus.Ready,
                Architecture = new ArchitectureSnapshot { Status = ArchitectureStatus.Partial, SourceSnapshotId = SnapshotId, SourceFingerprint = Fp },
                DatabaseArchitecture = new DatabaseArchitectureSnapshot { Status = DatabaseAnalysisStatus.Partial, SourceSnapshotId = SnapshotId, SourceFingerprint = Fp },
                Observability = ObservabilityWorkspaceTests.Fixture(), EvidenceDomains = SourceEvidenceDomainsUiTests.Evidence(),
            },
        ];
        var cut = Render<SourceAnalysis>();
        cut.WaitForElement("[data-testid=sa-current]");
        return cut;
    }

    // ── Main navigation ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Exactly_four_areas_are_links_and_quality_evidence_holds_the_five_domains()
    {
        var cut = Page();
        var nav = cut.Find("[data-testid=sa-areas]");
        nav.QuerySelectorAll("[role=tab], [role=tablist], button").Should().BeEmpty("areas are links, not ARIA tabs or buttons");
        var links = nav.QuerySelectorAll("a.sa-area-tab");
        links.Select(a => a.TextContent).Should().Equal("Overview", "Architecture", "Data", "Quality Evidence");
        links.Should().OnlyContain(a => a.GetAttribute("href")!.Contains("area="), "every area is a real deep link (keyboard and new-tab capable)");
        cut.FindAll("[data-testid=sa-area-group-label]").Should().BeEmpty("no eight-tab legacy bar with group captions");
        cut.Find("[data-testid=area-qualityevidence]").Click();
        var domains = cut.Find("nav.sa-subnav[aria-label='Quality evidence domains']");
        domains.QuerySelectorAll("button").Select(b => b.TextContent).Should().Equal("Observability", "Infrastructure", "Configuration", "CI/CD", "Contracts");
        cut.FindAll("[data-testid=sa-quality-rows] [data-testid=sa-summary-row]").Select(r => r.GetAttribute("data-key")).Should().Equal("observability", "infrastructure", "configuration", "cicd", "contracts");
    }

    [Fact]
    public void Exactly_one_area_is_current_and_clicking_moves_it()
    {
        var cut = Page();
        cut.FindAll("[data-testid=sa-areas] [aria-current]").Should().ContainSingle().Which.TextContent.Should().Be("Overview");
        OpenDomain(cut, "contracts");
        cut.FindAll("[data-testid=sa-areas] [aria-current]").Should().ContainSingle().Which.GetAttribute("data-testid").Should().Be("area-qualityevidence");
        cut.Find("[data-testid=area-qualityevidence]").GetAttribute("aria-current").Should().Be("page");
        cut.Find("[data-testid=contracts-workspace]");
        cut.Find("[data-testid=area-architecture]").Click();
        cut.FindAll("[data-testid=sa-areas] [aria-current]").Should().ContainSingle().Which.GetAttribute("data-testid").Should().Be("area-architecture");
    }

    [Fact]
    public void Evidence_mode_is_stated_once_with_runtime_and_target_context()
    {
        var cut = Page();
        var context = cut.Find("[data-testid=sa-target-context]");
        cut.Find("[data-testid=sa-evidence-mode]").TextContent.Should().Be("Source only");
        cut.Find("[data-testid=sa-runtime-verification]").TextContent.Should().Be("Not performed here");
        cut.Find("[data-testid=sa-target-state]").TextContent.Should().Be("Not selected");
        context.TextContent.Should().Contain(SourceAnalysisOverview.SafetyNote);
        cut.Markup.Split(SourceDomainText.SourceBoundary).Length.Should().Be(1, "the generic per-page banner is gone from the Overview");
        cut.Find("[data-testid=sa-status-legend-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.FindAll("[data-testid=sa-status-legend-list] > div").Select(d => d.GetAttribute("data-dimension")).Should().Equal("Confidence", "Completeness", "Resolution", "ReviewAction", "Runtime", "ParserSupport");
    }

    [Fact]
    public void Each_domain_page_shows_its_own_limitation_not_the_generic_banner()
    {
        var cut = Page();
        foreach (var (area, workspace, note) in new[]
                 {
                     ("infrastructure", "infrastructure-workspace", SourceEvidenceDomainsPresentation.InfrastructureNote),
                     ("configuration", "configuration-workspace", SourceEvidenceDomainsPresentation.ConfigurationNote),
                     ("cicd", "cicd-workspace", SourceEvidenceDomainsPresentation.PipelineNote),
                     ("contracts", "contracts-workspace", SourceEvidenceDomainsPresentation.ContractsNote),
                     ("observability", "observability-workspace", SourceObservabilityPresentation.Boundary),
                 })
        {
            OpenDomain(cut, area);
            var page = cut.Find($"[data-testid={workspace}]");
            page.QuerySelector(".sa-scope-note")!.TextContent.Should().Be(note);
            page.QuerySelectorAll(".sd-boundary, .arch-limitation, .obs-limitation").Should().BeEmpty("source limitations use the informational note, not warning styling");
            page.Children.Where(c => c.TagName == "P").Should().NotContain(p => p.TextContent.Contains(SourceDomainText.SourceBoundary), "no generic banner; an analyzer may still list it among its limitations");
        }
    }

    // ── Overview ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Overview_is_decision_first_with_one_row_per_domain_and_attention_apart_from_limitations()
    {
        var cut = Page();
        var snapshot = _snapshots[0];
        cut.FindAll("[data-testid=sa-summary-rows] [data-testid=sa-summary-row]").Select(r => r.GetAttribute("data-key")).Should().Equal("architecture", "data", "observability");
        cut.FindAll("[data-testid=sa-evidence-rows] [data-testid=sa-summary-row]").Select(r => r.GetAttribute("data-key")).Should().Equal("configuration", "cicd", "contracts", "infrastructure");
        foreach (var row in cut.FindAll("[data-testid=sa-summary-row][data-status=Partial]"))
            row.QuerySelector("[data-testid=sa-summary-reason]")!.TextContent.Should().NotBeNullOrWhiteSpace("a Partial status always carries its reason");
        cut.Find("[data-testid=sa-summary-row][data-key=observability] [data-testid=sa-summary-reason]").TextContent
            .Should().Be(SourceAnalysisReview.EvidenceNeedsReview(SourceObservabilityPresentation.NeedsReview(snapshot.Observability!)));
        cut.Find("[data-testid=sa-attention-count]").TextContent.Should().Be(SourceAnalysisReview.Plural(SourceAnalysisReview.ReviewAreas(snapshot).Count, "area", "areas"), "areas are counted; item counts of different kinds are never summed");
        cut.Find("[data-testid=sa-analyzer-limitations-toggle]").GetAttribute("aria-expanded").Should().Be("false", "analyzer limitations are separate and collapsed");
        cut.Find("[data-testid=sa-cross-domain-disclosure-toggle]").GetAttribute("aria-expanded").Should().Be("false", "cross-source relationships are one disclosure away");
        cut.Find("[data-testid=sa-cross-domain]").TextContent.Should().Contain("Cross-source relationships");
        cut.Find("[data-testid=sa-generated-docs]");
        cut.FindAll("[data-testid=sa-overview-actions] .btn-primary").Should().ContainSingle().Which.TextContent.Should().Be("Review architecture");
    }

    // ── Second-level navigation and actions ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("architecture", "Architecture views", 7, "Summary")]
    [InlineData("data", "Data views", 5, "Overview")]
    [InlineData("observability", "Observability views", 4, "Overview")]
    [InlineData("infrastructure", "Infrastructure views", 11, "Overview")]
    [InlineData("configuration", "Configuration views", 4, "Summary")]
    public void Every_area_uses_the_same_view_navigation_with_views_only(string area, string label, int views, string first)
    {
        var cut = Page();
        if (area is "architecture" or "data") cut.Find($"[data-testid=area-{area}]").Click();
        else OpenDomain(cut, area);
        var nav = cut.Find($"nav.sa-subnav[aria-label='{label}']");
        var items = nav.QuerySelectorAll("button");
        items.Should().HaveCount(views).And.OnlyContain(b => b.ClassList.Contains("sa-subnav-item") && b.GetAttribute("type") == "button");
        nav.QuerySelectorAll("[aria-current]").Should().ContainSingle().Which.TextContent.Should().Be(first);
        nav.QuerySelectorAll("[aria-pressed], [role=tab]").Should().BeEmpty("one pattern: buttons with aria-current");
        nav.TextContent.Should().NotContain("Export", "actions never sit inside view navigation");
        nav.QuerySelectorAll(".sa-subnav-caption").Should().OnlyContain(c => c.GetAttribute("aria-hidden") == "true", "a caption is never a tab");
        var last = items[^1].TextContent;
        items[^1].Click();
        cut.Find($"nav.sa-subnav[aria-label='{label}']").QuerySelectorAll("[aria-current]").Should().ContainSingle().Which.TextContent.Should().Be(last);
    }

    [Fact]
    public void One_export_menu_per_area_sits_in_the_section_header_and_keeps_its_behaviour()
    {
        var arch = Render<ArchitectureWorkspace>(p => p.Add(x => x.Snapshot, new ArchitectureSnapshot { Status = ArchitectureStatus.Partial, SourceFingerprint = Fp }));
        arch.Find("[data-testid=arch-views]").TextContent.Should().NotContain("Export");
        arch.FindAll("[data-testid=arch-actions] .export-menu-toggle").Should().ContainSingle("one Export button, the formats behind it").Which.TextContent.Should().Contain("Export");
        arch.Find("[data-testid=arch-actions] .export-menu-list").HasAttribute("hidden").Should().BeTrue("formats show only when the menu is opened");
        arch.Find("[data-testid=arch-export-menu-toggle]").Click();
        arch.FindAll("[data-testid=arch-export-menu] li button").Select(b => b.TextContent).Should().Equal(["HTML", "JSON"], "SVG only where the graph is shown");
        arch.Find("[data-testid=arch-export-menu-html]").Click();
        arch.Find("[data-testid=arch-export-menu-toggle]").Click();
        arch.Find("[data-testid=arch-export-menu-json]").Click();
        JSInterop.Invocations.Select(i => i.Identifier).Should().Contain(["downloadHtmlFile", "downloadJsonFile"]);
        JSInterop.Invocations.Single(i => i.Identifier == "downloadHtmlFile").Arguments[0].Should().Be("source-architecture-abcdef012345.html");
        arch.Find("[data-testid=arch-tab-system]").Click();
        arch.Find("[data-testid=arch-export-menu-toggle]").Click();
        arch.FindAll("[data-testid=arch-export-menu] li button").Select(b => b.TextContent).Should().Equal("HTML", "JSON", "SVG");

        var db = Render<DatabaseWorkspace>(p => p.Add(x => x.Snapshot, new DatabaseArchitectureSnapshot { Status = DatabaseAnalysisStatus.Partial, SourceFingerprint = Fp }));
        db.Find("[data-testid=db-views]").TextContent.Should().NotContain("Export");
        db.Find("[data-testid=db-export-menu-toggle]").Click();
        db.Find("[data-testid=db-export-menu-html]").Click();
        db.Find("[data-testid=db-export-menu-toggle]").Click();
        db.Find("[data-testid=db-export-menu-json]").Click();
        JSInterop.Invocations.Count(i => i.Identifier == "downloadHtmlFile" && (string)i.Arguments[0]! == "source-database-report.html").Should().Be(1);
        JSInterop.Invocations.Count(i => i.Identifier == "downloadJsonFile" && (string)i.Arguments[0]! == "source-database-model.json").Should().Be(1);

        var obs = Render<ObservabilityWorkspace>(p => p.Add(x => x.Snapshot, ObservabilityWorkspaceTests.Fixture()));
        obs.Find("[data-testid=obs-views]").TextContent.Should().NotContain("Export");
        obs.Find("[data-testid=obs-export-menu-toggle]").Click();
        obs.Find("[data-testid=obs-actions] [data-testid=obs-export-menu-json]").Click();
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "downloadJsonFile" && (string)i.Arguments[0]! == "source-observability.json");
    }

    // ── Compact disclosures ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Analyzer_coverage_and_limitations_are_one_compact_disclosure_that_announces_its_state()
    {
        var cut = Render<InfrastructureWorkspace>(p => p.Add(x => x.Evidence, SourceEvidenceDomainsUiTests.Evidence()));
        var row = cut.Find("[data-testid=sd-infra-limitations]");
        row.ClassList.Should().Contain("sa-disclosures");
        row.QuerySelectorAll(".sd-panel").Should().BeEmpty("no half-page card holds a single button");
        row.QuerySelectorAll("button.disclosure-toggle").Should().ContainSingle("coverage and limitations are one pattern, not a pair of buttons");
        var toggle = cut.Find("[data-testid=sd-infra-analyzer-toggle]");
        toggle.TextContent.Should().Contain("Analyzer details (").And.Contain("limitations)");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.Click();
        cut.Find("[data-testid=sd-infra-analyzer-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll("[data-testid=sd-infra-capability]").Should().HaveCount(2, "the detail is unchanged");

        var obs = Render<ObservabilityWorkspace>(p => p.Add(x => x.Snapshot, ObservabilityWorkspaceTests.Fixture()));
        obs.Find("[data-testid=obs-capability-detail-toggle]").TextContent.Should().Contain("Analyzer details").And.Contain("1 unsupported");
        obs.Find("[data-testid=obs-capability-detail-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        obs.FindAll("[data-testid=obs-limitations-detail-toggle]").Should().BeEmpty("no second raw Limitations button");
    }

    // ── Observability ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] AllDimensionIds = ["tracing", "propagation", "structured", "exceptions", "swallow", "context", "sensitive", "redaction", "retry", "handlers", "exporters", "service-name", "sampling", "tests"];

    [Fact]
    public void Observability_groups_every_dimension_by_concept_with_state_counts()
    {
        var states = new[] { ObservabilityDimensionState.Detected, ObservabilityDimensionState.Partial, ObservabilityDimensionState.NotFound, ObservabilityDimensionState.NeedsReview };
        var dimensions = AllDimensionIds.Select((id, i) => new ObservabilityDimension(id, $"Title {id}", states[i % states.Length], $"Detail {id}")).ToList();
        var snapshot = ObservabilityWorkspaceTests.Fixture() with { Logging = ObservabilityWorkspaceTests.Fixture().Logging with { Dimensions = dimensions } };
        var cut = Render<ObservabilityWorkspace>(p => p.Add(x => x.Snapshot, snapshot));

        cut.FindAll("[data-testid=obs-dimension-group]").Select(g => g.GetAttribute("data-group")).Should().Equal("tracing", "logging", "errors", "data-safety", "telemetry", "tests");
        cut.FindAll("[data-testid=obs-dimension-group] h4").Select(h => h.ChildNodes[0].TextContent.Trim()).Should().Equal("Tracing", "Logging", "Error observability", "Data safety", "Telemetry", "Developer tests");
        cut.FindAll("[data-testid=obs-dimension]").Select(d => d.GetAttribute("data-dimension")).Should().BeEquivalentTo(AllDimensionIds, "every dimension stays reachable, exactly once");
        string Ids(string group) => string.Join(",", cut.FindAll($"[data-group={group}] [data-testid=obs-dimension]").Select(d => d.GetAttribute("data-dimension")));
        Ids("tracing").Should().Be("tracing,propagation,service-name,sampling");
        Ids("logging").Should().Be("structured,context,handlers");
        Ids("errors").Should().Be("exceptions,swallow,retry");
        Ids("data-safety").Should().Be("sensitive,redaction");
        cut.Find("[data-group=tracing] [data-testid=obs-group-summary]").TextContent.Should().Be(SourceObservabilityPresentation.StateSummary(dimensions.Where(d => d.Id is "tracing" or "propagation" or "service-name" or "sampling")));
        var summary = cut.FindAll("[data-testid=obs-state-summary] > div").ToDictionary(d => d.GetAttribute("data-state")!, d => int.Parse(d.QuerySelector("dd")!.TextContent));
        summary.Values.Sum().Should().Be(dimensions.Count, "the status summary counts every dimension once");
        summary["NeedsReview"].Should().Be(dimensions.Count(d => d.State == ObservabilityDimensionState.NeedsReview));
    }

    [Fact]
    public void An_unknown_dimension_lands_in_other_instead_of_disappearing()
    {
        var groups = SourceObservabilityPresentation.Groups([new("tracing", "Tracing", ObservabilityDimensionState.Detected, ""), new("future", "Future", ObservabilityDimensionState.NotFound, "")]);
        groups.Select(g => g.Key).Should().Equal("tracing", "other");
    }

    [Fact]
    public void Component_table_merges_overlapping_columns_without_dropping_data()
    {
        var cut = Render<ObservabilityWorkspace>(p => p.Add(x => x.Snapshot, ObservabilityWorkspaceTests.Fixture()));
        cut.FindAll("[data-testid=obs-components] thead th").Select(t => t.TextContent).Should().Equal("Component", "Tracing", "Logging", "Exporters", "Runtime", "Evidence details");
        var row = cut.Find("[data-testid=obs-component-row][data-component='component:Orders.Api']");
        row.TextContent.Should().Contain("10 calls").And.Contain("1 exporter").And.Contain("Needs review").And.Contain("Partial")
            .And.Contain("Not assessed in Source Analysis", "runtime telemetry is never implied");
        row.QuerySelector("[data-testid=obs-component-evidence-toggle]")!.Click();
        cut.Find("[data-testid=obs-component-evidence]").TextContent.Should().Contain("OpenTelemetry").And.Contain("Microsoft.Extensions.Logging").And.Contain("10 log calls").And.Contain("OTLP exporter");
    }

    // ── Configuration ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Configuration_is_summary_first_and_names_environment_labels_as_source_labels()
    {
        var evidence = SourceEvidenceDomainsUiTests.Evidence();
        var cut = Render<ConfigurationWorkspace>(p => p.Add(x => x.Evidence, evidence));
        cut.FindAll("[data-testid=sd-config-overview] dt").Select(d => d.TextContent).Should().Equal("Classified", "Needs classification", "Source environment labels", "Sensitive references");
        cut.Find("[data-testid=sd-config-lead]").TextContent.Should().Be($"{evidence.Configuration.Entries.Count} entries across {evidence.Configuration.Files.Count} files");
        cut.Find("[data-testid=sd-config-labels]").TextContent.Should().Contain("not verified Target Environments");
        cut.Find("[data-testid=sd-config-environments] h3").TextContent.Should().Be("Source environment labels");
        cut.Find("[data-testid=sd-config-environments]").TextContent.Should().Contain("not deployed environments");
        cut.Find("[data-testid=sd-config-categories-detail-toggle]").GetAttribute("aria-expanded").Should().Be("false", "the full category table is secondary");
        cut.FindAll("[data-testid=sd-config-category]").Sum(r => int.Parse(r.QuerySelector("td")!.TextContent)).Should().Be(evidence.Configuration.Entries.Count, "the breakdown covers every entry");
        cut.Find("[data-testid=sd-config-signals]").TextContent.Should().Contain("Placeholders").And.Contain("Potential conflicts");
        cut.Find("[data-testid=sd-config-conflict-count]").TextContent.Should().Be("1");
        cut.Markup.Should().NotContain("Server=", "sensitive values are never rendered");
    }

    // ── CI/CD ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static PipelineDefinition Pipeline(string name, bool template = false) => new() { Id = name + ".yml", File = name + ".yml", Name = name, IsTemplate = template, Platform = PipelinePlatform.AzurePipelines,
        Triggers = template ? [] : [new PipelineTrigger { Type = "push", BranchesInclude = ["main"] }, new PipelineTrigger { Type = "schedule", Schedule = "0 3 * * *" }, new PipelineTrigger { Type = "push", BranchesInclude = ["release/*"] }] };

    [Theory]
    [InlineData("orders-dev", "orders", "dev")]
    [InlineData("Orders_QA", "Orders", "QA")]
    [InlineData("hendelse.prod", "hendelse", "prod")]
    [InlineData("deploy", null, null)]
    [InlineData("dev", null, null)]
    [InlineData("dev-tools", null, null)]
    [InlineData("qa-smoke", null, null)]
    public void Name_variants_need_an_explicit_environment_suffix(string name, string? stem, string? variant)
    {
        var result = SourceEvidenceDomainsPresentation.NameVariant(name);
        if (stem is null) result.Should().BeNull();
        else result.Should().Be((stem, variant!));
    }

    [Fact]
    public void Pipelines_group_only_when_names_share_a_stem_and_counts_are_unchanged()
    {
        var evidence = new PipelineEvidence { Pipelines = [Pipeline("orders-dev"), Pipeline("orders-qa"), Pipeline("billing-dev"), Pipeline("tools"), Pipeline("shared", template: true)] };
        var groups = SourceEvidenceDomainsPresentation.PipelineGroups(evidence);
        groups.Select(g => g.Stem).Should().Equal([null, "orders", null, null], "billing-dev has no sibling, so it is not grouped; order follows the files");
        groups[0].Pipelines.Single().Name.Should().Be("billing-dev");
        groups[1].Variants.Should().Equal("dev", "qa");
        groups.Last().Templates.Should().BeTrue();
        groups.Sum(g => g.Pipelines.Count).Should().Be(evidence.Pipelines.Count, "grouping never drops or duplicates a pipeline");
    }

    [Fact]
    public void Pipeline_rows_show_trigger_types_and_keep_the_exact_rules_in_the_detail()
    {
        var full = SourceEvidenceDomainsUiTests.Evidence();
        var evidence = full with { CiCd = full.CiCd with { Pipelines = [Pipeline("orders-dev"), Pipeline("orders-qa"), Pipeline("tools")] } };
        var cut = Render<PipelineWorkspace>(p => p.Add(x => x.Evidence, evidence));
        cut.FindAll("[data-testid=sd-cicd-pipeline]").Should().HaveCount(3);
        cut.FindAll("[data-testid=sd-cicd-group-header]").Should().ContainSingle().Which.TextContent.Should().Contain("orders").And.Contain("2 variants by name: dev, qa");
        cut.Find("[data-testid=sd-cicd-grouping-note]").TextContent.Should().Contain("not shared ownership");
        cut.FindAll("[data-testid=sd-cicd-trigger-types]").Select(t => t.TextContent).Should().AllBe("Push, Schedule", "distinct trigger types only");
        cut.FindAll("[data-testid=sd-cicd-pipeline-table] thead th").Select(t => t.TextContent).Should().Equal("Pipeline", "Definition", "Trigger", "Tests", "Deploy", "Environment", "Pipeline details");
        cut.FindAll("[data-testid=sd-cicd-open]").Should().OnlyContain(b => b.TextContent.StartsWith("View pipeline details"), "no generic Details button");
        cut.FindAll("[data-testid=sd-cicd-open]")[2].Click();
        var detail = cut.Find("[data-testid=sd-cicd-triggers]");
        detail.QuerySelectorAll("li").Select(l => l.TextContent).Should().Equal("Push · branches main", "Schedule · cron 0 3 * * *", "Push · branches release/*");
        cut.Find("[data-testid=sd-cicd-pipeline-table]").GetAttribute("role").Should().Be("table", "stacked rows on narrow screens keep table semantics");
    }

    // ── Contracts ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_contract_type_belongs_to_exactly_one_group()
    {
        foreach (var type in Enum.GetValues<SourceContractType>())
            SourceEvidenceDomainsPresentation.ContractGroups.Count(g => g.Types.Contains(type)).Should().Be(1, type.ToString());
    }

    private static SourceEvidenceDomainsSnapshot Contracts()
    {
        var full = SourceEvidenceDomainsUiTests.Evidence();
        SourceContract C(string id, SourceContractType type, string name, string? producer = null, params string[] consumers) => new()
            { Id = id, Type = type, Name = name, File = $"src/{name}.txt", Producer = producer, ConsumerHints = [.. consumers], ParseSupport = type == SourceContractType.OpenApi ? DomainSupport.Supported : DomainSupport.Partial,
              EvidenceState = producer is null ? ArchitectureEvidenceState.Inferred : ArchitectureEvidenceState.Confirmed };
        return full with { Contracts = full.Contracts with { Contracts =
        [
            C("o1", SourceContractType.OpenApi, "Orders API", "Orders.Api"), C("o2", SourceContractType.OpenApi, "Billing API", "Billing.Api"),
            C("g1", SourceContractType.GraphQlSchema, "Person schema", "Person.Api"), C("g2", SourceContractType.GraphQlOperations, "Person queries", null, "Web"),
            C("m1", SourceContractType.MessageContract, "OrderPlaced", "Orders.Api"), C("c1", SourceContractType.GeneratedClient, "BillingClient", null, "Orders.Api"),
            C("x1", SourceContractType.XmlSchema, "report"), C("p1", SourceContractType.Protobuf, "orders.proto"),
        ] } };
    }

    [Fact]
    public void Contracts_are_grouped_by_type_with_counts_and_start_collapsed()
    {
        var evidence = Contracts();
        var cut = Render<ContractsWorkspace>(p => p.Add(x => x.Evidence, evidence));
        cut.FindAll("[data-testid=sd-contracts-group]").Select(g => (g.GetAttribute("data-group"), g.GetAttribute("data-count")))
            .Should().Equal(("rest", "2"), ("graphql", "2"), ("messages", "1"), ("clients", "1"), ("other", "2"));
        cut.FindAll("[data-testid=sd-contracts-group]").Sum(g => int.Parse(g.GetAttribute("data-count")!)).Should().Be(evidence.Contracts.Contracts.Count);
        cut.FindAll("[data-testid=sd-contract]").Should().BeEmpty("rows render on demand");
        cut.FindAll("[data-testid=sd-contracts-group-toggle]").Should().OnlyContain(t => t.GetAttribute("aria-expanded") == "false");
        cut.Find("[data-group=graphql] [data-testid=sd-contracts-group-toggle]").Click();
        cut.Find("[data-group=graphql] [data-testid=sd-contracts-group-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll("[data-group=graphql] [data-testid=sd-contract]").Should().HaveCount(2);
        cut.FindAll("[data-group=graphql] thead th").Select(t => t.TextContent).Should().Equal("Contract", "Type", "Producer / owner", "Consumer / used by", "Evidence", "Parsing", "Review state", "Evidence details");
        cut.Find("[data-group=clients] [data-testid=sd-contracts-group-toggle]").Click();
        cut.FindAll("[data-group=clients] thead th").Select(t => t.TextContent).Should().Equal(["Contract", "Producer / owner", "Consumer / used by", "Evidence", "Parsing", "Review state", "Evidence details"], "a single-type group needs no Type column");
    }

    [Fact]
    public void Search_works_across_groups_and_expands_only_matching_groups()
    {
        var cut = Render<ContractsWorkspace>(p => p.Add(x => x.Evidence, Contracts()));
        cut.Find("[data-testid=sd-contracts-search]").Input("Orders");
        cut.FindAll("[data-testid=sd-contracts-group]").Select(g => g.GetAttribute("data-group")).Should().Equal("rest", "messages", "clients", "other");
        cut.FindAll("[data-testid=sd-contract]").Select(r => r.QuerySelector("th")!.ChildNodes[0].TextContent).Should().Equal("Orders API", "OrderPlaced", "BillingClient", "orders.proto");
        cut.Find("[data-group=rest] [data-testid=sd-contracts-group-count]").TextContent.Should().Be("1 of 2");
        cut.Find("[data-testid=sd-contracts-shown]").TextContent.Should().StartWith("4 of 8 shown");
    }

    [Fact]
    public void Ownership_and_usage_come_from_source_and_parsing_is_plain_language()
    {
        var cut = Render<ContractsWorkspace>(p => p.Add(x => x.Evidence, Contracts()));
        cut.Find("[data-testid=sd-contracts-search]").Input("Person");
        var consumer = cut.FindAll("[data-testid=sd-contract]").Single(r => r.TextContent.Contains("Person queries"));
        consumer.QuerySelector("[data-testid=sd-contract-role]")!.TextContent.Should().Be("Consumer");
        consumer.QuerySelector("[data-testid=sd-contract-component]")!.TextContent.Should().Contain("Web");
        consumer.QuerySelector("[data-testid=sd-contract-usage]")!.TextContent.Should().Be("Web", "a consumer hint is usage stated in source");
        consumer.QuerySelector("[data-testid=sd-contract-evidence]")!.GetAttribute("data-dimension").Should().Be("Confidence");
        consumer.QuerySelector("[data-testid=sd-contract-evidence]")!.TextContent.Should().EndWith("Detected in source", "the contract file exists; only ownership/usage may be inferred");
        consumer.QuerySelector("[data-testid=sd-contract-parser]")!.TextContent.Should().EndWith("Heuristic");
        cut.FindAll("[data-testid=sd-contracts-table] th").Select(t => t.TextContent).Should().NotContain("Read").And.NotContain("Parser coverage").And.Contain("Parsing");
        cut.Find("[data-testid=sd-contracts-parsing-help]").TextContent.Should().Contain("some constructs were interpreted heuristically");
        cut.Find("[data-testid=sd-contracts-how-toggle]").GetAttribute("aria-expanded").Should().Be("false", "parser notes are behind How contract analysis works");
        cut.Find("#sd-contracts-parser-help").TextContent.Should().Contain("not contract quality, compatibility or a pass/fail result");
        cut.FindAll("[data-testid=sd-contract-toggle]").Should().OnlyContain(b => b.TextContent.StartsWith("View evidence"));
    }

    // ── Status semantics ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_same_word_keeps_its_own_meaning_per_dimension()
    {
        var partialAnalysis = Render<SourceStatusBadge>(p => p.Add(x => x.Dimension, SourceStatusDimension.Completeness).Add(x => x.Label, "Partial")).Find("span");
        var partialParser = Render<SourceStatusBadge>(p => p.Add(x => x.Dimension, SourceStatusDimension.ParserSupport).Add(x => x.Label, "Partial")).Find("span");
        var partialObservation = Render<SourceStatusBadge>(p => p.Add(x => x.Dimension, SourceStatusDimension.Observation).Add(x => x.Label, "Partial")).Find("span");
        var needsReview = Render<SourceStatusBadge>(p => p.Add(x => x.Dimension, SourceStatusDimension.Observation).Add(x => x.Label, "Needs review")).Find("span");
        new[] { partialAnalysis, partialParser, partialObservation, needsReview }.Select(b => b.ClassName).Distinct().Should().HaveCount(4, "different meanings never share one look");
        new[] { partialAnalysis, partialParser, partialObservation, needsReview }.Should().OnlyContain(b => b.TextContent.Length > 0, "text is always shown — never colour alone");
        partialParser.GetAttribute("title").Should().Contain("Not a quality verdict");
        Render<SourceStatusBadge>(p => p.Add(x => x.Dimension, SourceStatusDimension.Confidence).Add(x => x.Label, "Strongly supported")).Find("span").GetAttribute("title").Should().Contain("several consistent source signals");
        foreach (var d in Enum.GetValues<SourceStatusDimension>()) SourceStatusPresentation.DimensionHelp(d).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Status_badges_use_the_dimension_that_matches_the_backend_concept()
    {
        var cut = Page();
        cut.Find("[data-testid=sa-summary-row][data-key=architecture] [data-testid=sa-summary-status]").GetAttribute("data-dimension").Should().Be("Completeness");
        cut.Find("[data-testid=sa-cross-domain-links-toggle]").Click();
        cut.FindAll("[data-testid=sa-cross-domain-link] .sa-status").Should().OnlyContain(b => b.GetAttribute("data-dimension") == "Confidence");
        OpenDomain(cut, "observability");
        cut.FindAll("[data-testid=obs-dimension] .sa-status").Should().OnlyContain(b => b.GetAttribute("data-dimension") == "Observation");
        OpenDomain(cut, "infrastructure");
        cut.Find("[data-testid=sd-infra-status]").GetAttribute("data-dimension").Should().Be("Completeness");
        cut.FindAll("[data-testid=sd-infra-capability] .sa-status").Should().OnlyContain(b => b.GetAttribute("data-dimension") == "ParserSupport");
    }
}
