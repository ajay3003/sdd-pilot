extern alias LauncherProject;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using LauncherProject::BirkNext.Launcher;
using Path = System.IO.Path;
using Profiles = LauncherProject::BirkNext.BuildInfo.RuntimeProfile;

namespace BirkNext.Api.Tests.Services.Launcher;

/// <summary>
/// The TesterNative launcher's decisions, without starting PostgreSQL, the backend or a browser: profile selection, data-folder state
/// classification (never initdb over existing files), secure database configuration, package integrity, port choice, backups that may be
/// pruned, the backend's environment and the frontend settings. The real processes are exercised by the isolated package acceptance.
/// </summary>
public sealed class TesterLauncherTests
{
    private static RuntimeManifest Manifest(int major = 16) => new()
    {
        Format = 1, Distribution = "WindowsTesterPackage", DefaultRuntimeProfile = "TesterNative",
        PostgreSql = new RuntimeManifest.BundledPostgres { Version = $"{major}.13", Major = major },
        K6 = new RuntimeManifest.BundledK6 { Version = "1.0.0", Sha256 = new string('c', 64) },
    };

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "birknext-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // ── Profile and options ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_package_default_selects_TesterNative_and_an_explicit_profile_wins_but_never_switches_to_developer_mode()
    {
        ProfileSelection.Resolve(null, "TesterNative").Should().Be((Profiles.TesterNative, "package default"));
        ProfileSelection.Resolve("testernative", "TesterNative").Should().Be((Profiles.TesterNative, "command line"));
        FluentActions.Invoking(() => ProfileSelection.Resolve("DeveloperContainer", "TesterNative")).Should().Throw<LauncherException>()
            .Which.Message.Should().Contain("scripts/start-local.ps1", "developer mode is a different launcher, never a switch inside the tester package");
        FluentActions.Invoking(() => ProfileSelection.Resolve("Hybrid", "TesterNative")).Should().Throw<LauncherException>().Which.ExitCode.Should().Be(64);
        FluentActions.Invoking(() => ProfileSelection.Resolve(null, null)).Should().Throw<LauncherException>().WithMessage("*cannot be determined*");
    }

    [Fact]
    public void Options_are_explicit_and_a_data_folder_must_be_a_full_path()
    {
        LauncherOptions.Parse([]).Should().Match<LauncherOptions>(o => o.Command == "start" && !o.NoBrowser && o.RuntimeProfile == null);
        LauncherOptions.Parse(["--no-browser", "--data-dir", "C:\\Data\\BN", "--status"]).Should().Match<LauncherOptions>(o => o.Command == "status" && o.NoBrowser && o.DataDirectory == "C:\\Data\\BN");
        FluentActions.Invoking(() => LauncherOptions.Parse(["--data-dir", "relative"])).Should().Throw<LauncherException>();
        FluentActions.Invoking(() => LauncherOptions.Parse(["--podman"])).Should().Throw<LauncherException>().Which.ExitCode.Should().Be(64);
        FluentActions.Invoking(() => LauncherOptions.Parse(["--runtime-profile"])).Should().Throw<LauncherException>();
    }

    [Fact]
    public void The_tester_data_folder_is_profile_specific_and_never_the_developer_setup()
    {
        var root = DataLayout.Default(Profiles.TesterNative);
        root.Should().EndWith(Path.Combine("BirkNext", "TesterNative"));
        var data = new DataLayout(root);
        data.Database.Should().StartWith(root);
        data.AppData.Should().StartWith(root).And.NotContain("App_Data");
    }

    // ── Database state: never initdb over files it did not create ─────────────────────────────────────────

    private static (PostgresManager Manager, DataLayout Data) Postgres(int bundledMajor = 16)
    {
        var data = new DataLayout(TempDir());
        data.Create();
        var package = new PackageLayout(TempDir(), Manifest(bundledMajor));
        return (new PostgresManager(package, data, new LauncherLog(data.Logs)), data);
    }

