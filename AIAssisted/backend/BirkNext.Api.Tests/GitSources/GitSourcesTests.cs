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
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BirkNext.Api.Tests.GitSources;

/// <summary>
/// Git Sources (Azure DevOps over Microsoft Entra, delegated): canonical organization URLs, latest-first branches, diagnostic failures (never one
/// "connection failed"), status without remote calls, an explicit test that never imports, a commit archive addressed by SHA, and a Git import that
/// goes through the same Project Import preview/commit as a ZIP and stamps the exact revision. No real Azure DevOps or Microsoft call is made.
/// </summary>
public sealed class GitSourcesTests : IDisposable
{
    const string Org = "https://dev.azure.com/bufdir";
    const string Sha = "0123456789abcdef0123456789abcdef01234567";
    const string Token = "test-token-not-real";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-git-tests", Guid.NewGuid().ToString("N"));
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    public void Dispose() { _db.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Theory]
    [InlineData("https://dev.azure.com/bufdir", "https://dev.azure.com/bufdir")]
    [InlineData("https://dev.azure.com/bufdir/", "https://dev.azure.com/bufdir")]
    [InlineData("https://Bufdir.visualstudio.com", "https://bufdir.visualstudio.com")]
    [InlineData("http://dev.azure.com/bufdir", null)]
    [InlineData("https://dev.azure.com/bufdir/project", null)]
    [InlineData("https://example.com/bufdir", null)]
    [InlineData("https://dev.azure.com/bufdir?x=1", null)]
    [InlineData("", null)]
    public void Organization_urls_are_canonical_or_rejected(string input, string? expected) =>
        GitSourcePolicy.NormalizeOrganizationUrl(input).Should().Be(expected);

