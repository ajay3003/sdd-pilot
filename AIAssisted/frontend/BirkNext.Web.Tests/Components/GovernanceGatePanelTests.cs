using BirkNext.Governance;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>Governance gate panel: the decision has a label and an icon (never colour alone) and its reason; each finding explains why, where, who and how;
/// policy ids are technical details; the action is chosen explicitly and a new Version re-evaluates (after a review decision).</summary>
public sealed class GovernanceGatePanelTests : BunitContext
{
    static readonly DateTimeOffset Now = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);

    static GovernanceFinding F(GovernancePolicy p, string? module = null, params string[] evidence) =>
        new(p.Id, p.Version, p.Title, p.Purpose, evidence.Length, evidence, p.Owner, p.Resolution, ["Integration Quality Review"], module, ["CR-K-DECLARED"]);

    [Fact]
    public void Shows_the_decision_reasons_and_finding_cards_and_reevaluates_per_action_and_version()
    {
        GovernanceFinding[] findings = [F(GovernancePolicies.IdentityConflict, null, "Buf.Meldingsutveksler.Service → Meldingsutveksleren"),
                                        F(GovernancePolicies.RemovedStory, "SkoleService", "T010 → US3")];
        var api = new Mock<ICanonicalInterpretationApiService>();
        api.Setup(a => a.GovernanceAsync("dev", It.IsAny<GateAction>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, GateAction action, IReadOnlyList<string>? _, CancellationToken _) =>
                (GovernanceGate.Evaluate(action, findings, Guid.NewGuid(), Now), (string?)null));
        Services.AddSingleton(api.Object);

        var cut = Render<GovernanceGatePanel>(p => p.Add(x => x.Scope, "dev"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ggp-decision]").GetAttribute("data-decision").Should().Be("Blocked"));
        cut.Find("[data-testid=ggp-decision]").TextContent.Should().Contain("✕").And.Contain("Blocked").And.Contain("1 blocking finding for release readiness");
        var cards = cut.FindAll("[data-testid=ggp-finding]");
        cards[0].GetAttribute("data-policy").Should().Be("gov.identity.conflict");
        cards[0].TextContent.Should().Contain("Blocking").And.Contain("Found in: Buf.Meldingsutveksler.Service → Meldingsutveksleren")
            .And.Contain("Resolve: Confirm the correct identity").And.Contain("owner: Human review");
        cards[0].QuerySelector("details")!.TextContent.Should().Contain("gov.identity.conflict").And.Contain("Policy default").And.Contain("CR-K-DECLARED");
        cards[1].TextContent.Should().Contain("Non-blocking warning").And.Contain("SkoleService");

        cut.Find("[data-testid=ggp-action]").Change(nameof(GateAction.Review));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ggp-decision]").GetAttribute("data-decision").Should().Be("ReadyWithLimitations"));
        cut.Find("[data-testid=ggp-decision]").TextContent.Should().Contain("Ready with limitations");

        cut.Render(p => p.Add(x => x.Version, 1));
        api.Verify(a => a.GovernanceAsync("dev", GateAction.Review, null, It.IsAny<CancellationToken>()), Times.Exactly(2), "a new version re-evaluates the chosen action");
    }

    [Fact]
    public void A_backend_error_is_stated_not_shown_as_ready()
    {
        var api = new Mock<ICanonicalInterpretationApiService>();
        api.Setup(a => a.GovernanceAsync("dev", It.IsAny<GateAction>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(((GateResult?)null, "Correlation review request failed (500)."));
        Services.AddSingleton(api.Object);
        var cut = Render<GovernanceGatePanel>(p => p.Add(x => x.Scope, "dev"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ggp-error]").TextContent.Should().Contain("failed (500)"));
        cut.FindAll("[data-testid=ggp-decision]").Should().BeEmpty();
    }
}
