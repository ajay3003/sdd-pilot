using System.Net;
using System.Net.Sockets;
using System.Text;
using BirkNext.Api.Services.BrowserAutomationDiagnostic;
using BirkNext.Api.Services.HeadlessAuthDiagnostic;
using BirkNext.HeadlessAuthDiagnostic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BirkNext.Api.Tests.Unit.HeadlessAuthDiagnostic;

public sealed class HeadlessEdgeTheoryAttribute : TheoryAttribute
{
    public HeadlessEdgeTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_HEADLESS_EDGE_DIAGNOSTIC_TESTS") != "true")
            Skip = "Set RUN_HEADLESS_EDGE_DIAGNOSTIC_TESTS=true to run real headless Edge against local synthetic pages.";
    }
}

public sealed class HeadlessEdgeAcceptanceTests
{
    [HeadlessEdgeTheory]
    [InlineData("login", HeadlessBlocker.InteractiveAuthenticationRequired, HeadlessReadiness.NotReady)]
    [InlineData("mfa", HeadlessBlocker.InteractiveMfaRequired, HeadlessReadiness.NotReady)]
    [InlineData("ready", HeadlessBlocker.None, HeadlessReadiness.Ready)]
    [InlineData("public", HeadlessBlocker.Unknown, HeadlessReadiness.Unknown)]
    [InlineData("foreign-ca-text", HeadlessBlocker.Unknown, HeadlessReadiness.Unknown)]
    [InlineData("possible-session", HeadlessBlocker.Unknown, HeadlessReadiness.Unknown)]
    public async Task RealHeadlessEdgeObservesOnlySafeEvidence(string scenario, HeadlessBlocker blocker, HeadlessReadiness readiness)
    {
        var body = scenario switch
        {
            "login" => "<input name=loginfmt value=DO_NOT_CAPTURE><input type=password value=DO_NOT_CAPTURE>",
            "mfa" => "<div id=idDiv_SAOTCAS_Description>Approve sign-in request</div><input name=otc value=DO_NOT_CAPTURE>",
            "ready" => "<main id=authenticated-shell>Authenticated fixture</main>",
            "foreign-ca-text" => "<main>AADSTS53003 Conditional Access</main>",
            "possible-session" => "<main>Microsoft Defender for Cloud Apps</main>",
            _ => "<main>Public application shell</main>"
        };
        await using var fixture = new Fixture(body);
        var request = new HeadlessDiagnosticRequest { TargetEnvironmentId = "local-fixture", TargetEnvironmentName = "Local synthetic fixture", EnvironmentType = "Local", TargetUrl = fixture.Url,
            Authority = scenario is "login" or "mfa" ? fixture.Url : null };
        var proof = new BrowserAutomationEvidenceStore();
        // Unit integration fixture seeds only the prerequisite; launch/control/navigation/auth observation are real Edge.
        proof.Record(new() { TargetEnvironmentId = request.TargetEnvironmentId, TargetUrl = request.TargetUrl, TargetEnvironmentType = request.EnvironmentType,
            Modes = [new() { Mode = BrowserAutomationDiagnosticMode.Headless, Result = BrowserAutomationDiagnosticModeResult.Available,
                Stages = [new(BrowserAutomationDiagnosticStage.TargetControl, BrowserAutomationDiagnosticStageState.Passed)] }] });
        var options = Options.Create(new HeadlessDiagnosticOptions { Verification = new() { [request.TargetEnvironmentId] = new() { TargetOrigin = fixture.Url, AuthenticatedSelector = "#authenticated-shell" } } });
        var service = new HeadlessDiagnosticService(new PlaywrightHeadlessBrowserFactory(options), proof, options, NullLogger<HeadlessDiagnosticService>.Instance)
            { ObservationTimeout = TimeSpan.FromSeconds(2) };
        var report = await service.RunAsync(request);
        Assert.True(blocker == report.PrimaryBlocker, System.Text.Json.JsonSerializer.Serialize(report));
        Assert.Equal(readiness, report.Readiness);
        Assert.Equal("Available", report.HeadlessBrowser);
        Assert.Equal("Available", report.TargetControl);
        Assert.Equal(HeadlessStageState.Passed, report.Stage(HeadlessStage.Cleanup).State);
        Assert.DoesNotContain("DO_NOT_CAPTURE", HeadlessItReport.Build(report));
        if (scenario == "foreign-ca-text") Assert.NotEqual("Explicit block observed", report.ConditionalAccess);
        if (scenario == "possible-session") Assert.Equal("Possible session-control signal", report.SessionControl);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _server;
        public string Url { get; }
        public Fixture(string content)
        {
            _listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _server = ServeAsync("<!doctype html><html><head><title>Headless diagnostic fixture</title></head><body>" + content + "</body></html>");
        }
        private async Task ServeAsync(string body)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    await stream.ReadAsync(new byte[8192], _stop.Token);
                    var response = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
                    await stream.WriteAsync(response, _stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _server; _stop.Dispose(); }
    }
}
