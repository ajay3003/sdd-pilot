using BirkNext.Applicability;
using BirkNext.Technology;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using DetectionConfidence = BirkNext.Applicability.DetectionConfidence;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Technology &amp; Analysis Coverage information architecture: project coverage first (summary, attention, reviews, detected technologies),
/// the product-wide support reference collapsed and filterable, and BirkNext capability gaps worded as tool limitations. Generic fixture
/// (a C# + Python order service) — no M2LB, Terraform or Skolenærvær knowledge.
/// </summary>
public sealed class TechnologyCoverageUxTests : BunitContext
{
    private sealed class FakeCoverageApi(ProjectTechnologyCoverage? coverage) : ITechnologyCoverageApiService
    {
        public Task<ProjectTechnologyCoverage?> GetAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(coverage);
    }

    private static ProjectTechnologyCoverage OrderService() => new()
    {
        EnvironmentId = "orders", SourceSnapshotId = Guid.NewGuid(), SourceArchive = "order-service-source.zip", AnalyzedAt = DateTimeOffset.Parse("2026-10-20T07:25:00Z"),
        Source = new SourceTechnologyCoverage
        {
            TotalFiles = 40, AnalyzedFiles = 30, UnsupportedSourceFiles = 4,
            Technologies =
            [
                new("lang.csharp", "C#", TechnologyArea.Language, DetectionConfidence.Confirmed, 20, ["src/Orders/Program.cs"]),
                new("lang.python", "Python", TechnologyArea.Language, DetectionConfidence.Confirmed, 4, ["tools/seed.py", "tools/report.py"]),
                new("integration.servicebus", "Azure Service Bus", TechnologyArea.Integration, DetectionConfidence.Inferred, 1, ["src/Orders/appsettings.json"]),
                new("pipeline.azuredevops", "Azure Pipelines", TechnologyArea.Pipeline, DetectionConfidence.Confirmed, 1, ["azure-pipelines.yml"]),
            ],
            Capabilities = [new(Capability.SourceCode, DetectionConfidence.Confirmed, "Source files"), new(Capability.BackendApplication, DetectionConfidence.Confirmed, "ASP.NET Core"),
                new(Capability.RestApi, DetectionConfidence.Confirmed, "Controllers"), new(Capability.Messaging, DetectionConfidence.Inferred, "Service Bus"),
                new(Capability.Pipeline, DetectionConfidence.Confirmed, "azure-pipelines.yml")],
        },
    };

