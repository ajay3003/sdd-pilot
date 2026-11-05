using BirkNext.GitSources;
using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SourceManagementPage = BirkNext.Web.Pages.SourceManagement;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// NEXT46 Source Management: summary metrics count what the list shows (applications distinct), analysis is per-dimension chips, the list
/// filters and sorts with review-required first, loading is never "No sources", a failed repository read is an error with Retry, and the
/// inspector's tabs work by keyboard.
/// </summary>
public sealed class SourceManagementRedesignTests : BunitContext
{
    private static SourceRowFacts Row(string name, string origin = "ZIP", SourceAnalysisBucket bucket = SourceAnalysisBucket.Analyzed, bool review = false,
        string[]? capabilities = null, string[]? apps = null, int day = 1)
    {
        IReadOnlyList<SourceAnalysisChip> analysis = bucket switch
        {
            SourceAnalysisBucket.Analyzed => [new("Configuration", "Complete")],
            SourceAnalysisBucket.Partial => [new("Configuration", "Complete"), new("CI/CD", "Partial")],
            _ => [],
        };
        return new(name, name, origin, bucket != SourceAnalysisBucket.NotImported, capabilities ?? ["Documentation"], analysis, bucket, null, review, review ? "No role" : null, [],
            [.. (apps ?? []).Select(a => (a, "Development"))], new DateTimeOffset(2026, 11, day, 12, 0, 0, TimeSpan.Zero), null, null);
    }

    [Fact]
    public void Analysis_bucket_is_the_shared_registry_rule_over_the_areas_the_snapshot_contains()
    {
        // The Terraform-BasicOpsInfra_M2LBM01 snapshot of the M2LB copy: the Snapshot Registry says Partial, so Source Management must too.
        IReadOnlyList<SourceSetupArea> terraform = [new("Application code", "NotDetected"), new("Architecture", "Unsupported"), new("Infrastructure as Code", "Complete"),
            new("Configuration", "Complete"), new("CI/CD", "Partial"), new("Contracts", "NotDetected")];
        SourceManagementPresentation.Bucket(true, terraform).Should().Be(SourceAnalysisBucket.Partial);
        SourceManagementPresentation.Bucket(true, terraform).Should().Be(SourceSetupOutcome.Overall(terraform) == BirkNext.Snapshots.SnapshotProcessingStatus.Partial
            ? SourceAnalysisBucket.Partial : SourceAnalysisBucket.Analyzed, "one definition, never a page-local one");
        SourceManagementPresentation.Chips(terraform).Select(c => c.Dimension).Should().Equal("Architecture", "Infrastructure as Code", "Configuration", "CI/CD");

        SourceManagementPresentation.Bucket(false, null).Should().Be(SourceAnalysisBucket.NotImported);
        SourceManagementPresentation.Bucket(true, null).Should().Be(SourceAnalysisBucket.NotAnalyzed);
        SourceManagementPresentation.Bucket(true, [new("Configuration", "Complete")]).Should().Be(SourceAnalysisBucket.Analyzed);
        SourceManagementPresentation.Bucket(true, [new("Application code", "Failed"), new("Architecture", "Unsupported")]).Should().Be(SourceAnalysisBucket.Failed);
        SourceManagementPresentation.Bucket(true, [new("Application code", "NotDetected")]).Should().Be(SourceAnalysisBucket.NothingAnalyzable);
        new SourceAnalysisChip("CI/CD", "Partial").StatusText.Should().Be("Partial", "the state is in the text, not only the colour");
        new SourceAnalysisChip("Architecture", "Unsupported").Meaning.Should().Contain("BirkNext limitation");
    }

    [Fact]
    public void The_summary_counts_sources_by_origin_and_applications_distinctly()
    {
        var rows = new[]
        {
            Row("M2LB", apps: ["M2LB"]), Row("M2LB-language", apps: ["M2LB"]), Row("Infra", "Git", SourceAnalysisBucket.Partial, review: true, apps: ["M2LB", "Skole"]),
            Row("New", "Git", SourceAnalysisBucket.NotImported),
        };
        SourceManagementPresentation.Summary(rows).Should().Be(new SourceSummary(4, 2, 2, 1, 2, 1, 0, 1, 2));
    }

    [Fact]
    public void Filters_combine_and_review_required_sources_come_first()
    {
        var rows = new[]
        {
            Row("Alpha", day: 3), Row("Beta", "Git", SourceAnalysisBucket.Partial, capabilities: ["Infrastructure"], day: 1),
            Row("Gamma", review: true, day: 2), Row("Delta", "Git", SourceAnalysisBucket.NotImported, capabilities: [], day: 4),
        };
        SourceManagementPresentation.Apply(rows, new()).Select(r => r.Name).Should().Equal("Gamma", "Alpha", "Beta", "Delta");
        SourceManagementPresentation.Apply(rows, new(Sort: "updated")).Select(r => r.Name).Should().Equal("Gamma", "Delta", "Alpha", "Beta");
        SourceManagementPresentation.Apply(rows, new(Origin: "Git")).Select(r => r.Name).Should().Equal("Beta", "Delta");
        SourceManagementPresentation.Apply(rows, new(Search: "ET")).Select(r => r.Name).Should().Equal("Beta");
        SourceManagementPresentation.Apply(rows, new(Capability: "Infrastructure")).Select(r => r.Name).Should().Equal("Beta");
        SourceManagementPresentation.Apply(rows, new(Analysis: nameof(SourceAnalysisBucket.NotAnalyzed))).Select(r => r.Name).Should().ContainSingle().Which.Should().Be("Delta", "not imported is not analyzed");
        SourceManagementPresentation.Apply(rows, new(ReviewOnly: true)).Select(r => r.Name).Should().Equal("Gamma");
        new SourceListFilter(Sort: "updated").IsActive.Should().BeFalse("sorting is not filtering");
        SourceManagementPresentation.Updated(null).Should().Be("Not imported yet");
    }

