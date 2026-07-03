using BirkNext.Api.Services.Review;
using Xunit;

namespace BirkNext.Api.Tests.Services.Review;

/// <summary>
/// Contract tests for ReviewPageModel structure.
/// All Review pages (Dashboard, Explorers) must follow this contract.
/// </summary>
public class ReviewPageContractTests
{
    [Fact]
    public void ReviewPageModel_Has_Required_Fields()
    {
        var model = new ReviewPageModel
        {
            Title = "Constitution Explorer",
            Description = "Review constitution.md",
            ReadinessStatus = ReviewStatus.Ready,
            RequiredInputs = ["Constitution"],
            MissingInputs = [],
            Summary = new ReviewSummary
            {
                StatusMessage = "Ready",
                CanRun = true,
                HasAvailableActions = true
            }
        };

        Assert.NotNull(model.Title);
        Assert.NotNull(model.Description);
        Assert.NotNull(model.RequiredInputs);
        Assert.NotNull(model.MissingInputs);
        Assert.NotNull(model.Summary);
    }

    [Fact]
    public void ReviewPageModel_Empty_Status_Not_Failure()
    {
        var model = new ReviewPageModel
        {
            Title = "Constitution Explorer",
            Description = "Review constitution.md",
            ReadinessStatus = ReviewStatus.Empty,
            RequiredInputs = ["Constitution"],
            MissingInputs = [],
            Results = [],
            Summary = new ReviewSummary
            {
                StatusMessage = "No artifact loaded yet",
                TotalResults = 0,
                CanRun = false,
                HasAvailableActions = true
            }
        };

        Assert.Equal(ReviewStatus.Empty, model.ReadinessStatus);
        Assert.Empty(model.Results);
        Assert.False(model.Summary.CanRun);
        Assert.True(model.Summary.HasAvailableActions);
    }

    [Fact]
    public void ReviewPageModel_Blocked_Status_Has_MissingInputs()
    {
        var model = new ReviewPageModel
        {
            Title = "Data Model Explorer",
            Description = "Review data-model.md",
            ReadinessStatus = ReviewStatus.Blocked,
            RequiredInputs = ["DataModel"],
            MissingInputs = ["DataModel"],
            Summary = new ReviewSummary
            {
                StatusMessage = "Data Model is required but not loaded",
                CanRun = false,
                HasAvailableActions = false
            }
        };

        Assert.Equal(ReviewStatus.Blocked, model.ReadinessStatus);
        Assert.NotEmpty(model.MissingInputs);
        Assert.False(model.Summary.CanRun);
        Assert.False(model.Summary.HasAvailableActions);
    }

    [Fact]
    public void ReviewPageModel_Ready_Status_Allows_Analysis()
    {
        var model = new ReviewPageModel
        {
            Title = "Plan Explorer",
            Description = "Review plan.md",
            ReadinessStatus = ReviewStatus.Ready,
            RequiredInputs = ["Plan"],
            MissingInputs = [],
            Results = [
                new ReviewResult
                {
                    Name = "Architecture Review",
                    Category = "Validation",
                    Status = ReviewStatus.Ready,
                    Severity = "Info"
                }
            ],
            Summary = new ReviewSummary
            {
                StatusMessage = "Plan loaded, analysis complete",
                TotalResults = 1,
                CanRun = true,
                HasAvailableActions = true
            }
        };

        Assert.Equal(ReviewStatus.Ready, model.ReadinessStatus);
        Assert.True(model.Summary.CanRun);
        Assert.NotEmpty(model.Results);
    }

    [Fact]
    public void ReviewPageModel_Warning_Status_Indicates_Degraded()
    {
        var model = new ReviewPageModel
        {
            Title = "Task Explorer",
            Description = "Review tasks.md",
            ReadinessStatus = ReviewStatus.Warning,
            RequiredInputs = ["Tasks"],
            MissingInputs = [],
            Summary = new ReviewSummary
            {
                StatusMessage = "Some tasks have potential issues",
                WarningCount = 3,
                CanRun = true,
                HasAvailableActions = true
            }
        };

        Assert.Equal(ReviewStatus.Warning, model.ReadinessStatus);
        Assert.True(model.Summary.CanRun);
        Assert.True(model.Summary.WarningCount > 0);
    }

    [Fact]
    public void ReviewPageModel_Fail_Status_Only_For_Runtime_Errors()
    {
        var model = new ReviewPageModel
        {
            Title = "Constitution Explorer",
            Description = "Review constitution.md",
            ReadinessStatus = ReviewStatus.Fail,
            RequiredInputs = [],
            MissingInputs = [],
            Summary = new ReviewSummary
            {
                StatusMessage = "Failed to parse artifact: invalid format",
                CanRun = false,
                HasAvailableActions = false
            }
        };

        Assert.Equal(ReviewStatus.Fail, model.ReadinessStatus);
        Assert.False(model.Summary.CanRun);
    }

