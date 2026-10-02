using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.AzureEnvironment;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.AzureEnvironment;

/// <summary>
/// The read-only boundary, enforced three ways: the policy refuses secret-returning or mutating paths before anything is sent, the HTTP client
/// sends only GET plus the predefined Resource Graph POST (with 401 refresh, bounded 429 retry and safe paging), and an architecture guard
/// keeps every other HTTP method, secret operation and free-form KQL out of the Azure Environment code.
/// </summary>
public sealed class AzureReadOnlyBoundaryTests
{
    private const string Ns = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.EventHub/namespaces/ns";
    private const string Site = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Web/sites/app";

    [Theory]
    [InlineData(Ns + "/authorizationRules/RootManageSharedAccessKey/listKeys?api-version=2024-01-01")]
    [InlineData(Ns + "/authorizationRules?api-version=2024-01-01")]
    [InlineData("/subscriptions/1/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/kv/secrets?api-version=2023-07-01")]
    [InlineData("/subscriptions/1/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/kv/keys?api-version=2023-07-01")]
    [InlineData("/subscriptions/1/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/kv/certificates?api-version=2023-07-01")]
    [InlineData(Site + "/config/appsettings?api-version=2022-09-01")]
    [InlineData(Site + "/config/connectionstrings?api-version=2022-09-01")]
    [InlineData(Site + "/config/authsettingsV2?api-version=2022-09-01")]
    [InlineData(Site + "/config/publishingcredentials?api-version=2022-09-01")]
    [InlineData(Site + "/host/default/listkeys?api-version=2022-09-01")]
    [InlineData("/subscriptions/1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/st/listKeys?api-version=2023-01-01")]
    [InlineData("/subscriptions/1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/st/listAccountSas?api-version=2023-01-01")]
    [InlineData("/subscriptions/1/resourceGroups/rg/providers/Microsoft.Web/sites/app/restart?api-version=2022-09-01")]
    [InlineData("https://management.azure.com/subscriptions?api-version=2022-12-01")]
    [InlineData("//evil.example/subscriptions?api-version=1")]
    [InlineData("/subscriptions/../../x?api-version=1")]
    [InlineData("/subscriptions")]
    public void Secret_returning_mutating_absolute_or_versionless_paths_are_refused(string path) =>
        AzureReadOnlyPolicy.CheckGet(path).Should().NotBeNull();

    [Theory]
    [InlineData("/subscriptions?api-version=2022-12-01")]
    [InlineData(Ns + "/eventhubs?api-version=2024-01-01")]
    [InlineData(Ns + "/eventhubs/hub/consumergroups?api-version=2024-01-01")]
    [InlineData(Site + "/config/web?api-version=2022-09-01")]
    [InlineData(Ns + "/providers/Microsoft.Insights/diagnosticSettings?api-version=2021-05-01-preview")]
    [InlineData("/subscriptions/1/providers/Microsoft.Authorization/roleEligibilityScheduleInstances?api-version=2020-10-01&$filter=asTarget()")]
    [InlineData("/subscriptions/1/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/st/blobServices/default/containers?api-version=2023-01-01")]
    public void Metadata_reads_are_allowed(string path) => AzureReadOnlyPolicy.CheckGet(path).Should().BeNull();

    [Fact]
    public void Next_links_are_followed_only_on_the_management_host()
    {
        AzureReadOnlyPolicy.RelativeNextLink("https://management.azure.com/subscriptions?api-version=2022-12-01&$skiptoken=x").Should().Be("/subscriptions?api-version=2022-12-01&$skiptoken=x");
        AzureReadOnlyPolicy.RelativeNextLink("https://management.azure.com.evil.example/subscriptions?api-version=1").Should().BeNull();
        AzureReadOnlyPolicy.RelativeNextLink("http://management.azure.com/subscriptions?api-version=1").Should().BeNull();
        AzureReadOnlyPolicy.RelativeNextLink("https://management.azure.com:8443/subscriptions?api-version=1").Should().BeNull();
        AzureReadOnlyPolicy.RelativeNextLink("https://management.azure.com" + Site + "/config/appsettings?api-version=1").Should().BeNull();
    }

