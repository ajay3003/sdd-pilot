using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Discovery is scoped to the selected module + specification (2026-11-25, the stale «SkoleService under HendelseAdapter» bug): a result is stamped with
/// the selection it was computed for (<see cref="DiscoveryScope"/>, from the same workspace evidence the request sends), a scoped request returns only a
/// result for exactly that selection, two selections never share a result even when their scenarios are identical, and results stored before the
/// scope existed are never returned for a scoped request. No database: the in-memory cache, the specification provider and synthetic specifications.
/// </summary>
public sealed class ScenarioDiscoveryScopeTests
{
    private readonly ScenarioDiscoveryCache _cache = new();

    private TestScenarioDiscoveryService Service() => new(new IqrSourceStore(null!), [new SpecificationScenarioProvider()], _cache);

    private static CoverageWorkspaceEvidence Spec(string module, string path, params string[] scenarios) => new()
    {
        SpecificationAvailable = true, ArtifactScope = module, SpecificationArtifacts = [new("spec-" + module, path, module, "fp-" + module)],
        AcceptanceScenarios = [.. scenarios.Select((t, i) => new QaTestCandidate { Id = $"{module}-{i}", Title = t, Given = "a report", When = "it is submitted", Then = t })],
    };

    private Task<ScenarioDiscoveryResult> Discover(CoverageWorkspaceEvidence? workspace) =>
        Service().DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "m2lb", ProjectName = "M2LB", Workspace = workspace }, default);

    [Fact]
    public void The_scope_is_the_module_and_the_selected_specification_or_all_specifications()
    {
        DiscoveryScope.Of(Spec("SkoleService", @"SkoleService\specs\001\spec.md")).Should().Be("skoleservice|skoleservice/specs/001/spec.md");
        DiscoveryScope.Of(new CoverageWorkspaceEvidence { ArtifactScope = "HendelseAdapter" }).Should().Be("hendelseadapter|*", "no feature chosen = all of the module's specifications");
        DiscoveryScope.Of(null).Should().Be("|*");
    }

    [Fact]
    public async Task A_module_switch_never_returns_the_previous_modules_scenarios()
    {
        var skole = Spec("SkoleService", "SkoleService/specs/001/spec.md", "A valid attendance report is stored");
        var hendelse = new CoverageWorkspaceEvidence { ArtifactScope = "HendelseAdapter" };
        var first = await Discover(skole);
        first.SelectionScope.Should().Be(DiscoveryScope.Of(skole));

        (await Service().LastAsync("m2lb", null, default, DiscoveryScope.Of(hendelse))).Should().BeNull("HendelseAdapter has no discovery yet: never SkoleService's");
        (await Service().LastAsync("m2lb", null, default, DiscoveryScope.Of(skole)))!.DiscoveryId.Should().Be(first.DiscoveryId);

        var second = await Discover(hendelse);
        (await Service().LastAsync("m2lb", null, default, DiscoveryScope.Of(hendelse)))!.DiscoveryId.Should().Be(second.DiscoveryId);
        (await Service().LastAsync("m2lb", null, default, DiscoveryScope.Of(skole)))!.DiscoveryId.Should().Be(first.DiscoveryId, "switching back shows SkoleService's own result, without rediscovering");
        (await Service().LastAsync("m2lb", null, default))!.DiscoveryId.Should().Be(second.DiscoveryId, "the unscoped call still returns the project's newest (older callers)");
    }

    [Fact]
    public async Task Two_selections_with_the_same_scenarios_are_still_two_results()
    {
        var a = await Discover(new CoverageWorkspaceEvidence { ArtifactScope = "Person" });
        var b = await Discover(new CoverageWorkspaceEvidence { ArtifactScope = "HendelseAdapter" });
        a.DiscoveryId.Should().NotBe(b.DiscoveryId, "the selection is part of a result's identity");
        _cache.LatestForScope("m2lb", "person|*")!.DiscoveryId.Should().Be(a.DiscoveryId);
        _cache.LatestForScope("m2lb", "hendelseadapter|*")!.DiscoveryId.Should().Be(b.DiscoveryId);
    }

    [Fact]
    public void A_result_stored_before_the_scope_existed_is_never_returned_for_a_scoped_request()
    {
        _cache.Store(new ScenarioDiscoveryResult { DiscoveryId = "old", ProjectKey = "m2lb", DiscoveredAt = DateTimeOffset.UtcNow, Basis = new DiscoverySourceBasis { Kind = DiscoveryBasisKind.Current } });
        _cache.LatestForScope("m2lb", "skoleservice|*").Should().BeNull();
        _cache.Latest("m2lb")!.DiscoveryId.Should().Be("old", "unscoped readers keep working");
    }

    [Fact]
    public async Task Repeating_a_discovery_for_the_same_selection_adds_no_second_result()
    {
        var skole = Spec("SkoleService", "SkoleService/specs/001/spec.md", "A valid attendance report is stored");
        var first = await Discover(skole);
        var again = await Discover(skole);
        again.DiscoveryId.Should().Be(first.DiscoveryId, "the same selection and sources give the same derived result (no duplicate)");
    }
}
