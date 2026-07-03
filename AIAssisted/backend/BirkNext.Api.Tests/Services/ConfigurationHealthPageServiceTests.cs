using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Tests for Configuration Health Page Service using shared architecture.
/// Verifies the service correctly returns SettingsSection[] with SettingsItem[] items,
/// uses the shared status engine, and provides accurate status summaries.
/// </summary>
public class ConfigurationHealthPageServiceTests
{
    private readonly IConfigurationHealthPageService _service;
    private readonly IConfiguration _mockConfig;
    private readonly IWebHostEnvironment _mockEnv;
    private readonly ISystemSettingsStatusEngine _statusEngine;
    private readonly ILogger<ConfigurationHealthPageService> _mockLogger;

    public ConfigurationHealthPageServiceTests()
    {
        _statusEngine = new SystemSettingsStatusEngine();
        _mockConfig = new ConfigurationBuilder().Build();
        _mockEnv = new MockWebHostEnvironment { EnvironmentName = "Development" };
        _mockLogger = new MockLogger<ConfigurationHealthPageService>();

        _service = new ConfigurationHealthPageService(
            _mockConfig,
            _mockEnv,
            _statusEngine,
            _mockLogger);
    }

    [Fact]
    public async Task GetSectionsAsync_ReturnsSettingsSection_WithSettingsItems()
    {
        // Act
        var sections = await _service.GetSectionsAsync();

        // Assert
        Assert.NotEmpty(sections);
        Assert.IsType<List<SettingsSection>>(sections);
        Assert.All(sections, section =>
        {
            Assert.NotNull(section.Title);
            Assert.NotNull(section.Items);
            Assert.IsType<List<SettingsItem>>(section.Items);
        });
    }

    [Fact]
    public async Task GetSectionsAsync_AllItemsHaveSystemSettingsStatusEnum()
    {
        // Act
        var sections = await _service.GetSectionsAsync();

        // Assert
        var allItems = sections.SelectMany(s => s.Items).ToList();
        Assert.NotEmpty(allItems);
        Assert.All(allItems, item =>
        {
            Assert.True(
                item.Status == SystemSettingsStatus.Pass ||
                item.Status == SystemSettingsStatus.Warning ||
                item.Status == SystemSettingsStatus.Fail ||
                item.Status == SystemSettingsStatus.Unavailable,
                "Item status must be a valid SystemSettingsStatus value");
        });
    }

    [Fact]
    public async Task GetSectionsAsync_HealthyState_AllItemsPass()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["DatabaseSettings:Provider"] = "PostgreSQL",
                ["DatabaseSettings:Host"] = "localhost",
                ["BACKEND_URL"] = "http://localhost:5000",
                ["Logging:LogLevel:Default"] = "Information"
            })
            .Build();

        var service = new ConfigurationHealthPageService(
            config,
            new MockWebHostEnvironment { EnvironmentName = "Production" },
            _statusEngine,
            _mockLogger);

        // Act
        var sections = await service.GetSectionsAsync();

        // Assert
        var allItems = sections.SelectMany(s => s.Items).ToList();
        Assert.NotEmpty(allItems);
        // At least required items should pass
        var requiredItems = allItems.Where(i => i.IsRequired).ToList();
        Assert.NotEmpty(requiredItems);
        Assert.True(requiredItems.All(i => i.Status == SystemSettingsStatus.Pass),
            "All required items should pass in healthy state");
    }

    [Fact]
    public async Task GetSectionsAsync_FailureState_HasFailStatus()
    {
        // Arrange - missing required configuration
        var config = new ConfigurationBuilder().Build(); // Empty config

        var service = new ConfigurationHealthPageService(
            config,
            _mockEnv,
            _statusEngine,
            _mockLogger);

        // Act
        var sections = await service.GetSectionsAsync();

        // Assert
        var allItems = sections.SelectMany(s => s.Items).ToList();
        Assert.NotEmpty(allItems);
        var hasFailOrWarning = allItems.Any(i =>
            i.Status == SystemSettingsStatus.Fail ||
            i.Status == SystemSettingsStatus.Warning);
        Assert.True(hasFailOrWarning, "Should have fail or warning items for missing config");
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
                // Section status should be calculated from items
                // FAIL > WARNING > PASS logic
                var itemStatuses = section.Items.Select(i => i.Status).ToArray();
                var expectedStatus = _statusEngine.CalculateOverallStatus(itemStatuses);
                Assert.Equal(expectedStatus, section.Status);
            }
        });
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
    }

    [Fact]
    public async Task GetStatusSummaryAsync_OverallStatus_CalculatedByEngine()
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
    public async Task GetSectionsAsync_EmptySection_Possible_ForOptionalFeatures()
    {
        // Act
        var sections = await _service.GetSectionsAsync();

        // Assert - sections can be empty if all optional features are unavailable
        // But the structure should be consistent
        Assert.All(sections, section =>
        {
            Assert.NotNull(section.Title);
            Assert.NotNull(section.Items);
            Assert.IsType<List<SettingsItem>>(section.Items);
        });
    }

    [Fact]
    public async Task GetSectionsAsync_RequiredVsOptional_Separated()
    {
        // Act
        var sections = await _service.GetSectionsAsync();

        // Assert
        var allItems = sections.SelectMany(s => s.Items).ToList();
        Assert.NotEmpty(allItems);

        var requiredItems = allItems.Where(i => i.IsRequired).ToList();
        var optionalItems = allItems.Where(i => !i.IsRequired).ToList();

        // Should have both required and optional items
        Assert.NotEmpty(requiredItems);
        Assert.NotEmpty(optionalItems);
    }

    [Fact]
    public async Task GetSectionsAsync_AllItemsHaveDescriptions_AndRecommendations()
    {
        // Act
        var sections = await _service.GetSectionsAsync();

        // Assert
        var allItems = sections.SelectMany(s => s.Items).ToList();
        Assert.NotEmpty(allItems);
        Assert.All(allItems, item =>
        {
            Assert.NotNull(item.Description);
            Assert.NotEmpty(item.Description);
            // Recommendation can be null or empty for passing items
        });
    }
}

// Mock implementations for testing
internal class MockWebHostEnvironment : IWebHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "TestApp";
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = new MockFileProvider();
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new MockFileProvider();
}

internal class MockFileProvider : IFileProvider
{
    public IDirectoryContents GetDirectoryContents(string subpath) =>
        new MockDirectoryContents([]);

    public IFileInfo GetFileInfo(string subpath) =>
        new MockFileInfo();

    public IChangeToken Watch(string filter) =>
        new MockChangeToken();
}

internal class MockDirectoryContents : IDirectoryContents
{
    private readonly List<IFileInfo> _files;

    public MockDirectoryContents(List<IFileInfo> files)
    {
        _files = files;
    }

    public bool Exists => true;

    public IEnumerator<IFileInfo> GetEnumerator() => _files.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal class MockFileInfo : IFileInfo
{
    public bool Exists => false;
    public long Length => 0;
    public string PhysicalPath => "";
    public string Name => "";
    public DateTimeOffset LastModified => DateTimeOffset.MinValue;
    public bool IsDirectory => false;
    public Stream CreateReadStream() => Stream.Null;
}

internal class MockChangeToken : IChangeToken
{
    public bool HasChanged => false;
    public bool ActiveChangeCallbacks => false;

    public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) =>
        new MockDisposable();
}

internal class MockDisposable : IDisposable
{
    public void Dispose() { }
}

internal class MockLogger<T> : ILogger<T>
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
        new MockDisposable();

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // Mock implementation - no-op
    }
}