    // ── Page ──────────────────────────────────────────────────────────────────────────────────

    private readonly Mock<IGitSourcesApiService> _git = new();

    private void Register()
    {
        var repository = new WorkspaceArtifactRepository();
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev" } });
        Services.AddSingleton(_git.Object);
        Services.AddSingleton(repository);
        Services.AddSingleton(new ProjectImportActivation(repository));
        Services.AddSingleton(context.Object);
        Services.AddSingleton(Mock.Of<IProjectImportApiService>());
        Services.AddSingleton(Mock.Of<IIntegrationJourneysApiService>());
        Services.AddSingleton(Mock.Of<IWorkspaceAutoSaveService>());
        Services.AddSingleton(Mock.Of<ITechnologyCoverageApiService>());
        Services.AddSingleton(WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddScoped<ProjectApplicabilityState>();
        Services.AddScoped<BirkNext.Web.Services.Operations.BackgroundOperationService>(_ => new BirkNext.Web.Services.Operations.BackgroundOperationService());
        Services.AddScoped<ProjectImportSession>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static GitSourceLink Link(string id, string name) => new()
    {
        Id = id, OrganizationUrl = "https://dev.azure.com/bufdir", Organization = "bufdir", ProjectId = "p1", Project = "M2LB", RepositoryId = id, Repository = name,
        Branch = "main", Roles = [], EnvironmentId = "dev", BirkNextProject = "M2LB",
    };

    [Fact]
    public void While_sources_load_the_page_says_loading_never_no_sources()
    {
        var pending = new TaskCompletionSource<(IReadOnlyList<GitSourceLink>, string?)>();
        _git.Setup(g => g.LinksAsync("dev", It.IsAny<CancellationToken>())).Returns(pending.Task);
        Register();

        var cut = Render<SourceManagementPage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=sm-loading]"));
        cut.FindAll("[data-testid=sm-empty]").Should().BeEmpty("'No sources yet' is a result, not a loading state");
        pending.SetResult(([], null));
        cut.WaitForAssertion(() => cut.Find("[data-testid=sm-empty]"));
        cut.FindAll("[data-testid=sm-loading]").Should().BeEmpty();
    }

    [Fact]
    public void A_failed_repository_read_is_an_error_with_retry()
    {
        _git.SetupSequence(g => g.LinksAsync("dev", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<GitSourceLink>)[], "Azure DevOps sign-in is required."))
            .ReturnsAsync(((IReadOnlyList<GitSourceLink>)[Link("r1", "M2LB.Common")], (string?)null));
        Register();

        var cut = Render<SourceManagementPage>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=sm-links-error-message]").TextContent.Should().Be("Azure DevOps sign-in is required."));
        cut.Find("[data-testid=sm-links-error-retry]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sm-row]"));
        cut.FindAll("[data-testid=sm-links-error]").Should().BeEmpty();
    }

    [Fact]
    public void The_toolbar_filters_the_list_and_tabs_move_by_keyboard()
    {
        _git.Setup(g => g.LinksAsync("dev", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<GitSourceLink>)[Link("r1", "M2LB.Common"), Link("r2", "M2LB-language")], (string?)null));
        Register();
        var cut = Render<SourceManagementPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sm-row]").Should().HaveCount(2));

        cut.Find("[data-testid=sm-search]").Input("language");
        cut.FindAll("[data-testid=sm-row]").Select(r => r.GetAttribute("data-key")).Should().Equal("r2");
        cut.Find("[data-testid=sm-count]").TextContent.Should().Be("1 of 2 shown");
        cut.Find("[data-testid=sm-search]").Input("nothing-matches");
        cut.Find("[data-testid=sm-no-match]");
        cut.Find("[data-testid=sm-clear-filters]").Click();
        cut.FindAll("[data-testid=sm-row]").Should().HaveCount(2);

        cut.Find("[data-testid=sm-summary-not-analyzed]").Click();
        cut.FindAll("[data-testid=sm-row]").Should().HaveCount(2, "linked but never imported is not analyzed");
        cut.Find("[data-testid=sm-filter-analysis]").GetAttribute("value").Should().Be(nameof(SourceAnalysisBucket.NotAnalyzed));

        cut.Find("[data-testid=sm-open]").Click();
        cut.Find("[data-testid=sm-tab][data-tab=overview]").GetAttribute("tabindex").Should().Be("0");
        cut.Find("[data-testid=sm-tab][data-tab=overview]").KeyDown("ArrowRight");
        cut.Find("[data-testid=sm-tabpanel]").GetAttribute("data-tab").Should().Be("roles");
        cut.Find("[data-testid=sm-effective-role]").TextContent.Should().Contain("None — assign a role");
        cut.Find("[data-testid=sm-tab][data-tab=roles]").KeyDown("End");
        cut.Find("[data-testid=sm-tabpanel]").GetAttribute("data-tab").Should().Be("history");
        cut.Find("[data-testid=sm-tab][data-tab=history]").KeyDown("Home");
        cut.Find("[data-testid=sm-tabpanel]").GetAttribute("data-tab").Should().Be("overview");
        cut.Find("[data-testid=sm-close]").Click();
        cut.FindAll("[data-testid=sm-detail]").Should().BeEmpty();
    }
}
