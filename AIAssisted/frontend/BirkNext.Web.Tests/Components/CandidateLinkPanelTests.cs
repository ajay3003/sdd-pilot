using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

public class CandidateLinkPanelTests : BunitContext
{
    private static ExtractionCandidate Candidate(string title, ScenarioKind kind) => new()
    {
        Title = title,
        Classification = kind,
        ClassificationSignal = ClassificationSignal.Default,
        SourceBlockType = BlockType.UnorderedListItem,
    };

    [Fact]
    public void TestCandidate_CanLinkToClarification()
    {
        var test = Candidate("Given login When valid Then success", ScenarioKind.Test);
        var clarification = Candidate("What happens if token expires?", ScenarioKind.NeedsClarification);
        CandidateLinkEntry? added = null;

        var cut = Render<CandidateLinkPanel>(p => p
            .Add(c => c.Candidate, test)
            .Add(c => c.LinkableCandidates, [clarification])
            .Add(c => c.OnLinkAdded, (CandidateLinkEntry e) =>
            {
                added = e;
                return Task.CompletedTask;
            }));

        cut.FindAll(".link-section")
            .Should().Contain(s => s.TextContent.Contains("Clarifications"));

        var clarificationSection = cut.FindAll(".link-section")
            .Single(s => s.TextContent.Contains("Clarifications"));
        clarificationSection.QuerySelector(".link-add-btn")!.Click();
        cut.FindAll(".link-section")
            .Single(s => s.TextContent.Contains("Clarifications"))
            .QuerySelector(".link-picker-item")!
            .Click();

        added.Should().NotBeNull();
        added!.SourceId.Should().Be(test.CandidateId);
        added.TargetId.Should().Be(clarification.CandidateId);
        added.LinkType.Should().Be(CandidateLinkType.TestClarification);
    }

    [Fact]
    public void ExistingLink_CanBeRemoved()
    {
        var req = Candidate("FR-001: system validates credentials", ScenarioKind.Requirement);
        var test = Candidate("Given login When valid Then success", ScenarioKind.Test);
        var link = new CandidateLinkEntry(req.CandidateId, test.CandidateId, CandidateLinkType.RequirementTest);
        CandidateLinkEntry? removed = null;

        var cut = Render<CandidateLinkPanel>(p => p
            .Add(c => c.Candidate, req)
            .Add(c => c.LinkableCandidates, [test])
            .Add(c => c.Links, [link])
            .Add(c => c.OnLinkRemoved, (CandidateLinkEntry e) =>
            {
                removed = e;
                return Task.CompletedTask;
            }));

        cut.Find(".link-remove-btn").Click();

        removed.Should().Be(link);
    }
}
