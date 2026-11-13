using BirkNext.Interpretation;
using BirkNext.Scopes;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Systems & Scopes: systems and connections come from the canonical interpretation (one identity, no second engine); scopes come from the
/// shared Scope Registry. A scope is created as a draft with one or several systems by their canonical key, approval is explicit and says
/// it pins membership only, and an edit after approval is a new draft revision.
/// </summary>
public sealed class ProjectScopesPageTests : BunitContext
{
    private const string Project = "import:m2lb-language";
    private readonly Mock<IScopeRegistryApiService> _scopes = new();
    private readonly Mock<ICanonicalInterpretationApiService> _canonical = new();
    private readonly List<ScopeDetail> _stored = [];

    private static CanonicalEntityView System(string id, string name) =>
        new(Guid.NewGuid(), CanonicalEntityType.System, id, name, name, "declared alias", null, new Dictionary<string, string>(), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private readonly CanonicalEntityView _skole = System("skoleservice", "SkoleService");
    private readonly CanonicalEntityView _mm = System("mm", "MM");

    private async Task Setup()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var storage = new ActiveTargetEnvironmentTests.Storage();
        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(storage);
        settings.Settings.ActiveProfileId = "dev";
        Services.AddSingleton<IJSRuntime>(storage);
        Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(ActiveTargetEnvironmentTests.Factory(settings, storage));
        Services.AddSingleton(WorkspaceSnapshots.Projection(WorkspaceSnapshots.Loaded("Imported", Project, "M2LB-language")).Object);
        _canonical.Setup(a => a.EntitiesAsync("dev", CanonicalEntityType.System, null, 0, 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new CanonicalPage<CanonicalEntityView>([_skole, _mm], 2, 0, 200), (string?)null));
        _canonical.Setup(a => a.EntitiesAsync("dev", CanonicalEntityType.Service, null, 0, 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new CanonicalPage<CanonicalEntityView>([], 0, 0, 200), (string?)null));
        Services.AddSingleton(_canonical.Object);
        _scopes.Setup(s => s.ListAsync(Project, It.IsAny<CancellationToken>())).ReturnsAsync(() => ([.. _stored.Select(d => d.Scope)], (string?)null));
        _scopes.Setup(s => s.GetAsync(Project, It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((string _, Guid id, CancellationToken _) => (_stored.FirstOrDefault(d => d.Scope.Id == id), (string?)null));
        _scopes.Setup(s => s.CreateAsync(It.IsAny<CreateScopeRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((CreateScopeRequest r, CancellationToken _) =>
        {
            var detail = new ScopeDetail
            {
                Scope = new ScopeSummary { Id = Guid.NewGuid(), ProjectKey = r.ProjectKey, Name = r.Name, Purpose = r.Purpose, State = ScopeState.Draft, CurrentRevision = 1, Version = 1, MemberCount = r.Members.Count },
                Revisions = [new ScopeRevisionView { Number = 1, State = ScopeRevisionState.Draft, Members = r.Members, CreatedBy = "tester" }],
            };
            _stored.Add(detail);
            return (detail, (string?)null, false);
        });
        Services.AddSingleton(_scopes.Object);
    }

    private void Tab(IRenderedComponent<ProjectScopes> cut, string tab)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"project-scopes?tab={tab}");
        cut.WaitForAssertion(() => cut.Find($"[data-testid=psc-tab][data-tab={tab}]").GetAttribute("aria-selected").Should().Be("true"));
    }

    [Fact]
    public async Task Systems_come_from_the_canonical_interpretation_of_the_active_environment()
    {
        await Setup();
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=psc-system]").Should().HaveCount(2));
        cut.Find("[data-testid=psc-project]").TextContent.Should().Be(Project);
        cut.FindAll("[data-testid=psc-system]").Select(s => s.GetAttribute("data-key")).Should().BeEquivalentTo([_skole.Key, _mm.Key], "the canonical entity key is the one system identity");
        cut.Find("[data-testid=psc-systems]").TextContent.Should().Contain("is not a verified runtime connection");
        cut.FindAll("[data-testid=psc-system]")[0].TextContent.Should().Contain("in no scope");
    }

    [Fact]
    public async Task A_multi_system_scope_is_created_as_a_draft_by_canonical_key_and_approval_is_explained_as_pinning_only()
    {
        await Setup();
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=psc-system]").Should().HaveCount(2));
        Tab(cut, "scopes");
        cut.Find("[data-testid=psc-scopes-empty]");
        cut.Find("[data-testid=psc-new-scope]").Click();
        cut.Find("[data-testid=psc-name]").Change("Report intake");
        // Re-query after each change: a re-render replaces the event handlers of the earlier elements.
        foreach (var key in cut.FindAll("[data-testid=psc-pick-system]").Select(b => b.GetAttribute("data-key")).ToList())
            cut.Find($"[data-testid=psc-pick-system][data-key='{key}']").Change(true);
        cut.Find("[data-testid=psc-other-id]").Change("gyldig-rapport");
        cut.Find("[data-testid=psc-other-add]").Click();
        cut.FindAll("[data-testid=psc-member]").Should().HaveCount(3);

        cut.Find("[data-testid=psc-save]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-notice]").TextContent.Should().Contain("draft revision 1").And.Contain("not used as approved membership"));
        _scopes.Verify(s => s.CreateAsync(It.Is<CreateScopeRequest>(r => r.ProjectKey == Project && r.Members.Count(m => m.Kind == ScopeMemberKind.System) == 2
            && r.Members.Any(m => m.Kind == ScopeMemberKind.Scenario && m.TargetId == "gyldig-rapport")), It.IsAny<CancellationToken>()), Times.Once);
        cut.Find("[data-testid=psc-scope]").GetAttribute("data-state").Should().Be("Draft");
        cut.Find("[data-testid=psc-approve]").TextContent.Should().Be("Approve revision 1");
        cut.Find("[data-testid=psc-scopes]").TextContent.Should().Contain("never authorizes running tests");
    }
}
