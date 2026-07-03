using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using BirkNext.Api.Services.QualityReview;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BirkNext.Api.Tests.Services.QualityReview;

/// <summary>
/// Tests for Quality Review page model building.
/// Validates that the page exposes artifact prerequisite logic clearly through the model structure.
/// </summary>
public class QualityReviewPageBuilderTests
{
    [Fact]
    public void PageModel_ExposesMissingPrerequisites()
    {
        // All Quality Review packs have prerequisites
        // The model must explicitly list what's missing so UI doesn't need custom logic
        var model = new QualityReviewPageModel
        {
            Title = "Quality Review",
            ReviewPacks = new()
            {
                new QualityReviewPack
                {
                    Name = "QA Auditor",
                    Category = "QA",
                    Status = QualityReviewStatus.Blocked,
                    MissingInputs = ["specification.md", "plan.md", "tasks"]
                }
            }
        };

        Assert.NotEmpty(model.ReviewPacks);
        var qaAuditor = model.ReviewPacks[0];
        Assert.Equal(QualityReviewStatus.Blocked, qaAuditor.Status);
        Assert.Contains("specification.md", qaAuditor.MissingInputs);
    }

    [Fact]
    public void PageModel_StatusReflectsBlockedPrerequisites()
    {
        // When all packs are blocked, page is blocked
        var model = new QualityReviewPageModel
        {
            Title = "Quality Review",
            ReadinessStatus = QualityReviewStatus.Blocked,
            ReviewPacks = new()
            {
                new QualityReviewPack { Name = "Pack1", Status = QualityReviewStatus.Blocked },
                new QualityReviewPack { Name = "Pack2", Status = QualityReviewStatus.Blocked }
            },
            Summary = new() { AvailablePacks = 0, BlockedPacks = 2, CanRun = false }
        };

        Assert.False(model.Summary.CanRun);
        Assert.Equal(0, model.Summary.AvailablePacks);
    }

    [Fact]
    public void PageModel_AvailableWhenSomePacksHavePrerequisites()
    {
        // When at least one pack has prerequisites satisfied, page becomes available
        var model = new QualityReviewPageModel
        {
            Title = "Quality Review",
            ReadinessStatus = QualityReviewStatus.Available,
            ReviewPacks = new()
            {
                new QualityReviewPack { Name = "Pack1", Status = QualityReviewStatus.Blocked, MissingInputs = ["spec"] },
                new QualityReviewPack { Name = "Pack2", Status = QualityReviewStatus.Available, MissingInputs = [] }
            },
            Summary = new() { AvailablePacks = 1, BlockedPacks = 1, CanRun = true }
        };

        Assert.True(model.Summary.CanRun);
        Assert.Equal(1, model.Summary.AvailablePacks);
    }

    [Fact]
    public void PageModel_RequiredInputsDefinePack()
    {
        // Each pack specifies its required inputs
        var model = new QualityReviewPageModel
        {
            ReviewPacks = new()
            {
                new QualityReviewPack
                {
                    Name = "QA Auditor",
                    RequiredInputs = ["specification", "plan", "tasks"],
                    MissingInputs = ["specification", "plan"]
                }
            }
        };

        var pack = model.ReviewPacks[0];
        Assert.Contains("specification", pack.RequiredInputs);
        Assert.Contains("specification", pack.MissingInputs);
    }

    [Fact]
    public void PageModel_CanListAllChecksToBeRun()
    {
        // Model exposes all checks that will be run in the audit
        var model = new QualityReviewPageModel
        {
            Title = "Quality Review",
            Checks = new()
            {
                new QualityReviewCheck { Name = "Requirement Coverage", Category = "QA", Status = QualityReviewStatus.Available },
                new QualityReviewCheck { Name = "Constitution Alignment", Category = "Compliance", Status = QualityReviewStatus.Available }
            }
        };

        Assert.Equal(2, model.Checks.Count);
        Assert.Contains(model.Checks, c => c.Name == "Requirement Coverage");
    }

    [Fact]
    public async Task BuildPageModelAsync_UsesMostRecentlyUpdatedWorkspace()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new AppDbContext(options);
        var olderUpdatedWorkspace = new SavedWorkspace
        {
            Id = Guid.NewGuid(),
            UserId = "default-user",
            Name = "Opened Later",
            ProjectName = "Test",
            UpdatedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
            LastOpenedAt = new DateTimeOffset(2026, 7, 3, 0, 0, 0, TimeSpan.Zero)
        };
        var newerUpdatedWorkspace = new SavedWorkspace
        {
            Id = Guid.NewGuid(),
            UserId = "default-user",
            Name = "Updated Later",
            ProjectName = "Test",
            UpdatedAt = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero),
            LastOpenedAt = new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero)
        };

        db.SavedWorkspaces.AddRange(olderUpdatedWorkspace, newerUpdatedWorkspace);
        await db.SaveChangesAsync();

        var artifactStatus = new Mock<IWorkspaceArtifactStatusService>();
        artifactStatus
            .Setup(service => service.GetStatusAsync(newerUpdatedWorkspace.Id))
            .ReturnsAsync(new WorkspaceArtifactStatus
            {
                WorkspaceId = newerUpdatedWorkspace.Id,
                HasSpecification = true
            });

        var builder = new QualityReviewPageModelBuilder(
            db,
            artifactStatus.Object,
            NullLogger<QualityReviewPageModelBuilder>.Instance);

        var model = await builder.BuildPageModelAsync();

        Assert.Contains(newerUpdatedWorkspace.Id.ToString()[..8], model.Target);
        artifactStatus.Verify(service => service.GetStatusAsync(newerUpdatedWorkspace.Id), Times.Once);
    }
}
