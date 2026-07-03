using BirkNext.Api.Services;
using Xunit;
namespace BirkNext.Api.Tests.Services;
public class SystemDiagnosticsPageServiceTests { 
    [Fact] 
    public async Task GetSectionsAsync_ReturnsSettingsSections() { 
        var service = new SystemDiagnosticsPageService(new SystemSettingsStatusEngine(), new MockLogger<SystemDiagnosticsPageService>()); 
        var sections = await service.GetSectionsAsync(); 
        Assert.NotEmpty(sections); 
    } 
}