    [Fact]
    public void Database_state_is_classified_from_the_files_and_BirkNexts_own_record()
    {
        var (pg, data) = Postgres();
        pg.State(out _).Should().Be(ClusterState.NotInitialized);

        Directory.CreateDirectory(data.Database);
        File.WriteAllText(Path.Combine(data.Database, "PG_VERSION"), "16");
        pg.State(out var unknown).Should().Be(ClusterState.UnknownFiles, "files without BirkNext's record are never initialised over");
        unknown.Should().Contain("not created by this BirkNext launcher");

        StateFile.Write(data.InitializationFile, new { state = "Initializing" });
        pg.State(out _).Should().Be(ClusterState.InterruptedInitialization);
        File.Delete(data.InitializationFile);

        StateFile.Write(data.ClusterFile, new ClusterRecord { SystemIdentifier = "1", Major = 16, DataDirectory = data.Database, Port = 55432 });
        pg.State(out _).Should().Be(ClusterState.Initialized);

        File.WriteAllText(Path.Combine(data.Database, "PG_VERSION"), "17");
        pg.State(out var newer).Should().Be(ClusterState.IncompatibleVersion);
        newer.Should().Contain("newer than the bundled PostgreSQL 16", "an older binary is never started on a newer data directory");
        File.WriteAllText(Path.Combine(data.Database, "PG_VERSION"), "15");
        pg.State(out var older).Should().Be(ClusterState.IncompatibleVersion);
        older.Should().Contain("needs a PostgreSQL upgrade");

        File.Delete(Path.Combine(data.Database, "PG_VERSION"));
        pg.State(out _).Should().Be(ClusterState.MissingFiles, "a recorded cluster whose files vanished is reported, never silently recreated");
    }

    [Fact]
    public void The_database_listens_on_loopback_only_and_requires_SCRAM_for_the_two_BirkNext_roles()
    {
        var conf = PostgresManager.Configuration(55432, "C:\\Users\\t\\AppData\\Local\\BirkNext\\TesterNative\\logs\\postgresql");
        conf.Should().Contain("listen_addresses = '127.0.0.1'").And.Contain("port = 55432").And.Contain("unix_socket_directories = ''")
            .And.Contain("password_encryption = 'scram-sha-256'").And.Contain("log_rotation_size").And.Contain("C:/Users/t/AppData");
        var hba = PostgresManager.HostBasedAuthentication();
        var rules = hba.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        rules.Should().HaveCount(4).And.OnlyContain(r => r.EndsWith("scram-sha-256") && r.Contains("127.0.0.1/32"));
        rules.Should().ContainSingle(r => r.Contains("birknext_app") && r.Contains("birknext_restore_")).Which.Should().Contain("\"/^birknext_restore_[0-9a-f]{12}$\"",
            "the application role may also open the isolated restore databases it owns, matched exactly (never all databases)");
        hba.Should().NotContain("trust").And.NotContain("0.0.0.0").And.NotContain("::/0");
        rules.Should().Contain(r => r.StartsWith("host") && r.Contains("birknext_app") && r.Contains(" birknext "), "the application role reaches only its own database");
        rules.Should().ContainSingle(r => r.Contains(DatabaseCredentials.ProvisionerUser)).Which.Should().StartWith("host    postgres ",
            "the restore-provisioning role reaches only the maintenance database, where it can create a new database and nothing else");
    }

    [Fact]
    public void The_restore_provisioning_role_is_narrow_and_the_application_role_never_gets_CREATEDB()
    {
        var statements = string.Join("\n", PostgresManager.ProvisionerStatements("birknext_app", "Pw23456789abcdefghijkmnopqrstuvw", 17));
        statements.Should().Contain("CREATE ROLE birknext_restore_provisioner LOGIN NOSUPERUSER CREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT")
            .And.Contain("ALTER ROLE birknext_restore_provisioner WITH LOGIN NOSUPERUSER CREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT",
                "an existing role is re-asserted narrow on every start")
            .And.Contain("GRANT birknext_app TO birknext_restore_provisioner WITH INHERIT FALSE, SET TRUE", "only to create databases OWNED BY the application role")
            .And.Contain("GRANT CONNECT ON DATABASE postgres TO birknext_restore_provisioner");
        statements.Should().NotContain("ALTER ROLE birknext_app").And.NotMatchRegex(@"(?<![A-Z])(SUPERUSER|CREATEROLE|REPLICATION|BYPASSRLS)", "only the NO… forms of the broad attributes appear");
        string.Join("\n", PostgresManager.ProvisionerStatements("birknext_app", "x", 15)).Should().Contain("GRANT birknext_app TO birknext_restore_provisioner")
            .And.NotContain("SET TRUE", "the WITH SET option exists from PostgreSQL 16");
    }

