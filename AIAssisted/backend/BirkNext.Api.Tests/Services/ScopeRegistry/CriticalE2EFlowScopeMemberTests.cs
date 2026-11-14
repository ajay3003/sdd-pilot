using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Data.ScopeRegistry;
using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.ScopeRegistry;
using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using BirkNext.Scopes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.ScopeRegistry;

/// <summary>
/// Critical E2E flows as scope members: the catalog lists the flow definitions of the active Target Environment by their flow id, describes
/// module and classification (critical vs smoke/diagnostic), and a flow id the store does not have — or one from another environment — is refused.
/// </summary>
public sealed class CriticalE2EFlowScopeMemberTests
{
    const string Env = "env-m2lb";

    sealed class NoSources : ICanonicalSourceReader
    {
        public Task<IqrSourceSnapshot?> FindAsync(Guid snapshotId, CancellationToken ct) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    sealed class FakeFlows(params CriticalE2EFlowDefinition[] flows) : ICriticalE2EStore
    {
        public IReadOnlyList<CriticalE2EFlowDefinition> Flows(string environmentId) => [.. flows.Where(f => f.EnvironmentId == environmentId)];
        public CriticalE2EFlowDefinition? Flow(string flowId) => flows.FirstOrDefault(f => f.Id == flowId);
        public CriticalE2EFlowDefinition Save(CriticalE2EFlowDefinition flow) => throw new InvalidOperationException("a catalog never writes flows");
        public bool Delete(string flowId) => throw new InvalidOperationException("a catalog never deletes flows");
        public IReadOnlyList<CriticalE2ERunResult> History(string environmentId, int limit = 100) => [];
        public void Record(CriticalE2ERunResult result) => throw new InvalidOperationException("a catalog never records runs");
        public IReadOnlyList<CriticalE2ERunResult> FlowHistory(string flowId) => [];
        public int SetArchived(string flowId, IReadOnlyCollection<string> runIds, DateTimeOffset? archivedAt, string? reason) => 0;
        public int RemoveRuns(string flowId, IReadOnlyCollection<string> runIds) => 0;
    }

    static readonly CriticalE2EFlowDefinition Report = new()
    {
        Id = "flow-report", Module = "M01", Name = "Submit attendance report", Kind = CriticalE2EFlowKind.Critical, EnvironmentId = Env, ProfileId = Env,
        RequiredForRelease = true, Mode = CriticalE2EExecutionMode.CompanionBrowser,
    };
    static readonly CriticalE2EFlowDefinition Smoke = new()
    {
        Id = "flow-smoke", Module = "M01", Name = "Report page opens", Kind = CriticalE2EFlowKind.Diagnostic, EnvironmentId = Env, ProfileId = Env,
        Source = CriticalE2EFlowSource.SuggestedFromSource, Mode = CriticalE2EExecutionMode.CompanionBrowser,
    };
    static readonly CriticalE2EFlowDefinition Elsewhere = new()
    {
        Id = "flow-other-env", Module = "M02", Name = "Other environment flow", EnvironmentId = "env-other", ProfileId = "env-other",
    };

    static ScopeMemberCatalogService Catalog(ICriticalE2EStore? store)
    {
        var db = new CanonicalInterpretationDbContext(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase($"e2e-scope-{Guid.NewGuid():N}").Options);
        return new ScopeMemberCatalogService(new CanonicalInterpretationService(db, new NoSources(), TimeProvider.System), criticalE2E: store);
    }

    static ScopeMember M(string id, string? catalogScope = null) => new() { Kind = ScopeMemberKind.CriticalE2EFlow, TargetId = id, Label = id, CatalogScope = catalogScope };

    [Fact]
    public async Task Search_lists_the_environments_flows_by_flow_id_with_module_and_classification()
    {
        var catalog = Catalog(new FakeFlows(Report, Smoke, Elsewhere));
        ScopeMemberCatalogService.HasCatalog(ScopeMemberKind.CriticalE2EFlow).Should().BeTrue();

        var page = await catalog.SearchAsync(ScopeMemberKind.CriticalE2EFlow, Env, null, 0, 25, default);
        page.Available.Should().BeTrue();
        page.Items.Select(i => i.TargetId).Should().Equal("flow-report", "flow-smoke");
        page.Items.Should().OnlyContain(i => i.CatalogScope == Env && i.SourceModule == ScopeMemberCatalogService.CriticalE2EModule);
        var report = page.Items.Single(i => i.TargetId == "flow-report");
        report.Label.Should().Be("Submit attendance report");
        report.Description.Should().Contain("M01").And.Contain("Critical flow");
        report.State.Should().Be("Required for release");
        page.Items.Single(i => i.TargetId == "flow-smoke").Description.Should().Contain("Smoke / diagnostic").And.Contain("never Business E2E");

        (await catalog.SearchAsync(ScopeMemberKind.CriticalE2EFlow, Env, "opens", 0, 25, default)).Items.Single().TargetId.Should().Be("flow-smoke");
        (await catalog.SearchAsync(ScopeMemberKind.CriticalE2EFlow, null, null, 0, 25, default)).Notices.Single().Should().Contain("active Target Environment");
        (await Catalog(null).SearchAsync(ScopeMemberKind.CriticalE2EFlow, Env, null, 0, 25, default)).Available.Should().BeFalse();
    }

    [Fact]
    public async Task Check_confirms_known_flow_ids_and_refuses_unknown_or_other_environment_ids()
    {
        var catalog = Catalog(new FakeFlows(Report, Smoke, Elsewhere));
        var statuses = await catalog.CheckAsync([M("flow-report"), M("flow-invented"), M("flow-other-env"), M("flow-smoke", "env-other")], Env, default);

        var known = statuses.Single(s => s.Key == "CriticalE2EFlow:flow-report");
        (known.Check, known.CurrentLabel).Should().Be((ScopeMemberCheck.Current, "Submit attendance report"));
        statuses.Single(s => s.Key == "CriticalE2EFlow:flow-invented").Check.Should().Be(ScopeMemberCheck.Missing);
        statuses.Single(s => s.Key == "CriticalE2EFlow:flow-other-env").Check.Should().Be(ScopeMemberCheck.Missing, "a flow of another environment is not in this catalog");
        statuses.Single(s => s.Key == "CriticalE2EFlow:flow-smoke").Check.Should().Be(ScopeMemberCheck.OtherContext);
        (await Catalog(null).CheckAsync([M("flow-report")], Env, default)).Single().Check.Should().Be(ScopeMemberCheck.NotCheckable);
    }

    [Fact]
    public async Task A_scope_accepts_a_known_flow_records_its_environment_and_refuses_an_unknown_id()
    {
        var registry = new ScopeRegistryService(new ScopeRegistryDbContext(new DbContextOptionsBuilder<ScopeRegistryDbContext>()
            .UseInMemoryDatabase($"scopes-{Guid.NewGuid():N}").Options), TimeProvider.System, Catalog(new FakeFlows(Report, Smoke)));

        var refused = await registry.CreateAsync(new CreateScopeRequest("import:m2lb", "Invented", null, ScopePurpose.IntegrationE2E, null, [M("flow-invented")], Env), "tester");
        refused.Error.Should().Contain("Nothing was saved");

        var created = await registry.CreateAsync(new CreateScopeRequest("import:m2lb", "Report release", null, ScopePurpose.ReleaseDelivery, null, [M("flow-report")], Env), "tester");
        created.Error.Should().BeNull();
        created.Scope!.Revisions.Single().Members.Single().CatalogScope.Should().Be(Env, "a flow id only means something in its Target Environment");
    }
}
