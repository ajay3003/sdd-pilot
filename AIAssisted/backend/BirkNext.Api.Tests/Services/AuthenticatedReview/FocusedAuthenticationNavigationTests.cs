using BirkNext.Api.Services.AuthenticatedReview;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Moq;
using Xunit;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.AuthenticatedReview;

/// <summary>
/// Critical test: Verify BeginAuthenticationAsync is invoked and performs navigation.
/// Root cause verification for blank browser issue: GotoAsync must be called in BeginAuthenticationAsync.
/// </summary>
[Trait("Category", "FocusedAuthenticationNavigation")]
public sealed class FocusedAuthenticationNavigationTests
{
    private const string Review = "focused-test-review";
    private const string Profile = "focused-test-profile";
    private const string TargetUrl = "https://example.com:8080/path";

    [Fact]
    public async Task BeginAuthenticationAsync_MustCallGotoAsync_OrPageRemainsBlank()
    {
        // ROOT CAUSE: If BeginAuthenticationAsync doesn't call Page.GotoAsync,
        // the page stays at about:blank (created in LaunchAsync), causing blank browser.

        // Arrange
        var mockPage = new Mock<IPage>();
        var mockBrowser = new Mock<IBrowser>();
        var mockContext = new Mock<IBrowserContext>();
        var mockResponse = new Mock<IResponse>();
        var gotoWasCalled = false;

        mockPage.Setup(p => p.Url).Returns("about:blank");
        mockPage.Setup(p => p.IsClosed).Returns(false);
        mockPage.Setup(p => p.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions>()))
            .Callback(() => gotoWasCalled = true)
            .ReturnsAsync(mockResponse.Object);

        mockPage.Setup(p => p.FrameNavigated += It.IsAny<EventHandler<IFrame>>());
        mockBrowser.Setup(b => b.IsConnected).Returns(true);

        var mockHost = new Mock<IAuthenticatedBrowserHost>();
        mockHost.Setup(h => h.LaunchAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<IAuthenticatedBrowserResources>(
                new TestResources(mockBrowser.Object, mockContext.Object, mockPage.Object)));

        var manager = CreateManager(mockHost.Object);

        // Act
        var session = await manager.StartAsync(
            new AuthenticatedBrowserSessionRequest(Review, Profile, TargetUrl));

        session.Status.Should().Be(AuthenticatedBrowserSessionStatus.BrowserReady);

        // Before BeginAuthenticationAsync, page must not have navigated
        gotoWasCalled.Should().BeFalse();

        var authSession = await manager.BeginAuthenticationAsync(
            new BeginAuthenticationRequest(session.SessionId, Review, Profile, "https://example.com"),
            CancellationToken.None);

        // Assert
        // CRITICAL: BeginAuthenticationAsync MUST call GotoAsync
        gotoWasCalled.Should().BeTrue(
            because: "BeginAuthenticationAsync must call Page.GotoAsync to navigate to target; " +
                     "without this call, page remains about:blank causing blank browser symptom");

        authSession.Status.Should().Be(AuthenticatedBrowserSessionStatus.AuthenticationInProgress);

        mockPage.Verify(
            p => p.GotoAsync(TargetUrl, It.IsAny<PageGotoOptions>()),
            Times.Once,
            "Page.GotoAsync must be called exactly once with the target URL");
    }

    private static AuthenticatedBrowserSessionManager CreateManager(IAuthenticatedBrowserHost host) =>
        new(
            host,
            Options.Create(new AuthenticatedReviewOptions("local", true, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5))),
            TimeProvider.System,
            NullLogger<AuthenticatedBrowserSessionManager>.Instance);

    private sealed class TestResources : IAuthenticatedBrowserResources
    {
        public IBrowser Browser { get; }
        public IBrowserContext Context { get; }
        public IPage Page { get; }
        public event EventHandler? BrowserDisconnected;

        public TestResources(IBrowser browser, IBrowserContext context, IPage page)
        {
            Browser = browser;
            Context = context;
            Page = page;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
