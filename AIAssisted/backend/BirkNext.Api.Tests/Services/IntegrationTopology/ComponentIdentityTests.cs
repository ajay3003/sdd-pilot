using BirkNext.Api.Services.IntegrationTopology;
using BirkNext.IntegrationTopology;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.IntegrationTopology;

/// <summary>
/// One logical component, however sources name it: "MU" (domain profile, source) and "Meldingsutveksleren (MU)" (Integration Catalog) are one
/// topology component with both names kept as aliases and every source's evidence. Only deterministic identities merge — never similar names.
/// </summary>
public sealed class ComponentIdentityTests
{
    private static readonly TopologyProvenance Profile = new(TopologySource.DomainProfile, "skolenaervaer", "Receives the report from Altinn");
    private static readonly TopologyProvenance Source = new(TopologySource.SourceAnalysis, "Tiltak 2.zip", "Api in Buf.Meldingsutveksler.Service");
    private static readonly TopologyProvenance Catalog = new(TopologySource.IntegrationCatalog, "env:httpapi:altinn-report-intake", "Skolenærvær – Altinn report intake");

    private static IntegrationTopologyView Build(TopologyBuilder builder) => builder.Build("env", null, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("MU", "Meldingsutveksleren (MU)", true)]
    [InlineData("Meldingsutveksleren", "Meldingsutveksleren (MU)", true)]
    [InlineData("meldingsutveksleren (mu)", "Meldingsutveksleren (MU)", true)]
    [InlineData("TT02", "Altinn (TT02)", false)]               // a qualifier, not an abbreviation of Altinn
    [InlineData("Development", "Orders API (Development)", false)]
    [InlineData("MUX", "Meldingsutveksleren (MU)", false)]
    [InlineData("Meldingsutveksler", "Meldingsutveksleren (MU)", false)] // similar is not the same
    [InlineData("MU", "MM", false)]
    public void Names_identify_the_same_component_only_by_an_exact_or_stated_name(string a, string b, bool same)
    {
        ComponentNames.Same(a, b).Should().Be(same);
        ComponentNames.Same(b, a).Should().Be(same);
    }

    [Fact]
    public void The_catalog_name_with_its_abbreviation_is_the_profile_and_source_component()
    {
        var builder = new TopologyBuilder();
        builder.Alias("MU", ["Buf.Meldingsutveksler.Service"]);
        builder.Component("MU", TopologyStatus.Expected, Profile);
        builder.Component("Buf.Meldingsutveksler.Service", TopologyStatus.Discovered, Source, "Api");
        var key = builder.Component("Meldingsutveksleren (MU)", TopologyStatus.Configured, Catalog);

        var view = Build(builder);

        var mu = view.Components.Should().ContainSingle().Which;
        (mu.Id, key, mu.Name).Should().Be(("mu", "mu", "Meldingsutveksleren (MU)"), "one canonical component; its id is unchanged and the fuller stated name is shown");
        mu.Provenance.Select(p => p.Source).Should().BeEquivalentTo([TopologySource.DomainProfile, TopologySource.SourceAnalysis, TopologySource.IntegrationCatalog]);
        mu.Aliases.Should().BeEquivalentTo(new[]
        {
            new TopologyComponentAlias("Buf.Meldingsutveksler.Service", TopologyBuilder.BasisDeclaredAlias, [TopologySource.SourceAnalysis]),
            new TopologyComponentAlias("MU", TopologyBuilder.BasisName, [TopologySource.DomainProfile]),
        });
        mu.Provenance.Single(p => p.Source == TopologySource.SourceAnalysis).Detail.Should().Be("Api in Buf.Meldingsutveksler.Service", "each source's own wording is kept");
        mu.IdentityNote.Should().BeNull();
    }

