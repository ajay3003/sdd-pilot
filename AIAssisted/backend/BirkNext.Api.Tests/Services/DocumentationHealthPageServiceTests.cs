using BirkNext.Api.Services;
using Xunit;
namespace BirkNext.Api.Tests.Services;
public class DocumentationHealthPageServiceTests {
    [Fact] 
    public async Task GetSectionsAsync_ReturnsSettingsSections() {
        var service = new DocumentationHealthPageService(new SystemSettingsStatusEngine(), new MockLogger<DocumentationHealthPageService>()); 
        var sections = await service.GetSectionsAsync(); 
        Assert.NotEmpty(sections); 
    }
    [Fact] 
    public async Task GetStatusSummaryAsync_ReturnsStatusSummary() {
        var service = new DocumentationHealthPageService(new SystemSettingsStatusEngine(), new MockLogger<DocumentationHealthPageService>()); 
        var summary = await service.GetStatusSummaryAsync(); 
        Assert.NotNull(summary); 
    }
}
