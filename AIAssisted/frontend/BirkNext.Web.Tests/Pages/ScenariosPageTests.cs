using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using StrawberryShake;

namespace BirkNext.Web.Tests.Pages;

public class ScenariosPageTests : BunitContext
{
    // ── T051 ──────────────────────────────────────────────────────────────────

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
}
