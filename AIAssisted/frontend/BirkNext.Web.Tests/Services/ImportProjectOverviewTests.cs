using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Import Project in tester words: what was imported (documents, source or both), what was found per document type (pack-aware — valid
/// separate packs never "require clarification"), what needs attention (same-pack ambiguity, ambiguous testing sources, no test environment),
/// the testing setup in plain language, and compact history. Technical terms stay behind Technical details.
/// </summary>
public class ImportProjectOverviewTests
{
    private static SampleFileDto File(string path, string kind) => new(path, true, kind, "", "", true, false);

    private static async Task<CurrentWorkspaceSnapshot> SnapshotAsync(params SampleFileDto[] files)
    {
        var handler = new DashboardPageTests.SampleProjectsHttpHandler();
        handler.SetProjects(new SampleProjectDto("proj", "Project", "PROJ", "", "C:/SampleData/proj", true, files));
        var services = new ServiceCollection();
        DashboardPageTests.AddCurrentWorkspace(services, new WorkspaceArtifactRepository { CurrentProject = "proj" }, handler);
        return await services.BuildServiceProvider().GetRequiredService<ICurrentWorkspaceProjection>().GetAsync();
    }

    private static SddProjectImportRecord Record(int documents) => new() { ImportId = "i1", ProjectName = "M2LB", ArtifactDocumentCount = documents, ImportedAt = DateTimeOffset.UtcNow };

    private static ProjectImportCurrentSource Source(bool detected, Guid? snapshot = null) =>
        new(ProjectImportTone.Available, "", "Source detail", snapshot, false, detected);

    // ── What was imported ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(5, true, "Project documents and source code imported")]
    [InlineData(5, false, "Project documents imported · no source code detected")]
    [InlineData(0, true, "Source code imported · no supported project documents found")]
    public void ImportedKind_DocumentsOnly_SourceOnly_AndBoth_AreAllValid(int documents, bool source, string expected) =>
        ImportProjectOverview.ImportedKind(Record(documents), Source(source)).Should().Be(expected);

    [Fact]
    public async Task Summary_UsesRealCounts_AndNoTechnicalMetadata()
    {
        var workspace = await SnapshotAsync(File("Person/specs/001-a/spec.md", "Specification"), File("Person/specs/002-b/spec.md", "Specification"),
            File("Adapter/specs/001-c/spec.md", "Specification"));
        var summary = ImportProjectOverview.Summary(Record(3), workspace, Source(true, Guid.NewGuid()));
        summary.Should().Be("2 modules · 3 feature packs · 3 documents · source code available");
        summary.Should().NotContainAny("Snapshot", "fingerprint", ".zip");
    }

    [Fact]
    public void SourceAnalysisPartial_IsExplainedInPlainLanguage()
    {
        var (status, explanation) = ImportProjectOverview.SourceAnalysis(Source(true, Guid.NewGuid()), "Partial");
        status.Should().Be("Source analysis available with limitations");
        explanation.Should().Contain("could not be assessed");
        ImportProjectOverview.SourceAnalysis(Source(true, Guid.NewGuid()), "Ready").Should().Be(("Source analysis available", (string?)null));
        ImportProjectOverview.SourceAnalysis(Source(false), null).Status.Should().Be("No source code in this project");
    }

    // ── Project contents ──────────────────────────────────────────────────────

    [Fact]
    public async Task ThreeSpecificationsInThreePacks_AreFound_WithoutClarification()
    {
        var workspace = await SnapshotAsync(File("specs/001-a/spec.md", "Specification"), File("specs/002-b/spec.md", "Specification"), File("specs/003-c/spec.md", "Specification"));
        var spec = ImportProjectOverview.Contents(workspace.Overview!).Single(r => r.Role == WorkspaceArtifactType.Specification);
        spec.Found.Should().Be("3 specifications found");
        spec.RequiresClarification.Should().Be(0);
        spec.ActionLabel.Should().Be("Review specifications");
        ImportProjectOverview.Attention(workspace, null, null, environmentSelected: true).Should().BeEmpty();
    }

    private static ExplorerArtifact Artifact(WorkspaceArtifactType role, string path, string? feature) =>
        new($"sample:{path}", role, path, path[(path.LastIndexOf('/') + 1)..], path, ExplorerArtifactSource.SampleProject, null, null, 0, "Unknown",
            ExplorerArtifactCurrentness.Current, null, Feature: feature);

    [Fact]
    public void TwoCompetingPlansInOnePack_NeedAttention()
    {
        // Pack 001-a has two plan documents and neither is its Spec-Kit plan.md: the explorers' own resolution reports it ambiguous.
        var plans = new[] { Artifact(WorkspaceArtifactType.Plan, "specs/001-a/implementation-plan.md", "001-a"), Artifact(WorkspaceArtifactType.Plan, "specs/001-a/delivery-plan.md", "001-a"),
            Artifact(WorkspaceArtifactType.Plan, "specs/002-b/plan.md", "002-b") };
        var specs = new[] { Artifact(WorkspaceArtifactType.Specification, "specs/001-a/spec.md", "001-a"), Artifact(WorkspaceArtifactType.Specification, "specs/002-b/spec.md", "002-b") };
        var overview = new ArtifactModuleOverview("proj", "Project", null, [ArtifactModules.ProjectLevel],
        [
            new(WorkspaceArtifactType.Specification, specs, [new ArtifactModuleRoleState(ArtifactModules.ProjectLevel, 2, null, null, 2, false)]),
            new(WorkspaceArtifactType.Plan, plans, [new ArtifactModuleRoleState(ArtifactModules.ProjectLevel, 3, null, null, 2, AmbiguousInPack: true)]),
        ]);
        var workspace = WorkspaceSnapshots.AllRoles() with { Overview = ProjectPackOverview.Build(overview) };

        var contents = ImportProjectOverview.Contents(workspace.Overview!);
        contents.Single(r => r.Role == WorkspaceArtifactType.Specification).RequiresClarification.Should().Be(0, "one specification per pack is valid");
        var plan = contents.Single(r => r.Role == WorkspaceArtifactType.Plan);
        plan.RequiresClarification.Should().Be(1);
        ImportProjectOverview.Clarification(plan.RequiresClarification).Should().Be("1 requires clarification");
        var item = ImportProjectOverview.Attention(workspace, null, null, true).Should().ContainSingle(i => i.Key == "ambiguous-Plan").Subject;
        item.Text.Should().Be("1 Plan requires clarification");
        item.Detail.Should().Contain("001-a").And.NotContain("002-b");
        item.ActionLabel.Should().Be("Choose file");
    }

    [Fact]
    public async Task SharedConstitution_AndSharedBaseDataModel_AreValidSharedCoverage()
    {
        var workspace = await SnapshotAsync(File(".specify/memory/constitution.md", "Constitution"),
            File("specs/001-a/spec.md", "Specification"), File("specs/002-b/spec.md", "Specification"),
            File("data-model.md", "DataModel"), File("specs/001-a/data-model.md", "DataModel"));
        var contents = ImportProjectOverview.Contents(workspace.Overview!);
        contents.Single(r => r.Role == WorkspaceArtifactType.Constitution).Note.Should().Be("Shared constitutions detected");
        contents.Single(r => r.Role == WorkspaceArtifactType.DataModel).Note.Should().Be("Includes shared/base models");
        contents.Should().OnlyContain(r => r.RequiresClarification == 0, "a shared document and a pack's own layer never compete");
    }

    // ── Testing setup and environment ─────────────────────────────────────────

    private static TestPackSourceCandidate Candidate(string sha) => new(Guid.NewGuid(), $"{sha}.zip", sha, DateTimeOffset.UtcNow);

    [Fact]
    public void AmbiguousSource_IsAttention_WithChooseSource()
    {
        var mu = new TestPackSourceStatus(new TestPackSourceRole("mu", "MU", "MU source"), TestPackSourceState.ReviewRequired)
        { Candidates = [Candidate("aaa"), Candidate("bbb")] };
        var line = ImportProjectOverview.Line(mu);
        line.State.Should().Be("2 possible sources found");
        line.NeedsAttention.Should().BeTrue();
        line.ActionLabel.Should().Be("Choose source");
        var setup = new SourceSetupView { BindingsRead = true, Packs = [new TestPackSourceSetup("demo", "Demo", [mu])] };
        ImportProjectOverview.Attention(null, setup, null, true).Single().Text.Should().Be("MU source requires clarification");
        ImportProjectOverview.SetupHeadline(setup.Packs[0]).Should().Be("1 setup item requires attention");
    }

    [Fact]
    public void FoundSource_BuiltInReference_AndOptionalInfrastructure_AreNotAttention()
    {
        var m2lb = new TestPackSourceStatus(new TestPackSourceRole("m2lb", "M2LB", ""), TestPackSourceState.AvailableNotBound) { Candidates = [Candidate("ccc")] };
        var reference = new TestPackSourceStatus(new TestPackSourceRole("altinn", "Altinn", "") { Provider = TestPackSourceProvider.TestPackReference }, TestPackSourceState.ReferenceAvailable);
        var iac = new TestPackSourceStatus(new TestPackSourceRole("iac", "Infrastructure", "") { Requirement = TestPackSourceRequirement.Optional, RequiresInfrastructureAsCode = true },
            TestPackSourceState.Available) { Candidates = [Candidate("ddd")] };

        ImportProjectOverview.Line(m2lb).Should().Match<ImportSetupLine>(l => l.State == "Found" && !l.NeedsAttention, "available without a binding is not incomplete");
        ImportProjectOverview.Line(reference).State.Should().Be("Built-in test reference available");
        ImportProjectOverview.Line(iac).Should().Match<ImportSetupLine>(l => l.State == "Infrastructure information available" && l.Optional && !l.NeedsAttention);
        var setup = new SourceSetupView { Packs = [new TestPackSourceSetup("demo", "Demo", [m2lb, reference, iac])] };
        ImportProjectOverview.Attention(null, setup, null, true).Should().BeEmpty();
    }

    [Fact]
    public void TestEnvironment_NotSelected_IsANote_AndSelected_StopsAsking()
    {
        ImportProjectOverview.TestEnvironment(null).Should().Be("Test environment: Not selected");
        ImportProjectOverview.TestEnvironment("Development").Should().Be("Test environment: Development");
        ImportProjectOverview.Attention(null, null, null, environmentSelected: false).Single()
            .Should().Match<ImportAttentionItem>(i => i.Key == "test-environment" && i.Informational && i.ActionLabel == "Select test environment");
        ImportProjectOverview.Attention(null, null, null, environmentSelected: true).Should().BeEmpty();
    }

    // ── Previously imported projects ──────────────────────────────────────────

    [Fact]
    public void PreviousImport_IsSummarizedCompactly()
    {
        PreviousImportRow Row(bool source, string documents) => new("k", PreviousImportOrigin.ProjectImport, "i", "M2LB.Common", "M2LB.Common.zip", "f",
            new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero), source ? Guid.NewGuid() : null, "Ready", documents, source, [], [], []);
        ImportProjectOverview.Contents(Row(true, "2 documents")).Should().Be("Source + 2 documents");
        ImportProjectOverview.Contents(Row(true, "No project documents")).Should().Be("Source only");
        ImportProjectOverview.Contents(Row(false, "4 documents · 1 role(s) to choose")).Should().Be("4 documents");
        ImportProjectOverview.Imported(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero)).Should().Be("Imported 24 Oct 2026");
    }
}
