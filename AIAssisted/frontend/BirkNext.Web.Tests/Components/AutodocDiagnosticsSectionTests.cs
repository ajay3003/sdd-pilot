using BirkNext.GeneratedDocumentation;
using BirkNext.Web.Components.GeneratedDocumentation;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Generated Documentation Health → Autodoc diagnostics: the disclaimer, actual counts, the one-click "APIs without autodoc" and "modules using
/// autodoc" answers, neutral Not configured (freshness Not applicable, health Neutral, never Missing), parse/errors, the repository level kept
/// apart, and the section inside the existing panel even when no generated documentation exists. Generic fixtures only.
/// </summary>
public sealed class AutodocDiagnosticsSectionTests : BunitContext
{
    private static readonly Guid SnapshotId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static AutodocModuleInventory Configured(string name, bool api, GeneratedDocumentationFreshnessStatus freshness = GeneratedDocumentationFreshnessStatus.Unknown,
        GeneratedDocumentationHealthStatus health = GeneratedDocumentationHealthStatus.Partial, AutodocUsage usage = AutodocUsage.Configured, int errors = 0) => new()
    {
        Name = name, Identity = "component:" + name, ProjectPath = $"{name}/src/{name}.Api/{name}.Api.csproj", ModuleType = api ? "REST API" : "Worker/service", ApiCapable = api,
        Usage = usage, UsageReason = "Module-specific generated output with a traced generator.", GeneratedOutputPresent = true, ArtifactCount = 3,
        ArtifactKinds = [GeneratedDocumentKind.Overview, GeneratedDocumentKind.ApiContract], GeneratedModuleId = "module:" + name, OutputPath = name + "/autodoc",
        Generator = "maintain-docs", WorkflowSources = [".claude/skills/maintain-docs/SKILL.md"], Freshness = freshness, Health = health, ParseErrors = errors,
    };

    private static AutodocModuleInventory NotConfigured(string name, bool api) => new()
    {
        Name = name, Identity = "component:" + name, ProjectPath = $"{name}/src/{name}.csproj", ModuleType = api ? "API" : "Library", ApiCapable = api,
        Usage = AutodocUsage.NotConfigured, UsageReason = "No module-specific generated documentation output was detected for this module.", TypeBasis = "Api component (Confirmed)",
    };

    private static AutodocDiagnostics Diagnostics() => new()
    {
        MaintainDocsWorkflowDetected = true, WorkflowSources = [".claude/skills/maintain-docs/SKILL.md"], InventoryBasis = "Source Analysis architecture components",
        DetectedModules = 5, ApiModules = 3, ModulesUsingAutodoc = 3, ModulesWithoutAutodoc = 2, ModulesPartial = 0, ApisUsingAutodoc = 2, ApisWithoutAutodoc = 1,
        GeneratedArtifacts = 9, FreshnessUnknown = 2, PotentiallyStale = 1, ParseErrors = 1, ModulesWithGeneratedOutput = 3,
        Modules =
        [
            Configured("Persons", api: true),
            Configured("Skole", api: true, GeneratedDocumentationFreshnessStatus.Stale, GeneratedDocumentationHealthStatus.Warning),
            Configured("Jobs", api: false, usage: AutodocUsage.Error, health: GeneratedDocumentationHealthStatus.Fail, errors: 1),
            NotConfigured("Mu", api: true),
            NotConfigured("Common", api: false),
        ],
        RepositoryLevelDocuments = ["autodoc/README.md"],
        Limitations = ["Not configured is neutral: no repository or project policy requiring generated documentation was found, and none is assumed."],
    };

    private IRenderedComponent<AutodocDiagnosticsSection> Section() => Render<AutodocDiagnosticsSection>(p => p
        .Add(x => x.Diagnostics, Diagnostics())
        .Add(x => x.Snapshot, new GeneratedDocumentationSnapshotBinding { SnapshotId = SnapshotId, ArchiveName = "M2LB.zip" }));

