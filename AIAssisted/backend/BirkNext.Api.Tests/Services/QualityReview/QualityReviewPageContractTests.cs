using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services.QualityReview;
using Xunit;

namespace BirkNext.Api.Tests.Services.QualityReview;

/// <summary>
/// Contract tests that all Quality Review pages must satisfy.
/// Every quality review page (Quality Review, Frontend Quality Review, API Quality Review, Integration Quality Review)
/// must expose a structured QualityReviewPageModel with consistent properties.
/// </summary>
public class QualityReviewPageContractTests
{
    [Fact]
    public void QualityReviewPageModel_HasRequiredProperties()
    {
        // Every page model must have these properties
        var model = typeof(QualityReviewPageModel);
        Assert.NotNull(model.GetProperty(nameof(QualityReviewPageModel.Title)));
        Assert.NotNull(model.GetProperty(nameof(QualityReviewPageModel.Description)));
        Assert.NotNull(model.GetProperty(nameof(QualityReviewPageModel.Target)));
        Assert.NotNull(model.GetProperty(nameof(QualityReviewPageModel.ReadinessStatus)));
        Assert.NotNull(model.GetProperty(nameof(QualityReviewPageModel.Sections)));
        Assert.NotNull(model.GetProperty(nameof(QualityReviewPageModel.ReviewPacks)));
        Assert.NotNull(model.GetProperty(nameof(QualityReviewPageModel.Checks)));
        Assert.NotNull(model.GetProperty(nameof(QualityReviewPageModel.Actions)));
        Assert.NotNull(model.GetProperty(nameof(QualityReviewPageModel.Summary)));
    }

    [Fact]
    public void QualityReviewPack_HasRequiredProperties()
    {
        var pack = typeof(QualityReviewPack);
        Assert.NotNull(pack.GetProperty(nameof(QualityReviewPack.Name)));
        Assert.NotNull(pack.GetProperty(nameof(QualityReviewPack.Category)));
        Assert.NotNull(pack.GetProperty(nameof(QualityReviewPack.Status)));
        Assert.NotNull(pack.GetProperty(nameof(QualityReviewPack.Description)));
        Assert.NotNull(pack.GetProperty(nameof(QualityReviewPack.RequiredInputs)));
        Assert.NotNull(pack.GetProperty(nameof(QualityReviewPack.MissingInputs)));
    }

    [Fact]
    public void QualityReviewCheck_HasRequiredProperties()
    {
        var check = typeof(QualityReviewCheck);
        Assert.NotNull(check.GetProperty(nameof(QualityReviewCheck.Name)));
        Assert.NotNull(check.GetProperty(nameof(QualityReviewCheck.Category)));
        Assert.NotNull(check.GetProperty(nameof(QualityReviewCheck.Status)));
        Assert.NotNull(check.GetProperty(nameof(QualityReviewCheck.Description)));
    }

    [Fact]
    public void QualityReviewStatus_HasAllRequiredValues()
    {
        Assert.Equal(6, typeof(QualityReviewStatus).GetEnumValues().Length);
        var values = Enum.GetValues(typeof(QualityReviewStatus)).Cast<QualityReviewStatus>().ToList();
        Assert.Contains(QualityReviewStatus.Available, values);
        Assert.Contains(QualityReviewStatus.Blocked, values);
        Assert.Contains(QualityReviewStatus.Disabled, values);
        Assert.Contains(QualityReviewStatus.Selected, values);
        Assert.Contains(QualityReviewStatus.Warning, values);
        Assert.Contains(QualityReviewStatus.Fail, values);
    }

    [Fact]
    public void QualityReviewPageModel_CanBeCreatedWithValidData()
    {
        var model = new QualityReviewPageModel
        {
            Title = "Quality Review",
            Description = "Test description",
            Target = "http://localhost:5173",
            ReadinessStatus = QualityReviewStatus.Available,
            Sections = [],
            ReviewPacks = [],
            Checks = [],
            Actions = [],
            Summary = new()
        };

        Assert.Equal("Quality Review", model.Title);
        Assert.Equal("Test description", model.Description);
        Assert.Equal("http://localhost:5173", model.Target);
        Assert.Equal(QualityReviewStatus.Available, model.ReadinessStatus);
    }

