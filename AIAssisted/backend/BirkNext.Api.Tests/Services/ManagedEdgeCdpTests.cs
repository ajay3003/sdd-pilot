using BirkNext.Api.Services.ManagedEdge;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.ManagedEdge;
using Microsoft.Extensions.Options;
using Moq;

namespace BirkNext.Api.Tests.Services;

public sealed class ManagedEdgeCdpTests
{
    private const string Origin = "https://m2lbdev.bufetat.no";
    private readonly Mock<IManagedEdgeConnector> _connector = new(MockBehavior.Strict);
    private readonly Mock<IManagedEdgeBrowser> _browser = new();
    private readonly Mock<IManagedEdgePage> _page = new(MockBehavior.Strict);
    private readonly ManagedEdgeTargetRule _rule = new() { Origin = Origin, AuthenticatedOnlySelector = "#signed-in-only", ProtectedGetPath = "/api/me", SafeGetPaths = ["/api/me"], GraphQlPath = "/graphql", AllowedGraphQlQueries = ["query { viewer { id } }"] };
    private readonly ManagedEdgeConnectRequest _request = new("dev", Origin + "/", new string('A', 64));

    public ManagedEdgeCdpTests()
    {
        _page.SetupGet(p => p.Url).Returns(Origin + "/home");
        _page.SetupGet(p => p.IsClosed).Returns(false);
        _page.SetupGet(p => p.NavigationChanged).Returns(false);
        _page.Setup(p => p.HasAuthenticatedElementAsync(Origin, "#signed-in-only")).ReturnsAsync(false);
        _page.Setup(p => p.FetchAsync(Origin, "/api/me", null)).ReturnsAsync(new ManagedEdgeProbeResult(401, "application/json", 3));
        _browser.SetupGet(b => b.IsConnected).Returns(true);
        _browser.SetupGet(b => b.ContextCount).Returns(1);
        _browser.SetupGet(b => b.Pages).Returns(new[] { _page.Object });
        _connector.Setup(c => c.ConnectAsync("http://127.0.0.1:9222", It.IsAny<CancellationToken>())).ReturnsAsync(_browser.Object);
    }

    private ManagedEdgeCdpService Service() => new(_connector.Object, Options.Create(new ManagedEdgeOptions { Targets = [_rule] }), Options.Create(new AuthenticatedReviewOptions { Enabled = true, Runtime = "LocalWorkstation" }));
    private ManagedEdgeSessionRequest Owner(ManagedEdgeStatus s) => new(s.SessionId!, _request.ProfileId, _request.ContextFingerprint);

    [Theory]
    [InlineData("http://localhost:9222")]
    [InlineData("http://127.0.0.1:9222")]
    [InlineData("http://[::1]:9222")]
    public void LocalEndpointsAccepted(string value) => Assert.True(ManagedEdgePolicy.Endpoint(value).IsLoopback);

    [Theory]
    [InlineData("http://192.168.1.10:9222")]
    [InlineData("http://8.8.8.8:9222")]
    [InlineData("http://example.com:9222")]
    [InlineData("http://localhost.evil.test:9222")]
    [InlineData("http://user:password@localhost:9222")]
    [InlineData("http://localhost:9222/?remote=1")]
    [InlineData("http://localhost:9222/json")]
    public void UnsafeEndpointRejected(string value) => Assert.Throws<ArgumentException>(() => ManagedEdgePolicy.Endpoint(value));

    [Theory]
    [InlineData("https://m2lbdev.bufetat.no:443/path", true)]
    [InlineData("https://m2lbdev.bufetat.no.evil.test/", false)]
    [InlineData("http://m2lbdev.bufetat.no/", false)]
    [InlineData("https://m2lbdev.bufetat.no:444/", false)]
    [InlineData("https://other.test/?next=https://m2lbdev.bufetat.no", false)]
    public void ExactOriginOnly(string url, bool expected) => Assert.Equal(expected, ManagedEdgePolicy.MatchesOrigin(url, Origin));

    [Theory]
    [InlineData("https://other.test/api")]
    [InlineData("//other.test/api")]
    [InlineData("/api?token=secret")]
    [InlineData("/api/../logout")]
    [InlineData("/%2f%2fother.test")]
    public void UnsafeFetchRejected(string path) => Assert.Throws<ArgumentException>(() => ManagedEdgePolicy.SafePath(Origin, path));

