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
/// Project Test Setup (formerly Systems &amp; Scopes) on an M2LB-shaped overview: the default view is six guided steps whose states and the one
/// recommended next step come from real data; loading is never shown as "no systems"; systems and services are grouped only by declared aliases;
/// Altinn has no invented connection; test areas are built from catalogs (a scenario-only test area needs no system), suggestions are never saved
/// as confirmed membership unless a person keeps them; revisions are approved, edited and compared without ever changing an approved one; only
/// modules that read ?scope= are linked; and a test area of another project is never reused.
/// </summary>
public sealed class ProjectScopesPageTests : BunitContext
{
    private const string Project = "import:m2lb-language";
    private const string OtherProject = "import:other";
    private readonly Mock<IScopeRegistryApiService> _scopes = new();
    private readonly Mock<ICanonicalInterpretationApiService> _canonical = new();
    private readonly Mock<ICurrentWorkspaceProjection> _workspace = new();
    private CurrentWorkspaceSnapshot _snapshot = WorkspaceSnapshots.Loaded("Imported", Project, "M2LB-language");
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

    /// <summary>The default catalogs: one scenario, one requirement and one system (no Test Data, no journeys).</summary>
    private static List<ScopeCatalogItem> DefaultCatalog(ScopeMemberKind kind, string? scope) => kind switch
    {
        ScopeMemberKind.Scenario => [new ScopeCatalogItem { Kind = kind, TargetId = "Scenario|system-understanding/skole/s1", Label = "Valid report is accepted", SourceModule = "Canonical interpretation", CatalogScope = scope }],
        ScopeMemberKind.Requirement => [new ScopeCatalogItem { Kind = kind, TargetId = "Requirement|skole/001/fr-001", Label = "FR-001: Receive the report", SourceModule = "Canonical interpretation", CatalogScope = scope }],
        ScopeMemberKind.System => [new ScopeCatalogItem { Kind = kind, TargetId = "System|person", Label = "Person", SourceModule = "Canonical interpretation", CatalogScope = scope }],
        _ => [],
    };

    private Func<ScopeMemberKind, string?, List<ScopeCatalogItem>> _catalog = DefaultCatalog;

