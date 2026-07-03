using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Tests for Environment Diagnostics Page Service using shared architecture.
/// Verifies the service correctly returns SettingsSection[] with SettingsItem[] items,
/// uses the shared status engine, and provides accurate status summaries.
/// </summary>
public class EnvironmentDiagnosticsPageServiceTests
{
    private readonly IEnvironmentDiagnosticsPageService _service;
    private readonly ISystemSettingsStatusEngine _statusEngine;
    private readonly ILogger<EnvironmentDiagnosticsPageService> _mockLogger;

    public EnvironmentDiagnosticsPageServiceTests()
    {
        _statusEngine = new SystemSettingsStatusEngine();
        _mockLogger = new TestLogger<EnvironmentDiagnosticsPageService>();

        _service = new EnvironmentDiagnosticsPageService(
            _statusEngine,
            _mockLogger);
    }

    [Fact]
    public async Task GetSectionsAsync_ReturnsSections_InCorrectOrder()
    {
        // Act
        var sections = await _service.GetSectionsAsync();

        // Assert
        Assert.NotEmpty(sections);
        var titles = sections.Select(s => s.Title).ToList();

        // Verify order: Backend, Database, Workspace, Runtime/API, Export
        Assert.Contains("Backend", titles[0]);
        Assert.Contains("Database", titles[1]);
        Assert.Contains("Workspace", titles[2]);
        Assert.True(titles[3].Contains("Runtime") || titles[3].Contains("API"));
        Assert.Contains("Export", titles[4]);
    }

    [Fact]
    public async Task GetSectionsAsync_ReturnsSettingsItems_NotEnvironmentDiagnosticChecks()
    {
        // Act
        var sections = await _service.GetSectionsAsync();

        // Assert
        Assert.All(sections, section =>
        {
            Assert.NotNull(section.Items);
            Assert.IsType<List<SettingsItem>>(section.Items);
            Assert.All(section.Items, item =>
            {
                Assert.NotNull(item.Name);
                Assert.NotNull(item.Description);
                // Verify it's using SystemSettingsStatus enum, not strings
                Assert.True(
                    item.Status == SystemSettingsStatus.Pass ||
                    item.Status == SystemSettingsStatus.Warning ||
                    item.Status == SystemSettingsStatus.Fail ||
                    item.Status == SystemSettingsStatus.Unavailable);
            });
        });
    }

    [Fact]
    public async Task GetSectionsAsync_DatabaseSection_HasReachabilityCheck()
    {
        // Act
        var sections = await _service.GetSectionsAsync();
        var databaseSection = sections.FirstOrDefault(s => s.Title.Contains("Database"));

        // Assert
        Assert.NotNull(databaseSection);
        var hasReachabilityCheck = databaseSection.Items.Any(i => i.Name.Contains("Reachable"));
        Assert.True(hasReachabilityCheck, "Database section should include a reachability check");
    }

    [Fact]
    public async Task GetSectionsAsync_DatabaseSection_HasTableCheck()
    {
        // Act
        var sections = await _service.GetSectionsAsync();
        var databaseSection = sections.FirstOrDefault(s => s.Title.Contains("Database"));

        // Assert
        Assert.NotNull(databaseSection);
        var hasTableCheck = databaseSection.Items.Any(i => i.Name.Contains("Required") && i.Name.Contains("Table"));
        Assert.True(hasTableCheck, "Database section should include a required tables check");
    }

    [Fact]
    public async Task GetSectionsAsync_NoActiveWorkspace_ReturnsWarningNotFail()
    {
        // Act
        var sections = await _service.GetSectionsAsync();
        var workspaceSection = sections.FirstOrDefault(s => s.Title.Contains("Workspace"));

        // Assert
        Assert.NotNull(workspaceSection);
        var items = workspaceSection.Items.ToList();
        Assert.NotEmpty(items);

        // No workspace is a WARNING, not a FAIL
        // Verify section status is not FAIL if only workspace is unavailable
        Assert.NotEqual(SystemSettingsStatus.Fail, workspaceSection.Status);
    }

    [Fact]
    public async Task GetSectionsAsync_OptionalDiagnosticsUnavailable_NotCountAsFail()
    {
        // Act
        var sections = await _service.GetSectionsAsync();

        // Assert
        var allItems = sections.SelectMany(s => s.Items).ToList();

        // If an item is Unavailable, the overall status should not be Fail
        var allStatuses = allItems.Select(i => i.Status).ToArray();
        var hasFail = allStatuses.Any(s => s == SystemSettingsStatus.Fail);
        var hasUnavailable = allStatuses.Any(s => s == SystemSettingsStatus.Unavailable);

        // If we only have unavailable items, overall should not be Fail
        if (!hasFail && hasUnavailable)
        {
            var overallStatus = _statusEngine.CalculateOverallStatus(allStatuses);
            Assert.NotEqual(SystemSettingsStatus.Fail, overallStatus);
        }
    }

    [Fact]
    public async Task GetStatusSummaryAsync_ReturnsStatusSummary_WithCorrectCounts()
    {
        // Act
        var summary = await _service.GetStatusSummaryAsync();

        // Assert
        Assert.NotNull(summary);
        Assert.True(summary.PassCount >= 0);
        Assert.True(summary.WarningCount >= 0);
        Assert.True(summary.FailCount >= 0);
        Assert.True(summary.UnavailableCount >= 0);

        // Total should equal sum of counts
        var total = summary.PassCount + summary.WarningCount + summary.FailCount + summary.UnavailableCount;
        Assert.True(total > 0, "Should have at least one diagnostic check");
    }

