using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

public class ExtractionCandidateRowTests : BunitContext
{
    private static ExtractionCandidate MakeCandidate(
        string title = "The system shall allow login",
        ScenarioKind kind = ScenarioKind.Requirement,
        string? contextHeading = null) => new()
        {
            Title = title,
            Classification = kind,
            ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
            SourceBlockType = BlockType.UnorderedListItem,
            ContextHeading = contextHeading,
        };

    [Fact]
    public void ClassificationBadge_ShowsRequirementLabel()
    {
        var cut = Render<ExtractionCandidateRow>(p =>
            p.Add(c => c.Candidate, MakeCandidate(kind: ScenarioKind.Requirement)));

        cut.Find("[data-testid='classification-badge']").TextContent.Trim().Should().Be("REQUIREMENT");
    }

    [Fact]
    public void ClassificationBadge_ShowsTestLabel()
    {
        var cut = Render<ExtractionCandidateRow>(p =>
            p.Add(c => c.Candidate, MakeCandidate(kind: ScenarioKind.Test)));

        cut.Find("[data-testid='classification-badge']").TextContent.Trim().Should().Be("TEST");
    }

    [Fact]
    public void ClassificationBadge_ShowsNeedsClarificationLabel()
    {
        var cut = Render<ExtractionCandidateRow>(p =>
            p.Add(c => c.Candidate, MakeCandidate(kind: ScenarioKind.NeedsClarification)));

        cut.Find("[data-testid='classification-badge']").TextContent.Trim().Should().Be("NEEDS_CLARIFICATION");
    }

    [Fact]
    public void CandidateIdentity_UsesRequirementIdWhenPresent()
    {
        var cut = Render<ExtractionCandidateRow>(p =>
            p.Add(c => c.Candidate, MakeCandidate(title: "FR-001: The system MUST validate credentials")));

        cut.Find("[data-testid='candidate-identity']").TextContent.Trim().Should().Be("FR-001");
    }

    [Fact]
    public void CandidateIdentity_UsesTestForTestCandidates()
    {
        var cut = Render<ExtractionCandidateRow>(p =>
            p.Add(c => c.Candidate, MakeCandidate(kind: ScenarioKind.Test)));

        cut.Find("[data-testid='candidate-identity']").TextContent.Trim().Should().Be("TEST");
    }

    [Fact]
    public void CandidateIdentity_UsesNeedsClarificationForClarificationCandidates()
    {
        var cut = Render<ExtractionCandidateRow>(p =>
            p.Add(c => c.Candidate, MakeCandidate(kind: ScenarioKind.NeedsClarification)));

        cut.Find("[data-testid='candidate-identity']").TextContent.Trim().Should().Be("NEEDS_CLARIFICATION");
    }

    [Fact]
    public void ContextHeading_AppearsWhenNonNull()
    {
        var cut = Render<ExtractionCandidateRow>(p =>
            p.Add(c => c.Candidate, MakeCandidate(contextHeading: "Authentication")));

        cut.Find("[data-testid='context-heading']").TextContent.Should().Be("Authentication");
    }

    [Fact]
    public void ContextHeading_AbsentWhenNull()
    {
        var cut = Render<ExtractionCandidateRow>(p =>
            p.Add(c => c.Candidate, MakeCandidate(contextHeading: null)));

        cut.FindAll("[data-testid='context-heading']").Should().BeEmpty();
    }

    [Fact]
    public void CandidateTitle_RenderedAsTextNotMarkup()
    {
        const string xssTitle = "<script>alert(1)</script>";

        var cut = Render<ExtractionCandidateRow>(p =>
            p.Add(c => c.Candidate, MakeCandidate(title: xssTitle)));

        var titleEl = cut.Find("[data-testid='candidate-title']");
        titleEl.InnerHtml.Should().NotContain("<script>");
        titleEl.TextContent.Should().Be(xssTitle);
    }

    [Fact]
    public void CandidateTitle_XssPayload_InnerHtmlContainsHtmlEscapedForm()
    {
        const string xssTitle = "<script>alert(1)</script>";

        var cut = Render<ExtractionCandidateRow>(p =>
            p.Add(c => c.Candidate, MakeCandidate(title: xssTitle)));

        var titleEl = cut.Find("[data-testid='candidate-title']");
        // Blazor text binding must HTML-escape the content, not inject it raw
        titleEl.InnerHtml.Should().Contain("&lt;script&gt;");
        cut.FindAll("script").Should().BeEmpty();
    }

    [Fact]
    public void ContextHeading_XssPayload_RenderedAsEscapedText()
    {
        const string xssHeading = "<img src=x onerror=alert(1)>";

        var cut = Render<ExtractionCandidateRow>(p =>
            p.Add(c => c.Candidate, MakeCandidate(contextHeading: xssHeading)));

        var headingEl = cut.Find("[data-testid='context-heading']");
        headingEl.InnerHtml.Should().NotContain("<img");
        headingEl.InnerHtml.Should().Contain("&lt;img");
        headingEl.TextContent.Should().Be(xssHeading);
    }

    [Fact]
    public void Checkbox_UncheckedByDefault()
    {
        var cut = Render<ExtractionCandidateRow>(p =>
            p.Add(c => c.Candidate, MakeCandidate()));

        cut.Find("[data-testid='candidate-checkbox']").HasAttribute("checked").Should().BeFalse();
    }

    [Fact]
    public async Task TogglingCheckbox_RaisesOnSelectionToggledWithCorrectId()
    {
        var candidate = MakeCandidate();
        Guid? raisedId = null;

        var cut = Render<ExtractionCandidateRow>(p =>
        {
            p.Add(c => c.Candidate, candidate);
            p.Add(c => c.OnSelectionToggled, (Guid id) => { raisedId = id; return Task.CompletedTask; });
        });

        cut.Find("[data-testid='candidate-checkbox']").Change(true);

        await cut.WaitForStateAsync(() => raisedId is not null, timeout: TimeSpan.FromSeconds(1));

        raisedId.Should().Be(candidate.CandidateId);
    }

    [Fact]
    public void SaveState_Saved_ShowsSavedBadge()
    {
        var candidate = MakeCandidate();
        candidate.SaveState = CandidateSaveState.Saved;

        var cut = Render<ExtractionCandidateRow>(p =>
            p.Add(c => c.Candidate, candidate));

        cut.Find("[data-testid='save-saved']").TextContent.Should().Be("Saved");
    }

    [Fact]
    public void SaveState_Failed_ShowsSaveError()
    {
        var candidate = MakeCandidate();
        candidate.SaveState = CandidateSaveState.Failed;
        candidate.SaveError = "Title too long";

        var cut = Render<ExtractionCandidateRow>(p =>
            p.Add(c => c.Candidate, candidate));

        cut.Find("[data-testid='save-error']").TextContent.Should().Be("Title too long");
    }

    [Fact]
    public void SaveState_Saving_ShowsSpinner()
    {
        var candidate = MakeCandidate();
        candidate.SaveState = CandidateSaveState.Saving;

        var cut = Render<ExtractionCandidateRow>(p =>
            p.Add(c => c.Candidate, candidate));

        cut.Find("[data-testid='save-spinner']").Should().NotBeNull();
    }
}
