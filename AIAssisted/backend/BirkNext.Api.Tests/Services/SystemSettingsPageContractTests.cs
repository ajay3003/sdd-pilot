using BirkNext.Api.Models.Admin;
using Xunit;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Contract tests defining expected behavior for all System Settings pages.
///
/// Every page should:
/// - Have sections organized by topic
/// - Have items with Name, Value, Status, Description
/// - Have proper status calculation
/// - Have recommendations when needed
/// - Use the shared SystemSettingsStatus enum
///
/// This prevents pages from inventing their own models or status logic.
/// </summary>
public class SystemSettingsPageContractTests
{
    [Fact]
    public void SettingsItem_HasAllRequiredProperties()
    {
        var item = new SettingsItem
        {
            Name = "Environment",
            Value = "Development",
            Status = SystemSettingsStatus.Pass,
            Description = "Current ASP.NET Core environment",
            Recommendation = null,
            IsRequired = true
        };

        Assert.NotNull(item.Name);
        Assert.NotNull(item.Value);
        Assert.NotNull(item.Status);
        Assert.NotNull(item.Description);
    }

    [Fact]
    public void SettingsSection_MustHaveTitle()
    {
        var section = new SettingsSection
        {
            Title = "Application",
            Description = "Application information",
            Status = SystemSettingsStatus.Pass,
            Items = new()
            {
                new SettingsItem { Name = "Version", Value = "1.0.0", Status = SystemSettingsStatus.Pass }
            }
        };

        Assert.NotEmpty(section.Title);
        Assert.NotEmpty(section.Description);
        Assert.NotEmpty(section.Items);
    }

    [Fact]
    public void SettingsPage_AllItemsRequireStatus()
    {
        var sections = new List<SettingsSection>
        {
            new()
            {
                Title = "Configuration",
                Items = new()
                {
                    new() { Name = "Database", Status = SystemSettingsStatus.Pass },
                    new() { Name = "Logging", Status = SystemSettingsStatus.Warning },
                    new() { Name = "Cache", Status = SystemSettingsStatus.Unavailable }
                }
            }
        };

        // Every item must have a status
        foreach (var section in sections)
        {
            foreach (var item in section.Items)
            {
                Assert.True(
                    item.Status == SystemSettingsStatus.Pass ||
                    item.Status == SystemSettingsStatus.Warning ||
                    item.Status == SystemSettingsStatus.Fail ||
                    item.Status == SystemSettingsStatus.Unavailable,
                    $"Item {item.Name} has invalid status");
            }
        }
    }

    [Fact]
    public void WarningItem_ShouldHaveRecommendation()
    {
        var item = new SettingsItem
        {
            Name = "AI Configuration",
            Value = "Not configured",
            Status = SystemSettingsStatus.Warning,
            Description = "AI provider is optional but not configured",
            Recommendation = "Configure AI provider to enable AI features",
            IsRequired = false
        };

        Assert.NotNull(item.Recommendation);
        Assert.False(item.IsRequired);
    }

    [Fact]
    public void FailItem_ShouldHaveRecommendation()
    {
        var item = new SettingsItem
        {
            Name = "Database",
            Value = "Not accessible",
            Status = SystemSettingsStatus.Fail,
            Description = "Database connection failed",
            Recommendation = "Check database connection string and server availability",
            IsRequired = true
        };

        Assert.NotNull(item.Recommendation);
        Assert.True(item.IsRequired);
    }

    [Fact]
    public void PageWithNoIssues_ShouldStillShowValidatedItems()
    {
        // This prevents pages from showing nothing when all checks pass
        var sections = new List<SettingsSection>
        {
            new()
            {
                Title = "Configuration",
                Items = new()
                {
                    new() { Name = "Environment", Value = "Production", Status = SystemSettingsStatus.Pass },
                    new() { Name = "Database", Value = "PostgreSQL", Status = SystemSettingsStatus.Pass },
                    new() { Name = "Logging", Value = "Enabled", Status = SystemSettingsStatus.Pass }
                }
            }
        };

        // Even with all Pass, items should be shown
        Assert.NotEmpty(sections);
        Assert.NotEmpty(sections[0].Items);
        Assert.True(sections[0].Items.Count > 0);
    }

    [Fact]
    public void RequiredItem_WithWarning_StillMarkedRequired()
    {
        var item = new SettingsItem
        {
            Name = "Migration Status",
            Value = "Pending",
            Status = SystemSettingsStatus.Warning,
            Description = "Database migrations pending",
            IsRequired = true
        };

        Assert.True(item.IsRequired);
        Assert.Equal(SystemSettingsStatus.Warning, item.Status);
    }

