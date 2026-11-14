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
/// Systems & Scopes on an M2LB-shaped overview: loading is never shown as "no systems", systems and services are grouped only by declared aliases,
/// Altinn has no invented connection, scopes are built from catalogs (a scenario-only scope needs no system) and revisions are approved, edited
/// and compared without ever changing an approved one.
/// </summary>
public sealed class ProjectScopesPageTests : BunitContext
{
    private const string Project = "import:m2lb-language";
    private readonly Mock<IScopeRegistryApiService> _scopes = new();
    private readonly Mock<ICanonicalInterpretationApiService> _canonical = new();
    private readonly List<ScopeDetail> _stored = [];

    private static SystemOverviewItem Sys(string key, string name, CanonicalEntityType type = CanonicalEntityType.System, string? parent = null, List<string>? services = null) =>
        new() { Id = Guid.NewGuid(), Key = key, Name = name, Type = type, Basis = type == CanonicalEntityType.System ? "declared system name" : "operation owner", ParentKey = parent,
            ParentBasis = parent is null ? null : "Person declares \"M2LB.Person.Api\" as one of its names (domain profile skolenaervaer).", ServiceKeys = services ?? [] };

    private static SystemsOverviewView M2lb() => new()
    {
        Scope = "dev", InterpretationId = Guid.NewGuid(), InterpretedAt = DateTimeOffset.UnixEpoch, SourceLabels = ["M2LB-language.zip"], Sources = ["Canonical interpretation: used"],
        Systems =
        [
            Sys("System|altinn", "Altinn"), Sys("System|mu", "MU"), Sys("System|person", "Person", services: ["Service|m2lb.person.api"]),
            Sys("Service|m2lb.person.api", "M2LB.Person.Api", CanonicalEntityType.Service, parent: "System|person") with { Outgoing = 1 },
            Sys("Service|m2lb.personbirkadapter.worker", "M2LB.PersonBiRKAdapter.Worker", CanonicalEntityType.Service) with { NeedsReview = 1, Incoming = 1 },
            Sys("Service|m2lb.skoleservice.api", "M2LB.SkoleService.Api", CanonicalEntityType.Service) with { Incoming = 1 },
        ],
        Connections =
        [
            new() { Id = "c1", SourceKey = "Service|m2lb.person.api", SourceName = "M2LB.Person.Api", TargetKey = "Service|m2lb.skoleservice.api", TargetName = "M2LB.SkoleService.Api",
                Label = "sends messages via person.barn", Kind = "Messaging", State = SystemConnectionState.FromSourceEvidence, Origin = "Canonical interpretation", Unverified = "No message observed." },
            new() { Id = "c2", SourceKey = "Service|m2lb.person.api", SourceName = "M2LB.Person.Api", TargetKey = "Service|m2lb.personbirkadapter.worker", TargetName = "M2LB.PersonBiRKAdapter.Worker",
                Label = "sends messages via person.barn", Kind = "Messaging", State = SystemConnectionState.Candidate, Origin = "Canonical interpretation", Unverified = "No message observed." },
            new() { Id = "c3", SourceKey = "System|mu", SourceName = "MU", TargetKey = "System|person", TargetName = "Person", Label = "depends on", Kind = "DependsOn",
                State = SystemConnectionState.Rejected, Origin = "Canonical interpretation" },
        ],
    };

