using BirkNext.Api.Services;
using Xunit;
namespace BirkNext.Api.Tests.Services;
public class AIPageServiceTests { 
    [Fact] 
    public async Task GetSectionsAsync_ReturnsSettingsSections() { 
        var service = new AIPageService(new SystemSettingsStatusEngine(), new MockLogger<AIPageService>()); 
        var sections = await service.GetSectionsAsync(); 
        Assert.NotEmpty(sections); 
    } 
}
