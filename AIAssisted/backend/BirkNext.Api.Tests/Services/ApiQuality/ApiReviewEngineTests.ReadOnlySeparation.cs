using System.Net;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using Xunit;

namespace BirkNext.Api.Tests.Services.ApiQuality;

/// <summary>
/// Read-only review separated from authentication: a protected target without a usable authenticated context still gets its contract reviewed
/// anonymously, while none of its operations is executed or reported as passed.
/// </summary>
public sealed partial class ApiReviewEngineTests
{
    [Fact]
    public async Task ExpiredSession_ContractIsStillReviewed_OperationsAreNotExecuted_NorReportedAsPassed()
    {
        const string openApi = """
            {"openapi":"3.0.3","info":{"title":"Children","version":"1.0"},
             "paths":{"/api/children":{"get":{"operationId":"getChildren","responses":{"200":{"description":"ok"}}}}}}
            """;
        var fixture = new Fixture { Respond = (req, _) => req.RequestUri!.AbsolutePath == "/swagger/v1/swagger.json"
            ? Json(HttpStatusCode.OK, openApi) : Json(HttpStatusCode.Unauthorized, "{}") };
        var report = await Engine(fixture, new FakeGateway(false)).RunAsync(Request(AuthenticatedTestingMethod.LocalHttpsProxy, false,
            Rest(auth: true, contract: "https://api-dev.example.test/swagger/v1/swagger.json")));

        var target = Assert.Single(report.Targets);
        Assert.Equal(ApiReviewAccessMode.Unavailable, target.AccessMode);
        Assert.True(target.Contract!.Available);
        Assert.Equal(1, target.Contract.OperationCount);
        Assert.All(target.Operations, o => { Assert.False(o.Executed); Assert.Equal(ApiReviewCheckResult.Blocked, o.Result); });
        Assert.DoesNotContain(fixture.Requests, r => r.RequestUri!.AbsolutePath == "/api/children");
        Assert.Equal(ApiAuthenticationEnforcementStatus.NotVerified, target.AuthenticationEnforcement!.Status);
    }
}
