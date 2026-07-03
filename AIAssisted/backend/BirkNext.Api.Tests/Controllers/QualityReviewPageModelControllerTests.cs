using BirkNext.Api.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Tests.Controllers;

public sealed class QualityReviewPageModelControllerTests
{
    [Fact]
    public void Route_UsesFrontendQualityPageModelPath()
    {
        var route = typeof(QualityReviewPageModelController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .OfType<RouteAttribute>()
            .Single();

        Assert.Equal("api/quality-review-page-model", route.Template);
    }
}
