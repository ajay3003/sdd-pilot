using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StrawberryShake;

namespace BirkNext.Web.Tests.Components;

public class ScenarioFormTests : BunitContext
{
    [Fact]
    public void ScenarioForm_Renders_TitleInput()
    {
        Services.AddSingleton(new Mock<IBirkNextClient>().Object);

        var cut = Render<ScenarioForm>();

        cut.Find("input[id='title']").Should().NotBeNull();
    }

    [Fact]
    public void ScenarioForm_Renders_DescriptionInput()
    {
        Services.AddSingleton(new Mock<IBirkNextClient>().Object);

        var cut = Render<ScenarioForm>();

        cut.Find("textarea[id='description']").Should().NotBeNull();
    }

    [Fact]
    public void ScenarioForm_Renders_KindDropdown()
    {
        Services.AddSingleton(new Mock<IBirkNextClient>().Object);

        var cut = Render<ScenarioForm>();

        cut.Find("select[id='kind']").Should().NotBeNull();
    }

    [Fact]
    public void ScenarioForm_Renders_SubmitButton()
    {
        Services.AddSingleton(new Mock<IBirkNextClient>().Object);

        var cut = Render<ScenarioForm>();

        cut.Find("button[type='submit']").Should().NotBeNull();
    }

    [Fact]
    public async Task ScenarioForm_SubmitButton_DisabledWhileMutationInFlight()
    {
        var tcs = new TaskCompletionSource<IOperationResult<ICreateScenarioResult>>();

        var mockMutation = new Mock<ICreateScenarioMutation>();
        mockMutation
            .Setup(m => m.ExecuteAsync(It.IsAny<CreateScenarioInput>(), It.IsAny<CancellationToken>()))
            .Returns(tcs.Task);

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.CreateScenario).Returns(mockMutation.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<ScenarioForm>();

        cut.Find("input[id='title']").Change("Test scenario");
        cut.Find("select[id='kind']").Change("Requirement");
        cut.Find("button[type='submit']").Click();

        await cut.WaitForStateAsync(
            () => cut.Find("button[type='submit']").HasAttribute("disabled"),
            timeout: TimeSpan.FromSeconds(1));

        cut.Find("button[type='submit']").HasAttribute("disabled").Should().BeTrue();
    }
}
