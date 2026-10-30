using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.DeliveryReporting;
using FluentAssertions;
using Xunit;
using static BirkNext.Api.Tests.DeliveryReporting.DeliveryReportingFixtures;

namespace BirkNext.Api.Tests.DeliveryReporting;

public sealed class DeliverySnapshotParserTests
{
    [Fact]
    public void ChildSearchZip_GivesOneFeature_WithItsCheckboxCounts()
    {
        var snapshot = Parse(ChildSearch(8, 3));

        var feature = snapshot.Features.Should().ContainSingle().Subject;
        feature.Title.Should().Be("Child search");
        feature.Module.Should().Be("Frontend");
        feature.SpecPath.Should().Be("Frontend/specs/001-child-search/spec.md");
        feature.TasksPath.Should().Be("Frontend/specs/001-child-search/tasks.md");
        feature.TaskTotal.Should().Be(8);
        feature.TaskCompleted.Should().Be(3);
        feature.Tasks.Select(t => t.TaskKey).Should().StartWith(["T001", "T002"]);
        feature.Status.Should().Be(DeliveryStatus.InProgress);
        feature.StatusReason.Should().Be("3 of 8 tasks completed");
        feature.AcceptanceCriteria.Should().NotBeEmpty("Spec-Kit acceptance scenarios are read through the Specification Explorer parser");
        snapshot.Modules.Should().Equal("Frontend");
        snapshot.Coverage.Should().Be(DeliveryCoverage.Complete);
    }

    [Fact]
    public void SupportingDocumentationTree_NeverBecomesAModuleOrAFeature()
    {
        var snapshot = Parse([.. ChildSearch(8, 3), ("Frontend/docs/superpowers/specs/design.md", "# Design notes\n\nSome design."),
            ("Frontend/docs/superpowers/plans/plan.md", "# Plan notes")]);

        snapshot.Modules.Should().Equal("Frontend").And.NotContain(m => m.Contains("superpowers"));
        snapshot.Features.Should().ContainSingle();
        snapshot.Artifacts.Should().Contain(a => a.Path == "Frontend/docs/superpowers/specs/design.md" && a.Kind == "SupportingDocument" && a.Module == "Frontend");
    }

    [Fact]
    public void AllCheckedTasks_AreTasksComplete_NotDone()
    {
        var feature = Parse(ChildSearch(5, 5)).Features.Single();

        feature.Status.Should().Be(DeliveryStatus.TasksComplete);
        feature.StatusReason.Should().Contain("not the same as delivered");
    }

    [Theory]
    [InlineData("Ready for test", DeliveryStatus.ReadyForTest)]
    [InlineData("Testing", DeliveryStatus.Testing)]
    [InlineData("Blocked", DeliveryStatus.Blocked)]
    [InlineData("Done", DeliveryStatus.Done)]
    public void ExplicitStatusLine_IsTheOnlyWayToStrongerStatuses(string line, DeliveryStatus expected)
    {
        var feature = Parse(ChildSearch(5, 2, statusLine: line)).Features.Single();

        feature.Status.Should().Be(expected);
        feature.Evidence.Should().Contain(e => e.Kind == "StatusLine" && e.Path.EndsWith("tasks.md"));
    }

    [Fact]
    public void DraftStatusLine_IsIgnored_AndClarificationMarkersNeedClarification()
    {
        var spec = Spec("Child search", DefaultAcceptance) + "\n- **FR-002**: System MUST [NEEDS CLARIFICATION: which registry?]\n";
        var feature = Parse([("Frontend/specs/001-child-search/spec.md", spec), ("Frontend/specs/001-child-search/tasks.md", Tasks(4, 1))]).Features.Single();

        feature.Status.Should().Be(DeliveryStatus.NeedsClarification, "Spec-Kit's [NEEDS CLARIFICATION] markers are explicit evidence; Draft is not a delivery status");
        feature.OpenClarifications.Should().Be(1);
    }

    [Fact]
    public void Warnings_SpecWithoutTasks_UnmappedTaskFile_DuplicateFeatureNumber_DoNotRejectTheImport()
    {
        var snapshot = Parse([
            .. ChildSearch(4, 1),
            ("Frontend/specs/001-child-search-v2/spec.md", Spec("Child search v2", DefaultAcceptance)),
            ("Frontend/tasks.md", Tasks(3, 0)),
        ]);

        snapshot.Features.Should().HaveCount(2);
        snapshot.Warnings.Select(w => w.Code).Should().Contain(["NO_TASKS", "TASKS_UNMAPPED", "DUPLICATE_FEATURE_ID"]);
        snapshot.Features.Single(f => f.Folder == "001-child-search-v2").Status.Should().Be(DeliveryStatus.NotAssessed);
    }

    [Fact]
    public void NoSpecKitStructure_IsInsufficient_WithAWarning()
    {
        var snapshot = Parse([("README.md", "# Repo"), ("docs/specs/notes.md", "# Notes")]);

        snapshot.Features.Should().BeEmpty();
        snapshot.Coverage.Should().Be(DeliveryCoverage.Insufficient);
        snapshot.Warnings.Should().Contain(w => w.Code == "NO_FEATURES");
    }

    [Fact]
    public void GitArchiveComment_GivesTheCommit_AndUniformEntryTimesSuggestAnEffectiveDate()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        var parsed = DeliverySnapshotParser.Parse("m2lb.zip", Zip(ChildSearch(8, 3), commit)).Snapshot!;

        parsed.GitCommit.Should().Be(commit);
        parsed.SourceType.Should().Be(DeliverySourceType.GitArchive);
        parsed.SuggestedEffectiveAt.Should().NotBeNull();
        parsed.SuggestedEffectiveReason.Should().Contain("0123456789");
    }

    [Fact]
    public void ArchiveWrapperFolder_IsRemoved_SoTheSameRepositoryKeepsItsFeatureIdentity()
    {
        var plain = Parse([.. ChildSearch(8, 3), ("Backend/src/App.cs", "class App {}")]);
        var wrapped = Parse([.. ChildSearch(8, 3, module: "M2LB-main/Frontend"), ("M2LB-main/Backend/src/App.cs", "class App {}")]);

        wrapped.WrapperFolder.Should().Be("M2LB-main");
        wrapped.Features.Single().Key.Should().Be(plain.Features.Single().Key);
    }

    [Fact]
    public void InvalidArchive_IsRejectedByTheCanonicalReader()
    {
        var result = DeliverySnapshotParser.Parse("m2lb.zip", [1, 2, 3, 4]);

        result.Snapshot.Should().BeNull();
        result.Failure!.Code.Should().Be("ARCHIVE_INVALID_ZIP");
    }

    [Fact]
    public void SameBytes_GiveTheSameSnapshot()
    {
        var bytes = Zip(ChildSearch(8, 3));
        var a = DeliverySnapshotParser.Parse("m2lb.zip", bytes).Snapshot!;
        var b = DeliverySnapshotParser.Parse("m2lb.zip", bytes).Snapshot!;

        b.Fingerprint.Should().Be(a.Fingerprint);
        b.Features.Single().Tasks.Should().BeEquivalentTo(a.Features.Single().Tasks);
        b.Features.Single().SpecFingerprint.Should().Be(a.Features.Single().SpecFingerprint);
    }
}
