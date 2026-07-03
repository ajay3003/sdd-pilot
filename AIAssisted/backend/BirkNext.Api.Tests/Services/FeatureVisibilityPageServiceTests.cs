using BirkNext.Api.Services;
using Xunit;
namespace BirkNext.Api.Tests.Services;
public class FeatureVisibilityPageServiceTests { 
    [Fact] 
    public async Task GetSectionsAsync_ReturnsSettingsSections() { 
        var service = new FeatureVisibilityPageService(new SystemSettingsStatusEngine(), new MockLogger<FeatureVisibilityPageService>()); 
        var sections = await service.GetSectionsAsync(); 
        Assert.NotEmpty(sections); 
    } 
}
