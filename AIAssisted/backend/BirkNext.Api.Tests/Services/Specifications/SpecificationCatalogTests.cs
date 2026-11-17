using BirkNext.Api.Data;
using BirkNext.Api.Data.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.Specifications;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.Integrations;
using BirkNext.Specifications;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BirkNext.Api.Tests.Services.Specifications;

/// <summary>
/// Server-authoritative requirements: the catalog is read on the server from the imported specification files with the shared classifier and
/// parser (independent fixtures here, not the browser's list), is immutable per content revision (PostgreSQL), and decides new requirement links:
/// unknown ids, ids in another file, ambiguous duplicates across files and another project's catalog are refused even when the browser's list
/// claims otherwise; FR-1 = FR-001; a technical token such as UTF-8 in prose is not a requirement; legacy links are reported honestly
/// (no server catalog, changed wording, no longer in the specification), never relabelled or deleted.
/// </summary>
public sealed class SpecificationCatalogTests : IAsyncLifetime
{
    private const string Project = "import:import-lendo0000000001";
    private const string ImportId = "import-lendo0000000001";
    private readonly string _database = TestPostgres.TemporaryName("birknext_spec");
    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(TestPostgres.Temporary(_database).ConnectionString).Options);
    private SpecificationCatalogService Catalogs() => new(Context());

    public async Task InitializeAsync()
    {
        await Execute($"CREATE DATABASE \"{_database}\"");
        await using var db = Context();
        await DatabaseMigrationLock.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await Execute($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
    }

    private static async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(TestPostgres.Admin().ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Spec(string feature, params string[] requirements) =>
        $"# Feature Specification: {feature}\n\n**Feature Branch**: `001-x`\n\n## User Scenarios & Testing\n\n### User Story 1 - Borrow (Priority: P1)\n\nA member borrows a book.\n\n" +
        "## Requirements *(mandatory)*\n\n### Functional Requirements\n\n" + string.Join("\n", requirements) + "\n\n## Success Criteria\n\n- **SC-001**: Loans are registered in under 2 seconds.\n";

    private static List<SpecificationCatalogService.Document> Archive(string loansSpec) =>
    [
        new("LendoPlus/specs/001-library-loans/spec.md", loansSpec),
        new("LendoPlus/specs/002-søknad/spec.md", Spec("Søknad", "- **FR-001**: Søknaden MÅ lagres med æ, ø og å uendret – ✓.", "- **FR-010**: The system MUST send a receipt.")),
        new("LendoPlus/specs/001-library-loans/plan.md", "# Implementation Plan\n\n## Summary\n\nCovers FR-009 and FR-001.\n"),
        new("LendoPlus/README.md", "# LendoPlus\n\nAll text is UTF-8.\n"),
    ];

    private static readonly string Loans = Spec("Library loans",
        "- **FR-001**: The system MUST register a loan for a valid book number and member card.",
        "- **FR-2**: The system MUST store every text as UTF-8 and reject other encodings.");

    [Fact]
    public async Task The_server_reads_only_specification_files_keeps_file_provenance_and_does_not_take_prose_tokens_for_requirements()
    {
        var catalog = (await Catalogs().CaptureAsync(ImportId, "sha-a", null, Archive(Loans), default))!;

        catalog.ProjectKey.Should().Be(Project);
        catalog.Revision.Should().Be(1);
        catalog.Documents.Select(d => d.Path).Should().BeEquivalentTo(["LendoPlus/specs/001-library-loans/spec.md", "LendoPlus/specs/002-søknad/spec.md"],
            "plan.md and README.md are not specifications");
        catalog.Requirements.Select(r => (r.RequirementId, r.Path)).Should().BeEquivalentTo(new[]
        {
            ("FR-001", "LendoPlus/specs/001-library-loans/spec.md"), ("FR-002", "LendoPlus/specs/001-library-loans/spec.md"),
            ("FR-001", "LendoPlus/specs/002-søknad/spec.md"), ("FR-010", "LendoPlus/specs/002-søknad/spec.md"),
        }, "FR-2 is normalised to FR-002 by the shared grammar; FR-009 (plan) and UTF-8 (prose) are not requirements; the same id in two files is two entries");
        catalog.Requirements.Single(r => r.Path.Contains("søknad") && r.RequirementId == "FR-001").Title.Should().Be("- **FR-001**: Søknaden MÅ lagres med æ, ø og å uendret – ✓.", "the wording as the shared parser reads it (the browser shows the same text)");
    }

    [Fact]
    public async Task A_catalog_is_one_immutable_revision_per_content_and_a_changed_specification_is_the_next_revision()
    {
        var first = (await Catalogs().CaptureAsync(ImportId, "sha-a", null, Archive(Loans), default))!;
        var again = (await Catalogs().CaptureAsync(ImportId, "sha-a", null, Archive(Loans), default))!;
        again.Id.Should().Be(first.Id, "the same content is the same revision (re-import, retried commit)");

        var changed = Loans.Replace("valid book number and member card", "valid book number, member card and PIN");
        var second = (await Catalogs().CaptureAsync(ImportId, "sha-b", null, Archive(changed), default))!;
        second.Revision.Should().Be(2);
        (await Catalogs().CurrentAsync(Project, default))!.Id.Should().Be(second.Id);
        (await Catalogs().RevisionsAsync(Project, default)).Select(r => r.Revision).Should().Equal(2, 1);

        // The database refuses rewriting a stored revision (append-only).
        await using var db = Context();
        var act = () => db.Database.ExecuteSqlRawAsync("UPDATE specification_catalogs SET \"EntriesJson\" = '[]' WHERE \"Revision\" = 1");
        await act.Should().ThrowAsync<PostgresException>().WithMessage("*append-only*");

        // Concurrent captures of the same new content agree on one revision.
        var third = Loans.Replace("reject other encodings", "refuse other encodings");
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Catalogs().CaptureAsync(ImportId, "sha-c", null, Archive(third), default))));
        results.Select(r => r!.Id).Distinct().Should().ContainSingle();
        results[0]!.Revision.Should().Be(3);
    }

    [Fact]
    public async Task Resolution_is_exact_about_identity_files_and_normalisation()
    {
        var catalog = (await Catalogs().CaptureAsync(ImportId, "sha-a", null, Archive(Loans), default))!;

        SpecificationCatalogService.Resolve(catalog, "FR-2", "LendoPlus/specs/001-library-loans/spec.md").Entry!.RequirementId.Should().Be("FR-002", "FR-2 and FR-002 are one requirement");
        SpecificationCatalogService.Resolve(catalog, "fr-010", null).Entry!.Path.Should().EndWith("002-søknad/spec.md");
        SpecificationCatalogService.Resolve(catalog, "FR-001", "specs/001-library-loans/spec.md").Entry!.Path.Should().Be("LendoPlus/specs/001-library-loans/spec.md",
            "the browser's path may omit the archive root folder");
        SpecificationCatalogService.Resolve(catalog, "FR-001", null).Problem.Should().Contain("2 specification files", "a duplicate id needs its file");
        SpecificationCatalogService.Resolve(catalog, "FR-010", "LendoPlus/specs/001-library-loans/spec.md").Problem.Should().Contain("is not in");
        SpecificationCatalogService.Resolve(catalog, "FR-404", null).Problem.Should().Contain("not a requirement");
        SpecificationCatalogService.Resolve(catalog, "UTF-8", null).Problem.Should().Contain("not a requirement");
        SpecificationCatalogService.Resolve(catalog, "FR-001", "spec.md").Entry.Should().BeNull("a bare file name never matches across folders");
    }

    [Fact]
    public async Task Recorded_links_are_checked_honestly_and_never_changed()
    {
        var empty = SpecificationCatalogService.Check("import:none", null, [new("FR-001", "specs/001/spec.md")]);
        empty.Checks.Single().State.Should().Be(RequirementSourceState.NoServerCatalog);

        var v1 = (await Catalogs().CaptureAsync(ImportId, "sha-a", null, Archive(Loans), default))!;
        var linkedSha = v1.Requirements.First(r => r.RequirementId == "FR-001" && r.Path.Contains("library")).TextSha256;
        var v2 = (await Catalogs().CaptureAsync(ImportId, "sha-b", null, Archive(Loans.Replace("member card", "library card").Replace("- **FR-2**: The system MUST store every text as UTF-8 and reject other encodings.", "")), default))!;

        var result = SpecificationCatalogService.Check(Project, v2, [
            new("FR-001", "LendoPlus/specs/001-library-loans/spec.md", linkedSha),
            new("FR-010", "LendoPlus/specs/002-søknad/spec.md", v1.Requirements.Single(r => r.RequirementId == "FR-010").TextSha256),
            new("FR-002", "LendoPlus/specs/001-library-loans/spec.md", "x"),
            new("FR-001", "LendoPlus/specs/002-søknad/spec.md", null),
        ]);
        result.CatalogRevision.Should().Be(2);
        result.Checks.Select(c => c.State).Should().Equal(RequirementSourceState.ChangedSinceLinked, RequirementSourceState.Current,
            RequirementSourceState.NotInCurrentSpecification, RequirementSourceState.ChangedSinceLinked);
        result.Checks[3].Detail.Should().Contain("does not record the wording", "a legacy link without a wording hash is not called current");
    }

    // ── Links use the server catalog ────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class Journeys(string name)
    {
        public IntegrationJourneysDbContext Db() => new(new DbContextOptionsBuilder<IntegrationJourneysDbContext>().UseInMemoryDatabase(name).Options);
    }

    private static async Task<string> Declare(Journeys j, string project) =>
        (await new DeclaredIntegrationJourneyStore(j.Db()).DeclareAsync(new DeclaredIntegrationJourneyRequest
            { ProjectKey = project, DisplayName = "Loan flow", Components = ["Web", "Loans.Api"], DeclaredBy = "Kari" }, Now)).Journey!.JourneyId;

    private static ProjectRequirementCatalog BrowserList(string project, params (string Id, string Path)[] items) =>
        new() { ProjectKey = project, Requirements = [.. items.Select(i => new ProjectRequirementOption(i.Id, "browser title", i.Path))] };

    [Fact]
    public async Task A_journey_link_is_decided_by_the_server_catalog_not_by_the_list_the_browser_sends()
    {
        await Catalogs().CaptureAsync(ImportId, "sha-a", null, Archive(Loans), default);
        var j = new Journeys($"spec-links-{Guid.NewGuid():N}");
        var journey = await Declare(j, Project);
        var store = new JourneyRequirementLinkStore(j.Db(), Catalogs());

        var ok = await store.LinkAsync(Project, journey, new("FR-001", "specs/001-library-loans/spec.md", BrowserList(Project), "Kari"), "local-user", Now, default);
        ok.Error.Should().BeNull();
        ok.Link!.Should().Match<JourneyRequirementLink>(l => l.CheckedAgainst == RequirementLinkProvenance.ServerSpecification && l.CatalogRevision == 1
            && l.Trust == JourneyRequirementRules.ServerCatalogTrust && l.Title.EndsWith("The system MUST register a loan for a valid book number and member card.")
            && l.SpecificationPath == "LendoPlus/specs/001-library-loans/spec.md" && l.RequirementTextSha256 != null);

        var forged = await store.LinkAsync(Project, journey, new("FR-777", "specs/001-library-loans/spec.md",
            BrowserList(Project, ("FR-777", "specs/001-library-loans/spec.md")), "Kari"), "local-user", Now, default);
        forged.Status.Should().Be(400, "the browser listing FR-777 does not make it a requirement");
        forged.Error.Should().Contain("not a requirement of this project's imported specification");

        var ambiguous = await store.LinkAsync(Project, journey, new("FR-001", "", BrowserList(Project), "Kari"), "local-user", Now, default);
        ambiguous.Status.Should().Be(400);
        (await store.LinkAsync(Project, journey, new("FR-002", "LendoPlus/specs/001-library-loans/spec.md", BrowserList("import:someone-else"), "Kari"), "local-user", Now, default))
            .Status.Should().Be(400, "another project's catalog is refused");
    }

    [Fact]
    public async Task A_project_without_a_server_catalog_keeps_the_browser_check_and_says_so()
    {
        var j = new Journeys($"spec-legacy-{Guid.NewGuid():N}");
        var journey = await Declare(j, "import:legacy");
        var link = await new JourneyRequirementLinkStore(j.Db(), Catalogs()).LinkAsync("import:legacy", journey,
            new("FR-001", "specs/001/spec.md", BrowserList("import:legacy", ("FR-001", "specs/001/spec.md")), "Kari"), "local-user", Now, default);

        link.Link!.Should().Match<JourneyRequirementLink>(l => l.CheckedAgainst == RequirementLinkProvenance.BrowserSpecification
            && l.Trust == JourneyRequirementRules.BrowserCatalogTrust && l.CatalogRevision == null);
    }
}
