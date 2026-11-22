using BirkNext.TestCoverage;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The specification's clarifications sent to Discover test scenarios: the documented answer and the state recorded in BirkNext, the ids the
/// answer or the question names, and never a superseded one. A documented answer is never sent as resolved.
/// </summary>
public sealed class ClarificationsForDiscoveryTests
{
    private static SemanticClarification Item(string question, string? answer = null, params string[] linked)
    {
        var item = new SemanticClarification { Question = question, Answer = answer };
        item.AffectedElements.AddRange(linked);
        return item;
    }

    [Fact]
    public void States_answers_and_named_ids_are_sent_and_superseded_ones_are_not()
    {
        var open = Item("What counts as a duplicate for fr-2 and US1?");
        var documented = Item("How long are reports kept?", "Ten years, see FR-003.", "FR-003");
        var resolved = Item("Must reports be encrypted?", "Yes");
        var superseded = Item("Old question?");
        var lifecycle = new SddLifecycleState();
        lifecycle.Questions.Add(new SddClarification { Id = ClarificationStates.Key(resolved), Status = "Resolved", Resolution = "Encrypted at rest (decision 2026-10-01)." });
        lifecycle.Questions.Add(new SddClarification { Id = ClarificationStates.Key(superseded), Status = "Superseded" });

        var sent = ClarificationStates.ForDiscovery([open, documented, resolved, superseded], lifecycle);

        sent.Should().HaveCount(3, "a superseded clarification is not sent");
        sent[0].State.Should().Be(CoverageClarification.Open);
        sent[0].AffectedIds.Should().BeEquivalentTo(["FR-002", "US1"], "ids the question names count, normalised like requirement ids");
        sent[1].State.Should().Be(CoverageClarification.Documented, "a documented answer is never promoted to resolved");
        sent[1].Answer.Should().Be("Ten years, see FR-003.");
        sent[1].AffectedIds.Should().Equal("FR-003");
        sent[2].State.Should().Be(CoverageClarification.Resolved);
        sent[2].Resolution.Should().StartWith("Encrypted at rest");
        sent[2].AffectedIds.Should().BeEmpty("it names no requirement: it applies to the whole specification");
        ClarificationStates.CaseLabel(sent[0]).Should().StartWith("Open");
    }
}
