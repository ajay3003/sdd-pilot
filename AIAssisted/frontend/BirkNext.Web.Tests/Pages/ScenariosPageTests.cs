using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Pages;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StrawberryShake;

namespace BirkNext.Web.Tests.Pages;

public class ScenariosPageTests : BunitContext
{
    [Fact]
    public void ScenariosPage_SuccessfulScenarioLoad_RendersScenariosAndPreservesSearchFilter()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult(
            [
                MakeScenario("sc-1", "Login requirement", "Users can sign in", ScenarioKind.Requirement),
                MakeScenario("sc-2", "Checkout test", null, ScenarioKind.Test),
            ]));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='scenario-row']").Should().HaveCount(2);
            cut.FindAll("[data-testid='coverage-dashboard']").Should().BeEmpty();
            cut.Markup.Should().NotContain("Loading scenarios");
        }, timeout: TimeSpan.FromSeconds(1));

        cut.Find("input[aria-label='Search scenarios']").Input("checkout");

        cut.FindAll("[data-testid='scenario-row']").Should().ContainSingle();
        cut.Markup.Should().Contain("Checkout test");
        cut.Markup.Should().NotContain("Login requirement");
    }

    [Fact]
    public void ScenariosPage_EmptyScenarioLoad_RendersEmptyState()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeGetScenariosResult([]));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='scenario-row']").Should().BeEmpty();
            cut.Find("[data-testid='empty-state']").TextContent.Should().Contain("No scenarios yet");
            cut.Markup.Should().NotContain("Loading scenarios");
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ScenariosPage_FailedScenarioLoad_ShowsInlineErrorInsteadOfThrowing()
    {
        var mockQuery = new Mock<IGetScenariosQuery>();
        mockQuery
            .Setup(q => q.ExecuteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Backend unavailable"));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.GetScenarios).Returns(mockQuery.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<Scenarios>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='scenario-load-error']")
                .TextContent.Should().Contain("couldn't load scenarios");
            cut.Markup.Should().NotContain("Loading scenarios");
            cut.Find("input[id='title']").Should().NotBeNull();
        }, timeout: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ScenarioForm_NetworkException_ShowsUserFriendlyErrorMessage()
    {
        var mockMutation = new Mock<ICreateScenarioMutation>();
        mockMutation
            .Setup(m => m.ExecuteAsync(It.IsAny<CreateScenarioInput>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network unavailable"));

        var mockClient = new Mock<IBirkNextClient>();
        mockClient.Setup(c => c.CreateScenario).Returns(mockMutation.Object);
        Services.AddSingleton(mockClient.Object);

        var cut = Render<ScenarioForm>();

        cut.Find("input[id='title']").Change("Network test scenario");
        cut.Find("select[id='kind']").Change("Requirement");
        cut.Find("button[type='submit']").Click();

        cut.WaitForAssertion(() =>
            cut.Find("[role='alert']").TextContent
                .Should().Contain("Something went wrong"),
            timeout: TimeSpan.FromSeconds(1));
    }

    private static IOperationResult<IGetScenariosResult> MakeGetScenariosResult(
        IReadOnlyList<IGetScenarios_Scenarios> scenarios)
    {
        var mockData = new Mock<IGetScenariosResult>();
        mockData.Setup(d => d.Scenarios).Returns(scenarios);

        var mockResult = new Mock<IOperationResult<IGetScenariosResult>>();
        mockResult.Setup(r => r.Data).Returns(mockData.Object);
        mockResult.Setup(r => r.Errors).Returns([]);

        return mockResult.Object;
    }

    private static IGetScenarios_Scenarios MakeScenario(
        string id,
        string title,
        string? description,
        ScenarioKind kind)
    {
        var mockScenario = new Mock<IGetScenarios_Scenarios>();
        mockScenario.Setup(s => s.Id).Returns(id);
        mockScenario.Setup(s => s.Title).Returns(title);
        mockScenario.Setup(s => s.Description).Returns(description);
        mockScenario.Setup(s => s.Kind).Returns(kind);
        mockScenario.Setup(s => s.CreatedAt).Returns(DateTimeOffset.UtcNow);

        return mockScenario.Object;
    }
}