    [Fact]
    public void A_component_identified_by_its_project_keeps_that_project_name_as_its_alias()
    {
        var builder = new TopologyBuilder();
        builder.Alias("MU", ["Buf.Meldingsutveksler.Service"]);
        builder.Component("MU", TopologyStatus.Expected, Profile);
        builder.Component("Service", TopologyStatus.Discovered, Source, "Api", identities: ["Buf.Meldingsutveksler.Service"]);

        Build(builder).Components.Should().ContainSingle().Which.Aliases.Select(a => a.Name).Should().Contain("Buf.Meldingsutveksler.Service")
            .And.NotContain("Service", "a bare component name says nothing; the identity that matched is what the source called it");
    }

    [Fact]
    public void The_order_sources_arrive_in_does_not_change_the_identity()
    {
        var builder = new TopologyBuilder();
        builder.Component("Meldingsutveksleren (MU)", TopologyStatus.Configured, Catalog);
        builder.Component("MU", TopologyStatus.Discovered, Source);
        builder.Component("Meldingsutveksleren", TopologyStatus.Discovered, Source);

        var mu = Build(builder).Components.Should().ContainSingle().Which;
        mu.Name.Should().Be("Meldingsutveksleren (MU)");
        mu.Aliases.Select(a => (a.Name, a.Basis)).Should().BeEquivalentTo([("MU", TopologyBuilder.BasisStated), ("Meldingsutveksleren", TopologyBuilder.BasisStated)]);
    }

    [Fact]
    public void A_name_that_could_be_two_components_merges_with_neither_and_needs_confirmation()
    {
        var builder = new TopologyBuilder();
        builder.Component("MU", TopologyStatus.Discovered, Source);
        builder.Component("Meldingsutveksleren", TopologyStatus.Discovered, Source);
        builder.Component("Meldingsutveksleren (MU)", TopologyStatus.Configured, Catalog);

        var view = Build(builder);

        view.Components.Should().HaveCount(3, "an ambiguous identity is never merged silently");
        view.Components.Single(c => c.Name == "Meldingsutveksleren (MU)").IdentityNote.Should().Contain("MU").And.Contain("Meldingsutveksleren").And.Contain("confirmed");
    }

    [Fact]
    public void Unrelated_and_merely_similar_components_stay_apart()
    {
        var builder = new TopologyBuilder();
        builder.Component("Altinn (TT02)", TopologyStatus.Configured, Catalog);
        builder.Component("TT02", TopologyStatus.Discovered, Source);
        builder.Component("Meldingsutveksler", TopologyStatus.Discovered, Source);
        builder.Component("MM", TopologyStatus.Discovered, Source);

        Build(builder).Components.Should().HaveCount(4);
    }

    [Fact]
    public void Edges_to_one_component_merge_only_when_type_and_address_match_and_keep_each_topic_and_source()
    {
        var builder = new TopologyBuilder();
        builder.Alias("MU", []);
        var demo = builder.Component("Demo.FagsystemKlientEksempel", TopologyStatus.Discovered, Source);
        var fromSource = builder.Component("MU", TopologyStatus.Discovered, Source);
        var fromCatalog = builder.Component("Meldingsutveksleren (MU)", TopologyStatus.Configured, Catalog);
        builder.Connection(demo, fromSource, TopologyInterfaceType.Messaging, "ServiceBus", "topic-a", TopologyStatus.Discovered, Source);
        builder.Connection(demo, fromCatalog, TopologyInterfaceType.Messaging, "ServiceBus", "topic-a", TopologyStatus.Configured, Catalog);
        builder.Connection(demo, fromSource, TopologyInterfaceType.Messaging, "ServiceBus", "topic-b", TopologyStatus.Discovered, Source);

        var view = Build(builder);

        view.Connections.Should().HaveCount(2, "the same topic between the same components is one flow; another topic is another flow");
        view.Connections.Single(c => c.Address == "topic-a").Provenance.Select(p => p.Source).Should().BeEquivalentTo([TopologySource.SourceAnalysis, TopologySource.IntegrationCatalog]);
        view.Connections.Should().OnlyContain(c => c.ConsumerComponentId == "mu");
    }
}
