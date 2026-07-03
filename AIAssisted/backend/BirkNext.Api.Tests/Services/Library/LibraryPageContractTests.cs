using BirkNext.Api.Services.Library;
using Xunit;

namespace BirkNext.Api.Tests.Services.Library;

/// <summary>
/// Contract tests for LibraryPageModel structure.
/// All library pages must follow this contract.
/// </summary>
public class LibraryPageContractTests
{
    [Fact]
    public void LibraryPageModel_Has_Required_Fields()
    {
        var model = new LibraryPageModel
        {
            Title = "QA Artifact Library",
            Description = "Test description",
            ReadinessStatus = LibraryStatus.Ready,
            RequiredInputs = [],
            MissingInputs = [],
            Summary = new LibrarySummary
            {
                StatusMessage = "Ready",
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
    public void LibraryPageModel_Empty_Status_Not_Failure()
    {
        var model = new LibraryPageModel
        {
            Title = "QA Artifact Library",
            Description = "Test",
            ReadinessStatus = LibraryStatus.Empty,
            RequiredInputs = [],
            MissingInputs = [],
            Items = [],
            Summary = new LibrarySummary
            {
                StatusMessage = "No artifacts loaded yet",
                TotalItems = 0,
                HasAvailableActions = true
            }
        };

        Assert.Equal(LibraryStatus.Empty, model.ReadinessStatus);
        Assert.Empty(model.Items);
        Assert.Equal(0, model.Summary.TotalItems);
    }

    [Fact]
    public void LibraryPageModel_Blocked_Status_Has_MissingInputs()
    {
        var model = new LibraryPageModel
        {
            Title = "Create Test Scenario",
            Description = "Test",
            ReadinessStatus = LibraryStatus.Blocked,
            RequiredInputs = ["Specification"],
            MissingInputs = ["Specification"],
            Summary = new LibrarySummary
            {
                StatusMessage = "Missing: Specification",
                HasAvailableActions = false
            }
        };

        Assert.Equal(LibraryStatus.Blocked, model.ReadinessStatus);
        Assert.NotEmpty(model.MissingInputs);
        Assert.False(model.Summary.HasAvailableActions);
    }

    [Fact]
    public void LibraryPageModel_Ready_Status_Has_AvailableActions()
    {
        var model = new LibraryPageModel
        {
            Title = "Sample Projects",
            Description = "Test",
            ReadinessStatus = LibraryStatus.Ready,
            RequiredInputs = [],
            MissingInputs = [],
            Actions = [
                new LibraryAction
                {
                    Name = "Load",
                    Status = LibraryStatus.Ready,
                    Enabled = true,
                    ExpectedEffect = "Load sample project"
                }
            ],
            Summary = new LibrarySummary
            {
                StatusMessage = "Ready to load projects",
                HasAvailableActions = true,
                AvailableActionsCount = 1
            }
        };

        Assert.Equal(LibraryStatus.Ready, model.ReadinessStatus);
        Assert.True(model.Summary.HasAvailableActions);
        Assert.NotEmpty(model.Actions);
    }

    [Fact]
    public void LibraryPageModel_Warning_Status_Indicates_Degraded()
    {
        var model = new LibraryPageModel
        {
            Title = "QA Artifact Library",
            Description = "Test",
            ReadinessStatus = LibraryStatus.Warning,
            RequiredInputs = [],
            MissingInputs = [],
            Summary = new LibrarySummary
            {
                StatusMessage = "Some artifacts have warnings",
                WarningCount = 2,
                HasAvailableActions = true
            }
        };

        Assert.Equal(LibraryStatus.Warning, model.ReadinessStatus);
        Assert.True(model.Summary.HasAvailableActions);
        Assert.True(model.Summary.WarningCount > 0);
    }

    [Fact]
    public void LibraryPageModel_Fail_Status_Only_For_Runtime_Errors()
    {
        var model = new LibraryPageModel
        {
            Title = "QA Artifact Library",
            Description = "Test",
            ReadinessStatus = LibraryStatus.Fail,
            RequiredInputs = [],
            MissingInputs = [],
            Summary = new LibrarySummary
            {
                StatusMessage = "Failed to load artifacts: database error",
                HasAvailableActions = false
            }
        };

        Assert.Equal(LibraryStatus.Fail, model.ReadinessStatus);
        Assert.False(model.Summary.HasAvailableActions);
    }

    [Fact]
    public void LibraryPageModel_Unavailable_Status_For_Missing_Environment()
    {
        var model = new LibraryPageModel
        {
            Title = "Sample Projects",
            Description = "Test",
            ReadinessStatus = LibraryStatus.Unavailable,
            RequiredInputs = ["Network Connection"],
            MissingInputs = ["Network Connection"],
            Summary = new LibrarySummary
            {
                StatusMessage = "Sample projects require internet connection",
                HasAvailableActions = false
            }
        };

        Assert.Equal(LibraryStatus.Unavailable, model.ReadinessStatus);
        Assert.False(model.Summary.HasAvailableActions);
    }

    [Fact]
    public void LibraryItem_Has_Required_Fields()
    {
        var item = new LibraryItem
        {
            Name = "specification.md",
            Type = "Artifact",
            Status = LibraryStatus.Ready,
            Source = "Workspace",
            ArtifactKind = "Specification",
            LastUpdated = DateTime.UtcNow
        };

        Assert.NotNull(item.Name);
        Assert.NotNull(item.Type);
        Assert.NotNull(item.Status);
    }

    [Fact]
    public void LibraryItem_Can_Have_Actions()
    {
        var item = new LibraryItem
        {
            Name = "specification.md",
            Type = "Artifact",
            Status = LibraryStatus.Ready,
            Actions = [
                new LibraryAction
                {
                    Name = "View",
                    Status = LibraryStatus.Ready,
                    Enabled = true,
                    ExpectedEffect = "Open artifact viewer"
                },
                new LibraryAction
                {
                    Name = "Replace",
                    Status = LibraryStatus.Ready,
                    Enabled = true,
                    ExpectedEffect = "Replace with new artifact"
                }
            ]
        };

        Assert.NotEmpty(item.Actions);
        Assert.Equal(2, item.Actions.Count);
    }

    [Fact]
    public void LibraryAction_Has_Status_And_Enabled_Flag()
    {
        var action = new LibraryAction
        {
            Name = "Load",
            Status = LibraryStatus.Ready,
            Enabled = true,
            ExpectedEffect = "Load sample project into workspace"
        };

        Assert.True(action.Enabled);
        Assert.Equal(LibraryStatus.Ready, action.Status);
    }

    [Fact]
    public void LibraryAction_Can_Be_Disabled_With_Reason()
    {
        var action = new LibraryAction
        {
            Name = "Load",
            Status = LibraryStatus.Blocked,
            Enabled = false,
            Reason = "Sample projects not available offline",
            ExpectedEffect = "Would load sample project"
        };

        Assert.False(action.Enabled);
        Assert.NotEmpty(action.Reason);
    }

    [Fact]
    public void LibrarySummary_Tracks_Item_Counts()
    {
        var summary = new LibrarySummary
        {
            TotalItems = 10,
            WarningCount = 2,
            EmptyCount = 1,
            AvailableActionsCount = 5,
            StatusMessage = "Test status",
            HasAvailableActions = true
        };

        Assert.Equal(10, summary.TotalItems);
        Assert.Equal(2, summary.WarningCount);
        Assert.Equal(1, summary.EmptyCount);
    }

    [Fact]
    public void LibraryPageModel_Distinguishes_Empty_From_Fail()
    {
        // Empty = no items yet, not a failure (user hasn't loaded/created anything)
        var emptyModel = new LibraryPageModel
        {
            Title = "Test",
            Description = "Test",
            ReadinessStatus = LibraryStatus.Empty,
            RequiredInputs = [],
            MissingInputs = [],
            Items = [],
            Summary = new LibrarySummary
            {
                StatusMessage = "No artifacts loaded",
                TotalItems = 0,
                HasAvailableActions = true
            }
        };

        // Fail = error occurred while loading/processing
        var failedModel = new LibraryPageModel
        {
            Title = "Test",
            Description = "Test",
            ReadinessStatus = LibraryStatus.Fail,
            RequiredInputs = [],
            MissingInputs = [],
            Summary = new LibrarySummary
            {
                StatusMessage = "Error loading artifacts: database connection failed",
                HasAvailableActions = false
            }
        };

        Assert.Equal(LibraryStatus.Empty, emptyModel.ReadinessStatus);
        Assert.True(emptyModel.Summary.HasAvailableActions);

        Assert.Equal(LibraryStatus.Fail, failedModel.ReadinessStatus);
        Assert.False(failedModel.Summary.HasAvailableActions);
    }

    [Fact]
    public void LibrarySummary_WarningCount_Reflects_Item_Degradation()
    {
        var summary = new LibrarySummary
        {
            TotalItems = 10,
            WarningCount = 3,
            EmptyCount = 0,
            StatusMessage = "Some items have warnings",
            HasAvailableActions = true
        };

        Assert.True(summary.WarningCount > 0);
        Assert.True(summary.WarningCount < summary.TotalItems);
    }
}
