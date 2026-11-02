using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectImport;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Package vs logical application rules: a legacy workspace gets one deterministic application without anything else changing; a shared
/// package names its application; MU next to M2LB is a related source, never the primary; an unclassified package's role is never guessed.
/// </summary>
public sealed class ApplicationContextsTests
{
    private static SddProjectImportRecord Record(string name, int minutes, Guid? snapshot = null) => new()
    {
        ImportId = "import-" + name.ToLowerInvariant().Replace(' ', '-'), ProjectName = name, ArchiveFileName = name + ".zip",
        ImportedAt = new DateTimeOffset(2026, 11, 2, 7, minutes, 0, TimeSpan.Zero), SourceSnapshotId = snapshot, SourceState = snapshot is null ? "NotDetected" : "Created",
    };

    private static SourceSetupView Setup(params (SddProjectImportRecord Record, int Services, int Shared)[] packages) => new()
    {
        Snapshots = [.. packages.Select(p => new SourceSetupSnapshot(p.Record.SourceSnapshotId!.Value, p.Record.ArchiveFileName, "f", p.Record.ImportedAt, "Partial")
            { ImportId = p.Record.ImportId, Areas = [new("Application code", "Complete")], ServiceProjectCount = p.Services, SharedProjectCount = p.Shared })],
    };

    [Fact]
    public void ALegacyWorkspace_GetsOneDeterministicApplication_AndNothingElseChanges()
    {
        SddLifecycleState Legacy() => new()
        {
            ProjectImports = [Record("M2LB", 8, Guid.NewGuid()), Record("M2LB.Common", 14, Guid.NewGuid()), Record("Tiltak 2", 16, Guid.NewGuid())],
            CurrentProjectImportId = "import-tiltak-2",
        };
        var first = Legacy(); var second = Legacy();
        ApplicationContexts.Ensure(first, DateTimeOffset.UtcNow).Should().BeTrue();
        ApplicationContexts.Ensure(second, DateTimeOffset.UtcNow);

        first.Applications.Should().ContainSingle();
        first.Applications[0].Id.Should().Be(second.Applications[0].Id, "reloading an unsaved legacy workspace must not re-key its activity");
        first.Applications[0].Name.Should().Be("M2LB", "M2LB.Common names the application it belongs to");
        first.ProjectImports.Should().OnlyContain(i => i.ApplicationId == first.Applications[0].Id);
        first.CurrentApplicationId.Should().Be(first.Applications[0].Id);
        first.CurrentProjectImportId.Should().Be("import-tiltak-2", "the migration never changes what is shown");
        ApplicationContexts.Ensure(first, DateTimeOffset.UtcNow).Should().BeFalse("idempotent");
    }

    [Fact]
    public void MuNextToM2lb_IsARelatedSource_TheApplicationKeepsM2lbAsPrimary_InAnyOrder()
    {
        foreach (var muFirst in new[] { true, false })
        {
            var mu = Record("Tiltak 2", muFirst ? 1 : 30, Guid.NewGuid());
            var common = Record("M2LB.Common", 10, Guid.NewGuid());
            var m2lb = Record("M2LB", muFirst ? 20 : 2, Guid.NewGuid());
            var lifecycle = new SddLifecycleState { ProjectImports = [mu, common, m2lb] };
            ApplicationContexts.Ensure(lifecycle, DateTimeOffset.UtcNow);
            var setup = Setup((mu, 5, 3), (common, 0, 1), (m2lb, 16, 23));

            var packages = ApplicationContexts.Packages(lifecycle, lifecycle.CurrentApplicationId, setup);
            ApplicationContexts.Primary(packages)!.DisplayName.Should().Be("M2LB", $"M2LB.Common points to M2LB (MU first: {muFirst})");
            packages.Single(p => p.DisplayName == "Tiltak 2").Role.Should().Be(PackageEvidenceRole.RelatedApplicationSource);
            packages.Single(p => p.DisplayName == "M2LB.Common").Role.Should().Be(PackageEvidenceRole.SupportingSource);
            ApplicationContexts.Reconcile(lifecycle, setup);
            lifecycle.Applications.Single().Name.Should().Be("M2LB");
            ApplicationContexts.DocumentImport(lifecycle, lifecycle.CurrentApplicationId!, setup, mu.ImportId).Should().Be(m2lb.ImportId);
        }
    }

    [Fact]
    public void AnUnclassifiedPackage_IsNeverGuessed_AndKeepsItsPlace()
    {
        var a = Record("orders-a", 1, Guid.NewGuid());
        var b = Record("orders-b", 2, Guid.NewGuid());
        var lifecycle = new SddLifecycleState { ProjectImports = [a, b] };
        ApplicationContexts.Ensure(lifecycle, DateTimeOffset.UtcNow);

        ApplicationContexts.Packages(lifecycle, lifecycle.CurrentApplicationId, null).Should().OnlyContain(p => p.Role == PackageEvidenceRole.NotClassified);
        ApplicationContexts.Packages(lifecycle, lifecycle.CurrentApplicationId, new SourceSetupView()).Should().OnlyContain(p => p.Role == PackageEvidenceRole.NotClassified,
            "a snapshot the backend does not report is not classified");
        ApplicationContexts.DocumentImport(lifecycle, lifecycle.CurrentApplicationId!, null, b.ImportId).Should().Be(b.ImportId);
    }

    [Theory]
    [InlineData("M2LB.Common", "M2LB")]
    [InlineData("Orders.Shared.Contracts", "Orders.Shared")]
    [InlineData("M2LB", null)]
    [InlineData("Terraform-iac-m2lb", null)]
    public void FamilyOf_NamesTheApplicationOfASharedPackageOnly(string name, string? family) =>
        ApplicationContexts.FamilyOf(name).Should().Be(family);

    [Fact]
    public void ANewApplication_IsSeparate_AndAReimportStaysWithItsApplication()
    {
        var lifecycle = new SddLifecycleState();
        var m2lb = Record("M2LB", 1); lifecycle.ProjectImports.Add(m2lb);
        var first = ApplicationContexts.Assign(lifecycle, m2lb, ImportTarget.CurrentApplication, DateTimeOffset.UtcNow);
        var orders = Record("Orders", 2); lifecycle.ProjectImports.Add(orders);
        var second = ApplicationContexts.Assign(lifecycle, orders, ImportTarget.NewApplication, DateTimeOffset.UtcNow);
        second.Id.Should().NotBe(first.Id);
        lifecycle.CurrentApplicationId.Should().Be(second.Id);

        ApplicationContexts.Assign(lifecycle, m2lb, ImportTarget.CurrentApplication, DateTimeOffset.UtcNow).Id.Should().Be(first.Id, "re-importing M2LB keeps it in M2LB");
    }
}
