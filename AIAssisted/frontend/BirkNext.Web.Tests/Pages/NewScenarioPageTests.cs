using BirkNext.Web.GraphQL;
using BirkNext.Web.Pages;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StrawberryShake;

namespace BirkNext.Web.Tests.Pages;

public class NewScenarioPageTests : BunitContext
{
    [Fact]
    public void NewScenarioPage_Renders_ScenarioForm()
    {
        Services.AddSingleton(new Mock<IBirkNextClient>().Object);

        var cut = Render<NewScenario>();

        cut.Find("input[id='title']").Should().NotBeNull();
        cut.Find("textarea[id='description']").Should().NotBeNull();
        cut.Find("select[id='kind']").Should().NotBeNull();
        cut.Find("button[type='submit']").Should().NotBeNull();
    }

    [Fact]
    public void NewScenarioPage_AfterSuccessfulCreate_NavigatesToScenarioLibrary()
    {
        var mockPayload = new Mock<ICreateScenario_CreateScenario>();
        mockPayload.Setup(p => p.Scenario).Returns(new Mock<ICreateScenario_CreateScenario_Scenario>().Object);
        mockPayload.Setup(p => p.Errors).Returns(new List<ICreateScenario_CreateScenario_Errors>());
        mockPayload.Setup(p => p.CorrelationId).Returns(string.Empty);

        var mockData = new Mock<ICreateScenarioResult>();
        mockData.Setup(d => d.CreateScenario).Returns(mockPayload.Object);

        var mockResult = new Mock<IOperationResult<ICreateScenarioResult>>();
        mockResult.Setup(r => r.Data).Returns(mockData.Object);

        var mockMutation = new Mock<ICreateScenarioMutation>();
        mockMutation
            .Setup(m => m.ExecuteAsync(It.IsAny<CreateScenarioInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResult.Object);

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.CreateScenario).Returns(mockMutation.Object);
        Services.AddSingleton(mockClient.Object);

        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = Render<NewScenario>();

        cut.Find("input[id='title']").Change("My scenario");
        cut.Find("select[id='kind']").Change("Requirement");
        cut.Find("button[type='submit']").Click();

        cut.WaitForAssertion(() =>
            navigation.Uri.Should().EndWith("/scenarios"),
            timeout: TimeSpan.FromSeconds(1));
    }
}
