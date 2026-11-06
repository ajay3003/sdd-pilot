using BirkNext.TestData;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Test Data → Session test identities shows what Test Data already holds — the built-in Skolenærvær defaults included — per role: the single
/// default (or the only identity), never an arbitrary pick among several; adding another identity is secondary. Identifiers are never shown.
/// </summary>
public sealed class SessionTestIdentitiesPanelTests : BunitContext
{
    public SessionTestIdentitiesPanelTests() => Services.AddSingleton(new Mock<IAltinnEnvironmentApiService>().Object);

    private static TestDataAssetSummary Identity(string alias, string label, string role, bool isDefault = false, TestDataApproval approval = TestDataApproval.Approved) => new()
    {
        Id = $"altinn-local:{role}:{alias}", Alias = alias, Label = label, SourceId = "altinn-local", Capabilities = ["person", "synthetic", role], IsDefault = isDefault, Approval = approval,
    };

    private IRenderedComponent<SessionTestIdentitiesPanel> Render(params TestDataAssetSummary[] assets) =>
        Render<SessionTestIdentitiesPanel>(p => p.Add(c => c.Assets, assets));

    private static string Row(IRenderedComponent<SessionTestIdentitiesPanel> cut, string role) => cut.Find($"[data-testid=sti-role-row][data-role={role}]").TextContent;

    [Fact]
    public void The_built_in_defaults_are_shown_selected_and_ready_without_creating_anything()
    {
        var cut = Render(Identity("skolenaervaer-reporter-default", "Skolenærvær default reporter", "reporter", isDefault: true),
            Identity("skolenaervaer-child-default", "Skolenærvær default child", "child", isDefault: true));

        cut.Find("[data-testid=sti-role-row][data-role=reporter] [data-testid=sti-chosen]").TextContent.Should().Be("Skolenærvær default reporter");
        Row(cut, "reporter").Should().Contain("Default").And.Contain("Ready").And.Contain("skolenaervaer-reporter-default");
        Row(cut, "child").Should().Contain("Skolenærvær default child").And.Contain("Default").And.Contain("Ready");
        cut.Find("[data-testid=sti-add-disclosure]").HasAttribute("open").Should().BeFalse("adding another identity is not part of the normal flow");
        cut.Markup.Should().NotContain("05859497324").And.NotContain("04061076692");
    }

    [Fact]
    public void A_default_wins_among_several_but_several_without_a_default_are_never_chosen_between_and_none_is_missing()
    {
        var cut = Render(Identity("reporter-a", "Reporter A", "reporter"), Identity("skolenaervaer-reporter-default", "Skolenærvær default reporter", "reporter", isDefault: true),
            Identity("child-a", "Child A", "child"), Identity("child-b", "Child B", "child"));

        cut.Find("[data-testid=sti-role-row][data-role=reporter] [data-testid=sti-chosen]").TextContent.Should().Be("Skolenærvær default reporter");
        Row(cut, "reporter").Should().Contain("1 other identity available");
        cut.Find("[data-testid=sti-role-row][data-role=child] [data-testid=sti-chosen]").TextContent.Should().Be("Choose…", "no arbitrary first or newest");

        Render(Identity("reporter-a", "Reporter A", "reporter")).Find("[data-testid=sti-role-row][data-role=child] [data-testid=sti-chosen]").TextContent.Should().Be("Missing");
    }

    // ── Test Data Overview: each value is its own labelled field (no run-together "…-defaultDefaultReady") ─────────────────────

    [Fact]
    public void Each_identity_value_is_a_separate_labelled_field_and_the_source_is_named()
    {
        var cut = Render(Identity("skolenaervaer-reporter-default", "Skolenærvær default reporter", "reporter", isDefault: true),
            Identity("skolenaervaer-child-only", "Only child", "child"));
        var reporter = cut.Find("[data-testid=sti-role-row][data-role=reporter]");
        reporter.QuerySelector("dd")!.ClassList.Should().Contain("sti-fields");
        reporter.QuerySelectorAll(".sti-field").Should().HaveCount(4, "name, technical ID, source and status are separate fields");
        reporter.QuerySelector("[data-testid=sti-alias]")!.TextContent.Should().Be("skolenaervaer-reporter-default");
        reporter.QuerySelector("[data-testid=sti-default]")!.TextContent.Should().Be("Default");
        reporter.QuerySelector("[data-testid=sti-state]")!.TextContent.Should().Be("Ready");
        reporter.TextContent.Should().NotContain("defaultDefault").And.NotContain("DefaultReady");
        reporter.TextContent.Should().Contain("Technical ID:").And.Contain("Status:", "screen readers hear what each value is");
        cut.Find("[data-testid=sti-role-row][data-role=child] [data-testid=sti-default]").TextContent.Should().Be("Only identity", "chosen because it is the only one, not a default");
    }
}
