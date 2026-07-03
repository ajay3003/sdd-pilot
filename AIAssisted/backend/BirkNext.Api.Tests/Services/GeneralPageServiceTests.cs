using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace BirkNext.Api.Tests.Services;

public class GeneralPageServiceTests
{
    private readonly ISystemSettingsStatusEngine _statusEngine = new SystemSettingsStatusEngine();
    private readonly Mock<IConfiguration> _configMock;
    private readonly Mock<IWebHostEnvironment> _envMock;
    private readonly GeneralPageService _service;

    public GeneralPageServiceTests()
    {
        _configMock = new Mock<IConfiguration>();
        _envMock = new Mock<IWebHostEnvironment>();

        // Default mock setup
        _configMock.Setup(c => c["ApplicationName"]).Returns("QA Review Studio");
        _configMock.Setup(c => c["ApplicationVersion"]).Returns("1.0.0");
        _configMock.Setup(c => c["PackageMode"]).Returns("Release");
        _configMock.Setup(c => c["DatabaseSettings:Provider"]).Returns("PostgreSQL");
        _configMock.Setup(c => c["DatabaseSettings:Mode"]).Returns("Shared");
        _configMock.Setup(c => c["Logging:LogLevel:Default"]).Returns("Information");
        _configMock.Setup(c => c["DatabaseSettings:MigrationStatus"]).Returns("Up to date");
        _configMock.Setup(c => c["BACKEND_URL"]).Returns("http://localhost:5000");
        _configMock.Setup(c => c["Frontend:FrontendBaseUrl"]).Returns("http://localhost:5173");
        _configMock.Setup(c => c["Frontend:GraphQlEndpoint"]).Returns("http://localhost:5000/graphql");
        _envMock.Setup(e => e.EnvironmentName).Returns("Development");

        _service = new GeneralPageService(_configMock.Object, _envMock.Object, _statusEngine);
    }

    [Fact]
    public async Task GetGeneralPageSectionsAsync_ReturnsFourSections()
    {
        var sections = await _service.GetGeneralPageSectionsAsync();

        Assert.Equal(4, sections.Count);
    }

    [Fact]
    public async Task GetGeneralPageSectionsAsync_HasApplicationSection()
    {
        var sections = await _service.GetGeneralPageSectionsAsync();

        Assert.Contains(sections, s => s.Title == "Application");
    }

    [Fact]
    public async Task GetGeneralPageSectionsAsync_HasRuntimeSection()
    {
        var sections = await _service.GetGeneralPageSectionsAsync();

        Assert.Contains(sections, s => s.Title == "Runtime");
    }

    [Fact]
    public async Task GetGeneralPageSectionsAsync_HasConfigurationSection()
    {
        var sections = await _service.GetGeneralPageSectionsAsync();

        Assert.Contains(sections, s => s.Title == "Configuration");
    }

    [Fact]
    public async Task GetGeneralPageSectionsAsync_HasEndpointsSection()
    {
        var sections = await _service.GetGeneralPageSectionsAsync();

        Assert.Contains(sections, s => s.Title == "Endpoints");
    }

    [Fact]
    public async Task ApplicationSection_HasVersionItem()
    {
        var sections = await _service.GetGeneralPageSectionsAsync();
        var appSection = sections.First(s => s.Title == "Application");

        var versionItem = appSection.Items.FirstOrDefault(i => i.Name == "Version");
        Assert.NotNull(versionItem);
        Assert.Equal("1.0.0", versionItem.Value);
    }

    [Fact]
    public async Task ApplicationSection_HasEnvironmentItem()
    {
        var sections = await _service.GetGeneralPageSectionsAsync();
        var appSection = sections.First(s => s.Title == "Application");

        var envItem = appSection.Items.FirstOrDefault(i => i.Name == "Environment");
        Assert.NotNull(envItem);
        Assert.Equal("Development", envItem.Value);
    }

    [Fact]
    public async Task RuntimeSection_HasDotNetRuntimeItem()
    {
        var sections = await _service.GetGeneralPageSectionsAsync();
        var runtimeSection = sections.First(s => s.Title == "Runtime");

        var runtimeItem = runtimeSection.Items.FirstOrDefault(i => i.Name == ".NET Runtime");
        Assert.NotNull(runtimeItem);
        Assert.NotEmpty(runtimeItem.Value);
    }

    [Fact]
    public async Task RuntimeSection_HasOperatingSystemItem()
    {
        var sections = await _service.GetGeneralPageSectionsAsync();
        var runtimeSection = sections.First(s => s.Title == "Runtime");

        var osItem = runtimeSection.Items.FirstOrDefault(i => i.Name == "Operating System");
        Assert.NotNull(osItem);
        Assert.NotEmpty(osItem.Value);
    }

