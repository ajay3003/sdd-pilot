using FluentAssertions;
using P = BirkNext.Web.Services.PlanPreparePresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>«Teststeget bør konkretiseres» (Planlegg v4, 2026-11-26): a warning with the reason, never a rewrite and never invented details.</summary>
public sealed class StepQualityWarningTests
{
    [Fact]
    public void Generic_or_open_actions_and_unobservable_results_are_flagged_with_their_reason()
    {
        P.StepWarning("Trigger and observe: the record is processed.", "It works")!.Should().HaveCount(2)
            .And.Contain(r => r.StartsWith("Handlingen er generell")).And.Contain(r => r.StartsWith("Forventet resultat er ikke observerbart"));
        P.StepWarning("The record is processed (the action that makes this happen in the test environment must be specified).", "Posten lagres med status Venter i Barn-tabellen")!
            .Should().ContainSingle().Which.Should().StartWith("Kilden sier ikke hvordan");
        P.StepWarning("", "Rapporten vises")!.Should().ContainSingle().Which.Should().StartWith("Mangler handling");
        P.StepWarning("Åpne rapportlisten som saksbehandler", "")!.Should().ContainSingle().Which.Should().StartWith("Mangler forventet resultat");
    }

    [Fact]
    public void A_concrete_step_and_an_empty_row_get_no_warning()
    {
        P.StepWarning("Send inn rapporten med en gyldig periode i skjemaet", "Rapporten vises i listen med status Mottatt").Should().BeNull();
        P.StepWarning("PUT /innmating/inngrep/42 med BarnId = null", "HTTP 202 og hendelsen er ikke synlig i GraphQL").Should().BeNull();
        P.StepWarning("", "").Should().BeNull("an empty new row is not a quality problem yet");
    }
}
