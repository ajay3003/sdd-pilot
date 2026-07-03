using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using Xunit;

namespace BirkNext.Api.Tests.Services;

public class SystemSettingsStatusEngineTests
{
    private readonly ISystemSettingsStatusEngine _engine = new SystemSettingsStatusEngine();

    [Fact]
    public void CalculateOverallStatus_AllPass_ReturnsPass()
    {
        var status = _engine.CalculateOverallStatus(
            SystemSettingsStatus.Pass,
            SystemSettingsStatus.Pass,
            SystemSettingsStatus.Pass);

        Assert.Equal(SystemSettingsStatus.Pass, status);
    }

    [Fact]
    public void CalculateOverallStatus_WithWarning_ReturnsWarning()
    {
        var status = _engine.CalculateOverallStatus(
            SystemSettingsStatus.Pass,
            SystemSettingsStatus.Warning,
            SystemSettingsStatus.Pass);

        Assert.Equal(SystemSettingsStatus.Warning, status);
    }

    [Fact]
    public void CalculateOverallStatus_WithFail_ReturnsFail()
    {
        var status = _engine.CalculateOverallStatus(
            SystemSettingsStatus.Pass,
            SystemSettingsStatus.Warning,
            SystemSettingsStatus.Fail);

        Assert.Equal(SystemSettingsStatus.Fail, status);
    }

    [Fact]
    public void CalculateOverallStatus_FailIsWorstStatus()
    {
        var status = _engine.CalculateOverallStatus(
            SystemSettingsStatus.Fail,
            SystemSettingsStatus.Warning,
            SystemSettingsStatus.Pass);

        Assert.Equal(SystemSettingsStatus.Fail, status);
    }

    [Fact]
    public void CalculateOverallStatus_UnavailableWithPass_ReturnsWarning()
    {
        var status = _engine.CalculateOverallStatus(
            SystemSettingsStatus.Unavailable,
            SystemSettingsStatus.Pass);

        Assert.Equal(SystemSettingsStatus.Warning, status);
    }

    [Fact]
    public void CalculateOverallStatus_OnlyUnavailable_ReturnsWarning()
    {
        var status = _engine.CalculateOverallStatus(
            SystemSettingsStatus.Unavailable);

        Assert.Equal(SystemSettingsStatus.Warning, status);
    }

    [Fact]
    public void CalculateOverallStatus_FromItems_RespectsHierarchy()
    {
        var items = new List<SettingsItem>
        {
            new() { Status = SystemSettingsStatus.Pass },
            new() { Status = SystemSettingsStatus.Warning },
            new() { Status = SystemSettingsStatus.Fail }
        };

        var status = _engine.CalculateOverallStatus(items);

        Assert.Equal(SystemSettingsStatus.Fail, status);
    }

    [Fact]
    public void CalculateOverallStatus_FromSections_RespectsHierarchy()
    {
        var sections = new List<SettingsSection>
        {
            new()
            {
                Items = new()
                {
                    new() { Status = SystemSettingsStatus.Pass },
                    new() { Status = SystemSettingsStatus.Pass }
                }
            },
            new()
            {
                Items = new()
                {
                    new() { Status = SystemSettingsStatus.Warning },
                    new() { Status = SystemSettingsStatus.Pass }
                }
            }
        };

        var status = _engine.CalculateOverallStatus(sections);

        Assert.Equal(SystemSettingsStatus.Warning, status);
    }

    [Fact]
    public void SummarizeStatuses_CountsCorrectly()
    {
        var statuses = new[]
        {
            SystemSettingsStatus.Pass,
            SystemSettingsStatus.Pass,
            SystemSettingsStatus.Warning,
            SystemSettingsStatus.Fail,
            SystemSettingsStatus.Unavailable
        };

        var summary = _engine.SummarizeStatuses(statuses);

        Assert.Equal(2, summary.PassCount);
        Assert.Equal(1, summary.WarningCount);
        Assert.Equal(1, summary.FailCount);
        Assert.Equal(1, summary.UnavailableCount);
    }

