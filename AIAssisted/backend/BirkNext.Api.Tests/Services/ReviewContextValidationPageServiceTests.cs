using BirkNext.Api.Services;
using Xunit;
namespace BirkNext.Api.Tests.Services;
public class ReviewContextValidationPageServiceTests {
    [Fact] 
    public async Task GetSectionsAsync_ReturnsSettingsSections() {
        var service = new ReviewContextValidationPageService(new SystemSettingsStatusEngine(), new MockLogger<ReviewContextValidationPageService>()); 
        var sections = await service.GetSectionsAsync(); 
        Assert.NotEmpty(sections); 
    }
    [Fact] 
    public async Task GetStatusSummaryAsync_ReturnsStatusSummary() {
        var service = new ReviewContextValidationPageService(new SystemSettingsStatusEngine(), new MockLogger<ReviewContextValidationPageService>()); 
        var summary = await service.GetStatusSummaryAsync(); 
        Assert.NotNull(summary); 
    }
}
