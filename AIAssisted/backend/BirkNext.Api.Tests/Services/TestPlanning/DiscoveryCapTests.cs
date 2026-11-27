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
        sent.NotKept.Should().Be(12_000 - D.MaxCandidates);
        sent.TotalCandidates.Should().Be(12_000, "counts over the whole population stay honest");
        sent.Limitations.Should().ContainSingle(l => l.Contains("not sent at once") && l.Contains("search finds them on the server"));
    }

    [Fact]
    public void A_result_within_the_window_is_sent_unchanged()
    {
        var small = Result(D.Finalize("p", "snap", [.. Enumerable.Range(0, D.MaxCandidates).Select(i => C($"c{i}", D.Specification))], []));
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
        var beyond = 12_000 - D.MaxCandidates;
        all.Total.Should().Be(beyond);
        var pages = Enumerable.Range(0, beyond / 200).SelectMany(p => D.Search(stored, "Operation", D.MaxCandidates, p * 200, 200).Items).Select(c => c.CandidateId).ToList();
        pages.Should().OnlyHaveUniqueItems().And.HaveCount(beyond);
        D.Search(stored, "Operation", D.MaxCandidates, beyond - 40, 40).HasMore.Should().BeFalse();
        D.Search(stored, "", D.MaxCandidates, 0, 40).Total.Should().Be(0, "an empty query returns nothing, never everything");
    }

    [Fact]
    public void The_window_is_two_thousand_so_the_first_load_stays_small()
    {
        D.MaxCandidates.Should().Be(2_000, "server search and Pick reach everything beyond it (2026-11-27; 10,000 synthetic scenarios were 15.7 MB)");
    }

    /// <summary>Scenarios with areas, stories, requirements and priorities, all beyond a window of the three specification scenarios.</summary>
    private static List<DiscoveredScenario> Filtered()
    {
        var story = new BirkNext.TestCoverage.CoverageUserStory { Key = "us-7", StoryId = "US7", Title = "Arkiv", Priority = "P1", Ordinal = 7 };
        return [.. Enumerable.Range(1, 3).Select(i => C($"spec{i}", D.Specification, $"Spesifikasjon {i}")),
            C("a1", D.ApiContract, "Henter arkivert rapport") with { Area = "Arkiv", RequirementIds = ["FR-900"], Story = story },
            C("a2", D.ApiContract, "Henter aktiv rapport") with { Area = "Rapporter", RequirementIds = ["FR-100"] },
            C("a3", D.ApiContract, "Sletter arkivert rapport") with { Area = "Arkiv", Type = ScenarioCandidateType.Integration }];
    }

    [Fact]
    public void Server_search_applies_the_pages_scenario_filters_and_a_filter_alone_is_a_search()
    {
        var stored = Result(Filtered());
        D.Search(stored, "", 3, 0, 40, new(Area: "Arkiv")).Items.Select(c => c.CandidateId).Should().Equal("a1", "a3");
        D.Search(stored, "Henter", 3, 0, 40, new(Area: "Arkiv")).Items.Select(c => c.CandidateId).Should().Equal("a1");
        D.Search(stored, "", 3, 0, 40, new(Requirement: "fr-900")).Items.Select(c => c.CandidateId).Should().Equal("a1");
        D.Search(stored, "", 3, 0, 40, new(Story: "us-7")).Items.Select(c => c.CandidateId).Should().Equal("a1");
        D.Search(stored, "", 3, 0, 40, new(Story: ScenarioSearchFilter.NoStory)).Items.Select(c => c.CandidateId).Should().Equal("a2", "a3");
        D.Search(stored, "", 3, 0, 40, new(Priority: "p1")).Items.Select(c => c.CandidateId).Should().Equal("a1");
        D.Search(stored, "", 3, 0, 40, new(Priority: ScenarioSearchFilter.NoPriority)).Items.Select(c => c.CandidateId).Should().Equal("a2", "a3");
        D.Search(stored, "", 3, 0, 40, new(Type: ScenarioCandidateTypes.Label(ScenarioCandidateType.Integration))).Items.Select(c => c.CandidateId).Should().Equal("a3");
        D.Search(stored, "", 3, 0, 40, new(TestLevel: nameof(ScenarioTestLevel.Integration))).Items.Select(c => c.CandidateId).Should().Equal("a3");
        D.Search(stored, "", 3, 0, 40, new()).Total.Should().Be(0, "no words and no filter: nothing, never everything");
        D.Search(stored, "", 3, 0, 40, new(Area: "Rapporter")).Items.Select(c => c.CandidateId).Should().Equal(["a2"], "a scenario inside the window is never repeated");
    }

    [Fact]
    public void A_window_names_the_filter_values_and_clarification_count_of_what_it_does_not_send()
    {
        var all = Filtered().Select(c => c with { ExpectedBehaviour = "Vises" }).ToList();
        all[4] = all[4] with { ExpectedBehaviour = null }; // no expected result → needs clarification
        var sent = D.Window(Result(all), 3);
        sent.NotKept.Should().Be(3);
        sent.NotKeptNeedsClarification.Should().Be(1);
        sent.TotalNeedsClarification.Should().Be(1);
        var f = sent.NotKeptFacets!;
        f.Areas.Should().Equal("Arkiv", "Rapporter");
        f.RequirementIds.Should().Equal("FR-100", "FR-900");
        f.Stories.Should().ContainSingle(st => st.Key == "us-7");
        f.Priorities.Should().Equal("P1");
        f.AnyWithoutStory.Should().BeTrue();
        f.AnyWithoutPriority.Should().BeTrue();
    }

    [Fact]
    public void Pick_returns_named_scenarios_beyond_the_window_by_candidate_id_or_source_identity()
    {
        var stored = Result(D.Finalize("p", "snap", TwelveThousand(), []));
        var far = stored.Candidates.Single(c => c.CandidateId == "api11997");
        D.Pick(stored, ["api11997", "unknown"]).Should().ContainSingle().Which.CandidateId.Should().Be("api11997");
        D.Pick(stored, [far.SourceIdentity]).Should().ContainSingle().Which.CandidateId.Should().Be("api11997");
        D.Pick(stored, Enumerable.Range(1, 500).Select(i => $"api{i}")).Should().HaveCount(200, "at most 200 ids per request");
    }
}