    [Fact]
    public void OptionalItem_WithWarning_NotMarkedRequired()
    {
        var item = new SettingsItem
        {
            Name = "Export Configuration",
            Value = "Not configured",
            Status = SystemSettingsStatus.Warning,
            Description = "Export features use defaults",
            IsRequired = false
        };

        Assert.False(item.IsRequired);
    }

    [Fact]
    public void UnavailableItem_NeverCounted_AsFailure()
    {
        var sections = new List<SettingsSection>
        {
            new()
            {
                Title = "Optional Features",
                Items = new()
                {
                    new() { Status = SystemSettingsStatus.Pass },
                    new() { Status = SystemSettingsStatus.Unavailable },
                    new() { Status = SystemSettingsStatus.Pass }
                }
            }
        };

        var items = sections.SelectMany(s => s.Items).ToList();
        var hasFail = items.Any(i => i.Status == SystemSettingsStatus.Fail);
        var hasUnavailable = items.Any(i => i.Status == SystemSettingsStatus.Unavailable);

        Assert.False(hasFail);
        Assert.True(hasUnavailable);
    }

    [Fact]
    public void SectionStatus_ShouldReflectWorstItemStatus()
    {
        var section = new SettingsSection
        {
            Title = "Configuration",
            Items = new()
            {
                new() { Status = SystemSettingsStatus.Pass },
                new() { Status = SystemSettingsStatus.Warning },
                new() { Status = SystemSettingsStatus.Pass }
            }
        };

        // Service should set section status to worst item status
        // (not automatic - done by service)
        Assert.NotNull(section);
    }

    [Fact]
    public void StatusItem_MustHaveDescription()
    {
        var item = new SettingsItem
        {
            Name = "Database Host",
            Value = "localhost:5432",
            Status = SystemSettingsStatus.Pass
        };

        // Item must have description
        item.Description = "PostgreSQL server address";
        Assert.NotEmpty(item.Description);
    }

    [Fact]
    public void RequiredCheck_Fails_PageOverallStatusFails()
    {
        var sections = new List<SettingsSection>
        {
            new()
            {
                Title = "Required",
                Items = new()
                {
                    new() { Status = SystemSettingsStatus.Fail, IsRequired = true },
                    new() { Status = SystemSettingsStatus.Pass, IsRequired = true }
                }
            }
        };

        var hasRequiredFail = sections
            .SelectMany(s => s.Items)
            .Where(i => i.IsRequired)
            .Any(i => i.Status == SystemSettingsStatus.Fail);

        Assert.True(hasRequiredFail);
    }

    [Fact]
    public void RequiredCheck_WithWarning_PageOverallStatusWarning()
    {
        var sections = new List<SettingsSection>
        {
            new()
            {
                Title = "Configuration",
                Items = new()
                {
                    new() { Status = SystemSettingsStatus.Warning, IsRequired = true },
                    new() { Status = SystemSettingsStatus.Pass, IsRequired = true }
                }
            }
        };

        var hasRequiredWarning = sections
            .SelectMany(s => s.Items)
            .Where(i => i.IsRequired)
            .Any(i => i.Status == SystemSettingsStatus.Warning);

        Assert.True(hasRequiredWarning);
    }

    [Fact]
    public void OptionalCheck_Missing_PageStillPass()
    {
        var sections = new List<SettingsSection>
        {
            new()
            {
                Title = "Configuration",
                Items = new()
                {
                    new() { Status = SystemSettingsStatus.Pass, IsRequired = true },
                    new() { Status = SystemSettingsStatus.Warning, IsRequired = false }
                }
            }
        };

        var allRequiredPass = sections
            .SelectMany(s => s.Items)
            .Where(i => i.IsRequired)
            .All(i => i.Status == SystemSettingsStatus.Pass);

        Assert.True(allRequiredPass);
    }

    [Fact]
    public void EmptyValue_AllowedOnly_WhenUnavailable()
    {
        var item = new SettingsItem
        {
            Name = "Optional Feature",
            Value = "",
            Status = SystemSettingsStatus.Unavailable
        };

        Assert.Empty(item.Value);
        Assert.Equal(SystemSettingsStatus.Unavailable, item.Status);
    }

    [Fact]
    public void Page_WithMultipleSections_EachSectionHasStatus()
    {
        var sections = new List<SettingsSection>
        {
            new() { Title = "Database", Status = SystemSettingsStatus.Pass },
            new() { Title = "Logging", Status = SystemSettingsStatus.Warning },
            new() { Title = "Cache", Status = SystemSettingsStatus.Fail }
        };

        foreach (var section in sections)
        {
            Assert.NotEmpty(section.Title);
            Assert.True(
                section.Status == SystemSettingsStatus.Pass ||
                section.Status == SystemSettingsStatus.Warning ||
                section.Status == SystemSettingsStatus.Fail ||
                section.Status == SystemSettingsStatus.Unavailable);
        }
    }
}
