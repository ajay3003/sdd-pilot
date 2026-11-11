using System.Text.RegularExpressions;
using BirkNext.BuildInfo;
using BirkNext.Web.Components.Admin;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// System Settings → Application information. The frontend row is this test assembly's compiled BirkNext.Web identity (checked against
/// versioning/BirkNext.Version.props read as text); the backend comes from a fake build-info API. Missing values stay unknown, a local build is
/// never presented as official, mismatches and inconsistent manifests are visible, and nothing crashes when the backend is unreachable.
/// </summary>
public sealed class ApplicationBuildCardTests : BunitContext
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private sealed class Api(BuildInfoResponse? info, string? error = null) : IBuildInfoApiService
    {
        public Task<(BuildInfoResponse? Info, string? Error)> GetAsync(CancellationToken ct = default) => Task.FromResult((info, error));
    }

    private static string PropsVersion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "versioning", "BirkNext.Version.props"))) dir = dir.Parent;
        return Regex.Match(File.ReadAllText(Path.Combine(dir!.FullName, "versioning", "BirkNext.Version.props")), "<BirkNextApplicationVersion>([^<]+)</BirkNextApplicationVersion>").Groups[1].Value;
    }

    private static BuildIdentity CiBackend(string run = "152") => new()
    {
        Component = "BirkNext.Api", ApplicationVersion = "1.4.0", BuildVersion = $"1.4.0-ci.{run}", InformationalVersion = $"1.4.0-ci.{run}+{Sha}", Channel = BuildChannel.CI,
        BuildId = run, BuildNumber = "20261111.1", SourceCommit = Sha, SourceDirty = false, BuildTimestampUtc = "2026-11-11T09:30:00Z", Configuration = "Release",
        AssemblyVersion = "1.4.0.0", FileVersion = $"1.4.0.{run}", Provenance = ProvenanceStatus.Complete,
    };

    private IRenderedComponent<ApplicationBuildCard> Render(BuildInfoResponse? info, string? error = null)
    {
        Services.AddSingleton<IBuildInfoApiService>(new Api(info, error));
        JSInterop.Mode = JSRuntimeMode.Loose;
        var card = Render<ApplicationBuildCard>();
        card.WaitForAssertion(() => card.Find("[data-testid=abc]").GetAttribute("aria-busy").Should().Be("false"));
        return card;
    }

    [Fact]
    public void The_frontend_row_is_this_compiled_frontend_and_carries_the_version_from_the_one_version_file()
    {
        var frontend = FrontendBuildIdentity.Current;
        frontend.ApplicationVersion.Should().Be(PropsVersion());
        frontend.BuildId.Should().BeNull("a test build is local: no CI build number is invented");
        var card = Render(null, "The backend's build information is not reachable.");
        card.Find("[data-testid=abc-row-frontend]").TextContent.Should().Contain(frontend.InformationalVersion);
        card.Find("[data-testid=abc-version]").TextContent.Should().Be(PropsVersion());
        card.Find("[data-testid=abc-row-backend]").TextContent.Should().Contain("Unknown");
        card.Find("[data-testid=abc-error]").TextContent.Should().Contain("not reachable");
        card.Find("[data-testid=abc-match]").TextContent.Should().Be("Not assessed");
        card.Find("[data-testid=abc-not-official]").TextContent.Should().Contain("not an official");
        card.FindAll("[data-testid=abc-build]").Should().BeEmpty("no build number is shown when there is none");
    }

    [Fact]
    public void A_ci_backend_shows_its_build_commit_and_component_states_compactly_with_details_on_demand()
    {
        var info = new BuildInfoResponse
        {
            Backend = CiBackend(),
            Manifest = new(ManifestCheckStatus.Inconsistent, "1.4.0-ci.152", "CI", new string('f', 64), ["Pipeline run: manifest 151, running 152"]),
            Companion = new() { BundledVersion = "0.1.0", BundledBuildId = new string('b', 64), Connected = [new("0.0.9", "old", 1)], Note = "A connected Companion differs from the build bundled with this BirkNext." },
            Database = [new("Application", 140, 139, 1, "x", SchemaCompatibility.PendingMigrations, "1 migration(s) of this build are not applied yet."), new("Snapshot registry", 3, 3, 0, "y", SchemaCompatibility.Current, "ok")],
        };
        var card = Render(info);
        card.Find("[data-testid=abc-channel]").TextContent.Should().Be("CI build");
        card.Find("[data-testid=abc-build]").TextContent.Should().Be("Build 152 · 20261111.1");
        card.Find("[data-testid=abc-commit]").TextContent.Should().Be("Commit " + Sha[..12]);
        card.FindAll("[data-testid=abc-not-official]").Should().BeEmpty();
        card.Find("[data-testid=abc-row-manifest]").GetAttribute("data-status").Should().Be("Inconsistent");
        card.Find("[data-testid=abc-row-manifest]").TextContent.Should().Contain("Differs from the running build");
        card.Find("[data-testid=abc-schema]").GetAttribute("data-status").Should().Be("PendingMigrations");
        card.Find("[data-testid=abc-row-companion]").TextContent.Should().Contain("v0.0.9 connected").And.Contain("bundled v0.1.0");
        card.Find("[data-testid=abc-companion-compat]").TextContent.Should().Be("Compatibility not assessed");
        var details = card.Find("[data-testid=abc-details]");
        details.HasAttribute("open").Should().BeFalse("technical details are collapsed by default");
        details.TextContent.Should().Contain(Sha).And.Contain("Pipeline run: manifest 151, running 152");
        card.FindAll("[data-testid=abc-schema-table] tbody tr").Should().HaveCount(2);
        // The frontend/backend verdict is the shared rule's, applied to this compiled frontend and the reported backend.
        card.Find("[data-testid=abc-row-match]").GetAttribute("data-status").Should().Be(BuildCompatibilityRules.Compare(FrontendBuildIdentity.Current, info.Backend).Status.ToString());
    }

    [Fact]
    public void Copy_puts_the_actual_identities_on_the_clipboard()
    {
        var card = Render(new BuildInfoResponse { Backend = CiBackend() });
        card.Find("[data-testid=abc-copy]").Click();
        var call = JSInterop.Invocations.Single(i => i.Identifier == "navigator.clipboard.writeText");
        var text = (string)call.Arguments[0]!;
        text.Should().Contain("BirkNext 1.4.0 (CI build)").And.Contain("Backend: 1.4.0-ci.152+" + Sha).And.Contain("Frontend: " + FrontendBuildIdentity.Current.InformationalVersion).And.Contain("Pipeline run: 152 (20261111.1)");
        card.Find("[data-testid=abc-copy]").TextContent.Should().Be("Copied");
    }
}
