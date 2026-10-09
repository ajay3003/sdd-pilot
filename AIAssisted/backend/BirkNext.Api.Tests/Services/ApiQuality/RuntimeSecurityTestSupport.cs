using System.Net;
using System.Text;
using BirkNext.Api.Services.ApiQuality.Fuzzing;
using BirkNext.Api.Services.ApiQuality.Security;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Tests.Services.ApiQuality;

/// <summary>Loopback-free fixtures for the runtime security checks: every response is synthetic, nothing leaves the process.</summary>
internal static class RuntimeSecurityTestSupport
{
    public const string Fp = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
    public const string ApiOrigin = "https://api-dev.example.test";
    public const string SecretToken = "eyJhbGciOiJSUzI1NiJ9.SENTINEL-TOKEN-NEVER-STORED.sig";
    public const string CookieSentinel = "SENTINEL-COOKIE-VALUE-7f3a";

    internal sealed class Handler : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        public readonly List<string?> Bodies = [];
        public Func<HttpRequestMessage, string?, HttpResponseMessage> Respond { get; set; } = (_, _) => Text(HttpStatusCode.NotFound, "");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            return Respond(request, body);
        }
    }

    public static HttpResponseMessage Text(HttpStatusCode status, string body, string contentType = "application/json", Action<HttpResponseMessage>? headers = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        headers?.Invoke(response);
        return response;
    }

    public static ApiReviewTarget Rest(bool auth = false, string? contract = null, string host = "api-dev.example.test", string basePath = "/api/children") => new()
    {
        TargetId = "rest-1", EnvironmentId = "dev", ApiType = ApiReviewTargetType.Rest, Scheme = "https", Host = host, Port = 443, BasePath = basePath, ServiceName = "Children API",
        Source = ApiReviewTargetSource.Configured, AuthRequired = auth, ContractSource = contract, Confidence = ObservedEndpointConfidence.Verified, Selected = true,
        Operations = [new ApiReviewOperation { Method = "GET", Path = basePath, ObservedCount = 1, AuthObserved = auth, LastStatus = 200 }],
    };

    public static ApiReviewRunRequest ReviewRequest(string type = "Development", BirkNext.RuntimeSecurity.ApiRuntimeSecurityPolicy? runtime = null,
        AuthenticatedTestingMethod method = AuthenticatedTestingMethod.ManagedEdgeCdp, params ApiReviewTarget[] targets) => new()
    {
        Environment = new ApiReviewEnvironmentSnapshot { EnvironmentId = "dev", Name = "DEV", EnvironmentType = type, IsProduction = type == "Production", TargetUrl = "https://app-dev.example.test/", AuthenticatedTestingMethod = method },
        Identity = new AuthenticatedReviewIdentity(method, method == AuthenticatedTestingMethod.LocalHttpsProxy ? "dev" : null, method == AuthenticatedTestingMethod.LocalHttpsProxy ? Fp : null),
        Targets = targets.Length == 0 ? [Rest()] : [.. targets],
        Policy = new ApiReviewPolicy { ErrorHandlingProbes = false, RuntimeSecurity = runtime },
        FrontendOrigin = "https://app-dev.example.test",
    };

    public static ITrustedSecurityTargetRegistry Registry(string type = "Development", params string[] origins) => new TrustedSecurityTargetRegistry(Options.Create(new SecurityTestingOptions
    {
        TrustedTargets = new() { ["dev"] = new TrustedSecurityTarget { EnvironmentType = type, ApiOrigins = origins.Length == 0 ? [ApiOrigin] : [.. origins] } },
    }));

    /// <summary>Authenticated gateway double: the proxy-session credential is applied here and never returned.</summary>
    internal sealed class Gateway(bool available = true, int status = 200, bool? graphQlData = null, List<string>? errorCodes = null) : IAuthenticatedReviewGateway
    {
        public readonly List<ApiSafeRequest> SafeRequests = [];
        public int RestCalls;
        public int GraphQlCalls;
        public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity) => available && identity.Method == AuthenticatedTestingMethod.LocalHttpsProxy
            ? new() { Method = identity.Method, ContextStatus = AuthenticatedApiContextStatus.Available, PublicApi = true, AuthenticatedApi = true, AuthenticatedRest = true, AuthenticatedGraphQlQuery = true, Reason = "ok" }
            : new() { Method = identity.Method, ContextStatus = AuthenticatedApiContextStatus.NotApplicable, PublicApi = true, Reason = "public only" };
        private AuthenticatedReviewExecutionOutcome Outcome() => new()
        {
            Status = AuthenticatedExecutionStatus.Executed, Mode = ReviewExecutionMode.AuthenticatedViaLocalHttpsProxy, Message = $"HTTP {status}",
            Result = new AuthenticatedApiExecutionResult { StatusCode = status, GraphQlHasData = graphQlData, GraphQlErrorCount = errorCodes?.Count ?? 0, GraphQlErrorCodes = errorCodes ?? [], Outcome = $"HTTP {status}" },
        };
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(AuthenticatedReviewIdentity identity, string httpMethod, string url, CancellationToken cancellationToken = default) { RestCalls++; return Task.FromResult(Outcome()); }
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(AuthenticatedReviewIdentity identity, string endpointUrl, string query, CancellationToken cancellationToken = default) { GraphQlCalls++; return Task.FromResult(Outcome()); }
        public Task<AuthenticatedGraphQlSchemaOutcome> FetchGraphQlSchemaAsync(AuthenticatedReviewIdentity identity, string endpointUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthenticatedGraphQlSchemaOutcome { Status = AuthenticatedExecutionStatus.NoContext, Message = "none" });
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteSafeRequestAsync(AuthenticatedReviewIdentity identity, ApiSafeRequest request, CancellationToken cancellationToken = default)
        {
            SafeRequests.Add(request);
            return Task.FromResult(Outcome());
        }
    }
}
