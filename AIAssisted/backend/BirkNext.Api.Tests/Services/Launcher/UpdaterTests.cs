extern alias UpdaterProject;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using UpdaterProject::BirkNext.Updater;
using SingleInstance = UpdaterProject::BirkNext.Launcher.SingleInstance;
using Path = System.IO.Path;

namespace BirkNext.Api.Tests.Services.Launcher;

/// <summary>
/// Update-BirkNext.exe without real BirkNext processes: version rules, the pipeline-origin trust policy, ZIP safety and integrity (every file
/// against the package manifest), and whole updates on disposable installation and data folders — first install, upgrade with and without
/// database changes, refusals, a running instance stopped through its own stop request (never killed), interrupted updates and rollback.
/// Packages are synthetic; the real packaged executables are exercised by the Windows acceptance run.
/// </summary>
public sealed class UpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-updater-tests", Guid.NewGuid().ToString("N"));
    public UpdaterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string AzureDevOps = "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://dev.azure.com/bufdir/BirkNext/_build/results?buildId=1002\r\nHostUrl=https://artprodcus3.artifacts.visualstudio.com/x/y/z\r\n";

    // ── Synthetic packages ──────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Pkg(string BuildVersion = "1.0.0-ci.1002", string App = "1.0.0", string Channel = "CI", string? RunId = "1002", string[]? Migrations = null,
        int? UpdateContract = 1, string Product = "BirkNext", string Architecture = "x64", string Prefix = "BirkNext-Tester/", bool OmitUpdater = false,
        Action<Dictionary<string, byte[]>>? Tamper = null, Action<ZipArchive>? Extra = null, bool ListEverything = true);

    private static readonly string[] BaseMigrations = ["20261101_Initial", "20261110_Sources"];

    private string Build(Pkg p, string? name = null, string? origin = AzureDevOps)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Start-BirkNext.exe"] = Encoding.UTF8.GetBytes("launcher " + p.BuildVersion),
            ["AIAssisted/backend/BirkNext.Api.exe"] = Encoding.UTF8.GetBytes("backend " + p.BuildVersion),
            ["AIAssisted/frontend/wwwroot/index.html"] = "<html></html>"u8.ToArray(),
            ["docs/readme.txt"] = "readme"u8.ToArray(),
            ["build-manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                product = p.Product, applicationVersion = p.App, buildVersion = p.BuildVersion, channel = p.Channel, pipelineRunId = p.RunId,
                informationalVersion = $"{p.BuildVersion}+{Commit}", sourceCommit = Commit,
                distribution = new { kind = "WindowsTesterPackage", runtimeProfile = "TesterNative", architecture = p.Architecture },
            }),
            ["config/runtime-manifest.json"] = p.UpdateContract is { } c
                ? JsonSerializer.SerializeToUtf8Bytes(new { format = 1, updateContract = c, distribution = "WindowsTesterPackage" })
                : JsonSerializer.SerializeToUtf8Bytes(new { format = 1, distribution = "WindowsTesterPackage" }),
        };
        if (!p.OmitUpdater) files["Update-BirkNext.exe"] = Encoding.UTF8.GetBytes("updater " + p.BuildVersion);
        var listed = files.Where(f => p.ListEverything || f.Key != "docs/readme.txt")
            .Select(f => new { path = f.Key, sha256 = Convert.ToHexString(SHA256.HashData(f.Value)).ToLowerInvariant() }).ToList();
        p.Tamper?.Invoke(files); // after hashing: a changed file no longer matches the list
        files["package-manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = 2, sourceCommit = Commit, migrations = (p.Migrations ?? BaseMigrations).Select(id => new { folder = "Data/Migrations", id }),
            files = listed.Select(l => l.path), fileHashes = listed,
        });
        var zip = Path.Combine(_dir, name ?? $"BirkNext-Tester-Windows-x64-{p.BuildVersion}-{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (path, bytes) in files)
            {
                using var s = archive.CreateEntry(p.Prefix + path).Open();
                s.Write(bytes);
            }
            p.Extra?.Invoke(archive);
        }
        if (origin is not null) File.WriteAllText(zip + ":Zone.Identifier", origin);
        return zip;
    }

    private (InstallationStore Store, InstanceControl Instance, UpdateEngine Engine) Updater(string name = "a")
    {
        var store = new InstallationStore(Path.Combine(_dir, name, "Installations"));
        var instance = new InstanceControl(Path.Combine(_dir, name, "TesterNative"));
        return (store, instance, new UpdateEngine(store, instance));
    }

    private sealed class Progress : IUpdateProgress
    {
        public List<string> Phases { get; } = [];
        public void Phase(string text) => Phases.Add(text);
        public void Fraction(int done, int total) { }
    }

    private static Task<UpdateResult> Install(UpdateEngine engine, string zip) =>
        engine.ApplyAsync(engine.Plan(zip), new Progress(), customDataRoot: true, startAfter: false, noBrowser: true, default);

    // ── Versions ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("1.0.0", "CI", "1.0.0-ci.100", "1.0.0", "CI", "1.0.0-ci.200", UpgradePath.Upgrade)]
    [InlineData("1.0.0", "CI", "1.0.0-ci.200", "1.0.0", "CI", "1.0.0-ci.100", UpgradePath.Downgrade)]
    [InlineData("1.0.0", "CI", "1.0.0-ci.200", "1.1.0", "CI", "1.1.0-ci.150", UpgradePath.Upgrade)]
    [InlineData("1.1.0", "CI", "1.1.0-ci.150", "1.0.0", "CI", "1.0.0-ci.200", UpgradePath.Downgrade)]
    [InlineData("1.0.0", "CI", "1.0.0-ci.100", "1.0.0", "Local", "1.0.0-local", UpgradePath.NotComparable)]
    [InlineData("1.0.0", "Local", "1.0.0-local", "1.0.0", "CI", "1.0.0-ci.100", UpgradePath.NotComparable)]
    [InlineData("1.0.0", "CI", "1.0.0-ci.100", "1.0.0", "Release", "1.0.0", UpgradePath.NotComparable)]
    public void Versions_are_ordered_by_application_version_then_pipeline_run_never_by_name(string a1, string c1, string b1, string a2, string c2, string b2, UpgradePath expected)
    {
        var installed = PackageVersion.Parse(a1, c1, b1, b1 + "+" + Commit, Commit)!;
        var candidate = PackageVersion.Parse(a2, c2, b2, b2 + "+x", Commit)!;
        VersionRules.Compare(installed, candidate).Should().Be(expected);
        VersionRules.Compare(null, candidate).Should().Be(UpgradePath.FirstInstall);
        VersionRules.Compare(installed, installed).Should().Be(UpgradePath.SameBuild);
    }

    [Fact]
    public void Release_builds_are_ordered_by_their_pipeline_run_id_and_invalid_versions_are_refused()
    {
        var older = PackageVersion.Parse("1.0.0", "Release", "1.0.0", "1.0.0+a", Commit, "5000")!;
        var newer = PackageVersion.Parse("1.0.0", "CI", "1.0.0-ci.5001", "1.0.0-ci.5001+b", Commit, "5001")!;
        VersionRules.Compare(older, newer).Should().Be(UpgradePath.Upgrade);
        PackageVersion.Parse("1.0", "CI", "1.0-ci.1", null, null).Should().BeNull();
        PackageVersion.Parse("1.0.0", "CI", "", null, null).Should().BeNull();
        PackageVersion.FromInformational($"1.0.0-ci.416921+{Commit}").Should().Match<PackageVersion>(v => v.Channel == "CI" && v.BuildId == 416921 && v.SourceCommit == Commit);
        PackageVersion.FromInformational("1.0.0-local+abc.dirty")!.Channel.Should().Be("Local");
        PackageVersion.Parse("1.0.0", "CI", "1.0.0-ci.1", null, null)!.FolderName.Should().Be("1.0.0-ci.1");
    }

    // ── Trust policy ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, null, TrustVerdict.NoOrigin)]
    [InlineData("https://dev.azure.com/bufdir/BirkNext/_build/results?buildId=1", null, TrustVerdict.PipelineOrigin)]
    [InlineData(null, "https://artprodcus3.artifacts.visualstudio.com/a/b", TrustVerdict.PipelineOrigin)]
    [InlineData("https://evil.example/dev.azure.com/", "https://evilvisualstudio.com/x", TrustVerdict.OtherOrigin)]
    [InlineData("http://dev.azure.com/bufdir/", null, TrustVerdict.OtherOrigin)]
    [InlineData("https://dev.azure.com.evil.example/bufdir/", null, TrustVerdict.OtherOrigin)]
    public void Only_a_download_from_azure_devops_is_trusted(string? referrer, string? host, TrustVerdict expected) =>
        TrustPolicy.Default.Evaluate(referrer is null && host is null ? null : new DownloadOrigin(3, host, referrer)).Verdict.Should().Be(expected);

    [Fact]
    public void An_organization_list_narrows_the_trusted_pipelines()
    {
        var policy = TrustPolicy.Default with { Organizations = ["bufdir"] };
        policy.Evaluate(new DownloadOrigin(3, null, "https://dev.azure.com/bufdir/p/_build")).Verdict.Should().Be(TrustVerdict.PipelineOrigin);
        policy.Evaluate(new DownloadOrigin(3, null, "https://bufdir.visualstudio.com/p/_build")).Verdict.Should().Be(TrustVerdict.PipelineOrigin);
        policy.Evaluate(new DownloadOrigin(3, null, "https://dev.azure.com/someone-else/p/_build")).Verdict.Should().Be(TrustVerdict.WrongOrganization);
        policy.Evaluate(new DownloadOrigin(3, "https://artprodcus3.artifacts.visualstudio.com/x", null)).Verdict.Should().Be(TrustVerdict.WrongOrganization,
            "an artifact host names no organization: the referring pipeline page must");
    }

    [Fact]
    public void The_mark_of_the_web_is_read_from_the_downloaded_file()
    {
        var zip = Build(new Pkg());
        DownloadOrigin.Read(zip).Should().Match<DownloadOrigin>(o => o.ZoneId == 3 && o.ReferrerUrl!.StartsWith("https://dev.azure.com/bufdir/"));
        DownloadOrigin.Read(Build(new Pkg(), origin: null)).Should().BeNull();
    }

    // ── Package checks (nothing written) ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_valid_package_is_read_with_its_version_migrations_and_file_list()
    {
        var info = PackageInspector.Inspect(Build(new Pkg(Migrations: [.. BaseMigrations, "20261119_New"])));
        info.Prefix.Should().Be("BirkNext-Tester/");
        info.Version.Should().Match<PackageVersion>(v => v.BuildVersion == "1.0.0-ci.1002" && v.BuildId == 1002 && v.Channel == "CI");
        info.Migrations.Should().Equal([.. BaseMigrations, "20261119_New"]);
        info.FileHashes.Keys.Should().Contain(["Start-BirkNext.exe", "Update-BirkNext.exe", "build-manifest.json"]).And.NotContain("package-manifest.json");
        PackageInspector.Inspect(Build(new Pkg(Prefix: ""))).Prefix.Should().BeEmpty("a package may also sit at the archive root");
    }

    public static TheoryData<string, string> Rejections => new()
    {
        { "no-updater", "too old for the updater" },
        { "no-contract", "too old for the updater" },
        { "future-contract", "newer updater" },
        { "product", "not a BirkNext Windows tester package" },
        { "arm64", "64-bit Windows" },
        { "traversal", "leaves the package folder" },
        { "absolute", "absolute path" },
        { "device", "device name" },
        { "ads", "characters Windows does not allow" },
        { "trailing-dot", "ending in a dot or space" },
        { "duplicate", "same file twice" },
        { "symlink", "link to another location" },
        { "unlisted", "not part of this BirkNext package" },
        { "outside-prefix", "outside the BirkNext package folder" },
        { "bomb", "compressed far more" },
        { "not-zip", "not a ZIP archive" },
        { "bad-version", "version information" },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void Unsafe_or_wrong_packages_are_refused_before_anything_is_written(string kind, string reason)
    {
        string zip;
        void Add(ZipArchive a, string name, byte[]? bytes = null) { using var s = a.CreateEntry(name).Open(); s.Write(bytes ?? "x"u8.ToArray()); }
        switch (kind)
        {
            case "no-updater": zip = Build(new Pkg(OmitUpdater: true)); break;
            case "no-contract": zip = Build(new Pkg(UpdateContract: null)); break;
            case "future-contract": zip = Build(new Pkg(UpdateContract: 2)); break;
            case "product": zip = Build(new Pkg(Product: "Other")); break;
            case "arm64": zip = Build(new Pkg(Architecture: "arm64")); break;
            case "traversal": zip = Build(new Pkg(Extra: a => Add(a, "BirkNext-Tester/../../evil.dll"))); break;
            case "absolute": zip = Build(new Pkg(Extra: a => Add(a, "C:/Windows/evil.dll"))); break;
            case "device": zip = Build(new Pkg(Extra: a => Add(a, "BirkNext-Tester/docs/NUL.txt"))); break;
            case "ads": zip = Build(new Pkg(Extra: a => Add(a, "BirkNext-Tester/docs/x.txt:hidden"))); break;
            case "trailing-dot": zip = Build(new Pkg(Extra: a => Add(a, "BirkNext-Tester/docs/x."))); break;
            case "duplicate": zip = Build(new Pkg(Extra: a => Add(a, "BirkNext-Tester/START-BIRKNEXT.EXE"))); break;
            case "symlink": zip = Build(new Pkg(Extra: a => { var e = a.CreateEntry("BirkNext-Tester/docs/link"); e.ExternalAttributes = unchecked((int)(0xA1FFu << 16)); using var s = e.Open(); s.Write("/etc/passwd"u8); })); break;
            case "unlisted": zip = Build(new Pkg(ListEverything: false)); break;
            case "outside-prefix": zip = Build(new Pkg(Extra: a => Add(a, "elsewhere/x.txt"))); break;
            case "bomb": zip = Build(new Pkg(Extra: a => Add(a, "BirkNext-Tester/docs/zeros.bin", new byte[8 * 1024 * 1024]))); break;
            case "not-zip": zip = Path.Combine(_dir, "fake.zip"); File.WriteAllText(zip, "not a zip"); break;
            case "bad-version": zip = Build(new Pkg(App: "1.0")); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        var act = () => PackageInspector.Inspect(zip);
        act.Should().Throw<PackageRejectedException>().Which.Message.Should().Contain(reason);
    }

    [Fact]
    public void A_damaged_file_is_found_while_extracting_and_nothing_is_written_outside_staging()
    {
        var zip = Build(new Pkg(Tamper: f => f["AIAssisted/backend/BirkNext.Api.exe"] = "tampered"u8.ToArray()));
        var info = PackageInspector.Inspect(zip);
        var staging = Path.Combine(_dir, "staging");
        var act = () => PackageInspector.Extract(info, staging);
        act.Should().Throw<PackageRejectedException>().Which.Message.Should().Contain("checksum does not match");
        Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(staging, StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".zip"))
            .Should().BeEmpty();
    }

    // ── Whole updates on disposable folders ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task First_install_then_upgrade_keeps_the_previous_version_and_never_touches_the_data_folder()
    {
        var (store, instance, engine) = Updater();
        Directory.CreateDirectory(Path.Combine(instance.DataRoot, "app-data"));
        File.WriteAllText(Path.Combine(instance.DataRoot, "app-data", "tester-file.json"), "{\"keep\":true}");
        string DataSnapshot() => string.Join("|", Directory.EnumerateFiles(instance.DataRoot, "*", SearchOption.AllDirectories).Order().Select(f => f + ":" + File.ReadAllText(f)));
        var before = DataSnapshot();

        var first = await Install(engine, Build(new Pkg(BuildVersion: "1.0.0-ci.1001", RunId: "1001")));
        first.Ok.Should().BeTrue(first.Message);
        store.Read().Current!.BuildVersion.Should().Be("1.0.0-ci.1001");
        File.Exists(Path.Combine(store.FolderOf("1.0.0-ci.1001"), "Start-BirkNext.exe")).Should().BeTrue();

        var plan = engine.Plan(Build(new Pkg(BuildVersion: "1.0.0-ci.1002", RunId: "1002", Migrations: [.. BaseMigrations, "20261119_New"])));
        plan.Path.Should().Be(UpgradePath.Upgrade);
        plan.MigrationsAdded.Should().Equal("20261119_New");
        plan.ChangesDatabase.Should().BeTrue();
        var second = await engine.ApplyAsync(plan, new Progress(), true, false, true, default);
        second.Ok.Should().BeTrue(second.Message);
        second.Message.Should().Contain("backs up your data");

        var state = store.Read();
        (state.Current!.BuildVersion, state.Previous!.BuildVersion).Should().Be(("1.0.0-ci.1002", "1.0.0-ci.1001"));
        Directory.Exists(store.FolderOf("1.0.0-ci.1001")).Should().BeTrue("the previous version stays installed");
        store.Journal().Should().BeNull("a completed update leaves no journal");
        Directory.EnumerateDirectories(store.Root, ".staging-*").Should().BeEmpty();
        DataSnapshot().Should().Be(before, "the updater never writes to the tester's data folder");
    }

    [Fact]
    public async Task Same_older_unordered_untrusted_and_schema_losing_packages_are_refused()
    {
        var (_, instance, engine) = Updater();
        (await Install(engine, Build(new Pkg(BuildVersion: "1.0.0-ci.1002", RunId: "1002")))).Ok.Should().BeTrue();

        engine.Plan(Build(new Pkg(BuildVersion: "1.0.0-ci.1002", RunId: "1002"))).Path.Should().Be(UpgradePath.SameBuild);
        engine.Plan(Build(new Pkg(BuildVersion: "1.0.0-ci.1001", RunId: "1001"))).Blocker.Should().Contain("older");
        engine.Plan(Build(new Pkg(BuildVersion: "1.0.0-local", Channel: "Local", RunId: null))).Blocker.Should().Contain("not a pipeline build");
        engine.Plan(Build(new Pkg(BuildVersion: "1.0.0-ci.1003", RunId: "1003"), origin: null)).Blocker.Should().Contain("not downloaded from the approved Azure DevOps");
        engine.Plan(Build(new Pkg(BuildVersion: "1.0.0-ci.1003", RunId: "1003"), origin: "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://files.example/BirkNext.zip\r\n"))
            .Trust.Should().Be(TrustVerdict.OtherOrigin);
        engine.Plan(Build(new Pkg(BuildVersion: "1.0.0-ci.1003", RunId: "1003", Migrations: ["20261101_Initial"]))).Blocker.Should().Contain("database update(s) the installed BirkNext already applied");

        // Data last opened by a newer build (a package extracted by hand): never downgraded.
        Directory.CreateDirectory(Path.Combine(instance.DataRoot, "runtime-state"));
        File.WriteAllText(instance.LastRunFile, JsonSerializer.Serialize(new { build = $"1.0.0-ci.2000+{Commit}", at = DateTimeOffset.UtcNow }));
        engine.Plan(Build(new Pkg(BuildVersion: "1.0.0-ci.1003", RunId: "1003"))).Blocker.Should().Contain("last opened by 1.0.0-ci.2000");
    }

    [Fact]
    public async Task A_running_BirkNext_is_asked_to_stop_through_its_own_stop_request_and_never_killed()
    {
        var (store, instance, engine) = Updater();
        (await Install(engine, Build(new Pkg(BuildVersion: "1.0.0-ci.1001", RunId: "1001")))).Ok.Should().BeTrue();
        Directory.CreateDirectory(Path.Combine(instance.DataRoot, "runtime-state"));

        // A stand-in launcher: holds the data folder's instance lock and stops (releases it) when the stop request appears.
        using var holding = new ManualResetEventSlim();
        var launcher = new Thread(() =>
        {
            using var lockHeld = SingleInstance.TryAcquire(instance.DataRoot)!;
            holding.Set();
            while (!File.Exists(instance.StopRequestFile)) Thread.Sleep(50);
            File.Delete(instance.StopRequestFile);
        });
        launcher.Start();
        holding.Wait();
        instance.IsRunning.Should().BeTrue();

        var plan = engine.Plan(Build(new Pkg(BuildVersion: "1.0.0-ci.1002", RunId: "1002")));
        plan.BirkNextRunning.Should().BeTrue();
        var progress = new Progress();
        var result = await engine.ApplyAsync(plan, progress, true, false, true, default);
        launcher.Join();

        result.Ok.Should().BeTrue(result.Message);
        progress.Phases.Should().ContainInOrder("Installing the new version", "Stopping BirkNext", "Activating the new version");
        instance.IsRunning.Should().BeFalse();
        store.Read().Current!.BuildVersion.Should().Be("1.0.0-ci.1002");
    }

    [Fact]
    public async Task A_BirkNext_that_does_not_stop_leaves_the_installed_version_active()
    {
        var (store, instance, engine) = Updater();
        (await Install(engine, Build(new Pkg(BuildVersion: "1.0.0-ci.1001", RunId: "1001")))).Ok.Should().BeTrue();
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var stubborn = new Thread(() => { using var l = SingleInstance.TryAcquire(instance.DataRoot)!; holding.Set(); release.Wait(); });
        stubborn.Start();
        holding.Wait();
        try
        {
            var stop = await instance.StopAsync(TimeSpan.FromSeconds(1), default);
            stop.Should().BeFalse();
            File.Exists(instance.StopRequestFile).Should().BeFalse("a stop that timed out is withdrawn, so it cannot stop BirkNext later");
            store.Read().Current!.BuildVersion.Should().Be("1.0.0-ci.1001");
        }
        finally { release.Set(); stubborn.Join(); }
    }

    [Fact]
    public async Task An_update_interrupted_before_activation_is_undone_and_one_after_activation_is_finished_by_starting()
    {
        var (store, _, engine) = Updater();
        (await Install(engine, Build(new Pkg(BuildVersion: "1.0.0-ci.1001", RunId: "1001")))).Ok.Should().BeTrue();

        // Interrupted while unpacking: a half-written staging folder and a journal at Staging.
        var staging = store.NewStagingFolder();
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "half.bin"), "x");
        store.Write(new UpdateJournal("j1", UpdateStage.Staging, "1.0.0-ci.1001", "1.0.0-ci.1002", staging, null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var plan = engine.Plan(Build(new Pkg(BuildVersion: "1.0.0-ci.1002", RunId: "1002")));
        (await engine.ApplyAsync(plan, new Progress(), true, false, true, default)).Action.Should().Contain("--recover", "an unfinished update is resolved first");
        var undone = engine.Recover();
        undone.Message.Should().Contain("interrupted before it was applied").And.Contain("unchanged");
        Directory.Exists(staging).Should().BeFalse();
        store.Read().Current!.BuildVersion.Should().Be("1.0.0-ci.1001");
        store.Journal().Should().BeNull();

        // Interrupted after activation: the new version is active; recovery says to start it (its launcher backs up and migrates).
        store.Write(new UpdateJournal("j2", UpdateStage.Activated, "1.0.0-ci.1001", "1.0.0-ci.1002", null, "1.0.0-ci.1002", ["m"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        engine.Recover().Action.Should().Contain("Start BirkNext");
        store.Journal().Should().BeNull();
    }

    [Fact]
    public async Task Rollback_is_allowed_only_when_the_database_was_not_changed()
    {
        var (store, _, engine) = Updater();
        (await Install(engine, Build(new Pkg(BuildVersion: "1.0.0-ci.1001", RunId: "1001")))).Ok.Should().BeTrue();
        (await Install(engine, Build(new Pkg(BuildVersion: "1.0.0-ci.1002", RunId: "1002")))).Ok.Should().BeTrue();
        var back = await engine.RollbackAsync(default);
        back.Ok.Should().BeTrue(back.Message);
        store.Read().Current!.BuildVersion.Should().Be("1.0.0-ci.1001");

        var (store2, _, engine2) = Updater("b");
        (await Install(engine2, Build(new Pkg(BuildVersion: "1.0.0-ci.1001", RunId: "1001")))).Ok.Should().BeTrue();
        (await Install(engine2, Build(new Pkg(BuildVersion: "1.0.0-ci.1002", RunId: "1002", Migrations: [.. BaseMigrations, "20261119_New"])))).Ok.Should().BeTrue();
        var refused = await engine2.RollbackAsync(default);
        refused.Ok.Should().BeFalse();
        refused.Message.Should().Contain("updated the database");
        refused.Action.Should().Contain("pre-upgrade backup");
        store2.Read().Current!.BuildVersion.Should().Be("1.0.0-ci.1002");
    }

    [Fact]
    public async Task Repeated_upgrades_keep_only_the_current_and_previous_versions()
    {
        var (store, _, engine) = Updater();
        foreach (var run in new[] { 1001, 1002, 1003, 1004 })
            (await Install(engine, Build(new Pkg(BuildVersion: $"1.0.0-ci.{run}", RunId: run.ToString())))).Ok.Should().BeTrue();
        Directory.EnumerateDirectories(store.Root).Select(Path.GetFileName).Where(n => !n!.StartsWith('.') && n != "logs")
            .Should().BeEquivalentTo(["1.0.0-ci.1003", "1.0.0-ci.1004"]);
    }

    [Fact]
    public void The_updater_computes_the_same_instance_lock_as_the_launcher()
    {
        var data = Path.Combine(_dir, "data");
        SingleInstance.NameFor(data).Should().Be(LauncherInstanceName(data));
        SingleInstance.NameFor(data.ToUpperInvariant()).Should().Be(SingleInstance.NameFor(data), "one data folder, however it is written");
    }

    // The launcher's own derivation (TesterLauncher's original code), kept here so a change to either side fails this test.
    private static string LauncherInstanceName(string dataRoot) =>
        $@"Local\BirkNext.Launcher.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToUpperInvariant())))[..24]}";
}
