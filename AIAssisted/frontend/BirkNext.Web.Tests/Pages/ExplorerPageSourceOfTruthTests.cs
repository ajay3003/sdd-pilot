using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

public sealed class ExplorerPageSourceOfTruthTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();

    public ExplorerPageSourceOfTruthTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<MarkdownRenderingService>();
        Services.AddSingleton<IConstitutionAnalysisService, ConstitutionAnalysisService>();
        Services.AddSingleton<IPlanAnalysisService, PlanAnalysisService>();
        Services.AddSingleton<IDataModelAnalysisService, DataModelAnalysisService>();
        Services.AddSingleton<IReportExportService, ReportExportService>();

        JSInterop.SetupVoid("localStorage.setItem", _ => true).SetVoidResult();
        JSInterop.SetupVoid("localStorage.removeItem", _ => true).SetVoidResult();
        JSInterop.SetupVoid("fileImport.initDropZone", _ => true).SetVoidResult();
    }

    [Fact]
    public void ConstitutionExplorer_UsesWorkspaceBeforeStandaloneStorage()
    {
        _workspace.CurrentProject = "Project B";
        _workspace.Set(WorkspaceArtifactKind.Constitution, "# Workspace Constitution");
        SetStandaloneScratch("ce-standalone-constitution", "# Standalone Constitution");

        var cut = Render<ConstitutionExplorer>();

        _workspace.Get(WorkspaceArtifactKind.Constitution)!.Text.Should().Be("# Workspace Constitution");
        JSInterop.Invocations
            .Where(invocation => invocation.Identifier == "localStorage.getItem")
            .Select(invocation => invocation.Arguments.Count == 1 ? invocation.Arguments[0]?.ToString() : null)
            .Should()
            .NotContain("ce-standalone-constitution");
    }

    [Fact]
    public void ConstitutionExplorer_UsesStandaloneScratchWhenWorkspaceIsEmpty()
    {
        SetStandaloneScratch("ce-standalone-constitution", "# Standalone Constitution");

        var cut = Render<ConstitutionExplorer>();

        cut.WaitForAssertion(() =>
            _workspace.Get(WorkspaceArtifactKind.Constitution)!.Text.Should().Be("# Standalone Constitution"));
    }

    [Fact]
    public void ConstitutionExplorer_StaysEmptyWhenWorkspaceAndStandaloneScratchAreEmpty()
    {
        var cut = Render<ConstitutionExplorer>();

        cut.WaitForAssertion(() =>
            _workspace.Get(WorkspaceArtifactKind.Constitution).Should().BeNull());
    }

    [Fact]
    public void PlanExplorer_DoesNotRequireProjectDocumentApiFallback()
    {
        var cut = Render<PlanExplorer>();

        cut.WaitForAssertion(() =>
            _workspace.Get(WorkspaceArtifactKind.Plan).Should().BeNull());
    }

    [Fact]
    public void TaskExplorer_DoesNotRequireProjectDocumentApiFallback()
    {
        var cut = Render<TaskExplorer>();

        cut.WaitForAssertion(() =>
            _workspace.Get(WorkspaceArtifactKind.Tasks).Should().BeNull());
    }

    [Fact]
    public void DataModelExplorer_DoesNotRequireProjectDocumentApiFallback()
    {
        var cut = Render<DataModelExplorer>();

        cut.WaitForAssertion(() =>
            _workspace.Get(WorkspaceArtifactKind.DataModel).Should().BeNull());
    }

    [Theory]
    [InlineData("pe-standalone-plan", WorkspaceArtifactKind.Plan, "# Standalone Plan")]
    [InlineData("te-standalone-tasks", WorkspaceArtifactKind.Tasks, "# Standalone Tasks")]
    [InlineData("dme-standalone-datamodel", WorkspaceArtifactKind.DataModel, "# Standalone Data Model")]
    public void ExplorerPages_UseStandaloneScratchWhenWorkspaceIsEmpty(
        string storageKey,
        WorkspaceArtifactKind kind,
        string text)
    {
        SetStandaloneScratch(storageKey, text);

        switch (kind)
        {
            case WorkspaceArtifactKind.Plan:
                Render<PlanExplorer>().WaitForAssertion(() =>
                    _workspace.Get(kind)!.Text.Should().Be(text));
                break;
            case WorkspaceArtifactKind.Tasks:
                Render<TaskExplorer>().WaitForAssertion(() =>
                    _workspace.Get(kind)!.Text.Should().Be(text));
                break;
            case WorkspaceArtifactKind.DataModel:
                Render<DataModelExplorer>().WaitForAssertion(() =>
                    _workspace.Get(kind)!.Text.Should().Be(text));
                break;
            default:
                throw new InvalidOperationException($"Unsupported kind {kind}");
        }
    }

    private void SetStandaloneScratch(string storageKey, string text)
    {
        JSInterop.Setup<string?>("localStorage.getItem", invocation =>
            invocation.Arguments.Count == 1
            && string.Equals(invocation.Arguments[0]?.ToString(), storageKey, StringComparison.Ordinal))
            .SetResult(text);
    }
}