    [Fact]
    public void Branch_names_order_and_archive_names_follow_the_policy()
    {
        GitSourcePolicy.BranchName("refs/heads/feature/x").Should().Be("feature/x");
        var old = new GitBranch("main", Sha, new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null, true);
        var recent = old with { Name = "feature/a", CommittedAt = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero), IsDefault = false };
        var undated = old with { Name = "z", CommittedAt = null };
        GitSourcePolicy.LatestFirst([undated, old, recent]).Select(b => b.Name).Should().Equal(["feature/a", "main", "z"]);
        var rev = new GitSourceRevision("AzureDevOps", "bufdir", "M2LB", "r1", "M2LB Common", "feature/a b", Sha, null, DateTimeOffset.UnixEpoch);
        GitSourcePolicy.ArchiveFileName(rev).Should().Be("M2LB-Common@feature-a-b-01234567.zip", "readable only: the SHA in the provenance is the identity");
        GitSourcePolicy.IsCommitSha(Sha).Should().BeTrue();
        GitSourcePolicy.IsCommitSha("main").Should().BeFalse();
    }

    // ── Fakes: no network, no Microsoft ─────────────────────────────────────────────────────────────────────────────

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Requests.Add(request); return Task.FromResult(respond(request)); }
    }

    static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    static HttpResponseMessage DevOps(HttpRequestMessage r)
    {
        var url = r.RequestUri!.ToString();
        if (url.Contains("/stats/branches"))
            return Json(("{\"value\":[{\"name\":\"main\",\"isBaseVersion\":true,\"commit\":{\"commitId\":\"OLD\",\"committer\":{\"name\":\"A\",\"date\":\"2026-01-01T00:00:00Z\"},\"comment\":\"old\"}},"
                + "{\"name\":\"feature/rapport\",\"commit\":{\"commitId\":\"SHA\",\"committer\":{\"name\":\"B\",\"date\":\"2026-11-03T10:00:00Z\"},\"comment\":\"recent\"}}]}")
                .Replace("OLD", new string('a', 40)).Replace("SHA", Sha));
        if (url.Contains("/items?"))
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(ProjectImportServiceTests.Zip([.. ProjectImportServiceTests.Documents, .. ProjectImportServiceTests.Source])) };
        if (url.Contains("/_apis/git/repositories?"))
            return Json("""{"value":[{"id":"r1","name":"M2LB.Common","project":{"name":"M2LB"},"defaultBranch":"refs/heads/main","webUrl":"https://dev.azure.com/bufdir/M2LB/_git/M2LB.Common"}]}""");
        return new(HttpStatusCode.NotFound);
    }

    sealed class Broker(bool signedIn) : IAzureTokenBroker
    {
        public bool SignedIn { get; set; } = signedIn;
        public Task<AzureToken> InteractiveAsync(Func<Uri, Task> openBrowser, CancellationToken ct) => throw new NotSupportedException();
        public Task<AzureToken> DeviceCodeAsync(Func<AzureDeviceCodePrompt, Task> prompt, CancellationToken ct) => throw new NotSupportedException();
        public Task<AzureToken?> SilentAsync(bool forceRefresh, CancellationToken ct) => Task.FromResult<AzureToken?>(null);
        public Task SignOutAsync() { SignedIn = false; return Task.CompletedTask; }
        public Task<AzureToken?> SilentForAsync(IReadOnlyList<string> scopes, CancellationToken ct)
        {
            scopes.Should().Equal([GitSourcePolicy.AzureDevOpsScope], "only the Azure DevOps resource is requested");
            return Task.FromResult(SignedIn ? new AzureToken(Token, DateTimeOffset.UtcNow.AddHours(1), "tester@bufdir.no", "tenant") : null);
        }
    }

    sealed class Browser : IAzureSignInBrowser
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public Task OpenAsync(Uri signInUri) => Task.CompletedTask;
    }

    private (GitSourcesService Service, Handler Http, Broker Broker, ProjectImportStagingStore Staging, ProjectImportService Imports) Create(bool configured = true, bool signedIn = true,
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null, IOptions<AzureEnvironmentOptions>? options = null)
    {
        var http = new Handler(respond ?? DevOps);
        var broker = new Broker(signedIn);
        var staging = new ProjectImportStagingStore();
        var sources = new Mock<IReviewSourceEvidenceProvider>();
        sources.SetupGet(s => s.SourceAnalysisEnabled).Returns(true);
        var imports = new ProjectImportService(new IqrSourceStore(_db), staging, sources.Object, NullLogger<ProjectImportService>.Instance);
        var provider = new ServiceCollection().AddScoped(_ => imports).BuildServiceProvider();
        var service = new GitSourcesService(options ?? Options.Create(new AzureEnvironmentOptions { Enabled = configured, ClientId = configured ? "client" : null }), broker, new Browser(),
            new AzureDevOpsGitClient(new HttpClient(http)), new GitSourceSettingsStore(_dir), provider.GetRequiredService<IServiceScopeFactory>(), staging,
            NullLogger<GitSourcesService>.Instance);
        return (service, http, broker, staging, imports);
    }

    [Fact]
    public async Task Status_never_calls_azure_devops_and_names_what_is_missing()
    {
        var (service, http, _, _, _) = Create(configured: false);
        var status = await service.StatusAsync(default);
        status.State.Should().Be(GitConnectionState.NotConfigured);
        status.Requirements.Should().Contain(r => r.Contains("Application (client) ID") && r.Contains("Microsoft sign-in setup"));

        var (signedIn, http2, broker, _, _) = Create();
        (await signedIn.StatusAsync(default)).Should().Match<GitConnectionStatus>(s => s.State == GitConnectionState.Connected && s.Account == "tester@bufdir.no");
        broker.SignedIn = false;
        (await signedIn.StatusAsync(default)).State.Should().Be(GitConnectionState.ReconnectRequired, "expiry is not configuration loss");
        http.Requests.Concat(http2.Requests).Should().BeEmpty("status reads the in-memory sign-in state only");
    }

    private sealed class MutableOptions(AzureEnvironmentOptions value) : IOptions<AzureEnvironmentOptions>
    {
        public AzureEnvironmentOptions Value { get; set; } = value;
    }

    [Fact]
    public async Task A_test_made_with_another_client_id_is_reported_stale_and_never_as_current_access()
    {
        var options = new MutableOptions(new AzureEnvironmentOptions { Enabled = true, ClientId = "11111111-2222-3333-4444-555555555555" });
        var (service, _, _, _, _) = Create(options: options);
        service.SaveSettings(new() { OrganizationUrl = Org });
        (await service.TestAsync(default)).Should().Match<GitTestResult>(t => t.Ready && t.ConfigurationKey != null && !t.ConfigurationKey.Contains(Token));
        (await service.StatusAsync(default)).LastTestStale.Should().BeFalse();

        options.Value = new AzureEnvironmentOptions { Enabled = true, ClientId = "99999999-2222-3333-4444-555555555555" };
        var status = await service.StatusAsync(default);
        status.LastTestStale.Should().BeTrue("the passed test ran with the previous app registration");
        status.LastTest!.Ready.Should().BeTrue("the result itself is kept as history");
    }

    [Fact]
    public async Task Settings_are_validated_and_a_new_organization_clears_the_last_test()
    {
        var (service, _, _, _, _) = Create();
        var invalid = () => service.SaveSettings(new() { OrganizationUrl = "http://dev.azure.com/x" });
        invalid.Should().Throw<GitSettingsException>();
        service.SaveSettings(new() { OrganizationUrl = "https://dev.azure.com/bufdir/" }).OrganizationUrl.Should().Be(Org);
        (await service.TestAsync(default)).Ready.Should().BeTrue();
        (await service.StatusAsync(default)).LastTest.Should().NotBeNull();
        service.SaveSettings(new() { OrganizationUrl = "https://dev.azure.com/other" });
        (await service.StatusAsync(default)).LastTest.Should().BeNull("a test of another organization is never shown as verified for this one");
        File.ReadAllText(Path.Combine(_dir, "git-sources.json")).Should().NotContain(Token, "no token is ever written");
    }

    [Fact]
    public async Task The_git_test_verifies_each_step_and_never_imports()
    {
        var (service, http, _, staging, _) = Create();
        service.SaveSettings(new() { OrganizationUrl = Org });
        var result = await service.TestAsync(default);

        result.Ready.Should().BeTrue();
        result.Steps.Select(s => (s.Name, s.State)).Should().Equal([
            ("Microsoft Entra authentication", GitTestStepState.Verified), ("Azure DevOps organization and repository discovery", GitTestStepState.Verified),
            ("Branch discovery", GitTestStepState.Verified), ("Commit metadata", GitTestStepState.Verified), ("Local storage", GitTestStepState.Verified)]);
        result.Steps.Single(s => s.Name == "Commit metadata").Message.Should().StartWith("feature/rapport @ 01234567", "latest branch first");
        result.RepositoryCount.Should().Be(1);
        http.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get && !r.RequestUri!.ToString().Contains("/items?"), "read-only; the test downloads no archive");
        http.Requests.Should().OnlyContain(r => r.Headers.Authorization!.Scheme == "Bearer" && !r.RequestUri!.ToString().Contains(Token), "the token is a header, never in a URL");
        staging.GitRevisionOf(Guid.Empty).Should().BeNull();
        (await _db.IqrSourceSnapshots.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GitFailureKind.SignInRequired)]
    [InlineData(HttpStatusCode.Forbidden, GitFailureKind.AccessDenied)]
    [InlineData(HttpStatusCode.NotFound, GitFailureKind.OrganizationNotFound)]
    [InlineData(HttpStatusCode.NonAuthoritativeInformation, GitFailureKind.SignInRequired)]
    [InlineData(HttpStatusCode.InternalServerError, GitFailureKind.Unexpected)]
    public async Task Provider_failures_are_diagnostic(HttpStatusCode status, GitFailureKind kind)
    {
        var client = new AzureDevOpsGitClient(new HttpClient(new Handler(_ => status == HttpStatusCode.NonAuthoritativeInformation
            ? new(status) { Content = new StringContent("<html>sign in</html>", Encoding.UTF8, "text/html") } : new(status))));
        var call = () => client.RepositoriesAsync(Org, Token, default);
        (await call.Should().ThrowAsync<GitProviderException>()).Which.Kind.Should().Be(kind);
    }

    [Fact]
    public async Task A_failed_sign_in_step_skips_the_rest_and_says_what_to_do()
    {
        var (service, http, _, _, _) = Create(signedIn: false);
        service.SaveSettings(new() { OrganizationUrl = Org });
        var result = await service.TestAsync(default);
        result.Ready.Should().BeFalse();
        result.Summary.Should().Contain("Sign-in required");
        result.Steps.Where(s => s.Name is "Branch discovery" or "Commit metadata").Should().OnlyContain(s => s.State == GitTestStepState.Skipped);
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_git_import_downloads_the_commit_by_sha_and_goes_through_the_same_project_import()
    {
        var (service, http, _, staging, imports) = Create();
        service.SaveSettings(new() { OrganizationUrl = Org });
        var result = await service.ImportAsync(new("r1", "refs/heads/feature/rapport"), default);

        result.Failure.Should().BeNull();
        result.Revision.Should().Match<GitSourceRevision>(r => r.CommitSha == Sha && r.Branch == "feature/rapport" && r.Repository == "M2LB.Common" && r.Project == "M2LB");
        var archiveCall = http.Requests.Single(r => r.RequestUri!.ToString().Contains("/items?")).RequestUri!.ToString();
        archiveCall.Should().Contain($"versionDescriptor.version={Sha}").And.Contain("versionDescriptor.versionType=commit", "a branch is resolved to its SHA first");
        result.Preview!.Archive.FileName.Should().Be("M2LB.Common@feature-rapport-01234567.zip");
        staging.GitRevisionOf(result.Preview.StagingId).Should().Be(result.Revision);

        var commit = (await imports.CommitAsync(result.Preview.StagingId))!;
        commit.Provenance.Git.Should().Be(result.Revision);
        var snapshot = await new IqrSourceStore(_db).FindSourceAnalysisAsync(commit.Source.SnapshotId!.Value, default);
        (snapshot!.Commit, snapshot.Branch).Should().Be((Sha, "feature/rapport"), "the provider's revision, never guessed from the archive");

        var missing = () => service.ImportAsync(new("r1", "deleted-branch"), default);
        (await missing.Should().ThrowAsync<GitProviderException>()).Which.Kind.Should().Be(GitFailureKind.BranchNotFound);
    }

    [Fact]
    public async Task Disconnect_signs_out_but_keeps_settings_and_imports()
    {
        var (service, _, broker, _, _) = Create();
        service.SaveSettings(new() { OrganizationUrl = Org, TrackedBranches = [new("r1", "M2LB.Common", "M2LB", "main")] });
        var status = await service.DisconnectAsync(default);
        broker.SignedIn.Should().BeFalse();
        status.State.Should().Be(GitConnectionState.NotConnected);
        status.OrganizationUrl.Should().Be(Org);
        new GitSourceSettingsStore(_dir).Read().Settings.TrackedBranches.Should().ContainSingle();
    }
}
