using BirkNext.TestPlanning;
using FluentAssertions;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>The discovery limit (2026-11-26) is a safety limit, never a working size: M2LB's 2,015 candidates are all kept, source-backed
/// scenarios are kept first when it is ever reached, and what is not kept is counted per capability (the page states NotKept).</summary>
public sealed class DiscoveryCapTests
{
    private static DiscoveredScenario C(string id, string capability) => new()
    {
        CandidateId = id, ProjectKey = "p", CapabilityId = capability, SourceCategory = capability, Title = id, CanonicalKey = $"{capability}:{id}", SourceIdentity = "s-" + id,
    };

    [Fact]
    public void The_limit_is_far_above_a_real_project()
    {
        D.MaxCandidates.Should().BeGreaterThanOrEqualTo(10000, "M2LB has 2,015 candidates; 2,000 dropped 15");
    }

    [Fact]
    public void Above_the_limit_specification_scenarios_are_kept_first_and_what_is_not_kept_is_counted()
    {
        // Derived API scenarios first in provider order, the specification's own scenarios last: the old cap would have dropped exactly those.
        var candidates = Enumerable.Range(0, D.MaxCandidates).Select(i => C($"api{i}", D.ApiContract))
            .Concat(Enumerable.Range(0, 3).Select(i => C($"spec{i}", D.Specification))).ToList();

        var kept = D.Finalize("p", "snap", candidates, []);

        kept.Should().HaveCount(D.MaxCandidates);
        kept.Where(c => c.CapabilityId == D.Specification).Should().HaveCount(3, "source-backed scenarios are never the ones cut");
        D.DroppedByCap("p", candidates).Should().BeEquivalentTo(new Dictionary<string, int> { [D.ApiContract] = 3 });
    }

    [Fact]
    public void Below_the_limit_nothing_is_dropped()
    {
        var candidates = Enumerable.Range(0, 2015).Select(i => C($"c{i}", i % 3 == 0 ? D.ApiContract : D.Specification)).ToList();
        D.Finalize("p", "snap", candidates, []).Should().HaveCount(2015);
        D.DroppedByCap("p", candidates).Should().BeEmpty();
    }
}
