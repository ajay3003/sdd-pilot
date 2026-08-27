namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

/// <summary>
/// Deterministic test fixtures for target environment detection.
/// Uses in-memory HTTP message handlers to simulate various target responses.
/// All metadata uses FAKE sentinels - never real credentials or secrets.
/// </summary>
public static class DetectionFixtures
{
    public const string FakeTenantGuid = "12345678-1234-1234-1234-123456789012";
    public const string FakeClientId = "FAKE-CLIENT-ID-SENTINEL-123";
    public const string FakeCodeSentinel = "FAKE-CODE-SENTINEL-123";
    public const string FakeStateSentinel = "FAKE-STATE-SENTINEL-123";
    public const string FakeNonceSentinel = "FAKE-NONCE-SENTINEL-123";
    public const string FakeAccessTokenSentinel = "FAKE-ACCESS-TOKEN-SENTINEL-123";

    /// <summary>
    /// Public reachable target - returns 200 OK
    /// </summary>
    public static HttpMessageHandler PublicReachableTarget()
    {
        return new FakeHttpMessageHandler(request =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(""),
                RequestMessage = request
            });
    }

    /// <summary>
    /// Target returns 401 Unauthorized
    /// </summary>
    public static HttpMessageHandler UnauthorizedTarget()
    {
        return new FakeHttpMessageHandler(request =>
            new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            {
                RequestMessage = request
            });
    }

    /// <summary>
    /// Target returns 403 Forbidden
    /// </summary>
    public static HttpMessageHandler ForbiddenTarget()
    {
        return new FakeHttpMessageHandler(request =>
            new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
            {
                RequestMessage = request
            });
    }

    /// <summary>
    /// Microsoft Entra redirect with concrete tenant GUID
    /// </summary>
    public static HttpMessageHandler EntraWithGuidTenant()
    {
        return new FakeHttpMessageHandler(request =>
        {
            var initialUri = request.RequestUri;

            // First request redirects to Entra with concrete tenant
            if (initialUri?.Host != "login.microsoftonline.com")
            {
                var entraUrl = $"https://login.microsoftonline.com/{FakeTenantGuid}/oauth2/v2.0/authorize?client_id={FakeClientId}&state={FakeStateSentinel}&nonce={FakeNonceSentinel}";
                return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                {
                    Headers = { { "Location", entraUrl } },
                    RequestMessage = request,
                    RequestMessage = { RequestUri = new Uri(entraUrl) }
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request
            };
        });
    }

    /// <summary>
    /// Microsoft Entra redirect with "common" tenant
    /// </summary>
    public static HttpMessageHandler EntraWithCommonTenant()
    {
        return new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri?.Host != "login.microsoftonline.com")
            {
                var entraUrl = $"https://login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id={FakeClientId}";
                return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                {
                    Headers = { { "Location", entraUrl } },
                    RequestMessage = request,
                    RequestMessage = { RequestUri = new Uri(entraUrl) }
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request
            };
        });
    }

    /// <summary>
    /// Microsoft Entra redirect with "organizations" tenant
    /// </summary>
    public static HttpMessageHandler EntraWithOrganizationsTenant()
    {
        return new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri?.Host != "login.microsoftonline.com")
            {
                var entraUrl = $"https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize?client_id={FakeClientId}";
                return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                {
                    Headers = { { "Location", entraUrl } },
                    RequestMessage = request,
                    RequestMessage = { RequestUri = new Uri(entraUrl) }
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request
            };
        });
    }

    /// <summary>
    /// Too many redirects
    /// </summary>
    public static HttpMessageHandler TooManyRedirects()
    {
        var requestCount = 0;
        return new FakeHttpMessageHandler(request =>
        {
            requestCount++;
            if (requestCount > 20)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    RequestMessage = request
                };
            }

            var nextUrl = $"https://example.com/redirect{requestCount}";
            return new HttpResponseMessage(System.Net.HttpStatusCode.Redirect)
            {
                Headers = { { "Location", nextUrl } },
                RequestMessage = request,
                RequestMessage = { RequestUri = new Uri(nextUrl) }
            };
        });
    }

    /// <summary>
    /// Timeout simulation
    /// </summary>
    public static HttpMessageHandler TimeoutTarget()
    {
        return new FakeHttpMessageHandler(request =>
            throw new TaskCanceledException());
    }

    /// <summary>
    /// Untrusted redirect (to private IP)
    /// </summary>
    public static HttpMessageHandler UntrustedRedirect()
    {
        return new FakeHttpMessageHandler(request =>
        {
            if (!request.RequestUri?.Host.StartsWith("192.168") == true)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.Redirect)
                {
                    Headers = { { "Location", "http://192.168.1.1/internal" } },
                    RequestMessage = request,
                    RequestMessage = { RequestUri = new Uri("http://192.168.1.1/internal") }
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request
            };
        });
    }

    /// <summary>
    /// Fake HTTP message handler for testing
    /// </summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return await Task.FromResult(_handler(request));
        }
    }
}
