using BirkNext.GitSources;
using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SourceManagementPage = BirkNext.Web.Pages.SourceManagement;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Source Management UX consolidation (2026-11-19): the open source and the filters live in the address (Back, reload and deep links restore
/// them; an unknown source is said, never guessed); every target application that binds a source is listed with the evidence roles confirmed
/// there and its own Project Evidence link; detected capabilities are never shown as roles.
/// </summary>
public sealed class SourceManagementConsolidationTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly FrontendAnalysisSettings _settings = new();

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public SourceManagementConsolidationTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Development" } });
        var git = new Mock<IGitSourcesApiService>();
        git.Setup(g => g.LinksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitSourceLink>)[], (string?)null));
        var settings = new Mock<IFrontendAnalysisSettingsService>();
        settings.SetupGet(s => s.Settings).Returns(_settings);
        Services.AddSingleton(git.Object);
        Services.AddSingleton(_repository);
        Services.AddSingleton(new ProjectImportActivation(_repository));
        Services.AddSingleton(context.Object);
        Services.AddSingleton(Mock.Of<IProjectImportApiService>());
        Services.AddSingleton(Mock.Of<IIntegrationJourneysApiService>());
        Services.AddSingleton(Mock.Of<IWorkspaceAutoSaveService>());
        Services.AddSingleton(Mock.Of<ITechnologyCoverageApiService>());
        Services.AddSingleton(WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);
        Services.AddSingleton(settings.Object);
        Services.AddScoped<ProjectApplicabilityState>();
        Services.AddScoped<BirkNext.Web.Services.Operations.BackgroundOperationService>(_ => new BirkNext.Web.Services.Operations.BackgroundOperationService());
        Services.AddScoped<ProjectImportSession>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    private void Import(string id, string name, params string[] confirmedRoles) =>
        _repository.SddLifecycle.ProjectImports.Add(new SddProjectImportRecord
        {
            ImportId = id, ProjectName = name, ArchiveFileName = name + ".zip", ConfirmedRoles = [.. confirmedRoles], ImportedAt = DateTimeOffset.UtcNow,
        });

    private void Bind(string profileId, string environment, string application, string importId, params string[] roleLabels) =>
        _settings.Profiles.Add(new FrontendAnalysisProfile
        {
            Id = profileId, Name = environment,
            Project = new EnvironmentProject
            {
                ProjectName = application,
                Evidence = [.. roleLabels.Select(label => new EnvironmentProjectEvidenceBinding
                    { RoleKey = label.ToLowerInvariant(), Label = label, Sources = [new ProjectEvidenceSourceReference { ImportId = importId }] })],
            },
        });

    [Fact]
    public void The_address_carries_only_what_differs_from_the_default_view()
    {
        SourceManagementPage.Address(new SourceListFilter(), null).Should().Be("sources");
        SourceManagementPage.Address(new SourceListFilter(Search: "m2 lb", Capability: "Contracts", ReviewOnly: true, Sort: "updated"), "imp|1")
            .Should().Be("sources?source=imp%7C1&q=m2%20lb&cap=Contracts&review=1&sort=updated");
    }

    [Fact]
    public void A_deep_link_opens_the_source_and_filtering_keeps_the_address_in_step()
    {
        Import("imp-a", "Alpha");
        Import("imp-b", "Beta");
        Nav.NavigateTo("sources?source=imp-b");
        var cut = Render<SourceManagementPage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sm-detail]").GetAttribute("data-key").Should().Be("imp-b"));

        cut.Find("[data-testid=sm-search]").Input("alp");
        cut.WaitForAssertion(() => Nav.ToBaseRelativePath(Nav.Uri).Should().Be("sources?source=imp-b&q=alp"));
        cut.Find("[data-testid=sm-detail]").GetAttribute("data-key").Should().Be("imp-b", "a filter never silently drops the open source");
        cut.Find("[data-testid=sm-count]").TextContent.Should().Be("1 of 2 shown");
    }

    [Fact]
    public void Returning_with_filters_restores_them()
    {
        Import("imp-a", "Alpha");
        Import("imp-b", "Beta");
        Nav.NavigateTo("sources?q=bet");
        var cut = Render<SourceManagementPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sm-row]").Select(r => r.GetAttribute("data-key")).Should().Equal("imp-b"));
        cut.Find("[data-testid=sm-search]").GetAttribute("value").Should().Be("bet");
    }

    [Fact]
    public void An_unknown_source_in_the_link_is_reported_and_nothing_is_opened()
    {
        Import("imp-a", "Alpha");
        Nav.NavigateTo("sources?source=does-not-exist");
        var cut = Render<SourceManagementPage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sm-notice]").TextContent.Should().Contain("not part of this project"));
        cut.FindAll("[data-testid=sm-detail]").Should().BeEmpty();
        cut.WaitForAssertion(() => Nav.ToBaseRelativePath(Nav.Uri).Should().Be("sources", "the stale source is dropped from the address"));
    }

    [Fact]
    public void Every_target_application_is_listed_with_its_own_confirmed_roles_and_its_own_Project_Evidence_link()
    {
        Import("imp-m", "M2LB");
        Bind("dev", "Development", "M2LB", "imp-m", "Integration implementation", "Contracts/schema");
        Bind("test", "Test", "Skolenærvær", "imp-m", "Supporting source");
        var cut = Render<SourceManagementPage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sm-associated]").TextContent.Should().Be("M2LB (Development), Skolenærvær (Test)"));
        cut.Find("[data-testid=sm-roles]").TextContent.Should().Contain("No confirmed role", "no source-level role was confirmed; detection is not a role");
        cut.Find("[data-testid=sm-open]").Click();
        cut.Find("[data-testid=sm-tab][data-tab=associations]").Click();

        var apps = cut.FindAll("[data-testid=sm-association]");
        apps.Select(a => a.GetAttribute("data-environment")).Should().Equal("dev", "test");
        apps[0].QuerySelector("[data-testid=sm-association-roles]")!.TextContent.Should().Contain("Integration implementation").And.Contain("Contracts/schema");
        apps[1].QuerySelector("[data-testid=sm-association-roles]")!.TextContent.Should().Contain("Supporting source").And.NotContain("Contracts");
        apps[0].QuerySelector("[data-testid=sm-association-link]")!.GetAttribute("href").Should().Contain("profile=dev");
        apps[1].QuerySelector("[data-testid=sm-association-link]")!.GetAttribute("href").Should().Contain("profile=test", "each application opens its own association, never the first");
        cut.Find("[data-testid=sm-roles-vs-capabilities]").TextContent.Should().Contain("Detected capabilities are not roles");
        cut.Find("[data-testid=sm-associate]").TextContent.Should().Be("Review roles");
    }

    [Theory] // Large inventories: one list build per load, no per-source request; search and filters stay in memory.
    [InlineData(100)]
    [InlineData(500)]
    public void A_large_inventory_renders_filters_and_opens_without_per_source_requests(int count)
    {
        var journeys = new Mock<IIntegrationJourneysApiService>();
        Services.AddSingleton(journeys.Object);
        for (var i = 0; i < count; i++) Import($"imp-{i:D4}", $"Source {i:D4}");
        Bind("dev", "Development", "M2LB", "imp-0001", "Integration implementation");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cut = Render<SourceManagementPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sm-row]").Should().HaveCount(count), TimeSpan.FromSeconds(30));
        var rendered = watch.Elapsed;

        watch.Restart();
        cut.Find("[data-testid=sm-search]").Input("Source 0001");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sm-row]").Should().HaveCount(1));
        var searched = watch.Elapsed;
        cut.Find("[data-testid=sm-open]").Click();
        cut.Find("[data-testid=sm-associated]").TextContent.Should().Be("M2LB (Development)");

        journeys.Verify(j => j.SourceSetupAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once(), "one source-setup read per load, never per source");
        _output.WriteLine($"SM-PERF {count} sources: render {rendered.TotalMilliseconds:F0} ms, search {searched.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void Open_reviews_are_shown_per_application_and_unknown_is_never_shown_as_none()
    {
        Import("imp-m", "M2LB");
        Bind("dev", "Development", "M2LB", "imp-m", "Integration implementation");
        Bind("test", "Test", "Skolenærvær", "imp-m", "Supporting source");
        Bind("qa", "QA", "Other", "imp-m", "Documentation");
        var package = new BirkNext.Web.Services.ProjectEvidence.ProjectEvidencePackage("imp-m", "M2LB", "M2LB.zip", "", DateTimeOffset.UtcNow, null, [],
            new Dictionary<WorkspaceArtifactType, int>(), 0, [], [], [], null);
        BirkNext.Web.Services.ProjectEvidence.ProjectEvidenceView View(string env, bool classified, params BirkNext.Web.Services.ProjectEvidence.PackageAssociation[] associations) =>
            new(env, env, null, null, [], [], [package], []) { Classified = classified, Associations = associations };
        var evidence = new Mock<BirkNext.Web.Services.ProjectEvidence.IProjectEvidenceService>();
        evidence.Setup(e => e.GetAsync("dev", It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(View("dev", true));
        evidence.Setup(e => e.Peek("test")).Returns(View("test", true,
            new BirkNext.Web.Services.ProjectEvidence.PackageAssociation(package, BirkNext.Web.Services.ProjectEvidence.EvidenceAssociationState.NeedsReview,
                PackageEvidenceRole.NotClassified, "Two applications match")));
        evidence.Setup(e => e.Peek("qa")).Returns(View("qa", false));
        Services.AddSingleton(evidence.Object);

        var cut = Render<SourceManagementPage>();
        cut.WaitForElement("[data-testid=sm-open]").Click();
        cut.Find("[data-testid=sm-tab][data-tab=associations]").Click();
        var pending = cut.FindAll("[data-testid=sm-association-pending]").Select(p => p.TextContent.Trim()).ToList();
        pending.Should().HaveCount(3);
        pending[0].Should().Be("No open review", "the active environment's Project Evidence was read");
        pending[1].Should().Be("Needs review", "the other application's own NeedsReview state");
        pending[2].Should().Be("Open reviews: see its Project Evidence", "not classified in this session: unknown, never \"none\"");
        evidence.Verify(e => e.GetAsync("test", It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never(), "another application is only peeked: GetAsync may apply a target application");
    }

    [Fact]
    public void Snapshot_and_analysis_actions_use_the_exact_snapshot_identity()
    {
        var snapshot = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
        Import("imp-s", "Sourced");
        _repository.SddLifecycle.ProjectImports[^1].SourceSnapshotId = snapshot;
        var cut = Render<SourceManagementPage>();
        cut.WaitForElement("[data-testid=sm-open]").Click();
        cut.Find("[data-testid=sm-snapshots]").GetAttribute("href").Should().Be($"snapshots?open=SourceAnalysis:{snapshot:D}");
        cut.Find("[data-testid=sm-snapshots]").TextContent.Should().Be("View snapshots");
        cut.Find("[data-testid=sm-evidence]").GetAttribute("href").Should().Be($"source-analysis?snapshot={snapshot:D}");
        cut.Find("[data-testid=sm-evidence]").TextContent.Should().Be("View analysis");
    }
}