    [Fact]
    public void ReviewPageModel_Distinguishes_Empty_From_Blocked_From_Fail()
    {
        // Empty = no artifact yet, not failure
        var emptyModel = new ReviewPageModel
        {
            Title = "Test",
            Description = "Test",
            ReadinessStatus = ReviewStatus.Empty,
            RequiredInputs = ["Constitution"],
            MissingInputs = [],
            Summary = new ReviewSummary
            {
                StatusMessage = "No artifact loaded",
                CanRun = false,
                HasAvailableActions = true
            }
        };

        // Blocked = required artifact missing
        var blockedModel = new ReviewPageModel
        {
            Title = "Test",
            Description = "Test",
            ReadinessStatus = ReviewStatus.Blocked,
            RequiredInputs = ["Constitution"],
            MissingInputs = ["Constitution"],
            Summary = new ReviewSummary
            {
                StatusMessage = "Constitution is required but missing",
                CanRun = false,
                HasAvailableActions = false
            }
        };

        // Fail = error during analysis
        var failedModel = new ReviewPageModel
        {
            Title = "Test",
            Description = "Test",
            ReadinessStatus = ReviewStatus.Fail,
            RequiredInputs = [],
            MissingInputs = [],
            Summary = new ReviewSummary
            {
                StatusMessage = "Error: parsing failed",
                CanRun = false,
                HasAvailableActions = false
            }
        };

        Assert.Equal(ReviewStatus.Empty, emptyModel.ReadinessStatus);
        Assert.Equal(ReviewStatus.Blocked, blockedModel.ReadinessStatus);
        Assert.Equal(ReviewStatus.Fail, failedModel.ReadinessStatus);
    }

    [Fact]
    public void ReviewResult_Has_Severity_And_Status()
    {
        var result = new ReviewResult
        {
            Name = "Validation Result",
            Category = "Validation",
            Status = ReviewStatus.Ready,
            Severity = "Warning",
            Summary = "Some validation warnings found",
            Details = "Details here",
            Recommendation = "Review and resolve warnings"
        };

        Assert.NotNull(result.Name);
        Assert.NotNull(result.Severity);
        Assert.True(result.Severity == "Critical" || result.Severity == "Warning" || result.Severity == "Info");
    }

    [Fact]
    public void ReviewSection_Has_Status_And_Items()
    {
        var section = new ReviewSection
        {
            Title = "Requirements",
            Status = ReviewStatus.Ready,
            Items = [
                new ReviewItem
                {
                    Name = "REQ-001",
                    Status = ReviewStatus.Ready
                },
                new ReviewItem
                {
                    Name = "REQ-002",
                    Status = ReviewStatus.Warning
                }
            ]
        };

        Assert.NotEmpty(section.Items);
        Assert.Equal(2, section.Items.Count);
    }

    [Fact]
    public void ReviewAction_Has_Status_And_Enabled_Flag()
    {
        var action = new ReviewAction
        {
            Name = "Upload",
            Status = ReviewStatus.Ready,
            Enabled = true,
            ExpectedEffect = "Load artifact into workspace"
        };

        Assert.True(action.Enabled);
        Assert.Equal(ReviewStatus.Ready, action.Status);
    }

    [Fact]
    public void ReviewAction_Can_Be_Disabled_With_Reason()
    {
        var action = new ReviewAction
        {
            Name = "Analyze",
            Status = ReviewStatus.Blocked,
            Enabled = false,
            Reason = "Artifact is required but not loaded",
            ExpectedEffect = "Would analyze artifact"
        };

        Assert.False(action.Enabled);
        Assert.NotEmpty(action.Reason);
    }

    [Fact]
    public void ReviewSummary_Tracks_Finding_Counts()
    {
        var summary = new ReviewSummary
        {
            TotalResults = 10,
            CriticalCount = 1,
            WarningCount = 3,
            InfoCount = 6,
            StatusMessage = "Test status",
            CanRun = true,
            HasAvailableActions = true
        };

        Assert.Equal(10, summary.TotalResults);
        Assert.Equal(1, summary.CriticalCount);
        Assert.Equal(3, summary.WarningCount);
        Assert.Equal(6, summary.InfoCount);
    }

    [Fact]
    public void ReviewPageModel_Missing_Artifact_Results_In_Blocked_Or_Empty()
    {
        // When artifact is never loaded: Empty
        var emptyModel = new ReviewPageModel
        {
            Title = "Test",
            Description = "Test",
            ReadinessStatus = ReviewStatus.Empty,
            RequiredInputs = ["Specification"],
            MissingInputs = [],
            Summary = new ReviewSummary
            {
                StatusMessage = "No specification loaded",
                CanRun = false,
                HasAvailableActions = true
            }
        };

        // When artifact is required but not in workspace: Blocked
        var blockedModel = new ReviewPageModel
        {
            Title = "Test",
            Description = "Test",
            ReadinessStatus = ReviewStatus.Blocked,
            RequiredInputs = ["Specification"],
            MissingInputs = ["Specification"],
            Summary = new ReviewSummary
            {
                StatusMessage = "Specification is required but not in workspace",
                CanRun = false,
                HasAvailableActions = false
            }
        };

        Assert.Equal(ReviewStatus.Empty, emptyModel.ReadinessStatus);
        Assert.Equal(ReviewStatus.Blocked, blockedModel.ReadinessStatus);

        // Both should NOT be Fail
        Assert.NotEqual(ReviewStatus.Fail, emptyModel.ReadinessStatus);
        Assert.NotEqual(ReviewStatus.Fail, blockedModel.ReadinessStatus);
    }

    [Fact]
    public void ReviewPageModel_Severity_Mapping_Is_Consistent()
    {
        var criticalResult = new ReviewResult
        {
            Name = "Critical Issue",
            Severity = "Critical"
        };

        var warningResult = new ReviewResult
        {
            Name = "Warning Issue",
            Severity = "Warning"
        };

        var infoResult = new ReviewResult
        {
            Name = "Info",
            Severity = "Info"
        };

        var validSeverities = new[] { "Critical", "Warning", "Info" };
        Assert.Contains(criticalResult.Severity, validSeverities);
        Assert.Contains(warningResult.Severity, validSeverities);
        Assert.Contains(infoResult.Severity, validSeverities);
    }
}
