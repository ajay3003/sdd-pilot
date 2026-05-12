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

    // ── T037 ────────────────────────────────────────────────────────────────

    [Fact]
    public void ScenarioForm_EmptyTitle_ShowsTitleRequiredValidationMessage()
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

        // Title is empty by default — submit without filling it in
        cut.Find("button[type='submit']").Click();

        cut.Find("input[id='title']").ParentElement!
            .TextContent.Should().Contain("Title is required");
    }

    [Fact]
    public void ScenarioForm_NoKindSelected_ShowsKindRequiredValidationMessage()
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

        // Provide a valid title so only the missing kind triggers the message
        cut.Find("input[id='title']").Change("My scenario");
        cut.Find("button[type='submit']").Click();

        cut.Find("select[id='kind']").ParentElement!
            .TextContent.Should().Contain("A valid type must be selected");
    }
}
