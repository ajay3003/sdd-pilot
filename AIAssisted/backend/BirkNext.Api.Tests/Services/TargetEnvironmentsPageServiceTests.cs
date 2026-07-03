using BirkNext.Api.Services;
using Xunit;
namespace BirkNext.Api.Tests.Services;
public class TargetEnvironmentsPageServiceTests { 
    [Fact] 
    public async Task GetSectionsAsync_ReturnsSettingsSections() { 
        var service = new TargetEnvironmentsPageService(new SystemSettingsStatusEngine(), new MockLogger<TargetEnvironmentsPageService>()); 
        var sections = await service.GetSectionsAsync(); 
        Assert.NotEmpty(sections); 
    } 
}
