using BirkNext.Api.Services.IntegrationSuites;
using BirkNext.IntegrationSuites;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationSuites;

/// <summary>
/// Integration suites remember which declared journeys (id + revision) they were prepared for: references accumulate across revisions, one
/// journey revision is recorded once, and preparing without journey context adds nothing.
/// </summary>
public sealed class SuiteJourneyReferenceTests
{
    private static readonly SuiteJourneyReference Orders1 = new("project.import:p1:orders", 1, "Order to invoice");

    [Fact]
    public void A_journey_revision_is_recorded_once_and_references_accumulate()
    {
        var first = IntegrationSuiteService.WithJourney([], Orders1);
        first.Should().Equal(Orders1);
        IntegrationSuiteService.WithJourney(first, Orders1).Should().Equal(Orders1);
        IntegrationSuiteService.WithJourney(first, Orders1 with { Revision = 2 }).Should().Equal(new[] { Orders1, Orders1 with { Revision = 2 } }, "a new journey revision is a new reference");
        IntegrationSuiteService.WithJourney(first, null).Should().Equal(new[] { Orders1 }, "preparing without journey context keeps the earlier references");
    }
}
