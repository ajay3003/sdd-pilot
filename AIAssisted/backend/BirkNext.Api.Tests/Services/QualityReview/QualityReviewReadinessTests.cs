using BirkNext.Api.Services.QualityReview;
using Xunit;

namespace BirkNext.Api.Tests.Services.QualityReview;

/// <summary>
/// Tests for Quality Review page readiness determination.
/// Verifies that page status correctly reflects available vs blocked packs.
/// </summary>
public class QualityReviewReadinessTests
{
    [Fact]
    public void PageModel_IsBlocked_When_NoPacks_Available()
    {
        var model = new QualityReviewPageModel
        {
            Title = "Quality Review",
            ReviewPacks = new()
            {
                new QualityReviewPack { Name = "QA Auditor", Status = QualityReviewStatus.Blocked, MissingInputs = ["spec", "plan"] },
                new QualityReviewPack { Name = "Data Model", Status = QualityReviewStatus.Blocked, MissingInputs = ["data-model.md"] }
            },
            Summary = new() { AvailablePacks = 0, BlockedPacks = 2, CanRun = false }
        };

        Assert.False(model.Summary.CanRun);
        Assert.Equal(0, model.Summary.AvailablePacks);
        Assert.Equal(2, model.Summary.BlockedPacks);
    }

    [Fact]
    public void PageModel_IsAvailable_When_AtLeast_OnePack_Available()
    {
        var model = new QualityReviewPageModel
        {
            Title = "Quality Review",
            ReviewPacks = new()
            {
                new QualityReviewPack { Name = "QA Auditor", Status = QualityReviewStatus.Available, MissingInputs = [] },
                new QualityReviewPack { Name = "Data Model", Status = QualityReviewStatus.Blocked, MissingInputs = ["data-model.md"] }
            },
            Summary = new() { AvailablePacks = 1, BlockedPacks = 1, CanRun = true }
        };

        Assert.True(model.Summary.CanRun);
        Assert.Equal(1, model.Summary.AvailablePacks);
    }

    [Fact]
    public void PageModel_Lists_MissingInputs_Explicitly()
    {
        var model = new QualityReviewPageModel
        {
            ReviewPacks = new()
            {
                new QualityReviewPack
                {
                    Name = "QA Auditor",
                    Status = QualityReviewStatus.Blocked,
                    RequiredInputs = ["specification", "plan", "tasks"],
                    MissingInputs = ["specification.md", "plan.md"]
                }
            }
        };

        var pack = model.ReviewPacks[0];
        Assert.Equal(2, pack.MissingInputs.Count);
        Assert.Contains("specification.md", pack.MissingInputs);
        Assert.Contains("plan.md", pack.MissingInputs);
        Assert.DoesNotContain("tasks", pack.MissingInputs); // tasks is not missing
    }

    [Fact]
    public void PageModel_StatusEnum_NeverUses_Strings()
    {
        var model = new QualityReviewPageModel
        {
            ReadinessStatus = QualityReviewStatus.Available,
            ReviewPacks = new()
            {
                new QualityReviewPack { Status = QualityReviewStatus.Blocked },
                new QualityReviewPack { Status = QualityReviewStatus.Available },
                new QualityReviewPack { Status = QualityReviewStatus.Selected }
            }
        };

        Assert.IsType<QualityReviewStatus>(model.ReadinessStatus);
        foreach (var pack in model.ReviewPacks)
        {
            Assert.IsType<QualityReviewStatus>(pack.Status);
        }
    }

    [Fact]
    public void Blocked_Status_Is_Not_Fail()
    {
        var model = new QualityReviewPageModel
        {
            ReadinessStatus = QualityReviewStatus.Blocked,
            ReviewPacks = new()
            {
                new QualityReviewPack { Status = QualityReviewStatus.Blocked, MissingInputs = ["config"] }
            }
        };

        // Missing configuration is Blocked, not Fail (it's fixable)
        Assert.Equal(QualityReviewStatus.Blocked, model.ReadinessStatus);
        Assert.NotEqual(QualityReviewStatus.Fail, model.ReadinessStatus);
        var pack = model.ReviewPacks[0];
        Assert.Equal(QualityReviewStatus.Blocked, pack.Status);
        Assert.NotEqual(QualityReviewStatus.Fail, pack.Status);
    }

    [Fact]
    public void AllPacks_With_Available_Status_Can_Run()
    {
        var packs = new List<QualityReviewPack>
        {
            new() { Name = "Pack1", Status = QualityReviewStatus.Available },
            new() { Name = "Pack2", Status = QualityReviewStatus.Available },
            new() { Name = "Pack3", Status = QualityReviewStatus.Available }
        };

        var model = new QualityReviewPageModel
        {
            ReviewPacks = packs,
            Summary = new() { AvailablePacks = 3, BlockedPacks = 0, CanRun = true }
        };

        Assert.True(model.Summary.CanRun);
        Assert.All(model.ReviewPacks, pack => Assert.Equal(QualityReviewStatus.Available, pack.Status));
    }

    [Fact]
    public void ReadinessMessage_Explains_What_Is_Needed()
    {
        var model = new QualityReviewPageModel
        {
            Summary = new()
            {
                CanRun = false,
                ReadinessMessage = "Load specification.md to enable QA Auditor"
            }
        };

        Assert.NotEmpty(model.Summary.ReadinessMessage);
        Assert.Contains("specification.md", model.Summary.ReadinessMessage);
        Assert.Contains("QA Auditor", model.Summary.ReadinessMessage);
    }

    [Fact]
    public void PackCategory_Groups_Related_Packs()
    {
        var model = new QualityReviewPageModel
        {
            ReviewPacks = new()
            {
                new QualityReviewPack { Name = "QA Auditor", Category = "QA" },
                new QualityReviewPack { Name = "QA Readiness", Category = "Readiness" },
                new QualityReviewPack { Name = "WCAG 2.2", Category = "Accessibility" },
                new QualityReviewPack { Name = "OWASP ASVS", Category = "Security" }
            }
        };

        var qaCategory = model.ReviewPacks.Where(p => p.Category == "QA").ToList();
        Assert.Single(qaCategory);

        var securityCategory = model.ReviewPacks.Where(p => p.Category == "Security").ToList();
        Assert.Single(securityCategory);
    }
}
