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

    public static HttpMessageHandler PublicReachableTarget()
    {
        return new FakeHttpMessageHandler(request =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(""),
                RequestMessage = request
            });
    }

    public static HttpMessageHandler BlazorWasmTarget()
    {
        return new FakeHttpMessageHandler(request =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<html><body><script src=\"_framework/blazor.webassembly.js\"></script></body></html>",
                    System.Text.Encoding.UTF8,
                    "text/html"),
                RequestMessage = request
            });
    }

    public static HttpMessageHandler UnauthorizedTarget()
    {
        return new FakeHttpMessageHandler(request =>
            new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
            {
                RequestMessage = request
            });
    }

    public static HttpMessageHandler ForbiddenTarget()
    {
        return new FakeHttpMessageHandler(request =>
            new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
            {
                RequestMessage = request
            });
    }

    public static HttpMessageHandler EntraAuthUrlDirect()
    {
        return new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri?.Host == "login.microsoftonline.com")
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("<!--auth page-->"),
                    RequestMessage = request
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request
            };
        });
    }

    public static HttpMessageHandler TimeoutTarget()
    {
        return new FakeHttpMessageHandler(request =>
            throw new TaskCanceledException());
    }

    public static HttpMessageHandler UnknownAuthProvider()
    {
        return new FakeHttpMessageHandler(request =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request
            });
    }

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
