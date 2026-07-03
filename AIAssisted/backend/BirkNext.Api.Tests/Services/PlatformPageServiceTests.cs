using BirkNext.Api.Services;
using Xunit;
namespace BirkNext.Api.Tests.Services;
public class PlatformPageServiceTests { 
    [Fact] 
    public async Task GetSectionsAsync_ReturnsSettingsSections() { 
        var service = new PlatformPageService(new SystemSettingsStatusEngine(), new MockLogger<PlatformPageService>()); 
        var sections = await service.GetSectionsAsync(); 
        Assert.NotEmpty(sections); 
    } 
    [Fact] 
    public async Task GetStatusSummaryAsync_ReturnsStatusSummary() { 
        var service = new PlatformPageService(new SystemSettingsStatusEngine(), new MockLogger<PlatformPageService>()); 
        var summary = await service.GetStatusSummaryAsync(); 
        Assert.NotNull(summary); 
    } 
}
