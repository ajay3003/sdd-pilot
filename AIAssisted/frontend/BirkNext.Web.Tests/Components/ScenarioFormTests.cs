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
    public void ScenarioForm_DoesNotRenderTypeSelector()
    {
        Services.AddSingleton(new Mock<IBirkNextClient>().Object);

        var cut = Render<ScenarioForm>();

        cut.FindAll("select[id='kind']").Should().BeEmpty(
            "type selector must be removed — manual scenarios are always TEST type");
    }

    [Fact]
    public void ScenarioForm_Renders_SubmitButton()
    {
        Services.AddSingleton(new Mock<IBirkNextClient>().Object);

        var cut = Render<ScenarioForm>();

        cut.Find("button[type='submit']").Should().NotBeNull();
    }

    [Fact]
    public void ScenarioForm_Renders_ClassificationPriorityAndExpectedResultDefaults()
    {
        Services.AddSingleton(new Mock<IBirkNextClient>().Object);

        var cut = Render<ScenarioForm>();

        cut.Find("select[id='scenario-type']").GetAttribute("value").Should().Be("Functional");
        cut.Find("select[id='priority']").GetAttribute("value").Should().Be("Medium");
        cut.Find("textarea[id='expected-result']").Should().NotBeNull();
        cut.Markup.Should().Contain("Traceability Impact");
        cut.Markup.Should().Contain("QA Scenario Quality Tips");
        cut.Find("button[type='submit']").TextContent.Should().Contain("Save Scenario");
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

    // ── T038 ────────────────────────────────────────────────────────────────

    [Fact]
    public void ScenarioForm_AfterCorrectingErrors_CallsMutationOnceAndResetsForm()
    {
        var mockPayload = new Mock<ICreateScenario_CreateScenario>();
        mockPayload.Setup(p => p.Scenario).Returns(new Mock<ICreateScenario_CreateScenario_Scenario>().Object);
        mockPayload.Setup(p => p.Errors).Returns(new List<ICreateScenario_CreateScenario_Errors>());
        mockPayload.Setup(p => p.CorrelationId).Returns("corr-001");

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

        var cut = Render<ScenarioForm>();

        // Step 1: submit with empty title to trigger validation errors
        cut.Find("button[type='submit']").Click();

        // Step 2: correct the validation error
        cut.Find("input[id='title']").Change("My scenario");

        // Step 3: resubmit with valid data
        cut.Find("button[type='submit']").Click();

        // Mutation called exactly once (first submit was blocked by validation),
        // and the form resets after success (title cleared).
        cut.WaitForAssertion(() =>
        {
            mockMutation.Verify(
                m => m.ExecuteAsync(It.IsAny<CreateScenarioInput>(), It.IsAny<CancellationToken>()),
                Times.Once);
            cut.Find("input[id='title']").GetAttribute("value").Should().BeNullOrEmpty();
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ScenarioForm_Submit_UsesTESTKind()
    {
        CreateScenarioInput? capturedInput = null;

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
            .Callback<CreateScenarioInput, CancellationToken>((input, _) => capturedInput = input)
            .ReturnsAsync(mockResult.Object);

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.CreateScenario).Returns(mockMutation.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<ScenarioForm>();
        cut.Find("input[id='title']").Change("My test scenario");
        cut.Find("button[type='submit']").Click();

        cut.WaitForAssertion(() =>
        {
            capturedInput.Should().NotBeNull();
            capturedInput!.Kind.Should().Be(ScenarioKind.Test,
                "manually created scenarios must always be TEST type");
        }, timeout: TimeSpan.FromSeconds(1));
    }
}
