using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

public sealed class PlanExplorerTestingTabTests : BunitContext
{
    private readonly PlanAnalysisService _analysisService = new();

    public PlanExplorerTestingTabTests()
    {
        Services.AddSingleton<IPlanAnalysisService>(_analysisService);
    }

    [Fact]
    public void TestingTab_RendersVersionedFrameworksOnceWithoutHorizontalRules()
    {
        var markdown = """
            # Implementation Plan: Test

            ## Technical Context

            **Testing**: xUnit 2.9.3, Shouldly 4.3.0

            ## Implementation Steps

            ### Step 8 - Unit Tests

            Add to UnitTests:
            ---
            - Validate service behavior

            ### Step 9 - Integration Tests

            Test scenarios (map to acceptance scenarios in spec):
            - POST /Users creates a user
            """;

        var plan = _analysisService.Parse(markdown);

        var cut = Render<PlanExplorerPanel>(parameters => parameters
            .Add(component => component.ParsedPlan, plan)
            .Add(component => component.InitialView, "testing"));

        cut.Markup.Should().Contain("xUnit 2.9.3");
        cut.Markup.Should().Contain("Shouldly 4.3.0");
        cut.Markup.Should().NotContain("xUnit 2.9.3 2.9.3");
        cut.Markup.Should().NotContain("---");
        cut.Markup.Should().Contain("Unit Testing");
        cut.Markup.Should().Contain("Integration Testing");
        cut.Markup.Should().Contain("POST /Users creates a user");
    }

    [Fact]
    public void TestingTab_SCIMPlan_RendersStrategiesAndAllMeaningfulScenariosWithoutHorizontalRules()
    {
        var markdown = File.ReadAllText(@"C:\Users\ajaan\source\sdd-repos\BirkNext\SampleData\autorisasjon\plan.md");
        var plan = _analysisService.Parse(markdown);

        var cut = Render<PlanExplorerPanel>(parameters => parameters
            .Add(component => component.ParsedPlan, plan)
            .Add(component => component.InitialView, "testing"));

        var text = cut.Find(".pe-testing").TextContent;

        text.Should().Contain("xUnit 2.9.3");
        text.Should().Contain("Shouldly 4.3.0");
        text.Should().Contain("NSubstitute 5.x");
        text.Should().Contain("Testcontainers 4.x");
        text.Should().Contain("Unit Testing");
        text.Should().Contain("Integration Testing");
        text.Should().Contain("POST /Users (new user, active=true)");
        text.Should().Contain("POST /Users (existing inactive user, active=true)");
        text.Should().Contain("POST /Users (same request twice)");
        text.Should().Contain("PATCH /Users/{id} (active=false)");
        text.Should().Contain("DELETE /Users/{id}");
        text.Should().Contain("DELETE /Users/{id} (already inactive, repeat)");
        text.Should().Contain("GET /Users (empty)");
        text.Should().Contain("GET /Users (paginated)");
        text.Should().Contain("GET /Users?filter=userName eq");
        text.Should().Contain("GET /Users/{id} (not found)");
        text.Should().Contain("POST /Users with invalid token");
        text.Should().Contain("Health endpoint");
        text.Should().NotContain("---");
    }
}
