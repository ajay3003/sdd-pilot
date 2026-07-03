using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using Xunit;
namespace BirkNext.Api.Tests.Services;
public class RuntimeDiagnosticsPageServiceTests {
    [Fact]
    public async Task GetSectionsAsync_ReturnsSettingsSections() {
        var service = new RuntimeDiagnosticsPageService(new SystemSettingsStatusEngine(), new MockLogger<RuntimeDiagnosticsPageService>());
        var sections = await service.GetSectionsAsync();
        Assert.NotEmpty(sections);
    }
    [Fact]
    public async Task GetStatusSummaryAsync_ReturnsCorrectCounts() {
        var service = new RuntimeDiagnosticsPageService(new SystemSettingsStatusEngine(), new MockLogger<RuntimeDiagnosticsPageService>());
        var summary = await service.GetStatusSummaryAsync();
        Assert.NotNull(summary);
        Assert.True(summary.PassCount + summary.WarningCount + summary.FailCount + summary.UnavailableCount > 0);
    }
}
