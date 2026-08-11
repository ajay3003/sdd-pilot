using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

public sealed class ConstitutionExplorerOverviewHealthActionTests : BunitContext
{
    public ConstitutionExplorerOverviewHealthActionTests()
    {
        Services.AddSingleton<IConstitutionAnalysisService, ConstitutionAnalysisService>();
        Services.AddSingleton<MarkdownRenderingService>();
    }

    [Fact]
    public void Overview_UnconnectedRulesAction_IsInlineSemanticButtonAndKeepsNavigation()
    {
        var document = new ConstitutionDocument
        {
            Title = "Test Constitution",
            RuleCatalog =
            [
                new ConstitutionRule
                {
                    RuleId = "PP-01",
                    Title = "Unconnected Rule",
                    RuleType = ConstitutionRuleType.Principle,
                },
            ],
            Health = new ConstitutionHealth
            {
                OrphanRules = 1,
                TotalRules = 1,
                Indicators =
                [
                    new ConstitutionHealthIndicator
                    {
                        Icon = "\u24d8",
                        Message = "1 unconnected rule — with no connections to other rules",
                        Level = HealthIndicatorLevel.Good,
                    },
                ],
            },
        };

        var cut = Render<ConstitutionExplorerPanel>(parameters => parameters
            .Add(component => component.ParsedDocument, document));

        var action = cut.Find("button.ce-indicator-action-button");
        action.GetAttribute("type").Should().Be("button");
        action.TextContent.Trim().Should().Be("View rules");

        action.Click();

        cut.Markup.Should().Contain("Rule Catalog");
        cut.FindAll(".ce-type-chip.is-active")
            .Should()
            .Contain(button => button.TextContent.Contains("Unconnected Only", StringComparison.Ordinal));
    }
}