    private static string[] Rows(IRenderedComponent<AutodocDiagnosticsSection> cut) => [.. cut.FindAll("[data-testid=gdh-autodoc-row]").Select(r => r.GetAttribute("data-module")!)];

    [Fact]
    public void Shows_disclaimer_actual_counts_and_the_selected_snapshot()
    {
        var cut = Section();
        cut.Find("#gdh-autodoc-title").TextContent.Should().Be("Modules with generated documentation");
        cut.FindAll("[data-testid=gdh-autodoc-disclaimer]").Should().BeEmpty("the page carries the single disclaimer");
        cut.Find("[data-testid=gdh-overview-state]").TextContent.Should().Be("Available");
        cut.Find("[data-testid=gdh-overview-health]").TextContent.Should().Be("Error", "a parser error outranks review suggested");
        cut.Find("[data-testid=gdh-overview-modules]").TextContent.Should().Be("5 · 3 with docs");
        cut.Find("[data-testid=gdh-overview-api]").TextContent.Should().Be("3 · 2 with docs");
        cut.Find("[data-testid=gdh-overview-review]").TextContent.Should().Be("2");
        cut.Find("[data-testid=gdh-overview-errors]").TextContent.Should().Be("1");
        cut.FindAll("[data-testid=gdh-review-item]").Select(i => i.GetAttribute("data-module")).Should().Equal("Skole", "Jobs");
        cut.Find("[data-testid=gdh-advanced-body]").HasAttribute("hidden").Should().BeTrue("every metric sits behind Advanced diagnostics");
        cut.Markup.Should().NotContain("autodoc diagnostics").And.NotContain("Show APIs without autodoc");
        cut.Find("[data-testid=gdh-autodoc-workflow]").TextContent.Should().Be("Yes");
        cut.Find("[data-testid=gdh-autodoc-using]").TextContent.Should().Be("3 of 5");
        cut.Find("[data-testid=gdh-autodoc-apis-using]").TextContent.Should().Be("2 of 3");
        cut.Find("[data-testid=gdh-autodoc-apis-without]").TextContent.Should().Be("1");
        cut.Find("[data-testid=gdh-autodoc-errors]").TextContent.Should().Be("1");
        cut.Find("[data-testid=gdh-autodoc-snapshot]").TextContent.Should().Contain("M2LB.zip").And.Contain(SnapshotId.ToString()).And.Contain("Repository-level workflow ≠ module usage");
        cut.Markup.Should().NotContain("score", "counts and states only");
    }

    [Fact]
    public void Freshness_names_its_denominator_and_its_categories_add_up_to_it()
    {
        var cut = Section();
        var d = Diagnostics();
        (d.Fresh + d.PotentiallyStale + d.FreshnessUnknown + d.FreshnessNotApplicable).Should().Be(d.ModulesWithGeneratedOutput);
        cut.Find("[data-testid=gdh-autodoc-freshness-scope]").TextContent.Should()
            .Contain("Freshness covers the 3 modules with generated output (of 5 detected)").And.Contain("A timestamp alone never makes a document stale");
    }

    [Fact]
    public void One_click_lists_apis_without_autodoc_and_modules_using_autodoc()
    {
        var cut = Section();
        Rows(cut).Should().HaveCount(5);
        cut.Find("[data-testid=gdh-autodoc-apis-without-action]").TextContent.Should().Contain("(1)");
        cut.Find("[data-testid=gdh-autodoc-apis-without-action]").Click();
        Rows(cut).Should().Equal("Mu");
        cut.Find("[data-testid=gdh-autodoc-apis-without-action]").GetAttribute("aria-pressed").Should().Be("true");

        cut.Find("[data-testid=gdh-autodoc-using-action]").Click();
        Rows(cut).Should().BeEquivalentTo(["Persons", "Skole", "Jobs"]);
    }

