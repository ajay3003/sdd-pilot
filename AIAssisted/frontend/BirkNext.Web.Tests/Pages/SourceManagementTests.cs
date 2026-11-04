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
        cut.Find("[data-testid=sm-updates]").TextContent.Should().Be("Not checked");

        cut.Find("[data-testid=sm-check]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sm-updates]").TextContent.Should().Be("Update available (01234567)"));
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
        cut.WaitForElement("[data-testid=sm-matches]").Click();
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
        cut.WaitForElement("[data-testid=sm-edit-roles]").Click();
        cut.Find("[data-testid=sm-role][data-role=Contracts]").Change(true);
        cut.Find("[data-testid=sm-save-roles]").Click();
        cut.WaitForAssertion(() => saved.Should().NotBeNull());
        saved!.Should().Match<GitLinkRequest>(r => r.OrganizationUrl == Org && r.RepositoryId == "r1" && r.Branch == "main" && r.EnvironmentId == "dev");
        saved.Roles.Should().BeEquivalentTo(["Infrastructure", "Contracts"]);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sm-role-editor]").Should().BeEmpty());
    }
}
