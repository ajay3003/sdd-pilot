using BirkNext.Api.Services.Analysis;
using Xunit;

namespace BirkNext.Api.Tests.Services.Analysis;

/// <summary>
/// Contract tests for AnalysisPageModel structure.
/// All analysis pages must follow this contract.
/// </summary>
public class AnalysisPageContractTests
{
    [Fact]
    public void AnalysisPageModel_Has_Required_Fields()
    {
        var model = new AnalysisPageModel
        {
            Title = "Test",
            Description = "Test Description",
            ReadinessStatus = AnalysisStatus.Ready,
            RequiredInputs = ["Specification"],
            MissingInputs = [],
            Summary = new AnalysisSummary
            {
                CanRun = true,
                ReadinessMessage = "Ready to run"
            }
        };

        Assert.NotNull(model.Title);
        Assert.NotNull(model.Description);
        Assert.NotNull(model.RequiredInputs);
        Assert.NotNull(model.MissingInputs);
        Assert.NotNull(model.Summary);
    }

    [Fact]
    public void AnalysisPageModel_Requires_Summary_With_CanRun_Flag()
    {
        var model = new AnalysisPageModel
        {
            Title = "Test",
            Description = "Test",
            ReadinessStatus = AnalysisStatus.Blocked,
            RequiredInputs = ["Specification"],
            MissingInputs = ["Specification"],
            Summary = new AnalysisSummary
            {
                CanRun = false,
                ReadinessMessage = "Specification required"
            }
        };

        Assert.False(model.Summary.CanRun);
        Assert.NotEmpty(model.Summary.ReadinessMessage);
    }

    [Fact]
    public void AnalysisPageModel_Blocked_Status_Requires_MissingInputs()
    {
        var model = new AnalysisPageModel
        {
            Title = "Test",
            Description = "Test",
            ReadinessStatus = AnalysisStatus.Blocked,
            RequiredInputs = ["Specification", "Change Input"],
            MissingInputs = ["Specification"],
            Summary = new AnalysisSummary
            {
                CanRun = false,
                ReadinessMessage = "Missing: Specification"
            }
        };

        Assert.Equal(AnalysisStatus.Blocked, model.ReadinessStatus);
        Assert.NotEmpty(model.MissingInputs);
        Assert.False(model.Summary.CanRun);
    }

    [Fact]
    public void AnalysisPageModel_Ready_Status_Has_Empty_MissingInputs()
    {
        var model = new AnalysisPageModel
        {
            Title = "Test",
            Description = "Test",
            ReadinessStatus = AnalysisStatus.Ready,
            RequiredInputs = ["Specification"],
            MissingInputs = [],
            Summary = new AnalysisSummary
            {
                CanRun = true,
                ReadinessMessage = "Ready to run"
            }
        };

        Assert.Empty(model.MissingInputs);
        Assert.True(model.Summary.CanRun);
    }

    [Fact]
    public void AnalysisPageModel_Warning_Status_Has_CanRun_True()
    {
        var model = new AnalysisPageModel
        {
            Title = "Test",
            Description = "Test",
            ReadinessStatus = AnalysisStatus.Warning,
            RequiredInputs = ["Specification"],
            MissingInputs = [],
            Summary = new AnalysisSummary
            {
                CanRun = true,
                ReadinessMessage = "Optional artifacts missing, continuing with available data"
            }
        };

        Assert.Equal(AnalysisStatus.Warning, model.ReadinessStatus);
        Assert.True(model.Summary.CanRun);
    }

    [Fact]
    public void AnalysisPageModel_Empty_Status_When_No_Results()
    {
        var model = new AnalysisPageModel
        {
            Title = "Test",
            Description = "Test",
            ReadinessStatus = AnalysisStatus.Empty,
            RequiredInputs = ["Specification"],
            MissingInputs = [],
            Results = [],
            Summary = new AnalysisSummary
            {
                CanRun = true,
                ReadinessMessage = "Ready but no analysis run yet",
                TotalResults = 0
            }
        };

        Assert.Equal(AnalysisStatus.Empty, model.ReadinessStatus);
        Assert.Empty(model.Results);
        Assert.Equal(0, model.Summary.TotalResults);
    }

    [Fact]
    public void AnalysisPageModel_Fail_Status_Only_For_Runtime_Errors()
    {
        var model = new AnalysisPageModel
        {
            Title = "Test",
            Description = "Test",
            ReadinessStatus = AnalysisStatus.Fail,
            RequiredInputs = ["Specification"],
            MissingInputs = [],
            Summary = new AnalysisSummary
            {
                CanRun = false,
                ReadinessMessage = "Analysis failed: database connection error",
                TotalResults = 0
            }
        };

        Assert.Equal(AnalysisStatus.Fail, model.ReadinessStatus);
        Assert.False(model.Summary.CanRun);
    }

    [Fact]
    public void AnalysisResult_Has_Required_Fields()
    {
        var result = new AnalysisResult
        {
            Name = "REQ-001",
            Category = "CoverageGap",
            Status = AnalysisStatus.Ready,
            Severity = AnalysisSeverity.High,
            Summary = "Requirement not covered by tests",
            Details = "No test cases linked to this requirement",
            Recommendation = "Add test cases",
            RelatedArtifacts = ["REQ-001"]
        };

        Assert.NotNull(result.Name);
        Assert.NotNull(result.Category);
        Assert.NotNull(result.Summary);
    }

    [Fact]
    public void AnalysisSeverity_Has_Five_Levels()
    {
        var severities = new[]
        {
            AnalysisSeverity.Info,
            AnalysisSeverity.Low,
            AnalysisSeverity.Medium,
            AnalysisSeverity.High,
            AnalysisSeverity.Critical
        };

        Assert.Equal(5, severities.Length);
    }

    [Fact]
    public void AnalysisStatus_Distinguishes_Missing_Inputs_From_Failures()
    {
        // Missing inputs = Blocked or Warning (prerequisites not met)
        var blockedModel = new AnalysisPageModel
        {
            Title = "Test",
            Description = "Test",
            ReadinessStatus = AnalysisStatus.Blocked,
            RequiredInputs = ["Specification"],
            MissingInputs = ["Specification"],
            Summary = new AnalysisSummary { CanRun = false, ReadinessMessage = "Missing" }
        };

        // Actual failure = Fail (prerequisites met but analysis failed)
        var failedModel = new AnalysisPageModel
        {
            Title = "Test",
            Description = "Test",
            ReadinessStatus = AnalysisStatus.Fail,
            RequiredInputs = ["Specification"],
            MissingInputs = [],
            Summary = new AnalysisSummary { CanRun = false, ReadinessMessage = "Runtime error" }
        };

        Assert.Equal(AnalysisStatus.Blocked, blockedModel.ReadinessStatus);
        Assert.NotEmpty(blockedModel.MissingInputs);

        Assert.Equal(AnalysisStatus.Fail, failedModel.ReadinessStatus);
        Assert.Empty(failedModel.MissingInputs);
    }
}