    [Fact]
    public void Not_configured_is_neutral_with_not_applicable_freshness()
    {
        var cut = Section();
        var mu = cut.FindAll("[data-testid=gdh-autodoc-row]").Single(r => r.GetAttribute("data-module") == "Mu");
        mu.QuerySelector("[data-testid=gdh-autodoc-usage]")!.TextContent.Should().Be("Generated docs: Not configured");
        mu.QuerySelector("[data-testid=gdh-autodoc-usage]")!.ClassList.Should().Contain("sd-pill-muted", "neutral, not attention");
        mu.QuerySelector("[data-testid=gdh-autodoc-freshness]")!.TextContent.Should().Be("Not applicable");
        mu.QuerySelector("[data-testid=gdh-autodoc-health]")!.TextContent.Should().Be("Neutral");
        mu.TextContent.Should().NotContainAny("Missing", "Unhealthy", "Stale", "Error");

        mu.QuerySelector("[data-testid=gdh-autodoc-toggle]")!.Click();
        var detail = cut.Find("[data-testid=gdh-autodoc-detail]").TextContent;
        detail.Should().Contain("Not configured").And.Contain("Not detected for this module").And.Contain("Not present").And.Contain("Not applicable").And.Contain("Source analysis").And.Contain("Available");
    }

    [Fact]
    public void Filters_separate_usage_freshness_health_and_parse_errors()
    {
        var cut = Section();
        void Filter(AutodocFilter f) => cut.FindAll("[data-testid=gdh-autodoc-filter]").Single(b => b.GetAttribute("data-filter") == f.ToString()).Click();
        Filter(AutodocFilter.PotentiallyStale); Rows(cut).Should().Equal("Skole");
        Filter(AutodocFilter.UnknownFreshness); Rows(cut).Should().BeEquivalentTo(["Persons", "Jobs"], "a not-configured module never counts as unknown freshness");
        Filter(AutodocFilter.NeedsReview); Rows(cut).Should().Equal("Skole");
        Filter(AutodocFilter.ParseErrors); Rows(cut).Should().Equal("Jobs");
        Filter(AutodocFilter.MaintainDocs); Rows(cut).Should().BeEquivalentTo(["Persons", "Skole", "Jobs"]);
        Filter(AutodocFilter.WithoutAutodoc); Rows(cut).Should().BeEquivalentTo(["Mu", "Common"]);
        cut.Find("[data-testid=gdh-autodoc-scope]").Change(nameof(AutodocApiScope.NonApi));
        Rows(cut).Should().Equal("Common");
    }

