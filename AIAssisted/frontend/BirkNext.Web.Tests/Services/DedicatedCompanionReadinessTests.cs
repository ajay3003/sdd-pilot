using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;

namespace BirkNext.Web.Tests.Services;

public sealed class DedicatedCompanionReadinessTests
{
    [Theory]
    [InlineData("NotObserved", "Not observed in dedicated profile")]
    [InlineData("SessionConflict", "Another browser is paired")]
    [InlineData("PolicyBlocked", "Unavailable: Edge policy")]
    [InlineData("BuildMissing", "Build not available")]
    [InlineData("AwaitingHeartbeat", "Loaded; awaiting heartbeat")]
    public void ProxyReadinessDoesNotClaimBrowserAutomation(string state, string label)
    {
        var proxy = new LocalHttpsProxyStatus { EdgeRunning = true, ProxyListening = true,
            RuntimeStatus = LocalHttpsProxyRuntimePhase.Running, State = LocalHttpsProxyState.Listening,
            EdgeVerification = DedicatedBrowserVerification.Confirmed,
            Companion = new() { State = state, Message = "Detailed setup reason." } };
        var summary = AuthenticationReadinessPresentation.Summarize(AuthConfigurationState.Configured,
            AuthenticatedTestingMethod.LocalHttpsProxy, proxy, new() { State = ProxyCertificateTrustState.Trusted }, true);
        var browser = summary.Prerequisites.Single(p => p.Id == "browser");
        Assert.Contains(("Browser Companion", label), browser.Facts!);
        Assert.Contains("Detailed setup reason.", browser.Explanation);
        Assert.Contains("not ready in this browser", browser.Explanation);
        Assert.False(proxy.Companion.BrowserDiscoveryReady);
        if (state == "NotObserved") Assert.Equal("edge-restart", browser.ActionId);
    }
    [Fact]
    public void PermissionRequiredNamesTheExactOriginAndIsNotRestartAdvice()
    {
        var proxy = new LocalHttpsProxyStatus { EdgeRunning = true, ProxyListening = true,
            RuntimeStatus = LocalHttpsProxyRuntimePhase.Running, State = LocalHttpsProxyState.Listening,
            EdgeVerification = DedicatedBrowserVerification.Confirmed,
            Companion = new() { State = "PermissionRequired", PermissionOrigins = ["https://m2lbdev.bufetat.no"],
                Message = "Companion is paired but has no access to https://m2lbdev.bufetat.no. In Dedicated Edge, open the Companion popup and choose Allow access." } };
        var browser = AuthenticationReadinessPresentation.Summarize(AuthConfigurationState.Configured,
            AuthenticatedTestingMethod.LocalHttpsProxy, proxy, new() { State = ProxyCertificateTrustState.Trusted }, true)
            .Prerequisites.Single(p => p.Id == "browser");
        Assert.Contains(("Browser Companion", "Site access required: https://m2lbdev.bufetat.no"), browser.Facts!);
        Assert.Contains("Allow access", browser.Explanation);
        // Restarting the browser does not grant a permission; the action is the popup's, not ours.
        Assert.NotEqual("edge-restart", browser.ActionId);
        // The proxy keeps working without the Companion.
        Assert.Contains("Proxy-based API testing remains independent", browser.Explanation);
    }
}
