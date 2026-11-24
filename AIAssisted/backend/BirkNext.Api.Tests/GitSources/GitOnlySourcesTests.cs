using System.Diagnostics;
using BirkNext.Api.Data;
using BirkNext.Api.Services.GitSources;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ProjectImport;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Tests.ProjectImport;
using BirkNext.GitSources;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Api.Tests.GitSources;

/// <summary>
/// Git-only source management (2026-11-19) with the real git on this machine and synthetic local repositories only — no organizational remote is
/// contacted. Git URLs are validated before any process starts (no credentials, no file://, no ext::, no options); a folder scan is bounded and
/// never follows links; adding a local repository fetches into BirkNext-owned storage and leaves the person's working tree, index, branch and
/// configuration exactly as they were; hooks and filter drivers of the person's repository never run; an import exports one exact commit through
/// the same Project Import preview as a ZIP; update checks never import; failures are classified (authentication, network, not found, timeout).
/// </summary>
public sealed class GitOnlySourcesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-git-only", Guid.NewGuid().ToString("N"));
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    public void Dispose()
    {
        _db.Dispose();
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ── Helpers: synthetic repositories made with git itself (no global configuration, no network) ──────────────────────────

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-c", "user.name=Synthetic", "-c", "user.email=synthetic@example.test", "-c", "init.defaultBranch=main", "-c", "core.autocrlf=false" }.Concat(args))
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Path.GetTempPath(), "birknext-git-only-empty.gitconfig");
        File.WriteAllText(psi.Environment["GIT_CONFIG_GLOBAL"]!, "");
        using var p = Process.Start(psi)!;
        var outputTask = p.StandardOutput.ReadToEndAsync();
        var errorTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { p.Kill(entireProcessTree: true); throw new TimeoutException($"git {string.Join(' ', args)} did not finish within 2 minutes."); }
        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
        return output.Trim();
    }

    /// <summary>A repository with a main branch (the synthetic project) and a feature branch with one more commit.</summary>
    private string NewRepository(string name, string? parent = null)
    {
        var path = Path.Combine(parent ?? _dir, name);
        Directory.CreateDirectory(path);
        Git(path, "init", "--quiet");
        foreach (var (file, content) in ProjectImportServiceTests.Documents.Concat(ProjectImportServiceTests.Source))
        {
            var full = Path.Combine(path, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        Git(path, "add", "-A");
        Git(path, "commit", "--quiet", "-m", "Initial synthetic project");
        Git(path, "switch", "--quiet", "-c", "feature/report");
        File.WriteAllText(Path.Combine(path, "FEATURE.md"), "# Feature\n");
        Git(path, "add", "FEATURE.md");
        Git(path, "commit", "--quiet", "-m", "Feature work");
        Git(path, "switch", "--quiet", "main");
        return path;
    }

    private (GitSourcesService Service, ProjectImportStagingStore Staging, GitCli Cli) Create(string? executable = null)
    {
        var staging = new ProjectImportStagingStore();
        var sources = new Mock<IReviewSourceEvidenceProvider>();
        sources.SetupGet(s => s.SourceAnalysisEnabled).Returns(true);
        var imports = new ProjectImportService(new IqrSourceStore(_db), staging, sources.Object, NullLogger<ProjectImportService>.Instance);
        var provider = new ServiceCollection().AddScoped(_ => imports).AddScoped(_ => new IqrSourceStore(_db)).BuildServiceProvider();
        var cli = new GitCli(null, Path.Combine(_dir, "state", "git"), executable);
        var service = new GitSourcesService(cli, new GitSourceSettingsStore(Path.Combine(_dir, "state")), provider.GetRequiredService<IServiceScopeFactory>(), staging,
            NullLogger<GitSourcesService>.Instance);
        return (service, staging, cli);
    }

    // ── URL policy (no process starts for a refused URL) ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://dev.azure.com/org/Project/_git/Repo", "https://dev.azure.com/org/Project/_git/Repo")]
    [InlineData("https://org@dev.azure.com/org/Project/_git/Repo", "https://dev.azure.com/org/Project/_git/Repo")]
    [InlineData("https://github.com/example/repo.git/", "https://github.com/example/repo.git")]
    [InlineData("git@ssh.dev.azure.com:v3/org/Project/Repo", "git@ssh.dev.azure.com:v3/org/Project/Repo")]
    [InlineData("ssh://git@example.test:2222/team/repo.git", "ssh://git@example.test:2222/team/repo.git")]
    [InlineData("https://user:secret@example.test/repo.git", null)]
    [InlineData("https://example.test/repo.git?token=abc", null)]
    [InlineData("http://example.test/repo.git", null)]
    [InlineData("file:///C:/repos/x", null)]
    [InlineData("C:\\repos\\x", null)]
    [InlineData("ext::sh -c touch% /tmp/pwned", null)]
    [InlineData("--upload-pack=touch /tmp/pwned", null)]
    [InlineData("-oProxyCommand=calc", null)]
    [InlineData("git@-oProxyCommand=calc:repo", null)]
    [InlineData("-oProxyCommand=calc@host:repo", null)]
    [InlineData("git@host:-repo", null)]
    [InlineData("ssh://-oProxyCommand=calc/repo", null)]
    [InlineData("ssh://-user@host/repo", null)]
    [InlineData("https://example.test/", null)]
    [InlineData("", null)]
    public void Git_urls_are_canonical_without_credentials_or_refused(string input, string? expected) =>
        GitUrlPolicy.Normalize(input).Url.Should().Be(expected);

    [Fact]
    public void Branch_and_archive_names_follow_the_policy()
    {
        GitUrlPolicy.IsBranchName("feature/report").Should().BeTrue();
        foreach (var bad in new[] { "-x", "a..b", "x.lock", "a b", "x/", "refs/../x", "" }) GitUrlPolicy.IsBranchName(bad).Should().BeFalse(bad);
        GitUrlPolicy.Redact("https://user:token@example.test/r.git").Should().Be("https://example.test/r.git");
        GitUrlPolicy.RepositoryId("https://Example.test/r").Should().Be(GitUrlPolicy.RepositoryId("https://example.test/r")).And.StartWith("git-");
        GitUrlPolicy.NameOf("git@host:team/Repo.git").Should().Be("Repo");
        var rev = new GitSourceRevision("Git", "local", "", "git-1", "Hub Common", "feature/a b", new string('a', 40), null, DateTimeOffset.UnixEpoch);
        GitSourcePolicy.ArchiveFileName(rev).Should().Be("Hub-Common@feature-a-b-aaaaaaaa.zip");
    }

    [Fact]
    public async Task A_refused_url_never_starts_git_and_needs_no_credentials()
    {
        var (service, _, _) = Create(executable: Path.Combine(_dir, "no-such-git.exe"));
        var act = () => service.AddRepositoryAsync(new(null, "https://user:secret@example.test/repo.git", "dev", "Hub"), default);
        (await act.Should().ThrowAsync<GitProviderException>()).Which.Kind.Should().Be(GitFailureKind.UnsupportedUrl);
    }

    // ── Installation ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_names_the_git_version_and_credential_helper_without_a_remote_call_and_a_missing_git_is_explained()
    {
        var (service, _, _) = Create();
        var status = await service.StatusAsync(default);
        status.State.Should().Be(GitConnectionState.Connected);
        status.Installation!.Version.Should().MatchRegex(@"^\d+\.\d+");
        status.Installation.Path.Should().NotBeNullOrEmpty();
        status.Installation.CredentialHelpers.Should().OnlyContain(h => !h.Contains(' '), "only the helper's name is shown");

        var (missing, _, _) = Create(executable: Path.Combine(_dir, "no-such-git.exe"));
        var none = await missing.StatusAsync(default);
        none.State.Should().Be(GitConnectionState.NotConfigured);
        none.Installation!.Found.Should().BeFalse();
        none.Message.Should().NotBeNullOrEmpty();
        var act = () => missing.FetchAsync("x", "dev", default);
        await act.Should().ThrowAsync<GitProviderException>();
    }

    // ── Local repositories ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_folder_scan_is_bounded_reports_branch_detached_dirty_and_worktree_and_skips_tool_folders()
    {
        var root = Path.Combine(_dir, "work");
        var hub = NewRepository("Hub", root);
        File.AppendAllText(Path.Combine(hub, "shop", "specs", "001-cart", "spec.md"), "\nlocal edit\n");      // a tracked file changed, not committed
        var other = NewRepository("Other", Path.Combine(root, "team"));
        Git(other, "switch", "--quiet", "--detach", "HEAD");
        NewRepository("Hidden", Path.Combine(root, "node_modules"));
        NewRepository("Deep", Path.Combine(root, "a", "b", "c", "d", "e"));
        Git(hub, "worktree", "add", "--quiet", Path.Combine(root, "hub-wt"), "feature/report");
        var (service, _, _) = Create();

        var result = await service.ScanAsync(new(root, MaxDepth: 3), default);
        result.Repositories.Select(r => r.Name).Should().BeEquivalentTo(["Hub", "Other", "hub-wt"], "node_modules is skipped and Deep is below the depth limit");
        var h = result.Repositories.Single(r => r.Name == "Hub");
        (h.Branch, h.Detached, h.Dirty, h.IsWorktree).Should().Be(("main", false, true, false));
        h.HeadCommit.Should().MatchRegex("^[0-9a-f]{40}$");
        var o = result.Repositories.Single(r => r.Name == "Other");
        (o.Branch, o.Detached).Should().Be(((string?)null, true));
        result.Repositories.Single(r => r.Name == "hub-wt").IsWorktree.Should().BeTrue();
        (await service.ScanAsync(new("relative\\path"), default)).Problem.Should().Contain("full path");
    }

    [Fact]
    public async Task Adding_a_local_repository_fetches_into_birknext_storage_and_never_changes_the_working_tree()
    {
        var hub = NewRepository("Hub");
        File.AppendAllText(Path.Combine(hub, "shop", "specs", "001-cart", "spec.md"), "\nuncommitted\n");
        Git(hub, "switch", "--quiet", "feature/report");
        // --no-optional-locks: the test's own status must not refresh the index whose timestamp it compares.
        string State() => string.Join("|", Git(hub, "--no-optional-locks", "status", "--porcelain"), Git(hub, "rev-parse", "HEAD"), Git(hub, "symbolic-ref", "HEAD"),
            File.ReadAllText(Path.Combine(hub, ".git", "config")), Git(hub, "for-each-ref"), File.GetLastWriteTimeUtc(Path.Combine(hub, ".git", "index")).Ticks.ToString());
        var before = State();
        var (service, _, _) = Create();

        var link = await service.AddRepositoryAsync(new(hub, null, "dev", "Hub"), default);
        link.Should().Match<GitSourceLink>(l => l.Kind == "Local" && l.Organization == "local" && l.Branch == "feature/report" && l.LastFetchFailure == null && l.LastFetchedAt != null);
        link.SelectedBranches.Should().Equal(["feature/report"], "adding selects the current branch, and only it is downloaded");
        (await service.BranchesAsync(link.Id, "dev", default)).Select(b => b.Name).Should().BeEquivalentTo(["feature/report"]);
        service.CacheOf(link).Should().StartWith(Path.Combine(_dir, "state"), "the fetch goes into BirkNext-owned storage");
        State().Should().Be(before, "the working tree, index, branch, refs and configuration are untouched");
        service.Links("qa").Should().BeEmpty("links belong to one BirkNext project");
    }

    [Fact]
    public async Task Fetch_latest_downloads_only_the_selected_branches_and_drops_deselected_ones_while_the_full_list_needs_no_download()
    {
        // 2026-11-23: a remote with hundreds of branches downloaded every branch's history on each Fetch latest.
        var hub = NewRepository("Hub");
        Git(hub, "branch", "release/1.0", "main");
        var (service, _, _) = Create();
        var link = await service.AddRepositoryAsync(new(hub, null, "dev", "Hub", Branch: "main"), default);
        link.SelectedBranches.Should().Equal(["main"]);
        (await service.AvailableBranchesAsync(link.Id, "dev", default)).Select(b => b.Name).Should().BeEquivalentTo(["main", "feature/report", "release/1.0"],
            "the full list is read from the repository's branch list, nothing is fetched for it");
        (await service.BranchesAsync(link.Id, "dev", default)).Select(b => b.Name).Should().Equal(["main"]);

        service.UpdateLink(link.Id, "dev", null, null, null, ["release/1.0", "feature/report"]).Should().Match<GitSourceLink>(l => l.Branch == "release/1.0",
            "the current branch left the selection, so the first selected branch became current");
        await service.FetchAsync(link.Id, "dev", default);
        (await service.BranchesAsync(link.Id, "dev", default)).Select(b => b.Name).Should().BeEquivalentTo(["release/1.0", "feature/report"], "main was deselected and removed from BirkNext's copy");

        var bad = () => Task.FromResult(service.UpdateLink(link.Id, "dev", null, null, null, ["-bad"]));
        await bad.Should().ThrowAsync<GitProviderException>();
        GitSourcesService.Wanted(new GitSourceLink { Branch = "" }, [], "main", ["main", "dev"]).Fetch.Should().Equal(["main"], "no selection: the default branch");
    }

    [Fact]
    public async Task Hooks_and_filter_drivers_of_the_persons_repository_never_run()
    {
        var hub = NewRepository("Hub");
        var marker = Path.Combine(_dir, "hook-ran.txt");
        Git(hub, "config", "filter.evil.smudge", $"sh -c 'echo ran > {marker.Replace('\\', '/')}'");
        Git(hub, "config", "filter.evil.clean", "cat");
        File.WriteAllText(Path.Combine(hub, ".gitattributes"), "*.md filter=evil\n");
        Git(hub, "add", ".gitattributes");
        Git(hub, "commit", "--quiet", "-m", "attributes");
        // The hooks are installed after the test's own commits (which would run them): from here on only BirkNext's actions could.
        var hooks = Path.Combine(hub, ".git", "hooks");
        foreach (var hook in new[] { "reference-transaction", "post-checkout", "pre-auto-gc", "post-index-change", "post-commit" })
            File.WriteAllText(Path.Combine(hooks, hook), $"#!/bin/sh\necho ran > '{marker.Replace('\\', '/')}'\n");
        File.Exists(marker).Should().BeFalse();
        var (service, _, _) = Create();

        var link = await service.AddRepositoryAsync(new(hub, null, "dev", "Hub"), default);
        var import = await service.ImportAsync(new GitImportRequest(link.RepositoryId, "main") { OrganizationUrl = link.OrganizationUrl }, default);
        import.Preview.Should().NotBeNull(import.Failure);
        File.Exists(marker).Should().BeFalse("no hook or filter driver of the person's repository runs during add, fetch or archive");
    }

    [Fact]
    public async Task An_import_exports_one_exact_commit_through_the_project_import_preview_and_checks_never_import()
    {
        var hub = NewRepository("Hub");
        var (service, staging, _) = Create();
        var link = await service.AddRepositoryAsync(new(hub, null, "dev", "Hub"), default);
        var mainHead = Git(hub, "rev-parse", "main");

        var first = await service.ImportAsync(new GitImportRequest(link.RepositoryId, "main") { OrganizationUrl = link.OrganizationUrl }, default);
        first.Failure.Should().BeNull();
        first.Revision.Should().Match<GitSourceRevision>(r => r.Provider == "Git" && r.CommitSha == mainHead && r.Branch == "main" && r.Organization == "local");
        first.Preview!.Archive.FileName.Should().Be(GitSourcePolicy.ArchiveFileName(first.Revision));
        staging.GitRevisionOf(first.Preview.StagingId).Should().Be(first.Revision, "the exact revision travels with the staged import as provenance");
        service.Record(first.Revision, Guid.NewGuid(), "import-1");

        var featureHead = Git(hub, "rev-parse", "feature/report");
        // Only selected branches are fetched (2026-11-23): select feature/report too, then a commit of its history can be imported.
        service.UpdateLink(link.Id, "dev", null, null, null, ["main", "feature/report"]);
        await service.FetchAsync(link.Id, "dev", default);
        (await service.ImportAsync(new GitImportRequest(link.RepositoryId, "main") { OrganizationUrl = link.OrganizationUrl, Commit = featureHead }, default))
            .Revision.CommitSha.Should().Be(featureHead, "an exact commit of the fetched history can be imported");
        var unknown = () => service.ImportAsync(new GitImportRequest(link.RepositoryId, "main") { OrganizationUrl = link.OrganizationUrl, Commit = new string('b', 40) }, default);
        (await unknown.Should().ThrowAsync<GitProviderException>()).Which.Kind.Should().Be(GitFailureKind.RevisionNotFound);
        var badBranch = () => service.ImportAsync(new GitImportRequest(link.RepositoryId, "--upload-pack=x") { OrganizationUrl = link.OrganizationUrl }, default);
        (await badBranch.Should().ThrowAsync<GitProviderException>()).Which.Kind.Should().Be(GitFailureKind.BranchNotFound);

        (await service.CheckForUpdatesAsync(link.Id, "dev", default)).LastCheck!.UpdateAvailable.Should().BeFalse("main is imported at its head");
        File.WriteAllText(Path.Combine(hub, "CHANGELOG.md"), "new\n");
        Git(hub, "add", "CHANGELOG.md");
        Git(hub, "commit", "--quiet", "-m", "New work");
        var checkedLink = await service.CheckForUpdatesAsync(link.Id, "dev", default);
        checkedLink.LastCheck!.Should().Match<GitUpdateCheck>(c => c.UpdateAvailable && c.RemoteCommit == Git(hub, "rev-parse", "main"));
        GitUpdateStates.Of(checkedLink).Should().Be(GitUpdateState.UpdateAvailable);
        checkedLink.Imports.Should().ContainSingle("a check never imports");
    }

    [Fact]
    public async Task The_same_repository_in_two_projects_imports_and_records_only_for_the_requesting_project()
    {
        var hub = NewRepository("Hub");
        var (service, _, _) = Create();
        var dev = await service.AddRepositoryAsync(new(hub, null, "dev", "Hub"), default);
        var qa = await service.AddRepositoryAsync(new(hub, null, "qa", "Hub QA", Branch: "feature/report"), default);
        dev.RepositoryId.Should().Be(qa.RepositoryId, "one repository, linked to two BirkNext projects");

        var ambiguous = () => service.ImportAsync(new GitImportRequest(dev.RepositoryId, "main") { OrganizationUrl = dev.OrganizationUrl }, default);
        await ambiguous.Should().ThrowAsync<GitSettingsException>("without the project an import could take another project's link");

        var forQa = await service.ImportAsync(new GitImportRequest(qa.RepositoryId, "") { OrganizationUrl = qa.OrganizationUrl, EnvironmentId = "qa" }, default);
        forQa.Revision.Should().Match<GitSourceRevision>(r => r.EnvironmentId == "qa" && r.Branch == "feature/report" && r.CommitSha == Git(hub, "rev-parse", "feature/report"),
            "the qa link's own branch is imported, not dev's");
        var other = () => service.ImportAsync(new GitImportRequest(dev.RepositoryId, "main") { OrganizationUrl = dev.OrganizationUrl, EnvironmentId = "prod" }, default);
        (await other.Should().ThrowAsync<GitProviderException>()).Which.Kind.Should().Be(GitFailureKind.RepositoryNotFound);

        service.Record(forQa.Revision, Guid.NewGuid(), "import-qa");
        service.Links("qa").Single().Imports.Should().ContainSingle(i => i.ImportId == "import-qa");
        service.Links("dev").Single().Imports.Should().BeEmpty("another project's link of the same repository is never changed");

        var legacy = forQa.Revision with { EnvironmentId = null, CommitSha = Git(hub, "rev-parse", "main") };
        service.Record(legacy, Guid.NewGuid(), "import-legacy");
        service.Links("dev").Single().Imports.Should().ContainSingle(i => i.ImportId == "import-legacy", "provenance from before projects were recorded keeps its old behaviour");
    }

    [Fact]
    public async Task A_link_keeps_several_roles_and_a_pinned_commit_is_imported_instead_of_the_branch_head()
    {
        var hub = NewRepository("Hub");
        var (service, _, _) = Create();
        var link = await service.AddRepositoryAsync(new(hub, null, "dev", "Hub", Roles: ["Backend", "Contracts", "backend"]), default);
        var pin = Git(hub, "rev-parse", "feature/report");
        var updated = service.UpdateLink(link.Id, "dev", "main", pin, ["Backend", "Infrastructure"]);
        updated.Roles.Should().Equal(["Backend", "Infrastructure"]);
        updated.PinnedCommit.Should().Be(pin);
        var act = () => Task.FromResult(service.UpdateLink(link.Id, "dev", "-bad", null, null));
        (await act.Should().ThrowAsync<GitProviderException>()).Which.Kind.Should().Be(GitFailureKind.BranchNotFound);
        var check = await service.CheckForUpdatesAsync(link.Id, "dev", default);
        check.LastCheck!.RemoteCommit.Should().Be(pin, "a pinned link compares the pinned commit, not the branch head");
        check.LastCheck.UpdateAvailable.Should().BeTrue("the pinned commit was never imported");
    }

    [Fact]
    public async Task Network_and_authentication_failures_are_classified_and_recorded_on_the_link()
    {
        var (service, _, _) = Create();
        // A closed local port: Git cannot connect. No external host is contacted.
        var link = await service.AddRepositoryAsync(new(null, "https://127.0.0.1:9/team/repo.git", "dev", "Remote"), default);
        link.LastFetchFailure.Should().Be(GitFailureKind.Unreachable);
        link.LastFetchProblem.Should().Contain("could not be reached");
        link.LastFetchedAt.Should().BeNull();

        GitCli.Classify(new(128, "", "fatal: could not read Username for 'https://example.test': terminal prompts disabled", false, false)).Should().Be(GitFailureKind.SignInRequired);
        GitCli.Classify(new(128, "", "fatal: User cancelled the authentication prompt.", false, false)).Should().Be(GitFailureKind.AuthenticationCancelled);
        GitCli.Classify(new(128, "", "remote: TF401019: The Git repository with name or identifier X does not exist or you do not have permissions. fatal: repository 'x' not found", false, false))
            .Should().Be(GitFailureKind.RepositoryNotFound);
        GitCli.Classify(new(128, "", "fatal: unable to access 'https://x/': The requested URL returned error: 403", false, false)).Should().Be(GitFailureKind.AccessDenied);
        GitCli.Classify(new(128, "", "Host key verification failed.", false, false)).Should().Be(GitFailureKind.HostKeyNotTrusted);
        GitCli.Classify(new(-1, "", "", true, false)).Should().Be(GitFailureKind.TimedOut);
        GitCli.Classify(new(-1, "", "", false, true)).Should().Be(GitFailureKind.Cancelled);
    }

    [Fact]
    public async Task Cancellation_stops_git_and_only_allowed_subcommands_run()
    {
        var (_, _, cli) = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await cli.RunAsync("version", [], null, GitConfigMode.Isolated, TimeSpan.FromSeconds(30), cts.Token);
        (r.Cancelled || r.Ok).Should().BeTrue("a cancelled call ends (it may already have completed)");
        var push = () => cli.RunAsync("push", [], null, GitConfigMode.User, TimeSpan.FromSeconds(5), default);
        await push.Should().ThrowAsync<InvalidOperationException>("push is not an allowed subcommand");
        cli.HardenedConfig().Should().Contain(c => c.StartsWith("core.hooksPath=", StringComparison.Ordinal)).And.Contain("protocol.ext.allow=never").And.Contain("core.fsmonitor=false");
    }

    // ── Matching earlier ZIP imports (unchanged rule) ─────────────────────────────────────────────────────────────────────

    const string Head = "0123456789abcdef0123456789abcdef01234567";

    static IqrSourceSnapshot Snapshot(string archive, string sha, string commit, params string[] projects) => new()
    {
        Id = Guid.NewGuid(), Archive = new SourceArchive(archive, sha, 10), Commit = commit, AnalyzedAt = DateTimeOffset.UnixEpoch,
        Projects = [.. projects.Select(p => new SourceProject(Path.GetFileNameWithoutExtension(p), p, false, "Library", SourceConfidence.Confirmed))],
    };

    [Fact]
    public void Earlier_imports_match_by_content_commit_or_structure_never_by_name_and_decisions_are_recorded()
    {
        var link = new GitSourceLink { Id = "local|git-1", Repository = "Hub.Common", Imports = [new(Head, "main", Guid.NewGuid(), "import-x", DateTimeOffset.UnixEpoch)] };
        var own = Snapshot("Hub.Common@main-01234567.zip", "aaa111", Head, "Hub.Common-main/src/A/A.csproj", "Hub.Common-main/src/B/B.csproj");
        var sameContent = Snapshot("Hub.Common.zip", "AAA111", "Unknown", "x/src/A/A.csproj");
        var sameCommit = Snapshot("export.zip", "bbb222", Head, "y/other.csproj");
        var sameStructure = Snapshot("Hub.Common (2).zip", "ccc333", "Unknown", "Hub.Common/src/A/A.csproj", "Hub.Common/src/B/B.csproj");
        var sameNameOnly = Snapshot("Hub.Common.zip", "ddd444", "Unknown", "Hub.Common/totally/Different.csproj", "Hub.Common/other/Thing.csproj");
        var matches = GitSourcesService.Matches(link, [own], [sameContent, sameCommit, sameStructure, sameNameOnly]);
        matches.Select(m => (m.SnapshotId, m.Strength)).Should().Equal([
            (sameContent.Id, GitSourceMatchStrength.SameContent), (sameCommit.Id, GitSourceMatchStrength.SameCommit), (sameStructure.Id, GitSourceMatchStrength.PossibleSameStructure)]);
        GitSourcesService.Matches(link with { AssociatedSnapshots = [sameStructure.Id], KeptSeparate = [sameCommit.Id] }, [own], [sameCommit, sameStructure])
            .Select(m => m.Decision).Should().Equal(["Kept separate", "Associated"]);
    }

    [Fact]
    public void A_link_made_with_the_retired_azure_devops_sign_in_is_read_as_a_remote_git_url()
    {
        var legacy = new GitSourceLink { Id = "bufdir|r1", OrganizationUrl = "https://dev.azure.com/bufdir", Organization = "bufdir", RepositoryId = "r1", Repository = "Common",
            WebUrl = "https://dev.azure.com/bufdir/M2LB/_git/Common", Branch = "main" };
        legacy.EffectiveKind.Should().Be("Remote");
        legacy.EffectiveRemoteUrl.Should().Be("https://dev.azure.com/bufdir/M2LB/_git/Common", "its web URL is its clone URL; its identity and import history stay");
    }
}
