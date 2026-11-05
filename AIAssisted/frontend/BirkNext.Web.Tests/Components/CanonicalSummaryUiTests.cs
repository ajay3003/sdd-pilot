using BirkNext.Interpretation;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>Summary-first canonical counts (correlation/2): the Dashboard tile reads only the summary endpoint for the active project, names what each
/// count counts (logical contracts vs occurrences), points to Relationship Review only when decisions are needed, and renders nothing without a scope.</summary>
public sealed class CanonicalSummaryUiTests : BunitContext
{
    static CanonicalSummaryView Summary(int needsReview, Guid? interpretation = null) => new("dev", interpretation ?? Guid.NewGuid(), DateTimeOffset.UnixEpoch,
        ["correlation/2", "work-items/1"], new Dictionary<string, int> { ["SourceSymbol"] = 1437 }, new Dictionary<string, int> { ["Uses"] = 38 }, 2500, 1362, needsReview, 1,
        new(70, 84, 81, 88, 14), [new(Guid.NewGuid(), "Terraform-iac-meldingsutveksler", ["Infrastructure", "Contracts", "Pipelines"], [])], [])
        { Decisions = needsReview, Suggestions = 1361 };

    private Mock<ICanonicalInterpretationApiService> Setup(string? profile, CanonicalSummaryView? summary)
    {
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext
            { ActiveProfile = profile is null ? null : new FrontendAnalysisProfile { Id = profile, Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development } });
        Services.AddSingleton(context.Object);
        var api = new Mock<ICanonicalInterpretationApiService>();
        api.Setup(a => a.SummaryAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync((summary, (string?)null));
        Services.AddSingleton(api.Object);
        return api;
    }

    [Fact]
    public void The_dashboard_tile_shows_named_counts_and_links_to_review_only_when_decisions_are_needed()
    {
        var api = Setup("dev", Summary(needsReview: 1363));
        var cut = Render<CanonicalSummaryTile>();
        cut.WaitForElement("[data-testid=db-canonical-summary]");
        cut.Find("[data-testid=cst-review]").TextContent.Should().Be("1363");
        cut.Find("[data-testid=cst-suggestions]").TextContent.Should().Be("1361", "optional suggestions are shown apart from decisions");
        cut.Find("[data-testid=db-canonical-summary]").TextContent.Should().Contain("70 logical contracts (88 occurrences)").And.Contain("correlation/2");
        cut.Find("[data-testid=cst-open-review]").GetAttribute("href").Should().Be("correlation-review");
        api.Verify(a => a.EntitiesAsync(It.IsAny<string>(), It.IsAny<CanonicalEntityType?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never(), "the dashboard never lists the graph");

        var calm = new BunitContext();
        calm.Services.AddSingleton(Services.GetRequiredService<IFrontendAnalysisContextFactory>());
        var quiet = new Mock<ICanonicalInterpretationApiService>();
        quiet.Setup(a => a.SummaryAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync((Summary(0), (string?)null));
        calm.Services.AddSingleton(quiet.Object);
        var noReview = calm.Render<CanonicalSummaryTile>();
        noReview.WaitForElement("[data-testid=db-canonical-summary]");
        noReview.FindAll("[data-testid=cst-open-review]").Should().BeEmpty();
    }

    [Fact]
    public void Without_an_interpretation_the_tile_says_so_and_without_a_project_it_renders_nothing()
    {
        Setup("dev", new CanonicalSummaryView("dev", null, null, [], new Dictionary<string, int>(), new Dictionary<string, int>(), 0, 0, 0, 0, new(0, 0, 0, 0, 0), [], []));
        var cut = Render<CanonicalSummaryTile>();
        cut.WaitForElement("[data-testid=cst-empty]").TextContent.Should().Contain("Relationships not analyzed for this environment yet");

        var none = new BunitContext();
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext());
        none.Services.AddSingleton(context.Object);
        var api = new Mock<ICanonicalInterpretationApiService>();
        none.Services.AddSingleton(api.Object);
        none.Render<CanonicalSummaryTile>().Markup.Trim().Should().BeEmpty();
        api.Verify(a => a.SummaryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public void Package_capabilities_use_tester_wording()
    {
        CorrelationPresentation.CapabilityLabel("SharedSource").Should().Be("Shared libraries");
        CorrelationPresentation.CapabilityLabel("ApplicationSource").Should().Be("Application source");
        CorrelationPresentation.CapabilityLabel("Infrastructure").Should().Be("Infrastructure");
    }
}
