using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Shared.Components.Results;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using S = BirkNext.Web.Services.ScenarioBrowserPresentation;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Progressive results (application-wide, 2026-11-26): one «Vis flere» control. A batch in memory appears at once (no spinner, no delay); a
/// fetched batch shows «Laster flere …», blocks a second click, keeps what is shown and offers «Prøv igjen» on failure; counts are honest; the
/// list's search runs on every row before batching (the acceptance test: a scenario far beyond the first rows is found at once).
/// </summary>
public sealed class ShowMoreTests : BunitContext
{
    [Fact]
    public void P01_a_list_that_fits_shows_no_button()
    {
        var cut = Render<ShowMore>(p => p.Add(x => x.Shown, 12).Add(x => x.Total, 12).Add(x => x.TestId, "sm"));
        cut.FindAll("[data-testid=sm]").Should().BeEmpty();
    }

    [Fact]
    public void P02_a_long_list_says_how_many_are_shown_and_the_next_batch_is_revealed_at_once_without_a_spinner()
    {
        var shown = 20;
        IRenderedComponent<ShowMore>? cut = null;
        cut = Render<ShowMore>(p => p.Add(x => x.Shown, shown).Add(x => x.Total, 248).Add(x => x.Step, 20).Add(x => x.Noun, "scenarioer").Add(x => x.TestId, "sm")
            .Add(x => x.OnMore, EventCallback.Factory.Create(this, () => { shown += 20; cut!.Render(p2 => p2.Add(x => x.Shown, shown)); })));

        cut.Find("[data-testid=sm-count]").TextContent.Should().Be("Viser 20 av 248 scenarioer");
        cut.Find("[data-testid=sm]").TextContent.Trim().Should().Be("Vis 20 flere");
        cut.Find("[data-testid=sm]").Click();
        cut.Find("[data-testid=sm-count]").TextContent.Should().Be("Viser 40 av 248 scenarioer");
        cut.Markup.Should().NotContain("Laster flere", "an in-memory batch has no spinner");
        cut.Find("[data-testid=sm-announce]").TextContent.Should().Be("20 flere vises.");
    }

    [Fact]
    public void P03_the_last_batch_names_only_what_remains_and_an_unknown_total_is_never_invented()
    {
        Render<ShowMore>(p => p.Add(x => x.Shown, 240).Add(x => x.Total, 248).Add(x => x.Step, 20).Add(x => x.TestId, "sm"))
            .Find("[data-testid=sm]").TextContent.Trim().Should().Be("Vis 8 flere");
        var unknown = Render<ShowMore>(p => p.Add(x => x.Shown, 40).Add(x => x.Step, 20).Add(x => x.TestId, "sm"));
        unknown.Find("[data-testid=sm-count]").TextContent.Should().Be("Viser 40");
        unknown.Find("[data-testid=sm]").TextContent.Trim().Should().Be("Vis flere");
    }

    [Fact]
    public void P04_a_fetched_batch_shows_the_spinner_blocks_a_second_click_and_a_failure_offers_retry_without_losing_rows()
    {
        var pending = new TaskCompletionSource();
        var calls = 0;
        string? error = null;
        IRenderedComponent<ShowMore>? cut = null;
        cut = Render<ShowMore>(p => p.Add(x => x.Shown, 20).Add(x => x.Total, 100).Add(x => x.TestId, "sm")
            .Add(x => x.OnMore, EventCallback.Factory.Create(this, async () => { calls++; await pending.Task; })));

        cut.Find("[data-testid=sm]").Click();
        cut.Find("[data-testid=sm]").TextContent.Should().Contain("Laster flere …");
        cut.Find("[data-testid=sm]").HasAttribute("disabled").Should().BeTrue("no duplicate request");
        calls.Should().Be(1);

        error = "Kunne ikke hente flere.";
        pending.SetResult();
        cut.Render(p => p.Add(x => x.Error, error));
        cut.WaitForAssertion(() => cut.Find("[data-testid=sm-error]").TextContent.Should().Contain("Kunne ikke hente flere."));
        cut.Find("[data-testid=sm-count]").TextContent.Should().Be("Viser 20 av 100", "the rows already shown stay");
        pending = new TaskCompletionSource();
        pending.SetResult();
        cut.Find("[data-testid=sm-retry]").Click();
        calls.Should().Be(2, "«Prøv igjen» asks for the same batch again");
    }

    [Fact]
    public void P05_search_runs_on_every_scenario_before_batching_so_one_far_beyond_the_first_rows_is_found_at_once()
    {
        // The acceptance test of the brief: 500 scenarios, search for no. 480 — found in the first batch, without clicking «Vis flere».
        var story = new CoverageUserStory { Key = "s", StoryId = "US1", Ordinal = 1, Title = "Rapport", Priority = "P1", SpecificationPath = "spec.md" };
        var candidates = Enumerable.Range(1, 500).Select(n => new DiscoveredScenario
        {
            CandidateId = $"c{n}", ProjectKey = "p", CapabilityId = "specification", SourceCategory = "specification", Area = "Rapporter", Title = $"Scenario nummer {n:000} rapport",
            Purpose = "Formål", Type = ScenarioCandidateType.BusinessFunctional, Confidence = CandidateConfidence.ExplicitSpecification, ExpectedBehaviour = "Vises",
            CanonicalKey = "k:" + n, Story = story, NumberInStory = n, SourceIdentity = "tss-" + n,
        }).ToList();
        var items = S.Items(candidates, new Dictionary<string, S.Prepared>());

        var (all, _) = S.Apply(items, new S.Filter(All: true));
        all.Count.Should().Be(500);
        var (found, _) = S.Apply(items, new S.Filter(Query: "nummer 480", All: true));
        found.Select(i => i.Candidate.CandidateId).Should().Equal(["c480"], "search covers every scenario, not the rendered batch");
        found.Take(S.PageSize).Should().ContainSingle("the match is in the first batch");
    }
}
