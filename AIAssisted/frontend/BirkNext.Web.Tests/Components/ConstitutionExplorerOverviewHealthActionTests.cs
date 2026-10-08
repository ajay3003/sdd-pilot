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
                TotalCatalogEntries = 1,
                RelationshipPopulationCount = 1,
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
        action.TextContent.Trim().Should().Be("View unconnected entries");

        action.Click();

        cut.Markup.Should().Contain("Rule Catalog");
        cut.FindAll(".ce-type-chip.is-active")
            .Should()
            .Contain(button => button.TextContent.Contains("Unconnected Only", StringComparison.Ordinal));
    }

    [Fact]
    public void Overview_SeparatesStructureScopeAndRelationships_AndKeepsTabsAsNavigation()
    {
        var document = new ConstitutionDocument
        {
            Title = "Sample Service Constitution",
            Type = ConstitutionType.Service,
            Principles = [new() { Title = "P" }],
            Standards = [new() { Title = "S" }],
            Constraints = [new() { Title = "C" }],
            GovernanceItems = [new() { Title = "G" }],
            RuleCatalog =
            [
                new() { RuleId = "PP-01", Title = "P", RuleType = ConstitutionRuleType.Principle },
                new() { RuleId = "PS-01", Title = "S", RuleType = ConstitutionRuleType.Standard },
                new() { RuleId = "MC-01", Title = "C", RuleType = ConstitutionRuleType.Constraint },
                new() { RuleId = "GOV-001", Title = "G", RuleType = ConstitutionRuleType.Governance },
            ],
            Health = new()
            {
                TotalPrinciples = 1,
                TotalStandards = 1,
                TotalConstraints = 1,
                TotalGovernanceItems = 1,
                TotalRules = 4,
                AuthoredRuleCount = 4,
                TotalCatalogEntries = 4,
                RelationshipPopulationCount = 4,
                TotalReferences = 2,
                OrphanRules = 2,
                ModuleConstraints = 1,
            },
        };

        var cut = Render<ConstitutionExplorerPanel>(parameters => parameters
            .Add(component => component.ParsedDocument, document));

        cut.Markup.Should().Contain("Structure").And.Contain("Governance rule");
        cut.Markup.Should().Contain("Scope: Service · Inferred from title");
        cut.Markup.Should().Contain("non-platform-scoped constraints (subset of Constraints)");
        cut.Markup.Should().Contain("2 of 4 catalog entries have no incoming or outgoing cross-rule references");
        cut.Markup.Should().Contain("Authored rules").And.Contain("Catalog entries");
        cut.Markup.Should().Contain("Rule Traceability (4 entries)");
        cut.Markup.Should().NotContain("ce-nav-cards");
        cut.Markup.Should().NotContain("HEALTHY");

        cut.FindAll(".ce-view-btn").Single(button => button.TextContent.Contains("Rule Traceability", StringComparison.Ordinal)).Click();
        cut.FindAll(".ce-trace-card").Should().HaveCount(4);
        cut.FindAll(".ce-view-btn").Single(button => button.TextContent.Trim() == "Rule Catalog (4 entries)").Click();
        cut.FindAll(".ce-catalog-row").Should().HaveCount(4);
        cut.FindAll(".ce-view-btn").Single(button => button.TextContent.Trim() == "Map").Click();
        cut.Find(".ce-map-tree").Should().NotBeNull();
    }

    [Fact]
    public void EmptyChangelog_IsPresentedAsNeutralAbsence()
    {
        var cut = Render<ConstitutionExplorerPanel>(parameters => parameters
            .Add(component => component.ParsedDocument, new ConstitutionDocument { Title = "Generic Constitution" })
            .Add(component => component.InitialView, "changelog"));

        cut.Markup.Should().Contain("No changelog entries");
        cut.Markup.Should().NotContain("No Changelog Found");
        cut.Markup.Should().NotContain("warning");
    }

    [Fact]
    public void GenericTitle_DoesNotPresentFallbackAsInferredGeneralScope()
    {
        var cut = Render<ConstitutionExplorerPanel>(parameters => parameters
            .Add(component => component.ParsedDocument, new ConstitutionAnalysisService().Parse("# Person-adapter Constitution\n\n## Principles\n\n### PP-01 — Local rule\n\nReferences PS-09.")));

        cut.Markup.Should().Contain("Scope: Not declared · Unspecified");
        cut.Markup.Should().Contain("No explicit scope metadata was found");
        cut.Markup.Should().NotContain("General");
    }

    [Fact]
    public void ExplicitScopeMetadata_IsLabeledExplicit()
    {
        var cut = Render<ConstitutionExplorerPanel>(parameters => parameters
            .Add(component => component.ParsedDocument, new ConstitutionDocument { Title = "Custom Constitution", Scope = "Service" }));

        cut.Markup.Should().Contain("Scope: Service · Explicit metadata");
    }

    [Fact]
    public void PersonAdapterOverview_LabelsAuthoredReferenceOnlyAndRelationshipPopulations()
    {
        var text = File.ReadAllText(BirkNext.Web.Tests.TestDataHelper.ResolveFixturePath("person-adapter", "constitution.md"));
        var document = new ConstitutionAnalysisService().Parse(text);
        var cut = Render<ConstitutionExplorerPanel>(parameters => parameters.Add(component => component.ParsedDocument, document));

        cut.Markup.Should().Contain("Authored rules")
            .And.Contain("Catalog entries")
            .And.Contain("catalog entries have no incoming or outgoing cross-rule references")
            .And.Contain("Scope: Not declared · Unspecified");

        var entryCount = document.RuleCatalog.Count;
        cut.FindAll(".ce-view-btn").Single(button => button.TextContent.Contains("Rule Traceability", StringComparison.Ordinal)).TextContent.Should().Contain($"{entryCount} entries");
        cut.FindAll(".ce-view-btn").Single(button => button.TextContent.Contains("Rule Catalog", StringComparison.Ordinal)).TextContent.Should().Contain($"{entryCount} entries");
    }
}
