using BirkNext.Api.Controllers;
using BirkNext.Api.Services.AuthenticatedReview;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Moq;
using System.Text.Json;

namespace BirkNext.Api.Tests.Services.AuthenticatedReview;

public sealed class AuthenticatedBrowserSessionManagerTests
{
    [Fact]
    public async Task Start_CreatesOpaqueUniqueSessionBoundToProfileAndOrigin()
    {
        var host = new FakeHost(); await using var manager = CreateManager(host);
        var first = await manager.StartAsync(Request("review-1", "profile-1", "https://Example.com/path?q=secret"));
        var second = await manager.StartAsync(Request("review-2", "profile-2", "https://example.org/else"));

        first.SessionId.Should().MatchRegex("^[0-9a-f]{64}$").And.NotContain("example").And.NotBe(second.SessionId);
        first.ProfileId.Should().Be("profile-1");
        first.ReviewSessionId.Should().Be("review-1");
        first.TargetOrigin.Should().Be("https://example.com");
        first.Status.Should().Be(AuthenticatedBrowserSessionStatus.BrowserReady);
        host.LaunchCount.Should().Be(2);
    }

    [Fact]
    public async Task DuplicateStart_ReturnsSameSessionWithoutSecondBrowser()
    {
        var host = new FakeHost(); await using var manager = CreateManager(host);
        var first = await manager.StartAsync(Request());
        var second = await manager.StartAsync(Request());
        second.SessionId.Should().Be(first.SessionId);
        host.LaunchCount.Should().Be(1);
    }

    [Fact]
    public async Task DuplicateStart_WithDifferentTarget_IsRejected()
    {
        await using var manager = CreateManager(new FakeHost());
        await manager.StartAsync(Request());
        var act = () => manager.StartAsync(Request(target: "https://other.example"));
        await act.Should().ThrowAsync<AuthenticatedSessionConflictException>();
    }