    [Fact]
    public void The_backend_gets_a_restore_provisioning_login_only_when_one_was_provisioned()
    {
        var data = new DataLayout(TempDir());
        var package = new PackageLayout("C:\\Pkg\\BirkNext-Tester", Manifest());
        var old = DatabaseCredentials.New();
        old.ProvisionerPassword.Should().BeNull("credentials written by earlier launchers have no provisioning role");
        BackendProcess.Environment(package, data, new Ports.Assignment(55433, 5480, 5481), old)["Backup__RestoreProvisioning__ConnectionString"].Should().BeEmpty();

        var provisioned = old with { ProvisionerPassword = DatabaseCredentials.NewPassword() };
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(BackendProcess.Environment(package, data, new Ports.Assignment(55433, 5480, 5481), provisioned)["Backup__RestoreProvisioning__ConnectionString"]);
        connection.Host.Should().Be("127.0.0.1");
        connection.Database.Should().Be("postgres");
        connection.Username.Should().Be(DatabaseCredentials.ProvisionerUser).And.NotBe(provisioned.AdminUser, "never the cluster administrator");
        provisioned.ToString().Should().NotContain(provisioned.ProvisionerPassword!);

        var file = Path.Combine(TempDir(), "secrets.bin");
        provisioned.Save(file);
        DatabaseCredentials.Load(file).Should().Be(provisioned);
    }

    [Fact]
    public void Only_older_automatic_pre_upgrade_backups_may_be_pruned()
    {
        var files = Enumerable.Range(1, 8).Select(i => $"C:\\b\\birknext-2026110{i}-120000-pre-upgrade.dump").Append("C:\\b\\birknext-20260101-120000-manual.dump").ToList();
        var prunable = PostgresManager.PrunablePreUpgradeBackups(files);
        prunable.Should().HaveCount(3).And.OnlyContain(f => f.EndsWith("-pre-upgrade.dump"));
        prunable.Should().NotContain(f => f.Contains("20261108") || f.Contains("20261104"), "the newest five are kept");
        prunable.Should().NotContain(f => f.Contains("manual"));
    }

    // ── Package integrity ────────────────────────────────────────────────────────────────────────────────

    private static string Package(params (string Path, string Content)[] files)
    {
        var root = TempDir();
        var hashes = new List<object>();
        foreach (var (p, c) in files)
        {
            var full = Path.Combine(root, p.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, c);
            hashes.Add(new { path = p, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant() });
        }
        File.WriteAllText(Path.Combine(root, "package-manifest.json"), JsonSerializer.Serialize(new { format = 2, files = files.Select(f => f.Path), fileHashes = hashes }));
        return root;
    }

    [Fact]
    public void Package_integrity_detects_missing_changed_and_out_of_package_entries()
    {
        var root = Package(("runtime/k6/k6.exe", "k6"), ("AIAssisted/backend/BirkNext.Api.exe", "api"), ("config/runtime-manifest.json", "{}"));
        PackageIntegrity.Verify(root).Should().Match<PackageIntegrity.Result>(r => r.Ok && r.Checked == 3);
        File.AppendAllText(Path.Combine(root, "runtime", "k6", "k6.exe"), "tampered");
        PackageIntegrity.Verify(root).Changed.Should().Equal("runtime/k6/k6.exe");
        PackageIntegrity.Verify(root, ["AIAssisted/backend/BirkNext.Api.exe"]).Ok.Should().BeTrue("the quick check covers only the listed runtime files");
        File.Delete(Path.Combine(root, "AIAssisted", "backend", "BirkNext.Api.exe"));
        PackageIntegrity.Verify(root, ["AIAssisted/backend/BirkNext.Api.exe"]).Missing.Should().Equal("AIAssisted/backend/BirkNext.Api.exe");
        PackageIntegrity.Verify(root, ["runtime/postgresql/bin/postgres.exe"]).Missing.Should().Contain("runtime/postgresql/bin/postgres.exe", "an expected file the manifest does not list is missing");

        var traversal = Package(("a.txt", "a"));
        File.WriteAllText(Path.Combine(traversal, "package-manifest.json"), JsonSerializer.Serialize(new { format = 2, fileHashes = new[] { new { path = "../outside.txt", sha256 = new string('0', 64) } } }));
        PackageIntegrity.Verify(traversal).Changed.Should().Equal("../outside.txt");
        File.WriteAllText(Path.Combine(traversal, "package-manifest.json"), "{ \"format\": 1 }");
        FluentActions.Invoking(() => PackageIntegrity.Verify(traversal)).Should().Throw<LauncherException>().WithMessage("*damaged*");
        File.Delete(Path.Combine(traversal, "package-manifest.json"));
        FluentActions.Invoking(() => PackageIntegrity.Verify(traversal)).Should().Throw<LauncherException>().WithMessage("*incomplete*");
    }