    [Fact]
    public void QualityReviewPack_StatusCanBeAnyValidValue()
    {
        var statuses = new[]
        {
            QualityReviewStatus.Available,
            QualityReviewStatus.Blocked,
            QualityReviewStatus.Disabled,
            QualityReviewStatus.Selected,
            QualityReviewStatus.Warning,
            QualityReviewStatus.Fail
        };

        foreach (var status in statuses)
        {
            var pack = new QualityReviewPack
            {
                Name = "Test Pack",
                Category = "Test",
                Status = status,
                Description = "Test"
            };
            Assert.Equal(status, pack.Status);
        }
    }

    [Fact]
    public void QualityReviewCheck_StatusCanBeAnyValidValue()
    {
        var statuses = new[]
        {
            QualityReviewStatus.Available,
            QualityReviewStatus.Blocked,
            QualityReviewStatus.Disabled,
            QualityReviewStatus.Selected,
            QualityReviewStatus.Warning,
            QualityReviewStatus.Fail
        };

        foreach (var status in statuses)
        {
            var check = new QualityReviewCheck
            {
                Name = "Test Check",
                Category = "Test",
                Status = status,
                Description = "Test"
            };
            Assert.Equal(status, check.Status);
        }
    }

    [Fact]
    public void QualityReviewPack_MissingInputsIsList()
    {
        var pack = new QualityReviewPack
        {
            Name = "Test",
            Category = "Test",
            Status = QualityReviewStatus.Available,
            Description = "Test",
            MissingInputs = ["specification.md", "plan.md"]
        };

        Assert.Equal(2, pack.MissingInputs.Count);
        Assert.Contains("specification.md", pack.MissingInputs);
        Assert.Contains("plan.md", pack.MissingInputs);
    }

    [Fact]
    public void QualityReviewPageModel_SummaryIsAlwaysPresent()
    {
        var model = new QualityReviewPageModel
        {
            Title = "Test",
            Summary = new QualityReviewSummary
            {
                TotalPacks = 5,
                AvailablePacks = 3,
                BlockedPacks = 2,
                CanRun = true
            }
        };

        Assert.NotNull(model.Summary);
        Assert.Equal(5, model.Summary.TotalPacks);
        Assert.Equal(3, model.Summary.AvailablePacks);
        Assert.Equal(2, model.Summary.BlockedPacks);
        Assert.True(model.Summary.CanRun);
    }

    [Fact]
    public void QualityReviewPageModel_AllPropertiesAreInitializable()
    {
        var packs = new[]
        {
            new QualityReviewPack { Name = "Pack1", Category = "Cat1", Status = QualityReviewStatus.Available },
            new QualityReviewPack { Name = "Pack2", Category = "Cat2", Status = QualityReviewStatus.Blocked }
        };

        var checks = new[]
        {
            new QualityReviewCheck { Name = "Check1", Category = "Cat1", Status = QualityReviewStatus.Available },
            new QualityReviewCheck { Name = "Check2", Category = "Cat2", Status = QualityReviewStatus.Disabled }
        };

        var sections = new[]
        {
            new QualityReviewSection
            {
                Title = "Section1",
                Description = "Desc1",
                Checks = new List<QualityReviewCheck> { checks[0] }
            }
        };

        var model = new QualityReviewPageModel
        {
            Title = "Complete Model",
            Description = "Full model with all properties",
            Target = "http://test:5000",
            ReadinessStatus = QualityReviewStatus.Available,
            Sections = sections.ToList(),
            ReviewPacks = packs.ToList(),
            Checks = checks.ToList(),
            Actions = ["Run Audit"],
            Summary = new QualityReviewSummary { TotalPacks = 2, AvailablePacks = 1, BlockedPacks = 1 }
        };

        Assert.NotNull(model);
        Assert.Equal(2, model.ReviewPacks.Count);
        Assert.Equal(2, model.Checks.Count);
        Assert.Single(model.Sections);
        Assert.Single(model.Actions);
    }
}
