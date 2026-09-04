using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Web.Tests.Services;

public sealed class TargetEnvironmentDetectionApiServiceBrowserContinuationTests
{
    [Fact]
    public async Task DetectFromUrlAsync_UsesConfiguredLocalBackendBaseAddress()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"success\":true}", Encoding.UTF8, "application/json")
        });

        await CreateService(handler).DetectFromUrlAsync("https://qa.example.test/");

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("http", handler.Request.RequestUri!.Scheme);
        Assert.Equal("localhost", handler.Request.RequestUri.Host);
        Assert.Equal(5000, handler.Request.RequestUri.Port);
        Assert.Equal("/api/frontend-target/detect", handler.Request.RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task StartBrowserDetectionAsync_PostsExactContractAndDeserializesOutcome()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new TargetDetectionOutcome
        {
            State = DetectionState.Complete,
            IsActivationReady = true,
            StrategySuggestion = "Browser authentication completed",
            DetectionResponse = new TargetEnvironmentDetectionResult
            {
                OriginalUrl = "https://qa.example.test/", Success = true, Reachability = TargetReachability.Reachable
            }
        }));
        var result = await CreateService(handler).StartBrowserDetectionAsync(
            "https://qa.example.test/", "detection-qa-123", "qa");

        Assert.NotNull(result);
        Assert.Equal(DetectionState.Complete, result.State);
        Assert.True(result.IsActivationReady);
        Assert.Equal("Browser authentication completed", result.StrategySuggestion);
        Assert.True(result.DetectionResponse!.Success);
        Assert.Equal(TargetReachability.Reachable, result.DetectionResponse.Reachability);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("http://localhost:5000/api/frontend-target/continue-in-browser", handler.Request.RequestUri!.AbsoluteUri);
        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("https://qa.example.test/", json.RootElement.GetProperty("targetUrl").GetString());
        Assert.Equal("detection-qa-123", json.RootElement.GetProperty("reviewSessionId").GetString());
        Assert.Equal("qa", json.RootElement.GetProperty("profileId").GetString());
    }

    [Theory]
    [InlineData("", "session", "profile")]
    [InlineData("https://qa.example.test", "", "profile")]
    [InlineData("https://qa.example.test", "session", "")]
    public async Task StartBrowserDetectionAsync_MissingRequiredValue_DoesNotSend(string url, string sessionId, string profileId)
    {
        var handler = new RecordingHandler(_ => JsonResponse(new TargetDetectionOutcome()));
        var result = await CreateService(handler).StartBrowserDetectionAsync(url, sessionId, profileId);
        Assert.Null(result);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task StartBrowserDetectionAsync_NonSuccess_ReturnsNull()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        Assert.Null(await CreateService(handler).StartBrowserDetectionAsync("https://qa.example.test", "session", "qa"));
    }

    [Fact]
    public async Task StartBrowserDetectionAsync_HttpFailure_ReturnsNull()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("sentinel-network-detail"));
        Assert.Null(await CreateService(handler).StartBrowserDetectionAsync("https://qa.example.test", "session", "qa"));
    }

    [Fact]
    public async Task StartBrowserDetectionAsync_Timeout_ReturnsNull()
    {
        var handler = new RecordingHandler(_ => throw new TaskCanceledException("timeout"));
        Assert.Null(await CreateService(handler).StartBrowserDetectionAsync("https://qa.example.test", "session", "qa"));
    }

    [Fact]
    public async Task StartBrowserDetectionAsync_MalformedJson_ReturnsNull()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not-json", Encoding.UTF8, "application/json")
        });
        Assert.Null(await CreateService(handler).StartBrowserDetectionAsync("https://qa.example.test", "session", "qa"));
    }

    private static TargetEnvironmentDetectionApiService CreateService(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") }, NullLogger<TargetEnvironmentDetectionApiService>.Instance);

    private static HttpResponseMessage JsonResponse(TargetDetectionOutcome outcome) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(outcome), Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return response(request);
        }
    }
}
