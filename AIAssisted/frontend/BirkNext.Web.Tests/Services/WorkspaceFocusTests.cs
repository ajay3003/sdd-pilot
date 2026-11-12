using BirkNext.SourceBaselines;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The view-aware focus over the shared capabilities: the workspace view changes relevance and order only — never a capability's identity,
/// decision or route — and the source-baseline risk is visible in every view. Expected values are written from the rule.
/// </summary>
public sealed class WorkspaceFocusTests : BunitContext
{
    private static ReviewCapability Cap(string id, string route, ReviewReadiness readiness = ReviewReadiness.Ready,
        ReviewDecisionState decision = ReviewDecisionState.NotReviewed, params string[] missing) =>
        new(id, id, ReviewSection.QualityTesting, ReviewApplicabilityState.Applicable, readiness, ReviewRequiredness.Recommended, decision, route, missing, $"why {id}");

    private static readonly IReadOnlyList<ReviewCapability> Capabilities =
    [
        Cap("api", "api-quality-review"),
        Cap("doc-quality", "quality/document"),
        Cap("spec", "specification-explorer", decision: ReviewDecisionState.Reviewed),
        Cap("traceability", "artifact-traceability"),
        Cap("coverage", "test-coverage-review", ReviewReadiness.NeedsSetup, ReviewDecisionState.NotReviewed, "Source snapshot"),
        Cap("test-data", "test-data"),
        Cap("pipeline", "pipeline-review"),
    ];

    private static SourceContextView NoBaseline => new([new LogicalSourceView("m2lb", "M2LB", "Archive file name", SourceBaselineState.NotSelected, null, null,
        "", 5, 5, [], "BASELINE_NOT_SELECTED", Guid.NewGuid())], 1, 5, 1, "fp");

    [Fact]
    public void Manual_testing_focuses_on_functional_work_and_keeps_identities()
    {
        var manual = WorkspaceFocus.Of(WorkspaceView.ManualTesting, Capabilities, null);
        manual.Relevant.Should().Be(5, "api-quality-review and pipeline-review are not manual-testing modules");
        manual.Primary.Should().HaveCount(3);
        manual.Primary.Select(a => a.Id).Should().NotContain(["api", "pipeline"]);
        manual.Primary.Should().OnlyContain(a => a.State == FocusActionState.CanDoNow, "ready work comes before blocked work");
        manual.Primary.Select(a => a.Id).Should().Equal(new[] { "doc-quality", "test-data", "traceability" }, "the view's workflow order: requirements, preparation, coverage");
        (manual.ReadyNow, manual.Blocked, manual.Completed).Should().Be((3, 1, 1));

        var developer = WorkspaceFocus.Of(WorkspaceView.Developer, Capabilities, null);
        developer.Primary.Select(a => a.Id).Should().Contain("api", "the same capability is relevant in a technical view");
        var shared = manual.Primary.Select(a => (a.Id, a.Route)).Intersect(WorkspaceFocus.Of(WorkspaceView.TestLead, Capabilities, null).Primary.Select(a => (a.Id, a.Route)));
        shared.Should().NotBeEmpty("a capability keeps its id and route in every view");
    }

    [Fact]
    public void Blocked_steps_say_what_is_missing_and_completed_steps_are_not_offered()
    {
        var focus = WorkspaceFocus.Of(WorkspaceView.ManualTesting, [Cap("coverage", "test-coverage-review", ReviewReadiness.NeedsSetup, ReviewDecisionState.NotReviewed, "Source snapshot"),
            Cap("spec", "specification-explorer", decision: ReviewDecisionState.Reviewed)], null);
        focus.Primary.Should().ContainSingle().Which.Should().Match<FocusAction>(a => a.State == FocusActionState.Blocked && a.StateLabel == "Blocked" && a.Blocker == "Needs setup: Source snapshot");
        focus.Completed.Should().Be(1);
    }

    [Theory]
    [InlineData(WorkspaceView.ManualTesting)]
    [InlineData(WorkspaceView.Developer)]
    [InlineData(WorkspaceView.TestLead)]
    [InlineData(WorkspaceView.AllModules)]
    public void The_source_baseline_risk_is_visible_in_every_view(WorkspaceView view)
    {
        WorkspaceFocus.Of(view, Capabilities, NoBaseline).Risks.Should().ContainSingle()
            .Which.Should().Match<FocusRisk>(r => r.Code == WorkspaceFocus.BaselineNotSelected && r.Route == "sources#scp-title" && r.Text.Contains("none is selected"));
    }

    [Fact]
    public void The_panel_follows_a_view_switch_without_reloading_and_renders_nothing_in_All_Modules()
    {
        Services.AddScoped<NavigationSectionState>();
        var navigation = Services.GetRequiredService<NavigationSectionState>();
        var cut = Render<WorkspaceFocusPanel>(p => p.Add(x => x.Capabilities, Capabilities));
        cut.FindAll("[data-testid=workspace-focus]").Should().BeEmpty("All Modules already shows the full page");

        navigation.SetView(WorkspaceView.ManualTesting);
        cut.WaitForAssertion(() => cut.Find("[data-testid=workspace-focus]").GetAttribute("data-view").Should().Be("ManualTesting"));
        cut.FindAll("[data-testid=wfp-action]").Select(a => a.GetAttribute("data-id")).Should().Equal("doc-quality", "test-data", "traceability");
        cut.FindAll("[data-testid=wfp-open]").Should().OnlyContain(a => !string.IsNullOrWhiteSpace(a.GetAttribute("href")), "no inert actions");

        navigation.SetView(WorkspaceView.Developer);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=wfp-action]").Select(a => a.GetAttribute("data-id")).Should().Contain("api"));
    }
}
