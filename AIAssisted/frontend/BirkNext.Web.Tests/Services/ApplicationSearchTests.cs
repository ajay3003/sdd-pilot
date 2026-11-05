using BirkNext.Integrations;
using BirkNext.Web.Services.ProjectEvidence;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Change Application search: everything BirkNext already knows is findable — registered applications (chosen as they are), imported
/// application sources and Delivery projects (create-and-link) — without duplicates, case-insensitively, partially and separator-insensitively;
/// infrastructure-only sources are never applications; similar names stay distinct identities.
/// </summary>
public sealed class ApplicationSearchTests
{
    private static SourceSetupSnapshot Snapshot(string archive, string code = "Complete", string? repository = null, string? importId = null) =>
        new(Guid.NewGuid(), archive, "fp", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), "Partial")
        { Repository = repository, ImportId = importId, Areas = [new("Application code", code)] };

    private static readonly IReadOnlyList<SourceSetupSnapshot> M2lbSources =
    [
        Snapshot("M2LB-language.zip"), Snapshot("M2LB.Common.zip"), Snapshot("Terraform-iac-m2lb.zip", "NotDetected"),
        Snapshot("Tiltak 2 - Sikkert digitalt økosystem.zip", "Partial"),
    ];

    [Fact]
    public void Imported_sources_and_delivery_projects_are_found_when_nothing_is_registered_yet()
    {
        var all = ApplicationSearch.Build([], [], M2lbSources, ["M2LB"]);
        var m2lb = ApplicationSearch.Search(all, "m2lb");
        m2lb.Select(r => (r.Name, r.Kind)).Should().Equal(
            [("M2LB", ApplicationSearchKind.DeliveryProject), ("M2LB-language", ApplicationSearchKind.ImportedSource), ("M2LB.Common", ApplicationSearchKind.ImportedSource)],
            "exact match first; the Terraform package is infrastructure, never an application");
        ApplicationSearch.Search(all, "terraform").Should().BeEmpty();
        ApplicationSearch.Search(all, "M2LB LANGUAGE").Should().ContainSingle().Which.Name.Should().Be("M2LB-language", "separators and case are ignored");
        ApplicationSearch.Search(all, "sikkert").Should().ContainSingle().Which.Kind.Should().Be(ApplicationSearchKind.ImportedSource);
        ApplicationSearch.Search(all, "nothing-like-this").Should().BeEmpty();
    }

    [Fact]
    public void A_registered_application_is_listed_once_first_and_names_what_it_links()
    {
        var all = ApplicationSearch.Build([("M2LB", 2)], [], M2lbSources, ["M2LB"]);
        var results = ApplicationSearch.Search(all, "m2lb");
        results[0].Should().Match<ApplicationSearchResult>(r => r.Name == "M2LB" && r.IsRegistered && r.Detail.Contains("used by 2 environments") && r.Detail.Contains("delivery projects"));
        results.Count(r => string.Equals(r.Name, "M2LB", StringComparison.OrdinalIgnoreCase)).Should().Be(1, "the Delivery project of the same name is not a second application");
        results.Where(r => !r.IsRegistered).Select(r => r.Name).Should().BeEquivalentTo(["M2LB.Common", "M2LB-language"], "similar names are different identities, never merged");
    }

    [Fact]
    public void Sources_already_recorded_as_imported_projects_are_not_listed_twice_and_the_archive_name_is_an_alias()
    {
        var withRepo = Snapshot("export-2026.zip", repository: "M2LB-language");
        var all = ApplicationSearch.Build([], [], [withRepo, Snapshot("other.zip", importId: "imp-1")], []);
        all.Should().ContainSingle().Which.Name.Should().Be("M2LB-language", "a snapshot from Import Project is listed through its import, not again");
        ApplicationSearch.Search(all, "export").Should().ContainSingle("the archive file name is an alias");
    }

    [Fact]
    public void Application_code_decides_what_is_an_application_source()
    {
        ApplicationSearch.HasApplicationCode(Snapshot("a.zip", "Complete")).Should().BeTrue();
        ApplicationSearch.HasApplicationCode(Snapshot("a.zip", "Partial")).Should().BeTrue();
        ApplicationSearch.HasApplicationCode(Snapshot("a.zip", "NotDetected")).Should().BeFalse();
        ApplicationSearch.HasApplicationCode(Snapshot("a.zip", "Failed")).Should().BeFalse("a failed code analysis says nothing about an application");
        ApplicationSearch.HasApplicationCode(new SourceSetupSnapshot(Guid.NewGuid(), "old.zip", "", DateTimeOffset.UnixEpoch, "Complete") { ServiceProjectCount = 1 }).Should().BeTrue("older backends report projects, not areas");
    }
}
