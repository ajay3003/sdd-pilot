using BirkNext.BuildInfo;
using BirkNext.Web.Components.Admin;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// System Settings → Overview. Each tile summarises one authoritative source (build-info, runtime, Configuration Health) and never claims more than
/// that source says: a local dirty build stays "compatibility not assessed", an unreachable database or missing k6 is never shown as ready, a
/// schema behind the build is a warning even when PostgreSQL answers, and an unanswering backend leaves the tiles Unknown (fixtures A–L of the task).
/// </summary>
public sealed class SettingsOverviewTests : BunitContext
{
    private sealed class Api(BuildInfoResponse? build, RuntimeEnvironmentInfo? runtime, string? error = null) : IBuildInfoApiService
    {
        public int Calls;
        public Task<(BuildInfoResponse? Info, string? Error)> GetAsync(CancellationToken ct = default) { Calls++; return Task.FromResult((build, build is null ? error : null)); }
        public Task<(RuntimeEnvironmentInfo? Info, string? Error)> GetRuntimeAsync(CancellationToken ct = default) => Task.FromResult((runtime, runtime is null ? error : null));
    }

    private static RuntimeEnvironmentInfo Runtime(RuntimeProfile profile = RuntimeProfile.DeveloperContainer, string db = "Connected", string k6 = "Available") => new()
    {
        Profile = profile, ProfileName = RuntimeProfiles.DisplayName(profile),
        Distribution = profile == RuntimeProfile.TesterNative ? "Windows Tester Package" : "Source checkout / developer build",
        DatabaseStatus = db, DatabaseServerVersion = db == "Connected" ? "16.13 (Debian 16.13-1.pgdg120+1)" : null,
        PerformanceRunner = profile == RuntimeProfile.TesterNative ? "Bundled k6.exe (native Windows process)" : "k6 container (Podman)",
        PerformanceRunnerAvailability = k6, K6Version = k6 == "Available" ? "1.0.0" : null, PerformanceRunnerDetail = "The k6 runner is not available: the image is missing.",
    };

    private static BuildInfoResponse Build(BuildIdentity? backend = null, params SchemaCompatibility[] schema) => new()
    {
        Backend = backend ?? FrontendBuildIdentity.Current,
        Database = [.. (schema.Length == 0 ? [SchemaCompatibility.Current, SchemaCompatibility.Current] : schema).Select((s, i) => new SchemaState($"Ctx{i}", 10, s == SchemaCompatibility.Current ? 10 : 8, s == SchemaCompatibility.Current ? 0 : 2, null, s, ""))],
    };

