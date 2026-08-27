using System.Diagnostics;
using System.Text.Json;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.FrontendLighthouse;

public sealed class FrontendLighthouseReviewService : IFrontendLighthouseReviewService
{
    public const string LabLimitation = "Lighthouse provides synthetic lab measurements. Field data and real-user Core Web Vitals are not included.";
    public const string InpLimitation = "INP requires field/real-user interaction data and is not assessed by this Lighthouse lab scan.";
    private readonly ILogger<FrontendLighthouseReviewService> _logger;
    private readonly BrowserTargetValidator _validator;
    private readonly LighthouseEvidenceSanitizer _sanitizer;
    private readonly string _runnerPath;
    private readonly string _nodeExecutable;
    private readonly bool _enabled;

    public FrontendLighthouseReviewService(
        ILogger<FrontendLighthouseReviewService> logger,
        BrowserTargetValidator validator,
        LighthouseEvidenceSanitizer sanitizer,
        IWebHostEnvironment environment,
        IOptions<FrontendLighthouseOptions> options)
        : this(logger, validator, sanitizer,
            System.IO.Path.Combine(environment.ContentRootPath, "Tools", "LighthouseRunner", "run-lighthouse.mjs"), "node", options.Value.Enabled) { }

    internal FrontendLighthouseReviewService(
        ILogger<FrontendLighthouseReviewService> logger,
        BrowserTargetValidator validator,
        LighthouseEvidenceSanitizer sanitizer,
        string runnerPath,
        string nodeExecutable,
        bool enabled = true)
    {
        _logger = logger;
        _validator = validator;
        _sanitizer = sanitizer;
        _runnerPath = runnerPath;
        _nodeExecutable = nodeExecutable;
        _enabled = enabled;
    }

    public async Task<LighthouseReviewResult> ReviewAsync(string targetUrl, LighthouseReviewOptions? options = null,
        bool requiresAuthentication = false, CancellationToken cancellationToken = default)
    {
        options ??= new();
        var started = DateTime.UtcNow;
        if (requiresAuthentication)
            return Failure(LighthouseExecutionStatus.AuthenticationRequired, targetUrl, started,
                "Anonymous Phase 2C Lighthouse assessment cannot review an authenticated target.");
        var validation = _validator.ValidateTarget(targetUrl, options.EnvironmentType);
        if (!validation.IsValid)
            return Failure(LighthouseExecutionStatus.Skipped, targetUrl, started, validation.BlockReason ?? "Target blocked by safety policy.");
        if (!_enabled)
            return Failure(LighthouseExecutionStatus.Skipped, targetUrl, started, "Lighthouse review engine is disabled.");
        if (!File.Exists(_runnerPath))
            return Failure(LighthouseExecutionStatus.EngineError, targetUrl, started, "Pinned Lighthouse runner is unavailable.");

        var execution = await ExecuteAsync([_runnerPath, $"--url={targetUrl}"], options.TimeoutMs, cancellationToken);
        if (execution.TimedOut)
            return Failure(LighthouseExecutionStatus.TimedOut, targetUrl, started, "Lighthouse execution timed out.");
        if (execution.ExitCode != 0)
            return Failure(LighthouseExecutionStatus.EngineError, targetUrl, started,
                _sanitizer.SanitizeText(execution.Error) ?? "Lighthouse process failed.");
        try
        {
            var raw = JsonSerializer.Deserialize<RunnerResult>(execution.Output, JsonOptions)
                ?? throw new InvalidOperationException("Lighthouse returned no normalized result.");
            var finalValidation = _validator.ValidateRedirectTarget(raw.FinalUrl ?? targetUrl, new Uri(targetUrl).Host, options.EnvironmentType);
            if (!finalValidation.IsValid)
                return Failure(LighthouseExecutionStatus.Skipped, targetUrl, started, finalValidation.BlockReason ?? "Redirect blocked by safety policy.");
            var completed = DateTime.UtcNow;
            return new(
                LighthouseExecutionStatus.Assessed,
                LighthouseVersion: raw.LighthouseVersion,
                NodeVersion: raw.NodeVersion,
                BrowserName: raw.BrowserName,
                BrowserVersion: raw.BrowserVersion,
                RequestedUrl: _sanitizer.SanitizeUrl(raw.RequestedUrl ?? targetUrl),
                FinalUrl: _sanitizer.SanitizeUrl(raw.FinalUrl),
                StartedAt: started,
                CompletedAt: completed,
                DurationMs: (long)(completed - started).TotalMilliseconds,
                PerformanceScore: raw.PerformanceScore,
                Metrics: raw.Metrics.Select(NormalizeMetric).ToList(),
                Audits: raw.Audits.Select(a => new LighthouseAuditFinding(a.AuditId, a.Title,
                    _sanitizer.SanitizeText(a.Description), a.Score, _sanitizer.SanitizeText(a.DisplayValue), ["Lighthouse"])).ToList(),
                Limitations: [LabLimitation, InpLimitation],
                EffectiveConfiguration: new(Categories: ["Performance"]));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to normalize Lighthouse result for {TargetUrl}", targetUrl);
            return Failure(LighthouseExecutionStatus.EngineError, targetUrl, started, _sanitizer.SanitizeText(ex.Message) ?? "Result parsing failed.");
        }
    }