    [Theory]
    [InlineData("mutation { deleteUser }")]
    [InlineData("subscription { messages }")]
    [InlineData("query { user } mutation { deleteUser }")]
    [InlineData("query($id: ID) { user(id: $id) }")]
    [InlineData("invalid document")]
    public void UnsafeGraphQlRejected(string query) => Assert.Throws<ArgumentException>(() => ManagedEdgePolicy.QueryOnly(query));

    [Fact]
    public void GraphQlQueryAccepted() => ManagedEdgePolicy.QueryOnly("query { viewer { id } }");

    [Fact]
    public async Task ConnectDoesNotProveAuthenticationOrProbe()
    {
        using var service = Service();
        var status = await service.ConnectAsync(_request);
        Assert.Equal(ManagedEdgeState.ConnectedUnproven, status.State);
        Assert.True(status.OriginMatched);
        Assert.False(status.SecurityBrowserAvailable);
        _page.Verify(p => p.FetchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Theory]
    [InlineData(0, ManagedEdgeState.TargetTabNotFound)]
    [InlineData(2, ManagedEdgeState.AmbiguousTargetTabs)]
    public async Task MissingOrAmbiguousTabDisconnects(int count, ManagedEdgeState expected)
    {
        _browser.SetupGet(b => b.Pages).Returns(Enumerable.Repeat(_page.Object, count).ToArray());
        using var service = Service();
        Assert.Equal(expected, (await service.ConnectAsync(_request)).State);
        _browser.Verify(b => b.Dispose(), Times.Once);
    }

    [Fact]
    public async Task WrongOriginIsNotMatched()
    {
        _page.SetupGet(p => p.Url).Returns("https://login.microsoftonline.com/");
        using var service = Service();
        Assert.Equal(ManagedEdgeState.TargetTabNotFound, (await service.ConnectAsync(_request)).State);
    }

    [Fact]
    public async Task FailureIsSanitized()
    {
        _connector.Setup(c => c.ConnectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("secret transport detail"));
        using var service = Service();
        var status = await service.ConnectAsync(_request);
        Assert.Equal(ManagedEdgeState.Failed, status.State);
        Assert.DoesNotContain("secret", status.Evidence);
    }

    [Fact]
    public async Task ProtectedProofEnablesOnlySupportedCapabilities()
    {
        _page.Setup(p => p.FetchAsync(Origin, "/api/me", null)).ReturnsAsync(new ManagedEdgeProbeResult(200, "application/json", 4));
        using var service = Service();
        var status = await service.StatusAsync(Owner(await service.ConnectAsync(_request)), true);
        Assert.True(status.SecurityBrowserAvailable);
        Assert.True(status.RestAvailable);
        Assert.False(status.GraphQlAvailable);
        Assert.True(status.PublicCoverageAvailable);
    }

    [Fact]
    public async Task ShellProofDoesNotClaimApiCoverage()
    {
        _page.Setup(p => p.HasAuthenticatedElementAsync(Origin, "#signed-in-only")).ReturnsAsync(true);
        using var service = Service();
        var status = await service.StatusAsync(Owner(await service.ConnectAsync(_request)), true);
        Assert.True(status.AuthenticatedBrowserAvailable);
        Assert.False(status.RestAvailable);
        Assert.False(status.GraphQlAvailable);
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("closed")]
    [InlineData("navigation")]
    [InlineData("origin")]
    public async Task RuntimeChangesInvalidateProof(string change)
    {
        _page.Setup(p => p.HasAuthenticatedElementAsync(Origin, "#signed-in-only")).ReturnsAsync(true);
        using var service = Service();
        var owner = Owner(await service.ConnectAsync(_request));
        await service.StatusAsync(owner, true);
        if (change == "disconnect") _browser.SetupGet(b => b.IsConnected).Returns(false);
        if (change == "closed") _page.SetupGet(p => p.IsClosed).Returns(true);
        if (change == "navigation") _page.SetupGet(p => p.NavigationChanged).Returns(true);
        if (change == "origin") _page.SetupGet(p => p.Url).Returns("https://other.test");
        var status = await service.StatusAsync(owner);
        Assert.Equal(ManagedEdgeState.Stale, status.State);
        Assert.False(status.SecurityBrowserAvailable);
        _browser.Verify(b => b.Dispose(), Times.Once);
    }

    [Fact]
    public async Task ApprovedSameOriginFetchWorksAndCrossOriginNeverExecutes()
    {
        _page.Setup(p => p.HasAuthenticatedElementAsync(Origin, "#signed-in-only")).ReturnsAsync(true);
        using var service = Service();
        var owner = Owner(await service.ConnectAsync(_request));
        await service.StatusAsync(owner, true);
        var request = new ManagedEdgeFetchRequest(owner.SessionId, owner.ProfileId, owner.ContextFingerprint, "/api/me");
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteSameOriginFetchAsync(request with { Path = "https://other.test" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteSameOriginFetchAsync(request with { Path = "/logout" }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ExecuteSameOriginFetchAsync(request with { Path = "/graphql", GraphQlQuery = "mutation { deleteUser }" }));
        Assert.Equal(401, (await service.ExecuteSameOriginFetchAsync(request)).StatusCode);
        Assert.False((await service.StatusAsync(owner)).AuthenticatedBrowserAvailable);
    }

    [Fact]
    public async Task ShellProofSurvivesUnavailableApiProbe()
    {
        _page.Setup(p => p.HasAuthenticatedElementAsync(Origin, "#signed-in-only")).ReturnsAsync(true);
        _page.Setup(p => p.FetchAsync(Origin, "/api/me", null)).ThrowsAsync(new Exception("request unavailable"));
        using var service = Service();
        var status = await service.StatusAsync(Owner(await service.ConnectAsync(_request)), true);
        Assert.True(status.AuthenticatedBrowserAvailable);
        Assert.False(status.RestAvailable);
    }

    [Fact]
    public async Task OwnershipFingerprintMustMatch()
    {
        using var service = Service();
        var owner = Owner(await service.ConnectAsync(_request));
        await Assert.ThrowsAsync<System.Collections.Generic.KeyNotFoundException>(() => service.StatusAsync(owner with { ContextFingerprint = new string('B', 64) }));
        await Assert.ThrowsAsync<System.Collections.Generic.KeyNotFoundException>(() => service.StatusAsync(owner with { ProfileId = "other" }));
    }

    [Fact]
    public async Task OpenButNonInspectableTabIsReportedPreciselyAndNeverAuthenticated()
    {
        // Edge advertises the tab in /json/list but refuses debugger attachment (Defender for Cloud Apps protected session).
        _browser.SetupGet(b => b.Pages).Returns(Array.Empty<IManagedEdgePage>());
        _browser.SetupGet(b => b.DiscoveredPageUrls).Returns(new[] { Origin + "/", "http://localhost:9222/json" });
        using var service = Service();
        var status = await service.ConnectAsync(_request);
        Assert.Equal(ManagedEdgeState.TargetTabNotInspectable, status.State);
        Assert.Equal(1, status.DiscoveredTargetTabs);
        Assert.Null(status.SessionId);
        Assert.False(status.OriginMatched);
        Assert.False(status.AuthenticatedBrowserAvailable);
        Assert.Contains("does not bypass", status.Evidence);
        _browser.Verify(b => b.Dispose(), Times.Once);
        _page.Verify(p => p.HasAuthenticatedElementAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task NotFoundStillReportedWhenBrowserAdvertisesNoTargetTab()
    {
        _browser.SetupGet(b => b.Pages).Returns(Array.Empty<IManagedEdgePage>());
        _browser.SetupGet(b => b.DiscoveredPageUrls).Returns(new[] { "https://other.test/" });
        using var service = Service();
        var status = await service.ConnectAsync(_request);
        Assert.Equal(ManagedEdgeState.TargetTabNotFound, status.State);
        Assert.Equal(0, status.DiscoveredTargetTabs);
    }

    [Fact]
    public void BridgeHasNoAuthenticationExtractionOrBrowserCloseCode()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "AIAssisted"))) root = root.Parent;
        var source = File.ReadAllText(Path.Combine(root!.FullName, "AIAssisted/backend/BirkNext.Api/Services/ManagedEdge/ManagedEdgeBrowser.cs"));
        foreach (var forbidden in new[] { "CookiesAsync", "StorageState", "localStorage", "sessionStorage", "Authorization", "Set-Cookie", ".CloseAsync", "LaunchAsync", "response.text", "response.json" })
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AllowAutoRedirect = false", source);
        Assert.Contains("redirect: 'error'", source);
    }
}