    [Fact]
    public void Basic_filters_first_with_counts_and_the_rest_behind_advanced_filters()
    {
        var cut = Section();
        cut.FindAll("[data-testid=gdh-autodoc-filters] [data-testid=gdh-autodoc-filter]").Select(b => b.TextContent.Trim()).Should().Equal(
            "All (5)", "With generated docs (3)", "Without (2)", "Needs review (1)");
        cut.Find("[data-testid=gdh-advanced-filters-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("[data-testid=gdh-autodoc-apis-without-action]").TextContent.Should().Be("Show APIs without generated docs (1)");
        cut.Find("[data-testid=gdh-autodoc-apis-without-action]").Click();
        cut.Find("[data-testid=gdh-advanced-filters-toggle]").GetAttribute("aria-expanded").Should().Be("true", "the active module-type scope stays visible");
    }

    [Fact]
    public void No_generated_documentation_is_neutral_with_the_policy_explanation()
    {
        var cut = Render<AutodocDiagnosticsSection>(p => p.Add(x => x.Diagnostics, new AutodocDiagnostics
        {
            DetectedModules = 2, ApiModules = 1, ModulesWithoutAutodoc = 2, ApisWithoutAutodoc = 1, Modules = [NotConfigured("Mu", api: true), NotConfigured("Common", api: false)],
        }));
        cut.Find("[data-testid=gdh-overview-state]").TextContent.Should().Be("Not configured");
        cut.Find("[data-testid=gdh-overview-health]").TextContent.Should().Be("Neutral");
        cut.Find("[data-testid=gdh-overview-health]").ClassList.Should().Contain("sd-pill-muted");
        cut.Find("[data-testid=gdh-overview-neutral]").TextContent.Should().Contain("No project policy requiring generated documentation was detected.");
        cut.Find("[data-testid=gdh-review-none]").TextContent.Should().Be("No generated-documentation review items.");
        cut.FindAll("[data-testid=gdh-autodoc-explorer]").Should().BeEmpty("no generated output to open");
    }

    [Fact]
    public void Explorer_link_is_offered_only_for_modules_with_generated_output()
    {
        var cut = Render<AutodocDiagnosticsSection>(p => p.Add(x => x.Diagnostics, Diagnostics()).Add(x => x.ExplorerHref, "generated-documentation-explorer?import=imp-1"));
        cut.FindAll("[data-testid=gdh-autodoc-explorer]").Should().BeEmpty("the inventory has no per-row Explorer link; a module's focus offers it");
        cut.FindAll("[data-testid=gdh-autodoc-table] thead th").Select(h => h.TextContent).Should().NotContain("Action");
        string? LinkOf(string module)
        {
            cut.FindAll("[data-testid=gdh-autodoc-row]").Single(r => r.GetAttribute("data-module") == module).QuerySelector("[data-testid=gdh-autodoc-toggle]")!.Click();
            return cut.FindAll("[data-testid=gdh-autodoc-detail] [data-testid=gdh-autodoc-explorer]").SingleOrDefault()?.GetAttribute("href");
        }
        LinkOf("Persons").Should().Be("generated-documentation-explorer?import=imp-1&module=Persons");
        LinkOf("Jobs").Should().Be("generated-documentation-explorer?import=imp-1&module=Jobs");
        LinkOf("Mu").Should().BeNull("no generated output to open");
    }

    [Fact]
    public void Repository_level_documentation_is_listed_apart_from_modules()
    {
        var cut = Section();
        cut.Find("[data-testid=gdh-autodoc-repository]").TextContent.Should().Contain("not counted as module or API coverage");
        Rows(cut).Should().NotContain("autodoc");
    }

    [Fact]
    public void The_panel_shows_autodoc_diagnostics_even_when_no_generated_documentation_exists()
    {
        var run = new GeneratedDocumentationDiagnosticRun
        {
            State = GeneratedDocumentationRunState.NoGeneratedDocumentation, StateMessage = GeneratedDocumentationText.EmptyState, CompletedAt = DateTimeOffset.UtcNow,
            Snapshot = new GeneratedDocumentationSnapshotBinding { SnapshotId = SnapshotId, ArchiveName = "mu.zip", Basis = "Newest Source Analysis snapshot", IsNewest = true },
            Autodoc = new AutodocDiagnostics { DetectedModules = 1, ApiModules = 1, ModulesWithoutAutodoc = 1, ApisWithoutAutodoc = 1, InventoryBasis = "Source Analysis architecture components", Modules = [NotConfigured("Mu", api: true)] },
        };
        var cut = Render<GeneratedDocumentationHealthPanel>(p => p.Add(x => x.Api, new FakeApi(run)));
        cut.Find("[data-testid=gdh-run]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gdh-autodoc]"));
        cut.Find("[data-testid=gdh-state]").GetAttribute("data-state").Should().Be("NoGeneratedDocumentation");
        Rows(cut).Should().Equal("Mu");
    }

    private static string[] Rows(IRenderedComponent<GeneratedDocumentationHealthPanel> cut) => [.. cut.FindAll("[data-testid=gdh-autodoc-row]").Select(r => r.GetAttribute("data-module")!)];

    private sealed class FakeApi(GeneratedDocumentationDiagnosticRun run) : IGeneratedDocumentationApiService
    {
        public Task<GeneratedDocumentationDiagnosticRun?> RunAsync(GeneratedDocumentationDiagnosticRequest request, CancellationToken ct = default) =>
            Task.FromResult<GeneratedDocumentationDiagnosticRun?>(run);
    }