    [Fact]
    public void SummarizeStatuses_OverallStatusCalculatesCorrectly()
    {
        var statuses = new[]
        {
            SystemSettingsStatus.Pass,
            SystemSettingsStatus.Pass,
            SystemSettingsStatus.Warning
        };

        var summary = _engine.SummarizeStatuses(statuses);

        Assert.Equal(SystemSettingsStatus.Warning, summary.OverallStatus);
    }

    [Fact]
    public void CreatePassItem_HasCorrectDefaults()
    {
        var item = _engine.CreatePassItem("Test", "value", "description");

        Assert.Equal("Test", item.Name);
        Assert.Equal("value", item.Value);
        Assert.Equal("description", item.Description);
        Assert.Equal(SystemSettingsStatus.Pass, item.Status);
        Assert.True(item.IsRequired);
        Assert.Null(item.Recommendation);
    }

    [Fact]
    public void CreateWarningItem_IsOptionalByDefault()
    {
        var item = _engine.CreateWarningItem("Test", "value", "description");

        Assert.Equal(SystemSettingsStatus.Warning, item.Status);
        Assert.False(item.IsRequired);
    }

    [Fact]
    public void CreateFailItem_IsRequiredByDefault()
    {
        var item = _engine.CreateFailItem("Test", "value", "description");

        Assert.Equal(SystemSettingsStatus.Fail, item.Status);
        Assert.True(item.IsRequired);
    }

    [Fact]
    public void CreateUnavailableItem_HasNotAvailableValue()
    {
        var item = _engine.CreateUnavailableItem("Test", "description");

        Assert.Equal("Not Available", item.Value);
        Assert.Equal(SystemSettingsStatus.Unavailable, item.Status);
        Assert.False(item.IsRequired);
    }

    [Fact]
    public void CreatePassItem_WithRecommendation_IncludesIt()
    {
        var item = _engine.CreatePassItem("Test", "value", "description", "Recommendation text");

        Assert.Equal("Recommendation text", item.Recommendation);
    }

    [Fact]
    public void StatusSummary_Empty_AllCountsZero()
    {
        var summary = _engine.SummarizeStatuses(Array.Empty<SystemSettingsStatus>());

        Assert.Equal(0, summary.PassCount);
        Assert.Equal(0, summary.WarningCount);
        Assert.Equal(0, summary.FailCount);
        Assert.Equal(0, summary.UnavailableCount);
    }

    [Fact]
    public void StatusSummary_Empty_OverallStatusIsUnavailable()
    {
        var summary = _engine.SummarizeStatuses(Array.Empty<SystemSettingsStatus>());

        Assert.Equal(SystemSettingsStatus.Unavailable, summary.OverallStatus);
    }

    [Fact]
    public void StatusSummary_AddStatus_UpdatesCounts()
    {
        var summary = new StatusSummary();

        summary.AddStatus(SystemSettingsStatus.Pass);
        summary.AddStatus(SystemSettingsStatus.Warning);
        summary.AddStatus(SystemSettingsStatus.Fail);

        Assert.Equal(1, summary.PassCount);
        Assert.Equal(1, summary.WarningCount);
        Assert.Equal(1, summary.FailCount);
    }

    [Fact]
    public void CalculateOverallStatus_EmptyArray_ReturnsUnavailable()
    {
        var status = _engine.CalculateOverallStatus();

        Assert.Equal(SystemSettingsStatus.Unavailable, status);
    }

    [Fact]
    public void SettingsItem_DefaultStatus_IsPass()
    {
        var item = new SettingsItem();

        Assert.Equal(SystemSettingsStatus.Pass, item.Status);
    }

    [Fact]
    public void SettingsSection_DefaultStatus_IsPass()
    {
        var section = new SettingsSection();

        Assert.Equal(SystemSettingsStatus.Pass, section.Status);
    }

    [Fact]
    public void SettingsSection_WithFailingItem_StatusIsNotAutomatic()
    {
        var section = new SettingsSection
        {
            Items = new()
            {
                new() { Status = SystemSettingsStatus.Fail }
            }
        };

        // Note: Status is manually set by the service, not automatically calculated
        Assert.Equal(SystemSettingsStatus.Pass, section.Status);
    }
}
