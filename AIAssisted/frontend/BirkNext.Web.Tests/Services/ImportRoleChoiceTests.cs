using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectImport;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>"What does this ZIP contain?" on Import Project (2026-11-21): ticked roles are saved only where the content supports them.</summary>
public sealed class ImportRoleChoiceTests
{
    private static (SddProjectImportRecord Record, SddLifecycleState Lifecycle) DocsOnly()
    {
        var record = new SddProjectImportRecord { ImportId = "t2", ProjectName = "Tiltak 2", ArchiveFileName = "Tiltak 2.zip", SourceState = "NotDetected" };
        var lifecycle = new SddLifecycleState { ProjectImports = [record] };
        lifecycle.Revisions.Add(new SddArtifactRevision { Role = nameof(WorkspaceArtifactType.Specification), FileName = "spec.md", Content = "# Spec", WorkspaceScope = ProjectImportScope.For("t2") });
        return (record, lifecycle);
    }

    [Fact]
    public void Only_roles_the_content_shows_are_saved_and_the_rest_are_reported()
    {
        var (record, lifecycle) = DocsOnly();
        var outcome = ImportRoleChoice.Apply(record, lifecycle, new SourceSetupView(), [PackageEvidenceRole.Documentation, PackageEvidenceRole.PrimaryApplicationSource]);
        record.ConfirmedRoles.Should().Equal(nameof(PackageEvidenceRole.Documentation));
        outcome.Refused.Should().Equal(PackageEvidenceRole.PrimaryApplicationSource);
        outcome.Message("Tiltak 2").Should().Contain("Not saved").And.Contain("Primary application source");
    }

    [Fact]
    public void Nothing_ticked_keeps_the_detected_role_and_an_unreachable_backend_saves_the_choice_unverified()
    {
        var (record, lifecycle) = DocsOnly();
        ImportRoleChoice.Apply(record, lifecycle, new SourceSetupView(), []).Accepted.Should().BeEmpty();
        record.ConfirmedRoles.Should().BeEmpty();

        var outcome = ImportRoleChoice.Apply(record, lifecycle, null, [PackageEvidenceRole.Contracts]);
        outcome.Verified.Should().BeFalse();
        record.ConfirmedRoles.Should().Equal(nameof(PackageEvidenceRole.Contracts));
        outcome.Message("Tiltak 2").Should().Contain("could not be checked");
    }
}