    private async Task Setup(SystemsOverviewView? overview = null, Task<(SystemsOverviewView?, string?)>? pending = null, bool noSourceImported = false)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var storage = new ActiveTargetEnvironmentTests.Storage();
        var settings = new FrontendAnalysisSettingsService();
        await settings.LoadAsync(storage);
        settings.Settings.ActiveProfileId = "dev";
        Services.AddSingleton<IJSRuntime>(storage);
        Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(ActiveTargetEnvironmentTests.Factory(settings, storage));
        _workspace.Setup(p => p.GetAsync()).ReturnsAsync(() => _snapshot);
        _workspace.SetupGet(p => p.Current).Returns(() => _snapshot);
        Services.AddSingleton(_workspace.Object);
        Services.AddSingleton(_canonical.Object);
        if (noSourceImported)
        {
            var catalog = new Mock<IIntegrationCatalogApiService>();
            catalog.Setup(c => c.ListSourceSnapshotSummariesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<BirkNext.Integrations.IqrSourceSnapshotSummary>());
            Services.AddSingleton(catalog.Object);
        }
        if (pending is not null) _scopes.Setup(s => s.SystemsAsync("dev", It.IsAny<CancellationToken>())).Returns(pending);
        else _scopes.Setup(s => s.SystemsAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync((overview ?? M2lb(), (string?)null));
        _scopes.Setup(s => s.ListAsync(Project, It.IsAny<CancellationToken>())).ReturnsAsync(() => ([.. _stored.Select(d => d.Scope)], (string?)null));
        _scopes.Setup(s => s.ListAsync(OtherProject, It.IsAny<CancellationToken>())).ReturnsAsync(() => ([], (string?)null));
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
            .ReturnsAsync((ScopeMemberKind kind, string? scope, string? search, int skip, int take, CancellationToken _) =>
            {
                var all = _catalog(kind, scope).Where(i => string.IsNullOrWhiteSpace(search) || i.Label.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
                return (new ScopeCatalogPage(kind, [.. all.Skip(skip).Take(take)], all.Count, skip, take), (string?)null);
            });
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
            MembersByKind = members.GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.Count()), UpdatedAt = DateTimeOffset.UnixEpoch },
        Revisions = [new ScopeRevisionView { Number = 1, State = ScopeRevisionState.Draft, Members = members, CreatedBy = "tester" }],
    };

    private static ScopeDetail Approve(ScopeDetail d) => d with
    {
        Scope = d.Scope with { State = ScopeState.Approved, ApprovedRevision = d.Scope.CurrentRevision, Version = d.Scope.Version + 1 },
        Revisions = [d.Revisions[0] with { State = ScopeRevisionState.Approved, ApprovedBy = "lead" }, .. d.Revisions.Skip(1)],
    };

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    private void Go(string query) => Nav.NavigateTo($"project-scopes{query}");

    private static string StepState(IRenderedComponent<ProjectScopes> cut, int step) => cut.Find($"[data-testid=pts-step][data-step='{step}']").GetAttribute("data-state")!;

    // ── Existing views (reached via their views and the old ?tab= deep links) ─────────────────────────────────────────────

    [Fact]
    public async Task While_systems_load_the_page_says_loading_and_never_no_systems()
    {
        var pending = new TaskCompletionSource<(SystemsOverviewView?, string?)>();
        await Setup(pending: pending.Task);
        Go("?view=systems");
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-loading]"));
        cut.FindAll("[data-testid=psc-state]").Should().BeEmpty("no empty state is shown before the answer arrives");
        cut.Markup.Should().NotContain("No systems found");

        pending.SetResult((new SystemsOverviewView { Scope = "dev", InterpretationId = Guid.NewGuid() }, null));
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-state]").GetAttribute("data-state").Should().Be(nameof(SystemsPageState.ZeroSystems)));
        cut.Find("[data-testid=psc-state]").TextContent.Should().Contain("finished and found no systems");
    }

    [Fact]
    public async Task While_systems_load_the_setup_steps_say_loading_and_never_no_systems()
    {
        var pending = new TaskCompletionSource<(SystemsOverviewView?, string?)>();
        await Setup(pending: pending.Task);
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => StepState(cut, 1).Should().Be(nameof(SetupStepState.NotStarted)));
        cut.Find("[data-testid=pts-step][data-step='1']").TextContent.Should().Contain("Loading systems");
        cut.Markup.Should().NotContain("found no systems");
    }

    [Fact]
    public async Task Without_an_interpretation_the_page_points_to_the_existing_analysis_and_does_not_run_one()
    {
        await Setup(new SystemsOverviewView { Scope = "dev", Limitations = ["No interpretation has been run for this Target Environment yet."] });
        Go("?view=systems");
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-state]").GetAttribute("data-state").Should().Be(nameof(SystemsPageState.NotInterpreted)));
        cut.Find("[data-testid=psc-state] a").GetAttribute("href").Should().Be("correlation-review");

        Go("");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-next-action]").GetAttribute("data-action").Should().Be("analyze"));
        cut.Find("[data-testid=pts-next-action-go]").GetAttribute("href").Should().Be("correlation-review", "Analyze source links to the existing place; this page never analyses");
        StepState(cut, 1).Should().Be(nameof(SetupStepState.NeedsAttention));
        StepState(cut, 2).Should().Be(nameof(SetupStepState.Unavailable));
        _canonical.Verify(c => c.InterpretAsync(It.IsAny<string>(), It.IsAny<InterpretRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Systems_show_a_readable_project_group_services_by_declaration_and_search_aliases()
    {
        await Setup();
        Go("?view=systems");
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=psc-system]").Should().HaveCount(6));
        cut.Find("[data-testid=psc-project]").TextContent.Should().Be("M2LB-language", "the header shows the project name; the key is under Technical details");
        cut.Find("[data-testid=pts-technical]").TextContent.Should().Contain(Project);
        cut.Find("[data-testid=pts-back]").GetAttribute("href").Should().Be("project-scopes");
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
    public async Task The_system_kind_filter_is_kept_in_the_url()
    {
        await Setup();
        Go("?view=systems&kind=service");
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=psc-system]").Should().HaveCount(3, "the kind filter is restored from the URL"));
        cut.Find("[data-testid=psc-kind-filter]").Change("system");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=psc-system]").Should().HaveCount(3));
        Nav.Uri.Should().Contain("kind=system");
        cut.FindAll("[data-testid=psc-system]").Select(e => e.GetAttribute("data-type")).Should().OnlyContain(t => t == "System");
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
        Nav.Uri.Should().Contain("status=rejected", "the status filter is kept in the URL");

        cut.Find("[data-testid=psc-conn-system]").Input("Alt");
        cut.Find("[data-testid=psc-conn-option]").GetAttribute("data-key").Should().Be("System|altinn");
        Go("?tab=connections&system=System%7Caltinn");
        cut.Find("[data-testid=psc-conn-state]").Change("active");
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-conn-empty]").TextContent.Should().Contain("No relationship evidence for Altinn"));
        cut.Find("[data-testid=psc-conn-focus]").TextContent.Should().Contain("Altinn");
        cut.Find("[data-testid=psc-conn-review]").GetAttribute("href").Should().StartWith("correlation-review?tab=review&from=project-scopes");
    }

    [Fact]
    public async Task An_approved_scope_is_edited_as_a_new_revision_compared_and_a_stale_member_is_flagged()
    {
        var approved = Approve(Detail("Person flow", ScopePurpose.IntegrationE2E, [new() { Kind = ScopeMemberKind.System, TargetId = "System|person", Label = "Person" },
            new() { Kind = ScopeMemberKind.System, TargetId = "Service|old.removed.api", Label = "Old.Removed.Api" }]));
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
        cut.Find("[data-testid='psc-scope-member'][data-key='System:Service|old.removed.api'] [data-testid=pts-member-state]").GetAttribute("data-state").Should().Be(nameof(AreaMemberState.Unresolved));
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
        Go("?view=systems");
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
        Go("?view=systems");
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-system][data-key='System|person']").TextContent.Should().Contain("In 1 test area"));
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

    [Fact]
    public async Task Connections_can_be_narrowed_to_the_selected_test_area()
    {
        _stored.Add(Detail("Person intake", ScopePurpose.Test, [new() { Kind = ScopeMemberKind.System, TargetId = "Service|m2lb.skoleservice.api", Label = "M2LB.SkoleService.Api" }]));
        await Setup();
        Go($"?view=connections&scope={_stored[0].Scope.Id}");
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=psc-conn]").Should().HaveCount(2));
        cut.Find("[data-testid=pts-conn-area-only]").Change(true);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=psc-conn]").Should().ContainSingle());
        Nav.Uri.Should().Contain("inarea=1");
    }

    // ── Guided setup: step states and the recommended next step per data state ─────────────────────────────────────────

    [Fact]
    public async Task The_default_view_is_six_guided_steps_and_without_test_areas_the_next_step_is_create()
    {
        await Setup();
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pts-step]").Select(s => s.GetAttribute("data-step")).Should().Equal("1", "2", "3", "4", "5", "6"));
        cut.Find("h1").TextContent.Should().Be("Project Test Setup");
        cut.Find("[data-testid=pts-purpose]").TextContent.Should().Be("Choose what you want to test and use the selected test area across BirkNext.");
        cut.Find("[data-testid=pts-summary-systems]").TextContent.Should().Contain("6");
        cut.Find("[data-testid=pts-summary-connections]").TextContent.Should().Contain("2", "rejected connections are not counted");
        cut.Find("[data-testid=pts-summary-areas]").TextContent.Should().Contain("0");
        cut.Find("[data-testid=pts-technical]").TextContent.Should().Contain(Project);
        cut.Find("[data-testid=pts-summary]").TextContent.Should().NotContain(Project, "raw ids stay under Technical details");

        StepState(cut, 1).Should().Be(nameof(SetupStepState.Completed));
        StepState(cut, 2).Should().Be(nameof(SetupStepState.Available), "reviewing connections is optional");
        var step2 = cut.Find("[data-testid=pts-step][data-step='2']").TextContent;
        step2.Should().Contain("1 connection may need review (optional)").And.Contain("Source-discovered connections are not proof that integrations work in a running environment.");
        step2.Should().NotContain("observed at runtime", "runtime verification is only mentioned when runtime evidence exists");
        cut.Find("[data-testid=pts-review-relationships]").GetAttribute("href").Should().StartWith("correlation-review?tab=review&from=project-scopes");
        StepState(cut, 3).Should().Be(nameof(SetupStepState.Current));
        cut.Find("[data-testid=pts-step][data-step='3']").TextContent.Should().Contain("No test areas have been created yet.");
        StepState(cut, 4).Should().Be(nameof(SetupStepState.Unavailable));
        StepState(cut, 6).Should().Be(nameof(SetupStepState.Unavailable));
        cut.Find("[data-testid=pts-step][data-step='3']").GetAttribute("aria-current").Should().Be("step");
        cut.Find("[data-testid=pts-next-action]").GetAttribute("data-action").Should().Be("create");
        cut.Find("[data-testid=pts-progress]").TextContent.Should().Contain("1 of 6");
        cut.FindAll("[data-testid=pts-use-link]").Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_project_the_next_step_is_import_project()
    {
        _snapshot = WorkspaceSnapshots.Loaded("Imported", null, null);
        await Setup();
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-no-project]"));
        cut.Find("[data-testid=pts-next-action]").GetAttribute("data-action").Should().Be("import");
        cut.Find("[data-testid=pts-next-action]").GetAttribute("href").Should().Be("project-import");
        _scopes.Verify(s => s.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Without_imported_source_the_next_step_is_import_project_source()
    {
        await Setup(new SystemsOverviewView { Scope = "dev" }, noSourceImported: true);
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-next-action]").GetAttribute("data-action").Should().Be("import-source"));
        cut.Find("[data-testid=pts-next-action-go]").GetAttribute("href").Should().Be("project-import");
        StepState(cut, 1).Should().Be(nameof(SetupStepState.NeedsAttention));
        cut.Find("[data-testid=pts-step][data-step='1']").TextContent.Should().Contain("No source has been imported");
    }

    [Fact]
    public async Task Systems_without_connections_never_block_creating_a_test_area()
    {
        await Setup(new SystemsOverviewView { Scope = "dev", InterpretationId = Guid.NewGuid(), Systems = [Sys("System|altinn", "Altinn")] });
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => StepState(cut, 2).Should().Be(nameof(SetupStepState.Available)));
        cut.Find("[data-testid=pts-step][data-step='2']").TextContent.Should().Contain("No connections were found");
        cut.FindAll("[data-testid=pts-review-relationships]").Should().BeEmpty("there is nothing uncertain to review");
        cut.Find("[data-testid=pts-next-action]").GetAttribute("data-action").Should().Be("create");
    }

    [Fact]
    public async Task A_draft_with_candidates_and_a_missing_member_shows_each_member_state_and_asks_to_review_them()
    {
        var draft = Detail("Report intake", ScopePurpose.Test,
        [
            new() { Kind = ScopeMemberKind.System, TargetId = "System|person", Label = "Person", Provenance = "Selected by a person from Canonical interpretation" },
            new() { Kind = ScopeMemberKind.Requirement, TargetId = "Requirement|skole/001/fr-001", Label = "FR-001: Receive the report", Provenance = "Suggested from the specification; kept by a person" },
            new() { Kind = ScopeMemberKind.Scenario, TargetId = "Scenario|s2", Label = "Late report", Inclusion = ScopeMemberInclusion.Candidate, Provenance = "Integration Topology candidate" },
            new() { Kind = ScopeMemberKind.Scenario, TargetId = "Scenario|removed", Label = "Old scenario", Provenance = "Selected by a person" },
        ]);
        _stored.Add(draft);
        await Setup();
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pts-coverage] [data-testid=pts-member]").Should().HaveCount(4));
        string State(string key) => cut.Find($"[data-testid=pts-coverage] [data-testid=pts-member][data-key='{key}'] [data-testid=pts-member-state]").GetAttribute("data-state")!;
        State("System:System|person").Should().Be(nameof(AreaMemberState.ExplicitlySelected));
        State("Requirement:Requirement|skole/001/fr-001").Should().Be(nameof(AreaMemberState.HumanConfirmed));
        State("Scenario:Scenario|s2").Should().Be(nameof(AreaMemberState.Candidate));
        State("Scenario:Scenario|removed").Should().Be(nameof(AreaMemberState.Unresolved));
        cut.Find("[data-testid=pts-coverage] [data-testid=pts-member][data-key='Scenario:Scenario|removed']").TextContent.Should().Contain("Unresolved").And.Contain("Missing from catalog");

        StepState(cut, 3).Should().Be(nameof(SetupStepState.Completed));
        StepState(cut, 4).Should().Be(nameof(SetupStepState.NeedsAttention));
        cut.Find("[data-testid=pts-next-action]").GetAttribute("data-action").Should().Be("review-members");
        cut.Find("[data-testid=pts-next-label]").TextContent.Should().Be("Review 2 unresolved items");
        cut.Find("[data-testid=pts-saved-revision]").TextContent.Should().Contain("Saved — revision 1").And.Contain("not approved yet");
        cut.Find("[data-testid=pts-approve]").TextContent.Should().Be("Approve revision 1");
        StepState(cut, 6).Should().Be(nameof(SetupStepState.NotStarted), "modules filter only by an approved revision");
        cut.FindAll("[data-testid=pts-use-link]").Select(l => l.GetAttribute("data-behaviour")).Should().OnlyContain(b => b == "context", "a draft is never offered as a filter");
        cut.FindAll("[data-testid=pts-use-pending]").Should().NotBeEmpty();

        cut.Find("[data-testid=pts-next-action-go]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-scope-detail]"));
        Nav.Uri.Should().Contain("view=areas").And.Contain($"scope={draft.Scope.Id}");
    }

    [Fact]
    public async Task A_clean_draft_recommends_continue_editing_which_opens_the_editor()
    {
        _stored.Add(Detail("Report intake", ScopePurpose.Test, [new() { Kind = ScopeMemberKind.Scenario, TargetId = "Scenario|s1", Label = "Valid report", Provenance = "Selected by a person" }]));
        await Setup();
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-next-action]").GetAttribute("data-action").Should().Be("continue"));
        StepState(cut, 4).Should().Be(nameof(SetupStepState.Available));
        StepState(cut, 5).Should().Be(nameof(SetupStepState.Completed));
        cut.Find("[data-testid=pts-next-action-go]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-editor]"));
        Nav.Uri.Should().Contain("view=areas");
    }

    [Fact]
    public async Task An_approved_test_area_links_only_to_modules_that_read_scope_with_honest_behaviour_labels()
    {
        var approved = Approve(Detail("Report intake", ScopePurpose.Test,
        [
            new() { Kind = ScopeMemberKind.Scenario, TargetId = "Scenario|s1", Label = "Valid report", Provenance = "Selected by a person" },
            new() { Kind = ScopeMemberKind.Journey, TargetId = "j-1", Label = "Report journey", Provenance = "Selected by a person" },
        ]));
        _stored.Add(approved);
        await Setup();
        var cut = Render<ProjectScopes>();
        var id = approved.Scope.Id.ToString();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-next-action]").GetAttribute("data-action").Should().Be("plan"));
        cut.Find("[data-testid=pts-next-action-go]").GetAttribute("href").Should().Be($"test-plan-suggestions?scope={id}");
        cut.Find("[data-testid=pts-next-secondary]").GetAttribute("href").Should().Be($"test-coverage-review?scope={id}");
        StepState(cut, 4).Should().Be(nameof(SetupStepState.Completed));
        StepState(cut, 6).Should().Be(nameof(SetupStepState.Available));
        cut.Find("[data-testid=pts-saved-revision]").TextContent.Should().Contain("Saved — revision 1, approved");

        var links = cut.FindAll("[data-testid=pts-use-link]").ToDictionary(l => l.GetAttribute("data-module")!, l => (Href: l.GetAttribute("href"), Behaviour: l.GetAttribute("data-behaviour")));
        links.Should().BeEquivalentTo(new Dictionary<string, (string?, string?)>
        {
            ["test-plan-suggestions"] = ($"test-plan-suggestions?scope={id}", "filters"),
            ["test-coverage-review"] = ($"test-coverage-review?scope={id}", "filters"),
            ["test-data"] = ($"test-data?scope={id}", "filters"),
            ["critical-e2e-regression"] = ($"critical-e2e-regression?scope={id}", "filters"),
            ["implementation-review"] = ($"implementation-review?scope={id}", "filters"),
            ["delivery-reporting"] = ($"delivery-reporting?tab=scope&scope={id}", "filters"),
            ["correlation-review"] = ("correlation-review", "context"),
            ["integration-journeys"] = ("integration-journeys", "context"),
        });
        cut.FindAll("[data-testid=pts-use-pending]").Should().BeEmpty();
    }

    [Fact]
    public async Task A_newer_unapproved_revision_is_shown_as_stale_against_the_approved_one()
    {
        var approved = Approve(Detail("Report intake", ScopePurpose.Test, [new() { Kind = ScopeMemberKind.Scenario, TargetId = "Scenario|s1", Label = "Valid report", Provenance = "Selected by a person" }]));
        var stale = approved with
        {
            Scope = approved.Scope with { CurrentRevision = 2, Version = 3 },
            Revisions = [new ScopeRevisionView { Number = 2, State = ScopeRevisionState.Draft, Members = approved.Revisions[0].Members }, approved.Revisions[0]],
        };
        _stored.Add(stale);
        await Setup();
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-stale-revision]").TextContent.Should().Contain("approved revision 1"));
        cut.Find("[data-testid=pts-saved-revision]").TextContent.Should().Contain("Modules still use approved revision 1");
        cut.Find("[data-testid=pts-next-action]").GetAttribute("data-action").Should().Be("continue");
        cut.Find("[data-testid=pts-approve]").TextContent.Should().Be("Approve revision 2");
    }

    // ── Create test area ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_suggestion_proposes_members_from_evidence_and_only_kept_items_are_saved_as_confirmed()
    {
        _catalog = (kind, scope) => kind switch
        {
            ScopeMemberKind.Requirement =>
            [
                new ScopeCatalogItem { Kind = kind, TargetId = "Requirement|skole/001/fr-001", Label = "FR-001: Receive the report", Description = "skole/001/FR-001", SourceModule = "Canonical interpretation", CatalogScope = scope },
                new ScopeCatalogItem { Kind = kind, TargetId = "Requirement|skole/001/fr-002", Label = "FR-002: Reject a late report", Description = "skole/001/FR-002", SourceModule = "Canonical interpretation", CatalogScope = scope },
            ],
            ScopeMemberKind.Scenario => [new ScopeCatalogItem { Kind = kind, TargetId = "Scenario|s9", Label = "Valid report is accepted", SourceModule = "Canonical interpretation", CatalogScope = scope }],
            _ => [],
        };
        await Setup();
        _scopes.Setup(s => s.VerifyingScenariosAsync(It.IsAny<ScopeScenarioLinkRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(([new ScopeScenarioLink("Requirement|skole/001/fr-001", "Scenario|s9", "explicit")], (string?)null));
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-create]").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-suggestion][data-id='path:skole/001']"));
        cut.FindAll("[data-testid=pts-suggestion]").Should().ContainSingle("only the requirement group has a module/feature path; the scenario's id has none");
        var suggestion = cut.Find("[data-testid=pts-suggestion]");
        suggestion.TextContent.Should().Contain("skole / 001").And.Contain("2 requirements");
        cut.Find("[data-testid=pts-suggestion-provenance]").TextContent.Should().Contain("specification");

        cut.Find("[data-testid=pts-suggestion-use]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pts-suggested-member]").Should().HaveCount(3, "the scenario verifying FR-001 through an established relation is proposed too"));
        cut.FindAll("[data-testid=pts-suggested-member] [data-testid=pts-member-state]").Select(s => s.GetAttribute("data-state")).Should().OnlyContain(s => s == nameof(AreaMemberState.SuggestedFromSource));
        cut.Find("[data-testid=pts-selected-count]").TextContent.Should().Be("0", "a suggestion is not membership");
        cut.Find("[data-testid=pts-suggested-member][data-key='Requirement:Requirement|skole/001/fr-001'] [data-testid=pts-keep]").Click();
        cut.Find("[data-testid=pts-selected-count]").TextContent.Should().Be("1");
        cut.Find("[data-testid=pts-selected] [data-testid=pts-member-state]").GetAttribute("data-state").Should().Be(nameof(AreaMemberState.HumanConfirmed));
        cut.Find("[data-testid=pts-next]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-name]").GetAttribute("value").Should().Be("skole / 001", "the name is prefilled from the suggestion"));
        cut.Find("[data-testid=pts-next]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-review-not-kept]").TextContent.Should().Contain("2 suggestions were not kept"));
        cut.Find("[data-testid=pts-save]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-saved-revision]").TextContent.Should().Contain("revision 1"));
        _scopes.Verify(s => s.CreateAsync(It.Is<CreateScopeRequest>(r => r.ProjectKey == Project && r.Name == "skole / 001" && r.Members.Count == 1
            && r.Members[0].TargetId == "Requirement|skole/001/fr-001" && r.Members[0].Inclusion == ScopeMemberInclusion.Confirmed
            && r.Members[0].Provenance.StartsWith("Suggested from")), It.IsAny<CancellationToken>()), Times.Once);
        Nav.Uri.Should().Contain($"scope={_stored[0].Scope.Id}").And.NotContain("view=create");
    }

    [Fact]
    public async Task A_test_area_with_only_a_scenario_and_a_requirement_is_created_from_catalogs_without_any_system()
    {
        await Setup();
        Go("?view=create");
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-create-custom]").Click());

        cut.WaitForAssertion(() => cut.Find("[data-testid=smp-kind][data-kind=Scenario]").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid=smp-pick]").GetAttribute("data-id").Should().Be("Scenario|system-understanding/skole/s1"));
        cut.Find("[data-testid=smp-pick]").Change(true);
        cut.Find("[data-testid=smp-kind][data-kind=Requirement]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=smp-pick]").GetAttribute("data-id").Should().Be("Requirement|skole/001/fr-001"));
        cut.Find("[data-testid=smp-pick]").Change(true);
        cut.Find("[data-testid=pts-selected-count]").TextContent.Should().Be("2");
        cut.Find("[data-testid=pts-selected]").TextContent.Should().NotContain("Scenario|system-understanding", "ids are not shown as names").And.Contain("Valid report is accepted");
        cut.FindAll("[data-testid=pts-selected] [data-testid=pts-member-state]").Select(s => s.GetAttribute("data-state")).Should().OnlyContain(s => s == nameof(AreaMemberState.ExplicitlySelected));
        cut.Find("[data-testid=smp]").TextContent.Should().NotContain("Requirement|skole");

        cut.Find("[data-testid=pts-next]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-details]"));
        cut.Find("[data-testid=pts-next]").Click();
        cut.Find("[data-testid=pts-name-error]").TextContent.Should().Contain("name");
        cut.Find("[data-testid=pts-name]").GetAttribute("aria-invalid").Should().Be("true");
        cut.Find("[data-testid=pts-name]").Input("Report intake");
        cut.Find("[data-testid=pts-next]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-review]").TextContent.Should().Contain("Creates draft revision 1"));
        cut.Find("[data-testid=pts-save]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-notice]").TextContent.Should().Contain("revision 1"));
        _scopes.Verify(s => s.CreateAsync(It.Is<CreateScopeRequest>(r => r.ProjectKey == Project && r.CatalogScope == "dev" && r.Members.Count == 2
            && r.Members.All(m => m.Kind != ScopeMemberKind.System) && r.Members.All(m => m.CatalogScope == "dev")
            && r.Members.All(m => m.Inclusion == ScopeMemberInclusion.Confirmed)), It.IsAny<CancellationToken>()), Times.Once);
        cut.Find("[data-testid=pts-saved-revision]").TextContent.Should().Contain("Saved — revision 1");
        cut.Find("[data-testid=pts-approve]").TextContent.Should().Be("Approve revision 1");

        Go($"?view=areas&scope={_stored[0].Scope.Id}");
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-scope-detail]").GetAttribute("data-state").Should().Be("Draft"));
    }

    [Fact]
    public async Task A_mixed_test_area_started_from_a_system_saves_exactly_the_selected_members()
    {
        await Setup();
        Go("?view=systems");
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-system][data-key='System|person'] [data-testid=psc-system-open]").Click());
        cut.Find("[data-testid=psc-add-to-new-scope]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-selected-count]").TextContent.Should().Be("1"));
        Nav.Uri.Should().Contain("view=create").And.Contain("step=select");
        cut.Find("[data-testid=smp-kind][data-kind=Scenario]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=smp-pick]"));
        cut.Find("[data-testid=smp-pick]").Change(true);
        cut.Find("[data-testid=pts-member-remove]").Should().NotBeNull();
        cut.Find("[data-testid=pts-next]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-name]").Input("Person flow"));
        cut.Find("[data-testid=pts-next]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-save]").Click());

        cut.WaitForAssertion(() => _stored.Should().ContainSingle());
        _scopes.Verify(s => s.CreateAsync(It.Is<CreateScopeRequest>(r => r.Members.Select(m => m.Key).OrderBy(k => k).SequenceEqual(new[] { "Scenario:Scenario|system-understanding/skole/s1", "System:System|person" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Without_evidence_no_suggestion_is_shown_and_a_custom_test_area_is_still_offered()
    {
        _catalog = (_, _) => [];
        await Setup();
        Go("?view=create");
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-no-suggestions]"));
        cut.FindAll("[data-testid=pts-suggestion]").Should().BeEmpty();
        cut.Find("[data-testid=pts-create-custom]").Should().NotBeNull();
        cut.Find("[data-testid=pts-back]").GetAttribute("href").Should().Be("project-scopes");
    }

    // ── URL state and project isolation ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Views_live_in_the_url_and_old_tab_links_open_the_matching_view()
    {
        await Setup();
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-view-systems]").Click());
        Nav.Uri.Should().EndWith("project-scopes?view=systems");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=psc-system]").Should().HaveCount(6));
        cut.Find("[data-testid=pts-nav][data-view=systems]").GetAttribute("aria-current").Should().Be("page");

        Go("");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pts-step]").Should().HaveCount(6, "Back returns to the guided steps"));
        Go("?tab=scopes");
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-scopes-empty]"));
        cut.Find("[data-testid=pts-page]").GetAttribute("data-view").Should().Be("areas");
        Go("?tab=connections");
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-page]").GetAttribute("data-view").Should().Be("connections"));
    }

    [Fact]
    public async Task Switching_project_never_reuses_a_test_area_of_the_previous_project()
    {
        var area = Detail("Report intake", ScopePurpose.Test, [new() { Kind = ScopeMemberKind.Scenario, TargetId = "Scenario|s1", Label = "Valid report", Provenance = "Selected by a person" }]);
        _stored.Add(area);
        await Setup();
        Go($"?scope={area.Scope.Id}");
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pts-saved-revision]").TextContent.Should().Contain("Report intake"));

        _snapshot = WorkspaceSnapshots.Loaded("Imported", OtherProject, "Other");
        _workspace.Raise(w => w.Changed += null);
        cut.WaitForAssertion(() => cut.Find("[data-testid=psc-project]").TextContent.Should().Be("Other"));
        cut.WaitForAssertion(() => StepState(cut, 3).Should().Be(nameof(SetupStepState.Current)));
        cut.FindAll("[data-testid=pts-saved-revision]").Should().BeEmpty();
        cut.Markup.Should().NotContain("Report intake");
        Nav.Uri.Should().NotContain(area.Scope.Id.ToString(), "the other project's test area is dropped from the URL");
        _scopes.Verify(s => s.GetAsync(OtherProject, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        cut.Find("[data-testid=pts-next-action]").GetAttribute("data-action").Should().Be("create");
    }

    [Fact]
    public async Task A_link_to_a_test_area_of_another_project_is_dropped_on_load_and_never_shown()
    {
        await Setup();
        var foreign = Guid.NewGuid();
        Go($"?scope={foreign}");
        var cut = Render<ProjectScopes>();
        cut.WaitForAssertion(() => Nav.Uri.Should().NotContain(foreign.ToString(), "an id that is not one of this project's test areas is removed from the URL"));
        cut.FindAll("[data-testid=pts-saved-revision]").Should().BeEmpty();
        _scopes.Verify(s => s.GetAsync(It.IsAny<string>(), foreign, It.IsAny<CancellationToken>()), Times.Never);
    }
}
