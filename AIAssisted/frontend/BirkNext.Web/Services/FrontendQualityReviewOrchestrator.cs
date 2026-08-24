using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface ISecurityScanner
{
    Task<(WasmSecurityReviewReport?, string?)> ScanAsync(WasmScanRequest request);
}

public sealed class SecurityScannerAdapter : ISecurityScanner
{
    private readonly WasmSecurityApiService _inner;

    public SecurityScannerAdapter(WasmSecurityApiService inner) => _inner = inner;

    public Task<(WasmSecurityReviewReport?, string?)> ScanAsync(WasmScanRequest request) =>
        _inner.ScanAsync(request);
}

/// <summary>
/// Minimal orchestration boundary for feature-toggle and preflight logic.
/// Extracted ONLY for testability — no rendering logic included.
/// This orchestrates: preflight → toggles → scanner invocation → state tracking.
/// </summary>
public sealed class FrontendQualityReviewOrchestrator
{
    private readonly ISecurityScanner _security;
    private readonly IBlazorWasmPerformanceReviewService _performance;
    private readonly ITargetPreflightService _preflight;
    private readonly IFrontendQualityReviewService _quality;
    private readonly IFrontendBrowserRuntimeReviewApiService? _runtime;

    public FrontendQualityReviewOrchestrator(
        ISecurityScanner security,
        IBlazorWasmPerformanceReviewService performance,
        ITargetPreflightService preflight,
        IFrontendQualityReviewService quality,
        IFrontendBrowserRuntimeReviewApiService? runtime = null)
    {
        _security = security;
        _performance = performance;
        _preflight = preflight;
        _quality = quality;
        _runtime = runtime;
    }

    public sealed record OrchestrationResult(
        WasmSecurityReviewReport? SecurityReport = null,
        WasmPerformanceReviewReport? PerformanceReport = null,
        BrowserRuntimeResultDto? BrowserRuntimeReport = null,
        string? SecurityError = null,
        string? PerformanceError = null,
        string? BrowserRuntimeError = null,
        List<string>? SkippedEngines = null,
        bool PreflightBlocked = false,
        string? PreflightBlockReason = null,
        PreflightStatus PreflightStatus = PreflightStatus.Ready)
    {
        public List<string> SkippedEngines { get; init; } = SkippedEngines ?? [];
    }

    public async Task<OrchestrationResult> RunAsync(
        string targetUrl,
        FrontendAnalysisContext context,
        CancellationToken cancellationToken = default)
    {
        var result = new OrchestrationResult();

        // ── Preflight validation ────────────────────────────────
        try
        {
            var preflightResult = await _preflight.CheckTargetAsync(targetUrl);
            result = result with { PreflightStatus = preflightResult.Status };

            // Block scanners on error statuses
            if (preflightResult.Status is PreflightStatus.Unreachable
                or PreflightStatus.InvalidTarget
                or PreflightStatus.AuthenticationRequired
                or PreflightStatus.ScannerUnavailable)
            {
                return result with
                {
                    PreflightBlocked = true,
                    PreflightBlockReason = preflightResult.Message
                };
            }
        }
        catch (Exception ex)
        {
            return result with
            {
                PreflightBlocked = true,
                PreflightBlockReason = $"Preflight error: {ex.Message}"
            };
        }

        // ── Security scanner — respects toggle ──────────────────
        if (context.FeatureToggles.EnableSecurityEngine)
        {
            try
            {
                var scanRequest = BuildScanRequest(context);
                var (report, error) = await _security.ScanAsync(scanRequest);
                result = result with
                {
                    SecurityReport = report,
                    SecurityError = error
                };
            }
            catch (Exception ex)
            {
                result = result with { SecurityError = ex.Message };
            }
        }
        else
        {
            result = result with { SkippedEngines = [.. result.SkippedEngines, "Security"] };
        }

        // ── Performance scanner — respects toggle ───────────────
        if (context.FeatureToggles.EnablePerformanceEngine)
        {
            try
            {
                var perfReport = await _performance.RunReviewAsync(
                    targetUrl,
                    context.ActiveProfile.Performance,
                    cancellationToken);
                result = result with { PerformanceReport = perfReport };
            }
            catch (Exception ex)
            {
                result = result with { PerformanceError = ex.Message };
            }
        }
        else
        {
            result = result with { SkippedEngines = [.. result.SkippedEngines, "Performance"] };
        }

        // ── Browser Runtime scanner — respects toggle ───────────────
        if (context.FeatureToggles.EnableBrowserRuntimeEngine && _runtime != null)
        {
            try
            {
                var runtimeReport = await _runtime.ReviewAsync(targetUrl, 30000, 5000, cancellationToken);
                result = result with { BrowserRuntimeReport = runtimeReport };
            }
            catch (Exception ex)
            {
                result = result with { BrowserRuntimeError = ex.Message };
            }
        }
        else if (context.FeatureToggles.EnableBrowserRuntimeEngine)
        {
            result = result with { SkippedEngines = [.. result.SkippedEngines, "BrowserRuntime"] };
        }
        else
        {
            result = result with { SkippedEngines = [.. result.SkippedEngines, "BrowserRuntime"] };
        }

        return result;
    }

    private static WasmScanRequest BuildScanRequest(FrontendAnalysisContext ctx)
    {
        var allowedHosts = ctx.AllowedBackendDomains
            .Concat(ctx.AllowedRestHosts)
            .Distinct()
            .ToList();

        var clientIds = new List<string>();
        if (!string.IsNullOrWhiteSpace(ctx.SecuritySettings.ExpectedClientId))
            clientIds.Add(ctx.SecuritySettings.ExpectedClientId);

        var knownSafe = ctx.AllowedGraphQlEndpoints
            .Concat(ctx.AllowedCdnHosts)
            .ToList();

        return new WasmScanRequest
        {
            TargetUrl = ctx.TargetUrl,
            EnvironmentName = ctx.ActiveProfile.EnvironmentType.ToString(),
            ExpectedApiGatewayBasePath = ctx.ActiveProfile.ExpectedApiGateway,
            AllowedBackendHostnames = allowedHosts,
            AllowedAuthority = ctx.SecuritySettings.ExpectedAuthority,
            AllowedClientIds = clientIds,
            KnownSafeDomains = knownSafe
        };
    }
}
