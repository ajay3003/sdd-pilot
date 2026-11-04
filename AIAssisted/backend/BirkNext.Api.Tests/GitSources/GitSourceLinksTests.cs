using System.Net;
using System.Text;
using BirkNext.Api.Data;
using BirkNext.Api.Services.AzureEnvironment;
using BirkNext.Api.Services.GitSources;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ProjectImport;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Tests.ProjectImport;
using BirkNext.AzureEnvironment;
using BirkNext.GitSources;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BirkNext.Api.Tests.GitSources;

/// <summary>
/// Unified source import, Git side: configuration diagnostics (no generic "not configured"), sign-in failures classified from the identity platform's
/// codes, organization discovery with manual fallback, paged projects, a pinned commit that must exist, repository links by stable id with several
/// roles, import history recorded at commit, update checks that never import, and ZIP ↔ Git matching that never uses names and never merges.
/// No Azure DevOps or Microsoft call is made.
/// </summary>
public sealed class GitSourceLinksTests : IDisposable
{
    const string Org = "https://dev.azure.com/bufdir";
    const string Head = "0123456789abcdef0123456789abcdef01234567";
    const string Pinned = "fedcba9876543210fedcba9876543210fedcba98";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-git-link-tests", Guid.NewGuid().ToString("N"));
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    public void Dispose() { _db.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Urls.Add(request.RequestUri!.ToString()); return Task.FromResult(respond(request)); }
    }

    static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    static HttpResponseMessage DevOps(HttpRequestMessage r)
    {
        var url = r.RequestUri!.ToString();
        if (url.Contains("/profile/profiles/me")) return Json("{\"id\":\"member-1\",\"displayName\":\"Tester\"}");
        if (url.Contains("/_apis/accounts?")) return Json("{\"value\":[{\"accountId\":\"a1\",\"accountName\":\"bufdir\"},{\"accountId\":\"a2\",\"accountName\":\"other-org\"}]}");
        if (url.Contains("/_apis/projects?") && !url.Contains("continuationToken"))
        {
            var page = Json("{\"value\":[{\"id\":\"p1\",\"name\":\"M2LB\",\"state\":\"wellFormed\"}]}");
            page.Headers.Add("x-ms-continuationtoken", "next-1");
            return page;
        }
        if (url.Contains("continuationToken=next-1")) return Json("{\"value\":[{\"id\":\"p2\",\"name\":\"Meldingsutveksler\",\"state\":\"wellFormed\"}]}");
        if (url.Contains("/p1/_apis/git/repositories") || url.Contains("/_apis/git/repositories?"))
            return Json("{\"value\":[{\"id\":\"r1\",\"name\":\"M2LB.Common\",\"project\":{\"id\":\"p1\",\"name\":\"M2LB\"},\"defaultBranch\":\"refs/heads/main\"}]}");
        if (url.Contains($"/commits/{Pinned}")) return Json($"{{\"commitId\":\"{Pinned}\",\"committer\":{{\"name\":\"A\",\"date\":\"2026-10-01T00:00:00Z\"}},\"comment\":\"pinned\"}}");
        if (url.Contains("/commits/")) return new(HttpStatusCode.NotFound);
        if (url.Contains("/stats/branches")) return Json($"{{\"value\":[{{\"name\":\"main\",\"isBaseVersion\":true,\"commit\":{{\"commitId\":\"{Head}\",\"committer\":{{\"date\":\"2026-11-03T10:00:00Z\"}}}}}}]}}");
        if (url.Contains("/items?"))
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(ProjectImportServiceTests.Zip([.. ProjectImportServiceTests.Documents, .. ProjectImportServiceTests.Source])) };
        return new(HttpStatusCode.NotFound);
    }

    sealed class Broker : IAzureTokenBroker
    {
        public Exception? InteractiveFailure { get; set; }
        public Task<AzureToken> InteractiveAsync(Func<Uri, Task> openBrowser, CancellationToken ct) => throw new NotSupportedException();
        public Task<AzureToken> DeviceCodeAsync(Func<AzureDeviceCodePrompt, Task> prompt, CancellationToken ct) => throw new NotSupportedException();
        public Task<AzureToken?> SilentAsync(bool forceRefresh, CancellationToken ct) => Task.FromResult<AzureToken?>(null);
        public Task SignOutAsync() => Task.CompletedTask;
        public Task<AzureToken?> SilentForAsync(IReadOnlyList<string> scopes, CancellationToken ct) =>
            Task.FromResult<AzureToken?>(new AzureToken("not-a-real-token", DateTimeOffset.UtcNow.AddHours(1), "tester@bufdir.no", "tenant"));
        public Task<AzureToken> InteractiveForAsync(IReadOnlyList<string> scopes, Func<Uri, Task> openBrowser, CancellationToken ct) =>
            InteractiveFailure is { } e ? Task.FromException<AzureToken>(e) : Task.FromResult(new AzureToken("t", DateTimeOffset.UtcNow.AddHours(1), "a", "t"));
        public async Task<AzureToken> DeviceCodeForAsync(IReadOnlyList<string> scopes, Func<AzureDeviceCodePrompt, Task> prompt, CancellationToken ct)
        {
            await prompt(new AzureDeviceCodePrompt("ABCD-1234", "https://microsoft.com/devicelogin", DateTimeOffset.UtcNow.AddMinutes(15)));
            await Task.Delay(Timeout.Infinite, ct);
            throw new OperationCanceledException();
        }
    }

    sealed class Browser(bool available) : IAzureSignInBrowser
    {
        public bool Available => available;
        public string? UnavailableReason => available ? null : "Not a local workstation runtime.";
        public Task OpenAsync(Uri signInUri) => Task.CompletedTask;
    }

    private (GitSourcesService Service, Handler Http, Broker Broker, ProjectImportService Imports) Create(AzureEnvironmentOptions? options = null, bool browser = true,
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var http = new Handler(respond ?? DevOps);
        var broker = new Broker();
        var staging = new ProjectImportStagingStore();
        var sources = new Mock<IReviewSourceEvidenceProvider>();
        sources.SetupGet(s => s.SourceAnalysisEnabled).Returns(true);
        GitSourcesService? service = null;
        var provider = new ServiceCollection()
            .AddScoped(_ => new ProjectImportService(new IqrSourceStore(_db), staging, sources.Object, NullLogger<ProjectImportService>.Instance, service))
            .AddScoped(_ => new IqrSourceStore(_db)).BuildServiceProvider();
        service = new GitSourcesService(Options.Create(options ?? new AzureEnvironmentOptions { Enabled = true, ClientId = "8f3c1a52-0000-4000-8000-000000000001" }), broker,
            new Browser(browser), new AzureDevOpsGitClient(new HttpClient(http)), new GitSourceSettingsStore(_dir), provider.GetRequiredService<IServiceScopeFactory>(), staging,
            NullLogger<GitSourcesService>.Instance);
        return (service, http, broker, provider.GetRequiredService<ProjectImportService>());
    }

    // ── Configuration health ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Diagnostics_name_exactly_what_is_missing_instead_of_a_generic_not_configured()
    {
        var (disabled, _, _, _) = Create(new AzureEnvironmentOptions { Enabled = false });
        var status = await disabled.StatusAsync(default);
        status.State.Should().Be(GitConnectionState.NotConfigured);
        status.Diagnostics.Single(d => d.Key == "enabled").State.Should().Be(GitCheckState.Missing);
        status.Diagnostics.Single(d => d.Key == "client-id").Should().Match<GitConfigurationCheck>(d => d.State == GitCheckState.Missing && d.AdminAction!.Contains("public client"));
        status.Message.Should().Contain("AzureEnvironment:Enabled");

        var (invalid, _, _, _) = Create(new AzureEnvironmentOptions { Enabled = true, ClientId = "my-app", TenantId = "not a tenant!" }, browser: false);
        var checks = invalid.Diagnostics();
        checks.Single(d => d.Key == "client-id").State.Should().Be(GitCheckState.Invalid);
        checks.Single(d => d.Key == "tenant").State.Should().Be(GitCheckState.Invalid);
        checks.Single(d => d.Key == "method").Detail.Should().StartWith("Device code", "the fallback when the dedicated browser cannot be opened");
        checks.Single(d => d.Key == "devops").State.Should().Be(GitCheckState.NotChecked, "a saved organization or a sign-in is not proof of repository access");
    }

    [Theory]
    [InlineData("AADSTS65001: The user or administrator has not consented", "consent", false)]
    [InlineData("AADSTS50011: The redirect URI specified in the request does not match", "redirect", false)]
    [InlineData("AADSTS700016: Application with identifier was not found", "sign-in", false)]
    [InlineData("AADSTS53003: Access has been blocked by Conditional Access policies", "sign-in", false)]
    public void Sign_in_failures_are_classified_from_the_identity_platform_code(string message, string key, bool cancelled)
    {
        var (isCancelled, _, check) = GitSourcesService.ClassifySignInFailure(new Microsoft.Identity.Client.MsalServiceException("invalid_request", message));
        isCancelled.Should().Be(cancelled);
        check.Key.Should().Be(key);
        check.State.Should().Be(GitCheckState.Failed);
        GitSourcesService.ClassifySignInFailure(new Microsoft.Identity.Client.MsalClientException("authentication_canceled", "closed")).Cancelled
            .Should().BeTrue("a closed sign-in window is a recoverable cancellation, not a failure");
    }

    [Fact]
    public async Task A_consent_failure_marks_the_permission_check_failed_after_interactive_sign_in()
    {
        var (service, _, broker, _) = Create();
        broker.InteractiveFailure = new Microsoft.Identity.Client.MsalServiceException("invalid_grant", "AADSTS65001: consent required");
        await service.SignInAsync(default);
        await WaitUntil(async () => (await service.StatusAsync(default)).State == GitConnectionState.Failed);
        (await service.StatusAsync(default)).Diagnostics.Single(d => d.Key == "consent").State.Should().Be(GitCheckState.Failed);
    }

    [Fact]
    public async Task Device_code_sign_in_shows_the_code_while_waiting()
    {
        var (service, _, _, _) = Create(browser: false);
        await service.StartDeviceCodeAsync(default);
        await WaitUntil(async () => (await service.StatusAsync(default)).DeviceCode is not null);
        var status = await service.StatusAsync(default);
        status.State.Should().Be(GitConnectionState.AwaitingDeviceCode);
        status.DeviceCode!.UserCode.Should().Be("ABCD-1234");
    }

    // ── Discovery ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Organizations_are_discovered_from_membership_and_entered_ones_are_kept()
    {
        var (service, http, _, _) = Create();
        service.SelectOrganizations(["https://dev.azure.com/entered-org"]);
        var (organizations, problem) = await service.OrganizationsAsync(default);
        problem.Should().BeNull();
        organizations.Select(o => (o.Name, o.Discovered)).Should().BeEquivalentTo([("bufdir", true), ("other-org", true), ("entered-org", false)]);
        http.Urls.Should().Contain(u => u.StartsWith(AzureDevOpsGitClient.ProfileHost + "/_apis/profile/profiles/me"));

        var (failing, _, _, _) = Create(respond: _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        failing.SelectOrganizations(["https://dev.azure.com/entered-org"]);
        var (fallback, why) = await failing.OrganizationsAsync(default);
        fallback.Should().ContainSingle(o => o.Name == "entered-org", "discovery failed, the entered organization still works");
        why.Should().Contain("could not be discovered").And.Contain("Advanced");
        FluentActions.Invoking(() => failing.SelectOrganizations(["https://example.com/x"])).Should().Throw<GitSettingsException>();
    }

    [Fact]
    public async Task Projects_are_paged_with_the_continuation_token_and_repositories_listed_per_project()
    {
        var (service, _, _, _) = Create();
        var first = await service.ProjectsAsync(Org, null, default);
        first.Items.Select(p => p.Name).Should().Equal(["M2LB"]);
        first.Next.Should().Be("next-1");
        var second = await service.ProjectsAsync(Org, first.Next, default);
        (second.Items.Single().Name, second.Next).Should().Be(("Meldingsutveksler", (string?)null));
        var repos = await service.ProjectRepositoriesAsync(Org, "p1", default);
        repos.Single().Should().Match<GitRepository>(r => r.Id == "r1" && r.ProjectId == "p1" && r.DefaultBranch == "main");
    }

    // ── Revision, link, import history, updates ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_pinned_commit_must_exist_and_is_imported_instead_of_the_branch_head()
    {
        var (service, http, _, _) = Create();
        var missing = () => service.ImportAsync(new GitImportRequest("r1", "main") { OrganizationUrl = Org, Commit = new string('a', 40) }, default);
        (await missing.Should().ThrowAsync<GitProviderException>()).Which.Kind.Should().Be(GitFailureKind.RevisionNotFound);

        var result = await service.ImportAsync(new GitImportRequest("r1", "main") { OrganizationUrl = Org, Commit = Pinned }, default);
        result.Revision.CommitSha.Should().Be(Pinned);
        http.Urls.Single(u => u.Contains("/items?")).Should().Contain($"versionDescriptor.version={Pinned}");
    }

    [Fact]
    public async Task A_link_keeps_several_roles_records_imports_at_commit_and_checks_for_updates_without_importing()
    {
        var (service, http, _, imports) = Create();
        var link = await service.LinkAsync(new GitLinkRequest(Org, "r1", "main", null, ["Infrastructure", "Contracts", "Infrastructure"], "dev", "M2LB"), default);
        link.Should().Match<GitSourceLink>(l => l.Id == "bufdir|r1" && l.Repository == "M2LB.Common" && l.ProjectId == "p1" && l.BirkNextProject == "M2LB");
        link.Roles.Should().Equal(["Infrastructure", "Contracts"], "several roles, each once");

        var staged = await service.ImportAsync(new GitImportRequest("r1", "main") { OrganizationUrl = Org }, default);
        var commit = (await imports.CommitAsync(staged.Preview!.StagingId))!;
        var recorded = service.Links("dev").Single();
        recorded.Imports.Should().ContainSingle().Which.Should().Match<GitLinkImport>(i => i.CommitSha == Head && i.SnapshotId == commit.Source.SnapshotId && i.Branch == "main");

        var archiveCalls = http.Urls.Count(u => u.Contains("/items?"));
        var check = await service.CheckForUpdatesAsync(recorded.Id, "dev", default);
        check.LastCheck!.UpdateAvailable.Should().BeFalse("the imported commit is the branch head");
        http.Urls.Count(u => u.Contains("/items?")).Should().Be(archiveCalls, "a check never downloads or imports");

        var pinned = await service.LinkAsync(new GitLinkRequest(Org, "r1", "main", Pinned, ["Infrastructure"], "dev", null), default);
        pinned.Imports.Should().ContainSingle("re-linking keeps the history");
        (await service.CheckForUpdatesAsync(pinned.Id, "dev", default)).LastCheck!.UpdateAvailable.Should().BeTrue("the pinned commit was not imported yet");

        service.Unlink(pinned.Id, "dev").Should().BeTrue();
        (await _db.IqrSourceSnapshots.CountAsync()).Should().Be(1, "removing a link never deletes snapshots");
        service.Links("qa").Should().BeEmpty("links are per BirkNext project");
    }

    // ── ZIP ↔ Git matching ────────────────────────────────────────────────────────────────────────────────────────────

    static IqrSourceSnapshot Snapshot(string archive, string sha, string commit, params string[] projects) => new()
    {
        Id = Guid.NewGuid(), Archive = new SourceArchive(archive, sha, 10), Commit = commit, AnalyzedAt = DateTimeOffset.UnixEpoch,
        Projects = [.. projects.Select(p => new SourceProject(Path.GetFileNameWithoutExtension(p), p, false, "Library", SourceConfidence.Confirmed))],
    };

    [Fact]
    public void Earlier_imports_match_by_content_commit_or_structure_never_by_name_and_decisions_are_recorded()
    {
        var link = new GitSourceLink { Id = "bufdir|r1", Repository = "M2LB.Common", Imports = [new(Head, "main", Guid.NewGuid(), "import-x", DateTimeOffset.UnixEpoch)] };
        var own = Snapshot("M2LB.Common@main-01234567.zip", "aaa111", Head, "M2LB.Common-main/src/A/A.csproj", "M2LB.Common-main/src/B/B.csproj");
        var sameContent = Snapshot("M2LB.Common.zip", "AAA111", "Unknown", "x/src/A/A.csproj");
        var sameCommit = Snapshot("export.zip", "bbb222", Head, "y/other.csproj");
        var sameStructure = Snapshot("M2LB.Common (2).zip", "ccc333", "Unknown", "M2LB.Common/src/A/A.csproj", "M2LB.Common/src/B/B.csproj");
        var sameNameOnly = Snapshot("M2LB.Common.zip", "ddd444", "Unknown", "M2LB.Common/totally/Different.csproj", "M2LB.Common/other/Thing.csproj");

        var matches = GitSourcesService.Matches(link, [own], [sameContent, sameCommit, sameStructure, sameNameOnly]);
        matches.Select(m => (m.SnapshotId, m.Strength)).Should().Equal([
            (sameContent.Id, GitSourceMatchStrength.SameContent), (sameCommit.Id, GitSourceMatchStrength.SameCommit), (sameStructure.Id, GitSourceMatchStrength.PossibleSameStructure)],
            "the same name with a different structure is never a match");
        matches.Last().Evidence.Single().Should().Contain("review before associating");

        var decided = GitSourcesService.Matches(link with { AssociatedSnapshots = [sameStructure.Id], KeptSeparate = [sameCommit.Id] }, [own], [sameCommit, sameStructure]);
        decided.Select(m => m.Decision).Should().Equal(["Kept separate", "Associated"]);
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 200 && !await condition(); i++) await Task.Delay(25);
        (await condition()).Should().BeTrue();
    }
}
