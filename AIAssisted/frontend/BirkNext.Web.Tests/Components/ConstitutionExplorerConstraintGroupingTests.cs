using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

public sealed class ConstitutionExplorerConstraintGroupingTests : BunitContext
{
    public ConstitutionExplorerConstraintGroupingTests()
    {
        Services.AddSingleton<IConstitutionAnalysisService, ConstitutionAnalysisService>();
        Services.AddSingleton<MarkdownRenderingService>();
    }

    [Fact]
    public void ConstraintsTab_SingleRedundantGroup_HidesHeading()
    {
        var cut = RenderConstraints(
            Constraint("Performance as First-Class Concern", "Performance as First-Class Concern"));

        cut.Markup.Should().NotContain("Performance as First-Class Concern Constraints");
        cut.Find(".ce-constraint-title").TextContent.Should().Be("Performance as First-Class Concern");
    }

    [Fact]
    public void ConstraintsTab_SingleRedundantGroupWithCaseAndPunctuationDifferences_HidesHeading()
    {
        var cut = RenderConstraints(
            Constraint("Strict Role\u2013Operation Separation", "STRICT ROLE-OPERATION SEPARATION"));

        cut.Markup.Should().NotContain("STRICT ROLE-OPERATION SEPARATION Constraints");
        cut.Find(".ce-constraint-title").TextContent.Should().Be("Strict Role\u2013Operation Separation");
    }

    [Fact]
    public void ConstraintsTab_MeaningfulSingleItemGroup_ShowsHeading()
    {
        var cut = RenderConstraints(
            Constraint("Audit Trail Requirements", "Frontend"));

        cut.Markup.Should().Contain("Frontend Constraints");
        cut.Find(".ce-constraint-title").TextContent.Should().Be("Audit Trail Requirements");
    }

    [Fact]
    public void ConstraintsTab_MultiItemGroup_ShowsHeading()
    {
        var cut = RenderConstraints(
            Constraint("Authentication", "Security"),
            Constraint("Authorization", "Security"));

        cut.Markup.Should().Contain("Security Constraints");
        cut.FindAll(".ce-constraint-title").Select(title => title.TextContent)
            .Should().Equal("Authentication", "Authorization");
    }

    private Bunit.IRenderedComponent<ConstitutionExplorerPanel> RenderConstraints(params ConstitutionConstraint[] constraints)
    {
        var document = new ConstitutionDocument
        {
            Title = "Test Constitution",
            Constraints = constraints.ToList(),
        };

        return Render<ConstitutionExplorerPanel>(parameters => parameters
            .Add(component => component.ParsedDocument, document)
            .Add(component => component.InitialView, "constraints"));
    }

    private static ConstitutionConstraint Constraint(string title, string scope) => new()
    {
        Title = title,
        Scope = scope,
        Description = "Constraint description.",
        RawText = "Constraint description.",
        IsPlatformWide = false,
    };
}
