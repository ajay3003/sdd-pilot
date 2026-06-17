using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Pages;

public class ArchitectureViewTests : BunitContext
{
    // Minimal spec with an "API Surface" section — reliably produces architecture elements via Pass-1 structured node extraction
    private const string MinimalSpec = @"
# Technical Design

## API Surface
- **GraphQL** — queries for the presentation layer
- **REST /users** — user management endpoints
";

    private static ExtractionCandidate MakeCandidate(
        string title,
        ScenarioKind kind = ScenarioKind.Requirement,
        string heading = "Technical Design") =>
        new ExtractionCandidate
        {
            Title = title,
            Classification = kind,
            ClassificationSignal = ClassificationSignal.Default,
            SourceBlockType = BlockType.ParagraphLine,
            ContextHeading = heading,
        };

    [Fact]
    public void ArchitectureView_ShowsOnlyStructuredComponents()
    {
        var cut = Render<ArchitectureView>(p => p
            .Add(x => x.SpecMarkdown, MinimalSpec)
            .Add(x => x.Candidates, Array.Empty<ExtractionCandidate>()));

        cut.Find("[data-testid='av-root']").Should().NotBeNull();
        cut.FindAll("[data-testid='av-arch-notes']").Should().BeEmpty();
    }

    [Fact]
    public void ArchitectureView_DoesNotRenderRawSpecDump()
    {
        var archCandidates = new[]
        {
            MakeCandidate("Some API surface item", heading: "API Surface"),
            MakeCandidate("Key entity description", heading: "Key Entities"),
        };

        var cut = Render<ArchitectureView>(p => p
            .Add(x => x.SpecMarkdown, null)
            .Add(x => x.Candidates, archCandidates));

        cut.Find("[data-testid='av-not-generated']").Should().NotBeNull();
        cut.FindAll("[data-testid='av-arch-notes']").Should().BeEmpty();
        cut.Markup.Should().Contain("No architecture mappings available yet");
    }

    [Fact]
    public void ArchitectureView_HasTraceabilityLinks()
    {
        var cut = Render<ArchitectureView>(p => p
            .Add(x => x.SpecMarkdown, MinimalSpec)
            .Add(x => x.Candidates, Array.Empty<ExtractionCandidate>()));

        cut.Find("button.av-item-btn").Click();

        cut.Find("[data-testid='av-source-links']").Should().NotBeNull();
        cut.FindAll("[data-testid='av-link-spec-drift']").Should().NotBeEmpty();
        cut.FindAll("[data-testid='av-link-traceability']").Should().NotBeEmpty();
    }

    [Fact]
    public void ArchitectureView_ConnectsToSpecDrift()
    {
        var cut = Render<ArchitectureView>(p => p
            .Add(x => x.SpecMarkdown, MinimalSpec)
            .Add(x => x.Candidates, Array.Empty<ExtractionCandidate>()));

        cut.Find("button.av-item-btn").Click();

        cut.Find("[data-testid='av-link-spec-drift']")
            .GetAttribute("href")
            .Should().Contain("spec-drift");
    }

    [Fact]
    public void ArchitectureView_ConnectsToTraceabilityMatrix()
    {
        var cut = Render<ArchitectureView>(p => p
            .Add(x => x.SpecMarkdown, MinimalSpec)
            .Add(x => x.Candidates, Array.Empty<ExtractionCandidate>()));

        cut.Find("button.av-item-btn").Click();

        cut.Find("[data-testid='av-link-traceability']")
            .GetAttribute("href")
            .Should().Contain("traceability");
    }
}
