using BirkNext.Api.Services;
using Xunit;
namespace BirkNext.Api.Tests.Services;
public class MaintenancePageServiceTests { 
    [Fact] 
    public async Task GetSectionsAsync_ReturnsSettingsSections() { 
        var service = new MaintenancePageService(new SystemSettingsStatusEngine(), new MockLogger<MaintenancePageService>()); 
        var sections = await service.GetSectionsAsync(); 
        Assert.NotEmpty(sections); 
    } 
}