    [Fact]
    public void A_missing_or_invalid_runtime_manifest_is_a_tester_message_not_a_crash()
    {
        var root = TempDir();
        FluentActions.Invoking(() => RuntimeManifest.Read(PackageLayout.RuntimeManifestPath(root))).Should().Throw<LauncherException>().WithMessage("*incomplete*");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(PackageLayout.RuntimeManifestPath(root), "{ not json");
        FluentActions.Invoking(() => RuntimeManifest.Read(PackageLayout.RuntimeManifestPath(root))).Should().Throw<LauncherException>().WithMessage("*damaged*");
        File.WriteAllText(PackageLayout.RuntimeManifestPath(root), JsonSerializer.Serialize(Manifest() with { K6 = new RuntimeManifest.BundledK6 { Sha256 = "short" } }, RuntimeManifest.Json));
        FluentActions.Invoking(() => RuntimeManifest.Read(PackageLayout.RuntimeManifestPath(root))).Should().Throw<LauncherException>().WithMessage("*not valid*");
        File.WriteAllText(PackageLayout.RuntimeManifestPath(root), JsonSerializer.Serialize(Manifest(), RuntimeManifest.Json));
        RuntimeManifest.Read(PackageLayout.RuntimeManifestPath(root)).PostgreSql.Major.Should().Be(16);
    }

    // ── Ports, credentials, environment ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_busy_port_moves_BirkNext_to_the_next_free_port_and_never_takes_one_already_assigned()
    {
        Ports.Choose(5480, new HashSet<int>(), p => p != 5480).Should().Be(5481);
        Ports.Choose(5480, new HashSet<int> { 5481 }, p => p != 5480).Should().Be(5482);
        FluentActions.Invoking(() => Ports.Choose(5480, new HashSet<int>(), _ => false)).Should().Throw<LauncherException>().WithMessage("*No free local port*");
        Ports.Preferred.Should().Match<Ports.Assignment>(p => p.Backend != 5000 && p.Frontend != 5173 && p.Database != 5432, "the developer ports are never used by default");
    }

    [Fact]
    public void Credentials_are_strong_protected_for_this_user_and_never_printed()
    {
        var pw = DatabaseCredentials.NewPassword();
        pw.Should().HaveLength(32).And.MatchRegex("^[A-Za-z0-9]+$", "no character needs quoting in SQL or a connection string");
        var credentials = DatabaseCredentials.New();
        credentials.ToString().Should().NotContain(credentials.AppPassword).And.NotContain(credentials.AdminPassword);
        new Npgsql.NpgsqlConnectionStringBuilder(credentials.AppConnectionString(55432)).Should().Match<Npgsql.NpgsqlConnectionStringBuilder>(b =>
            b.Host == "127.0.0.1" && b.Port == 55432 && b.Database == credentials.Database && b.Username == credentials.AppUser && b.Password == credentials.AppPassword);
        LauncherLog.Redact(credentials.AppConnectionString(55432)).Should().NotContain(credentials.AppPassword).And.Contain("Password=[redacted]");
        LauncherLog.Redact("PGPASSWORD=abc123 pwd: xyz").Should().Be("PGPASSWORD=[redacted] pwd: [redacted]");
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(TempDir(), "db-credentials.dat");
        credentials.Save(file);
        File.ReadAllText(file).Should().NotContain(credentials.AppPassword, "stored with DPAPI for this Windows user, never in plaintext");
        DatabaseCredentials.Load(file).Should().Be(credentials);
    }