    private void Register(ProjectTechnologyCoverage? coverage, string? targetUrl = null)
    {
        Services.AddSingleton<FeatureVisibilityService>();
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "orders", Name = "Orders", TargetUrl = targetUrl } });
        Services.AddSingleton(context.Object);
        Services.AddSingleton(new Mock<IWorkspaceSessionService>().Object);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);
        Services.AddSingleton<ITechnologyCoverageApiService>(new FakeCoverageApi(coverage));
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddScoped<ProjectApplicabilityState>();
    }

    private IRenderedComponent<TechnologyCoverage> RenderLoaded(ProjectTechnologyCoverage? coverage, string? targetUrl = null)
    {
        Register(coverage, targetUrl);
        var cut = Render<TechnologyCoverage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tc-reviews]"));
        return cut;
    }

    [Fact]
    public void ProjectCoverageComesBeforeTheCollapsedSupportReference()
    {
        var cut = RenderLoaded(OrderService());

        var markup = cut.Markup;
        markup.IndexOf("data-testid=\"tc-attention\"", StringComparison.Ordinal).Should().BeLessThan(markup.IndexOf("data-testid=\"tc-reviews\"", StringComparison.Ordinal));
        markup.IndexOf("data-testid=\"tc-detected\"", StringComparison.Ordinal).Should().BeLessThan(markup.IndexOf("data-testid=\"tc-registry\"", StringComparison.Ordinal));
        cut.Find("[data-testid=tc-registry]").HasAttribute("open").Should().BeFalse("the product reference is secondary and collapsed by default");
        cut.FindAll(".tc-category").Should().OnlyContain(c => !c.HasAttribute("open"), "categories start collapsed");
        cut.Find("#tc-reference-h").TextContent.Should().Be("BirkNext capability reference");
        cut.Find("#tc-project-h").TextContent.Should().Be("Project coverage");
    }

    [Fact]
    public void DetectedTechnologiesListOnlyProjectTechnologies_AndTheReferenceStaysComplete()
    {
        var cut = RenderLoaded(OrderService());

        cut.Find("[data-testid=tc-detected]").QuerySelectorAll("tbody tr[data-testid^=tc-tech-]").Should().HaveCount(4);
        cut.FindAll("[data-testid=tc-tech-lang\\.java]").Should().BeEmpty("the global catalog does not leak into the project list");
        cut.FindAll("[data-testid^=tc-ref-]").Select(r => r.GetAttribute("data-testid"))
            .Should().BeEquivalentTo(["tc-ref-lang.csharp", "tc-ref-lang.python", "tc-ref-integration.servicebus", "tc-ref-pipeline.azuredevops"], "the reference starts on this project's technologies");
        cut.Find("[data-testid=tc-reference-project-only]").Change(false);
        cut.FindAll("[data-testid^=tc-ref-]").Should().HaveCount(TechnologySupportRegistry.Technologies.Count, "the full reference is complete");
        cut.Find("[data-testid=tc-detected]").QuerySelectorAll("thead th").Select(th => th.TextContent)
            .Should().Equal("Technology", "Evidence", "BirkNext support", "What works", "Main limitation", "Next action");
    }

    [Fact]
    public void DetectionOnlyIsAToolLimitation_NeverAProjectFailure()
    {
        var cut = RenderLoaded(OrderService());

        var python = cut.Find("[data-testid=tc-tech-lang\\.python]");
        python.QuerySelector("[data-testid=tc-overall]")!.TextContent.Should().Be("Detection only");
        python.QuerySelector("[data-testid=tc-overall]")!.ClassName.Should().Contain("sd-pill-muted");
        cut.Find("[data-testid=tc-tech-lang\\.csharp] [data-testid=tc-overall]").TextContent.Should().Be("Supported");
        cut.FindAll(".sd-pill-attention").Should().BeEmpty();
        cut.Find("[data-testid=tc-attention-unanalyzed]").ClassName.Should().Contain("tc-neutral");
        cut.Find("[data-testid=tc-attention-unanalyzed]").TextContent.Should().Contain("4 source files were not analyzed (BirkNext limitation)");

        var split = cut.Find("[data-testid=tc-review-source-analysis] [data-testid=tc-source-split]").TextContent;
        split.Should().Contain("Analyzed").And.Contain("C#").And.Contain("Detection only").And.Contain("Python");
    }

    [Fact]
    public void AttentionCountsComeFromTheEvaluatedReviews()
    {
        var cut = RenderLoaded(OrderService());
        var state = Services.GetRequiredService<ProjectApplicabilityState>();
        var needsConfiguration = ReviewCatalog.All.Count(r => state.For(r.ReviewId)?.Status == ApplicabilityStatus.NeedsConfiguration);

        needsConfiguration.Should().BeGreaterThan(0);
        cut.Find("[data-testid=tc-attention-needs-configuration]").TextContent.Should().StartWith($"{needsConfiguration} review");
        cut.Find("[data-testid=tc-attention-actions]").QuerySelectorAll("a").Select(a => a.TextContent).Should().Contain("Configure API target");
    }

    [Fact]
    public void NeedsConfigurationLinksToTheRightSettings_NotApplicableHasNoAction()
    {
        var cut = RenderLoaded(OrderService());

        var api = cut.Find("[data-testid=tc-review-api-quality-review]");
        api.GetAttribute("data-status").Should().Be("NeedsConfiguration");
        var link = api.QuerySelector("[data-testid=tc-review-action]")!;
        link.GetAttribute("href").Should().Be("admin/system-settings?section=target-environments&tab=endpoint");
        link.TextContent.Should().StartWith("Configure API target");

        var frontend = cut.Find("[data-testid=tc-review-frontend-quality-review]");
        frontend.GetAttribute("data-status").Should().Be("NotApplicable");
        frontend.QuerySelector("[data-testid=tc-review-action]").Should().BeNull("not applicable is neutral, there is nothing to configure");
        cut.Find("[data-testid=tc-review-pipeline-review] [data-testid=tc-review-action]").GetAttribute("href").Should().Be("pipeline-review");
    }

    [Fact]
    public void EveryReviewActionOpensAPageThatExists()
    {
        var routes = typeof(TechnologyCoverage).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>())
            .Select(r => r.Template.Trim('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var review in ReviewCatalog.All)
        foreach (var status in Enum.GetValues<ApplicabilityStatus>())
        {
            var action = TechnologyCoverageGuidance.ReviewAction(review, new ReviewApplicability { Status = status });
            if (action is null) continue;
            routes.Should().Contain(action.Href.Split('?')[0], $"{review.ReviewId}/{status} must not link to a fabricated route");
        }
        foreach (var technology in OrderService().Source!.Technologies)
            if (TechnologyCoverageGuidance.TechnologyAction(technology, TechnologySupportRegistry.Find(technology.TechnologyId)) is { } action)
                routes.Should().Contain(action.Href.Split('?')[0]);
    }

    [Fact]
    public void FilesNotAnalyzedCanBeOpenedFromTheAttentionSummary()
    {
        var cut = RenderLoaded(OrderService());

        cut.Find("[data-testid=tc-unanalyzed]").HasAttribute("open").Should().BeFalse();
        cut.Find("[data-testid=tc-attention-unanalyzed-open]").Click();
        var panel = cut.Find("[data-testid=tc-unanalyzed]");
        panel.HasAttribute("open").Should().BeTrue();
        var python = cut.Find("[data-testid=tc-unanalyzed-lang\\.python]").TextContent;
        python.Should().Contain("tools/seed.py").And.Contain("tools/report.py").And.Contain("Unsupported language: Source Analysis has no Python analyzer");
        cut.Find("[data-testid=tc-unanalyzed-note]").TextContent.Should().Contain("The files themselves are not invalid");
        python.Should().Contain("+2 more file(s) not listed");
    }

    [Fact]
    public void SupportDetailsShowEveryCapabilityDimension_WithRuntimeKeptSeparate()
    {
        var cut = RenderLoaded(OrderService());

        cut.Find("[data-testid=tc-tech-integration\\.servicebus] [data-testid=tc-details-toggle]").Click();
        var details = cut.Find("[data-testid=tc-details-integration\\.servicebus]");
        details.QuerySelectorAll(".tc-dimensions dt").Select(dt => dt.TextContent)
            .Should().Equal(Enum.GetValues<SupportDimension>().Select(TechnologySupportRegistry.Label));
        details.TextContent.Should().Contain("runtime.servicebus.azure").And.Contain("not a verified runtime resource");
        cut.Find("[data-testid=tc-tech-integration\\.servicebus] [data-testid=tc-details-toggle]").GetAttribute("aria-expanded").Should().Be("true");
    }

    [Fact]
    public void DetectedTechnologyFiltersNarrowTheProjectList()
    {
        var cut = RenderLoaded(OrderService());

        cut.Find("[data-testid=tc-detected-state]").Change("None");
        cut.Find("[data-testid=tc-detected]").QuerySelectorAll("tbody tr[data-testid^=tc-tech-]").Select(r => r.GetAttribute("data-testid")).Should().Equal("tc-tech-lang.python");
        cut.Find("[data-testid=tc-detected-state]").Change("");
        cut.Find("[data-testid=tc-detected-search]").Input("pipelines");
        cut.Find("[data-testid=tc-detected-count]").TextContent.Should().Be("Showing 1 of 4 detected technologies.");
    }

    [Fact]
    public void SupportReferenceSearchCategoryAndCapabilityFiltersWork()
    {
        var cut = RenderLoaded(OrderService());

        cut.Find("[data-testid=tc-reference-project-only]").Change(false);
        cut.Find("[data-testid=tc-reference-search]").Input("kafka");
        cut.FindAll("[data-testid^=tc-ref-]").Select(r => r.GetAttribute("data-testid")).Should().Equal("tc-ref-integration.kafka");
        cut.FindAll(".tc-category").Should().ContainSingle().Which.HasAttribute("open").Should().BeTrue("a filtered category opens to show its match");

        cut.Find("[data-testid=tc-reference-search]").Input("");
        cut.Find("[data-testid=tc-reference-area]").Change(nameof(TechnologyArea.Database));
        cut.FindAll(".tc-category").Select(c => c.GetAttribute("data-testid")).Should().Equal("tc-category-Database");
        cut.FindAll("[data-testid^=tc-ref-]").Should().HaveCount(TechnologySupportRegistry.Matrix(TechnologyArea.Database).Count());

        cut.Find("[data-testid=tc-reference-area]").Change("");
        cut.Find("[data-testid=tc-reference-capability]").Change(nameof(SupportDimension.ActiveTest));
        cut.Find("[data-testid=tc-reference-state]").Change("Supported");
        var expected = TechnologySupportRegistry.Technologies.Count(t => t.ActiveTest == SupportLevel.Full);
        cut.FindAll("[data-testid^=tc-ref-]").Should().HaveCount(expected);
    }

    [Fact]
    public void GenericProjectNeverOffersToEnableTheM2lbExtension()
    {
        var cut = RenderLoaded(OrderService());

        var row = cut.Find("[data-testid=tc-review-security-classification-review]");
        row.GetAttribute("data-status").Should().Be("NotApplicable");
        row.QuerySelector("[data-testid=tc-review-action]").Should().BeNull("domain extensions are never one click away from the coverage page");
        cut.Find("[data-testid=tc-extensions]").TextContent.Should().Be("None");
        cut.Find("[data-testid=tc-project-type]").TextContent.Should().Be("Generic project");
        row.TextContent.Should().Contain("Optional: Apply the M2LB integration template only if this is an M2LB environment.");
    }

    [Fact]
    public void ConfiguredTargetMovesReviewsOutOfNeedsConfiguration()
    {
        var cut = RenderLoaded(OrderService(), targetUrl: "https://orders.example.test/");

        cut.Find("[data-testid=tc-review-api-quality-review]").GetAttribute("data-status").Should().Be("Applicable");
        cut.Find("[data-testid=tc-review-api-quality-review] [data-testid=tc-review-action]").GetAttribute("href").Should().Be("api-quality-review");
    }

    [Fact]
    public void ProjectWordingKeepsCapabilityAndQualityApart()
    {
        TechnologyCoverageGuidance.ProjectSupport(SupportLevel.Unsupported).Should().Be("Detection only");
        TechnologyCoverageGuidance.ProjectSupport(SupportLevel.Planned).Should().Be("Detection only");
        TechnologyCoverageGuidance.ProjectSupport(SupportLevel.Full).Should().Be("Supported");
        TechnologyCoverageGuidance.CapabilityLabel(SupportLevel.Unsupported).Should().Be("Not available in BirkNext");
        TechnologyCoverageGuidance.ReviewAction(ReviewCatalog.Find("frontend-quality-review")!, new ReviewApplicability { Status = ApplicabilityStatus.NotApplicable }).Should().BeNull();
        TechnologyCoverageGuidance.ReviewAction(ReviewCatalog.Find("source-analysis")!, new ReviewApplicability { Status = ApplicabilityStatus.Unsupported }).Should().BeNull();
        TechnologyCoverageGuidance.MainLimitation(TechnologySupportRegistry.Find("integration.kafka")).Should().NotContain("No broker", "only the first sentence is on the row");
    }

    [Fact]
    public void FileCountsExplainTheDenominator_WithoutInventingABreakdown()
    {
        var cut = RenderLoaded(OrderService());

        cut.Find("[data-testid=tc-analyzed]").TextContent.Should().Be("30");
        cut.Find("[data-testid=tc-unsupported-files]").TextContent.Should().Be("4");
        cut.Find("[data-testid=tc-outside-scope]").TextContent.Should().Be("6", "40 in archive − 30 analyzed − 4 recognized not analyzed");
        cut.Find("[data-testid=tc-total]").TextContent.Should().Be("40");
        cut.Find("[data-testid=tc-file-note]").TextContent.Should().Contain("does not break them down further");
        TechnologyCoverageGuidance.OutsideSourceScope(new SourceTechnologyCoverage { TotalFiles = 5, AnalyzedFiles = 4, UnsupportedSourceFiles = 3 }).Should().Be(0, "never negative");
    }

    [Fact]
    public void ReviewsShowApplicabilityReadinessAndCoverageSeparately()
    {
        var cut = RenderLoaded(OrderService());

        cut.Find("[data-testid=tc-reviews] thead").TextContent.Should().Contain("Applicability").And.Contain("Readiness").And.Contain("Coverage");
        var api = cut.Find("[data-testid=tc-review-api-quality-review]");
        api.QuerySelector("[data-testid=tc-review-applicability]")!.TextContent.Should().Be("Applicable");
        api.QuerySelector("[data-testid=tc-review-readiness]")!.TextContent.Should().Be("Needs configuration");
        api.QuerySelector("[data-testid=tc-review-coverage]")!.TextContent.Should().Be("Not assessed");
        var frontend = cut.Find("[data-testid=tc-review-frontend-quality-review]");
        frontend.QuerySelector("[data-testid=tc-review-applicability]")!.TextContent.Should().Be("Not applicable");
        frontend.QuerySelector("[data-testid=tc-review-readiness] .sd-pill")!.ClassName.Should().Contain("sd-pill-muted");

        TechnologyCoverageGuidance.Dimensions(ApplicabilityStatus.PartiallyApplicable).Should().Be(new ReviewDimensions("Applicable", "Ready", "Partial"));
        TechnologyCoverageGuidance.Dimensions(ApplicabilityStatus.Unsupported).Readiness.Should().Be("Not supported by BirkNext");
        TechnologyCoverageGuidance.Dimensions(ApplicabilityStatus.NotEnoughEvidence).Applicability.Should().Be("Not determined");
    }

    [Fact]
    public void AttentionSeparatesSetupFromLimitations_AndNotApplicableIsNeutral()
    {
        var cut = RenderLoaded(OrderService());
        var state = Services.GetRequiredService<ProjectApplicabilityState>();
        var evaluated = ReviewCatalog.All.Select(r => state.For(r.ReviewId)).ToList();
        var counts = TechnologyCoverageGuidance.ReadinessCounts(evaluated);

        cut.Find("[data-testid=tc-readiness]").TextContent.Should().Contain($"Need setup{counts.NeedSetup}").And.Contain($"Not applicable{counts.NotApplicable}");
        var markup = cut.Markup;
        markup.IndexOf("tc-attention-setup", StringComparison.Ordinal).Should().BeLessThan(markup.IndexOf("tc-attention-limitations", StringComparison.Ordinal));
        cut.Find("[data-testid=tc-attention]").TextContent.Should().NotContain("not applicable", "not applicable is never an attention item");
        cut.Find("[data-testid=tc-attention-not-applicable]").TextContent.Should().Contain("This is not a problem");
        cut.Find("[data-testid=tc-attention-limitations] + ul [data-testid=tc-attention-unanalyzed]").Should().NotBeNull();
    }

    [Fact]
    public void DetectedTechnologiesSeparateSourceEvidenceFromSupport_AndNeverDeadEnd()
    {
        var cut = RenderLoaded(OrderService());

        cut.Find("[data-testid=tc-tech-lang\\.csharp] [data-testid=tc-evidence]").TextContent.Should().StartWith("Detected in source");
        cut.Find("[data-testid=tc-tech-integration\\.servicebus] [data-testid=tc-evidence]").TextContent.Should().StartWith("Inferred from source");
        cut.Find("[data-testid=tc-tech-lang\\.python] [data-testid=tc-what-works]").TextContent.Should().Be("Detection only");
        cut.Find("[data-testid=tc-tech-lang\\.python] [data-testid=tc-no-action]").TextContent.Should().Be("No Source Analysis action available");
        cut.Markup.Should().NotContain("No BirkNext action");
        cut.FindAll("[data-testid=tc-reference-providers-cell]").Should().BeEmpty("implementation details are hidden by default");
        cut.Find("[data-testid=tc-reference-show-providers]").Change(true);
        cut.FindAll("[data-testid=tc-reference-providers-cell]").Should().NotBeEmpty();
        cut.FindAll(".tc-cell-na").Should().OnlyContain(c => c.TextContent == TechnologyCoverageGuidance.CapabilityLabel(SupportLevel.NotApplicable));
    }

    [Fact]
    public void EveryPartialTechnologySaysWhatWorksAndWhatIsLimited()
    {
        foreach (var t in TechnologySupportRegistry.Technologies.Where(t => t.Overall == SupportLevel.Partial))
        {
            TechnologyCoverageGuidance.WhatWorks(t).Should().NotBe("Detection only", t.TechnologyId);
            TechnologyCoverageGuidance.MainLimitation(t).Should().NotBeNullOrWhiteSpace(t.TechnologyId).And.NotBe("—", t.TechnologyId)
                .And.NotStartWith("Source configuration is analyzed", "the row shows a limitation, not a capability");
        }
    }

    [Fact]
    public void ContractTechnologiesOpenWhereTheyAreReviewed()
    {
        var snapshot = Guid.Parse("6f1c2d3e-0000-4000-8000-000000000001");
        DetectedTechnology Tech(string id) => new(id, id, TechnologyArea.Contract, DetectionConfidence.Confirmed, 1, ["x"]);

        TechnologyCoverageGuidance.TechnologyAction(Tech("contract.xsd"), TechnologySupportRegistry.Find("contract.xsd"), snapshot)!.Href.Should().Be("schema-studio");
        TechnologyCoverageGuidance.TechnologyAction(Tech("contract.openapi"), TechnologySupportRegistry.Find("contract.openapi"), snapshot)!.Href
            .Should().Be($"source-analysis?area=quality-evidence&view=contracts&snapshot={snapshot:D}");
        TechnologyCoverageGuidance.TechnologyAction(Tech("contract.openapi"), TechnologySupportRegistry.Find("contract.openapi"))!.Href
            .Should().Be("source-analysis?area=quality-evidence&view=contracts");
    }
}
