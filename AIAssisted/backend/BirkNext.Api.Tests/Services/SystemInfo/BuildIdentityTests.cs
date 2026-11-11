using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using BirkNext.Api.Data;
using BirkNext.Api.Services.SystemInfo;
using BirkNext.BuildInfo;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Api.Tests.Services.SystemInfo;

/// <summary>
/// BirkNext's own build identity. The real BirkNext.Api assembly is checked against versioning/BirkNext.Version.props read here as text
/// (independent of MSBuild); CI, release, inconsistent and identity-less builds use synthetic assemblies built in memory, so every expected
/// value comes from the test, never from the code under test. Also: frontend/backend compatibility, the packaged-manifest check, migration
/// state and the build-info service.
/// </summary>
public sealed class BuildIdentityTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static string PropsVersion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "versioning", "BirkNext.Version.props"))) dir = dir.Parent;
        dir.Should().NotBeNull("the tests run inside the AIAssisted tree");
        return Regex.Match(File.ReadAllText(Path.Combine(dir!.FullName, "versioning", "BirkNext.Version.props")), "<BirkNextApplicationVersion>([^<]+)</BirkNextApplicationVersion>").Groups[1].Value;
    }

    /// <summary>An in-memory assembly carrying exactly the given informational version and metadata.</summary>
    private static Assembly Synthetic(string informational, params (string Key, string Value)[] metadata)
    {
        var builder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Synthetic" + Guid.NewGuid().ToString("N")) { Version = new Version(1, 4, 0, 0) }, AssemblyBuilderAccess.Run);
        builder.SetCustomAttribute(new CustomAttributeBuilder(typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [informational]));
        builder.SetCustomAttribute(new CustomAttributeBuilder(typeof(AssemblyFileVersionAttribute).GetConstructor([typeof(string)])!, ["1.4.0.152"]));
        foreach (var (key, value) in metadata)
            builder.SetCustomAttribute(new CustomAttributeBuilder(typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!, [key, value]));
        return builder;
    }

    private static Assembly Ci(string informational = "1.4.0-ci.152+" + Sha, string channel = "CI", string buildVersion = "1.4.0-ci.152", string commit = Sha) => Synthetic(informational,
        ("BirkNextIdentitySchema", "birknext-build-identity/1"), ("BirkNextBuildChannel", channel), ("BirkNextBuildVersion", buildVersion), ("BirkNextBuildId", "152"),
        ("BirkNextBuildNumber", "20261111.1"), ("SourceCommit", commit), ("BirkNextSourceBranch", "feature-test"), ("BirkNextSourceDirty", "false"),
        ("BirkNextBuildTimestamp", "2026-11-11T09:30:00Z"), ("BirkNextBuildConfiguration", "Release"));

    [Fact]
    public void The_running_backend_carries_the_application_version_from_the_one_version_file()
    {
        var expected = PropsVersion();
        var id = BuildInfoService.Current;
        id.ApplicationVersion.Should().Be(expected);
        id.BuildVersion.Should().StartWith(expected + "-", "a developer/test build is never the bare release version");
        id.Channel.Should().BeOneOf(BuildChannel.Local, BuildChannel.Unknown);
        id.BuildId.Should().BeNull("no CI build number is invented for a local build");
        id.InformationalVersion.Should().Be($"{id.BuildVersion}+{id.SourceCommit ?? "unknown"}{(id.SourceDirty == true ? ".dirty" : "")}");
        var parts = expected.Split('.');
        id.AssemblyVersion.Should().Be($"{parts[0]}.{parts[1]}.0.0", "binding stays stable across patches and builds");
        id.FileVersion.Should().Be($"{expected}.0");
        id.Problems.Should().BeEmpty();
        id.Provenance.Should().Be(id.SourceCommit is null ? ProvenanceStatus.Unknown : ProvenanceStatus.Partial);
    }

    [Fact]
    public void A_ci_build_identity_is_read_completely()
    {
        var id = BuildIdentityReader.FromAssembly(Ci(), "Synthetic");
        id.Should().Match<BuildIdentity>(i => i.Channel == BuildChannel.CI && i.ApplicationVersion == "1.4.0" && i.BuildVersion == "1.4.0-ci.152" && i.BuildId == "152"
            && i.BuildNumber == "20261111.1" && i.SourceCommit == Sha && i.SourceBranch == "feature-test" && i.SourceDirty == false && i.FileVersion == "1.4.0.152"
            && i.Provenance == ProvenanceStatus.Complete && i.Problems.Count == 0);
        id.ShortCommit.Should().Be(Sha[..12]);
    }

    [Fact]
    public void Contradictions_and_missing_identity_are_reported_never_smoothed_over()
    {
        BuildIdentityReader.FromAssembly(Ci(informational: "1.4.0-ci.999+" + Sha), "x").Provenance.Should().Be(ProvenanceStatus.Inconsistent);
        BuildIdentityReader.FromAssembly(Ci(commit: "abc123", informational: "1.4.0-ci.152+abc123"), "x").Problems.Should().Contain(p => p.Contains("not a full commit"));
        var none = BuildIdentityReader.FromAssembly(Synthetic("1.0.0"), "x");
        none.Should().Match<BuildIdentity>(i => i.Channel == BuildChannel.Unknown && i.Provenance == ProvenanceStatus.Unknown && i.SourceCommit == null && i.Problems.Count == 1);
        var release = BuildIdentityReader.FromAssembly(Synthetic("1.4.0+" + Sha, ("BirkNextIdentitySchema", "birknext-build-identity/1"), ("BirkNextBuildChannel", "Release"),
            ("BirkNextBuildVersion", "1.4.0"), ("SourceCommit", Sha), ("BirkNextSourceDirty", "false")), "x");
        release.Provenance.Should().Be(ProvenanceStatus.Inconsistent, "a release without a pipeline run id is not a complete release identity");
    }

    [Fact]
    public void Frontend_and_backend_compatibility_is_only_claimed_where_the_rules_support_it()
    {
        var backend = BuildIdentityReader.FromAssembly(Ci(), "BirkNext.Api");
        var sameBuild = BuildIdentityReader.FromAssembly(Ci(), "BirkNext.Web");
        BuildCompatibilityRules.Compare(sameBuild, backend).Status.Should().Be(ComponentCompatibility.Matched);
        var other = BuildIdentityReader.FromAssembly(Ci(informational: "1.4.0-ci.153+" + Sha, buildVersion: "1.4.0-ci.153"), "BirkNext.Web") with { BuildId = "153" };
        BuildCompatibilityRules.Compare(other, backend).Status.Should().Be(ComponentCompatibility.Mismatch);
        BuildCompatibilityRules.Compare(sameBuild with { SourceDirty = true }, backend).Status.Should().Be(ComponentCompatibility.NotAssessed, "uncommitted changes: same commit is not same source");
        BuildCompatibilityRules.Compare(sameBuild with { SourceCommit = null, Provenance = ProvenanceStatus.Unknown }, backend).Status.Should().Be(ComponentCompatibility.NotAssessed);
        new[] { sameBuild, other }.Select(f => BuildCompatibilityRules.Compare(f, backend).Status).Should().NotContain(ComponentCompatibility.CompatibleDifferentBuilds, "no rule defines it");
    }

    [Fact]
    public void A_packaged_manifest_is_compared_with_the_running_build()
    {
        var running = BuildIdentityReader.FromAssembly(Ci(), "BirkNext.Api");
        var dir = Directory.CreateTempSubdirectory("bn-manifest-");
        try
        {
            var path = Path.Combine(dir.FullName, "build-manifest.json");
            BuildInfoService.CheckManifest(running, path).Status.Should().Be(ManifestCheckStatus.NotPackaged);
            File.WriteAllText(path, $$"""{"manifestSchemaVersion":"birknext-build-manifest/1","informationalVersion":"1.4.0-ci.152+{{Sha}}","buildVersion":"1.4.0-ci.152","channel":"CI","pipelineRunId":"152","sourceCommit":"{{Sha}}"}""");
            var ok = BuildInfoService.CheckManifest(running, path);
            ok.Status.Should().Be(ManifestCheckStatus.Consistent);
            ok.Fingerprint.Should().Be(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
            File.WriteAllText(path, $$"""{"manifestSchemaVersion":"birknext-build-manifest/1","informationalVersion":"1.4.0-ci.151+{{Sha}}","buildVersion":"1.4.0-ci.151","channel":"CI","pipelineRunId":"151","sourceCommit":"{{Sha}}"}""");
            var bad = BuildInfoService.CheckManifest(running, path);
            bad.Status.Should().Be(ManifestCheckStatus.Inconsistent);
            bad.Differences.Should().Contain(d => d.StartsWith("Pipeline run: manifest 151, running 152"));
            File.WriteAllText(path, "{ not json");
            BuildInfoService.CheckManifest(running, path).Status.Should().Be(ManifestCheckStatus.Invalid);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task The_build_info_service_reports_the_running_build_and_never_migrates()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase($"bi-{Guid.NewGuid():N}"));
        await using var sp = services.BuildServiceProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["BuildInfo:ManifestPath"] = Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid().ToString("N"), "build-manifest.json") }).Build();
        var info = await new BuildInfoService(sp.GetRequiredService<IServiceScopeFactory>(), config).GetAsync(default);
        info.Backend.Should().Be(BuildInfoService.Current);
        info.Manifest.Status.Should().Be(ManifestCheckStatus.NotPackaged, "a source build has no package manifest; that is not an error");
        info.Database.Should().Contain(d => d.Context == "Application" && d.Status == SchemaCompatibility.Unknown, "the in-memory provider has no migration history");
        info.Companion.Compatibility.Should().Be(ComponentCompatibility.NotAssessed, "the Companion has no protocol version handshake");
    }
}

