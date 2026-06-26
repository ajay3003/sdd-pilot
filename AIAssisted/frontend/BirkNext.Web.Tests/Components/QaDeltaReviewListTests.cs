using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Moq;

namespace BirkNext.Web.Tests.Components;

public class QaDeltaReviewListTests : BunitContext
{
    [Fact]
    public void EmptyList_ShowsEmptyState()
    {
        var cut = Render<QaDeltaReviewList>(p => p
            .Add(x => x.Reviews, [])
            .Add(x => x.OnDeleteRequested, EventCallback.Factory.Create<string>(this, (string _) => { }))
            .Add(x => x.DeletingId, (string?)null)
            .Add(x => x.DeleteErrors, null));

        cut.Find("[data-testid='review-list-empty']").Should().NotBeNull();
        cut.Find("[data-testid='review-list-empty']").TextContent.Should().Contain("No Delta Reviews Yet");
        cut.Find("[data-testid='review-list-empty']").TextContent.Should().Contain("Open Compare Specs");
        cut.FindAll("[data-testid='review-card']").Should().BeEmpty();
    }

    [Fact]
    public void Reviews_RendersOneCardPerReview()
    {
        var reviews = new[]
        {
            MakeReview("rev-1", "First Review"),
            MakeReview("rev-2", "Second Review"),
        };

        var cut = Render<QaDeltaReviewList>(p => p
            .Add(x => x.Reviews, reviews)
            .Add(x => x.OnDeleteRequested, EventCallback.Factory.Create<string>(this, (string _) => { }))
            .Add(x => x.DeletingId, (string?)null)
            .Add(x => x.DeleteErrors, null));

        cut.FindAll("[data-testid='review-card']").Should().HaveCount(2);
        cut.Markup.Should().Contain("First Review");
        cut.Markup.Should().Contain("Second Review");
    }

    [Fact]
    public void Reviews_ShowsSummaryMetrics()
    {
        var review = MakeReview("rev-1", "My Review", addedReq: 3, modifiedReq: 1, removedReq: 2);

        var cut = Render<QaDeltaReviewList>(p => p
            .Add(x => x.Reviews, new[] { review })
            .Add(x => x.OnDeleteRequested, EventCallback.Factory.Create<string>(this, (string _) => { }))
            .Add(x => x.DeletingId, (string?)null)
            .Add(x => x.DeleteErrors, null));

        var cardText = cut.Find("[data-testid='review-card']").TextContent;
        cardText.Should().Contain("+3");
        cardText.Should().Contain("~1");
        cardText.Should().Contain("-2");
    }

    [Fact]
    public void Review_WithCoverageImpact_ShowsAttentionStatus()
    {
        var review = MakeReview("rev-1", "My Review", impactedTests: 2);

        var cut = Render<QaDeltaReviewList>(p => p
            .Add(x => x.Reviews, new[] { review })
            .Add(x => x.OnDeleteRequested, EventCallback.Factory.Create<string>(this, (string _) => { }))
            .Add(x => x.DeletingId, (string?)null)
            .Add(x => x.DeleteErrors, null));

        cut.Find("[data-testid='review-card']").TextContent.Should().Contain("Attention");
        cut.Find("[data-testid='review-card']").TextContent.Should().Contain("Coverage impact detected");
    }

    [Fact]
    public void OpenLink_HasCorrectHref()
    {
        var review = MakeReview("rev-abc", "My Review");

        var cut = Render<QaDeltaReviewList>(p => p
            .Add(x => x.Reviews, new[] { review })
            .Add(x => x.OnDeleteRequested, EventCallback.Factory.Create<string>(this, (string _) => { }))
            .Add(x => x.DeletingId, (string?)null)
            .Add(x => x.DeleteErrors, null));

        cut.Find("[data-testid='review-open-btn-rev-abc']")
            .GetAttribute("href").Should().Contain("compare/reviews/rev-abc");
    }

    [Fact]
    public async Task DeleteButton_Click_ThenConfirm_InvokesCallback()
    {
        var callbackId = string.Empty;
        var review = MakeReview("rev-1", "My Review");

        var cut = Render<QaDeltaReviewList>(p => p
            .Add(x => x.Reviews, new[] { review })
            .Add(x => x.OnDeleteRequested, (string id) => { callbackId = id; return Task.CompletedTask; })
            .Add(x => x.DeletingId, (string?)null)
            .Add(x => x.DeleteErrors, null));

        cut.Find("[data-testid='delete-btn-rev-1']").Click();
        cut.Find("[data-testid='delete-confirm-btn']").Click();

        await Task.Delay(50);
        callbackId.Should().Be("rev-1");
    }

    [Fact]
    public void DeleteError_ShowsInline()
    {
        var review = MakeReview("rev-1", "My Review");
        var deleteErrors = new Dictionary<string, string> { ["rev-1"] = "Review not found" };

        var cut = Render<QaDeltaReviewList>(p => p
            .Add(x => x.Reviews, new[] { review })
            .Add(x => x.OnDeleteRequested, EventCallback.Factory.Create<string>(this, (string _) => { }))
            .Add(x => x.DeletingId, (string?)null)
            .Add(x => x.DeleteErrors, deleteErrors));

        cut.Find("[data-testid='delete-error-rev-1']")
            .TextContent.Should().Contain("Review not found");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IGetQaDeltaReviews_QaDeltaReviews MakeReview(
        string id, string title, int addedReq = 0, int modifiedReq = 0, int removedReq = 0, int impactedTests = 0)
    {
        var summaryDto = new DeltaSummaryDto(
            AddedRequirements: addedReq, ModifiedRequirements: modifiedReq,
            RemovedRequirements: removedReq, UnchangedRequirements: 0,
            AddedTests: 0, RemovedTests: 0, PotentiallyImpactedTests: impactedTests,
            AddedClarifications: 0, RemovedClarifications: 0, StillUnresolvedClarifications: 0,
            UncoveredRequirements: 0, NewClarificationRisks: 0);
        var summaryJson = System.Text.Json.JsonSerializer.Serialize(summaryDto,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

        var mock = new Mock<IGetQaDeltaReviews_QaDeltaReviews>();
        mock.Setup(r => r.Id).Returns(id);
        mock.Setup(r => r.Title).Returns(title);
        mock.Setup(r => r.ProjectId).Returns("proj-1");
        mock.Setup(r => r.CreatedAt).Returns(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));
        mock.Setup(r => r.OldSpecFileName).Returns((string?)null);
        mock.Setup(r => r.NewSpecFileName).Returns((string?)null);
        mock.Setup(r => r.AnalysisProfile).Returns("Speckit");
        mock.Setup(r => r.SummaryJson).Returns(summaryJson);
        return mock.Object;
    }
}
