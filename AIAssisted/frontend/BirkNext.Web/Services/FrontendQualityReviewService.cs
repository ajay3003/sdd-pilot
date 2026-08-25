using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed class FrontendQualityReviewService : IFrontendQualityReviewService
{
    // ── Public API ────────────────────────────────────────────────────────────

    public FrontendQualityReviewReport BuildReport(
        string targetUrl,
        WasmSecurityReviewReport? securityReport,
        WasmPerformanceReviewReport? performanceReport)
    {
        var findings        = new List<FrontendQualityFinding>();
        var recommendations = new List<string>();
        var risks           = new List<string>();
        var limitations     = new List<string>();
        var assessedEngines = new List<string>();
        var failedEngines   = new List<string>();
        var skippedEngines  = new List<string>();

        bool isBlazorWasm = securityReport?.IsBlazorWasm ?? performanceReport?.IsBlazorWasm ?? false;

        // ── Security findings ─────────────────────────────────────────────────
        if (securityReport is not null)
        {
            foreach (var f in securityReport.Findings)
                findings.Add(MapSecurityFinding(f));
            assessedEngines.Add("Security");
        }
        else
        {
            limitations.Add("Security scan was not available — security findings cannot be assessed.");
            skippedEngines.Add("Security");
        }

        // ── Performance findings ──────────────────────────────────────────────
        if (performanceReport is not null)
        {
            foreach (var f in performanceReport.Findings)
                findings.Add(MapPerformanceFinding(f));
            assessedEngines.Add("Performance");
        }
        else
        {
            limitations.Add("Performance scan was not available — performance findings cannot be assessed.");
            skippedEngines.Add("Performance");
        }

        // ── Standards findings derived from security headers ──────────────────
        if (securityReport?.Headers is { Count: > 0 } headers)
        {
            findings.AddRange(DeriveStandardsFindings(headers));
        }
        else if (securityReport is null)
        {
            limitations.Add("HTTP security headers could not be assessed — no security scan data.");
        }

        // ── WASM-specific findings derived from asset/startup metrics ─────────
        if (performanceReport?.StartupMetrics is { } sm)
            findings.AddRange(DeriveWasmFindings(sm, performanceReport));

        // ── Accessibility findings (lightweight, static analysis) ─────────────
        limitations.Add("Accessibility is assessed only when the optional axe-core browser engine executes. Automated tooling cannot verify all WCAG requirements. Manual accessibility testing is still required.");

        // ── Readiness findings derived from performance readiness report ───────
        if (performanceReport?.ReadinessReport is { HasData: true } rdyReport)
        {
            findings.AddRange(DeriveReadinessFindings(rdyReport));
        }
        else
        {
            limitations.Add("QA readiness scoring unavailable — no performance readiness data.");
        }

        // ── Compute per-category scores ───────────────────────────────────────
        // Only compute scores for assessed categories.
        int? perfScore   = securityReport is not null || performanceReport is not null
                              ? ComputeScore(findings, FrontendQualityCategory.Performance,
                                  performanceReport?.ReadinessReport?.OverallScore)
                              : null;
        int? secScore    = securityReport is not null
                              ? securityReport.Health.Score
                              : null;
        int? accessScore = null; // Accessibility is not fully assessed without browser tools.
        int? stdScore    = securityReport?.Headers?.Count > 0
                              ? ComputeScore(findings, FrontendQualityCategory.Standards, null)
                              : null;
        int? wasmScore   = isBlazorWasm
                              ? ComputeScore(findings, FrontendQualityCategory.BlazorWasm, null)
                              : null;
        int? rdyScore    = performanceReport?.ReadinessReport?.HasData == true
                              ? ComputeScore(findings, FrontendQualityCategory.Readiness,
                                  performanceReport.ReadinessReport.OverallScore)
                              : null;

        // Only average assessed (non-null) scores.
        var assessedScores = new[] { perfScore, secScore, stdScore, wasmScore, rdyScore }
            .Where(s => s.HasValue)
            .Select(s => s.Value)
            .ToList();

        int? overallScore = assessedScores.Count > 0
            ? (int?)Math.Round(assessedScores.Average())
            : null;

        var categoryScores = new List<FrontendQualityCategoryScore>
        {
            CategoryScoreEntry(findings, FrontendQualityCategory.Performance,   perfScore,
                securityReport is not null || performanceReport is not null,
                perfScore.HasValue ? null : "Performance engine not available"),
            CategoryScoreEntry(findings, FrontendQualityCategory.Security,      secScore,
                securityReport is not null,
                secScore.HasValue ? null : "Security scan not available"),
            CategoryScoreEntry(findings, FrontendQualityCategory.Accessibility, accessScore,
                false,
                "Automated accessibility audit requires browser tools (axe-core, Lighthouse) — not enabled"),
            CategoryScoreEntry(findings, FrontendQualityCategory.Standards,     stdScore,
                securityReport?.Headers?.Count > 0,
                stdScore.HasValue ? null : "Security scan not available"),
            CategoryScoreEntry(findings, FrontendQualityCategory.BlazorWasm,    wasmScore,
                isBlazorWasm,
                wasmScore.HasValue ? null : "Target is not a Blazor WASM application"),
            CategoryScoreEntry(findings, FrontendQualityCategory.Readiness,     rdyScore,
                performanceReport?.ReadinessReport?.HasData == true,
                rdyScore.HasValue ? null : "Performance readiness data not available"),
        };

        // Determine overall assessment completeness.
        var completeness = securityReport is not null || performanceReport is not null
            ? (securityReport is not null && performanceReport is not null
                ? AssessmentCompleteness.Full
                : AssessmentCompleteness.Partial)
            : AssessmentCompleteness.Failed;

        // ── Recommendations ───────────────────────────────────────────────────
        if (securityReport?.Recommendations is { Count: > 0 } secRecs)
            recommendations.AddRange(secRecs.Take(5));

        if (performanceReport?.ReadinessReport?.TopRecommendations is { Count: > 0 } perfRecs)
            recommendations.AddRange(perfRecs.Take(3).Select(r => $"[Performance] {r.Title} — {r.Description}"));

        // Surface top risks (critical/high findings titles)
        risks.AddRange(findings
            .Where(f => f.Severity is FrontendQualitySeverity.Critical or FrontendQualitySeverity.High)
            .OrderBy(f => f.Severity)
            .Take(5)
            .Select(f => f.Title));

        return new FrontendQualityReviewReport
        {
            TargetUrl          = targetUrl,
            GeneratedAt        = DateTime.UtcNow,
            OverallScore       = overallScore,
            PerformanceScore   = perfScore,
            SecurityScore      = secScore,
            AccessibilityScore = accessScore,
            StandardsScore     = stdScore,
            WasmScore          = wasmScore,
            ReadinessScore     = rdyScore,
            Findings           = findings,
            CategoryScores     = categoryScores,
            Recommendations    = recommendations,
            Risks              = risks,
            Limitations        = limitations,
            IsBlazorWasm       = isBlazorWasm,
            Completeness       = completeness,
            AssessedEngines    = assessedEngines,
            FailedEngines      = failedEngines,
            SkippedEngines     = skippedEngines,
        };
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static FrontendQualityFinding MapSecurityFinding(WasmSecurityFinding f)
    {
        var cat = f.Category switch
        {
            WasmSecurityCategory.SecurityHeaders  => FrontendQualityCategory.Standards,
            WasmSecurityCategory.CorsConfiguration => FrontendQualityCategory.Standards,
            WasmSecurityCategory.BlazorSpecific    => FrontendQualityCategory.BlazorWasm,
            _                                      => FrontendQualityCategory.Security,
        };

        return new FrontendQualityFinding
        {
            Id             = $"sec-{f.Id}",
            Title          = f.Title,
            Severity       = MapSecuritySeverity(f.Severity),
            Category       = cat,
            Description    = f.Description,
            Recommendation = f.Recommendation,
            Evidence       = f.Evidence.Select(e => $"{e.Key}: {e.MaskedValue}").ToList(),
            SourceSystem   = "Security",
        };
    }

    private static FrontendQualityFinding MapPerformanceFinding(PerformanceFinding f)
    {
        var cat = f.Category switch
        {
            PerformanceCategory.BlazorRuntime => FrontendQualityCategory.BlazorWasm,
            PerformanceCategory.Compression   => FrontendQualityCategory.BlazorWasm,
            _                                 => FrontendQualityCategory.Performance,
        };

        return new FrontendQualityFinding
        {
            Id             = $"perf-{f.Id}",
            Title          = f.Title,
            Severity       = MapPerformanceSeverity(f.Severity),
            Category       = cat,
            Description    = f.Description,
            Recommendation = f.Recommendation,
            Evidence       = f.Evidence,
            SourceSystem   = "Performance",
        };
    }

    private static IEnumerable<FrontendQualityFinding> DeriveStandardsFindings(
        IReadOnlyList<SecurityHeaderResult> headers)
    {
        // Critical headers → derive additional standards findings where the security scan
        // may not have generated a finding with the right category.
        var csp  = headers.FirstOrDefault(h => h.Header.Equals("Content-Security-Policy", StringComparison.OrdinalIgnoreCase));
        var hsts = headers.FirstOrDefault(h => h.Header.Equals("Strict-Transport-Security", StringComparison.OrdinalIgnoreCase));

        if (csp is { Status: "Missing" })
        {
            yield return new FrontendQualityFinding
            {
                Id             = "std-csp-missing",
                Title          = "Content-Security-Policy header missing",
                Severity       = FrontendQualitySeverity.Critical,
                Category       = FrontendQualityCategory.Standards,
                Description    = "No CSP header was returned. Without CSP, the browser cannot enforce restrictions on script execution, blocking XSS attacks.",
                Recommendation = "Define a strict Content-Security-Policy. Start with 'default-src self' and expand as needed. Use nonces or hashes for inline scripts.",
                SourceSystem   = "Standards",
            };
        }

        if (hsts is { Status: "Missing" })
        {
            yield return new FrontendQualityFinding
            {
                Id             = "std-hsts-missing",
                Title          = "Strict-Transport-Security (HSTS) header missing",
                Severity       = FrontendQualitySeverity.High,
                Category       = FrontendQualityCategory.Standards,
                Description    = "HSTS is absent. Browsers cannot enforce HTTPS-only connections, leaving users vulnerable to SSL-stripping attacks.",
                Recommendation = "Add 'Strict-Transport-Security: max-age=31536000; includeSubDomains' to all HTTPS responses.",
                SourceSystem   = "Standards",
            };
        }

        // OWASP ASVS / Top 10 frontend indicators from header presence
        var privacyHeaders = new[]
        {
            ("Referrer-Policy",    FrontendQualitySeverity.Low,    "Controls how much referrer information is sent. Without it, full URLs including query strings may be leaked to third parties."),
            ("Permissions-Policy", FrontendQualitySeverity.Low,    "Restricts browser feature access (geolocation, camera, etc.). Absence may expose unnecessary browser APIs."),
        };

        foreach (var (name, sev, desc) in privacyHeaders)
        {
            var result = headers.FirstOrDefault(h => h.Header.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (result is { Status: "Missing" })
            {
                yield return new FrontendQualityFinding
                {
                    Id             = $"std-{name.ToLowerInvariant().Replace("-", "")}-missing",
                    Title          = $"{name} header missing",
                    Severity       = sev,
                    Category       = FrontendQualityCategory.Standards,
                    Description    = desc,
                    Recommendation = $"Add a '{name}' header to your server or CDN configuration.",
                    SourceSystem   = "Standards",
                };
            }
        }
    }

    private static IEnumerable<FrontendQualityFinding> DeriveWasmFindings(
        StartupMetrics sm,
        WasmPerformanceReviewReport perfReport)
    {
        const long LargeBundleBytes  = 10 * 1_048_576; // 10 MB
        const long HeavyBundleBytes  = 20 * 1_048_576; // 20 MB

        long total = sm.FrameworkDownloadBytes + sm.ApplicationDownloadBytes;

        if (total > HeavyBundleBytes)
        {
            yield return new FrontendQualityFinding
            {
                Id             = "wasm-bundle-heavy",
                Title          = $"Startup bundle is very large ({FormatBytes(total)})",
                Severity       = FrontendQualitySeverity.High,
                Category       = FrontendQualityCategory.BlazorWasm,
                Description    = $"The total compressed startup download is {FormatBytes(total)}. This significantly increases Time-to-Interactive on low-bandwidth connections.",
                Recommendation = "Enable IL Trimming, publish-time compression, and consider lazy-loading feature assemblies to reduce the critical path bundle.",
                Evidence       = [$"Framework: {FormatBytes(sm.FrameworkDownloadBytes)}", $"Application: {FormatBytes(sm.ApplicationDownloadBytes)}", $"Startup requests: {sm.StartupRequestCount}"],
                SourceSystem   = "BlazorWasm",
            };
        }
        else if (total > LargeBundleBytes)
        {
            yield return new FrontendQualityFinding
            {
                Id             = "wasm-bundle-large",
                Title          = $"Startup bundle exceeds 10 MB ({FormatBytes(total)})",
                Severity       = FrontendQualitySeverity.Medium,
                Category       = FrontendQualityCategory.BlazorWasm,
                Description    = $"The startup bundle is {FormatBytes(total)} compressed. Consider reducing size to improve first-load performance.",
                Recommendation = "Review assembly references, enable IL Trimming, and lazy-load non-critical assemblies.",
                Evidence       = [$"Framework: {FormatBytes(sm.FrameworkDownloadBytes)}", $"Application: {FormatBytes(sm.ApplicationDownloadBytes)}"],
                SourceSystem   = "BlazorWasm",
            };
        }

        // PWA / Service Worker indicator
        bool hasSw = perfReport.Assets.Any(a =>
            a.Url.Contains("service-worker", StringComparison.OrdinalIgnoreCase) ||
            a.Url.EndsWith("sw.js", StringComparison.OrdinalIgnoreCase));

        if (!hasSw)
        {
            yield return new FrontendQualityFinding
            {
                Id             = "wasm-no-service-worker",
                Title          = "No service worker / PWA support detected",
                Severity       = FrontendQualitySeverity.Info,
                Category       = FrontendQualityCategory.BlazorWasm,
                Description    = "No service worker was found among the startup assets. Service workers enable offline support, caching, and PWA capabilities.",
                Recommendation = "Consider adding a service worker for offline resilience, especially for production deployments. Blazor WASM supports PWA configuration out of the box.",
                SourceSystem   = "BlazorWasm",
            };
        }

        // Lazy loading hint from assembly count
        if (sm.ApplicationAssemblyCount > 15)
        {
            yield return new FrontendQualityFinding
            {
                Id             = "wasm-lazy-loading-hint",
                Title          = $"Large assembly count ({sm.ApplicationAssemblyCount}) — lazy loading may help",
                Severity       = FrontendQualitySeverity.Info,
                Category       = FrontendQualityCategory.BlazorWasm,
                Description    = $"The application loads {sm.ApplicationAssemblyCount} assemblies at startup. Lazy-loading non-critical assemblies can reduce initial load time.",
                Recommendation = "Review your Blazor routing configuration and apply @attribute [StreamRendering] or lazy loading for feature assemblies not needed on first render.",
                SourceSystem   = "BlazorWasm",
            };
        }
    }

    private static IEnumerable<FrontendQualityFinding> DeriveAccessibilityFindings(
        WasmSecurityReviewReport? securityReport,
        bool isBlazorWasm)
    {
        // Static analysis of accessibility for a Blazor WASM app is inherently limited
        // because the meaningful HTML is generated at runtime in the browser.

        if (isBlazorWasm)
        {
            yield return new FrontendQualityFinding
            {
                Id             = "a11y-wasm-dynamic-rendering",
                Title          = "Accessibility requires browser-side verification for Blazor WASM",
                Severity       = FrontendQualitySeverity.Info,
                Category       = FrontendQualityCategory.Accessibility,
                Description    = "Blazor WebAssembly renders HTML dynamically in the browser. Static HTTP analysis can only assess the initial HTML shell; semantic markup, ARIA attributes, and focus management require browser-based tools (Axe, Lighthouse) for full WCAG 2.2 coverage.",
                Recommendation = "Run Lighthouse Accessibility audit and Axe browser extension against the live application. Validate: heading hierarchy, label associations, focus order, colour contrast, and keyboard navigation.",
                SourceSystem   = "Accessibility",
            };
        }

        // Check viewport meta from configuration entries if the security scan ran
        if (securityReport is not null)
        {
            bool viewportFound = securityReport.ConfigurationSummary
                .Any(e => e.Key.Contains("viewport", StringComparison.OrdinalIgnoreCase));

            if (!viewportFound && securityReport.Assets.Count > 0)
            {
                yield return new FrontendQualityFinding
                {
                    Id             = "a11y-viewport-not-confirmed",
                    Title          = "Viewport meta tag presence not confirmed",
                    Severity       = FrontendQualitySeverity.Info,
                    Category       = FrontendQualityCategory.Accessibility,
                    Description    = "A responsive viewport meta tag (<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">) was not detected in the configuration surface. This tag is required for correct mobile rendering and WCAG 2.1 Reflow compliance.",
                    Recommendation = "Verify that index.html includes a viewport meta tag. Ensure user-scalable is not set to no, which would violate WCAG 1.4.4.",
                    SourceSystem   = "Accessibility",
                };
            }
        }

        yield return new FrontendQualityFinding
        {
            Id             = "a11y-wcag22-manual",
            Title          = "WCAG 2.2 compliance requires manual and automated verification",
            Severity       = FrontendQualitySeverity.Info,
            Category       = FrontendQualityCategory.Accessibility,
            Description    = "Automated static analysis covers only a subset of WCAG 2.2 success criteria. Manual review is required for: keyboard accessibility, focus management, screen reader compatibility, sufficient colour contrast, and timing adjustments.",
            Recommendation = "Integrate axe-core into your test suite for automated checks. Conduct periodic manual reviews with NVDA/JAWS and keyboard-only navigation. Target WCAG 2.2 Level AA as a minimum.",
            SourceSystem   = "Accessibility",
        };
    }

    private static IEnumerable<FrontendQualityFinding> DeriveReadinessFindings(
        PerformanceReadinessReport rdyReport)
    {
        foreach (var cat in rdyReport.Categories.Where(c => c.WasAssessed))
        {
            var severity = cat.State switch
            {
                PerformanceReadinessState.HighRisk         => FrontendQualitySeverity.High,
                PerformanceReadinessState.NeedsImprovement => FrontendQualitySeverity.Medium,
                PerformanceReadinessState.MostlyReady      => FrontendQualitySeverity.Low,
                _                                          => FrontendQualitySeverity.Info,
            };

            if (cat.State is PerformanceReadinessState.Ready) continue;

            yield return new FrontendQualityFinding
            {
                Id             = $"rdy-{cat.Category.ToString().ToLowerInvariant()}",
                Title          = $"{cat.CategoryName}: {ReadinessStateLabel(cat.State)}",
                Severity       = severity,
                Category       = FrontendQualityCategory.Readiness,
                Description    = $"{cat.CategoryName} readiness is {ReadinessStateLabel(cat.State)} (score {cat.Score}/100, {cat.FindingsCount} finding{(cat.FindingsCount != 1 ? "s" : "")}).",
                Recommendation = $"Review {cat.CategoryName} findings and apply recommendations to improve the readiness score above 80.",
                SourceSystem   = "Readiness",
            };
        }

        foreach (var risk in rdyReport.TopRisks.Take(3))
        {
            yield return new FrontendQualityFinding
            {
                Id             = $"rdy-risk-{risk.Id}",
                Title          = risk.Title,
                Severity       = MapPerformanceSeverity(risk.Severity),
                Category       = FrontendQualityCategory.Readiness,
                Description    = risk.Description,
                Recommendation = risk.Recommendation,
                Evidence       = risk.Evidence,
                SourceSystem   = "Readiness",
            };
        }
    }

    // ── Score helpers ─────────────────────────────────────────────────────────

    private static int ComputeScore(
        IEnumerable<FrontendQualityFinding> findings,
        FrontendQualityCategory category,
        int? baselineScore)
    {
        var catFindings = findings.Where(f => f.Category == category).ToList();

        int penalty = catFindings.Sum(f => f.Severity switch
        {
            FrontendQualitySeverity.Critical => 25,
            FrontendQualitySeverity.High     => 15,
            FrontendQualitySeverity.Medium   => 8,
            FrontendQualitySeverity.Low      => 3,
            _                                => 0,
        });

        int computed = Math.Max(0, 100 - penalty);

        return baselineScore.HasValue
            ? (int)Math.Round((baselineScore.Value + computed) / 2.0)
            : computed;
    }

    private static FrontendQualityCategoryScore CategoryScoreEntry(
        IEnumerable<FrontendQualityFinding> findings,
        FrontendQualityCategory category,
        int? score,
        bool assessed,
        string? notAssessedReason)
    {
        var catFindings = findings.Where(f => f.Category == category).ToList();
        return new FrontendQualityCategoryScore
        {
            Category     = category,
            Score        = score,
            FindingCount = catFindings.Count,
            Critical     = catFindings.Count(f => f.Severity == FrontendQualitySeverity.Critical),
            High         = catFindings.Count(f => f.Severity == FrontendQualitySeverity.High),
            Assessed     = assessed,
            NotAssessedReason = !assessed ? notAssessedReason : null,
        };
    }

    // ── Severity converters ───────────────────────────────────────────────────

    private static FrontendQualitySeverity MapSecuritySeverity(WasmSecuritySeverity s) => s switch
    {
        WasmSecuritySeverity.Critical => FrontendQualitySeverity.Critical,
        WasmSecuritySeverity.High     => FrontendQualitySeverity.High,
        WasmSecuritySeverity.Medium   => FrontendQualitySeverity.Medium,
        WasmSecuritySeverity.Low      => FrontendQualitySeverity.Low,
        _                             => FrontendQualitySeverity.Info,
    };

    private static FrontendQualitySeverity MapPerformanceSeverity(PerformanceSeverity s) => s switch
    {
        PerformanceSeverity.Critical => FrontendQualitySeverity.Critical,
        PerformanceSeverity.High     => FrontendQualitySeverity.High,
        PerformanceSeverity.Medium   => FrontendQualitySeverity.Medium,
        PerformanceSeverity.Low      => FrontendQualitySeverity.Low,
        _                            => FrontendQualitySeverity.Info,
    };

    // ── Misc helpers ──────────────────────────────────────────────────────────

    private static string ReadinessStateLabel(PerformanceReadinessState state) => state switch
    {
        PerformanceReadinessState.Ready            => "Ready",
        PerformanceReadinessState.MostlyReady      => "Mostly Ready",
        PerformanceReadinessState.NeedsImprovement => "Needs Improvement",
        PerformanceReadinessState.HighRisk         => "High Risk",
        _                                          => "Not Assessed",
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1_024)     return $"{bytes} B";
        if (bytes < 1_048_576) return $"{bytes / 1024} KB";
        return $"{bytes / 1_048_576.0:F1} MB";
    }
}
