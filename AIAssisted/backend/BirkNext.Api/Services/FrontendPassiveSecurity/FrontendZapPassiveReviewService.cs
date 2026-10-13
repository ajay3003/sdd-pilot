using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.ContainerRuntime;
using BirkNext.Api.Services.FrontendQualityEngines;

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
    internal static readonly string[] DaemonArguments = ["zap.sh", "-Xmx512m", "-daemon", "-dir", "/tmp/zap", "-host", "0.0.0.0", "-port", "8080", "-config", "api.addrs.addr.name=.*", "-config", "api.addrs.addr.regex=true", "-config", "pscans.maxBodySizeInBytes=1048576", "-config", "pscans.maxAlertsPerRule=20"];
    private const int MaxTargetResponseBytes = 2 * 1024 * 1024;
    private const int MaxAlertsResponseBytes = 2 * 1024 * 1024;
    private readonly bool _enabled = FrontendQualityEngineEnablement.Resolve(
        configuration,
        FrontendQualityEngineId.PassiveSecurity,
        configuration.GetValue<bool>("FrontendPassiveSecurity:Enabled"));
    private readonly bool _installationAllowed = bool.TryParse(configuration["FrontendQualityCapabilities:PassiveSecurityAllowed"], out var installationAllowed) && installationAllowed;
    private readonly string _containerRuntime = configuration.GetValue<string>("FrontendPassiveSecurity:ContainerRuntime") ?? "podman";
    private readonly string? _containerNetwork = configuration.GetValue<string>("FrontendPassiveSecurity:ContainerNetwork");
    private IReadOnlySet<string> _resourceControllers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private List<string> _resourceLimitations = [];

    public async Task<PassiveSecurityReadiness> CheckReadinessAsync(CancellationToken ct = default)
    {
        if (!_installationAllowed) return new(PassiveSecurityReadinessState.InstallationPolicyBlocked, false, Image: Image,
            Error: "Installation policy does not allow container security engines.");
        if (!_enabled) return new(PassiveSecurityReadinessState.Disabled, false, Image: Image, Error: "Passive security engine is disabled.");
        if (_containerRuntime is not ("podman" or "docker") || (_containerNetwork is not null && !ContainerRuntime.ContainerArgumentRules.IsNetwork(_containerNetwork)))
            return new(PassiveSecurityReadinessState.ConfigurationInvalid, false, Image: Image, Error: "Passive Security container configuration is invalid.");
        var docker = await runner.RunAsync(_containerRuntime, ["version", "--format", "{{.Server.Version}}"], 15000, ct);
        if (docker.ExitCode != 0)
        {
            logger.LogWarning("Container runtime readiness failed for configured runtime {Runtime}.", _containerRuntime);
            return new(PassiveSecurityReadinessState.ContainerRuntimeUnavailable, false, Image: Image,
                Error: "Container runtime is unavailable.");
        }
        _resourceControllers = _containerRuntime == "podman"
            ? await ReadPodmanControllersAsync(ct)
            : new HashSet<string>(["cpu", "memory", "pids"], StringComparer.OrdinalIgnoreCase);
        _resourceLimitations = [.. new[] { ("cpu", "CPU"), ("memory", "memory"), ("pids", "process-count") }
            .Where(x => !_resourceControllers.Contains(x.Item1)).Select(x => $"Container {x.Item2} limits are not delegated by this runtime; BirkNext request, response, alert and time limits remain enforced.")];
        var inspect = await runner.RunAsync(_containerRuntime, ["image", "inspect", Image, "--format", "{{index .RepoDigests 0}}"], 15000, ct);
        if (inspect.ExitCode != 0) return new(PassiveSecurityReadinessState.ZapImageUnavailable, false, Image: Image, Error: "Pinned ZAP image is not locally available; no scan-time download is performed.");
        var probeContainer = $"birknext-zap-readiness-{Guid.NewGuid():N}";
        ZapProcessResult version;
        try { version = await runner.RunAsync(_containerRuntime, BuildProbeArguments(probeContainer, _resourceControllers), 30000, ct); }
        finally { await CleanupProbeAsync(probeContainer); }
        if (version.ExitCode != 0)
        {
            logger.LogWarning("Passive Security runtime version probe failed.");
            return new(PassiveSecurityReadinessState.ZapLaunchFailed, false, Image: Image, ImageDigest: inspect.Output.Trim(),
                Error: "Passive Security runtime could not be started.");
        }
        return new(PassiveSecurityReadinessState.Ready, true, ParseVersion(version.Output + version.Error), Image: Image,
            ImageDigest: inspect.Output.Trim(), Limitations: _resourceLimitations);
    }

    public async Task<PassiveSecurityResult> ReviewAsync(PassiveSecurityReviewRequest request, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        if (!_installationAllowed) return Fail(PassiveSecurityExecutionStatus.Skipped, SafeRequestedUrl(request.TargetUrl), started,
            "Installation policy does not allow container security engines.", PassiveSecurityOutcomeReason.InstallationPolicyBlocked);
        if (!_enabled) return Fail(PassiveSecurityExecutionStatus.Skipped, SafeRequestedUrl(request.TargetUrl), started, "Passive security engine is disabled.", PassiveSecurityOutcomeReason.DisabledInSystemSettings);
        if (request.RequiresAuthentication) return Fail(PassiveSecurityExecutionStatus.AuthenticationRequired, SafeRequestedUrl(request.TargetUrl), started, "Authentication is required; authenticated traffic is not supported by this engine.", PassiveSecurityOutcomeReason.AuthenticationModeUnsupported);
        var authorized = authorizer.Authorize(request);
        if (!authorized.IsValid) return Fail(PassiveSecurityExecutionStatus.Skipped, SafeRequestedUrl(request.TargetUrl), started, authorized.BlockReason ?? "Target is not authorized.", PassiveSecurityOutcomeReason.TargetPolicyRejected);
        var ready = await CheckReadinessAsync(ct);
        if (!ready.Available) return Fail(PassiveSecurityExecutionStatus.Skipped, SafeRequestedUrl(request.TargetUrl), started, ready.Error ?? ready.State.ToString(), PassiveSecurityOutcomeReason.ReadinessUnavailable);

        var port = FreePort();
        var container = $"birknext-zap-passive-{Guid.NewGuid():N}";
        // A fresh key scopes the remotely reachable ZAP API to this one ephemeral run.
        // It is never returned, persisted, or logged.
        var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 10, 600)));
        try
        {
            var args = BuildContainerArguments(container, port, apiKey, _containerNetwork, _resourceControllers);
            var launch = await runner.RunAsync(_containerRuntime, args, 30000, timeout.Token);
            if (launch.TimedOut || launch.Cancelled) throw new OperationCanceledException(timeout.Token);
            if (launch.ExitCode != 0) return Fail(PassiveSecurityExecutionStatus.EngineError, SafeRequestedUrl(request.TargetUrl), started, "The isolated ZAP container could not be started.", PassiveSecurityOutcomeReason.EngineError);
            using var api = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
            // ZAP identifies API requests by its container-side listener authority. With a
            // dynamic published host port, the default Host header is otherwise treated as
            // an ordinary proxy destination and ZAP attempts to connect back to that port.
            api.DefaultRequestHeaders.Host = "127.0.0.1:8080";
            api.DefaultRequestHeaders.TryAddWithoutValidation("X-ZAP-API-Key", apiKey);
            using (var startup = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                startup.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("FrontendPassiveSecurity:StartupTimeoutSeconds", 30), 5, 120)));
                try { await WaitForZapAsync(api, startup.Token); }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
                {
                    return Fail(PassiveSecurityExecutionStatus.EngineError, SafeRequestedUrl(request.TargetUrl), started,
                        "The isolated ZAP container did not become ready before the startup timeout.", PassiveSecurityOutcomeReason.EngineError);
                }
            }
            var proxyTarget = RewriteLoopbackForContainer(request.TargetUrl, _containerRuntime);
            using (var proxied = new HttpClient(new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) })
            using (var response = await proxied.GetAsync(proxyTarget, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
            {
                await DrainBoundedResponseAsync(response, MaxTargetResponseBytes, timeout.Token);
                if (response.Headers.Location is { } location)
                {
                    var absolute = location.IsAbsoluteUri ? location : new Uri(new Uri(request.TargetUrl), location);
                    var redirect = authorizer.AuthorizeRedirect(request, absolute.AbsoluteUri);
                    if (!redirect.IsValid) return Fail(PassiveSecurityExecutionStatus.Skipped, SafeRequestedUrl(request.TargetUrl), started, redirect.BlockReason ?? "Redirect scope blocked.", PassiveSecurityOutcomeReason.TargetPolicyRejected);
                    using var redirectedResponse = await proxied.GetAsync(absolute.AbsoluteUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    await DrainBoundedResponseAsync(redirectedResponse, MaxTargetResponseBytes, timeout.Token);
                }
            }
            await DrainPassiveQueueAsync(api, timeout.Token);
            using var alertsResponse = await api.GetAsync($"JSON/core/view/alerts/?baseurl={Uri.EscapeDataString(proxyTarget)}", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            alertsResponse.EnsureSuccessStatusCode();
            var json = await ReadBoundedAsync(alertsResponse.Content, MaxAlertsResponseBytes, timeout.Token);
            var findings = Normalize(json, request.TargetUrl, _containerRuntime);
            var final = DateTime.UtcNow;
            return new(PassiveSecurityExecutionStatus.Assessed, ZapVersion: ready.ZapVersion, RequestedUrl: SafeRequestedUrl(request.TargetUrl),
                FinalUrl: SafeRequestedUrl(request.TargetUrl), StartedAt: started, CompletedAt: final, DurationMs: (long)(final - started).TotalMilliseconds,
                HighCount: findings.Count(f => f.Risk == "High"), MediumCount: findings.Count(f => f.Risk == "Medium"),
                LowCount: findings.Count(f => f.Risk == "Low"), InformationalCount: findings.Count(f => f.Risk == "Informational"),
                Findings: findings, Limitations: [PassiveLimitation, .. ready.Limitations ?? []],
                ConfigurationSummary: new(MaxDurationSeconds: Math.Clamp(request.TimeoutSeconds, 10, 600)), OutcomeReason: PassiveSecurityOutcomeReason.None);
        }
        catch (OperationCanceledException)
        {
            return Fail(ct.IsCancellationRequested ? PassiveSecurityExecutionStatus.EngineError : PassiveSecurityExecutionStatus.TimedOut,
                SafeRequestedUrl(request.TargetUrl), started, ct.IsCancellationRequested ? "Passive ZAP execution was cancelled." : "Passive ZAP execution timed out.",
                ct.IsCancellationRequested ? PassiveSecurityOutcomeReason.Cancelled : PassiveSecurityOutcomeReason.EngineError);
        }
        catch (Exception ex) { logger.LogWarning("ZAP passive assessment failed ({ExceptionType})", ex.GetType().Name); return Fail(PassiveSecurityExecutionStatus.EngineError, SafeRequestedUrl(request.TargetUrl), started, "Passive ZAP assessment failed; raw engine details were discarded.", PassiveSecurityOutcomeReason.EngineError); }
        finally
        {
            try
            {
                await runner.RunAsync(_containerRuntime, ["rm", "--force", container], 15000, CancellationToken.None);
                var remaining = await runner.RunAsync(_containerRuntime, ["container", "exists", container], 10000, CancellationToken.None);
                if (remaining.ExitCode == 0) logger.LogError("Passive ZAP container cleanup could not be confirmed for managed run {RunId}.", container);
            }
            catch (Exception ex) { logger.LogError("Passive ZAP container cleanup check failed ({ExceptionType}) for managed run {RunId}.", ex.GetType().Name, container); }
        }
    }

    internal List<PassiveSecurityFinding> Normalize(string json, string requestedUrl, string containerRuntime)
    {
        using var doc = JsonDocument.Parse(json);
        var alerts = doc.RootElement.GetProperty("alerts").EnumerateArray();
        if (alerts.Count() > 1000) throw new InvalidOperationException("Alert count limit exceeded.");
        var loopbackAlias = containerRuntime == "podman" ? "host.containers.internal" : "host.docker.internal";
        return alerts.GroupBy(a => $"{Get(a,"pluginId")}|{Get(a,"alertRef")}|{SanitizeUrl(Get(a,"url").Replace(loopbackAlias, new Uri(requestedUrl).Host, StringComparison.OrdinalIgnoreCase))}|{Get(a,"param")}")
            .Select(g => { var a = g.First(); var risk = NormalizeRisk(Get(a,"risk")); return new PassiveSecurityFinding(
                Get(a,"pluginId"), Get(a,"alertRef"), sanitizer.Sanitize(Get(a,"alert")), risk,
                sanitizer.Sanitize(Get(a,"confidence")), "A passive rule was observed on the bounded response. Raw response content was not retained; verify the alert manually.",
                SanitizeUrl(Get(a,"url").Replace(loopbackAlias, new Uri(requestedUrl).Host, StringComparison.OrdinalIgnoreCase)),
                sanitizer.Sanitize(Get(a,"param")), null, sanitizer.Sanitize(Get(a,"solution")),
                Get(a,"reference").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(sanitizer.Sanitize).ToList(), Get(a,"cweid"), Get(a,"wascid"), g.Count()); }).ToList();
    }

    private static string Get(JsonElement e, string n) => e.TryGetProperty(n, out var p) ? p.ToString() : "";
    internal static string NormalizeRisk(string risk) => risk.StartsWith("High", StringComparison.OrdinalIgnoreCase) ? "High" : risk.StartsWith("Medium", StringComparison.OrdinalIgnoreCase) ? "Medium" : risk.StartsWith("Low", StringComparison.OrdinalIgnoreCase) ? "Low" : "Informational";
    internal static List<string> BuildContainerArguments(string container, int port, string apiKey, string? network = null, IReadOnlySet<string>? controllers = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length < 32 || apiKey.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("A per-run hexadecimal ZAP API key is required.", nameof(apiKey));
        var args = BuildContainerIsolationArguments(controllers);
        args.InsertRange(1, ["--detach", "--rm", "--name", container]);
        args.AddRange(["--label", "birknext.managed=true", "--label", "birknext.component=zap-passive", "--label", "birknext.engine=zap-passive"]);
        if (!string.IsNullOrWhiteSpace(network)) args.AddRange(["--network", network]);
        args.AddRange(["-p", $"127.0.0.1:{port}:8080", Image, .. DaemonArguments, "-config", $"api.key={apiKey}"]);
        return args;
    }
    internal static List<string> BuildProbeArguments(string container, IReadOnlySet<string>? controllers = null) =>
        [.. BuildContainerIsolationArguments(controllers), "--rm", "--name", container, "--network", "none", Image,
            "zap.sh", "-Xmx512m", "-dir", "/tmp/zap", "-version"];

    private static List<string> BuildContainerIsolationArguments(IReadOnlySet<string>? controllers)
    {
        var args = new List<string> { "run", "--pull=never", "--read-only", "--tmpfs", "/tmp:rw,size=512m", "--cap-drop=ALL", "--security-opt=no-new-privileges" };
        if (controllers?.Contains("memory") == true) args.Add("--memory=768m");
        if (controllers?.Contains("cpu") == true) args.Add("--cpus=1");
        if (controllers?.Contains("pids") == true) args.Add("--pids-limit=512");
        return args;
    }

    private async Task CleanupProbeAsync(string name)
    {
        try
        {
            await runner.RunAsync(_containerRuntime, ["rm", "--force", name], 15000, CancellationToken.None);
            var remaining = await runner.RunAsync(_containerRuntime, ["container", "exists", name], 10000, CancellationToken.None);
            if (remaining.ExitCode == 0) logger.LogError("Passive ZAP readiness probe cleanup could not be confirmed for probe {ProbeId}.", name);
        }
        catch { logger.LogError("Passive ZAP readiness probe cleanup could not be confirmed for probe {ProbeId}.", name); }
    }
    private static async Task WaitForZapAsync(HttpClient api, CancellationToken ct) { while (true) { try { if ((await api.GetStringAsync("JSON/core/view/version/", ct)).Contains("version")) return; } catch { } await Task.Delay(250, ct); } }
    private static async Task DrainPassiveQueueAsync(HttpClient api, CancellationToken ct) { while (true) { var j = await api.GetStringAsync("JSON/pscan/view/recordsToScan/", ct); using var d = JsonDocument.Parse(j); if (d.RootElement.GetProperty("recordsToScan").ToString() == "0") return; await Task.Delay(250, ct); } }
    private async Task<IReadOnlySet<string>> ReadPodmanControllersAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync(_containerRuntime, ["info", "--format", "{{.Host.CgroupControllers}}"], 15000, ct);
        if (result.ExitCode != 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return System.Text.RegularExpressions.Regex.Matches(result.Output, @"[A-Za-z]+")
            .Select(match => match.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
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
    private static string ParseVersion(string s)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(s, @"\d+\.\d+\.\d+");
        return matches.Count == 0 ? "" : matches[^1].Value;
    }
    private static string SafeRequestedUrl(string value) => SanitizeUrl(value);
    private static string SanitizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "";
        var builder = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
        builder.Path = string.Join('/', builder.Path.Split('/').Select(segment =>
            Guid.TryParse(segment, out _) || (segment.Length >= 4 && segment.All(char.IsAsciiDigit)) || segment.Contains('@')
                ? "[redacted]" : segment));
        return builder.Uri.AbsoluteUri;
    }
    private static async Task DrainBoundedResponseAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            throw new InvalidOperationException("The target response exceeded the passive scan size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            total += read;
            if (total > maxBytes) throw new InvalidOperationException("The target response exceeded the passive scan size limit.");
        }
    }
    private static async Task<string> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (output.Length + read > maxBytes) throw new InvalidOperationException("The passive scan response exceeded its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
    private static PassiveSecurityResult Fail(PassiveSecurityExecutionStatus status, string url, DateTime started, string error, PassiveSecurityOutcomeReason reason = PassiveSecurityOutcomeReason.None) => new(status, RequestedUrl: url, StartedAt: started, CompletedAt: DateTime.UtcNow, EngineError: error, ConfigurationSummary: new(), OutcomeReason: reason);
}