/// <summary>Migration state on PostgreSQL (fresh migrated temporary database): Current, then AheadOfBuild after a newer migration id is recorded.</summary>
public sealed class BuildInfoSchemaPostgresTests : IAsyncLifetime
{
    private readonly string _database = BirkNext.Api.Tests.TestInfrastructure.TestPostgres.TemporaryName("birknext_buildinfo");
    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(BirkNext.Api.Tests.TestInfrastructure.TestPostgres.Temporary(_database).ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await Execute($"CREATE DATABASE \"{_database}\"");
        await using var db = Context();
        await BirkNext.Api.Data.DatabaseMigrationLock.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        Npgsql.NpgsqlConnection.ClearAllPools();
        await Execute($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
    }

    private static async Task Execute(string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(BirkNext.Api.Tests.TestInfrastructure.TestPostgres.Admin().ConnectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Applied_versus_known_migrations_are_read_without_migrating()
    {
        await using (var db = Context())
        {
            var state = await BuildInfoService.SchemaAsync("Application", db, default);
            state.Should().Match<SchemaState>(s => s.Status == SchemaCompatibility.Current && s.Pending == 0 && s.Known == s.Applied && s.Known > 0);
            await db.Database.ExecuteSqlRawAsync("""INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('29991231000000_FromANewerBuild', '8.0.0')""");
        }
        await using (var db = Context())
            (await BuildInfoService.SchemaAsync("Application", db, default)).Status.Should().Be(SchemaCompatibility.AheadOfBuild);
    }
}
