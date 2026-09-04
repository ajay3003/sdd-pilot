using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Api.Controllers;
using BirkNext.Api.Models;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.TargetEnvironmentDetection;
using BirkNext.Api.Tests.Services.TargetEnvironmentDetection;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Api.Tests.Controllers;

public sealed class TargetEnvironmentContinuationClosureTests
{
    private const string TargetUrl = "https://app.example.com/qa";
    private const string ReviewSessionId = "review-session-closure";
    private const string ProfileId = "qa-profile-closure";
    private static readonly string[] Sentinels =
    [
        "SUPER_SECRET_ACCESS_TOKEN", "SUPER_SECRET_REFRESH_TOKEN", "SUPER_SECRET_COOKIE",
        "SUPER_SECRET_PASSWORD", "SUPER_SECRET_AUTHORIZATION"
    ];

    [Fact]
    public async Task Controller_RealService_ConcreteInteractiveStrategy_InvokesSessionManagerAfterSafePreflight()
    {
        var manager = new RecordingSessionManager(string.Join('|', Sentinels));
        var (controller, preflight) = CreateController(DetectionFixtures.UnauthorizedTarget(), manager);

        var action = await controller.ContinueDetectionInBrowser(new BrowserDetectionRequest
        {
            TargetUrl = TargetUrl, ReviewSessionId = ReviewSessionId, ProfileId = ProfileId
        }, CancellationToken.None);

        preflight.RequestCount.Should().BeGreaterThan(0);
        manager.StartCount.Should().Be(1);
        manager.StartRequest.Should().Be(new AuthenticatedBrowserSessionRequest(ReviewSessionId, ProfileId, TargetUrl));
        manager.BeginCount.Should().Be(1);
        var outcome = action.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<TargetDetectionOutcome>().Subject;
        outcome.State.Should().Be(TargetDetectionState.Complete);
        outcome.IsActivationReady.Should().BeTrue();

        var json = JsonSerializer.Serialize(outcome, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var sentinel in Sentinels)
        {
            outcome.Message.Should().NotContain(sentinel);
            outcome.StrategySuggestion.Should().NotContain(sentinel);
            json.Should().NotContain(sentinel);
        }
    }

    [Fact]
    public async Task Controller_RealService_PreflightFailure_DoesNotInvokeInteractiveSessionManager()
    {
        var manager = new RecordingSessionManager();
        var (controller, preflight) = CreateController(DetectionFixtures.TimeoutTarget(), manager);

        var action = await controller.ContinueDetectionInBrowser(new BrowserDetectionRequest
        {
            TargetUrl = TargetUrl, ReviewSessionId = ReviewSessionId, ProfileId = ProfileId
        }, CancellationToken.None);

        preflight.RequestCount.Should().BeGreaterThan(0);
        manager.StartCount.Should().Be(0);
        var outcome = action.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<TargetDetectionOutcome>().Subject;
        outcome.State.Should().Be(TargetDetectionState.Failed);
        outcome.IsActivationReady.Should().BeFalse();
    }

    [Fact]
    public async Task MalformedUrl_ThroughAspNetPipeline_Returns400WithoutServiceOrStrategy()
    {
        var service = new Mock<ITargetEnvironmentDetectionService>(MockBehavior.Strict);
        var manager = new RecordingSessionManager();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITargetEnvironmentDetectionService>();
                services.RemoveAll<IAuthenticatedBrowserSessionManager>();
                services.AddSingleton(service.Object);
                services.AddSingleton<IAuthenticatedBrowserSessionManager>(manager);
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.PostAsJsonAsync("/api/frontend-target/continue-in-browser", new
        {
            targetUrl = "not a valid URI", reviewSessionId = ReviewSessionId, profileId = ProfileId
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Target URL");
        service.VerifyNoOtherCalls();
        manager.StartCount.Should().Be(0);
    }

    private static (TargetEnvironmentDetectionController Controller, CountingHandler Preflight) CreateController(
        HttpMessageHandler innerHandler, RecordingSessionManager manager)
    {
        var preflight = new CountingHandler(innerHandler);
        var resolver = new FakeDnsResolver();
        resolver.Add("app.example.com", "203.0.113.1");
        var service = new TargetEnvironmentDetectionService(
            new BrowserTargetValidator(), new HttpClient(preflight), resolver,
            new ClientFrameworkDetector(),
            NullLogger<TargetEnvironmentDetectionService>.Instance);
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticatedBrowserSessionManager>(manager)
            .AddSingleton<ILogger<InteractiveBrowserDetectionStrategy>>(NullLogger<InteractiveBrowserDetectionStrategy>.Instance)
            .BuildServiceProvider();
        var controller = new TargetEnvironmentDetectionController(
            service, NullLogger<TargetEnvironmentDetectionController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services }
            }
        };
        return (controller, preflight);
    }

    private sealed class CountingHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingSessionManager(string? secretFailureCategory = null) : IAuthenticatedBrowserSessionManager
    {
        public int StartCount { get; private set; }
        public int BeginCount { get; private set; }
        public AuthenticatedBrowserSessionRequest? StartRequest { get; private set; }

        public Task<AuthenticatedBrowserSessionDescriptor> StartAsync(AuthenticatedBrowserSessionRequest request, CancellationToken cancellationToken = default)
        {
            StartCount++;
            StartRequest = request;
            return Task.FromResult(Descriptor(AuthenticatedBrowserSessionStatus.BrowserReady));
        }

        public Task<AuthenticatedBrowserSessionDescriptor> BeginAuthenticationAsync(BeginAuthenticationRequest request, CancellationToken cancellationToken = default)
        {
            BeginCount++;
            request.ReviewSessionId.Should().Be(ReviewSessionId);
            request.ProfileId.Should().Be(ProfileId);
            return Task.FromResult(Descriptor(AuthenticatedBrowserSessionStatus.AuthenticationInProgress));
        }

        public Task<AuthenticatedBrowserSessionDescriptor?> GetStatusAsync(string sessionId, string reviewSessionId, string profileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthenticatedBrowserSessionDescriptor?>(Descriptor(AuthenticatedBrowserSessionStatus.Authenticated));

        public Task<bool> CancelAsync(string sessionId, string reviewSessionId, string profileId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<IAuthenticatedBrowserPageLease> AcquireAuthenticationPageLeaseAsync(string sessionId, string reviewSessionId, string profileId, string targetUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IAuthenticatedBrowserPageLease> AcquirePageLeaseAsync(string sessionId, string reviewSessionId, string profileId, string targetUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private AuthenticatedBrowserSessionDescriptor Descriptor(AuthenticatedBrowserSessionStatus status) =>
            new("browser-session-closure", ReviewSessionId, ProfileId, "https://app.example.com", status,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), secretFailureCategory,
                AuthenticatedDeliveryContext.DirectApplication, true);
    }
}