    private sealed class RecordingHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Authorization, string? Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return responses[Math.Min(Requests.Count - 1, responses.Length - 1)](request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        if (retryAfter is { } wait) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
        return response;
    }

    private static HttpAzureManagementClient Client(RecordingHandler handler, FakeSignIn signIn) => new(new HttpClient(handler), signIn, NullLogger<HttpAzureManagementClient>.Instance);

    [Fact]
    public async Task A_refused_read_is_never_sent_and_allowed_reads_are_GET_with_the_token()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"value":[]}"""));
        var client = Client(handler, new FakeSignIn());
        (await client.GetAsync(Site + "/config/appsettings?api-version=2022-09-01", default)).Status.Should().Be(0);
        handler.Requests.Should().BeEmpty();

        (await client.GetAsync("/subscriptions?api-version=2022-12-01", default)).Ok.Should().BeTrue();
        handler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Get && r.Url == "https://management.azure.com/subscriptions?api-version=2022-12-01" && r.Authorization == "Bearer fake-token");
    }

    [Fact]
    public async Task Resource_Graph_is_the_only_POST_and_carries_only_the_predefined_query()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"data":[]}"""));
        await Client(handler, new FakeSignIn()).QueryAsync(AzureGraphQuery.Resources, ["11111111-1111-1111-1111-111111111111"], "tok", default);
        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Url.Should().Be("https://management.azure.com" + AzureReadOnlyPolicy.ResourceGraphPath);
        request.Body.Should().Contain(AzureGraphQuery.Resources.Kql.Replace("'", "\\u0027")).And.Contain("\"$skipToken\":\"tok\"").And.Contain("\"$top\":1000");
        typeof(AzureGraphQuery).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Should().BeEmpty("no caller can construct a query from its own text");
    }

    [Fact]
    public async Task Throttling_is_retried_within_a_bound_and_then_reported()
    {
        var handler = new RecordingHandler(_ => Json((HttpStatusCode)429, """{"error":{"code":"TooManyRequests"}}""", TimeSpan.Zero), _ => Json(HttpStatusCode.OK, """{"value":[]}"""));
        (await Client(handler, new FakeSignIn()).GetAsync("/subscriptions?api-version=2022-12-01", default)).Ok.Should().BeTrue();
        handler.Requests.Should().HaveCount(2);

        var always = new RecordingHandler(_ => Json((HttpStatusCode)429, """{"error":{"code":"TooManyRequests"}}""", TimeSpan.Zero));
        var result = await Client(always, new FakeSignIn()).GetAsync("/subscriptions?api-version=2022-12-01", default);
        result.Should().Match<ArmResult>(r => r.Throttled && r.ErrorCode == "TooManyRequests");
        always.Requests.Should().HaveCount(3);

        var tooLong = new RecordingHandler(_ => Json((HttpStatusCode)429, "{}", TimeSpan.FromMinutes(5)));
        (await Client(tooLong, new FakeSignIn()).GetAsync("/subscriptions?api-version=2022-12-01", default)).Throttled.Should().BeTrue();
        tooLong.Requests.Should().ContainSingle("a long Retry-After is reported, not slept through");
    }

    [Fact]
    public async Task A_401_refreshes_the_token_once_then_marks_the_session_expired()
    {
        var recovers = new RecordingHandler(_ => Json(HttpStatusCode.Unauthorized, """{"error":{"code":"ExpiredAuthenticationToken"}}"""), _ => Json(HttpStatusCode.OK, """{"value":[]}"""));
        var signIn = new FakeSignIn();
        (await Client(recovers, signIn).GetAsync("/subscriptions?api-version=2022-12-01", default)).Ok.Should().BeTrue();
        signIn.Refreshes.Should().Be(1);
        signIn.Expired.Should().BeFalse();

        var rejects = new RecordingHandler(_ => Json(HttpStatusCode.Unauthorized, """{"error":{"code":"InvalidAuthenticationToken"}}"""));
        var expired = new FakeSignIn();
        var result = await Client(rejects, expired).GetAsync("/subscriptions?api-version=2022-12-01", default);
        result.Status.Should().Be(401);
        expired.Expired.Should().BeTrue();
        rejects.Requests.Should().HaveCount(2);

        var signedOut = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        (await Client(signedOut, new FakeSignIn(BirkNext.AzureEnvironment.AzureConnectionState.SignedOut)).GetAsync("/subscriptions?api-version=2022-12-01", default))
            .ErrorCode.Should().Be("NotSignedIn");
        signedOut.Requests.Should().BeEmpty();
    }

    private static readonly string Api = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../BirkNext.Api"));

    private static IEnumerable<(string Rel, string Text)> AzureSources() => Directory.GetFiles(Path.Combine(Api, "Services/AzureEnvironment"), "*.cs")
        .Concat([Path.Combine(Api, "Controllers/AzureEnvironmentController.cs")])
        .Select(f => (Path.GetRelativePath(Api, f).Replace('\\', '/'), File.ReadAllText(f)));

    [Fact]
    public void Azure_Environment_code_has_no_mutating_method_secret_operation_or_free_form_query()
    {
        foreach (var (rel, text) in AzureSources())
        {
            Regex.IsMatch(text, @"HttpMethod\.(Put|Patch|Delete)|new HttpMethod\(|SendAsync\(\s*HttpMethod|PostAsync|PutAsync|PatchAsync|DeleteAsync").Should().BeFalse($"{rel} must not send a mutating request");
            Regex.IsMatch(text, @"Azure\.ResourceManager|ArmClient|SecretClient|KeyClient|CertificateClient|BlobContainerClient|ServiceBusReceiver|EventHubConsumerClient|NpgsqlConnection|SqlConnection")
                .Should().BeFalse($"{rel} must not use data-plane or mutating SDK clients");
            Regex.IsMatch(text, @"\bUsernamePassword|AcquireTokenByUsernamePassword|WithClientSecret|ClientSecretCredential|WithCertificate").Should().BeFalse($"{rel} must not use stored credentials");
            Regex.IsMatch(text, @"roleAssignmentScheduleRequests|roleEligibilityScheduleRequests|/activate").Should().BeFalse($"{rel} must never request a PIM activation");
        }
        AzureSources().Sum(f => Regex.Matches(f.Text, @"HttpMethod\.Post").Count).Should().Be(1, "the one POST is the Resource Graph read");
        AzureSources().Single(f => f.Text.Contains("HttpMethod.Post", StringComparison.Ordinal)).Rel.Should().Be("Services/AzureEnvironment/AzureManagementClient.cs");
        AzureSources().Where(f => Regex.IsMatch(f.Text, @"\b(Resources|ResourceContainers)\s*\|")).Select(f => f.Rel).Should().Equal(["Services/AzureEnvironment/AzureManagementClient.cs"],
            "KQL exists only as the predefined AzureGraphQuery constants");
    }

    [Fact]
    public void Tokens_are_never_logged_or_persisted()
    {
        foreach (var (rel, text) in AzureSources())
        {
            Regex.IsMatch(text, @"Log\w*\([^;]*(AccessToken|\btoken\b|Authorization|signInUri|uri\b)", RegexOptions.IgnoreCase).Should().BeFalse($"{rel} must not log tokens, headers or sign-in URLs");
            Regex.IsMatch(text, @"TokenCacheNotification|SetBeforeAccess|SetAfterAccess|WithCacheOptions").Should().BeFalse($"{rel} must keep the MSAL cache in memory only");
        }
        typeof(BirkNext.AzureEnvironment.AzureEnvironmentSnapshot).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Token", StringComparison.OrdinalIgnoreCase));
        typeof(BirkNext.AzureEnvironment.AzureConnectionStatus).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }
}
