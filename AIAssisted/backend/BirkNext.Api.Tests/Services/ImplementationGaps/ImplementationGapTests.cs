using BirkNext.Api.Data;
using BirkNext.Api.Services.ImplementationGaps;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ManualReview;
using BirkNext.Api.Services.ProjectImport;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Tests.ProjectImport;
using BirkNext.ImplementationGaps;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BirkNext.Api.Tests.Services.ImplementationGaps;

/// <summary>
/// Implementation-gap detection end to end through the real Project Import → Source Analysis pipeline (synthetic archives, real analyzers): a
/// positive control that is Verified, and negative controls that each get a different, correct verdict — partial, ambiguous, missing,
/// declared scope absent from the archive, unapproved/pending/unknown decision gates, moved file, renamed type, partial type, interface,
/// registration-only change, unchecked-but-implemented, deferred task, no snapshot, truncated inventory. Review decisions survive re-evaluation
/// and never leak to another import.
/// </summary>
public sealed class ImplementationGapTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly ProjectImportStagingStore _staging = new();

    public void Dispose() => _db.Dispose();

    private ImplementationGapService Gaps() => new(_db, new IqrSourceStore(_db), new ManualReviewDecisionStore(_db));

    private async Task<GapEvaluation> Import(Dictionary<string, string> files, bool sourceAnalysis = true, string name = "demo.zip")
    {
        var sources = new Mock<IReviewSourceEvidenceProvider>();
        sources.SetupGet(s => s.SourceAnalysisEnabled).Returns(sourceAnalysis);
        var service = new ProjectImportService(new IqrSourceStore(_db), _staging, sources.Object, NullLogger<ProjectImportService>.Instance, gaps: Gaps());
        var preview = service.Preview(name, ProjectImportServiceTests.Zip([.. files.Select(f => (f.Key, f.Value))])).Preview!;
        var commit = (await service.CommitAsync(preview.StagingId))!;
        commit.ImplementationGapEvaluationId.Should().NotBeNull("an archive with tasks.md is evaluated at import");
        return (await Gaps().GetAsync(commit.ImplementationGapEvaluationId!.Value, default))!;
    }

    // ── Synthetic project ────────────────────────────────────────────────────────────────────────────────────────────────────

    private const string Tasks = """
        # Tasks: Partner delivery

        ## External gates (not tasks; they block where marked)

        | Gate | Blocks | Owner |
        |---|---|---|
        | ADR-031 Exporter library sign-off (T010 drafts it) | T020–T023 | Tech lead |
        | G-1 Constitution approval | Only work after a constitution change | SA |

        ## Phase 1: Setup

        - [x] T001 Create `demo/src/Demo.Infra/Demo.Infra.csproj` referencing nothing else
        - [x] T010 [P] Draft ADR-031 "Exporter library for the partner delivery" in `docs/adr.md`

        ## Phase 3: User Story 1 - Delivery (Priority: P1)

        - [x] T020 [P] [US1] Unit tests in `demo/tests/Demo.Unit/ExporterTests.cs` (after T021, covers T022–T023):
          - Output bytes are deterministic.
        - [x] T021 [US1] **Gate: ADR-031 signed off.** Add `Acme.Parquet`, pinned exactly, to `demo/src/Demo.Infra/Demo.Infra.csproj`, and expose its version for `delivery.library_version`
        - [x] T022 [US1] Implement `DatasetExporter` and `CoverageExporter`, deterministic per `contracts/delivery.md`, in `demo/src/Demo.Infra/Export/Exporters.cs` (FR-010)
        - [x] T023 [US1] Implement `ManifestWriter`, which writes `_manifest.json` with `period`, in `demo/src/Demo.Infra/Export/ManifestWriter.cs` (FR-011)

        ## Phase 9: Deferred (future milestone)

        - [ ] T090 [US1] Implement `ArchiveSweeper` in `demo/src/Demo.Infra/Jobs/ArchiveSweeper.cs`
        """;

    private static string Register(string? adr031Status) =>
        "# Architecture decisions\n\n| ADR | Tittel | Status | Dato |\n|---|---|---|---|\n| [ADR-030](#adr-030) | Earlier decision | Gjeldende | Mars 2026 |\n\n"
        + "ADR-030\n-------\n\n**Tittel:** Earlier decision\n**Status:** Gjeldende\n"
        + (adr031Status is null ? "" : $"\nADR-031\n-------\n\n**Tittel:** Exporter library for the partner delivery\n**Status:** {adr031Status}\n");

    private static Dictionary<string, string> Project(string? adr031 = "Gjeldende", bool register = true)
    {
        var files = new Dictionary<string, string>
        {
            ["demo/specs/001-delivery/spec.md"] = "# Feature Specification: Delivery\n\n## Requirements\n\n- **FR-010**: System MUST export the dataset.\n- **FR-011**: System MUST write a manifest.\n",
            ["demo/specs/001-delivery/plan.md"] = "# Implementation Plan: Delivery\n\n## Technical Context\n\n**Language/Version**: C# 12\n",
            ["demo/specs/001-delivery/tasks.md"] = Tasks,
            ["demo/specs/001-delivery/contracts/delivery.md"] = "# Contract: Delivery\n\nDataset (FR-010) and manifest written last (FR-011).\n",
            ["demo/src/Demo.Infra/Demo.Infra.csproj"] = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Acme.Parquet" Version="1.2.3" /></ItemGroup></Project>""",
            ["demo/src/Demo.Infra/Export/Exporters.cs"] = "namespace Demo.Infra.Export;\npublic sealed class DatasetExporter { public byte[] Write() => []; }\npublic sealed class CoverageExporter { public byte[] Write() => []; }\n",
            ["demo/src/Demo.Infra/Export/ManifestWriter.cs"] = "namespace Demo.Infra.Export;\npublic sealed class ManifestWriter { public string Write() => \"{}\"; }\n",
            ["demo/tests/Demo.Unit/Demo.Unit.csproj"] = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="xunit" Version="2.9.0" /></ItemGroup></Project>""",
            ["demo/tests/Demo.Unit/ExporterTests.cs"] = "using Xunit;\nnamespace Demo.Unit;\npublic sealed class ExporterTests\n{\n    [Fact]\n    public void Writes_deterministic_bytes() { }\n\n    [Fact]\n    public void Manifest_is_written_last() { }\n}\n",
        };
        if (register) files["docs/adr.md"] = Register(adr031);
        return files;
    }

    private static GapFeatureAssessment GateFeature(GapEvaluation e) => e.Features.Single(f => f.FeatureId.StartsWith("gate:", StringComparison.Ordinal) && f.GateIds.Contains("ADR-031"));
    private static GapTaskAssessment Task(GapEvaluation e, string id) => e.Tasks.Single(t => t.TaskId == id);

    private static readonly GapExecutedTest[] Passed =
    [
        new("Demo.Unit.ExporterTests.Writes_deterministic_bytes", GapExecutionOutcome.Passed, "synthetic TRX"),
        new("Demo.Unit.ExporterTests.Manifest_is_written_last", GapExecutionOutcome.Passed, "synthetic TRX"),
    ];

    // ── Positive control ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Positive_control_is_implemented_then_verified_only_after_recorded_passing_executions()
    {
        var e = await Import(Project());
        e.SnapshotId.Should().NotBeNull();
        e.Gates.Single(g => g.GateId == "ADR-031").Should().Match<GapGateAssessment>(g => g.Status == GapGateStatus.Approved && g.Evidence.Any(r => r.Path == "docs/adr.md" && r.Line != null));
        foreach (var id in new[] { "T021", "T022", "T023" })
            Task(e, id).Implementation.Should().Be(GapImplementationEvidence.ConfirmedSourceImplementation, id);
        Task(e, "T020").Verification.Should().Be(GapVerificationEvidence.TestDefined, "a test definition is not an execution");
        GateFeature(e).Readiness.Should().Be(GapReadiness.ImplementedButNotVerified);
        e.Findings.Should().Contain(f => f.Type == GapFindingType.TestNotExecuted);
        e.Findings.Should().NotContain(f => new[] { GapFindingType.TaskFileMissing, GapFindingType.TaskTypeMissing, GapFindingType.PackageMissing, GapFindingType.CompletedTaskWithoutImplementation, GapFindingType.DecisionNotApproved, GapFindingType.BlockedByGate, GapFindingType.ImplementationScopeNotInArchive }.Contains(f.Type));

        var verified = (await Gaps().ReevaluateAsync(e.Id, new GapReevaluateRequest { Executions = [.. Passed] }, default))!;
        Task(verified, "T020").Verification.Should().Be(GapVerificationEvidence.TestPassed);
        Task(verified, "T022").Verification.Should().Be(GapVerificationEvidence.TestPassed, "T020 names T022 explicitly");
        GateFeature(verified).Readiness.Should().Be(GapReadiness.Verified);
        GateFeature(verified).RuntimeExecution.Should().StartWith("Not assessed", "no runtime observation was recorded — never a runtime PASS");
        verified.ExecutionSources.Should().Equal("synthetic TRX");
        GateFeature(verified).Dimensions.Single(d => d.Name == "Source implementation coverage").Should().Match<GapDimension>(d => d.Numerator == 3 && d.Denominator == 3);
        GateFeature(verified).RequirementIds.Should().Equal("FR-010", "FR-011");
    }

    [Fact]
    public async Task A_package_is_found_in_its_project_whatever_the_archive_is_called()
    {
        // GAP-DEF-10 (API acceptance): dependency evidence names its repository after the archive ("control-positive") with paths relative to the
        // detected root ("src/…" under "demo/"); matching only "{repository}/{path}" sent the package to "another project".
        var e = await Import(Project(), name: "control-positive.zip");
        Task(e, "T021").Artifacts.Single(c => c.Kind == GapArtifactKind.Package).Relationship.Should().Be(GapRelationship.Confirmed);
        GateFeature(e).Readiness.Should().Be(GapReadiness.ImplementedButNotVerified);
    }

    [Fact]
    public async Task A_failed_execution_is_executed_not_passed()
    {
        var e = await Import(Project());
        var r = (await Gaps().ReevaluateAsync(e.Id, new GapReevaluateRequest { Executions = [Passed[0], Passed[1] with { Outcome = GapExecutionOutcome.Failed }] }, default))!;
        Task(r, "T020").Verification.Should().Be(GapVerificationEvidence.TestExecuted);
        GateFeature(r).Readiness.Should().Be(GapReadiness.ImplementedButNotVerified);
    }

    // ── Negative controls: each a different verdict ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Partial_implementation_and_a_task_checked_complete_without_code()
    {
        var files = Project();
        files.Remove("demo/src/Demo.Infra/Export/ManifestWriter.cs");
        var e = await Import(files);
        Task(e, "T023").Should().Match<GapTaskAssessment>(t => t.TaskState == GapTaskState.Completed && t.Implementation == GapImplementationEvidence.NoMatchingSource && !t.ScopeAbsentFromArchive);
        GateFeature(e).Readiness.Should().Be(GapReadiness.PartiallyImplemented);
        e.Findings.Should().Contain(f => f.Type == GapFindingType.CompletedTaskWithoutImplementation && f.TaskId == "T023" && f.Severity == GapFindingSeverity.High);
        e.Findings.Should().Contain(f => f.Type == GapFindingType.TaskTypeMissing && f.TaskId == "T023" && f.Expected == "ManifestWriter");
    }

    [Fact]
    public async Task Ambiguous_implementation_needs_human_review()
    {
        var files = Project();
        files.Remove("demo/src/Demo.Infra/Export/ManifestWriter.cs");
        files["demo/src/Demo.Infra/A/Writers.cs"] = "namespace Demo.Infra.A;\npublic sealed class ManifestWriter { }\n";
        files["demo/src/Demo.Infra/B/Writers.cs"] = "namespace Demo.Infra.B;\npublic sealed class ManifestWriter { }\n";
        var e = await Import(files);
        Task(e, "T023").Implementation.Should().Be(GapImplementationEvidence.CandidateImplementation);
        e.Findings.Single(f => f.Type == GapFindingType.MultipleCandidates && f.TaskId == "T023").Should().Match<GapFinding>(f => f.NeedsHumanReview && f.ActualEvidence.Count == 2);
        GateFeature(e).Readiness.Should().Be(GapReadiness.PartiallyImplemented);
    }

    [Fact]
    public async Task Declared_scope_absent_from_the_archive_is_not_evidenced_there_never_proven_missing()
    {
        var files = Project().Where(f => !f.Key.StartsWith("demo/src/", StringComparison.Ordinal) && !f.Key.StartsWith("demo/tests/", StringComparison.Ordinal)).ToDictionary();
        files["other/src/Other.Api/Other.Api.csproj"] = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Azure.Storage.Blobs" Version="12.0.0" /></ItemGroup></Project>""";
        files["other/src/Other.Api/Checkpoints.cs"] = "namespace Other.Api;\npublic sealed class Checkpoints { object Container = new object(); }\n";
        var e = await Import(files);
        foreach (var id in new[] { "T021", "T022", "T023" })
            Task(e, id).Should().Match<GapTaskAssessment>(t => t.Implementation == GapImplementationEvidence.NoMatchingSource && t.ScopeAbsentFromArchive && t.Explanation.Contains("not evidenced in supplied archive"));
        e.Findings.Should().ContainSingle(f => f.Type == GapFindingType.ImplementationScopeNotInArchive);
        e.Findings.Should().NotContain(f => new[] { GapFindingType.TaskFileMissing, GapFindingType.TaskTypeMissing, GapFindingType.PackageMissing, GapFindingType.CompletedTaskWithoutImplementation }.Contains(f.Type),
            "the scope finding states the narrower conclusion once; no per-task 'missing' claims");
        GateFeature(e).Readiness.Should().Be(GapReadiness.NotImplemented);
        e.Findings.Should().Contain(f => f.Type == GapFindingType.IntegrationNotImplemented && f.Title.EndsWith("implementation not evidenced in supplied archive", StringComparison.Ordinal));
        Task(e, "T020").Verification.Should().Be(GapVerificationEvidence.TestNotFound);
    }

    [Fact]
    public async Task An_unapproved_mandatory_decision_blocks_even_a_complete_implementation()
    {
        var e = await Import(Project(adr031: "Foreslått"));
        e.Gates.Single(g => g.GateId == "ADR-031").Status.Should().Be(GapGateStatus.Pending);
        Task(e, "T022").Implementation.Should().Be(GapImplementationEvidence.ConfirmedSourceImplementation, "the code is there; the gate does not hide it");
        GateFeature(e).Readiness.Should().Be(GapReadiness.Blocked);
        GateFeature(e).Explanation.Should().Contain("ADR-031").And.Contain("Pending");
        e.Findings.Should().Contain(f => f.Type == GapFindingType.DecisionNotApproved && f.GateId == "ADR-031");
        e.Findings.Should().Contain(f => f.Type == GapFindingType.BlockedByGate && f.Title.StartsWith("T020–T023", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_decision_absent_from_the_register_is_pending_and_without_any_register_unknown()
    {
        var absent = await Import(Project(adr031: null));
        var gate = absent.Gates.Single(g => g.GateId == "ADR-031");
        gate.Status.Should().Be(GapGateStatus.Pending);
        gate.Reason.Should().Contain("no entry").And.Contain("ADR-030");
        Task(absent, "T010").Implementation.Should().Be(GapImplementationEvidence.NoMatchingSource, "the drafting task's decision entry is not in the register");
        Task(absent, "T010").Verification.Should().Be(GapVerificationEvidence.NotApplicable, "a decision record is not verified by tests (independent oracle, Skole T014)");

        var none = await Import(Project(register: false), name: "demo-noregister.zip");
        none.Gates.Single(g => g.GateId == "ADR-031").Status.Should().Be(GapGateStatus.Unknown);
        none.Gates.Single(g => g.Label.StartsWith("G-1", StringComparison.Ordinal)).Status.Should().Be(GapGateStatus.Unknown, "a gate without a decision record is never approved");
    }

    [Fact]
    public async Task Moved_file_renamed_type_partial_type_and_unchecked_tasks_are_candidates_not_missing()
    {
        var files = Project();
        files.Remove("demo/src/Demo.Infra/Export/Exporters.cs");
        files["demo/src/Demo.Infra/Exporting/Exporters.cs"] = "namespace Demo.Infra.Export;\npublic sealed class DatasetExporter { }\npublic sealed partial class CoverageExporter { }\n";
        files["demo/src/Demo.Infra/Exporting/CoverageExporter.Part2.cs"] = "namespace Demo.Infra.Export;\npublic sealed partial class CoverageExporter { }\n";
        files["demo/src/Demo.Infra/Export/ManifestWriter.cs"] = "namespace Demo.Infra.Export;\npublic sealed class PeriodManifestWriter { }\n";
        files["demo/specs/001-delivery/tasks.md"] = Tasks.Replace("- [x] T023", "- [ ] T023", StringComparison.Ordinal);
        var e = await Import(files);
        Task(e, "T022").Artifacts.Should().Contain(c => c.Kind == GapArtifactKind.File && c.Relationship == GapRelationship.Candidate && c.Rule == "same-file-name");
        Task(e, "T022").Artifacts.Where(c => c.Kind == GapArtifactKind.Type).Should().OnlyContain(c => c.Relationship == GapRelationship.Candidate, "declared, but not in the file the task names");
        Task(e, "T022").Artifacts.Single(c => c.Expected == "CoverageExporter").Actual.Should().HaveCount(2, "one partial type in two files is one candidate, not two");
        Task(e, "T022").Implementation.Should().Be(GapImplementationEvidence.CandidateImplementation);
        Task(e, "T023").Artifacts.Single(c => c.Kind == GapArtifactKind.Type).Should().Match<GapArtifactCheck>(c => c.Rule == "renamed-type-candidate" && c.Detail.Contains("PeriodManifestWriter"));
        e.Findings.Should().Contain(f => f.Type == GapFindingType.ArtifactMovedOrRenamed && f.NeedsHumanReview);
        e.Findings.Should().NotContain(f => f.Type == GapFindingType.TaskTypeMissing || f.Type == GapFindingType.CompletedTaskWithoutImplementation);
    }

    [Fact]
    public async Task Unchecked_task_with_its_implementation_present_is_flagged_for_review()
    {
        var files = Project();
        files["demo/specs/001-delivery/tasks.md"] = Tasks.Replace("- [x] T023", "- [ ] T023", StringComparison.Ordinal);
        var e = await Import(files);
        Task(e, "T023").Should().Match<GapTaskAssessment>(t => t.TaskState == GapTaskState.NotStarted && t.Implementation == GapImplementationEvidence.ConfirmedSourceImplementation);
        e.Findings.Single(f => f.Type == GapFindingType.OpenTaskWithImplementation).Should().Match<GapFinding>(f => f.TaskId == "T023" && f.NeedsHumanReview);
    }

    [Fact]
    public async Task Interface_implementation_and_a_registration_only_change()
    {
        var files = Project();
        files["demo/specs/001-delivery/tasks.md"] = Tasks.Replace("## Phase 9", """
            - [x] T024 [US1] Implement `IExporter` in `demo/src/Demo.Infra/Export/IExporter.cs`
            - [x] T025 [US1] Register `DatasetExporter` in `demo/src/Demo.Infra/Export/Exporters.cs`

            ## Phase 9
            """, StringComparison.Ordinal);
        files["demo/src/Demo.Infra/Export/IExporter.cs"] = "namespace Demo.Infra.Export;\npublic interface IExporter { byte[] Write(); }\n";
        var e = await Import(files);
        Task(e, "T024").Implementation.Should().Be(GapImplementationEvidence.ConfirmedSourceImplementation);
        Task(e, "T025").Implementation.Should().Be(GapImplementationEvidence.CandidateImplementation, "the file exists, but a registration inside it cannot be verified from the inventory");
    }

    [Fact]
    public async Task Deferred_tasks_are_excluded_from_readiness_and_findings()
    {
        var e = await Import(Project());
        Task(e, "T090").NotApplicableReason.Should().Contain("Deferred");
        e.Findings.Should().NotContain(f => f.TaskId == "T090");
        e.Features.Single(f => f.FeatureId.StartsWith("story:", StringComparison.Ordinal)).Dimensions.Single(d => d.Name == "Task coverage").Excluded.Should().Be(1);
    }

    [Fact]
    public async Task Without_a_source_snapshot_everything_is_not_assessed_and_nothing_is_called_missing()
    {
        var e = await Import(Project(), sourceAnalysis: false);
        e.SnapshotId.Should().BeNull();
        e.Tasks.Should().OnlyContain(t => t.Implementation == GapImplementationEvidence.NotAssessed && t.Verification == GapVerificationEvidence.NotAssessed);
        e.Features.Should().OnlyContain(f => f.Readiness == GapReadiness.NotAssessed);
        e.Findings.Should().NotContain(f => new[] { GapFindingType.TaskFileMissing, GapFindingType.TaskTypeMissing, GapFindingType.CompletedTaskWithoutImplementation, GapFindingType.ImplementationScopeNotInArchive }.Contains(f.Type));
        e.Gates.Single(g => g.GateId == "ADR-031").Status.Should().Be(GapGateStatus.Approved, "gate status comes from documents, not from source");
    }

    [Fact]
    public void A_truncated_type_inventory_leaves_absence_unresolved()
    {
        var docs = ImplementationGapExtractor.Extract(Project().Where(f => f.Key.EndsWith(".md", StringComparison.Ordinal)).Select(f => new ImplementationGapExtractor.Document(f.Key, f.Value)));
        var source = new GapSourceEvidence
        {
            SnapshotId = Guid.NewGuid(), ArchiveName = "x.zip", FileInventoryComplete = true, SymbolsCaptured = true, SymbolsTruncated = true, PackagesCaptured = true, TestsCaptured = true,
            Files = ["demo/src/Demo.Infra/Demo.Infra.csproj", "demo/src/Demo.Infra/Export/Exporters.cs"],
        };
        var e = ImplementationGapEngine.Evaluate(docs, source, [], [], DateTimeOffset.UnixEpoch, "test");
        Task(e, "T022").Artifacts.Where(c => c.Kind == GapArtifactKind.Type).Should().OnlyContain(c => c.Relationship == GapRelationship.Unresolved);
        e.Findings.Should().NotContain(f => f.Type == GapFindingType.TaskTypeMissing);
        e.Findings.Should().Contain(f => f.Type == GapFindingType.InventoryIncomplete);
    }

    // ── Real-archive regressions (defects found on the M2LB Skole archive, reproduced synthetically) ───────────────────────────

    [Fact]
    public async Task Referenced_files_of_other_modules_globs_and_relative_paths_are_never_this_tasks_implementation()
    {
        // GAP-DEF-1/2/3/4/5: "Create `ward/src/W.Domain/W.Domain.csproj` … (as `demo/src/…`)", a glob, a ../ path, a package declared only by another module's
        // project, a pipeline folder absent from the archive, and a same-named file in another module.
        var files = Project();
        files["ward/specs/001-ward/tasks.md"] = """
            # Tasks: Ward

            - [ ] T002 [P] Create `ward/src/W.Domain/W.Domain.csproj` with only `Acme.Parquet` 1.2.3 (as `demo/src/Demo.Infra/Demo.Infra.csproj`)
            - [ ] T006 [P] Create `ward/tests/W.Unit/W.Unit.csproj`; tests in `../W.Unit/Pii/**/*.cs`
            - [ ] T009 [P] Create `ward/.pipeline/ward-dev.yml` from `demo/.pipeline/demo-*.yml`
            - [ ] T010 [P] Implement `ExporterTests` support in `ward/src/W.Api/Export/Exporters.cs`
            """;
        var e = await Import(files);
        var t002 = e.Tasks.Single(t => t.TaskId == "T002" && t.Source.Path.StartsWith("ward/", StringComparison.Ordinal));
        t002.Artifacts.Should().NotContain(c => c.Expected.StartsWith("demo/", StringComparison.Ordinal), "a referenced template in another module is context, not an expected artifact");
        t002.Artifacts.Single(c => c.Kind == GapArtifactKind.Package).Relationship.Should().Be(GapRelationship.Missing, "the project the task names does not exist; another module's declaration is not evidence");
        t002.Implementation.Should().Be(GapImplementationEvidence.NoMatchingSource);
        t002.ScopeAbsentFromArchive.Should().BeTrue();
        var t006 = e.Tasks.Single(t => t.TaskId == "T006" && t.Source.Path.StartsWith("ward/", StringComparison.Ordinal));
        t006.Artifacts.Should().NotContain(c => c.Expected.Contains('*') || c.Expected.StartsWith("..", StringComparison.Ordinal));
        e.Tasks.Single(t => t.TaskId == "T009" && t.Source.Path.StartsWith("ward/", StringComparison.Ordinal)).ScopeAbsentFromArchive.Should().BeTrue("ward/.pipeline/ has no file in the archive");
        var t010 = e.Tasks.Single(t => t.TaskId == "T010" && t.Source.Path.StartsWith("ward/", StringComparison.Ordinal));
        t010.Artifacts.Single(c => c.Kind == GapArtifactKind.File).Relationship.Should().Be(GapRelationship.Missing, "demo's Exporters.cs is another module's file, not a moved one");
        e.Findings.Should().NotContain(f => f.Sources.Any(s => s.Path.StartsWith("ward/", StringComparison.Ordinal))
            && new[] { GapFindingType.ArtifactMovedOrRenamed, GapFindingType.MultipleCandidates, GapFindingType.TaskFileMissing, GapFindingType.PackageMissing }.Contains(f.Type));
    }

    [Fact]
    public async Task A_spec_only_module_is_out_of_this_archive_for_root_files_typeless_tasks_and_contract_references()
    {
        // GAP-DEF-6/7/8 (real Skole archive): module-root files and tasks naming only types counted the module's specs/ as "implementation present";
        // field names after "with" were taken as types; contract yaml/graphql references were treated as expected code ("found elsewhere").
        var files = Project();
        files["ward/specs/001-ward/tasks.md"] = """
            # Tasks: Ward

            - [ ] T001 Create `ward/global.json` pinning the SDK
            - [ ] T078 [US2] Create entity `Utdanning` with `Navn`, `Fra` and `Til`
            - [ ] T140 [US2] Contract tests in `ward/tests/W.Contract/SchemaSnapshotTests.cs` against `contracts/ward.graphql`
            """;
        files["ward/specs/001-ward/contracts/ward.graphql"] = "type Query { ward: String }";
        files["ward/specs/001-ward/spec.md"] = "# Feature Specification: Ward\n\n- **FR-001**: System MUST exist.\n";
        var e = await Import(files);
        var ward = e.Tasks.Where(t => t.Source.Path.StartsWith("ward/", StringComparison.Ordinal)).ToList();
        ward.Should().OnlyContain(t => t.ScopeAbsentFromArchive, "the ward module holds specifications only");
        ward.Single(t => t.TaskId == "T078").Artifacts.Where(c => c.Kind == GapArtifactKind.Type).Select(c => c.Expected).Should().Equal("Utdanning");
        ward.Single(t => t.TaskId == "T140").Artifacts.Should().NotContain(c => c.Expected.Contains("contracts/"));
        e.Findings.Where(f => f.Sources.Any(s => s.Path.StartsWith("ward/", StringComparison.Ordinal))).Should()
            .ContainSingle(f => f.Type == GapFindingType.ImplementationScopeNotInArchive);
    }

    [Fact]
    public async Task Only_decision_gates_delimit_a_feature_but_every_unresolved_gate_blocks_its_tasks()
    {
        var files = Project();
        files["demo/specs/001-delivery/tasks.md"] = Tasks.Replace("| G-1 Constitution approval | Only work after a constitution change | SA |",
            "| Code list values from the partner | T022 | Product owner |", StringComparison.Ordinal);
        var e = await Import(files);
        e.Features.Should().NotContain(f => f.FeatureId.StartsWith("gate:", StringComparison.Ordinal) && f.Name.StartsWith("Code list", StringComparison.Ordinal));
        e.Gates.Single(g => g.Label.StartsWith("Code list", StringComparison.Ordinal)).Status.Should().Be(GapGateStatus.Unknown);
        GateFeature(e).Readiness.Should().Be(GapReadiness.Blocked, "T022 is in the ADR-031 feature and is blocked by an unresolved dependency gate");
        e.Findings.Should().Contain(f => f.Type == GapFindingType.BlockedByGate && f.Title.StartsWith("T022 blocked by Code list", StringComparison.Ordinal));
    }

    // ── Persistence, review, isolation ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_review_survives_re_evaluation_and_never_leaks_to_another_import()
    {
        var files = Project(adr031: null);
        var first = await Import(files);
        var finding = first.Findings.First(f => f.Type == GapFindingType.DecisionNotApproved);
        var (decision, error) = await Gaps().ReviewAsync(first.Id, new GapReviewRequest { FindingId = finding.Id, Decision = "IssueFound", Note = "Sign-off requested" }, "Tester", default);
        error.Should().BeNull();
        decision!.Current.Should().BeTrue();

        var again = (await Gaps().ReevaluateAsync(first.Id, new GapReevaluateRequest(), default))!;
        again.Id.Should().NotBe(first.Id, "re-evaluation adds an evaluation; nothing is overwritten");
        again.Findings.Single(f => f.Id == finding.Id).Should().Match<GapFinding>(f => f.ReviewStatus == "IssueFound" && f.ReviewedBy == "Tester");
        (await Gaps().ListAsync(first.ScopeKey, default)).Should().HaveCount(2);

        files["demo/specs/001-delivery/plan.md"] += "\nAnother revision.\n";
        var other = await Import(files, name: "demo-v2.zip");
        other.ScopeKey.Should().NotBe(first.ScopeKey);
        other.Findings.Single(f => f.Id == finding.Id).ReviewStatus.Should().Be("Not reviewed", "a review belongs to its own import scope");
        (await Gaps().ReviewAsync(first.Id, new GapReviewRequest { FindingId = "GAP-nope", Decision = "IssueFound" }, "Tester", default)).Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Finding_ids_are_stable_and_every_finding_is_explainable()
    {
        var a = await Import(Project(adr031: null));
        var b = (await Gaps().ReevaluateAsync(a.Id, new GapReevaluateRequest(), default))!;
        b.Findings.Select(f => f.Id).Should().BeEquivalentTo(a.Findings.Select(f => f.Id));
        a.Findings.Should().OnlyContain(f => f.Id.StartsWith("GAP-") && f.Rule.Id.Length > 0 && f.Rule.Description.Length > 0 && f.Expected.Length > 0 && f.Guidance.Length > 0
            && f.EvaluatedAt != default && f.SnapshotId == a.SnapshotId);
        a.Findings.SelectMany(f => f.Sources.Concat(f.ActualEvidence)).Where(r => r.Line is not null).Should().OnlyContain(r => r.Line > 0);
    }

    // ── Extraction rules ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("- [ ] T119 [US3] **Gate: ADR-028 signed off.** Add `Parquet.Net`, pinned exactly, to `Skole/src/X/X.csproj`, and expose its version as a constant for `leveranse.bibliotek_versjon`", "", "Parquet.Net")]
    [InlineData("- [ ] T120 [US3] Implement `DatasettParquetSkriver` and `DekningParquetSkriver`, deterministic per `contracts/bufdata-leveranse.md`, in `Skole/src/X/Leveranse/ParquetSkrivere.cs`:", "DatasettParquetSkriver,DekningParquetSkriver", "")]
    [InlineData("- [ ] T121 [US3] Implement `ManifestSkriver`, which writes `_manifest.json` with `periode`, `kontraktversjon` `1.0.0-utkast`, in `Skole/src/X/ManifestSkriver.cs`", "ManifestSkriver", "")]
    [InlineData("- [ ] T100 [US6] Implement `Dekningsberegner.Beregn(grunnlag, rapporter)` in `Skole/src/X/Dekningsberegner.cs`. It sets `Mottatt`", "Dekningsberegner", "")]
    [InlineData("- [ ] T111 [US3] Add `TaKrav(naa)`, `MarkerLaast(tidspunkt)` to `Skole/src/X/Periodelaas.cs`", "", "")]
    [InlineData("- [ ] T003 [P] Create `Skole/src/X/X.csproj` referencing Domain, with these exact pins: `M2LB.Common` 2.3.0, `Azure.Storage.Blobs` 12.29.2", "", "M2LB.Common,Azure.Storage.Blobs")]
    // GAP-DEF-9 (real archive): framework types and methods were taken as types the task must declare.
    [InlineData("- [x] T027 Implement `OutboxRelay` as `IHostedService` in `src/X/OutboxRelay.cs`", "OutboxRelay", "")]
    [InlineData("- [x] T043 Add a typed `HttpClient` and `GraphKlient` in `src/X/GraphKlient.cs`", "GraphKlient", "")]
    [InlineData("- [x] T030 Add `GetByIdAsync` to `src/X/Repo.cs`", "", "")]
    [InlineData("- [x] T031 Implement `BarnCache` implementing `IBarnCache` in `src/X/BarnCache.cs`", "BarnCache", "")]
    public void Declared_types_and_packages_come_only_from_what_the_task_says_to_create(string line, string types, string packages)
    {
        var (t, p) = ImplementationGapExtractor.Declarations(line);
        string.Join(',', t).Should().Be(types);
        string.Join(',', p).Should().Be(packages);
    }

    [Fact]
    public void Gate_tables_ranges_markers_and_continuations_are_extracted_with_their_lines()
    {
        var set = ImplementationGapExtractor.Extract([new("demo/specs/001-delivery/tasks.md", Tasks)]);
        var gate = set.Gates.Single(g => g.GateId == "ADR-031");
        gate.BlockedTaskIds.Should().Equal("T020", "T021", "T022", "T023");
        gate.Source.Line.Should().Be(7);
        var t021 = set.Tasks.Single(t => t.TaskId == "T021");
        t021.GateMarkers.Should().Equal("ADR-031");
        t021.Line.Should().Be(19);
        set.Tasks.Single(t => t.TaskId == "T020").Should().Match<GapTaskInput>(t => t.IsTestTask && t.Continuation.Count == 1 && t.ReferencedTaskIds.SequenceEqual(new[] { "T021", "T022", "T023" }));
        set.Tasks.Single(t => t.TaskId == "T022").ContractPaths.Should().Equal("demo/specs/001-delivery/contracts/delivery.md");
        set.DecisionTitles["ADR-031"].Should().Be("Exporter library for the partner delivery");
    }

    [Fact]
    public void Only_a_test_path_or_tests_in_makes_a_test_task()
    {
        // GAP-DEF-12 (real archive, Skole T151): the parser's keyword "testing task" flag made an implementation task a test task.
        var set = ImplementationGapExtractor.Extract([new("ward/specs/001-ward/tasks.md", """
            # Tasks

            - [ ] T151 Implement the retention guard in `ward/src/W.Infra/Jobs/OppbevaringJobb.cs`. Assert no national id is logged; test the refusal path.
            - [ ] T152 [P] Unit tests in `ward/tests/W.Unit/OppbevaringJobbTests.cs`
            """)]);
        set.Tasks.Single(t => t.TaskId == "T151").IsTestTask.Should().BeFalse();
        set.Tasks.Single(t => t.TaskId == "T152").IsTestTask.Should().BeTrue();
    }

    [Theory]
    [InlineData("Gjeldende", GapGateStatus.Approved)]
    [InlineData("Godkjent av tech lead", GapGateStatus.Approved)]
    [InlineData("Accepted", GapGateStatus.Approved)]
    [InlineData("Foreslått", GapGateStatus.Pending)]
    [InlineData("Utkast", GapGateStatus.Pending)]
    [InlineData("Proposed", GapGateStatus.Pending)]
    [InlineData("Avvist", GapGateStatus.Rejected)]
    [InlineData("Erstattet av [ADR-027](#adr-027)", GapGateStatus.Unknown)]
    [InlineData("Under vurdering?", GapGateStatus.Unknown)]
    public void Only_an_explicit_accepted_status_approves_a_decision(string status, GapGateStatus expected) =>
        ImplementationGapExtractor.StatusOf(status).Should().Be(expected);

    [Fact]
    public void Decision_registers_read_sections_before_index_rows()
    {
        var decisions = ImplementationGapExtractor.Decisions(new("docs/adr.md", Register("Foreslått")));
        decisions.Select(d => (d.Id, d.Status)).Should().Equal(("ADR-030", GapGateStatus.Approved), ("ADR-031", GapGateStatus.Pending));
        decisions.Single(d => d.Id == "ADR-031").Source.Note.Should().Be("decision section status");
    }
}