    private async Task Setup(SystemsOverviewView? overview = null, Task<(SystemsOverviewView?, string?)>? pending = null)
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
        Services.AddSingleton(_canonical.Object);
        if (pending is not null) _scopes.Setup(s => s.SystemsAsync("dev", It.IsAny<CancellationToken>())).Returns(pending);
        else _scopes.Setup(s => s.SystemsAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync((overview ?? M2lb(), (string?)null));
        _scopes.Setup(s => s.ListAsync(Project, It.IsAny<CancellationToken>())).ReturnsAsync(() => ([.. _stored.Select(d => d.Scope)], (string?)null));
        _scopes.Setup(s => s.GetAsync(Project, It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((string _, Guid id, CancellationToken _) => (_stored.FirstOrDefault(d => d.Scope.Id == id), (string?)null));
        _scopes.Setup(s => s.MembershipAsync(Project, ScopeMemberKind.System, It.IsAny<CancellationToken>())).ReturnsAsync(() => ([.. _stored.Select(d =>
            new ScopeMembershipSummary(d.Scope.Id, d.Scope.Name, d.Scope.State, d.Scope.CurrentRevision,
                [.. d.Revisions.First().Members.Where(m => m.Kind == ScopeMemberKind.System).Select(m => m.TargetId)]))], (string?)null));
        _scopes.Setup(s => s.CheckAsync(Project, It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Guid id, int? _, string? _, CancellationToken _) => ([.. _stored.Single(d => d.Scope.Id == id).Revisions.First().Members
                .Select(m => new ScopeMemberStatus(m.Key, m.TargetId.Contains("removed") ? ScopeMemberCheck.Missing : ScopeMemberCheck.Current, "checked"))], (string?)null));
        _scopes.Setup(s => s.CheckMembersAsync(It.IsAny<ScopeMemberCheckRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ScopeMemberCheckRequest r, CancellationToken _) => ([.. r.Members.Select(m => new ScopeMemberStatus(m.Key, ScopeMemberCheck.Current, "ok", m.Label))], (string?)null));
        _scopes.Setup(s => s.CatalogAsync(It.IsAny<ScopeMemberKind>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ScopeMemberKind kind, string? scope, string? search, int skip, int take, CancellationToken _) => (new ScopeCatalogPage(kind, kind switch
            {
                ScopeMemberKind.Scenario => [new ScopeCatalogItem { Kind = kind, TargetId = "Scenario|system-understanding/skole/s1", Label = "Valid report is accepted", SourceModule = "Canonical interpretation", CatalogScope = scope }],
                ScopeMemberKind.Requirement => [new ScopeCatalogItem { Kind = kind, TargetId = "Requirement|skole/001/fr-001", Label = "FR-001: Receive the report", SourceModule = "Canonical interpretation", CatalogScope = scope }],
                ScopeMemberKind.System => [new ScopeCatalogItem { Kind = kind, TargetId = "System|person", Label = "Person", SourceModule = "Canonical interpretation", CatalogScope = scope }],
                _ => [],
            }, 1, skip, take), (string?)null));
        _scopes.Setup(s => s.CreateAsync(It.IsAny<CreateScopeRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((CreateScopeRequest r, CancellationToken _) =>
        {
            var detail = Detail(r.Name, r.Purpose, r.Members);
            _stored.Add(detail);
            return (detail, (string?)null, false);
        });
        Services.AddSingleton(_scopes.Object);
    }

    private static ScopeDetail Detail(string name, ScopePurpose purpose, List<ScopeMember> members) => new()
    {
        Scope = new ScopeSummary { Id = Guid.NewGuid(), ProjectKey = Project, Name = name, Purpose = purpose, State = ScopeState.Draft, CurrentRevision = 1, Version = 1, MemberCount = members.Count,
            MembersByKind = members.GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.Count()) },
        Revisions = [new ScopeRevisionView { Number = 1, State = ScopeRevisionState.Draft, Members = members, CreatedBy = "tester" }],
    };

    private void Go(string query) => Services.GetRequiredService<NavigationManager>().NavigateTo($"project-scopes{query}");

    [Fact]
    public async Task While_systems_load_the_page_says_loading_and_never_no_systems()
    {
        var pending = new TaskCompletionSource<(SystemsOverviewView?, string?)>();
        await Setup(pending: pending.Task);
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-loading]"));
        cut.FindAll("[data-testid=psc-state]").Should().BeEmpty("no empty state is shown before the answer arrives");
        cut.Markup.Should().NotContain("No systems found");

        pending.SetResult((new SystemsOverviewView { Scope = "dev", InterpretationId = Guid.NewGuid() }, null));
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-state]").GetAttribute("data-state").Should().Be(nameof(SystemsPageState.ZeroSystems)));
        cut.Find("[data-testid=psc-state]").TextContent.Should().Contain("finished and found no systems");
    }

    [Fact]
    public async Task Without_an_interpretation_the_page_points_to_the_existing_analysis_and_does_not_run_one()
    {
        await Setup(new SystemsOverviewView { Scope = "dev", Limitations = ["No interpretation has been run for this Target Environment yet."] });
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-state]").GetAttribute("data-state").Should().Be(nameof(SystemsPageState.NotInterpreted)));
        cut.Find("[data-testid=psc-state] a").GetAttribute("href").Should().Be("correlation-review");
        _canonical.Verify(c => c.InterpretAsync(It.IsAny<string>(), It.IsAny<InterpretRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Systems_show_a_readable_project_group_services_by_declaration_and_search_aliases()
    {
        await Setup();
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=psc-system]").Should().HaveCount(6));
        cut.Find("[data-testid=psc-project]").TextContent.Should().Be("M2LB-language", "the header shows the project name; the key is under Technical details");
        cut.Find("[data-testid=psc-technical]").TextContent.Should().Contain(Project);
        cut.FindAll("[data-testid=psc-system].is-nested").Select(e => e.GetAttribute("data-key")).Should().Equal("Service|m2lb.person.api");
        cut.Find("[data-testid=psc-system][data-key='Service|m2lb.personbirkadapter.worker']").ClassList.Should().NotContain("is-nested", "a similar name is not membership");

        cut.Find("[data-testid=psc-system-search]").Input("person");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=psc-system]").Should().HaveCount(3));
        cut.Find("[data-testid=psc-system][data-key='Service|m2lb.person.api'] [data-testid=psc-system-open]").Click();
        cut.Find("[data-testid=psc-parent]").TextContent.Should().Contain("Person").And.Contain("declares");

        cut.Find("[data-testid=psc-system-search]").Input("");
        cut.Find("[data-testid=psc-review-filter]").Change("needs-review");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=psc-system]").Select(e => e.GetAttribute("data-key")).Should().Equal("Service|m2lb.personbirkadapter.worker"));
    }

