using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BirkNext.Api.Data;
using BirkNext.Api.Services.DependencyReview;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Dependencies;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.DependencyReview;

/// <summary>
/// Source Analysis → Dependency Review: Source Analysis captures dependency evidence once per immutable snapshot; Dependency Review reviews
/// an explicit scope (one primary + zero or more related snapshots) bound to exact IDs and fingerprints, keeps per-repository provenance and
/// Renovate results, suggests related sources from explicit evidence only and never includes or re-binds anything silently.
/// Generic fixture: AppRepo references the Shared.Common package (published by the Shared.Common repository) and an external project.
/// </summary>
public sealed class DependencyReviewSourceScopeTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    public void Dispose() => _db.Dispose();

    private static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files) { using var w = new StreamWriter(zip.CreateEntry(path).Open()); w.Write(content); }
        return buffer.ToArray();
    }

    private static string Csproj(string extra, params (string Id, string Version)[] refs) =>
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework>" + extra + "</PropertyGroup>\n  <ItemGroup>\n"
        + string.Join("\n", refs.Select(r => $"    <PackageReference Include=\"{r.Id}\" Version=\"{r.Version}\" />")) + "\n  </ItemGroup>\n</Project>";

    private const string RenovateA = "{\"extends\":[\"config:recommended\"],\"vulnerabilityAlerts\":{\"enabled\":true},\"hostRules\":[{\"hostType\":\"nuget\",\"matchHost\":\"pkgs.example.test\",\"token\":\"SECRET_SENTINEL_TOKEN\"}]}";
    private const string RenovateB = "{\"packageRules\":[{\"matchPackageNames\":[\"Package.X\"],\"enabled\":false}]}";

    internal static byte[] AppRepo(string packageXVersion = "8.1.0") => Zip(
        ("AppRepo/AppRepo.sln", "Microsoft Visual Studio Solution File"),
        ("AppRepo/renovate.json", RenovateA),
        ("AppRepo/src/App.Api/App.Api.csproj", Csproj("", ("Package.X", packageXVersion), ("Shared.Common", "2.1.0"), ("Newtonsoft.Json", "13.0.3")).Replace("</Project>", "  <ItemGroup><ProjectReference Include=\"..\\..\\..\\External.Lib\\External.Lib.csproj\" /></ItemGroup>\n</Project>")),
        ("AppRepo/src/App.Worker/App.Worker.csproj", Csproj("", ("Shared.Common", "2.1.0"))),
        ("AppRepo/src/App.Api/Program.cs", "var builder = WebApplication.CreateBuilder(args);"));

    internal static byte[] SharedCommon() => Zip(
        ("Shared.Common.sln", "Microsoft Visual Studio Solution File"),
        ("renovate.json", RenovateB),
        ("Directory.Build.props", "<Project><PropertyGroup><PackageId>$(MSBuildProjectName)</PackageId><Version>2.0.0</Version></PropertyGroup></Project>"),
        ("src/Shared.Common/Shared.Common.csproj", Csproj("", ("Package.X", "7.4.0"), ("Package.Y", "1.0.0"), ("Newtonsoft.Json", "13.0.3"))),
        ("tests/Shared.Common.Tests/Shared.Common.Tests.csproj", Csproj("<IsPackable>false</IsPackable>", ("xunit", "2.9.0"))));

    private static IqrSourceSnapshot Snapshot(string archive, byte[] bytes, DateTimeOffset at)
    {
        var identity = SourceDependencyEvidenceExtractor.Identity(archive, bytes);
        return new IqrSourceSnapshot
        {
            Id = Guid.NewGuid(), IntegrationId = "source-analysis", AnalyzedAt = at, Status = SourceAnalysisStatus.Partial,
            Archive = new SourceArchive(archive, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), 5), Repository = identity,
            DependencyEvidence = SourceDependencyEvidenceExtractor.Extract(identity.DisplayName, bytes, archive).Evidence,
        };
    }

    private async Task Store(params IqrSourceSnapshot[] snapshots)
    {
        foreach (var s in snapshots)
            _db.IqrSourceSnapshots.Add(new() { Id = s.Id, EnvironmentId = "dev", IntegrationId = s.IntegrationId, AnalyzedAt = s.AnalyzedAt, EvidenceJson = System.Text.Json.JsonSerializer.Serialize(s, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) });
        await _db.SaveChangesAsync();
    }

    private DependencyReviewSourceScopeService Service() => new(new ReviewSourceEvidenceProvider(new IqrSourceStore(_db)), new DependencyReviewService(_db, NullLogger<DependencyReviewService>.Instance));

    // ── Source Analysis evidence ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Identity_ComesFromTheRootSolution_ElseTheArchiveName()
    {
        SourceDependencyEvidenceExtractor.Identity("upload.zip", AppRepo()).Should().Be(new SourceRepositoryIdentity("apprepo", "AppRepo", "Root solution file AppRepo.sln"));
        SourceDependencyEvidenceExtractor.Identity("Shared.Common (1).zip", SharedCommon()).DisplayName.Should().Be("Shared.Common");
        var noSolution = Zip(("A/a.csproj", Csproj("")), ("B/b.csproj", Csproj("")));
        SourceDependencyEvidenceExtractor.Identity("Orders _2_.zip", noSolution).Should().Be(new SourceRepositoryIdentity("orders", "Orders", "Archive file name"));
        SourceDependencyEvidenceExtractor.ArchiveRepositoryName("M2LB (2).zip").Should().Be("M2LB");
    }

    [Fact]
    public void Evidence_IsStructuredAndSecretSafe_WithProducedPackagesAndExternalReferences()
    {
        var e = SourceDependencyEvidenceExtractor.Extract("AppRepo", AppRepo()).Evidence!;
        e.Dependencies.Select(d => (d.PackageName, d.CurrentValue)).Should().Contain([("Package.X", "8.1.0"), ("Shared.Common", "2.1.0"), ("Newtonsoft.Json", "13.0.3")]);
        e.Dependencies.Should().OnlyContain(d => d.Repository == "AppRepo" && d.OwnerFile.EndsWith(".csproj"));
        var renovate = e.RenovateFiles.Single();
        renovate.Content.Should().NotContain("SECRET_SENTINEL_TOKEN", "Renovate configs are stored redacted");
        renovate.Sha256.Should().Be(RenovateConfig.Hash(RenovateA), "the hash is the original file's");
        e.ExternalProjectReferences.Single().ProjectFileName.Should().Be("External.Lib.csproj");
        e.PublishedPackages.Should().BeEmpty("AppRepo declares no package identity");

        var shared = SourceDependencyEvidenceExtractor.Extract("Shared.Common", SharedCommon()).Evidence!;
        shared.PublishedPackages.Should().ContainSingle().Which.Should().Be(new SourcePublishedPackage("Shared.Common", "src/Shared.Common/Shared.Common.csproj", "2.0.0"), "IsPackable=false projects publish nothing");
    }

    [Fact]
    public async Task SourceAnalysisCapturesRepositoryAndDependencyEvidenceOnTheSnapshot()
    {
        var (snapshot, error) = await new IqrSourceStore(_db).AnalyzeAsync("dev", "source-analysis", "AppRepo.zip", AppRepo());
        error.Should().BeNull();
        snapshot!.Repository!.DisplayName.Should().Be("AppRepo");
        snapshot.DependencyEvidence!.Dependencies.Should().NotBeEmpty();
        (await new IqrSourceStore(_db).ListAsync("dev")).Single().DependencyEvidence!.RenovateFiles.Should().ContainSingle("persisted with the snapshot");
    }

    // ── Related sources ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RelatedSources_AreSuggestedFromExactEvidence_NeverIncluded_AndMissingOrAmbiguousStayVisible()
    {
        var app = Snapshot("AppRepo.zip", AppRepo(), DateTimeOffset.Parse("2026-10-01T10:00:00Z"));
        var shared = Snapshot("Shared.Common.zip", SharedCommon(), DateTimeOffset.Parse("2026-10-01T09:00:00Z"));
        var candidates = DependencyReviewSourceScopeService.Candidates(app, [app, shared]);

        var common = candidates.Single(c => c.Repository == "Shared.Common");
        (common.State, common.Confidence).Should().Be((RelatedSourceState.SnapshotAvailable, "Suggested"));
        common.MatchingSnapshotIds.Should().Equal(shared.Id);
        common.Evidence.Single().Should().Match<RelatedSourceEvidence>(e => e.Kind == "PackageReference" && e.Projects == 2 && e.Detail.Contains("exact package ID"));
        common.Reason.Should().Be("Referenced by 2 project(s) in the primary source.");
        var external = candidates.Single(c => c.Repository == "External.Lib");
        external.State.Should().Be(RelatedSourceState.SnapshotUnavailable);
        external.MatchingSnapshotIds.Should().BeEmpty();

        var other = Snapshot("Other.zip", Zip(("Other.sln", ""), ("Directory.Build.props", "<Project><PropertyGroup><PackageId>Shared.Common</PackageId></PropertyGroup></Project>"), ("src/A/A.csproj", Csproj(""))), DateTimeOffset.Parse("2026-10-01T08:00:00Z"));
        DependencyReviewSourceScopeService.Candidates(app, [app, shared, other]).Where(c => c.Repository is "Shared.Common" or "Other").Should().OnlyContain(c => c.State == RelatedSourceState.NeedsReview, "two repositories produce the same package: no guess");

        var namedOnly = Snapshot("Shared.Common.zip", Zip(("Shared.Common.sln", ""), ("src/Shared.Common.Logging/Shared.Common.Logging.csproj", "<Project><PropertyGroup><PackageId>$(MSBuildProjectName)</PackageId></PropertyGroup></Project>")), DateTimeOffset.Parse("2026-09-01T08:00:00Z"));
        DependencyReviewSourceScopeService.Candidates(app, [app, namedOnly]).Single(c => c.Repository == "Shared.Common").State.Should().Be(RelatedSourceState.NeedsReview, "the name alone is not a match");
        DependencyReviewSourceScopeService.Candidates(app with { DependencyEvidence = null }, [app, shared]).Should().BeEmpty("no evidence, no suggestion");
    }

    // ── Scope validation and runs ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Validation_RequiresAPrimary_ResolvableUniqueSnapshots_OnePerRepository_AndEvidence()
    {
        var app = Snapshot("AppRepo.zip", AppRepo(), DateTimeOffset.UtcNow);
        var newer = Snapshot("AppRepo.zip", AppRepo("8.2.0"), DateTimeOffset.UtcNow.AddMinutes(1));
        var legacy = app with { Id = Guid.NewGuid(), DependencyEvidence = null };
        IReadOnlyList<IqrSourceSnapshot> all = [app, newer, legacy];
        DependencyReviewSourceScopeService.Validate(new() { }, all).Error.Should().Be("Choose a primary source snapshot.");
        DependencyReviewSourceScopeService.Validate(new() { PrimarySnapshotId = app.Id, RelatedSnapshotIds = [app.Id] }, all).Error.Should().Contain("more than once");
        DependencyReviewSourceScopeService.Validate(new() { PrimarySnapshotId = Guid.NewGuid() }, all).Error.Should().Contain("unavailable").And.Contain("nothing is substituted");
        DependencyReviewSourceScopeService.Validate(new() { PrimarySnapshotId = legacy.Id }, all).Error.Should().Contain("Analyze the archive again in Source Analysis");
        DependencyReviewSourceScopeService.Validate(new() { PrimarySnapshotId = app.Id, RelatedSnapshotIds = [newer.Id] }, all).Error.Should().Contain("one snapshot per repository");
        DependencyReviewSourceScopeService.Validate(new() { PrimarySnapshotId = app.Id }, all).Selected!.Single().Id.Should().Be(app.Id);
    }

    [Fact]
    public async Task Run_BindsExactSnapshots_KeepsProvenance_PerRepositoryRenovate_AndNeutralCrossSourceObservations()
    {
        var app = Snapshot("AppRepo.zip", AppRepo(), DateTimeOffset.Parse("2026-10-01T10:00:00Z"));
        var shared = Snapshot("Shared.Common.zip", SharedCommon(), DateTimeOffset.Parse("2026-10-01T09:00:00Z"));
        await Store(app, shared);

        var (result, error) = await Service().RunAsync(new() { EnvironmentId = "dev", PrimarySnapshotId = app.Id, RelatedSnapshotIds = [shared.Id] });

        error.Should().BeNull();
        result!.SourceScope!.Primary.Should().Match<SourceScopeEntry>(p => p.SnapshotId == app.Id && p.Repository == "AppRepo" && p.Fingerprint == app.Archive.Sha256);
        result.SourceScope.Related.Single().Should().Match<SourceScopeEntry>(r => r.SnapshotId == shared.Id && r.Fingerprint == shared.Archive.Sha256);
        result.Repositories.Select(r => (r.Repository, r.ArchiveSha256)).Should().Equal(new[] { ("AppRepo", app.Archive.Sha256), ("Shared.Common", shared.Archive.Sha256) }, "one review per snapshot — never merged");
        result.Repositories.Single(r => r.Repository == "AppRepo").Dependencies.Should().OnlyContain(d => d.Repository == "AppRepo");
        result.Repositories.Select(r => r.ConfigHash).Should().Equal(new[] { RenovateConfig.Hash(RenovateA), RenovateConfig.Hash(RenovateB) }, "Renovate is reviewed per repository, by its own file");
        result.Repositories.Single(r => r.Repository == "AppRepo").NormalizedConfig.Should().NotContain("SECRET_SENTINEL_TOKEN");

        var x = result.CrossSource.Single(o => o.PackageName == "Package.X");
        x.Kind.Should().Be("Version difference observed");
        x.Values.Select(v => (v.Repository, v.Value)).Should().Equal(("AppRepo", "8.1.0"), ("Shared.Common", "7.4.0"));
        x.Values[0].Files.Should().Contain("src/App.Api/App.Api.csproj");
        result.CrossSource.Should().NotContain(o => o.PackageName == "Newtonsoft.Json", "the same version in both sources is not a difference");
        result.CrossSource.Select(o => o.Kind).Should().NotContain(k => k.Contains("fail", StringComparison.OrdinalIgnoreCase) || k.Contains("vulnerab", StringComparison.OrdinalIgnoreCase) || k.Contains("incompat", StringComparison.OrdinalIgnoreCase));
        result.SourceRelationships.Single().Should().Be(result.SourceRelationships.Single() with { FromRepository = "AppRepo", PackageName = "Shared.Common", ToRepository = "Shared.Common", PublishedVersion = "2.0.0", Projects = 2 });
        result.SourceRelationships.Single().ReferencedValues.Should().Equal("2.1.0");
        result.SourceScope.ExcludedSuggestions.Should().Equal("External.Lib");
        result.SourceScope.Limitations.Single().Should().Contain("External.Lib").And.Contain("no analyzed snapshot");
        result.Categories.Single(c => c.Name == "Runtime automation").State.Should().Be(ReviewCategoryState.NotAssessed, "Renovate policy is not a runtime or security result");
    }

    [Fact]
    public async Task ContinuingWithoutASuggestedSource_IsRecordedAsAScopeLimitation()
    {
        var app = Snapshot("AppRepo.zip", AppRepo(), DateTimeOffset.UtcNow);
        var shared = Snapshot("Shared.Common.zip", SharedCommon(), DateTimeOffset.UtcNow.AddMinutes(-5));
        await Store(app, shared);
        var (result, _) = await Service().RunAsync(new() { EnvironmentId = "dev", PrimarySnapshotId = app.Id, ExcludedSuggestions = ["Shared.Common"] });
        result!.SourceScope!.Related.Should().BeEmpty();
        result.SourceScope.ExcludedSuggestions.Should().Equal("External.Lib", "Shared.Common");
        result.SourceScope.Limitations.Should().Contain(l => l.Contains("Shared.Common") && l.Contains("not included"));
        result.CrossSource.Should().BeEmpty("a single source has nothing to compare");
    }

    [Fact]
    public async Task ARunStaysBoundToItsSnapshots_WhenNewerSnapshotsArrive()
    {
        var a = Snapshot("AppRepo.zip", AppRepo(), DateTimeOffset.Parse("2026-10-01T10:00:00Z"));
        await Store(a);
        var (run, _) = await Service().RunAsync(new() { EnvironmentId = "dev", PrimarySnapshotId = a.Id });
        var b = Snapshot("AppRepo.zip", AppRepo("9.0.0"), DateTimeOffset.Parse("2026-10-02T10:00:00Z"));
        await Store(b);

        var stored = await new DependencyReviewService(_db, NullLogger<DependencyReviewService>.Instance).GetAsync(run!.RunId);
        stored!.SourceScope!.Primary.SnapshotId.Should().Be(a.Id, "no silent rebind to the newer snapshot");
        stored.Repositories.Single().Dependencies.Single(d => d.PackageName == "Package.X").CurrentValue.Should().Be("8.1.0");
        var options = await Service().OptionsAsync("dev", null);
        options.Snapshots.Single(s => s.SnapshotId == a.Id).Latest.Should().BeFalse();
        options.Snapshots.Single(s => s.SnapshotId == b.Id).Latest.Should().BeTrue("the newer snapshot is offered, not applied");
        options.Snapshots.Should().HaveCount(2, "each snapshot keeps its own history");
    }

    [Fact]
    public async Task LegacyArchiveRuns_HaveNoSourceScope_AndOldSnapshotsAreDescribedHonestly()
    {
        var (legacy, _) = await new DependencyReviewService(_db, NullLogger<DependencyReviewService>.Instance).RunAsync("", [("AppRepo.zip", AppRepo())], []);
        legacy!.SourceScope.Should().BeNull("legacy runs are never given a fake snapshot id");
        var old = new IqrSourceSnapshot { Id = Guid.NewGuid(), Archive = new SourceArchive("M2LB _2_.zip", "c850a1b2ffff", 3), AnalyzedAt = DateTimeOffset.UtcNow, Status = SourceAnalysisStatus.Partial };
        var described = DependencyReviewSourceScopeService.Describe([old]).Single();
        (described.Repository, described.HasConsumerEvidence, described.SourceStatus).Should().Be(("M2LB", false, "Partial"));
        described.ConsumerEvidenceNote.Should().Contain("Analyze the archive again in Source Analysis");
    }
}