    private IRenderedComponent<SettingsOverview> Render(Api api, int issues = 0, int git = 0)
    {
        Services.AddSingleton<IBuildInfoApiService>(api);
        var cut = Render<SettingsOverview>(p => p.Add(x => x.ConfigIssueCount, issues).Add(x => x.GitPrerequisites, git));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ss-overview]").GetAttribute("aria-busy").Should().Be("false"));
        return cut;
    }

    private static string Tile(IRenderedComponent<SettingsOverview> cut, string id) => cut.Find($"[data-testid=ss-ov-{id}]").TextContent;
    private static string Tone(IRenderedComponent<SettingsOverview> cut, string id) => cut.Find($"[data-testid=ss-ov-{id}]").GetAttribute("data-tone")!;

    [Fact]
    public void A_developer_installation_with_everything_answering_reads_ready_but_a_local_build_is_never_called_official_or_matched()
    {
        var cut = Render(new Api(Build(), Runtime()));
        Tile(cut, "application").Should().Contain($"BirkNext {FrontendBuildIdentity.Current.ApplicationVersion}").And.Contain("Local development build")
            .And.NotContain("Release");
        Tile(cut, "runtime").Should().Contain("Developer (containers)").And.Contain("Source checkout");
        Tile(cut, "database").Should().Contain("PostgreSQL 16.13 connected").And.Contain("2 of 2 contexts current");
        Tone(cut, "database").Should().Be("ok");
        Tile(cut, "performance").Should().Contain("k6 1.0.0 available").And.Contain("Podman");
        cut.Find("[data-testid=ss-config-health]").GetAttribute("data-state").Should().Be("clear");
        cut.Find("[data-testid=ss-overview-fresh]").TextContent.Should().Contain("not monitored live", "a read at page open is never presented as live health");
    }

    [Fact]
    public void A_tester_installation_names_its_profile_and_bundled_runner()
    {
        var cut = Render(new Api(Build(), Runtime(RuntimeProfile.TesterNative)));
        Tile(cut, "runtime").Should().Contain("Tester (native Windows)").And.Contain("Windows Tester Package");
        Tile(cut, "performance").Should().Contain("k6.exe");
    }

    [Fact]
    public void An_unreachable_database_or_a_missing_k6_is_never_shown_as_ready()
    {
        var cut = Render(new Api(Build(), Runtime(db: "Unavailable", k6: "ImageMissing")));
        Tile(cut, "database").Should().Contain("PostgreSQL unavailable");
        Tone(cut, "database").Should().Be("bad");
        Tile(cut, "performance").Should().Contain("k6 not available").And.Contain("image is missing");
        Tone(cut, "performance").Should().Be("warn");
    }

    [Fact]
    public void A_connected_database_with_pending_migrations_is_a_warning_not_current()
    {
        var cut = Render(new Api(Build(null, SchemaCompatibility.Current, SchemaCompatibility.PendingMigrations), Runtime()));
        Tile(cut, "database").Should().Contain("1 of 2 contexts current");
        Tone(cut, "database").Should().Be("warn", "TCP connectivity is not a current schema");
    }

    [Fact]
    public void Different_frontend_and_backend_builds_are_a_visible_mismatch()
    {
        var other = FrontendBuildIdentity.Current with { ApplicationVersion = "9.9.9", BuildVersion = "9.9.9-ci.1", InformationalVersion = "9.9.9-ci.1+ffffffffffffffffffffffffffffffffffffffff",
            Channel = BuildChannel.CI, SourceCommit = "ffffffffffffffffffffffffffffffffffffffff", SourceDirty = false, BuildId = "1" };
        var cut = Render(new Api(Build(other), Runtime()));
        Tile(cut, "application").Should().Contain("BirkNext 9.9.9", "the backend's version is the installation's version");
        Tile(cut, "application").Should().NotContain("same build", "a different build is never called the same; a dirty or unknown frontend stays not assessed");
    }

    [Fact]
    public void A_backend_that_does_not_answer_leaves_the_tiles_unknown_never_healthy()
    {
        var cut = Render(new Api(null, null, "The backend's runtime information is not reachable."));
        Tile(cut, "runtime").Should().Contain("Unknown").And.Contain("not reachable");
        Tile(cut, "database").Should().Contain("Unknown");
        Tile(cut, "performance").Should().Contain("Unknown");
        Tile(cut, "application").Should().Contain("compatibility not assessed");
        new[] { "application", "runtime", "database", "performance" }.Select(t => Tone(cut, t)).Should().NotContain("ok", "no backend-sourced tile is green without an answer (Configuration comes from the page's own evaluation, which needs the settings to load)");
    }

    [Theory]
    [InlineData(0, 0, "clear", "All clear")]
    [InlineData(0, 2, "setup", "No failures · Git Sources not set up")]
    [InlineData(3, 1, "issues", "3 issues · Git Sources not set up")]
    public void Configuration_health_shows_the_pages_own_evaluation_and_optional_setup_is_not_a_failure(int issues, int git, string state, string label)
    {
        var cut = Render(new Api(Build(), Runtime()), issues, git);
        var health = cut.Find("[data-testid=ss-config-health]");
        (health.GetAttribute("data-state"), health.TextContent).Should().Be((state, label));
        Tone(cut, "configuration").Should().Be(issues > 0 ? "bad" : git > 0 ? "warn" : "ok");
    }
}