    [Fact]
    public async Task SeparateReviews_OwnSeparateBrowserResources()
    {
        var host = new FakeHost(); await using var manager = CreateManager(host);
        await manager.StartAsync(Request("review-1"));
        await manager.StartAsync(Request("review-2"));
        host.Resources.Should().HaveCount(2).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Cancel_DisposesOnlyOwnedBrowser_AndIsIdempotent()
    {
        var host = new FakeHost(); await using var manager = CreateManager(host);
        var first = await manager.StartAsync(Request("review-1"));
        var second = await manager.StartAsync(Request("review-2"));

        (await manager.CancelAsync(first.SessionId, "review-1", "profile-1")).Should().BeTrue();
        (await manager.CancelAsync(first.SessionId, "review-1", "profile-1")).Should().BeTrue();
        host.Resources[0].DisposeCount.Should().Be(1);
        host.Resources[1].DisposeCount.Should().Be(0);
        (await manager.GetStatusAsync(second.SessionId, "review-2", "profile-1")).Should().NotBeNull();
    }

    [Fact]
    public async Task WrongOwner_CannotStatusCancelOrLease()
    {
        await using var manager = CreateManager(new FakeHost());
        var session = await manager.StartAsync(Request());
        (await manager.GetStatusAsync(session.SessionId, "wrong", "profile-1")).Should().BeNull();
        (await manager.CancelAsync(session.SessionId, "wrong", "profile-1")).Should().BeFalse();
        var act = () => manager.AcquirePageLeaseAsync(session.SessionId, "wrong", "profile-1", "https://example.com");
        await act.Should().ThrowAsync<System.Collections.Generic.KeyNotFoundException>();
    }

    [Fact]
    public async Task WrongTarget_CannotAcquireLease()
    {
        await using var manager = CreateManager(new FakeHost());
        var session = await manager.StartAsync(Request());
        var act = () => manager.AcquirePageLeaseAsync(session.SessionId, "review-1", "profile-1", "https://other.example");
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Lease_UsesSamePageAndCannotDisposeSessionResources()
    {
        var host = new FakeHost(); await using var manager = CreateManager(host);
        var session = await manager.StartAsync(Request());
        await using (var lease = await manager.AcquirePageLeaseAsync(session.SessionId, "review-1", "profile-1", "https://example.com/path"))
        {
            lease.Page.Should().BeSameAs(host.Resources[0].Page);
            lease.Context.Should().BeSameAs(host.Resources[0].Context);
        }
        host.Resources[0].DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task Lease_IsRejectedAfterCancel()
    {
        await using var manager = CreateManager(new FakeHost());
        var session = await manager.StartAsync(Request());
        await manager.CancelAsync(session.SessionId, "review-1", "profile-1");
        var act = () => manager.AcquirePageLeaseAsync(session.SessionId, "review-1", "profile-1", "https://example.com");
        await act.Should().ThrowAsync<System.Collections.Generic.KeyNotFoundException>();
    }

    [Fact]
    public async Task AbsoluteExpiry_DisposesAndRejectsLease()
    {
        var time = new MutableTimeProvider(); var host = new FakeHost();
        await using var manager = CreateManager(host, time: time);
        var session = await manager.StartAsync(Request());
        time.Advance(TimeSpan.FromMinutes(46));
        var act = () => manager.AcquirePageLeaseAsync(session.SessionId, "review-1", "profile-1", "https://example.com");
        await act.Should().ThrowAsync<AuthenticatedSessionExpiredException>();
        await EventuallyAsync(() => host.Resources[0].DisposeCount == 1);
    }

    [Fact]
    public async Task InactivityExpiry_IsNotExtendedByStatusPolling()
    {
        var time = new MutableTimeProvider(); var host = new FakeHost();
        await using var manager = CreateManager(host, time: time);
        var session = await manager.StartAsync(Request());
        time.Advance(TimeSpan.FromMinutes(16));
        var status = await manager.GetStatusAsync(session.SessionId, "review-1", "profile-1");
        status!.Status.Should().Be(AuthenticatedBrowserSessionStatus.Expired);
        await EventuallyAsync(() => host.Resources[0].DisposeCount == 1);
    }

    [Fact]
    public async Task BrowserCrash_RemovesAndDisposesSession()
    {
        var host = new FakeHost(); await using var manager = CreateManager(host);
        var session = await manager.StartAsync(Request());
        host.Resources[0].Crash();
        await EventuallyAsync(() => host.Resources[0].DisposeCount == 1);
        (await manager.GetStatusAsync(session.SessionId, "review-1", "profile-1")).Should().BeNull();
    }

    [Fact]
    public async Task Shutdown_DisposesAllOwnedSessions()
    {
        var host = new FakeHost(); await using var manager = CreateManager(host);
        await manager.StartAsync(Request("review-1")); await manager.StartAsync(Request("review-2"));
        await manager.StopAsync(default);
        host.Resources.Should().OnlyContain(r => r.DisposeCount == 1);
    }

    [Theory]
    [InlineData(false, "LocalWorkstation")]
    [InlineData(true, "Unsupported")]
    [InlineData(true, "RemoteServer")]
    public async Task UnsupportedDeployment_FailsClosedWithoutLaunching(bool enabled, string runtime)
    {
        var host = new FakeHost(); await using var manager = CreateManager(host, enabled, runtime);
        var act = () => manager.StartAsync(Request());
        await act.Should().ThrowAsync<AuthenticatedReviewUnavailableException>();
        host.LaunchCount.Should().Be(0);
    }

    [Fact]
    public void AuthenticatedBrowser_IsExplicitlyHeaded()
    {
        PlaywrightAuthenticatedBrowserHost.CreateLaunchOptions().Headless.Should().BeFalse();
    }

    [Fact]
    public void ApiContracts_ContainNoSecretBearingProperties()
    {
        var forbidden = new[] { "password", "token", "cookie", "authorization", "storage", "localstorage", "sessionstorage", "browserpid" };
        var properties = new[] { typeof(StartAuthenticatedBrowserSessionRequest), typeof(AuthenticatedBrowserSessionOwnerRequest), typeof(AuthenticatedBrowserSessionResponse) }
            .SelectMany(t => t.GetProperties()).Select(p => p.Name.ToLowerInvariant()).ToArray();
        properties.Should().NotContain(p => forbidden.Any(p.Contains));

        var response = new AuthenticatedBrowserSessionResponse("opaque", AuthenticatedBrowserSessionStatus.BrowserReady, "https://example.com", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(45), null);
        var json = JsonSerializer.Serialize(response).ToLowerInvariant();
        json.Should().NotContain("password").And.NotContain("access token").And.NotContain("refresh token").And.NotContain("auth code").And.NotContain("cookie").And.NotContain("authorization").And.NotContain("storagestate");
    }

    [Fact]
    public void SessionManager_HasNoPersistenceDependency()
    {
        var dependencies = typeof(AuthenticatedBrowserSessionManager).GetConstructors().Single().GetParameters().Select(p => p.ParameterType.FullName ?? "");
        dependencies.Should().NotContain(name => name.Contains("DbContext", StringComparison.OrdinalIgnoreCase) || name.Contains("Repository", StringComparison.OrdinalIgnoreCase) || name.Contains("DistributedCache", StringComparison.OrdinalIgnoreCase));
    }

    private static AuthenticatedBrowserSessionRequest Request(string review = "review-1", string profile = "profile-1", string target = "https://example.com/path") => new(review, profile, target);
    private static AuthenticatedBrowserSessionManager CreateManager(FakeHost host, bool enabled = true, string runtime = "LocalWorkstation", TimeProvider? time = null) =>
        new(host, Options.Create(new AuthenticatedReviewOptions { Enabled = enabled, Runtime = runtime, AbsoluteLifetimeMinutes = 45, InactivityTimeoutMinutes = 15 }), time ?? TimeProvider.System, NullLogger<AuthenticatedBrowserSessionManager>.Instance);

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++) await Task.Delay(10);
        condition().Should().BeTrue();
    }

    private sealed class FakeHost : IAuthenticatedBrowserHost
    {
        public int LaunchCount { get; private set; }
        public List<FakeResources> Resources { get; } = [];
        public Task<IAuthenticatedBrowserResources> LaunchAsync(Uri target, CancellationToken cancellationToken)
        {
            LaunchCount++; var resources = new FakeResources(); Resources.Add(resources);
            return Task.FromResult<IAuthenticatedBrowserResources>(resources);
        }
    }

    private sealed class FakeResources : IAuthenticatedBrowserResources
    {
        public IBrowser Browser { get; } = new Mock<IBrowser>().Object;
        public IBrowserContext Context { get; } = new Mock<IBrowserContext>().Object;
        public IPage Page { get; } = new Mock<IPage>().Object;
        public int DisposeCount { get; private set; }
        public event EventHandler? BrowserDisconnected;
        public void Crash() => BrowserDisconnected?.Invoke(this, EventArgs.Empty);
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
