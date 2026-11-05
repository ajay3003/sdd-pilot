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
/// Source Management: linked repositories appear before their first import, "Check for updates" only reports (never imports), earlier ZIP
/// snapshots are offered with their match strength and the person decides, and roles are saved on the link (several, explicit). No real Azure DevOps.
/// </summary>
public sealed class SourceManagementTests : BunitContext
{
    const string Org = "https://dev.azure.com/bufdir";
    const string Head = "0123456789abcdef0123456789abcdef01234567";
    private readonly Mock<IGitSourcesApiService> _git = new();
    private static readonly Guid Earlier = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private static GitSourceLink Link => new()
    {
        Id = "bufdir|r1", OrganizationUrl = Org, Organization = "bufdir", ProjectId = "p1", Project = "M2LB", RepositoryId = "r1", Repository = "M2LB.Common",
        Branch = "main", Roles = ["Infrastructure"], EnvironmentId = "dev", BirkNextProject = "M2LB",
    };

    public SourceManagementTests()
    {
        var repository = new WorkspaceArtifactRepository();
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev" } });
        _git.Setup(g => g.LinksAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitSourceLink>)[Link], (string?)null));
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

    [Fact]
    public void A_linked_repository_is_listed_before_its_first_import_and_a_check_only_reports()
    {
        _git.Setup(g => g.CheckAsync(Link.Id, "dev", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Link with { LastCheck = new GitUpdateCheck(Head, null, DateTimeOffset.UtcNow, true) }, (string?)null));
        var cut = Render<SourceManagementPage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sm-row]").GetAttribute("data-origin").Should().Be("Git"));
        cut.Find("[data-testid=sm-roles]").TextContent.Should().Contain("confirmed");
        cut.FindAll("[data-testid=sm-check]").Should().BeEmpty("actions live in the detail panel, not on every row");
        cut.Find("[data-testid=sm-open]").Click();
        cut.Find("[data-testid=sm-updates]").TextContent.Should().Be("Not checked");

        cut.Find("[data-testid=sm-check]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sm-updates]").TextContent.Should().Be("Not imported — remote 01234567", "never imported: not an update"));
        _git.Verify(g => g.ImportAsync(It.IsAny<GitImportRequest>(), It.IsAny<CancellationToken>()), Times.Never(), "a check never imports");
    }

    [Theory]
    [InlineData(null, null, false, null, false, GitUpdateState.NotChecked)]
    [InlineData(Head, null, true, null, true, GitUpdateState.UpdateAvailable)]
    [InlineData(Head, null, false, null, true, GitUpdateState.UpToDate)]
    [InlineData(Head, null, true, null, false, GitUpdateState.NotImported)]
    [InlineData(null, null, false, null, true, GitUpdateState.UnableToCompare)]
    [InlineData(null, "Branch not found", false, GitFailureKind.BranchNotFound, true, GitUpdateState.BranchUnavailable)]
    [InlineData(null, "Sign in", false, GitFailureKind.SignInRequired, true, GitUpdateState.AuthenticationRequired)]
    [InlineData(null, "Throttled", false, GitFailureKind.Throttled, true, GitUpdateState.CheckFailed)]
    public void Update_states_are_classified_by_commit_never_by_timestamp(string? remote, string? problem, bool update, GitFailureKind? failure, bool imported, GitUpdateState expected)
    {
        var link = Link with
        {
            LastCheck = remote is null && problem is null && failure is null && !imported && !update ? null : new GitUpdateCheck(remote, null, DateTimeOffset.UtcNow, update, problem, failure),
            Imports = imported ? [new GitLinkImport("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "main", null, "import-1", DateTimeOffset.UtcNow)] : [],
        };
        GitUpdateStates.Of(link).Should().Be(expected);
    }

    [Fact]
    public void Import_takes_the_commit_the_check_reported_so_a_branch_that_moved_is_never_imported_silently()
    {
        var checkedLink = Link with { LastCheck = new GitUpdateCheck(Head, null, DateTimeOffset.UtcNow, true) };
        GitUpdateStates.CommitToImport(checkedLink).Should().Be(Head);
        GitUpdateStates.CommitToImport(Link).Should().BeNull("without a check the branch head at import time is taken — and the button says so");
        GitUpdateStates.CommitToImport(checkedLink with { LastCheck = checkedLink.LastCheck with { Failure = GitFailureKind.Throttled, Problem = "x" } }).Should().BeNull();
        GitUpdateStates.CommitToImport(checkedLink with { PinnedCommit = new string('b', 40) }).Should().Be(new string('b', 40), "a pinned commit always wins");

        _git.Setup(g => g.LinksAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitSourceLink>)[checkedLink], (string?)null));
        var cut = Render<SourceManagementPage>();
        cut.WaitForElement("[data-testid=sm-open]").Click();
        cut.Find("[data-testid=sm-import-revision]").TextContent.Should().Be("Import revision 01234567");
    }

    [Fact]
    public void Check_all_checks_every_linked_repository_read_only_and_reports_each_outcome()
    {
        var second = Link with { Id = "bufdir|r2", RepositoryId = "r2", Repository = "M2LB-language" };
        _git.Setup(g => g.LinksAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitSourceLink>)[Link, second], (string?)null));
        _git.Setup(g => g.CheckAsync(Link.Id, "dev", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Link with { LastCheck = new GitUpdateCheck(Head, null, DateTimeOffset.UtcNow, true) }, (string?)null));
        _git.Setup(g => g.CheckAsync(second.Id, "dev", It.IsAny<CancellationToken>()))
            .ReturnsAsync((second with { LastCheck = new GitUpdateCheck(null, null, DateTimeOffset.UtcNow, false, "Branch not found", GitFailureKind.BranchNotFound) }, (string?)null));
        var cut = Render<SourceManagementPage>();

        cut.WaitForElement("[data-testid=sm-check-all]").TextContent.Should().Contain("Check all linked repositories (2)");
        cut.Find("[data-testid=sm-check-all]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sm-bulk-row]").Select(r => r.GetAttribute("data-state")).Should().BeEquivalentTo(["NotImported", "BranchUnavailable"]));
        cut.Find("[data-testid=sm-bulk-check]").TextContent.Should().Contain("nothing was imported, no snapshot was created");
        _git.Verify(g => g.ImportAsync(It.IsAny<GitImportRequest>(), It.IsAny<CancellationToken>()), Times.Never(), "a check never imports");
    }

    [Fact]
    public void Earlier_imports_show_their_match_strength_and_the_person_decides()
    {
        var match = new GitSourceMatch(Earlier, "M2LB.Common.zip", DateTimeOffset.UnixEpoch, GitSourceMatchStrength.PossibleSameStructure, ["2 of 2 project files at the same paths"], null);
        _git.SetupSequence(g => g.MatchesAsync(Link.Id, "dev", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<GitSourceMatch>)[match], (string?)null))
            .ReturnsAsync(((IReadOnlyList<GitSourceMatch>)[match with { Decision = "Kept separate" }], (string?)null));
        _git.Setup(g => g.DecideAsync(Link.Id, "dev", new GitAssociationRequest(Earlier, false), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Link with { KeptSeparate = [Earlier] }, (string?)null));
        var cut = Render<SourceManagementPage>();
        cut.WaitForElement("[data-testid=sm-open]").Click();
        cut.Find("[data-testid=sm-tab][data-tab=history]").Click();
        cut.Find("[data-testid=sm-matches]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sm-match]").GetAttribute("data-strength").Should().Be("PossibleSameStructure"));
        _git.Verify(g => g.DecideAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<GitAssociationRequest>(), It.IsAny<CancellationToken>()), Times.Never(), "nothing is associated without a decision");

        cut.Find("[data-testid=sm-keep-separate]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sm-match]").TextContent.Should().Contain("Kept separate"));
        _git.Verify(g => g.DecideAsync(Link.Id, "dev", new GitAssociationRequest(Earlier, false), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public void Roles_are_saved_on_the_link_several_at_once()
    {
        GitLinkRequest? saved = null;
        _git.Setup(g => g.LinkAsync(It.IsAny<GitLinkRequest>(), It.IsAny<CancellationToken>()))
            .Callback((GitLinkRequest r, CancellationToken _) => saved = r)
            .ReturnsAsync((GitLinkRequest r, CancellationToken _) => (Link with { Roles = [.. r.Roles] }, (string?)null));
        var cut = Render<SourceManagementPage>();
        cut.WaitForElement("[data-testid=sm-open]").Click();
        cut.Find("[data-testid=sm-edit-roles]").Click();
        cut.Find("[data-testid=sm-save-roles]").HasAttribute("disabled").Should().BeTrue("nothing changed yet");
        cut.Find("[data-testid=sm-role][data-role=Contracts]").Change(true);
        cut.Find("[data-testid=sm-unsaved]").TextContent.Should().Be("Unsaved role changes.");
        cut.Find("[data-testid=sm-save-roles]").Click();
        cut.WaitForAssertion(() => saved.Should().NotBeNull());
        saved!.Should().Match<GitLinkRequest>(r => r.OrganizationUrl == Org && r.RepositoryId == "r1" && r.Branch == "main" && r.EnvironmentId == "dev");
        saved.Roles.Should().BeEquivalentTo(["Infrastructure", "Contracts"]);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sm-role-editor]").Should().BeEmpty());
        cut.Find("[data-testid=sm-notice]").TextContent.Should().Contain("Project Evidence and every review use them");
    }

    [Fact]
    public void The_grid_is_compact_with_a_summary_and_one_row_opens_its_detail_panel_with_all_sections()
    {
        var cut = Render<SourceManagementPage>();
        cut.WaitForElement("[data-testid=sm-summary]");
        cut.Find("[data-testid=sm-summary-total]").TextContent.Should().Be("1 source");
        cut.Find("[data-testid=sm-summary-git]").TextContent.Should().Be("1 Git");
        cut.FindAll("[data-testid=sm-table] thead th").Select(t => t.TextContent).Should().Equal(
            "Source", "Origin", "Capabilities", "Associated applications", "Current snapshot", "Analysis status", "Last updated");
        cut.FindAll("[data-testid=sm-detail]").Should().BeEmpty();

        cut.Find("[data-testid=sm-open]").Click();
        cut.Find("[data-testid=sm-open]").GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll("[data-testid=sm-tab]").Select(t => t.TextContent).Should().Equal(
            "Overview", "Capabilities & Roles", "Associations", "Snapshots", "Analysis & Evidence", "History");
        cut.Find("[data-testid=sm-tab][data-tab=associations]").Click();
        cut.Find("[data-testid=sm-tabpanel]").GetAttribute("data-tab").Should().Be("associations");
        cut.Find("[data-testid=sm-no-associations]").TextContent.Should().Contain("Not associated with any application yet");
        cut.Find("[data-testid=sm-associate-link]").GetAttribute("href").Should().Contain("tab=project");
        cut.Find("[data-testid=sm-open]").Click();
        cut.FindAll("[data-testid=sm-detail]").Should().BeEmpty("the same row closes its panel");
    }
}
