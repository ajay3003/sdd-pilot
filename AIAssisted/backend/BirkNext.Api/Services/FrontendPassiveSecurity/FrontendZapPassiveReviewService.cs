using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace BirkNext.Api.Services.FrontendPassiveSecurity;

public sealed class FrontendZapPassiveReviewService(
    ILogger<FrontendZapPassiveReviewService> logger,
    PassiveSecurityTargetAuthorizer authorizer,
    PassiveSecurityEvidenceSanitizer sanitizer,
    IConfiguration configuration,
    IZapProcessRunner runner) : IFrontendZapPassiveReviewService
{
    public const string Image = "ghcr.io/zaproxy/zaproxy:2.16.1";
    public const string PassiveLimitation = "Passive automated scanning cannot prove that an application is secure and does not replace authenticated or active penetration testing.";
    internal static readonly string[] DaemonArguments = ["zap.sh", "-daemon", "-host", "0.0.0.0", "-port", "8080", "-config", "api.disablekey=true", "-config", "api.addrs.addr.name=.*", "-config", "api.addrs.addr.regex=true"];
    private readonly bool _enabled = configuration.GetValue<bool>("FrontendPassiveSecurity:Enabled");
    private readonly string _containerRuntime = configuration.GetValue<string>("FrontendPassiveSecurity:ContainerRuntime") ?? "docker";
    private readonly string? _containerNetwork = configuration.GetValue<string>("FrontendPassiveSecurity:ContainerNetwork");

    public async Task<PassiveSecurityReadiness> CheckReadinessAsync(CancellationToken ct = default)
    {
        if (!_enabled) return new(PassiveSecurityReadinessState.Disabled, false, Image: Image, Error: "Passive security engine is disabled.");
        var docker = await runner.RunAsync(_containerRuntime, ["version", "--format", "{{.Server.Version}}"], 15000, ct);
        if (docker.ExitCode != 0) return new(PassiveSecurityReadinessState.DockerUnavailable, false, Image: Image, Error: CleanError(docker));
        var inspect = await runner.RunAsync(_containerRuntime, ["image", "inspect", Image, "--format", "{{index .RepoDigests 0}}"], 15000, ct);
        if (inspect.ExitCode != 0) return new(PassiveSecurityReadinessState.ZapImageUnavailable, false, Image: Image, Error: "Pinned ZAP image is not locally available; no scan-time download is performed.");
        var version = await runner.RunAsync(_containerRuntime, ["run", "--rm", "--network", "none", Image, "zap.sh", "-version"], 30000, ct);
        if (version.ExitCode != 0) return new(PassiveSecurityReadinessState.ZapLaunchFailed, false, Image: Image, ImageDigest: inspect.Output.Trim(), Error: CleanError(version));
        return new(PassiveSecurityReadinessState.Ready, true, ParseVersion(version.Output + version.Error), Image: Image, ImageDigest: inspect.Output.Trim());
    }

    public async Task<PassiveSecurityResult> ReviewAsync(PassiveSecurityReviewRequest request, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        if (!_enabled) return Fail(PassiveSecurityExecutionStatus.Skipped, request.TargetUrl, started, "Passive security engine is disabled.");
        if (request.RequiresAuthentication) return Fail(PassiveSecurityExecutionStatus.AuthenticationRequired, request.TargetUrl, started, "Authentication is required; Phase 2D does not replay credentials or sessions.");
        var authorized = authorizer.Authorize(request);
        if (!authorized.IsValid) return Fail(PassiveSecurityExecutionStatus.Skipped, request.TargetUrl, started, authorized.BlockReason ?? "Target is not authorized.");
        var ready = await CheckReadinessAsync(ct);
        if (!ready.Available) return Fail(PassiveSecurityExecutionStatus.EngineError, request.TargetUrl, started, ready.Error ?? ready.State.ToString());

        var port = FreePort();
        var container = $"birknext-zap-passive-{Guid.NewGuid():N}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 10, 600)));
        try
        {
            var args = BuildContainerArguments(container, port, _containerNetwork);
            var launch = await runner.RunAsync(_containerRuntime, args, 30000, timeout.Token);
            if (launch.TimedOut || launch.Cancelled) throw new OperationCanceledException(timeout.Token);
            if (launch.ExitCode != 0) return Fail(PassiveSecurityExecutionStatus.EngineError, request.TargetUrl, started, CleanError(launch));
            using var api = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
            // ZAP identifies API requests by its container-side listener authority. With a
            // dynamic published host port, the default Host header is otherwise treated as
            // an ordinary proxy destination and ZAP attempts to connect back to that port.
            api.DefaultRequestHeaders.Host = "127.0.0.1:8080";
            await WaitForZapAsync(api, timeout.Token);
            var proxyTarget = RewriteLoopbackForContainer(request.TargetUrl, _containerRuntime);
            using (var proxied = new HttpClient(new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) })
            using (var response = await proxied.GetAsync(proxyTarget, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
            {
                if (response.Headers.Location is { } location)
                {
                    var absolute = location.IsAbsoluteUri ? location : new Uri(new Uri(request.TargetUrl), location);
                    var redirect = authorizer.AuthorizeRedirect(request, absolute.AbsoluteUri);
                    if (!redirect.IsValid) return Fail(PassiveSecurityExecutionStatus.Skipped, request.TargetUrl, started, redirect.BlockReason ?? "Redirect scope blocked.");
                }
            }
            await DrainPassiveQueueAsync(api, timeout.Token);
            var json = await api.GetStringAsync($"JSON/core/view/alerts/?baseurl={Uri.EscapeDataString(proxyTarget)}", timeout.Token);
            var findings = Normalize(json, request.TargetUrl, _containerRuntime);
            var final = DateTime.UtcNow;
            return new(PassiveSecurityExecutionStatus.Assessed, ZapVersion: ready.ZapVersion, RequestedUrl: request.TargetUrl,
                FinalUrl: request.TargetUrl, StartedAt: started, CompletedAt: final, DurationMs: (long)(final - started).TotalMilliseconds,
                HighCount: findings.Count(f => f.Risk == "High"), MediumCount: findings.Count(f => f.Risk == "Medium"),
                LowCount: findings.Count(f => f.Risk == "Low"), InformationalCount: findings.Count(f => f.Risk == "Informational"),
                Findings: findings, ConfigurationSummary: new(MaxDurationSeconds: request.TimeoutSeconds));
        }
        catch (OperationCanceledException)
        {
            return Fail(ct.IsCancellationRequested ? PassiveSecurityExecutionStatus.EngineError : PassiveSecurityExecutionStatus.TimedOut,
                request.TargetUrl, started, ct.IsCancellationRequested ? "Passive ZAP execution was cancelled." : "Passive ZAP execution timed out.");
        }
        catch (Exception ex) { logger.LogWarning(ex, "ZAP passive assessment failed"); return Fail(PassiveSecurityExecutionStatus.EngineError, request.TargetUrl, started, sanitizer.Sanitize(ex.Message)); }
        finally { await runner.RunAsync(_containerRuntime, ["rm", "--force", container], 15000, CancellationToken.None); }
    }

    internal List<PassiveSecurityFinding> Normalize(string json, string requestedUrl, string containerRuntime)
    {
        using var doc = JsonDocument.Parse(json);
        var alerts = doc.RootElement.GetProperty("alerts").EnumerateArray();
        var loopbackAlias = containerRuntime == "podman" ? "host.containers.internal" : "host.docker.internal";
        return alerts.GroupBy(a => $"{Get(a,"pluginId")}|{Get(a,"alertRef")}|{Get(a,"url")}")
            .Select(g => { var a = g.First(); var risk = NormalizeRisk(Get(a,"risk")); return new PassiveSecurityFinding(
                Get(a,"pluginId"), Get(a,"alertRef"), sanitizer.Sanitize(Get(a,"alert")), risk,
                sanitizer.Sanitize(Get(a,"confidence")), sanitizer.Sanitize(Get(a,"description")),
                sanitizer.Sanitize(Get(a,"url").Replace(loopbackAlias, new Uri(requestedUrl).Host, StringComparison.OrdinalIgnoreCase)),
                sanitizer.Sanitize(Get(a,"param")), sanitizer.Sanitize(Get(a,"evidence")), sanitizer.Sanitize(Get(a,"solution")),
                Get(a,"reference").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(sanitizer.Sanitize).ToList(), Get(a,"cweid"), Get(a,"wascid"), g.Count()); }).ToList();
    }

    private static string Get(JsonElement e, string n) => e.TryGetProperty(n, out var p) ? p.ToString() : "";
    internal static string NormalizeRisk(string risk) => risk.StartsWith("High", StringComparison.OrdinalIgnoreCase) ? "High" : risk.StartsWith("Medium", StringComparison.OrdinalIgnoreCase) ? "Medium" : risk.StartsWith("Low", StringComparison.OrdinalIgnoreCase) ? "Low" : "Informational";
    internal static List<string> BuildContainerArguments(string container, int port, string? network = null) => ["run", "--detach", "--rm", "--name", container, "--label", "birknext.engine=zap-passive", .. (string.IsNullOrWhiteSpace(network) ? Array.Empty<string>() : new[] { "--network", network }), "-p", $"127.0.0.1:{port}:8080", Image, .. DaemonArguments];
    private static async Task WaitForZapAsync(HttpClient api, CancellationToken ct) { while (true) { try { if ((await api.GetStringAsync("JSON/core/view/version/", ct)).Contains("version")) return; } catch { } await Task.Delay(250, ct); } }
    private static async Task DrainPassiveQueueAsync(HttpClient api, CancellationToken ct) { while (true) { var j = await api.GetStringAsync("JSON/pscan/view/recordsToScan/", ct); using var d = JsonDocument.Parse(j); if (d.RootElement.GetProperty("recordsToScan").ToString() == "0") return; await Task.Delay(250, ct); } }
    private static string RewriteLoopbackForContainer(string url, string containerRuntime)
    {
        var u = new UriBuilder(url);
        if (u.Host is "localhost" or "127.0.0.1" or "::1")
        {
            u.Host = containerRuntime == "podman" ? "host.containers.internal" : "host.docker.internal";
        }
        return u.Uri.AbsoluteUri;
    }
    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
    private static string ParseVersion(string s) => System.Text.RegularExpressions.Regex.Match(s, @"\d+\.\d+\.\d+").Value;
    private static string CleanError(ZapProcessResult r) => string.IsNullOrWhiteSpace(r.Error) ? r.Output.Trim() : r.Error.Trim();
    private static PassiveSecurityResult Fail(PassiveSecurityExecutionStatus status, string url, DateTime started, string error) => new(status, RequestedUrl: url, StartedAt: started, CompletedAt: DateTime.UtcNow, EngineError: error, ConfigurationSummary: new());
}
