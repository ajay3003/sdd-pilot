using BirkNext.DatabaseArchitecture;
using BirkNext.GeneratedDocumentation;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using BirkNext.Technology;
using BirkNext.Web.Components;
using BirkNext.Web.Components.GeneratedDocumentation;
using BirkNext.Web.Components.SourceDomains;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ConfigurationEntry = BirkNext.SourceDomains.ConfigurationEntry;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Source Analysis describes what the source proves. Evidence state, analysis completeness, resolution, review action and runtime verification are
/// separate facts; source-level confirmation never reads as runtime confirmation; analyzer limitations and classification gaps are never
/// presented as application defects, and counts of different kinds are never summed. Generic fixtures only.
/// </summary>
public sealed class SourceAnalysisStatusSemanticsTests : BunitContext
{
    public SourceAnalysisStatusSemanticsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IReportExportService, ReportExportService>();
    }

    private static IqrSourceSnapshot Snapshot(SourceEvidenceDomainsSnapshot evidence) => new()
    {
        Id = evidence.SourceSnapshotId, IntegrationId = "source-analysis", Archive = new SourceArchive("shop.zip", evidence.SourceFingerprint, 40),
        AnalyzedAt = DateTimeOffset.UnixEpoch, Status = SourceAnalysisStatus.Ready, EvidenceDomains = evidence,
    };

    [Fact]
    public void Status_dimensions_are_separate_and_runtime_is_its_own_dimension()
    {
        Enum.GetValues<SourceStatusDimension>().Select(SourceStatusPresentation.DimensionLabel).Should().OnlyHaveUniqueItems()
            .And.Contain(["Evidence state", "Analysis completeness", "Resolution", "Review action", "Runtime verification"]);
        SourceStatusPresentation.DimensionHelp(SourceStatusDimension.Confidence).Should().Contain("does not prove absence at runtime");
        SourceStatusPresentation.DimensionHelp(SourceStatusDimension.Completeness).Should().Contain("not a failure");
        SourceStatusPresentation.Meaning(SourceStatusDimension.Confidence, "Not found in source").Should().Contain("not proof of absence at runtime");
        SourceStatusPresentation.Meaning(SourceStatusDimension.Runtime, "Not assessed here").Should().Contain("not assessed");
        SourceStatusPresentation.Tone(SourceStatusDimension.ReviewAction, "Needs confirmation").Should().NotBe("critical", "a review prompt is never styled as a defect");
    }

    [Fact]
    public void Partial_snapshot_explains_why_from_its_own_counts()
    {
        var snapshot = Snapshot(SourceEvidenceDomainsUiTests.Evidence()) with
        {
            Status = SourceAnalysisStatus.Partial,
            Limitations = ["Python source is detected but not analyzed."],
            TechnologyCoverage = new SourceTechnologyCoverage { TotalFiles = 40, AnalyzedFiles = 24, UnsupportedSourceFiles = 16 },
        };

        var coverage = SourceAnalysisReview.Coverage(snapshot);

        coverage.Status.Should().Be("Partial");
        coverage.Reasons.Should().Contain("16 source files are in a language Source Analysis detects but does not analyze")
            .And.Contain("2 contracts required heuristic parsing")
            .And.Contain("1 analyzer limitation applies to this snapshot")
            .And.Contain("Runtime and deployment state are outside Source Analysis");
        coverage.Limitations.Should().Equal("Python source is detected but not analyzed.");
        coverage.Reasons.Should().NotContain(r => r.Contains("fail", StringComparison.OrdinalIgnoreCase), "Partial is never an analysis failure");
        SourceAnalysisReview.Coverage(snapshot with { Status = SourceAnalysisStatus.Ready }).Status.Should().Be("Complete");
    }

    [Fact]
    public void Review_needed_lists_areas_and_never_sums_counts_of_different_kinds()
    {
        var evidence = SourceEvidenceDomainsUiTests.Evidence();
        var withUnknown = evidence with
        {
            Configuration = evidence.Configuration with
            {
                Entries = [.. evidence.Configuration.Entries, new ConfigurationEntry { Id = "u1", Key = "Feature:Mode", NormalizedKey = "feature:mode", Category = ConfigurationCategory.Unknown, File = "src/Api/appsettings.json", Line = 9 }],
            },
        };
        var snapshot = Snapshot(withUnknown);

        var areas = SourceAnalysisReview.ReviewAreas(snapshot);

        areas.Select(a => a.Area).Should().Equal("Configuration", "Contracts");
        areas.Single(a => a.Area == "Configuration").Items.Select(i => i.Text).Should().Equal("1 entry needs classification", "1 potential configuration conflict");
        areas.Single(a => a.Area == "Contracts").Items.Single().Text.Should().Be("2 heuristic parses need confirmation");
        SourceAnalysisReview.Attention(snapshot).Select(i => i.Text).Should().NotContain(t => t.Contains("uncategorized") || t.Contains("attention"));
    }

    [Fact]
    public void Database_and_relationship_evidence_is_scoped_to_source()
    {
        DatabaseEvidenceSummary.StateLabel(DatabaseEvidenceState.Confirmed).Should().Be("Confirmed in source");
        DatabaseEvidenceSummary.StateLabel(DatabaseEvidenceState.Inferred).Should().Be("Inferred");
        DatabaseEvidenceSummary.StateLabel(DatabaseEvidenceState.ObservedFromMigration).Should().Be("Observed from migration");
        ArchitecturePresentation.StateLabel(ArchitectureEvidenceState.Confirmed).Should().Be("Confirmed in source");

        var direct = new ArchitectureDependency { Id = "1", FromComponentId = "api", ToId = "db", DependencyType = ArchitectureDependencyType.DatabaseReadWrite, EvidenceState = ArchitectureEvidenceState.Confirmed };
        var inferred = direct with { Id = "2", EvidenceState = ArchitectureEvidenceState.Inferred };
        var missing = direct with { Id = "3", ToId = null, TargetReference = "HttpClient 'Billing'", DependencyType = ArchitectureDependencyType.Http, EvidenceState = ArchitectureEvidenceState.Unresolved };
        ArchitectureReview.StateText(direct).Should().Be("Confirmed in source");
        ArchitectureReview.StateText(inferred).Should().Be("Inferred");
        ArchitectureReview.StateText(missing).Should().Be("Relationship detected · target unresolved");
        ArchitectureReview.ReasonCategory(direct).Should().BeNull("a resolved target needs no reason");
    }

    [Fact]
    public void Unresolved_dependencies_get_distinct_reason_categories_from_their_own_fields()
    {
        ArchitectureDependency Dep(ArchitectureDependencyType type, params ArchitectureEvidenceKind[] kinds) => new()
        {
            Id = type.ToString(), FromComponentId = "worker", DependencyType = type, EvidenceState = ArchitectureEvidenceState.Unresolved,
            Evidence = [.. kinds.Select(k => new ArchitectureEvidence(k, "src/Program.cs", 1, "s", "x", "e"))],
        };

        ArchitectureReview.ReasonCategory(Dep(ArchitectureDependencyType.DatabaseReadWrite, ArchitectureEvidenceKind.ApplicationSource)).Should().Be(ArchitectureReview.ReasonTarget);
        ArchitectureReview.ReasonCategory(Dep(ArchitectureDependencyType.ServiceBusPublish, ArchitectureEvidenceKind.ApplicationSource)).Should().Be(ArchitectureReview.ReasonChannel);
        ArchitectureReview.ReasonCategory(Dep(ArchitectureDependencyType.Http, ArchitectureEvidenceKind.ApplicationSource)).Should().Be(ArchitectureReview.ReasonBaseUrl);
        ArchitectureReview.ReasonCategory(Dep(ArchitectureDependencyType.Http, ArchitectureEvidenceKind.DevelopmentOrchestration)).Should().Be(ArchitectureReview.ReasonLocalOrchestration);
        ArchitectureReview.ReasonCategory(Dep(ArchitectureDependencyType.Http, ArchitectureEvidenceKind.ContainerCompose)).Should().Be(ArchitectureReview.ReasonCompose);
        ArchitectureReview.ReasonCategory(Dep(ArchitectureDependencyType.Http, ArchitectureEvidenceKind.ApplicationSource) with { ConflictingTargets = ["a", "b"] }).Should().Be(ArchitectureReview.ReasonConflict);

        // An Unresolved-state dependency may point at an unresolved channel node: it still gets a reason (every unresolved target is explained).
        ArchitectureReview.ReasonCategory(Dep(ArchitectureDependencyType.ServiceBusPublish, ArchitectureEvidenceKind.ApplicationSource) with { ToId = "channel:unnamed" })
            .Should().Be(ArchitectureReview.ReasonChannel);

        ArchitectureReview.CauseGroup("Unresolved target").Should().Be(ArchitectureReview.ResolutionLimitations, "an unresolved target is an analyzer resolution limit, not missing evidence");
        ArchitectureReview.CauseGroup("Producer not found").Should().Be(ArchitectureReview.ResolutionLimitations);
        ArchitectureReview.CauseGroup("Consumer not found").Should().Be(ArchitectureReview.ResolutionLimitations);
        ArchitectureReview.CauseGroup("Channel name unresolved").Should().Be(ArchitectureReview.ResolutionLimitations);
        ArchitectureReview.CauseGroup("Conflict").Should().Be(ArchitectureReview.DeterministicConflicts, "only disagreeing evidence is a deterministic conflict");
        ArchitectureReview.CauseGroup("Deployment not assessed").Should().Be(ArchitectureReview.ScopeLimitations);
        ArchitectureReview.CauseGroup("Local orchestration only").Should().Be(ArchitectureReview.ScopeLimitations);
        ArchitectureReview.CauseGroup("Compose evidence").Should().Be("Other analyzer evidence");
        ArchitectureReview.CauseGroups[0].Should().Be(ArchitectureReview.DeterministicConflicts, "conflicts are the only group needing an evidence decision");
    }

    [Fact]
    public void Architecture_status_separates_partial_evidence_from_deterministic_conflict()
    {
        var partial = ArchitectureWorkspaceTests.Model() with { Status = ArchitectureStatus.NeedsReview };
        ArchitectureReview.StatusText(partial).Should().Be("Partial evidence", "an unresolved HttpClient base URL is partial evidence, not a defect");
        ArchitectureReview.Conflicts(partial).Should().Be(0);
        ArchitectureReview.StatusReason(partial).Should().Contain("could not be resolved from source").And.NotContain("conflict");

        var conflict = partial with { Dependencies = [.. partial.Dependencies, new() { Id = "d-c", FromComponentId = "component:Storefront", DependencyType = ArchitectureDependencyType.Http,
            EvidenceState = ArchitectureEvidenceState.Conflict, ConflictingTargets = ["component:Catalog.Api", "component:Search.Worker"] }] };
        ArchitectureReview.StatusText(conflict).Should().Be("Evidence review · deterministic conflict");
        ArchitectureReview.StatusReason(conflict).Should().Contain("1 deterministic conflict");
        ArchitectureReview.StatusText(partial with { Status = ArchitectureStatus.Complete }).Should().Be("Complete");
        ArchitectureReview.StatusText(partial with { Status = ArchitectureStatus.Unsupported }).Should().Be("Not assessed");
    }

    [Fact]
    public void Unresolved_http_base_url_keeps_its_reason_and_evidence_strength_is_preserved()
    {
        var model = ArchitectureWorkspaceTests.Model();
        var unresolved = model.Dependencies.Single(d => d.Id == "d-unres");
        ArchitectureReview.UnresolvedReason(unresolved).Should().NotBeNullOrEmpty().And.NotContainEquivalentOf("missing");
        ArchitectureEvidenceSummary.Dependencies(model).Unresolved.Should().Be(1);
        model.Dependencies.Select(d => ArchitecturePresentation.StateLabel(d.EvidenceState)).Should().Contain(["Confirmed in source", "Strongly supported", "Inferred"],
            "Confirmed / Strongly supported / Inferred are separate evidence strengths, never collapsed");
    }

    [Fact]
    public void Dependencies_table_shows_detected_relationship_reason_column_and_reason_filter()
    {
        var model = ArchitectureWorkspaceTests.Model();
        var cut = Render<ArchitectureWorkspace>(p => p.Add(x => x.Snapshot, model).Add(x => x.InitialTab, "Dependencies"));
        cut.Find("[data-testid=arch-dependencies] thead").TextContent.Should().Contain("Reason");
        var unresolved = model.Dependencies.Where(d => d.ToId is null).ToList();
        if (unresolved.Count == 0) return;
        cut.FindAll("[data-testid=arch-dep-state-text]").Select(e => e.TextContent).Should().Contain("Relationship detected · target unresolved");
        cut.Find("[data-testid=arch-dep-reason]").Should().NotBeNull("unresolved targets can be filtered by reason");
        cut.Find("[data-testid=arch-dep-reason-summary]").TextContent.Should().Contain("The dependency itself was detected in source");
    }

    [Fact]
    public void Architecture_summary_says_linked_and_targets_unresolved_and_keeps_technologies_compact()
    {
        var cut = Render<ArchitectureWorkspace>(p => p.Add(x => x.Snapshot, ArchitectureWorkspaceTests.Model()));
        var summary = cut.Find("[data-testid=arch-dependency-summary] dd").TextContent;
        summary.Should().Contain("linked in source").And.Contain("unresolved").And.NotContain("confirmed ·");
        cut.Find("[data-testid=arch-technologies]").TextContent.Should().StartWith("Technologies detected:");
        cut.FindAll("[data-testid=arch-tech]").Should().NotBeEmpty("the full inventory stays reachable behind View all technologies");
    }

    [Fact]
    public void Configuration_frames_unknown_keys_as_needing_classification_and_source_environment_labels()
    {
        var evidence = SourceEvidenceDomainsUiTests.Evidence();
        evidence = evidence with
        {
            Configuration = evidence.Configuration with
            {
                Status = SourceDomainStatus.Partial,
                Environments = [SourceEnvironmentLabel.Default, new(SourceEnvironmentKind.Development, "Development"), new(SourceEnvironmentKind.QA, "Qa")],
                Entries = [.. evidence.Configuration.Entries, new ConfigurationEntry { Id = "u1", Key = "Feature:Mode", NormalizedKey = "feature:mode", Category = ConfigurationCategory.Unknown, File = "src/Api/appsettings.Qa.json", Line = 9 }],
            },
        };
        var cut = Render<ConfigurationWorkspace>(p => p.Add(x => x.Evidence, evidence));

        cut.Find("[data-testid=sd-config-reason]").TextContent.Should().Be("1 entry needs classification");
        var overview = cut.Find("[data-testid=sd-config-overview]").TextContent;
        overview.Should().Contain("Needs classification").And.Contain("Source environment labels").And.NotContain("Needs attention");
        cut.Find("[data-testid=sd-config-labels]").TextContent.Should().Contain("Development").And.Contain("Qa").And.Contain("not verified Target Environments");
        cut.Find("[data-testid=sd-config-source-note]").TextContent.Should().Be("Values are as written in source files. Deployed settings are not verified.");

        cut.FindAll(".sa-subnav-item").Single(b => b.TextContent == "Entries").Click();
        cut.Find("[data-testid=sd-config-entry-table] thead").TextContent.Should().Contain("Source environment label");
        cut.FindAll("[data-testid=sd-config-entry]").Select(r => r.TextContent).Should().Contain(t => t.Contains("Feature:Mode") && t.Contains("Needs classification"));
        cut.Find("[data-testid=sd-config-more-filters]").Should().NotBeNull("technology, file, sensitivity and component filters sit under More filters");
    }

    [Fact]
    public void Unclassified_pipeline_steps_are_a_classification_gap_and_trigger_is_its_own_column()
    {
        var evidence = SourceEvidenceDomainsUiTests.Evidence();
        var pipeline = evidence.CiCd.Pipelines.First(p => !p.IsTemplate);
        var other = evidence.CiCd with { Pipelines = [pipeline with { Steps = [.. pipeline.Steps, new PipelineStep { Id = "x#1", Name = "Custom script", Kind = PipelineStepKind.Other }] }] };
        SourceAnalysisReview.CiCdReason(other).Should().Be("1 pipeline step needs classification");

        var cut = Render<PipelineWorkspace>(p => p.Add(x => x.Evidence, evidence with { CiCd = other }));
        cut.Markup.Should().NotContain("Failed").And.NotContain("Broken");
        cut.Find("[data-testid=sd-cicd-unclassified]").TextContent.Should().Contain("needs classification");
        cut.Find("[data-testid=sd-cicd-pipeline-table] thead").TextContent.Should().Contain("Definition").And.Contain("Trigger").And.NotContain("Review state");
        cut.FindAll("[data-testid=sd-cicd-review-state]").Select(c => c.TextContent).Should().OnlyContain(t => t == "Detected in source" || t == "Template");
    }

    [Fact]
    public void Contract_existence_ownership_usage_parsing_and_review_are_separate()
    {
        var cut = Render<ContractsWorkspace>(p => p.Add(x => x.Evidence, SourceEvidenceDomainsUiTests.Evidence()));
        foreach (var group in cut.FindAll("[data-testid=sd-contracts-group]").Select(g => g.GetAttribute("data-group")).ToList())
            cut.Find($"[data-testid=sd-contracts-group][data-group={group}] [data-testid=sd-contracts-group-toggle]").Click();
        var api = cut.FindAll("[data-testid=sd-contract]").Single(r => r.TextContent.Contains("Shop API"));
        api.QuerySelector("[data-testid=sd-contract-evidence]")!.TextContent.Should().EndWith("Detected in source", "the contract file exists — the contract is not Inferred");
        api.QuerySelector("[data-testid=sd-contract-owner-evidence]")!.TextContent.Should().EndWith("Inferred", "only the ownership is inferred");
        api.QuerySelector("[data-testid=sd-contract-parser]")!.TextContent.Should().EndWith("Heuristic");
        api.QuerySelector("[data-testid=sd-contract-review-state]")!.TextContent.Should().Be("Needs confirmation");
        api.TextContent.Should().NotContain("Invalid").And.NotContain("Broken").And.NotContain("Failed");

        var message = cut.FindAll("[data-testid=sd-contract]").Single(r => r.TextContent.Contains("OrderPlaced"));
        message.QuerySelector("[data-testid=sd-contract-usage]")!.TextContent.Should().Be("Not stated in source", "absence of stated usage is not proof of no consumer");
        message.QuerySelector("[data-testid=sd-contract-owner-evidence]").Should().BeNull("no producer is stated, so no ownership evidence badge");
        cut.Find("[data-testid=sd-contracts-parsing]").TextContent.Should().Contain("Heuristic 2").And.NotContain("Partial");
    }

    [Fact]
    public void Observability_shows_one_summary_and_one_area_list()
    {
        var cut = Render<ObservabilityWorkspace>(p => p.Add(x => x.Snapshot, ObservabilityWorkspaceTests.Fixture()));
        cut.FindAll("[data-testid=obs-evidence-summary]").Should().ContainSingle();
        cut.FindAll("[data-testid=obs-group-headline]").Should().BeEmpty("area states are not repeated in a second card set");
        cut.FindAll("[data-testid=obs-dimension-group]").Select(g => g.GetAttribute("data-group")).Should().OnlyHaveUniqueItems();
        cut.Markup.Should().NotContain("Needs attention");
        cut.Find("[data-testid=obs-not-found-help]").TextContent.Should().Contain("does not mean the capability is absent at runtime");
        var row = cut.FindAll("[data-testid=obs-component-row]").First();
        row.TextContent.Should().Contain("Not assessed in Source Analysis");
        row.QuerySelector("[data-testid=obs-component-evidence-toggle]")!.Click();
        cut.Find("[data-testid=obs-component-evidence]").TextContent.Should().Contain("Logging frameworks");
    }

    [Fact]
    public void Infrastructure_without_iac_is_a_neutral_source_coverage_state()
    {
        var evidence = SourceEvidenceDomainsUiTests.Evidence(infrastructure: false);
        var cut = Render<InfrastructureWorkspace>(p => p.Add(x => x.Evidence, evidence));
        var text = cut.Find("[data-testid=sd-infra-not-assessed]").TextContent;
        text.Should().Contain("No infrastructure evidence detected in this snapshot").And.Contain("may exist in another source package or repository");
        cut.FindAll("[data-testid=sd-infra-unresolved]").Should().BeEmpty("no fake issue count");
        cut.Find("[data-testid=sd-infra-coverage-link]").TextContent.Should().Be("Check infrastructure source coverage");
    }

    [Fact]
    public void Configuration_without_an_earlier_analysis_shows_one_line_and_no_table()
    {
        var cut = Render<ConfigurationWorkspace>(p => p.Add(x => x.Evidence, SourceEvidenceDomainsUiTests.Evidence()));
        cut.FindAll(".sa-subnav-item").Single(b => b.TextContent == "Changes").Click();
        cut.Find("[data-testid=sd-config-changes-empty]").TextContent.Should().Be("No earlier configuration analysis is available for comparison.");
        cut.FindAll("[data-testid=sd-config-changes] table").Should().BeEmpty();
    }

    [Fact]
    public void Data_changes_are_source_declaration_changes_summary_first()
    {
        static TableModel Table(string id) => new() { Id = id, LogicalName = id, Columns = [new() { Name = "Id", IsPrimaryKey = true }] };
        var previous = new DatabaseArchitectureSnapshot { SourceFingerprint = "a", Status = DatabaseAnalysisStatus.Complete, Databases = [new() { Id = "db", LogicalName = "Shop", Schemas = [new() { Name = "dbo", Tables = [Table("Customer")] }] }] };
        var current = new DatabaseArchitectureSnapshot { SourceFingerprint = "b", Status = DatabaseAnalysisStatus.Complete, Databases = [new() { Id = "db", LogicalName = "Shop", Schemas = [new() { Name = "dbo", Tables = [Table("Order")] }] }] };
        var cut = Render<DatabaseWorkspace>(p => p.Add(x => x.Snapshot, current).Add(x => x.Previous, previous).Add(x => x.InitialTab, "Changes"));

        cut.Markup.Should().Contain("Source declaration changes").And.NotContain("deployed schema drift");
        cut.Find("[data-testid=db-change-label]").TextContent.Should().Contain("does not prove migration deployment, live database drift or runtime database changes");
        cut.Find("[data-testid=db-change-summary]").TextContent.Should().Contain("Tables");
        cut.Find("[data-testid=db-change-details-toggle]").GetAttribute("aria-expanded").Should().Be("false", "the detail list is one disclosure away");
    }

    [Fact]
    public void Large_data_diagram_opens_on_connected_tables_with_a_way_to_show_all()
    {
        JSInterop.SetupModule("./js/databaseDiagram.js").Mode = JSRuntimeMode.Loose;
        var tables = Enumerable.Range(0, 100).Select(i => new TableModel
        {
            Id = $"t{i}", LogicalName = $"Table{i:00}", Columns = [new() { Name = "Id", IsPrimaryKey = true }],
            Relationships = i is > 0 and < 20 ? [new() { Id = $"r{i}", FromTable = $"t{i}", ToTable = "t0", FromColumns = ["Id"], ToColumns = ["Id"], Cardinality = "N:1", EvidenceState = DatabaseEvidenceState.Confirmed }] : [],
        }).ToList();
        var model = new DatabaseArchitectureSnapshot { SourceFingerprint = "f", Status = DatabaseAnalysisStatus.Complete, Databases = [new() { Id = "db", LogicalName = "Shop", Schemas = [new() { Name = "dbo", Tables = tables }] }] };

        var cut = Render<DatabaseWorkspace>(p => p.Add(x => x.Snapshot, model).Add(x => x.InitialTab, "Diagram"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=db-diagram-scope]").TextContent.Should().Contain("Showing 20 tables of 100").And.Contain("connected tables only"));
        cut.Find("[data-testid=db-connected-only]").HasAttribute("checked").Should().BeTrue();
        cut.Find("[data-testid=db-show-all]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=db-diagram-scope]").TextContent.Should().Contain("Showing 100 tables of 100"));
        cut.FindAll("[data-testid=db-show-all]").Should().BeEmpty();
    }

    [Fact]
    public void Generated_documentation_differences_are_review_candidates()
    {
        var generated = new GeneratedDocumentationSnapshot
        {
            Documents = [new GeneratedDocumentationEvidence { EvidenceId = "d", Origin = DocumentationOrigin.Generated }],
            Modules = [new GeneratedDocumentationModule { ModuleId = "m", GeneratorIds = ["w"] }],
            Drift = [new CrossArtifactDriftCandidate { Id = "x", DriftType = CrossArtifactDriftType.DocumentationDrift, ModuleId = "m" }],
        };
        var cut = Render<GeneratedDocumentationSourceSummary>(p => p.Add(x => x.Snapshot, new IqrSourceSnapshot { GeneratedDocumentation = generated }));
        cut.Find("[data-testid=sa-generated-docs-differences]").TextContent.Should().Contain("Candidate differences").And.Contain("1").And.Contain("need review");
        cut.Markup.Should().NotContain("discrepancies").And.Contain("not confirmed defects");
    }
}
