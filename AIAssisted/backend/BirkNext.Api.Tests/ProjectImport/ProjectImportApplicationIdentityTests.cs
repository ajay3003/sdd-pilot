using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ProjectImport;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;
using BirkNext.ProjectImport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using static BirkNext.Api.Tests.ProjectImport.ProjectImportServiceTests;

namespace BirkNext.Api.Tests.ProjectImport;

/// <summary>
/// Application identity at import (2026-11-23): "Ny applikasjon" / "Ny versjon av eksisterende applikasjon" is the tester's explicit choice. A
/// differently named archive joins an application only when chosen; both imports keep their own snapshot; the archive's own identity is kept for
/// lineage; nothing is inferred from file names; without a choice the archive's identity applies as before.
/// </summary>
public sealed class ProjectImportApplicationIdentityTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly ProjectImportStagingStore _staging = new();

    public void Dispose() => _db.Dispose();

    private ProjectImportService Service()
    {
        var sources = new Mock<IReviewSourceEvidenceProvider>();
        sources.SetupGet(s => s.SourceAnalysisEnabled).Returns(true);
        var store = new IqrSourceStore(_db);
        return new ProjectImportService(store, _staging, sources.Object, NullLogger<ProjectImportService>.Instance, baselines: new SourceBaselineService(_db, store));
    }

    private static byte[] Shop(string marker, string project = "Shop.Api") => Zip(
        ($"shop/src/{project}/{project}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"),
        ($"shop/src/{project}/Program.cs", $"var app = WebApplication.CreateBuilder(args).Build(); app.MapGet(\"/carts\", () => \"{marker}\"); app.Run();"));

    private async Task<(ProjectImportCommitResult Commit, IqrSourceSnapshot Snapshot)> ImportAsync(string file, byte[] bytes, ProjectImportApplicationChoice? choice = null)
    {
        var service = Service();
        var preview = service.Preview(file, bytes).Preview!;
        if (choice is not null) await service.ChooseApplicationAsync(preview.StagingId, choice, default);
        var commit = (await service.CommitAsync(preview.StagingId))!;
        return (commit, (await new IqrSourceStore(_db).FindSourceAnalysisAsync(commit.Source.SnapshotId!.Value))!);
    }

    [Fact]
    public async Task Without_a_choice_the_archive_identity_applies_as_before_and_a_different_file_name_is_a_different_application()
    {
        var (first, s1) = await ImportAsync("M2LB.zip", Shop("v1"));
        var (second, s2) = await ImportAsync("M2LB-language.zip", Shop("v2"));

        SourceBaselineService.KeyOf(s1).Should().Be("m2lb");
        SourceBaselineService.KeyOf(s2).Should().Be("m2lb-language", "nothing is inferred from a similar file name");
        s2.ArchiveRepository.Should().BeNull();
        second.Application!.Explicit.Should().BeFalse();
        second.Application.Mode.Should().Be(ProjectImportApplicationMode.NewApplication);
        first.Application!.ApplicationKey.Should().Be("m2lb");
    }

    [Fact]
    public async Task A_new_version_keeps_one_application_identity_both_snapshots_and_the_archive_lineage_and_becomes_the_baseline()
    {
        var (_, s1) = await ImportAsync("M2LB.zip", Shop("v1"));
        var service = Service();
        var preview = service.Preview("M2LB-language.zip", Shop("v2")).Preview!;

        var options = (await service.ApplicationOptionsAsync(preview.StagingId, default))!;
        options.ArchiveKey.Should().Be("m2lb-language");
        options.SameIdentityAs.Should().BeNull();
        options.Applications.Should().ContainSingle(a => a.Key == "m2lb" && a.Name == "M2LB" && a.LatestArchive == "M2LB.zip" && a.Imports == 1);

        var chosen = (await service.ChooseApplicationAsync(preview.StagingId, new(ProjectImportApplicationMode.NewVersion, "m2lb"), default))!;
        chosen.ApplicationName.Should().Be("M2LB");
        chosen.Warning.Should().BeNull("the archives share the Shop.Api project");
        var commit = (await service.CommitAsync(preview.StagingId))!;
        var s2 = (await new IqrSourceStore(_db).FindSourceAnalysisAsync(commit.Source.SnapshotId!.Value))!;

        s2.Id.Should().NotBe(s1.Id, "every import keeps its own snapshot");
        SourceBaselineService.KeyOf(s2).Should().Be("m2lb", "one logical application");
        s2.Repository!.Basis.Should().Contain("Ny versjon av M2LB");
        s2.ArchiveRepository!.Key.Should().Be("m2lb-language", "the archive's own identity is kept for lineage");
        SourceBaselineService.KeyOf((await new IqrSourceStore(_db).FindSourceAnalysisAsync(s1.Id))!).Should().Be("m2lb", "the earlier import is unchanged");
        commit.Application.Should().BeEquivalentTo(new ProjectImportApplicationOutcome
        {
            Mode = ProjectImportApplicationMode.NewVersion, ApplicationKey = "m2lb", ApplicationName = "M2LB", ArchiveKey = "m2lb-language", ArchiveName = "M2LB-language",
            BaselineSelected = true, Explicit = true,
        });
        (await SourceBaselineService.CurrentBaselinesAsync(_db, default))["m2lb"].Should().Be(s2.Id, "recorded with the existing baseline decision");
        (await Service().ApplicationsAsync(default)).Should().ContainSingle(a => a.Key == "m2lb" && a.Imports == 2);
    }

    [Fact]
    public async Task Use_as_current_off_records_no_baseline()
    {
        await ImportAsync("M2LB.zip", Shop("v1"));
        var (commit, _) = await ImportAsync("M2LB-language.zip", Shop("v2"), new(ProjectImportApplicationMode.NewVersion, "m2lb", UseAsCurrent: false));
        commit.Application!.BaselineSelected.Should().BeFalse();
        (await SourceBaselineService.CurrentBaselinesAsync(_db, default)).Should().NotContainKey("m2lb");
    }

    [Fact]
    public async Task A_new_application_whose_name_is_taken_gets_its_own_identity_and_never_joins_silently()
    {
        await ImportAsync("M2LB.zip", Shop("v1"));
        var (commit, s2) = await ImportAsync("M2LB.zip", Shop("other"), new(ProjectImportApplicationMode.NewApplication));

        SourceBaselineService.KeyOf(s2).Should().Be("m2lb (2)");
        s2.Repository!.DisplayName.Should().Be("M2LB (2)");
        s2.ArchiveRepository!.Key.Should().Be("m2lb");
        commit.Application!.Mode.Should().Be(ProjectImportApplicationMode.NewApplication);
    }

    [Fact]
    public async Task An_unknown_application_is_refused_and_an_unrelated_archive_is_warned_about_but_allowed()
    {
        await ImportAsync("M2LB.zip", Shop("v1"));
        var service = Service();
        var preview = service.Preview("Billing.zip", Shop("b", project: "Billing.Api")).Preview!;

        (await FluentActions.Awaiting(() => service.ChooseApplicationAsync(preview.StagingId, new(ProjectImportApplicationMode.NewVersion, "does-not-exist"), default))
            .Should().ThrowAsync<ProjectImportChoiceException>()).Which.Code.Should().Be("APPLICATION_UNKNOWN");
        var chosen = (await service.ChooseApplicationAsync(preview.StagingId, new(ProjectImportApplicationMode.NewVersion, "m2lb"), default))!;
        chosen.Warning.Should().Contain("ingen prosjekter felles", "a legitimate version is never rejected, but an unrelated one is pointed out");
        (await service.CommitAsync(preview.StagingId))!.Application!.ApplicationKey.Should().Be("m2lb");
    }

    [Fact]
    public async Task The_same_zip_repeated_is_reused_and_a_settled_import_cannot_change_its_choice()
    {
        await ImportAsync("M2LB.zip", Shop("v1"));
        var service = Service();
        var bytes = Shop("v2");
        var preview = service.Preview("M2LB-language.zip", bytes).Preview!;
        await service.ChooseApplicationAsync(preview.StagingId, new(ProjectImportApplicationMode.NewVersion, "m2lb"), default);
        var first = (await service.CommitAsync(preview.StagingId))!;

        (await FluentActions.Awaiting(() => service.ChooseApplicationAsync(preview.StagingId, new(ProjectImportApplicationMode.NewApplication), default))
            .Should().ThrowAsync<ProjectImportChoiceException>()).Which.Code.Should().Be("IMPORT_ALREADY_COMMITTED");

        // The same bytes uploaded again with the same choice: the current snapshot is reused, not analysed again.
        var again = service.Preview("M2LB-language.zip", bytes).Preview!;
        await service.ChooseApplicationAsync(again.StagingId, new(ProjectImportApplicationMode.NewVersion, "m2lb"), default);
        var second = (await service.CommitAsync(again.StagingId))!;
        second.Source.State.Should().Be(ProjectImportSourceState.Reused);
        second.Source.SnapshotId.Should().Be(first.Source.SnapshotId);
        (await _db.IqrSourceSnapshots.CountAsync()).Should().Be(2);
    }
}
