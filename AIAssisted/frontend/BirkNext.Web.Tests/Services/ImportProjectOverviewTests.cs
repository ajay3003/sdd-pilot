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

    // ── Source Analysis status (separate from the import) ─────────────────────

    private static SourceSetupSnapshot Snap(string status, params (string Area, string Status)[] areas) =>
        new(Guid.NewGuid(), "x.zip", "f", DateTimeOffset.UtcNow, status) { Areas = [.. areas.Select(a => new SourceSetupArea(a.Area, a.Status))] };

    /// <summary>The real Terraform-iac-meldingsutveksler shape: code analysis Failed (no projects), the evidence domains analyzed.</summary>
    private static SourceSetupSnapshot TerraformOnly() => Snap("Failed", ("Application code", "NotDetected"), ("Architecture", "Unsupported"),
        ("Infrastructure as Code", "Complete"), ("Configuration", "Complete"), ("CI/CD", "Partial"), ("Contracts", "Partial"));

    [Fact]
    public void ImportSucceeds_EvenWithoutASourceAnalysis_AndTheTwoStatusesStaySeparate()
    {
        var notCreated = ImportProjectOverview.SourceAnalysis(sourceDetected: true, snapshotId: null, snapshot: null, "The source snapshot could not be saved.", canRetry: true);
        notCreated.Should().Match<ImportSourceAnalysis>(a => a.State == ImportAnalysisState.NotCreated && a.Status == "Not created" && a.Tone == ProjectImportTone.Partial);
        notCreated.Explanation.Should().Contain("The import succeeded");
        ImportProjectOverview.SourceAnalysis(false, null, null).Should().Match<ImportSourceAnalysis>(a => a.State == ImportAnalysisState.NoSource && a.Status == "No source code");
    }

    [Fact]
    public void FailedCodeAnalysis_WithSurvivingInfrastructureEvidence_IsPartialWithNoApplicationCode_NotFailed()
    {
        var analysis = ImportProjectOverview.SourceAnalysis(true, Guid.NewGuid(), TerraformOnly());

        analysis.State.Should().Be(ImportAnalysisState.NoApplicationCode);
        analysis.Status.Should().Be("Partial · no application code", "the backend Failed is the code analyzer's outcome only");
        analysis.Tone.Should().Be(ProjectImportTone.Partial);
        analysis.Explanation.Should().Contain("No supported production project");
        analysis.Areas.Should().Be("Infrastructure as Code, Configuration complete · CI/CD, Contracts partial · Architecture not supported · no supported application code");
        analysis.HasDetails.Should().BeTrue();
    }

    [Fact]
    public void FailedAnalysis_WithNoUsableArea_StaysFailed()
    {
        var analysis = ImportProjectOverview.SourceAnalysis(true, Guid.NewGuid(), Snap("Failed", ("Application code", "NotDetected"), ("Architecture", "Unsupported"),
            ("Infrastructure as Code", "NotDetected"), ("Configuration", "NotDetected"), ("CI/CD", "Failed"), ("Contracts", "NotDetected")));

        analysis.Should().Match<ImportSourceAnalysis>(a => a.State == ImportAnalysisState.Failed && a.Status == "Failed" && a.Tone == ProjectImportTone.Rejected,
            "Partial is never used to hide a source nothing could be read from");
        analysis.Areas.Should().Contain("CI/CD failed");
    }

    [Fact]
    public void ReadyAndPartial_KeepTheSnapshotsOwnStatus_WithAreaOutcomes()
    {
        ImportProjectOverview.SourceAnalysis(true, Guid.NewGuid(), Snap("Ready", ("Application code", "Complete"), ("Architecture", "Complete")))
            .Should().Match<ImportSourceAnalysis>(a => a.State == ImportAnalysisState.Ready && a.Status == "Ready" && !a.HasDetails && a.Areas == "Application code complete · Architecture complete");
        var partial = ImportProjectOverview.SourceAnalysis(true, Guid.NewGuid(), Snap("Partial", ("Application code", "Partial"), ("Architecture", "Complete"), ("Contracts", "NotDetected")));
        partial.Should().Match<ImportSourceAnalysis>(a => a.State == ImportAnalysisState.Partial && a.Status == "Partial" && a.HasDetails);
        partial.Areas.Should().Be("Application code partial · Architecture complete", "an area not detected in the source says nothing and is omitted");
        // An older backend sends no areas: the snapshot's own wording, never a guess.
        ImportProjectOverview.SourceAnalysis(true, Guid.NewGuid(), Snap("Failed")).Status.Should().Be("No production project found");
    }

    // ── Source role ───────────────────────────────────────────────────────────

    private static TestPackSourceRole Role(string key, string label) => new(key, label, "") { ExpectedArtifact = $"{label} repository source archive" };

    [Fact]
    public void SourceRole_IsRecognizedFromContents_OrExplainedWithTheRecognizersOwnReason()
    {
        var mu = Guid.NewGuid(); var common = Guid.NewGuid(); var terraform = Guid.NewGuid();
        var setup = new SourceSetupView
        {
            Packs = [new TestPackSourceSetup("skolenaervaer", "Skolenærvær",
            [
                new(Role("mu", "MU (Meldingsutveksleren)"), TestPackSourceState.AvailableNotBound)
                    { Candidates = [new(mu, "Tiltak 2.zip", "a", DateTimeOffset.UtcNow) { MatchedOn = ["Project: Buf.Meldingsutveksler.Common"] }] },
                new(Role("m2lb", "M2LB application"), TestPackSourceState.Missing)
                {
                    NearMisses = [new(common, "M2LB.Common.zip", "It contains none of the expected components or projects: Meldingsutvekslermottak, Skole."),
                        new(terraform, "Terraform-iac-m2lb.zip", "It contains none of the expected components or projects: Meldingsutvekslermottak, Skole.")],
                },
                new(Role("infrastructure", "Infrastructure as Code") with { RequiresInfrastructureAsCode = true, Requirement = TestPackSourceRequirement.Optional }, TestPackSourceState.Available)
                    { Candidates = [new(terraform, "Terraform-iac-m2lb.zip", "t", DateTimeOffset.UtcNow) { MatchedOn = ["Infrastructure as Code: Terraform"] }] },
            ])],
        };

        ImportProjectOverview.SourceRole(setup, mu, true).Should().Match<ImportSourceRole>(r => r.Identified && r.Status == "MU (Meldingsutveksleren)"
            && r.Detail!.Contains("Buf.Meldingsutveksler.Common"));
        // The M2LB-named IaC repository is Infrastructure as Code only: its name never makes it the M2LB application source.
        ImportProjectOverview.SourceRole(setup, terraform, true)!.Status.Should().Be("Infrastructure as Code");
        // A shared library is genuinely not the application source: Not identified, with what was checked.
        ImportProjectOverview.SourceRole(setup, common, true).Should().Match<ImportSourceRole>(r => !r.Identified && r.Status == "Not identified"
            && r.Detail!.StartsWith("Checked as M2LB application: It contains none of the expected components"));
        ImportProjectOverview.SourceRole(setup, Guid.NewGuid(), true)!.Detail.Should().Contain("matches no source requirement of Skolenærvær");
        ImportProjectOverview.SourceRole(setup, null, false)!.Status.Should().Be("No source");
        ImportProjectOverview.SourceRole(new SourceSetupView(), mu, true).Should().BeNull("without a testing setup a source role means nothing");
    }

    [Fact]
    public void Attention_IsGroupedByOwner_AndNamesTheExactMissingRole()
    {
        var missing = new TestPackSourceStatus(Role("m2lb", "M2LB application"), TestPackSourceState.Missing)
            { NearMisses = [new(Guid.NewGuid(), "M2LB.Common.zip", "It contains none of the expected components or projects.")] };
        var setup = new SourceSetupView { Packs = [new TestPackSourceSetup("skolenaervaer", "Skolenærvær", [missing])] };

        var items = ImportProjectOverview.Attention(null, setup, Source(true), environmentSelected: false);

        var source = items.Single(i => i.Key == "setup-skolenaervaer-m2lb");
        source.Text.Should().Be("M2LB application source missing", "never the bare \"M2LB: Not found\"");
        source.Detail.Should().Contain("M2LB.Common.zip was checked but does not match").And.Contain("Expected: M2LB application repository source archive");
        source.Group.Should().Be(ImportAttentionGroup.TestingSetup);
        items.Single(i => i.Key == "source-analysis").Group.Should().Be(ImportAttentionGroup.SourceAnalysis);
        items.Single(i => i.Key == "test-environment").Should().Match<ImportAttentionItem>(i => i.Group == ImportAttentionGroup.Environment && i.Informational);
        ImportProjectOverview.Grouped(items).Select(g => g.Title).Should().Equal("Testing setup", "Source analysis", "Environment");
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