    [Fact]
    public async Task Connections_list_the_project_by_default_and_Altinn_gets_no_invented_relationship()
    {
        await Setup();
        Go("?tab=connections");
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=psc-conn]").Should().HaveCount(2, "rejected connections are hidden by default"));
        cut.FindAll("[data-testid=psc-conn-state-label]").Select(e => e.TextContent).Should().BeEquivalentTo(["From source evidence", "Candidate — needs evidence or review"]);
        cut.Find("[data-testid=psc-conn-summary]").TextContent.Should().Contain("0 observed at runtime");

        cut.Find("[data-testid=psc-conn-state]").Change("rejected");
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-conn]").GetAttribute("data-state").Should().Be("Rejected"));

        cut.Find("[data-testid=psc-conn-system]").Input("Alt");
        cut.Find("[data-testid=psc-conn-option]").GetAttribute("data-key").Should().Be("System|altinn");
        Go("?tab=connections&system=System%7Caltinn");
        cut.Find("[data-testid=psc-conn-state]").Change("active");
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-conn-empty]").TextContent.Should().Contain("No relationship evidence for Altinn"));
        cut.Find("[data-testid=psc-conn-focus]").TextContent.Should().Contain("Altinn");
        cut.Find("[data-testid=psc-conn-review]").GetAttribute("href").Should().StartWith("correlation-review?tab=review&from=project-scopes");
    }

    [Fact]
    public async Task A_scope_with_only_a_scenario_and_a_requirement_is_created_from_catalogs_without_any_system()
    {
        await Setup();
        Go("?tab=scopes");
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-scopes-empty]"));
        cut.Find("[data-testid=psc-new-scope]").Click();
        cut.Find("[data-testid=psc-name]").Input("Report intake");
        cut.Find("[data-testid=psc-next]").Click();

        cut.Find("[data-testid=smp-kind][data-kind=Scenario]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=smp-pick]").GetAttribute("data-id").Should().Be("Scenario|system-understanding/skole/s1"));
        cut.Find("[data-testid=smp-pick]").Change(true);
        cut.Find("[data-testid=smp-kind][data-kind=Requirement]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=smp-pick]").GetAttribute("data-id").Should().Be("Requirement|skole/001/fr-001"));
        cut.Find("[data-testid=smp-pick]").Change(true);
        cut.Find("[data-testid=psc-member-count]").TextContent.Should().Be("2");
        cut.Find("[data-testid=psc-members]").TextContent.Should().NotContain("Scenario|system-understanding", "ids are not shown as names").And.Contain("Valid report is accepted");
        cut.Find("[data-testid=smp]").TextContent.Should().NotContain("Requirement|skole");

        cut.Find("[data-testid=psc-next]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-review]").TextContent.Should().Contain("Creates draft revision 1"));
        cut.Find("[data-testid=psc-save]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-notice]").TextContent.Should().Contain("draft revision 1"));
        _scopes.Verify(s => s.CreateAsync(It.Is<CreateScopeRequest>(r => r.ProjectKey == Project && r.CatalogScope == "dev" && r.Members.Count == 2
            && r.Members.All(m => m.Kind != ScopeMemberKind.System) && r.Members.All(m => m.CatalogScope == "dev")), It.IsAny<CancellationToken>()), Times.Once);
        cut.Find("[data-testid=psc-scope-detail]").GetAttribute("data-state").Should().Be("Draft");
        cut.Find("[data-testid=psc-approve]").TextContent.Should().Be("Approve revision 1");
    }

    [Fact]
    public async Task An_approved_scope_is_edited_as_a_new_revision_compared_and_a_stale_member_is_flagged()
    {
        var approved = Detail("Person flow", ScopePurpose.IntegrationE2E, [new() { Kind = ScopeMemberKind.System, TargetId = "System|person", Label = "Person" },
            new() { Kind = ScopeMemberKind.System, TargetId = "Service|old.removed.api", Label = "Old.Removed.Api" }]);
        approved = approved with
        {
            Scope = approved.Scope with { State = ScopeState.Approved, ApprovedRevision = 1, Version = 2 },
            Revisions = [approved.Revisions[0] with { State = ScopeRevisionState.Approved, ApprovedBy = "lead" }],
        };
        _stored.Add(approved);
        await Setup();
        var edited = approved with
        {
            Scope = approved.Scope with { CurrentRevision = 2, Version = 3 },
            Revisions = [new ScopeRevisionView { Number = 2, State = ScopeRevisionState.Draft, Members = [.. approved.Revisions[0].Members, new() { Kind = ScopeMemberKind.Scenario, TargetId = "Scenario|x", Label = "Valid report is accepted" }] },
                approved.Revisions[0]],
        };
        _scopes.Setup(s => s.SaveAsync(Project, approved.Scope.Id, It.IsAny<SaveScopeDraftRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { _stored[0] = edited; return (edited, (string?)null, false); });
        _scopes.Setup(s => s.CompareAsync(Project, approved.Scope.Id, 1, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ScopeComparison(approved.Scope.Id, 1, 2, [new(ScopeMemberChange.Added, null, edited.Revisions[0].Members[^1])]), (string?)null));

        Go($"?tab=scopes&scope={approved.Scope.Id}");
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-scope-detail]").GetAttribute("data-state").Should().Be("Approved"));
        cut.Find("[data-testid='psc-scope-member'][data-key='System:Service|old.removed.api']").TextContent.Should().Contain("Missing from catalog");
        cut.Find("[data-testid=psc-approved-note]").TextContent.Should().Contain("never changes");

        cut.Find("[data-testid=psc-edit]").Click();
        cut.Find("[data-testid=smp-kind][data-kind=Scenario]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=smp-pick]"));
        cut.Find("[data-testid=smp-pick]").Change(true);
        cut.Find("[data-testid=psc-next]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-review]").TextContent.Should().Contain("Creates draft revision 2").And.Contain("approved revision 1 stays"));
        cut.Find("[data-testid=psc-save]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=psc-revision]").Should().HaveCount(2));
        _scopes.Verify(s => s.SaveAsync(Project, approved.Scope.Id, It.Is<SaveScopeDraftRequest>(r => r.ExpectedVersion == 2 && r.Members.Count == 3), It.IsAny<CancellationToken>()), Times.Once);
        cut.Find("[data-testid=psc-compare]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-diff]").GetAttribute("data-change").Should().Be("Added"));
    }

    [Fact]
    public void Presentation_states_and_connection_filters_never_count_a_candidate_as_established()
    {
        SystemsScopesPresentation.StateOf(false, true, null, null, null, null).Should().Be(SystemsPageState.NoEnvironment);
        SystemsScopesPresentation.StateOf(false, false, "dev", null, null, null).Should().Be(SystemsPageState.NoProject);
        SystemsScopesPresentation.StateOf(false, true, "dev", null, new SystemsOverviewView(), 0).Should().Be(SystemsPageState.NoSource);
        SystemsScopesPresentation.StateOf(false, true, "dev", "boom", null, null).Should().Be(SystemsPageState.Error);
        var connections = M2lb().Connections;
        SystemsScopesPresentation.FilterConnections(connections, null, "both", "established", "all", null).Select(c => c.Id).Should().Equal("c1");
        SystemsScopesPresentation.FilterConnections(connections, "Service|m2lb.person.api", "in", "active", "all", null).Should().BeEmpty();
        SystemsScopesPresentation.FilterConnections(connections, "Service|m2lb.person.api", "out", "active", "all", null).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_running_analysis_is_announced_and_an_unfinished_first_analysis_is_never_shown_as_empty()
    {
        await Setup(new SystemsOverviewView { Scope = "dev", AnalysisRunningSince = DateTimeOffset.UtcNow, AnalysisRunningTrigger = "Relationship Review" });
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-state]").GetAttribute("data-state").Should().Be(nameof(SystemsPageState.Analysing)));
        cut.Find("[data-testid=psc-analysing]").TextContent.Should().Contain("Relationship Review");
        cut.Markup.Should().NotContain("No systems found");
    }

    [Fact]
    public async Task System_scope_membership_is_read_with_one_call_not_one_per_scope()
    {
        _stored.Add(Detail("Person flow", ScopePurpose.Test, [new() { Kind = ScopeMemberKind.System, TargetId = "System|person", Label = "Person" }]));
        _stored.Add(Detail("Altinn intake", ScopePurpose.Test, [new() { Kind = ScopeMemberKind.System, TargetId = "System|altinn", Label = "Altinn" }]));
        await Setup();
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-system][data-key='System|person']").TextContent.Should().Contain("In 1 scope"));
        _scopes.Verify(s => s.MembershipAsync(Project, ScopeMemberKind.System, It.IsAny<CancellationToken>()), Times.Once);
        _scopes.Verify(s => s.GetAsync(Project, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_connection_graph_draws_only_listed_connections_marks_candidates_and_opens_an_edge_in_the_list()
    {
        await Setup();
        Go("?tab=connections");
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=psc-graph-pick]").Should().NotBeEmpty());
        cut.FindAll("[data-testid=scg]").Should().BeEmpty("without a focused system the project is not drawn as one hairball");

        Go("?tab=connections&system=Service%7Cm2lb.person.api");
        cut.WaitForAssertion(() => cut.Find("[data-testid=scg-focus]").TextContent.Should().Contain("M2LB.Person.Api"));
        cut.FindAll("[data-testid=scg-node]").Select(n => n.GetAttribute("data-key")).Should().BeEquivalentTo(["Service|m2lb.skoleservice.api", "Service|m2lb.personbirkadapter.worker"]);
        var edges = cut.FindAll("[data-testid=scg-edge]");
        edges.Should().HaveCount(2, "the rejected MU → Person connection is not drawn");
        edges.Single(e => e.GetAttribute("data-id") == "c2").ClassList.Should().Contain("is-candidate");
        edges.Should().NotContain(e => e.ClassList.Contains("is-observed"), "nothing was observed at runtime");

        cut.Find("[data-testid=scg-edge][data-id=c2]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-conn-detail]").TextContent.Should().Contain("Not proven"));
        cut.Find("[data-testid=scg-canvas]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "+" });
        cut.Find("[data-testid=scg-zoom]").TextContent.Should().Be("125%");
        cut.Find("[data-testid=scg-fit]").Click();
        cut.Find("[data-testid=scg-zoom]").TextContent.Should().Be("100%");
    }
}