    [Fact]
    public async Task ConfigurationSection_WhenMigrationsCurrent_StatusIsPass()
    {
        var sections = await _service.GetGeneralPageSectionsAsync();
        var configSection = sections.First(s => s.Title == "Configuration");

        var migrationItem = configSection.Items.FirstOrDefault(i => i.Name == "Database Migrations");
        Assert.NotNull(migrationItem);
        Assert.Equal(SystemSettingsStatus.Pass, migrationItem.Status);
    }

    [Fact]
    public async Task ConfigurationSection_WhenMigrationsPending_StatusIsWarning()
    {
        _configMock.Setup(c => c["DatabaseSettings:MigrationStatus"]).Returns("Pending");

        var sections = await _service.GetGeneralPageSectionsAsync();
        var configSection = sections.First(s => s.Title == "Configuration");

        var migrationItem = configSection.Items.FirstOrDefault(i => i.Name == "Database Migrations");
        Assert.NotNull(migrationItem);
        Assert.Equal(SystemSettingsStatus.Warning, migrationItem.Status);
    }

    [Fact]
    public async Task EndpointsSection_HasBackendUrlItem()
    {
        var sections = await _service.GetGeneralPageSectionsAsync();
        var endpointSection = sections.First(s => s.Title == "Endpoints");

        var backendItem = endpointSection.Items.FirstOrDefault(i => i.Name == "Backend URL");
        Assert.NotNull(backendItem);
        Assert.Equal("http://localhost:5000", backendItem.Value);
    }

    [Fact]
    public async Task AllSections_AreMarkedRequired()
    {
        var sections = await _service.GetGeneralPageSectionsAsync();

        Assert.All(sections, s => Assert.True(s.IsRequired));
    }

    [Fact]
    public async Task AllItems_HaveDescriptions()
    {
        var sections = await _service.GetGeneralPageSectionsAsync();
        var allItems = sections.SelectMany(s => s.Items);

        Assert.All(allItems, item => Assert.NotEmpty(item.Description));
    }

    [Fact]
    public async Task AllItems_HavePassStatus_WhenConfigurationHealthy()
    {
        var sections = await _service.GetGeneralPageSectionsAsync();
        var allItems = sections.SelectMany(s => s.Items);

        // All should pass when migrations are current
        Assert.All(allItems, item => Assert.Equal(SystemSettingsStatus.Pass, item.Status));
    }

    [Fact]
    public async Task GetStatusSummaryAsync_CountsItemsCorrectly()
    {
        var summary = await _service.GetStatusSummaryAsync();

        // Should have multiple items
        Assert.True(summary.PassCount > 0);
    }

    [Fact]
    public async Task GetStatusSummaryAsync_CalculatesOverallStatus()
    {
        var summary = await _service.GetStatusSummaryAsync();

        Assert.Equal(SystemSettingsStatus.Pass, summary.OverallStatus);
    }

    [Fact]
    public async Task GetOverallStatusAsync_ReturnsPass_WhenHealthy()
    {
        var status = await _service.GetOverallStatusAsync();

        Assert.Equal(SystemSettingsStatus.Pass, status);
    }

    [Fact]
    public async Task GetOverallStatusAsync_ReturnsWarning_WhenMigrationsPending()
    {
        _configMock.Setup(c => c["DatabaseSettings:MigrationStatus"]).Returns("Pending");

        var status = await _service.GetOverallStatusAsync();

        Assert.Equal(SystemSettingsStatus.Warning, status);
    }

    [Fact]
    public async Task SectionStatus_MatchesWorstItemStatus()
    {
        _configMock.Setup(c => c["DatabaseSettings:MigrationStatus"]).Returns("Pending");

        var sections = await _service.GetGeneralPageSectionsAsync();
        var configSection = sections.First(s => s.Title == "Configuration");

        // When migrations pending, section status should be warning
        var hasWarningItem = configSection.Items.Any(i => i.Status == SystemSettingsStatus.Warning);
        Assert.True(hasWarningItem);
        Assert.Equal(SystemSettingsStatus.Warning, configSection.Status);
    }

    [Fact]
    public async Task VersionItem_StripsMetadata()
    {
        _configMock.Setup(c => c["ApplicationVersion"]).Returns("1.0.0+commit.abc123");

        var sections = await _service.GetGeneralPageSectionsAsync();
        var appSection = sections.First(s => s.Title == "Application");
        var versionItem = appSection.Items.First(i => i.Name == "Version");

        // Should show only semantic version, not commit metadata
        Assert.Equal("1.0.0", versionItem.Value);
    }
}
