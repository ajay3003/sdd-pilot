using BirkNext.TestPlanning;
using FluentAssertions;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// The scenario population is never cut (2026-11-26): discovery keeps and stores every candidate (source-backed first); a result sent to a client
/// holds a window of <see cref="D.MaxCandidates"/> and says how many it did not send; the rest is searched on the server, so a scenario at
/// position 12,000 is found without loading the 11,999 before it.
/// </summary>
public sealed class DiscoveryCapTests
{
    private static DiscoveredScenario C(string id, string capability, string? title = null) => new()
    {
        CandidateId = id, ProjectKey = "p", CapabilityId = capability, SourceCategory = capability, Title = title ?? id, CanonicalKey = $"{capability}:{id}", SourceIdentity = "s-" + id,
    };

    private static ScenarioDiscoveryResult Result(List<DiscoveredScenario> candidates) => new() { DiscoveryId = "d", ProjectKey = "p", Candidates = candidates };

    /// <summary>12,000 candidates; derived API scenarios first in provider order, the specification's own scenarios last (what a cap would cut).</summary>
    private static List<DiscoveredScenario> TwelveThousand() =>
        [.. Enumerable.Range(1, 11_997).Select(i => C($"api{i}", D.ApiContract, $"Operation {i:00000} returns its resource")),
         .. Enumerable.Range(1, 3).Select(i => C($"spec{i}", D.Specification, $"Specification scenario {i}"))];

    [Fact]
    public void Discovery_keeps_the_complete_population_in_source_priority_order()
    {
        var kept = D.Finalize("p", "snap", TwelveThousand(), []);
        kept.Should().HaveCount(12_000, "nothing is cut when a result is computed and stored");
        kept.Select(c => c.CandidateId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_sent_result_is_a_window_that_says_what_it_does_not_hold_and_keeps_source_backed_scenarios()
    {
        var sent = D.Window(Result(D.Finalize("p", "snap", TwelveThousand(), [])));
        sent.Candidates.Should().HaveCount(D.MaxCandidates);
        sent.Candidates.Where(c => c.CapabilityId == D.Specification).Should().HaveCount(3);
        sent.NotKept.Should().Be(2_000);
        sent.Limitations.Should().ContainSingle(l => l.Contains("not sent at once") && l.Contains("search finds them on the server"));
    }

    [Fact]
    public void A_result_within_the_window_is_sent_unchanged()
    {
        var small = Result(D.Finalize("p", "snap", [.. Enumerable.Range(0, 2015).Select(i => C($"c{i}", D.Specification))], []));
        D.Window(small).Should().BeSameAs(small);
        D.Window(small).NotKept.Should().BeNull();
    }

    [Fact]
    public void Server_search_finds_the_scenario_at_position_12000_without_the_ones_before_it_and_pages_deterministically()
    {
        var stored = Result(D.Finalize("p", "snap", TwelveThousand(), []));
        // Position 12,000 in the window order is the last API scenario (the three specification scenarios come first).
        var last = stored.Candidates.Single(c => c.CandidateId == "api11997");
        var hit = D.Search(stored, last.Title, D.MaxCandidates, 0, 40);
        hit.Total.Should().Be(1);
        hit.Items.Single().CandidateId.Should().Be(last.CandidateId);

        // Every word must match; case is ignored; matches inside the window are not repeated (the client already has them).
        D.Search(stored, "OPERATION 11997 returns", D.MaxCandidates, 0, 40).Items.Single().CandidateId.Should().Be("api11997");
        D.Search(stored, "Specification scenario", D.MaxCandidates, 0, 40).Total.Should().Be(0, "those are in the window the client holds");

        // Paging: stable order, no duplicates, no gaps.
        var all = D.Search(stored, "Operation", D.MaxCandidates, 0, 200);
        all.Total.Should().Be(2_000);
        var pages = Enumerable.Range(0, 10).SelectMany(p => D.Search(stored, "Operation", D.MaxCandidates, p * 200, 200).Items).Select(c => c.CandidateId).ToList();
        pages.Should().OnlyHaveUniqueItems().And.HaveCount(2_000);
        D.Search(stored, "Operation", D.MaxCandidates, 1_960, 40).HasMore.Should().BeFalse();
        D.Search(stored, "", D.MaxCandidates, 0, 40).Total.Should().Be(0, "an empty query returns nothing, never everything");
    }
}
