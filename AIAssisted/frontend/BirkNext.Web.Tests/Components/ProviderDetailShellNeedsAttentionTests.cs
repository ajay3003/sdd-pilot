using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>Needs attention separates true blockers from review-only issues per card, keeps long diagnostics collapsed and shows a calm
/// "ready for current configured use" state when nothing is open.</summary>
public sealed class ProviderDetailShellNeedsAttentionTests : BunitContext
{
    private static readonly ShellCard[] Cards = [new("app", "App", "Ready", "ok", "")];

    [Fact]
    public void Blockers_and_review_items_are_counted_separately_with_owner_action_and_collapsed_details()
    {
        ShellFixGroup[] groups =
        [
            new("contract", "Report contract", "IQR / Altinn contract evidence", "Review contract", [new("Contract identifier", "Known")])
                { Severity = ShellSeverity.Review, Impact = "Does not block manual preparation", Details = "No application metadata … (long diagnostic)" },
            new("automated", "Automated authentication", "Target Environment · Altinn unattended client", "Configure", [new("Client ID", "Missing"), new("Client secret", "Missing")])
                { Impact = "Blocks automated execution", CountBadge = "2 missing" },
        ];
        var cut = Render<ProviderDetailShell>(p => p.Add(x => x.Prefix, "t").Add(x => x.Label, "System").Add(x => x.Cards, Cards).Add(x => x.FixGroups, groups));
        cut.Find("#t-fix-title").TextContent.Should().Be("Needs attention");
        var count = cut.Find("[data-testid=t-fix-count]");
        (count.GetAttribute("data-blockers"), count.GetAttribute("data-reviews")).Should().Be(("1", "1"));
        count.TextContent.Should().Contain("1 blocker").And.Contain("1 review item");
        cut.FindAll("[data-testid=t-fix-group]").Select(g => g.GetAttribute("data-key")).Should().Equal(["automated", "contract"], "blockers come first");
        var contract = cut.Find("[data-testid=t-fix-group][data-key=contract]");
        contract.QuerySelector("[data-testid=t-fix-severity]")!.TextContent.Should().Be("Review required");
        contract.ClassList.Should().Contain("ps-tone-warn", "review is amber, not red");
        contract.QuerySelector("[data-testid=t-fix-details]")!.HasAttribute("open").Should().BeFalse();
        contract.QuerySelector("[data-testid=t-fix-impact]")!.TextContent.Should().Be("Does not block manual preparation");
        var automated = cut.Find("[data-testid=t-fix-group][data-key=automated]");
        automated.ClassList.Should().Contain("ps-tone-bad");
        automated.QuerySelector("[data-testid=t-fix-badge]")!.TextContent.Should().Be("2 missing");
        automated.TextContent.Should().Contain("Owner: Target Environment · Altinn unattended client");
        automated.QuerySelector("[data-testid=t-fix-action]")!.TextContent.Should().Be("Configure · Automated authentication", "the action names its issue for screen readers");
    }

    [Fact]
    public void Nothing_open_shows_a_ready_state_not_an_empty_container()
    {
        var cut = Render<ProviderDetailShell>(p => p.Add(x => x.Prefix, "t").Add(x => x.Label, "System").Add(x => x.Cards, Cards));
        cut.FindAll("[data-testid=t-fix]").Should().BeEmpty();
        cut.Find("[data-testid=t-fix-none]").TextContent.Should().Contain("Ready for current configured use").And.Contain("No unresolved issues");
        cut.Find("[data-testid=t-card] .ps-pill").GetAttribute("title").Should().Be("Required configuration for this dimension is complete.");
    }
}
