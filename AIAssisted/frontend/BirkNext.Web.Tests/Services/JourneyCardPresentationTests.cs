using BirkNext.Integrations;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>Journey at a glance: only declared facts, blockers by kind, eligibility ≠ verification, historical run ≠ current readiness.</summary>
public sealed class JourneyCardPresentationTests
{
    private static IntegrationJourneyView View(bool canRun = false, JourneyAssessmentState assessment = JourneyAssessmentState.NotRun, IntegrationJourneyRunSummary? lastRun = null,
        params JourneyPrerequisite[] prerequisites) => new()
    {
        Journey = new IntegrationJourneyDefinition
        {
            JourneyId = "rapportmottak", DisplayName = "Rapportmottak", Description = "A school reports absence; the report reaches Bufdata.",
            Integrations = [new("report-intake", "Report intake", IntegrationKind.HttpApi, "Altinn", "MU", "Receives the report")],
        },
        CanRun = canRun, Assessment = assessment, LastRun = lastRun, Prerequisites = prerequisites, Maturity = new(true, true, canRun, false, ""),
    };

    private static JourneyPrerequisite P(string key, JourneyPrerequisiteCategory category, JourneyReadinessState state) => new(key, key, category, state, $"{key} detail");

    [Fact]
    public void Blockers_are_listed_by_kind_and_the_next_action_names_the_owner()
    {
        var card = JourneyCardPresentation.Build(View(false, JourneyAssessmentState.NotRun, null,
            P("M2LB source", JourneyPrerequisiteCategory.SourceEvidence, JourneyReadinessState.NotConfigured),
            P("Synthetic child", JourneyPrerequisiteCategory.SyntheticData, JourneyReadinessState.Partial),
            P("Altinn sign-in", JourneyPrerequisiteCategory.Authentication, JourneyReadinessState.Ready)));
        card.Eligibility.Should().Be("Blocked — 2 prerequisites not met");
        card.Blockers.Select(b => b.Label).Should().Equal("M2LB source", "Synthetic child");
        card.NextAction.Should().Contain("available is not bound");
        card.SourceEvidence.Should().ContainSingle(i => i.State == "Not configured");
        card.TestData.Should().ContainSingle(i => i.State == "Partial");
        card.Access.Should().ContainSingle(i => i.State == "Ready");
        card.Integrations.Should().ContainSingle(i => i.State == "Not configured" && i.Label.Contains("Altinn → MU"));
    }

    [Fact]
    public void Executable_is_not_verified_and_source_evidence_is_not_runtime_verification()
    {
        var card = JourneyCardPresentation.Build(View(canRun: true));
        card.Eligibility.Should().Be("Executable after explicit confirmation");
        card.Verification.Should().Be("Not run (source evidence only — not runtime verified)");
        card.NextAction.Should().Contain("confirm it explicitly").And.Contain("nothing runs by itself");
        JourneyCardPresentation.Verification(JourneyAssessmentState.ExecutedNotVerified, new(true, true, true, false, "")).Should().Be("Executed, not verified");
    }

    [Fact]
    public void A_historical_pass_is_labelled_historical_while_current_blockers_remain()
    {
        var run = new IntegrationJourneyRunSummary(Guid.NewGuid(), "skolenaervaer", "rapportmottak", "s1", "dev", JourneyRunState.Completed, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 5, 5, 0);
        var card = JourneyCardPresentation.Build(View(false, JourneyAssessmentState.Verified, run, P("Contract", JourneyPrerequisiteCategory.Contract, JourneyReadinessState.NotReady)));
        card.History.Should().Contain("Historical — not current readiness").And.Contain("5/5 steps verified");
        card.Eligibility.Should().StartWith("Blocked", "a past pass never makes the journey executable now");
    }

    [Fact]
    public void Nothing_is_invented_when_the_pack_declares_nothing()
    {
        var card = JourneyCardPresentation.Build(new IntegrationJourneyView { Journey = new IntegrationJourneyDefinition { JourneyId = "x" } });
        card.Purpose.Should().Be("No purpose is declared for this journey.");
        card.Integrations.Should().BeEmpty();
        card.Expected.Should().Be("No verification steps are declared.");
        card.Scenarios.Should().BeEmpty();
    }
}
