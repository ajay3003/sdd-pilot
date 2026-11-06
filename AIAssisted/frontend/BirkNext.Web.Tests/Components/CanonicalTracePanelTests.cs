using BirkNext.Interpretation;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>The canonical end-to-end trace panel: summary first (document, implementation and test evidence apart), feature packs and chains loaded on request,
/// explicit links distinct from suggestions, missing links shown as missing, and an unavailable shared interpretation shown as such (no local fallback).</summary>
public sealed class CanonicalTracePanelTests : BunitContext
{
    static CanonicalEntityView E(CanonicalEntityType type, string key, string name, Dictionary<string, string>? attrs = null) =>
        new(Guid.NewGuid(), type, key, name, key, "test", null, attrs ?? [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    static CanonicalRelationView R(CanonicalEntityView from, CanonicalEntityView to, CanonicalRelationType type, RelationResolution resolution, string rule) =>
        new(Guid.NewGuid(), $"{from.Key}->{type}->{to.Key}", from, to, type, resolution, resolution, CorrelationConfidence.Explicit, rule, "correlation/3",
            [new("test", "x")], "fp", $"Linked by {rule}.", RelationReviewState.NotReviewed, null, true);

    private Mock<ICanonicalInterpretationApiService> Setup(TraceSummaryView? summary, string? error = null)
    {
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext
            { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development } });
        Services.AddSingleton(context.Object);
        var api = new Mock<ICanonicalInterpretationApiService>();
        api.Setup(a => a.TraceSummaryAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync((summary, error));
        Services.AddSingleton(api.Object);
        return api;
    }

    static TraceSummaryView Summary() => new(Guid.NewGuid(), 1, 2, 1, 0, 0, 0, 3, 1, 1, 1, 0, 1, 0, 0, []);

    [Fact]
    public void The_summary_loads_first_and_chains_load_only_on_request()
    {
        var api = Setup(Summary());
        var feature = E(CanonicalEntityType.Feature, "skoleservice/001", "Motta rapport");
        api.Setup(a => a.TraceFeaturesAsync("dev", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<TraceFeatureRow>)[new(feature, 2, 1, 3, 1, 1, 1, 0)], (string?)null));
        var fr1 = E(CanonicalEntityType.Requirement, "skoleservice/001/FR-001", "FR-001 Lagre fravær");
        var fr2 = E(CanonicalEntityType.Requirement, "skoleservice/001/FR-002", "FR-002 Validere");
        var test = E(CanonicalEntityType.Test, "t", "RapportTests.Lagrer");
        var suggested = E(CanonicalEntityType.Test, "s", "NavnTests.Ligner");
        var task = E(CanonicalEntityType.Task, "skoleservice/001/t010", "T010 Implement handler", new() { ["completed"] = "true" });
        var bare = E(CanonicalEntityType.Task, "skoleservice/001/t011", "T011 Dokumenter", new() { ["completed"] = "true" });
        var symbol = E(CanonicalEntityType.SourceSymbol, "m2lb|Ns.Handler", "Ns.Handler");
        var story = E(CanonicalEntityType.UserStory, "skoleservice/001/US1", "US1 Motta");
        api.Setup(a => a.TraceFeatureAsync("dev", feature.Id, It.IsAny<CancellationToken>())).ReturnsAsync((new TraceFeatureDetail(feature,
            [new(fr1, [new(test, R(test, fr1, CanonicalRelationType.Verifies, RelationResolution.Explicit, CorrelationRules.TestExplicitRule)),
                       new(suggested, R(suggested, fr1, CanonicalRelationType.Verifies, RelationResolution.Candidate, CorrelationRules.TestCandidateRule))]), new(fr2, [])],
            [new(story, "User story", [new(task, [new(symbol, R(task, symbol, CanonicalRelationType.References, RelationResolution.Explicit, CorrelationRules.TaskSymbolRule))], [], [], []),
                                       new(bare, [], [], [], [])])],
            ["1 of 2 task(s) have no linked implementation evidence (a checked box is not implementation evidence)."], null), (string?)null));

        var cut = Render<CanonicalTracePanel>();
        cut.WaitForElement("[data-testid=ct-summary]");
        cut.Find("[data-testid=ct-implemented]").TextContent.Should().Be("1");
        cut.Find("[data-testid=ct-tested]").TextContent.Should().Be("1");
        api.Verify(a => a.TraceFeaturesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never(), "feature packs load on request");

        cut.Find("[data-testid=ct-show-features]").Click();
        cut.WaitForElement("[data-testid=ct-feature] button").Click();
        cut.WaitForElement("[data-testid=ct-detail]");
        var links = cut.FindAll("[data-testid=ct-requirement]")[0].QuerySelectorAll("[data-testid=ct-link]");
        links.Select(l => l.GetAttribute("data-counts")).Should().Equal("true", "false");
        links[1].TextContent.Should().Contain("Candidate", "a suggestion is visibly not a confirmed link");
        cut.FindAll("[data-testid=ct-requirement]")[1].QuerySelector("[data-testid=ct-missing]")!.TextContent.Should().Be("No linked test");
        var tasks = cut.FindAll("[data-testid=ct-task]");
        tasks[0].TextContent.Should().Contain("Ns.Handler").And.Contain("checked in the tasks file");
        tasks[1].QuerySelector("[data-testid=ct-missing]")!.TextContent.Should().Be("Implementation evidence not linked");
        cut.Find("[data-testid=ct-detail]").TextContent.Should().Contain("a checked box is not implementation evidence");
        cut.Find("[data-testid=ct-workitem]").GetAttribute("data-kind").Should().Be("User story");
    }

    [Fact]
    public void An_unavailable_shared_interpretation_is_shown_as_unavailable()
    {
        Setup(null, "The backend could not be reached.");
        var cut = Render<CanonicalTracePanel>();
        cut.WaitForElement("[data-testid=ct-unavailable]").TextContent.Should().Contain("Shared interpretation unavailable").And.Contain("could not be reached");
        cut.FindAll("[data-testid=ct-summary]").Should().BeEmpty();
    }

    [Fact]
    public void Without_an_interpretation_counts_read_not_assessed_and_the_disabled_feature_action_says_why()
    {
        Setup(new TraceSummaryView(null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, ["No feature packs are interpreted yet."]));
        var cut = Render<CanonicalTracePanel>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ct-summary]"));
        cut.Find("[data-testid=ct-summary]").GetAttribute("data-interpreted").Should().Be("false");
        cut.Find("[data-testid=ct-implemented]").TextContent.Should().Be("Not assessed", "an unknown is never shown as a measured zero");
        cut.Find("[data-testid=ct-tested]").TextContent.Should().Be("Not assessed");
        var button = cut.Find("[data-testid=ct-show-features]");
        button.HasAttribute("disabled").Should().BeTrue();
        button.GetAttribute("aria-describedby").Should().Be("ct-features-why");
        cut.Find("[data-testid=ct-features-why]").TextContent.Should().Contain("No feature pack is interpreted").And.Contain("nothing is invented");
    }

    [Fact]
    public void With_an_interpretation_zero_is_a_measured_zero()
    {
        Setup(new TraceSummaryView(Guid.NewGuid(), 1, 4, 1, 0, 0, 0, 6, 1, 0, 0, 0, 4, 0, 0, []));
        var cut = Render<CanonicalTracePanel>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ct-summary]"));
        cut.Find("[data-testid=ct-implemented]").TextContent.Should().Be("0");
        cut.FindAll("[data-testid=ct-features-why]").Should().BeEmpty();
    }
}