    [Fact]
    public void The_backend_gets_the_TesterNative_profile_the_native_k6_runner_and_only_the_managed_database()
    {
        var data = new DataLayout(TempDir());
        var package = new PackageLayout("C:\\Pkg\\BirkNext-Tester", Manifest());
        var credentials = DatabaseCredentials.New();
        var env = BackendProcess.Environment(package, data, new Ports.Assignment(55433, 5480, 5481), credentials);
        env["BirkNext__RuntimeProfile"].Should().Be("TesterNative");
        env["BirkNext__Distribution"].Should().Be("WindowsTesterPackage");
        env["PerformanceTests__Runner"].Should().Be("Native");
        env["Authorization__LocalTrustedExecution"].Should().Be("true", "the single-user tester runs its own tests; the backend still checks loopback, Host, Origin and the session");
        env["Authorization__LocalSystemConfiguration"].Should().Be("true", "the tester maintains their own installation's configuration");
        env["Authorization__LocalWorkspaceTransfer"].Should().Be("true", "the tester backs up and restores their own installation");
        env["PerformanceTests__Native__K6Path"].Should().Be(Path.Combine("C:\\Pkg\\BirkNext-Tester", "runtime", "k6", "k6.exe"));
        env["PerformanceTests__Native__K6Sha256"].Should().Be(new string('c', 64));
        // In-app Backup & Transfer uses the package's bundled pg_dump/pg_restore (the root cause of "PostgreSQL tools: Not available" in the package).
        env["Backup__PostgresTools__Mode"].Should().Be("Host");
        env["Backup__PostgresTools__BinDirectory"].Should().Be(Path.Combine("C:\\Pkg\\BirkNext-Tester", "runtime", "postgresql", "bin"), "never the global PATH");
        new Npgsql.NpgsqlConnectionStringBuilder(env["ConnectionStrings__Default"]).Should().Match<Npgsql.NpgsqlConnectionStringBuilder>(b =>
            b.Host == "127.0.0.1" && b.Port == 55433 && b.Database == credentials.Database && b.Username == credentials.AppUser, "only the launcher-managed database, as its own non-superuser role");
        env["ASPNETCORE_URLS"].Should().Be("http://localhost:5480");
        env["FRONTEND_ORIGIN"].Should().Be("http://localhost:5481");
        env["BIRKNEXT_APP_DATA_DIR"].Should().StartWith(data.Root, "mutable files live in the user's data folder, never in the package");
        env["LoggingSettings__LogPath"].Should().StartWith(data.Root);
        env.Keys.Should().NotContain(k => k.Contains("Podman", StringComparison.OrdinalIgnoreCase) || k.Contains("Container", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_frontend_is_served_with_the_actual_backend_address_and_its_other_settings_unchanged()
    {
        var json = FrontendHost.AppSettings("""{ "BackendUrl": "http://localhost:5000", "ExtractionRules": {}, "Authentication": { "Entra": { "TenantId": "" } } }""", 5480);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("BackendUrl").GetString().Should().Be("http://localhost:5480");
        doc.RootElement.TryGetProperty("Authentication", out _).Should().BeTrue();
        FrontendHost.AppSettings("{}", 5490).Should().Contain("http://localhost:5490");
    }

    [Fact]
    public void Missing_Windows_prerequisites_are_named_in_plain_language()
    {
        var problems = Prerequisites.Problems(fileExists: _ => false, freeBytes: 100L << 20);
        problems.Should().Contain(p => p.Contains("Visual C++ 2015–2022 Redistributable (x64)")).And.Contain(p => p.Contains("free disk space"));
        problems.Should().NotContain(p => p.Contains("Podman", StringComparison.OrdinalIgnoreCase) || p.Contains("WSL") || p.Contains("Docker"));
    }

    [Fact]
    public void One_launcher_per_data_folder()
    {
        var root = TempDir();
        using (var first = SingleInstance.TryAcquire(root))
        {
            first.Should().NotBeNull();
            var second = Task.Run(() => SingleInstance.TryAcquire(root)).Result; // another thread = another owner
            second.Should().BeNull("a second launch never starts a second database or backend on the same data");
        }
        using var again = Task.Run(() => SingleInstance.TryAcquire(root)).Result;
        again.Should().NotBeNull("released on exit");
    }
}
