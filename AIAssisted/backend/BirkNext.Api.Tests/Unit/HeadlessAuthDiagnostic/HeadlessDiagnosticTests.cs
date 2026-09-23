using System.Text.Json;
using BirkNext.HeadlessAuthDiagnostic;
using BirkNext.Api.Services.HeadlessAuthDiagnostic;
using BirkNext.Api.Services.BrowserAutomationDiagnostic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Moq;   // ReturnsAsync is an extension method and needs the namespace imported, not just Moq.Mock qualified.
using Xunit;

namespace BirkNext.Api.Tests.Unit.HeadlessAuthDiagnostic;

public sealed class HeadlessDiagnosticTests
{
    private static HeadlessDiagnosticRequest Request => new() { TargetEnvironmentId = "qa", TargetEnvironmentName = "QA", TargetUrl = "https://app.qa.example/", EnvironmentType = "QA" };
    private static string Profile => System.IO.Path.Combine(HeadlessDiagnosticPolicy.ProfileRoot, Guid.NewGuid().ToString("N"));
    private static readonly NullLogger<HeadlessDiagnosticService> Log = NullLogger<HeadlessDiagnosticService>.Instance;
    /// <summary>
    /// A headless mode that kept stable control through the navigation and reached the Entra redirect — the evidence
    /// the prerequisite is built on. The failing variant lost the page at the target.
    /// </summary>
    private static BrowserAutomationDiagnosticModeReport HeadlessMode(bool passed) => new()
    {
        Mode = BrowserAutomationDiagnosticMode.Headless, Headless = true,
        Result = passed ? BrowserAutomationDiagnosticModeResult.AvailableAtAuthenticationBoundary : BrowserAutomationDiagnosticModeResult.TargetRestricted,
        Stages = [new(BrowserAutomationDiagnosticStage.TargetControl, passed ? BrowserAutomationDiagnosticStageState.Passed : BrowserAutomationDiagnosticStageState.Blocked),
                  new(BrowserAutomationDiagnosticStage.TargetStability, passed ? BrowserAutomationDiagnosticStageState.Passed : BrowserAutomationDiagnosticStageState.NotRun)],
        Target = new() { FinalLocation = passed ? BrowserAutomationFinalLocation.AuthenticationAuthority : BrowserAutomationFinalLocation.Unknown,
                         AuthenticationHost = passed ? "login.microsoftonline.com" : null },
    };
    private static BrowserAutomationEvidenceStore Proof(bool passed = true)
    {
        var store = new BrowserAutomationEvidenceStore();
        store.Record(new() { TargetEnvironmentId = Request.TargetEnvironmentId, TargetUrl = Request.TargetUrl,
            TargetEnvironmentType = Request.EnvironmentType, DiagnosticId = "proof", Modes = [HeadlessMode(passed)] });
        return store;
    }
    private sealed class Browser : IHeadlessBrowser, IHeadlessBrowserFactory
    {
        public readonly List<string> Calls = [];
        public Exception? LaunchError, NavigationError, ObservationError;
        public Queue<bool> Controls = new([true, true, true]);
        public Queue<HeadlessObservation> Observations = new();
        public string? LaunchedProfile;
        public string? EdgeVersion => "test-edge";
        public IHeadlessBrowser Create(HeadlessDiagnosticRequest request) { Calls.Add("create"); return this; }
        public Task LaunchAsync(string profile, CancellationToken ct) { Calls.Add("launch"); LaunchedProfile = profile; ct.ThrowIfCancellationRequested(); if (LaunchError is not null) throw LaunchError; return Task.CompletedTask; }
        public Task NavigateAsync(CancellationToken ct) { Calls.Add("navigate"); if (NavigationError is not null) throw NavigationError; return Task.CompletedTask; }
        public Task<bool> ControlAsync(CancellationToken ct) { Calls.Add("control"); return Task.FromResult(Controls.Count == 0 || Controls.Dequeue()); }
        public Task<HeadlessObservation> ObserveAsync(CancellationToken ct)
        {
            Calls.Add("observe"); ct.ThrowIfCancellationRequested();
            if (Observations.Count > 0) return Task.FromResult(Observations.Dequeue());
            if (ObservationError is not null) throw ObservationError;
            return Task.FromResult(new HeadlessObservation());
        }
        public ValueTask DisposeAsync() { Calls.Add("dispose"); return ValueTask.CompletedTask; }
    }
    private static HeadlessDiagnosticService Service(Browser browser, BrowserAutomationEvidenceStore? proof = null) =>
        new(browser, proof ?? Proof(), Options.Create(new HeadlessDiagnosticOptions()), Log) { ObservationTimeout = TimeSpan.FromMilliseconds(350) };
    private static (HeadlessDiagnosticReport Report, HeadlessEvidenceRun Run) Reducer()
    { var report = new HeadlessDiagnosticReport(); return (report, new(report, Log)); }
    private static HeadlessObservation Entra(bool login = false, bool mfa = false, bool ca = false) =>
        new() { AtIdentityProvider = true, Entra = true, InteractiveLogin = login, MfaChallenge = mfa, ConditionalAccessBlock = ca };

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingOrFailedPrerequisitePreventsAnyBrowser(bool failed)
    {
        var b = new Browser(); var r = await Service(b, failed ? Proof(false) : new()).RunAsync(Request);
        Assert.Equal(HeadlessBlocker.BrowserAutomationBlocked, r.PrimaryBlocker); Assert.Empty(b.Calls);
        Assert.Equal(HeadlessStageState.NotRun, r.Stage(HeadlessStage.MfaDetection).State);
    }
    [Fact] public void PrerequisiteIsBoundToExactTargetAndEnvironment()
    { var p = Proof(); Assert.True(p.Check(Request).Available); Assert.False(p.Check(Request with { TargetUrl = "https://other.qa.example/" }).Available); Assert.False(p.Check(Request with { EnvironmentType = "Production" }).Available); }
    [Fact] public void LatestFailureRevokesEarlierPass()
    { var p = Proof(); p.Record(new() { TargetEnvironmentId = "qa", TargetUrl = Request.TargetUrl, TargetEnvironmentType = "QA", Result = BrowserAutomationDiagnosticComparison.MixedOrInconclusive }); Assert.False(p.Check(Request).Available); }
    [Fact] public void PassWithoutTargetControlIsNotProof()
    { var p = new BrowserAutomationEvidenceStore(); p.Record(new() { TargetEnvironmentId = "qa", TargetUrl = Request.TargetUrl, TargetEnvironmentType = "QA", Result = BrowserAutomationDiagnosticComparison.AutomationAvailable }); Assert.False(p.Check(Request).Available); }
    [Fact] public void HeadedSuccessAloneDoesNotSatisfyHeadlessPrerequisite()
    {
        var p = new BrowserAutomationEvidenceStore();
        p.Record(new() { TargetEnvironmentId = "qa", TargetUrl = Request.TargetUrl, TargetEnvironmentType = "QA",
            Modes = [new() { Mode = BrowserAutomationDiagnosticMode.Headed, Result = BrowserAutomationDiagnosticModeResult.Available,
                Stages = [new(BrowserAutomationDiagnosticStage.TargetControl, BrowserAutomationDiagnosticStageState.Passed)] }] });
        Assert.False(p.Check(Request).Available);
    }
    [Fact] public async Task BrowserControllerRecordsServerSideProof()
    {
        var p = new BrowserAutomationEvidenceStore();
        var result = new BrowserAutomationDiagnosticReport { TargetEnvironmentId = "qa", TargetUrl = Request.TargetUrl, TargetEnvironmentType = "QA",
            Modes = [HeadlessMode(passed: true)] };
        var diagnostic = new Moq.Mock<IBrowserAutomationDiagnosticService>();
        diagnostic.Setup(d => d.RunAsync(Moq.It.IsAny<BrowserAutomationDiagnosticRequest>(), Moq.It.IsAny<CancellationToken>())).ReturnsAsync(result);
        var controller = new BirkNext.Api.Controllers.BrowserAutomationDiagnosticController(diagnostic.Object,
            NullLogger<BirkNext.Api.Controllers.BrowserAutomationDiagnosticController>.Instance, p);
        await controller.Run(new(), default);
        Assert.True(p.Check(Request).Available);
    }
    [Fact] public void SignalsAreRetainedAcrossLaterNeutralPages()
    {
        var (r, run) = Reducer(); run.Observe(new() { ConditionalAccessSignal = true, PossibleSessionControl = true }); run.Observe(new());
        Assert.Equal("Signal observed", r.ConditionalAccess); Assert.Equal("Possible session-control signal", r.SessionControl);
    }
    [Fact] public void ControlLossDuringAuthIsNotPostAuthControlEvidence()
    {
        var (r, run) = Reducer(); run.Observe(Entra()); run.ControlLost();
        Assert.Equal(HeadlessBlocker.Unknown, r.PrimaryBlocker); Assert.Equal("Not tested", r.PostAuthenticationControl);
    }
    [Theory]
    [InlineData("Production")] [InlineData("production")] [InlineData("Prod")] [InlineData("")] [InlineData("Custom")] [InlineData("arbitrary")]
    public async Task ProductionAndUnspecifiedEnvironmentBlocked(string type)
    { var b = new Browser(); var r = await Service(b).RunAsync(Request with { EnvironmentType = type }); Assert.Equal(HeadlessRunStatus.Blocked, r.Status); Assert.Empty(b.Calls); }
    [Theory]
    [InlineData("")] [InlineData("not-a-url")] [InlineData("https://user:secret@qa.example/")] [InlineData("https://qa.example/?code=secret")]
    [InlineData("https://qa.example/#access_token=secret")] [InlineData("https://app.prod.example/")]
    public async Task InvalidOrProductionTargetBlocked(string url)
    { var b = new Browser(); var r = await Service(b).RunAsync(Request with { TargetUrl = url }); Assert.Equal(HeadlessRunStatus.Blocked, r.Status); Assert.Empty(b.Calls); Assert.DoesNotContain("secret", JsonSerializer.Serialize(r)); }
    [Fact] public void FreshDedicatedProfileAccepted() => Assert.True(HeadlessDiagnosticPolicy.IsDedicatedProfile(Profile));
    [Theory]
    [InlineData("Default")] [InlineData("C:\\Users\\employee\\AppData\\Local\\Microsoft\\Edge\\User Data\\Default")]
    [InlineData("C:\\BirkNext\\BrowserCompanion")] [InlineData("C:\\BirkNext\\LocalHttpsProxyEdgeProfile")]
    public void OtherProfilesRefused(string path) => Assert.False(HeadlessDiagnosticPolicy.IsDedicatedProfile(path));
    [Fact] public void ProfileRootItselfRefused() => Assert.False(HeadlessDiagnosticPolicy.IsDedicatedProfile(HeadlessDiagnosticPolicy.ProfileRoot));
    [Fact] public void SupportedHeadlessEdgeLaunchOptions()
    { var o = PlaywrightHeadlessBrowser.LaunchOptions; Assert.True(o.Headless); Assert.Equal("msedge", o.Channel); Assert.True(o.ChromiumSandbox); Assert.Null(o.Args); Assert.Null(o.IgnoreHTTPSErrors); }
    [Fact] public async Task LaunchSuccessAndOrder()
    {
        var b = new Browser(); b.Observations.Enqueue(Entra(login: true)); var r = await Service(b).RunAsync(Request);
        Assert.Equal(new[] { "create", "launch", "control", "navigate", "control", "observe", "dispose" }, b.Calls);
        Assert.Equal("Available", r.HeadlessBrowser); Assert.True(HeadlessDiagnosticPolicy.IsDedicatedProfile(b.LaunchedProfile!));
    }
    [Fact] public async Task LaunchFailureStopsAuthenticationAndCleansUp()
    { var b = new Browser { LaunchError = new PlaywrightException("secret") }; var r = await Service(b).RunAsync(Request); Assert.Equal(HeadlessBlocker.BrowserAutomationBlocked, r.PrimaryBlocker); Assert.DoesNotContain("observe", b.Calls); Assert.Contains("dispose", b.Calls); Assert.DoesNotContain("secret", JsonSerializer.Serialize(r)); }
    [Fact] public async Task TargetOnlyAfterBrowserControl()
    { var b = new Browser { Controls = new([false]) }; var r = await Service(b).RunAsync(Request); Assert.DoesNotContain("navigate", b.Calls); Assert.Equal(HeadlessBlocker.BrowserAutomationBlocked, r.PrimaryBlocker); }
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task TargetClosesBeforeAuth(bool throws)
    {
        var b = new Browser { NavigationError = throws ? new PlaywrightException("Target page, context or browser has been closed") : null, Controls = new([true, false]) };
        var r = await Service(b).RunAsync(Request); Assert.Equal(HeadlessBlocker.BrowserAutomationBlocked, r.PrimaryBlocker);
        Assert.Equal(HeadlessStageState.NotRun, r.Stage(HeadlessStage.AuthenticationDetection).State); Assert.Equal("Unknown / not reached", r.SessionControl);
    }
    [Fact] public async Task NavigationErrorClassifiedSeparately()
    { var b = new Browser { NavigationError = new PlaywrightException("net::ERR_CONNECTION_REFUSED") }; Assert.Equal(HeadlessBlocker.TargetNavigationBlocked, (await Service(b).RunAsync(Request)).PrimaryBlocker); }
    [Fact] public async Task TimeoutIsUnknown()
    { var b = new Browser { NavigationError = new TimeoutException() }; var r = await Service(b).RunAsync(Request); Assert.Equal(HeadlessReadiness.Unknown, r.Readiness); Assert.Equal(HeadlessBlocker.Unknown, r.PrimaryBlocker); }
    [Fact] public async Task CancellationIsNotFailureAndCleanupRuns()
    { var b = new Browser { LaunchError = new OperationCanceledException() }; var r = await Service(b).RunAsync(Request); Assert.Equal(HeadlessRunStatus.Cancelled, r.Status); Assert.Contains("dispose", b.Calls); }
    [Fact] public void RedirectAndIdentityProviderAreDetectedSeparatelyFromSession()
    { var (r, run) = Reducer(); run.Observe(Entra()); Assert.Equal("Microsoft Entra ID", r.IdentityProvider); Assert.Equal(HeadlessStageState.Passed, r.Stage(HeadlessStage.AuthenticationDetection).State); Assert.NotEqual("Established", r.AuthenticatedSession); }
    [Fact] public void InteractiveSignInIsNotGenericFailure()
    { var (r, run) = Reducer(); run.Observe(Entra(login: true)); Assert.Equal(HeadlessBlocker.InteractiveAuthenticationRequired, r.PrimaryBlocker); Assert.Equal(HeadlessReadiness.NotReady, r.Readiness); Assert.Equal("Unknown / not reached", r.Mfa); }
    [Fact] public void NoInteractionPermitsContinuation()
    { var (r, run) = Reducer(); run.Observe(Entra()); Assert.False(run.Terminal); Assert.Equal(HeadlessBlocker.None, r.PrimaryBlocker); }
    [Fact] public void ExplicitMfaRequiresHumanAction()
    { var (r, run) = Reducer(); run.Observe(Entra(mfa: true)); Assert.Equal(HeadlessBlocker.InteractiveMfaRequired, r.PrimaryBlocker); Assert.Equal("Required", r.Mfa); Assert.Equal(HeadlessReadiness.NotReady, r.Readiness); Assert.Equal("Unavailable", r.NonInteractiveAuthentication); }
    [Fact] public void MfaWinsOverLaterPossibleSessionControl()
    { var (r, run) = Reducer(); run.Observe(Entra(mfa: true)); run.Observe(new() { PossibleSessionControl = true }); Assert.Equal(HeadlessBlocker.InteractiveMfaRequired, r.PrimaryBlocker); }
    [Fact] public void CaBeforeMfaIsPrimary()
    { var (r, run) = Reducer(); run.Observe(Entra(ca: true)); run.Observe(Entra(mfa: true)); Assert.Equal(HeadlessBlocker.ConditionalAccessBlocked, r.PrimaryBlocker); Assert.Equal("AADSTS53003", r.ConditionalAccessErrorCode); }
    [Fact] public void CaSignalIsNotCaBlock()
    { var (r, run) = Reducer(); run.Observe(new() { ConditionalAccessSignal = true }); Assert.Equal("Signal observed", r.ConditionalAccess); Assert.Equal(HeadlessBlocker.None, r.PrimaryBlocker); }
    [Fact] public void NoCaEvidenceCannotClaimAllowed()
    { var (r, run) = Reducer(); run.Observe(new()); Assert.Equal("No observable signal", r.ConditionalAccess); Assert.DoesNotContain("Allowed", HeadlessItReport.Build(r)); }
    [Fact] public void StrongSessionSignalIsObservationOnly()
    { var (r, run) = Reducer(); run.Observe(new() { StrongSessionControl = true }); Assert.Equal("Session-control signal observed", r.SessionControl); Assert.False(run.Terminal); }
    [Fact] public void WeakSessionSignalIsPossible()
    { var (r, run) = Reducer(); run.Observe(new() { PossibleSessionControl = true }); Assert.Equal("Possible session-control signal", r.SessionControl); Assert.False(run.Terminal); }
    [Fact] public void NoSessionSignalIsNotAssumed()
    { var (r, run) = Reducer(); run.Observe(new()); Assert.Equal("No observable signal", r.SessionControl); }
    [Theory] [InlineData("https://app.access.mcas.ms/", true)] [InlineData("https://app.mcas-gov.us/", true)] [InlineData("https://app.mcas-gov.ms/", true)] [InlineData("https://app.mcas.ms.evil.example/", false)] [InlineData("https://notmcas.ms/", false)]
    public void SessionHostSuffixBoundaries(string url, bool expected) => Assert.Equal(expected, PlaywrightHeadlessBrowser.IsSessionHost(new(url)));
    [Fact] public void ControlLostAfterObservedSessionStageIsQualified()
    { var (r, run) = Reducer(); run.Observe(new() { StrongSessionControl = true }); run.ControlLost(); Assert.Equal(HeadlessBlocker.SessionControlHeadlessRestriction, r.PrimaryBlocker); Assert.Contains("does not establish MCAS as the cause", r.Interpretation); }
    [Fact] public void ExplicitSessionRestrictionBlocks()
    { var (r, run) = Reducer(); run.Observe(new() { StrongSessionControl = true, ExplicitHeadlessRestriction = true }); Assert.Equal(HeadlessReadiness.NotReady, r.Readiness); Assert.Equal(HeadlessBlocker.SessionControlHeadlessRestriction, r.PrimaryBlocker); }
    [Fact] public void AuthenticatedReturnDoesNotVerifySession()
    { var (r, run) = Reducer(); run.Observe(Entra()); run.Observe(new() { AtApplication = true }); Assert.True(run.ReturnObserved); Assert.False(run.SessionVerified); run.Ready(); Assert.NotEqual(HeadlessReadiness.Ready, r.Readiness); }
    [Theory] [InlineData(false, true)] [InlineData(true, false)]
    public void SessionRequiresConfiguredExplicitEvidence(bool configured, bool shell)
    { var (_, run) = Reducer(); run.Observe(new() { AtApplication = true, VerificationConfigured = configured, AuthenticatedShell = shell }); Assert.False(run.SessionVerified); }
    [Fact] public void CookieExistenceNotAnInput()
    { Assert.DoesNotContain(typeof(HeadlessObservation).GetProperties(), p => p.Name.Contains("Cookie")); }
    [Fact] public async Task VerifiedSessionAllowsPostAuthControlAndReady()
    { var b = new Browser(); b.Observations.Enqueue(new() { AtApplication = true, VerificationConfigured = true, AuthenticatedShell = true }); var r = await Service(b).RunAsync(Request); Assert.Equal(3, b.Calls.Count(c => c == "control")); Assert.Equal(HeadlessReadiness.Ready, r.Readiness); Assert.Equal(HeadlessBlocker.None, r.PrimaryBlocker); Assert.Equal("Available", r.PostAuthenticationControl); Assert.DoesNotContain("MFA disabled", HeadlessItReport.Build(r)); }
    [Fact] public async Task PostAuthControlLossIsSeparateFromMfa()
    { var b = new Browser { Controls = new([true, true, false]) }; b.Observations.Enqueue(new() { AtApplication = true, VerificationConfigured = true, AuthenticatedShell = true }); var r = await Service(b).RunAsync(Request); Assert.Equal(HeadlessBlocker.AutomationControlLostAfterAuthentication, r.PrimaryBlocker); Assert.Equal("Lost", r.PostAuthenticationControl); }
    [Fact] public async Task UncertainStateRemainsUnknown()
    { var r = await Service(new()).RunAsync(Request); Assert.Equal(HeadlessReadiness.Unknown, r.Readiness); Assert.Equal(HeadlessBlocker.Unknown, r.PrimaryBlocker); }
    [Fact] public void FirstBlockerCannotBeOverwritten()
    { var (r, run) = Reducer(); run.Block(HeadlessBlocker.BrowserAutomationBlocked, HeadlessStage.TargetNavigation, "closed"); run.Observe(Entra(mfa: true, ca: true)); run.Block(HeadlessBlocker.Unknown, HeadlessStage.MfaDetection, "later"); Assert.Equal(HeadlessBlocker.BrowserAutomationBlocked, r.PrimaryBlocker); Assert.Equal("Unknown / not reached", r.Mfa); }
    [Theory]
    [InlineData("code")] [InlineData("state")] [InlineData("nonce")] [InlineData("access_token")] [InlineData("id_token")] [InlineData("refresh_token")]
    [InlineData("login_hint")] [InlineData("client_secret")] [InlineData("client_assertion")] [InlineData("assertion")]
    [InlineData("session_state")] [InlineData("SAMLResponse")] [InlineData("RelayState")] [InlineData("request")]
    public void AuthQueryValuesAndFragmentsRedacted(string key)
    { var url = HeadlessEvidenceSanitizer.Url($"https://login.microsoftonline.com/tenant/oauth2/v2.0/authorize?{key}=secret#othersecret"); Assert.DoesNotContain("secret", url); Assert.EndsWith("/authorize?[redacted]", url); }
    [Fact] public void PathAndUserInfoSecretsRedacted()
    { var url = HeadlessEvidenceSanitizer.Url("https://user:secret@qa.example/secret"); Assert.Equal("https://qa.example/[path]", url); }
    [Fact] public void ReportContainsNoHeadersOrBodies()
    { var (r, run) = Reducer(); run.Observe(Entra(login: true)); var json = JsonSerializer.Serialize(r); Assert.DoesNotContain("Authorization", json); Assert.DoesNotContain("Set-Cookie", json); Assert.DoesNotContain("PostBody", json); }
    [Fact] public void ReportIsMeetingFriendlyAndDoesNotPrescribeBypass()
    {
        var (r, run) = Reducer(); run.Observe(Entra(mfa: true)); var text = HeadlessItReport.Build(r);
        foreach (var expected in new[] { "Headless", "unattended", "PRIMARY HEADLESS BLOCKER", "Interpretation:", "No credentials or tokens were captured", "approved non-interactive" }) Assert.Contains(expected, text);
        foreach (var forbidden in new[] { "MFA disabled", "Disable MFA", "Exclude user", "MCAS caused", "Defender killed", "Authentication failed" }) Assert.DoesNotContain(forbidden, text);
    }
}
