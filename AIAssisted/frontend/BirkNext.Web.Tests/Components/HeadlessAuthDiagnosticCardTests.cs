using BirkNext.HeadlessAuthDiagnostic;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

public sealed class HeadlessAuthDiagnosticCardTests : BunitContext
{
    private readonly Mock<IHeadlessDiagnosticApiService> _api = new();
    private static FrontendAnalysisProfile Target => new() { Id = "qa", Name = "QA", TargetUrl = "https://app.qa.example/", EnvironmentType = FrontendEnvironmentType.QA,
        Authentication = new() { ExpectedAuthority = "https://login.microsoftonline.com/tenant", AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId } };
    private IRenderedComponent<HeadlessAuthDiagnosticCard> Card(FrontendAnalysisProfile? profile = null)
    {
        Services.AddSingleton(_api.Object);
        return Render<HeadlessAuthDiagnosticCard>(p => p.Add(c => c.Profile, profile ?? Target));
    }
    private void Returns(HeadlessDiagnosticReport report) => _api.Setup(a => a.RunAsync(It.IsAny<HeadlessDiagnosticRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(report);
    [Fact] public void CorrectNameAndSeparatePurpose()
    { var c = Card(); Assert.Contains(HeadlessItReport.Title, c.Find("h4").TextContent); Assert.Contains("Browser Automation Diagnostic", c.Markup); Assert.Contains("unattended", c.Markup); Assert.Equal("Not tested", c.Find("[data-testid=had-readiness]").TextContent); }
    [Theory] [InlineData(FrontendEnvironmentType.Production)] [InlineData(FrontendEnvironmentType.Custom)]
    public void ProductionAndUnknownTypeCannotRun(FrontendEnvironmentType type)
    { var p = Target; p.EnvironmentType = type; var c = Card(p); Assert.True(c.Find("[data-testid=had-run]").HasAttribute("disabled")); Assert.NotNull(c.Find("[data-testid=had-guard]")); }
    [Fact] public void MissingUrlCannotRun()
    { var p = Target; p.TargetUrl = ""; var c = Card(p); Assert.True(c.Find("[data-testid=had-run]").HasAttribute("disabled")); }
    [Fact] public void UrlIsSanitizedBeforeDisplay()
    { var p = Target; p.TargetUrl = "https://qa.example/?code=secret"; var c = Card(p); Assert.DoesNotContain("secret", c.Markup); }
    [Fact] public async Task SendsConfiguredTargetAndAuthority()
    {
        Returns(new()); var c = Card(); await c.Find("[data-testid=had-run]").ClickAsync(new());
        _api.Verify(a => a.RunAsync(It.Is<HeadlessDiagnosticRequest>(r => r.TargetEnvironmentId == "qa" && r.TargetUrl == Target.TargetUrl && r.Authority == Target.Authentication.ExpectedAuthority), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact] public async Task PrerequisiteUnavailableExplainsWhy()
    { _api.Setup(a => a.CheckAsync(It.IsAny<HeadlessDiagnosticRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new HeadlessPrerequisite(false, "Run Browser Automation Diagnostic first.")); var c = Card(); await c.Find("[data-testid=had-check]").ClickAsync(new()); Assert.Contains("Blocked", c.Find("[data-testid=had-prerequisite]").TextContent); }
    [Theory]
    [InlineData(HeadlessBlocker.InteractiveAuthenticationRequired)] [InlineData(HeadlessBlocker.InteractiveMfaRequired)]
    [InlineData(HeadlessBlocker.ConditionalAccessBlocked)] [InlineData(HeadlessBlocker.SessionControlHeadlessRestriction)]
    [InlineData(HeadlessBlocker.BrowserAutomationBlocked)] [InlineData(HeadlessBlocker.AutomationControlLostAfterAuthentication)]
    [InlineData(HeadlessBlocker.AutomationControlLostAfterObservedSessionControl)]
    public async Task ShowsPrecisePrimaryBlocker(HeadlessBlocker blocker)
    { Returns(new() { PrimaryBlocker = blocker, Readiness = HeadlessReadiness.NotReady, Interpretation = "Observed blocker." }); var c = Card(); await c.Find("[data-testid=had-run]").ClickAsync(new()); Assert.Equal(HeadlessItReport.BlockerLabel(blocker), c.Find("[data-testid=had-blocker]").TextContent); Assert.Equal(blocker.ToString(), c.Find("[data-testid=had-blocker]").GetAttribute("data-blocker")); Assert.Equal("Not Ready", c.Find("[data-testid=had-readiness]").TextContent); }
    [Theory] [InlineData(HeadlessReadiness.Unknown, "Unknown")] [InlineData(HeadlessReadiness.Ready, "Ready")]
    public async Task ReadinessIsNotAssumed(HeadlessReadiness readiness, string label)
    { Returns(new() { Readiness = readiness }); var c = Card(); await c.Find("[data-testid=had-run]").ClickAsync(new()); Assert.Equal(label, c.Find("[data-testid=had-readiness]").TextContent); }
    [Fact] public async Task TechnicalDetailsCollapsedAndIdentityIsNotConfigured()
    { Returns(new()); var c = Card(); await c.Find("[data-testid=had-run]").ClickAsync(new()); Assert.False(c.Find("[data-testid=had-details]").HasAttribute("open")); Assert.Contains("Dedicated QA automation identity: Not configured", c.Markup); }
    [Fact] public async Task CopiesActualItReport()
    {
        var r = new HeadlessDiagnosticReport { PrimaryBlocker = HeadlessBlocker.InteractiveMfaRequired, Readiness = HeadlessReadiness.NotReady };
        Returns(r); JSInterop.SetupVoid("navigator.clipboard.writeText", HeadlessItReport.Build(r)).SetVoidResult();
        var c = Card(); await c.Find("[data-testid=had-run]").ClickAsync(new()); await c.Find("[data-testid=had-copy]").ClickAsync(new()); Assert.Equal("IT report copied", c.Find("[data-testid=had-copy]").TextContent);
    }
    [Fact] public async Task ClipboardFailureDoesNotClaimSuccess()
    { Returns(new()); var c = Card(); await c.Find("[data-testid=had-run]").ClickAsync(new()); await c.Find("[data-testid=had-copy]").ClickAsync(new()); Assert.Equal("Copy IT report", c.Find("[data-testid=had-copy]").TextContent); Assert.Contains("could not be copied", c.Find("[data-testid=had-error]").TextContent); }
    [Fact] public async Task TargetChangeClearsOldReport()
    { Returns(new()); var c = Card(); await c.Find("[data-testid=had-run]").ClickAsync(new()); var p = Target; p.TargetUrl = "https://other.qa.example/"; c.Render(pms => pms.Add(x => x.Profile, p)); Assert.Empty(c.FindAll("[data-testid=had-result]")); }
    [Fact] public async Task TransportErrorIsNotAuthenticationFailure()
    { _api.Setup(a => a.RunAsync(It.IsAny<HeadlessDiagnosticRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException()); var c = Card(); await c.Find("[data-testid=had-run]").ClickAsync(new()); Assert.Contains("not an authentication finding", c.Find("[data-testid=had-error]").TextContent); }
    [Fact] public async Task CancellationIsVisible()
    { _api.Setup(a => a.RunAsync(It.IsAny<HeadlessDiagnosticRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException()); var c = Card(); await c.Find("[data-testid=had-run]").ClickAsync(new()); Assert.Equal("Cancelled", c.Find("[data-testid=had-readiness]").TextContent); Assert.Empty(c.FindAll("[data-testid=had-error]")); }

    // §27, §37. The session-control sequence blocker reads as a sequence, visibly, not in technical details.
    [Fact] public async Task SessionControlSequenceBlockerIsVisibleAndNotCausal()
    {
        Returns(new() { PrimaryBlocker = HeadlessBlocker.AutomationControlLostAfterObservedSessionControl, Readiness = HeadlessReadiness.NotReady,
            Interpretation = "Control was lost after a session-control signal was observed. This establishes sequence, not causality." });
        var c = Card(); await c.Find("[data-testid=had-run]").ClickAsync(new());
        var blocker = c.Find("[data-testid=had-blocker]");
        Assert.Null(blocker.Closest("details"));
        Assert.Contains("sequence, not cause", blocker.TextContent);
        Assert.DoesNotContain("MCAS blocked", c.Markup);
    }

    // §28, §38. Entra error identifiers and evidence provenance are visible outside technical details.
    [Fact] public async Task EntraIdentifiersAndProvenanceAreVisible()
    {
        Returns(new() { PrimaryBlocker = HeadlessBlocker.ConditionalAccessBlocked, Readiness = HeadlessReadiness.NotReady,
            EntraErrorCode = "AADSTS53003", EntraCorrelationId = "9f8e7d6c-aaaa-bbbb-cccc-ddddeeeeffff", EntraRequestId = "0a1b2c3d-1111-2222-3333-444455556666",
            Evidence = [new("Identity provider", "Microsoft Entra ID", HeadlessEvidenceProvenance.Observed),
                        new("MFA", "Unknown / not reached", HeadlessEvidenceProvenance.Unknown),
                        new("Configured authority", "https://login.microsoftonline.com/[tenant]", HeadlessEvidenceProvenance.Configured)] });
        var c = Card(); await c.Find("[data-testid=had-run]").ClickAsync(new());
        var entra = c.Find("[data-testid=had-entra-error]");
        Assert.Null(entra.Closest("details"));
        Assert.Contains("9f8e7d6c-aaaa-bbbb-cccc-ddddeeeeffff", entra.TextContent);
        var items = c.FindAll("[data-testid=had-provenance-item]");
        Assert.Equal(["Observed", "Unknown", "Configured"], items.Select(i => i.GetAttribute("data-provenance")));
        Assert.DoesNotContain("MFA disabled", c.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
