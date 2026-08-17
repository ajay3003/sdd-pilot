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

        cut.Find("[data-testid='link-section-clarifications'] [data-testid='link-add-btn']").Click();

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

        cut.Find("[data-testid='link-remove-btn']").Click();

        removed.Should().Be(link);
    }

    [Fact]
    public void RequirementCandidate_ShowsTestsAndClarificationsSections()
    {
        var req = Candidate("FR-001: system validates credentials", ScenarioKind.Requirement);

        var cut = Render<CandidateLinkPanel>(p => p
            .Add(c => c.Candidate, req)
            .Add(c => c.LinkableCandidates, Array.Empty<ExtractionCandidate>()));

        cut.Find("[data-testid='link-section-tests']").Should().NotBeNull();
        cut.Find("[data-testid='link-section-clarifications']").Should().NotBeNull();
        cut.FindAll("[data-testid='link-section-requirements']").Should().BeEmpty();
    }

    [Fact]
    public void ExtractionReview_ExplainsLinkingPanel()
    {
        var req = Candidate("FR-001: system validates credentials", ScenarioKind.Requirement);

        var cut = Render<CandidateLinkPanel>(p => p
            .Add(c => c.Candidate, req)
            .Add(c => c.LinkableCandidates, Array.Empty<ExtractionCandidate>()));

        var help = cut.Find("[data-testid='link-panel-help']").TextContent;
        help.Should().Contain("Connect requirements to tests");
        help.Should().Contain("success criteria");
        help.Should().Contain("improve traceability coverage");
        help.Should().Contain("affect Traceability & Coverage calculations");
    }

    [Fact]
    public void TestCandidate_ShowsRequirementsAndClarificationsSections()
    {
        var test = Candidate("Given login When valid Then success", ScenarioKind.Test);

        var cut = Render<CandidateLinkPanel>(p => p
            .Add(c => c.Candidate, test)
            .Add(c => c.LinkableCandidates, Array.Empty<ExtractionCandidate>()));

        cut.Find("[data-testid='link-section-requirements']").Should().NotBeNull();
        cut.Find("[data-testid='link-section-clarifications']").Should().NotBeNull();
        cut.FindAll("[data-testid='link-section-tests']").Should().BeEmpty();
    }

    [Fact]
    public void SearchFilter_HidesNonMatchingAvailableCandidates()
    {
        var req = Candidate("FR-001: system validates credentials", ScenarioKind.Requirement);
        var testA = Candidate("Given login succeeds", ScenarioKind.Test);
        var testB = Candidate("Given logout works", ScenarioKind.Test);

        var cut = Render<CandidateLinkPanel>(p => p
            .Add(c => c.Candidate, req)
            .Add(c => c.LinkableCandidates, [testA, testB]));

        cut.Find("[data-testid='link-search']").Input("login");

        var availableItems = cut.Find("[data-testid='link-section-tests']")
            .QuerySelectorAll("[data-testid='link-available-item']");
        availableItems.Should().HaveCount(1);
        availableItems[0].TextContent.Should().Contain("login");
    }

    [Fact]
    public void SearchFilter_ShowsNoMatchingCandidates_DisplaysEmptyMessage()
    {
        var req = Candidate("FR-001: system validates credentials", ScenarioKind.Requirement);
        var test = Candidate("Given login succeeds", ScenarioKind.Test);

        var cut = Render<CandidateLinkPanel>(p => p
            .Add(c => c.Candidate, req)
            .Add(c => c.LinkableCandidates, [test]));

        cut.Find("[data-testid='link-search']").Input("xyznotfound");

        var testsSection = cut.Find("[data-testid='link-section-tests']");
        testsSection.QuerySelectorAll("[data-testid='link-available-item']").Should().BeEmpty();
        testsSection.TextContent.Should().Contain("No matching candidates");
    }

    [Fact]
    public void SelfLink_NotShownInAvailableCandidates()
    {
        var req = Candidate("FR-001: system validates credentials", ScenarioKind.Requirement);

        var cut = Render<CandidateLinkPanel>(p => p
            .Add(c => c.Candidate, req)
            .Add(c => c.LinkableCandidates, [req]));

        cut.FindAll("[data-testid='link-available-item']").Should().BeEmpty();
    }

    [Fact]
    public void AlreadyLinked_NotShownInAvailableList()
    {
        var req = Candidate("FR-001: system validates credentials", ScenarioKind.Requirement);
        var test = Candidate("Given login When valid Then success", ScenarioKind.Test);
        var link = new CandidateLinkEntry(req.CandidateId, test.CandidateId, CandidateLinkType.RequirementTest);

        var cut = Render<CandidateLinkPanel>(p => p
            .Add(c => c.Candidate, req)
            .Add(c => c.LinkableCandidates, [test])
            .Add(c => c.Links, [link]));

        var testsSection = cut.Find("[data-testid='link-section-tests']");
        testsSection.QuerySelectorAll("[data-testid='link-available-item']").Should().BeEmpty();
        testsSection.QuerySelectorAll("[data-testid='link-linked-item']").Should().HaveCount(1);
    }

    [Fact]
    public void OnClose_InvokedWhenCloseButtonClicked()
    {
        var req = Candidate("FR-001: system validates credentials", ScenarioKind.Requirement);
        bool closed = false;

        var cut = Render<CandidateLinkPanel>(p => p
            .Add(c => c.Candidate, req)
            .Add(c => c.OnClose, () => { closed = true; return Task.CompletedTask; }));

        cut.Find("[data-testid='link-drawer-close']").Click();

        closed.Should().BeTrue();
    }
}
