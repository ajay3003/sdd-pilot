using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>The explorer's URL state: compact (defaults left out), a lossless round-trip, stale values dropped with a note, and the session store.</summary>
public sealed class DeliveryExplorerStateTests
{
    [Fact]
    public void State_round_trips_through_compact_url_keys_and_defaults_are_left_out()
    {
        var f = new DeliveryExplorerFilter("list", "dashboard", "Frontend", "Task", "changed", true, "title", true);
        var query = DeliveryExplorerState.ToQuery(f, "hierarchy");
        query.Should().ContainKeys(DeliveryExplorerState.QueryKeys);
        var back = DeliveryExplorerState.FromQuery(query.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()));
        back.Should().Be(f);

        DeliveryExplorerState.ToQuery(new DeliveryExplorerFilter("hierarchy"), "hierarchy").Values.Should().OnlyContain(v => v == null, "the default state adds nothing to the URL");
        DeliveryExplorerState.HasAny(new Dictionary<string, string?> { ["item"] = "f:x" }).Should().BeFalse();
        DeliveryExplorerState.HasAny(new Dictionary<string, string?> { ["wi-q"] = "x" }).Should().BeTrue();
    }

    [Fact]
    public void Stale_or_incompatible_values_are_dropped_with_a_note()
    {
        var (current, notes) = DeliveryExplorerState.Validate(new DeliveryExplorerFilter("board", "x", "Gone", "Epic", "changed", false, "bogus"),
            DeliveryExplorerOptions.CurrentState, ["Frontend"]);
        current.Should().Be(new DeliveryExplorerFilter("hierarchy", "x", "", "", "", false, "id"));
        notes.Should().HaveCount(4).And.Contain(n => n.Contains("Gone")).And.Contain(n => n.Contains("comparisons only"));

        var (comparison, none) = DeliveryExplorerState.Validate(new DeliveryExplorerFilter("board", "", "Frontend", "Task", "changed"), DeliveryExplorerOptions.Comparison, ["Frontend"]);
        comparison.View.Should().Be("board");
        comparison.Change.Should().Be("changed");
        none.Should().BeEmpty();
    }

    [Fact]
    public void Query_parsing_decodes_values_and_the_store_keeps_only_set_keys_per_key()
    {
        var q = DeliveryExplorerState.Parse("https://x/delivery-reporting?project=1&wi-q=a%20b&item=f%3Afrontend%3A%3A001#top");
        q["wi-q"].Should().Be("a b");
        q["item"].Should().Be("f:frontend::001");

        var store = new ExplorerViewStateStore();
        store.Set("k", new Dictionary<string, object?> { ["wi-q"] = "x", ["wi-type"] = null });
        store.Get("k").Should().BeEquivalentTo(new Dictionary<string, string?> { ["wi-q"] = "x" });
        store.Get("other").Should().BeNull("another project or report never shares state");
        store.Set("k", new Dictionary<string, object?> { ["wi-q"] = null });
        store.Get("k").Should().BeNull();
    }
}
