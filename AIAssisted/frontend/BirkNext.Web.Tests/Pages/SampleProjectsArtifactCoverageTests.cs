using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.SampleProjects;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using static BirkNext.Web.Tests.Services.SampleArtifactClassifierTests;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Sample Projects card: artifact coverage by role instead of a fixed "Missing expected" filename list.</summary>
public sealed class SampleProjectsArtifactCoverageTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly SampleProjectArtifactDiscoveryTests.FakeBackend _backend = new();
    private readonly SampleProjectArtifactDiscoveryService _discovery;

    public SampleProjectsArtifactCoverageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<bool>("confirm", _ => true).SetResult(true);
        var autoSave = new Mock<IWorkspaceAutoSaveService>();
        autoSave.Setup(x => x.SaveNowAsync()).ReturnsAsync(true);

        var api = _backend.Api();
        _discovery = new SampleProjectArtifactDiscoveryService(api);
        Services.AddSingleton<IWorkspaceArtifactRepository>(_workspace);
        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<IWorkspaceArtifactStatusService>(sp => new WorkspaceArtifactStatusService(sp.GetRequiredService<IWorkspaceSessionService>()));
        Services.AddSingleton<IWorkspaceUpdateCoordinator, WorkspaceUpdateCoordinator>();
        Services.AddSingleton(autoSave.Object);
        Services.AddSingleton(new QualityReviewSessionService());
        Services.AddSingleton(Mock.Of<IDashboardSnapshotService>());
        Services.AddSingleton<ITargetEnvironmentHintExtractor>(new TargetEnvironmentHintExtractor());
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddSingleton(Mock.Of<IIntegrationTargetRegistryService>());
        Services.AddSingleton(NullLogger<SampleProjects>.Instance);
        Services.AddSingleton(api);
        Services.AddSingleton<ISampleProjectArtifactDiscovery>(_discovery);
    }

    [Fact]
    public void NestedSpecKitProject_ShowsDetectedRolesAndNeutralNotFound_NoMissingExpected()
    {
        _backend.Add("skole", new()
        {
            ["Skole/.specify/memory/constitution.md"] = Constitution,
            ["Skole/specs/001-skole/spec.md"] = Spec,
            ["Skole/specs/001-skole/checklists/requirements.md"] = "# Specification Quality Checklist\n\n- [x] a\n- [x] b\n- [x] c\n- [x] d\n- [x] e\n",
        });

        var cut = Render<SampleProjects>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sp-role]").Should().HaveCount(6));

        cut.Markup.Should().NotContain("Missing Expected");
        State(cut, "Constitution").Should().Be("Detected");
        State(cut, "Specification").Should().Be("Detected");
        State(cut, "Plan").Should().Be("Not found");
        State(cut, "Tasks").Should().Be("Not found");
        State(cut, "DataModel").Should().Be("Not found");
        cut.Find("[data-testid=sp-artifact-summary]").TextContent.Should().Be("2 supported artifacts · 1 other document");
        cut.Find("[data-role=Specification]").TextContent.Should().Contain("Skole/specs/001-skole/spec.md").And.Contain("Specification Explorer");
        cut.Find("[data-role=Plan] .sp-state-none").Should().NotBeNull("an absent optional role is neutral, not an error");
        cut.Find("[data-testid=sp-other-documents]").TextContent.Should().Contain("checklists/requirements.md");
        cut.FindAll("[data-testid=sp-read-errors]").Should().BeEmpty();
    }

    [Fact]
    public void MultipleSpecifications_AreListed_AndTheChoiceDrivesTheResolver()
    {
        _backend.Add("multi", new()
        {
            ["specs/person.md"] = Spec.Replace("School attendance", "Person"),
            ["specs/school.md"] = Spec.Replace("School attendance", "School"),
        });
        var resolver = new SampleProjectDocumentResolver(_backend.Api(), _workspace, _discovery);

        var cut = Render<SampleProjects>();
        cut.WaitForAssertion(() => State(cut, "Specification").Should().Be("Multiple — choose one"));
        cut.FindAll("[data-role=Specification] [data-testid=sp-role-doc]").Should().HaveCount(2);

        cut.Find("[data-testid=sp-choose-document]").Change("specs/school.md");

        cut.WaitForAssertion(() => State(cut, "Specification").Should().Be("Multiple"));
        cut.Find("[data-role=Specification]").TextContent.Should().Contain("Opened by explorers");
        var result = resolver.ResolveAsync("multi", ExplorerDocumentType.Specification).GetAwaiter().GetResult();
        result.Filename.Should().Be("specs/school.md");
    }

    [Fact]
    public void AllFiles_ListsPathRoleConfidenceAndReason()
    {
        _backend.Add("files", new() { ["docs/requirements.md"] = Spec, ["README.md"] = "# Files\n\nIntro." }, extraFiles: ["src/App.cs"]);

        var cut = Render<SampleProjects>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sp-role]").Should().HaveCount(6));
        cut.FindAll("button").First(b => b.TextContent.Contains("View All Files")).Click();

        var rows = cut.FindAll("[data-testid=sp-file-row]").Select(r => r.TextContent).ToList();
        rows.Should().HaveCount(3);
        rows.Single(r => r.Contains("docs/requirements.md")).Should().Contain("Specification").And.Contain("Strong").And.Contain("Yes");
        rows.Single(r => r.Contains("README.md")).Should().Contain("Other document");
        rows.Single(r => r.Contains("src/App.cs")).Should().Contain("CS").And.Contain("Not a document type");
    }

    /// <summary>The state text without its decorative (aria-hidden) icon.</summary>
    private static string State(IRenderedComponent<SampleProjects> cut, string role)
    {
        var state = cut.Find($"[data-role={role}] [data-testid=sp-role-state]");
        var icon = state.QuerySelector("[aria-hidden=true]")?.TextContent ?? "";
        return state.TextContent.Trim()[icon.Length..].Trim();
    }
}