    [Fact]
    public void Review_items_state_their_factual_reasons_and_open_the_one_module_focus()
    {
        var diagnostics = Diagnostics() with
        {
            Modules = [.. Diagnostics().Modules.Select(m => m.Name == "Skole" ? m with { MissingExpectedDocs = 2, DiscrepancyCandidates = 1 } : m)],
        };
        var cut = Render<AutodocDiagnosticsSection>(p => p.Add(x => x.Diagnostics, diagnostics));

        var skole = cut.FindAll("[data-testid=gdh-review-item]").Single(i => i.GetAttribute("data-module") == "Skole");
        skole.TextContent.Should().Contain("2 required generated documents are missing").And.Contain("Potentially stale").And.Contain("not a confirmed defect");
        cut.FindAll("[data-testid=gdh-review-item]").Single(i => i.GetAttribute("data-module") == "Jobs").TextContent.Should().Contain("1 generated document does not parse");
        skole.QuerySelector("[data-testid=gdh-review-open]")!.Click();
        cut.FindAll("[data-testid=gdh-autodoc-detail]").Should().ContainSingle();
        cut.Find("#gdh-autodoc-focus-title").TextContent.Should().Be("Skole");
        cut.Find("[data-testid=gdh-autodoc-reasons]").TextContent.Should().Contain("2 required generated documents are missing");
        cut.Find("[data-testid=gdh-autodoc-close]").Click();
        cut.FindAll("[data-testid=gdh-autodoc-detail]").Should().BeEmpty();
    }

    [Fact]
    public void Overall_partial_names_the_modules_with_partial_output()
    {
        var diagnostics = Diagnostics() with
        {
            Modules = [.. Diagnostics().Modules.Select(m => m.Name == "Persons" ? m with { Usage = AutodocUsage.Partial } : m)],
        };
        var cut = Render<AutodocDiagnosticsSection>(p => p.Add(x => x.Diagnostics, diagnostics));
        cut.Find("[data-testid=gdh-overview-state]").TextContent.Should().Be("Partial");
        cut.Find("[data-testid=gdh-overview-state-reason]").TextContent.Should().Be("1 module has partial generated documentation (required output missing or generator not traced — see each module): Persons.");
        cut.FindAll("[data-testid=gdh-review-item]").Should().NotContain(i => i.GetAttribute("data-module") == "Persons", "Partial alone is not a review item");
        Section().Find("[data-testid=gdh-overview-state-reason]").TextContent.Should().Contain("has its declared outputs").And.Contain("says nothing about freshness");
    }

    [Fact]
    public void The_module_focus_names_the_missing_documents_and_links_each_generated_document()
    {
        var diagnostics = Diagnostics() with
        {
            Modules = [.. Diagnostics().Modules.Select(m => m.Name == "Skole"
                ? m with { MissingExpectedDocs = 1, MissingExpectedDocumentNames = ["Data flow"], ArtifactPaths = ["Skole/autodoc/README.md", "Skole/autodoc/architecture.md"] }
                : m.Name == "Persons" ? m with { Usage = AutodocUsage.Partial, UsageReason = "Generated output present; the generator/workflow for this module could not be traced." } : m)],
        };
        var cut = Render<AutodocDiagnosticsSection>(p => p.Add(x => x.Diagnostics, diagnostics).Add(x => x.ExplorerHref, "generated-documentation-explorer?import=imp-1"));

        cut.FindAll("[data-testid=gdh-autodoc-row]").Single(r => r.GetAttribute("data-module") == "Skole").QuerySelector("[data-testid=gdh-autodoc-toggle]")!.Click();
        cut.Find("[data-testid=gdh-autodoc-reasons]").TextContent.Should().Contain("1 required generated document is missing: Data flow.");
        cut.FindAll("[data-testid=gdh-autodoc-document]").Select(a => a.GetAttribute("href")).Should().Equal(
            "generated-documentation-explorer?import=imp-1&module=Skole&document=Skole%2Fautodoc%2FREADME.md",
            "generated-documentation-explorer?import=imp-1&module=Skole&document=Skole%2Fautodoc%2Farchitecture.md");

        AutodocPresentation.ReviewReasons(diagnostics.Modules.Single(m => m.Name == "Persons")).Should()
            .Equal(["Generated output present; the generator/workflow for this module could not be traced."], "the module's own reason, not a generic 'outputs incomplete'");
    }
}
