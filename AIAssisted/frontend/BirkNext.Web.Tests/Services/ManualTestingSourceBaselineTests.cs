using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>SRC-001 (M2LB (9) source analysis, 2026-11-27): Testoversikt names the import behind its numbers and warns about a newer import of the
/// same application; it never compares unrelated applications or imports without a recorded application, and never switches by itself.</summary>
public sealed class ManualTestingSourceBaselineTests
{
    private static SddProjectImportRecord Import(string id, string file, string at, string? app) =>
        new() { ImportId = id, ArchiveFileName = file, ArchiveSha256 = "bbc3ec2b1fe08251dd7377f2", ImportedAt = DateTimeOffset.Parse(at), ApplicationId = app };

    private static readonly SddProjectImportRecord[] Imports =
    [
        Import("import-old", "M2LB.zip", "2026-11-18T13:30:00Z", "m2lb"),
        Import("import-new", "M2LB (9).zip", "2026-11-27T13:20:00Z", "m2lb"),
        Import("import-tf", "Terraform.zip", "2026-11-28T09:00:00Z", "terraform"),
        Import("import-loose", "Loose.zip", "2026-11-29T09:00:00Z", null),
    ];

    [Fact]
    public void SRC001_the_current_import_is_named_with_its_archive_time_and_hash()
    {
        var b = ManualTestingSourceBaseline.Of("import:import-new", Imports)!;
        b.Current.ArchiveFileName.Should().Be("M2LB (9).zip");
        b.Text.Should().StartWith("M2LB (9).zip · importert ").And.EndWith("SHA-256 bbc3ec2b1fe0");
        b.Warning.Should().BeNull("no newer import of the same application exists");
    }

    [Fact]
    public void SRC001_an_older_import_of_the_same_application_warns_and_names_the_newer_one()
    {
        var b = ManualTestingSourceBaseline.Of("import:import-old", Imports)!;
        b.Newer.Select(i => i.ImportId).Should().Equal("import-new");
        b.Warning.Should().Contain("M2LB (9).zip").And.Contain("bygger på den eldre importen").And.Contain("Velg den nyere importen");
    }

    [Fact]
    public void SRC001_other_applications_and_imports_without_an_application_are_never_compared()
    {
        ManualTestingSourceBaseline.Of("import:import-tf", Imports)!.Newer.Should().BeEmpty();
        ManualTestingSourceBaseline.Of("import:import-loose", Imports)!.Newer.Should().BeEmpty();
        ManualTestingSourceBaseline.Of("library", Imports).Should().BeNull("a project that is not an import has no source version to show");
        ManualTestingSourceBaseline.Of("import:unknown", Imports).Should().BeNull();
    }
}
