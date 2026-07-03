using BirkNext.Api.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Tests.Controllers;

public sealed class AnalysisPageModelControllerTests
{
    [Fact]
    public void Route_UsesFrontendAnalysisPageModelPath()
    {
        var route = typeof(AnalysisPageModelController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .OfType<RouteAttribute>()
            .Single();

        Assert.Equal("api/analysis-page-model", route.Template);
    }
}
