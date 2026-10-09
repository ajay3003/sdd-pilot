using System.Security.Claims;
using BirkNext.Api.Controllers;
using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.Api.Services.ActiveEventTesting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BirkNext.Api.Tests.Controllers;

public sealed class ActiveCdcTestsControllerAuthorizationTests
{
    [Fact]
    public async Task Scenarios_RejectsAnonymousCaller()
    {
        var controller = Controller(new ClaimsPrincipal(new ClaimsIdentity()));

        (await controller.Scenarios("env", "integration", default)).Result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Scenarios_RejectsAuthenticatedCallerWithoutExecutionPermission()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], "test");
        var controller = Controller(new ClaimsPrincipal(identity));

        (await controller.Scenarios("env", "integration", default)).Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Scenarios_AllowsExplicitExecutionPermission()
    {
        var identity = new ClaimsIdentity([new Claim("permission", "ActiveEventExecute")], "test");
        var lifecycle = new Mock<IActiveEventLifecycleService>();
        lifecycle.Setup(x => x.ScenariosAsync("env", "integration", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var controller = Controller(new ClaimsPrincipal(identity), lifecycle.Object);

        (await controller.Scenarios("env", "integration", default)).Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeEquivalentTo(Array.Empty<BirkNext.Integrations.ActiveEventScenarioDescriptor>());
    }

    private static ActiveCdcTestsController Controller(ClaimsPrincipal principal, IActiveEventLifecycleService? lifecycle = null) => new(new Mock<IActiveCdcTestService>().Object, lifecycle ?? new Mock<IActiveEventLifecycleService>().Object)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } },
    };
}