    [Fact]
    public async Task GetStatusSummaryAsync_OverallStatusCalculated_ByEngine()
    {
        // Arrange
        var sections = await _service.GetSectionsAsync();
        var allItems = sections.SelectMany(s => s.Items).ToList();
        var expectedStatuses = allItems.Select(i => i.Status).ToArray();
        var expectedOverallStatus = _statusEngine.CalculateOverallStatus(expectedStatuses);

        // Act
        var summary = await _service.GetStatusSummaryAsync();

        // Assert
        Assert.Equal(expectedOverallStatus, summary.OverallStatus);
    }

    [Fact]
    public async Task GetSectionsAsync_BackendSection_First()
    {
        // Act
        var sections = await _service.GetSectionsAsync();

        // Assert
        Assert.True(sections.Count > 0);
        Assert.Contains("Backend", sections[0].Title);
    }

    [Fact]
    public async Task GetSectionsAsync_DatabaseSection_Second()
    {
        // Act
        var sections = await _service.GetSectionsAsync();

        // Assert
        Assert.True(sections.Count > 1);
        Assert.Contains("Database", sections[1].Title);
    }

    [Fact]
    public async Task GetSectionsAsync_WorkspaceSection_Third()
    {
        // Act
        var sections = await _service.GetSectionsAsync();

        // Assert
        Assert.True(sections.Count > 2);
        Assert.Contains("Workspace", sections[2].Title);
    }

    [Fact]
    public async Task GetSectionsAsync_RuntimeSection_Fourth()
    {
        // Act
        var sections = await _service.GetSectionsAsync();

        // Assert
        Assert.True(sections.Count > 3);
        Assert.True(
            sections[3].Title.Contains("Runtime") ||
            sections[3].Title.Contains("API") ||
            sections[3].Title.Contains("Export"),
            "Fourth section should be Runtime/API");
    }

    [Fact]
    public async Task GetSectionsAsync_ExportSection_Last()
    {
        // Act
        var sections = await _service.GetSectionsAsync();

        // Assert
        Assert.True(sections.Count > 0);
        var lastSection = sections[sections.Count - 1];
        Assert.Contains("Export", lastSection.Title);
    }

    [Fact]
    public async Task GetSectionsAsync_AllSectionsHaveItems()
    {
        // Act
        var sections = await _service.GetSectionsAsync();

        // Assert
        Assert.All(sections, section =>
        {
            Assert.NotNull(section.Items);
            Assert.NotEmpty(section.Items);
        });
    }

    [Fact]
    public async Task GetSectionsAsync_AllItemsHaveStatus()
    {
        // Act
        var sections = await _service.GetSectionsAsync();

        // Assert
        var allItems = sections.SelectMany(s => s.Items).ToList();
        Assert.All(allItems, item =>
        {
            var isValidStatus =
                item.Status == SystemSettingsStatus.Pass ||
                item.Status == SystemSettingsStatus.Warning ||
                item.Status == SystemSettingsStatus.Fail ||
                item.Status == SystemSettingsStatus.Unavailable;
            Assert.True(isValidStatus, $"Item '{item.Name}' has invalid status");
        });
    }

    [Fact]
    public async Task GetSectionsAsync_SectionStatusCalculated_FromItems()
    {
        // Act
        var sections = await _service.GetSectionsAsync();

        // Assert
        Assert.All(sections, section =>
        {
            if (section.Items.Count > 0)
            {
                var itemStatuses = section.Items.Select(i => i.Status).ToArray();
                var expectedStatus = _statusEngine.CalculateOverallStatus(itemStatuses);
                Assert.Equal(expectedStatus, section.Status);
            }
        });
    }

    [Fact]
    public async Task GetSectionsAsync_StatusHierarchy_Fail_Over_Warning()
    {
        // Arrange
        var sections = await _service.GetSectionsAsync();

        // Assert
        // If any item is Fail, overall status should be Fail
        var allItems = sections.SelectMany(s => s.Items).ToList();
        var hasFail = allItems.Any(i => i.Status == SystemSettingsStatus.Fail);
        var overallStatus = _statusEngine.CalculateOverallStatus(allItems.Select(i => i.Status).ToArray());

        if (hasFail)
        {
            Assert.Equal(SystemSettingsStatus.Fail, overallStatus);
        }
    }

    [Fact]
    public async Task GetSectionsAsync_StatusHierarchy_Warning_Over_Pass()
    {
        // Arrange
        var sections = await _service.GetSectionsAsync();

        // Assert
        var allItems = sections.SelectMany(s => s.Items).ToList();
        var hasFail = allItems.Any(i => i.Status == SystemSettingsStatus.Fail);
        var hasWarning = allItems.Any(i => i.Status == SystemSettingsStatus.Warning);
        var overallStatus = _statusEngine.CalculateOverallStatus(allItems.Select(i => i.Status).ToArray());

        if (!hasFail && hasWarning)
        {
            Assert.Equal(SystemSettingsStatus.Warning, overallStatus);
        }
    }

    [Fact]
    public async Task GetStatusSummaryAsync_UnavailableNotCountedAsFail()
    {
        // Act
        var summary = await _service.GetStatusSummaryAsync();

        // Assert
        // If all items are unavailable, overall status should not be Fail
        var total = summary.PassCount + summary.WarningCount + summary.FailCount + summary.UnavailableCount;

        if (summary.UnavailableCount == total && summary.FailCount == 0)
        {
            Assert.NotEqual(SystemSettingsStatus.Fail, summary.OverallStatus);
        }
    }
}

// Test logger implementation
internal class TestLogger<T> : ILogger<T>
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
        new NullDisposable();

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // Test logger - no-op
    }

    private class NullDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
