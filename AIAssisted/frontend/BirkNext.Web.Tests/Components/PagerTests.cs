using BirkNext.Web.Shared.Components.Results;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace BirkNext.Web.Tests.Components;

/// <summary>Numbered pagination for large tables (2026-11-26): honest counts, a compact page window, previous/next, the current page marked
/// for screen readers, nothing for one page or an unknown total.</summary>
public sealed class PagerTests : BunitContext
{
    private IRenderedComponent<Pager> Render(int page, int? total, Action<int>? onPage = null) =>
        Render<Pager>(p => p.Add(x => x.PageIndex, page).Add(x => x.PageSize, 20).Add(x => x.Total, total).Add(x => x.Noun, "rader").Add(x => x.TestId, "pg")
            .Add(x => x.OnPage, EventCallback.Factory.Create<int>(this, i => onPage?.Invoke(i))));

    [Fact]
    public void It_says_which_rows_of_how_many_are_shown_and_which_page_this_is()
    {
        var cut = Render(1, 248);
        cut.Find("[data-testid=pg-count]").TextContent.Should().Be("Viser 21–40 av 248 rader · Side 2 av 13");
        cut.Find("[data-testid=pg-page][aria-current=page]").GetAttribute("data-page").Should().Be("2");
    }

    [Fact]
    public void The_window_is_compact_with_first_last_and_neighbours()
    {
        Pager.Window(6, 13).Should().Equal([0, -1, 5, 6, 7, -1, 12]);
        Pager.Window(0, 13).Should().Equal([0, 1, -1, 12]);
        Pager.Window(12, 13).Should().Equal([0, -1, 11, 12]);
        Pager.Window(1, 3).Should().Equal([0, 1, 2]);
        var cut = Render(6, 248);
        cut.FindAll("[data-testid=pg-page]").Select(b => b.GetAttribute("data-page")).Should().Equal(["1", "6", "7", "8", "13"], "never one button per page");
    }

    [Fact]
    public void Previous_next_and_a_page_number_move_and_the_ends_are_disabled()
    {
        var asked = new List<int>();
        var cut = Render(0, 248, asked.Add);
        cut.Find("[data-testid=pg-prev]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=pg-next]").Click();
        cut.Find("[data-testid=pg-page][data-page='13']").Click();
        asked.Should().Equal([1, 12]);
        Render(12, 248).Find("[data-testid=pg-next]").HasAttribute("disabled").Should().BeTrue();
        Render(12, 248).Find("[data-testid=pg-count]").TextContent.Should().StartWith("Viser 241–248 av 248");
    }

    [Fact]
    public void One_page_or_an_unknown_total_shows_no_pager()
    {
        Render(0, 20).FindAll("[data-testid=pg]").Should().BeEmpty();
        Render(0, null).FindAll("[data-testid=pg]").Should().BeEmpty("no page count is invented");
    }
}
