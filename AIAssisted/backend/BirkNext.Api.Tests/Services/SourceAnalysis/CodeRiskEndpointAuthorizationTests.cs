using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Api.Services.SourceAnalysis.CodeRisk;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.SourceAnalysis;

/// <summary>
/// Task 9 §20: endpoint authorization metadata carries the literal policy and role names and the route template the source states, so API
/// Quality Review can suggest authorization checks. Only literals are read — a constant or computed policy name is not resolved or guessed.
/// </summary>
public sealed class CodeRiskEndpointAuthorizationTests
{
    private static IReadOnlyList<BirkNext.AiCodeReview.CodeEndpointAuthorization> Endpoints(string code)
    {
        var files = new[] { new SourceFile("src/Api/Api.csproj", "<Project />"), new SourceFile("src/Api/Endpoints.cs", code) };
        return CodeRiskSourceAnalyzer.Analyze(Guid.NewGuid(), files, files.Select(f => f.Path).ToList(), []).Endpoints;
    }

    [Fact]
    public void ControllerRoutesPoliciesAndRolesAreCapturedLiterally()
    {
        var endpoints = Endpoints("""
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.AspNetCore.Authorization;
            [ApiController, Route("api/[controller]"), Authorize(Policy = "ReadOrders")]
            public sealed class OrdersController : ControllerBase
            {
                [HttpGet("{id}")] public IActionResult Get(int id) => Ok();
                [HttpGet("admin"), Authorize(Roles = "Admin, Auditor")] public IActionResult Admin() => Ok();
                [HttpPost, Authorize("WriteOrders")] public IActionResult Create() => Ok();
                [HttpGet("public"), AllowAnonymous] public IActionResult Public() => Ok();
                [HttpGet("constant"), Authorize(Policy = Policies.Admin)] public IActionResult Constant() => Ok();
            }
            """);

        var get = endpoints.Single(e => e.Display == "GET OrdersController.Get");
        get.Route.Should().Be("/api/Orders/{id}");
        get.Policies.Should().Equal("ReadOrders");
        get.Roles.Should().BeEmpty();

        var admin = endpoints.Single(e => e.Display == "GET OrdersController.Admin");
        admin.Route.Should().Be("/api/Orders/admin");
        admin.Roles.Should().Equal("Admin", "Auditor");
        admin.Policies.Should().Equal(["ReadOrders"], "the type's policy applies as well");

        endpoints.Single(e => e.Display == "POST OrdersController.Create").Policies.Should().Equal("ReadOrders", "WriteOrders");
        endpoints.Single(e => e.Display == "GET OrdersController.Public").AllowAnonymous.Should().BeTrue();
        endpoints.Single(e => e.Display == "GET OrdersController.Constant").Policies.Should().Equal(["ReadOrders"], "a non-literal policy name is not guessed");
    }

    [Fact]
    public void MinimalApiPatternsAndRequireAuthorizationPoliciesAreCaptured()
    {
        var endpoints = Endpoints("""
            var app = WebApplication.Create();
            app.MapGet("/api/reports", () => "ok").RequireAuthorization("Reports", "Auditors");
            app.MapGet("api/health", () => "ok").AllowAnonymous();
            app.MapGet("/api/me", () => "ok").RequireAuthorization();
            """);

        endpoints.Single(e => e.Display == "GET /api/reports").Should().BeEquivalentTo(new { Route = "/api/reports", Authorized = true }, o => o.ExcludingMissingMembers());
        endpoints.Single(e => e.Display == "GET /api/reports").Policies.Should().Equal("Reports", "Auditors");
        endpoints.Single(e => e.Display == "GET api/health").Route.Should().Be("/api/health");
        endpoints.Single(e => e.Display == "GET /api/me").Policies.Should().BeEmpty("authenticated only — no role or policy is invented");
    }
}
