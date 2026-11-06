using BirkNext.IntegrationTopology;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>Topology map graph: bounded, states drawn by line style, nothing inferred shown as resolved.</summary>
public sealed class TopologyMapPresentationTests
{
    private static TopologyConnection Edge(string from, string to, TopologyRelationshipState state, TopologyStatus status = TopologyStatus.Discovered) =>
        new() { Id = $"{from}>{to}:{state}", ProducerComponentId = from, ConsumerComponentId = to, Relationship = state, Status = status, Type = TopologyInterfaceType.Rest, Protocol = "HTTP" };

    [Fact]
    public void Many_components_are_bounded_to_the_most_connected_and_the_rest_are_counted()
    {
        var components = Enumerable.Range(0, 60).Select(i => new TopologyComponent { Id = $"c{i}", Name = $"Component {i}", Kind = TopologyComponentKind.Application }).ToList();
        var connections = Enumerable.Range(1, 59).Select(i => Edge("c0", $"c{i}", TopologyRelationshipState.Resolved)).ToList();
        var graph = TopologyMapPresentation.Build(new IntegrationTopologyView { Components = components, Connections = connections }, null, TopologyMapPresentation.SystemsLevel);
        graph.Nodes.Should().HaveCount(TopologyMapPresentation.MaxSystemNodes);
        graph.Nodes.Should().Contain(n => n.Id == "c0", "the hub is the most connected");
        graph.OmittedNodes.Should().Be(60 - TopologyMapPresentation.MaxSystemNodes);
        graph.Edges.Should().OnlyContain(e => graph.Nodes.Any(n => n.Id == e.From) && graph.Nodes.Any(n => n.Id == e.To));
    }

    [Fact]
    public void A_pair_with_only_candidate_connections_is_inferred_and_a_conflict_is_never_solid()
    {
        var view = new IntegrationTopologyView
        {
            Components = [new() { Id = "a", Name = "A" }, new() { Id = "b", Name = "B" }, new() { Id = "c", Name = "C" }],
            Connections = [Edge("a", "b", TopologyRelationshipState.Candidate), Edge("a", "b", TopologyRelationshipState.StronglySupported),
                Edge("b", "c", TopologyRelationshipState.Resolved), Edge("b", "c", TopologyRelationshipState.Candidate, TopologyStatus.Conflict)],
        };
        var graph = TopologyMapPresentation.Build(view, null, TopologyMapPresentation.SystemsLevel);
        var ab = graph.Edges.Single(e => e.From == "a");
        ab.Draw.Should().Be("Inferred");
        ab.Label.Should().StartWith("2 × ");
        graph.Edges.Single(e => e.From == "b").Draw.Should().Be("Conflict");
    }

    [Fact]
    public void Expected_flow_uses_the_profile_relationships_and_their_own_state()
    {
        var coverage = new TopologyCoverageView
        {
            Relationships = [new() { Id = "x", Label = "Altinn → MU", From = "Altinn", To = "MU", Transport = "Webhook", State = TopologyRelationshipState.ExpectedUnresolved },
                new() { Id = "y", Label = "MU → MM", From = "MU", To = "MM", Transport = "Service Bus", State = TopologyRelationshipState.Observed, RuntimeObserved = true }],
        };
        var graph = TopologyMapPresentation.Build(new IntegrationTopologyView(), coverage, TopologyMapPresentation.ExpectedLevel);
        graph.Nodes.Select(n => n.Name).Should().BeEquivalentTo(["Altinn", "MU", "MM"]);
        graph.Edges.Single(e => e.Id == "r:x").Draw.Should().Be("Unresolved");
        graph.Edges.Single(e => e.Id == "r:y").Label.Should().Contain("observed");
        graph.Edges.Single(e => e.Id == "r:y").Draw.Should().Be("");
    }
}