    public async Task<LighthouseReadinessResult> CheckReadinessAsync(CancellationToken cancellationToken = default)
    {
        if (!_enabled)
            return new(LighthouseReadinessState.Disabled, false, Error: "Lighthouse review engine is disabled.");
        if (!File.Exists(_runnerPath))
            return new(LighthouseReadinessState.LighthouseUnavailable, false, Error: "Pinned Lighthouse runner is unavailable.");
        var execution = await ExecuteAsync([_runnerPath, "--readiness"], 30000, cancellationToken);
        if (execution.TimedOut)
            return new(LighthouseReadinessState.LaunchFailed, false, Error: "Lighthouse readiness timed out.");
        if (execution.ExitCode != 0)
        {
            var state = execution.Error.Contains("node", StringComparison.OrdinalIgnoreCase)
                ? LighthouseReadinessState.NodeUnavailable : LighthouseReadinessState.LaunchFailed;
            return new(state, false, BrowserName: "Chromium", Error: _sanitizer.SanitizeText(execution.Error));
        }
        try
        {
            var raw = JsonSerializer.Deserialize<ReadinessRunnerResult>(execution.Output, JsonOptions)!;
            return new(LighthouseReadinessState.Ready, raw.Available, raw.LighthouseVersion, raw.NodeVersion,
                raw.BrowserName, raw.BrowserVersion, null);
        }
        catch (Exception ex)
        {
            return new(LighthouseReadinessState.ConfigurationInvalid, false, Error: ex.Message);
        }
    }

    private async Task<ProcessResult> ExecuteAsync(IReadOnlyList<string> arguments, int timeoutMs, CancellationToken cancellationToken)
    {
        var started = false;
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = _nodeExecutable,
            WorkingDirectory = System.IO.Path.GetDirectoryName(_runnerPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            started = true;
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try { await process.WaitForExitAsync(linked.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                return new(-1, await Safe(stdout), await Safe(stderr), timeout.IsCancellationRequested);
            }
            return new(process.ExitCode, await stdout, await stderr, false);
        }
        catch (Exception ex)
        {
            if (started && !process.HasExited) process.Kill(entireProcessTree: true);
            return new(-1, "", ex.Message, false);
        }
    }

    private static async Task<string> Safe(Task<string> task) { try { return await task; } catch { return ""; } }

    private static LighthouseMetric NormalizeMetric(RunnerMetric metric)
    {
        var status = metric.Status == "FieldDataRequired" ? LighthouseMetricStatus.FieldDataRequired
            : metric.ObservedValue is null ? LighthouseMetricStatus.NotAvailable
            : metric.Name switch
            {
                "LCP" => metric.ObservedValue <= 2500 ? LighthouseMetricStatus.Good : metric.ObservedValue > 4000 ? LighthouseMetricStatus.Poor : LighthouseMetricStatus.NeedsImprovement,
                "CLS" => metric.ObservedValue <= .1 ? LighthouseMetricStatus.Good : metric.ObservedValue > .25 ? LighthouseMetricStatus.Poor : LighthouseMetricStatus.NeedsImprovement,
                "TBT" => metric.ObservedValue <= 200 ? LighthouseMetricStatus.Good : metric.ObservedValue > 600 ? LighthouseMetricStatus.Poor : LighthouseMetricStatus.NeedsImprovement,
                _ => LighthouseMetricStatus.Measured
            };
        var threshold = metric.Name switch { "LCP" => 2500, "CLS" => .1, "TBT" => 200, _ => (double?)null };
        var thresholdSource = metric.Name switch
        {
            "LCP" or "CLS" => "Web Vitals lab diagnostic thresholds",
            "TBT" => "Lighthouse responsiveness diagnostic threshold",
            _ => null
        };
        return new(metric.Name, metric.ObservedValue, metric.Unit, status, "Lighthouse", "Lab", metric.AuditId, threshold, thresholdSource);
    }

    private static LighthouseReviewResult Failure(LighthouseExecutionStatus status, string url, DateTime started, string error) =>
        new(status, RequestedUrl: url, StartedAt: started, CompletedAt: DateTime.UtcNow,
            Limitations: [LabLimitation, InpLimitation], EngineError: error,
            EffectiveConfiguration: new(Categories: ["Performance"]));

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed record ProcessResult(int ExitCode, string Output, string Error, bool TimedOut);
    private sealed record RunnerResult(string? LighthouseVersion, string? NodeVersion, string? BrowserName, string? BrowserVersion,
        string? RequestedUrl, string? FinalUrl, int? PerformanceScore, List<RunnerMetric> Metrics, List<RunnerAudit> Audits);
    private sealed record RunnerMetric(string Name, double? ObservedValue, string? Unit, string Status, string? AuditId);
    private sealed record RunnerAudit(string AuditId, string Title, string? Description, double? Score, string? DisplayValue);
    private sealed record ReadinessRunnerResult(bool Available, string? LighthouseVersion, string? NodeVersion, string? BrowserName, string? BrowserVersion);
}
