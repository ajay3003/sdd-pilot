using BirkNext.Integrations;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>IQR light mode decides only what a tester sees first: resolved from evidence (never runtime verified) or the one decision needed.</summary>
public sealed class IntegrationReviewLightPresentationTests
{
    private static IntegrationReviewSubject Subject(string id, ConsumerMappingState mapping = ConsumerMappingState.Confirmed, IntegrationReviewEvidenceFacts? facts = null) =>
        new(id, id, IntegrationKind.HttpApi, IntegrationReviewCapability.HttpApi) { MappingState = mapping, Facts = facts };

    [Fact]
    public void Resolved_items_say_from_evidence_and_open_items_name_the_decision()
    {
        var items = IntegrationReviewLightPresentation.Classify(new IntegrationReviewReadiness
        {
            Subjects =
            [
                Subject("a", facts: new("a") { SourceState = TestPackSourceState.Bound, BoundArchive = "tiltak2.zip", SourceMatches = 1 }),
                Subject("b"),
                Subject("c", ConsumerMappingState.Suggested),
                Subject("d", facts: new("d") { SourceState = TestPackSourceState.AvailableNotBound, SourceMatches = 2 }),
                Subject("e", facts: new("e") { SourceState = TestPackSourceState.AvailableNotBound, SourceMatches = 1 }),
                Subject("f", facts: new("f") { SourceState = TestPackSourceState.Bound, BoundArchive = "x.zip", SchemaFiles = ["a.xsd", "b.xsd"] }),
            ],
        });

        items.Single(i => i.Subject.IntegrationId == "a").Should().Match<IntegrationReviewLightPresentation.Item>(i => i.Resolved && i.Detail.Contains("tiltak2.zip") && i.Detail.Contains("Not runtime verified"));
        items.Single(i => i.Subject.IntegrationId == "c").Detail.Should().StartWith("Suggested consumer mapping");
        items.Single(i => i.Subject.IntegrationId == "d").Detail.Should().Contain("2 source snapshots match").And.Contain("never chosen automatically");
        items.Single(i => i.Subject.IntegrationId == "e").Detail.Should().Be("Source evidence available — bind it.");
        items.Single(i => i.Subject.IntegrationId == "f").Detail.Should().Be("2 contract candidates — choose one.");
        IntegrationReviewLightPresentation.Summary(items).Should().Be("6 integrations relevant · 2 resolved from evidence · 4 need your decision");
    }
}
