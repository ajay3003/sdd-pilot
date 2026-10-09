using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

public sealed class ExplorerProjectionDomIdentityTests : BunitContext
{
    private readonly ConstitutionAnalysisService _constitution = new();
    private readonly PlanAnalysisService _plan = new();
    private readonly DataModelAnalysisService _dataModel = new();

    public ExplorerProjectionDomIdentityTests()
    {
        Services.AddSingleton<IConstitutionAnalysisService>(_constitution);
        Services.AddSingleton<IPlanAnalysisService>(_plan);
        Services.AddSingleton<IDataModelAnalysisService>(_dataModel);
        Services.AddSingleton<MarkdownRenderingService>();
        JSInterop.SetupVoid("fileImport.initDropZone", _ => true);
    }

    [Fact]
    public void Specification_renders_the_existing_projection_identity()
    {
        const string markdown = "# Requirements\n- REQ-1: Sign in\n";
        var parsed = SpecExplorerService.Parse(markdown);
        var expected = FindProvenance(parsed)?.ProjectionId;
        expected.Should().NotBeNull();

        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, markdown));

        cut.Find($"[data-birknext-projection-id='{expected}']").GetAttribute("data-birknext-projection-kind").Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Constitution_renders_the_existing_projection_identity()
    {
        var document = _constitution.Parse("# Constitution\n## Principles\n### PP-01: Safety\nKeep user data safe.\n");
        var expected = document.Principles.Single().Provenance!.ProjectionId;

        var cut = Render<ConstitutionExplorerPanel>(p => p.Add(c => c.ParsedDocument, document).Add(c => c.InitialView, "principles"));

        cut.Find($"[data-birknext-projection-id='{expected}']").GetAttribute("data-birknext-projection-kind").Should().Be("Principle");
    }

    [Fact]
    public void Plan_renders_the_existing_projection_identity()
    {
        var plan = _plan.Parse("# Plan\n## Risks\n### High Risk: Data loss\nBackups may fail.\n");
        var expected = plan.Risks.Single().Provenance!.ProjectionId;

        var cut = Render<PlanExplorerPanel>(p => p.Add(c => c.ParsedPlan, plan).Add(c => c.InitialView, "risks"));

        cut.Find($"[data-birknext-projection-id='{expected}']").GetAttribute("data-birknext-projection-kind").Should().Be("Risk");
    }

    [Fact]
    public void Tasks_renders_the_existing_projection_identity()
    {
        var tree = TaskExplorerService.Parse("# Tasks\n- [ ] T001: Build the package\n");
        var task = Enumerate(tree.Roots).First(node => node.NodeType == TaskNodeType.Task);
        var expected = task.Provenance!.ProjectionId;

        var cut = Render<TaskExplorerPanel>(p => p.Add(c => c.TasksText, "# Tasks\n- [ ] T001: Build the package\n").Add(c => c.ParsedTasks, tree));

        cut.Find($"[data-birknext-projection-id='{expected}']").GetAttribute("data-birknext-projection-kind").Should().Be("Task");
    }

    [Fact]
    public void Data_model_renders_the_existing_projection_identity()
    {
        var document = _dataModel.Parse("# Data Model\n## Entity: Account\n### Fields\n| Field | Type | Nullable |\n| --- | --- | --- |\n| Id | UUID | No |\n");
        var expected = document.Entities.Single().Provenance!.ProjectionId;

        var cut = Render<DataModelExplorerPanel>(p => p.Add(c => c.ParsedDataModel, document).Add(c => c.InitialView, "design"));

        cut.Find($"[data-birknext-projection-id='{expected}']").GetAttribute("data-birknext-projection-kind").Should().Be("Entity");
    }

    private static ProjectionProvenance? FindProvenance(SpecNode node) => node.Provenance ?? node.Children.Select(FindProvenance).FirstOrDefault(p => p is not null);
    private static ProjectionProvenance? FindProvenance(SpecTree tree) => tree.Roots.Select(FindProvenance).FirstOrDefault(p => p is not null);
    private static IEnumerable<TaskNode> Enumerate(IEnumerable<TaskNode> nodes) => nodes.SelectMany(node => new[] { node }.Concat(Enumerate(node.Children)));
}
