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
    public void ArchitectureView_WithNoMarkdown_DoesNotDumpRawSpecContent()
    {
        // Regardless of whether the extractor finds elements or not, the view must never
        // render raw spec text as-is. It shows either a structured model or an empty state.
        var archCandidates = new[]
        {
            MakeCandidate("Some API surface item", heading: "API Surface"),
            MakeCandidate("Key entity description", heading: "Key Entities"),
        };

        var cut = Render<ArchitectureView>(p => p
            .Add(x => x.SpecMarkdown, null)
            .Add(x => x.Candidates, archCandidates));

        // The view must not show a loading state or an error state.
        cut.FindAll("[data-testid='av-loading']").Should().BeEmpty("loading must not persist");
        cut.FindAll("[data-testid='av-failed']").Should().BeEmpty("no extraction error expected");
        cut.Markup.Should().Contain("Potential Architecture Elements");

        // Raw candidate titles must not be dumped as unstructured text blobs.
        cut.FindAll("[data-testid='av-arch-notes']").Should().BeEmpty();
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
