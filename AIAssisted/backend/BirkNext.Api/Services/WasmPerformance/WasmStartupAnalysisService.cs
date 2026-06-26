namespace BirkNext.Api.Services.WasmPerformance;

public sealed class WasmStartupAnalysisService : IWasmStartupAnalysisService
{
    private static readonly StartupAnalysisThresholds Defaults = new();

    public StartupAnalysisResult Analyze(
        IReadOnlyList<DiscoveredAsset> assets,
        StartupAnalysisThresholds? thresholds = null)
    {
        var t       = thresholds ?? Defaults;
        var metrics = CalculateMetrics(assets);
        var findings = DetectFindings(metrics, assets, t);
        var recs     = GenerateRecommendations(findings);
        var display  = BuildDisplayMetrics(metrics, t);

        return new StartupAnalysisResult
        {
            StartupMetrics  = metrics,
            Findings        = findings,
            Recommendations = recs,
            DisplayMetrics  = display
        };
    }

    // ── Pure static methods — unit-testable ───────────────────────────────────

    internal static StartupMetrics CalculateMetrics(IReadOnlyList<DiscoveredAsset> assets)
    {
        var ok = assets.Where(a => a.StatusCode is >= 200 and < 300).ToList();
        if (ok.Count == 0) return new StartupMetrics();

        var frameworkTypes = new HashSet<AssetType>
        {
            AssetType.FrameworkJs, AssetType.BootManifest,
            AssetType.FrameworkDll, AssetType.WasmRuntime, AssetType.Other
        };
        var appTypes = new HashSet<AssetType>
        {
            AssetType.ApplicationDll, AssetType.SatelliteAssembly
        };

        var largest = ok.OrderByDescending(AssetSize).First();

        return new StartupMetrics
        {
            StartupDownloadBytes     = ok.Sum(AssetSize),
            FrameworkDownloadBytes   = ok.Where(a => frameworkTypes.Contains(a.Type)).Sum(AssetSize),
            ApplicationDownloadBytes = ok.Where(a => appTypes.Contains(a.Type)).Sum(AssetSize),
            StartupRequestCount      = ok.Count,
            FrameworkAssemblyCount   = ok.Count(a => a.Type == AssetType.FrameworkDll),
            ApplicationAssemblyCount = ok.Count(a => a.Type == AssetType.ApplicationDll),
            SatelliteAssemblyCount   = ok.Count(a => a.Type == AssetType.SatelliteAssembly),
            JavaScriptCount          = ok.Count(a => a.Type is AssetType.JavaScript or AssetType.FrameworkJs),
            CssCount                 = ok.Count(a => a.Type == AssetType.Css),
            FontCount                = ok.Count(a => a.Type == AssetType.Font),
            ImageCount               = ok.Count(a => a.Type == AssetType.Image),
            LargestAssetUrl          = largest.Url,
            LargestAssetBytes        = AssetSize(largest),
            LargestAssetType         = largest.Type
        };
    }

    internal static IReadOnlyList<PerformanceFinding> DetectFindings(
        StartupMetrics metrics,
        IReadOnlyList<DiscoveredAsset> assets,
        StartupAnalysisThresholds t)
    {
        var findings = new List<PerformanceFinding>();

        var maxStartupBytes    = MbToBytes(t.MaxStartupDownloadMB);
        var maxFrameworkBytes  = MbToBytes(t.MaxFrameworkMB);
        var maxAppBytes        = MbToBytes(t.MaxApplicationMB);
        var maxIndividualBytes = MbToBytes(t.MaxIndividualAssetMB);
        var maxSatBytes        = MbToBytes(t.MaxSatelliteResourcesMB);
        var maxUserJsBytes     = KbToBytes(t.MaxUserJavaScriptKB);
        var maxCssBytes        = KbToBytes(t.MaxCssKB);

        // STA-001: Large startup payload
        if (metrics.StartupDownloadBytes > maxStartupBytes)
        {
            var severity = metrics.StartupDownloadBytes > (long)(maxStartupBytes * 1.5)
                ? PerformanceSeverity.High
                : PerformanceSeverity.Medium;

            findings.Add(new PerformanceFinding
            {
                Id          = "STA-001",
                Title       = "Large startup download payload",
                Severity    = severity,
                Category    = PerformanceCategory.Startup,
                Description = $"Total startup download is {FormatBytes(metrics.StartupDownloadBytes)}, exceeding the " +
                              $"{t.MaxStartupDownloadMB} MB threshold. Large payloads delay time-to-interactive, " +
                              "especially on mobile and slow connections.",
                Recommendation = "Enable Brotli compression on the hosting server and publish with IL trimming " +
                                 "(PublishTrimmed=true) to shrink the payload.",
                Evidence =
                [
                    $"Startup download: {FormatBytes(metrics.StartupDownloadBytes)}",
                    $"Threshold: {t.MaxStartupDownloadMB} MB",
                    $"Startup requests: {metrics.StartupRequestCount}"
                ]
            });
        }

        // STA-002: Large framework footprint
        if (metrics.FrameworkDownloadBytes > maxFrameworkBytes)
        {
            findings.Add(new PerformanceFinding
            {
                Id          = "STA-002",
                Title       = "Large .NET framework download",
                Severity    = PerformanceSeverity.Medium,
                Category    = PerformanceCategory.Startup,
                Description = $"The .NET runtime and framework assemblies account for {FormatBytes(metrics.FrameworkDownloadBytes)}, " +
                              $"exceeding the {t.MaxFrameworkMB} MB threshold. Framework size is determined by IL trimming configuration.",
                Recommendation = "Enable PublishTrimmed=true and TrimMode=Full in the project file. " +
                                 "Use the trim analyzer to annotate code that blocks safe trimming.",
                Evidence =
                [
                    $"Framework download: {FormatBytes(metrics.FrameworkDownloadBytes)}",
                    $"Framework assemblies: {metrics.FrameworkAssemblyCount}"
                ]
            });
        }

        // STA-003: Large application assemblies
        if (metrics.ApplicationDownloadBytes > maxAppBytes)
        {
            findings.Add(new PerformanceFinding
            {
                Id          = "STA-003",
                Title       = "Large application assembly download",
                Severity    = PerformanceSeverity.Medium,
                Category    = PerformanceCategory.Startup,
                Description = $"Application assemblies account for {FormatBytes(metrics.ApplicationDownloadBytes)}, " +
                              $"exceeding the {t.MaxApplicationMB} MB threshold. Consider moving feature-specific assemblies to lazy loading.",
                Recommendation = "Use Blazor lazy loading (LazyAssemblyLoader) to defer assemblies only needed by " +
                                 "specific routes. Enable assembly trimming to remove unused code paths.",
                Evidence =
                [
                    $"Application download: {FormatBytes(metrics.ApplicationDownloadBytes)}",
                    $"Application assemblies: {metrics.ApplicationAssemblyCount}"
                ]
            });
        }

        // STA-004: Large satellite/localization resources
        var satelliteBytes = assets
            .Where(a => a.Type == AssetType.SatelliteAssembly && a.StatusCode is >= 200 and < 300)
            .Sum(AssetSize);

        if (metrics.SatelliteAssemblyCount > 0 && satelliteBytes > maxSatBytes)
        {
            findings.Add(new PerformanceFinding
            {
                Id          = "STA-004",
                Title       = "Large localization resource footprint",
                Severity    = PerformanceSeverity.Low,
                Category    = PerformanceCategory.Startup,
                Description = $"{metrics.SatelliteAssemblyCount} satellite assemblies add {FormatBytes(satelliteBytes)} " +
                              "to the startup payload. If the application only targets a single language, " +
                              "unused localization data can be eliminated.",
                Recommendation = "Set <InvariantGlobalization>true</InvariantGlobalization> if the application does not " +
                                 "require locale-specific formatting. Alternatively, use sharded ICU data to load only the required culture.",
                Evidence =
                [
                    $"Satellite assemblies: {metrics.SatelliteAssemblyCount}",
                    $"Satellite download: {FormatBytes(satelliteBytes)}"
                ]
            });
        }

        // STA-005: Large user JavaScript payload (excludes framework JS)
        var userJsBytes = assets
            .Where(a => a.Type == AssetType.JavaScript && a.StatusCode is >= 200 and < 300)
            .Sum(AssetSize);

        if (userJsBytes > maxUserJsBytes)
        {
            var severity = userJsBytes > maxUserJsBytes * 2
                ? PerformanceSeverity.Medium
                : PerformanceSeverity.Low;

            findings.Add(new PerformanceFinding
            {
                Id          = "STA-005",
                Title       = "Large application JavaScript payload",
                Severity    = severity,
                Category    = PerformanceCategory.Startup,
                Description = $"Application JavaScript files total {FormatBytes(userJsBytes)}, exceeding the " +
                              $"{t.MaxUserJavaScriptKB} KB threshold. Large JS payloads block parsing and extend startup time.",
                Recommendation = "Audit JavaScript dependencies for unused packages. Use tree shaking and minification. " +
                                 "Defer non-critical scripts with the 'defer' attribute.",
                Evidence =
                [
                    $"User JavaScript: {FormatBytes(userJsBytes)}",
                    $"JS file count: {metrics.JavaScriptCount - assets.Count(a => a.Type == AssetType.FrameworkJs && a.StatusCode is >= 200 and < 300)}"
                ]
            });
        }

        // STA-006: Large CSS payload
        var cssBytes = assets
            .Where(a => a.Type == AssetType.Css && a.StatusCode is >= 200 and < 300)
            .Sum(AssetSize);

        if (cssBytes > maxCssBytes)
        {
            findings.Add(new PerformanceFinding
            {
                Id          = "STA-006",
                Title       = "Large CSS payload",
                Severity    = PerformanceSeverity.Low,
                Category    = PerformanceCategory.Startup,
                Description = $"CSS files total {FormatBytes(cssBytes)}, exceeding the {t.MaxCssKB} KB threshold. " +
                              "Unused CSS blocks rendering and increases startup parse time.",
                Recommendation = "Use PurgeCSS or similar tooling to remove unused CSS rules. " +
                                 "Consider deferring non-critical stylesheets.",
                Evidence =
                [
                    $"CSS download: {FormatBytes(cssBytes)}",
                    $"CSS files: {metrics.CssCount}"
                ]
            });
        }

        // STA-007: Too many startup requests
        if (metrics.StartupRequestCount > t.MaxStartupRequests)
        {
            findings.Add(new PerformanceFinding
            {
                Id          = "STA-007",
                Title       = "High number of startup HTTP requests",
                Severity    = PerformanceSeverity.Medium,
                Category    = PerformanceCategory.Startup,
                Description = $"Startup requires {metrics.StartupRequestCount} HTTP requests, exceeding the " +
                              $"{t.MaxStartupRequests} request threshold. Even with HTTP/2 multiplexing, a high request " +
                              "count increases connection overhead and extends time-to-interactive.",
                Recommendation = "Reduce startup request count through IL trimming (fewer assemblies) and lazy loading " +
                                 "(defer optional assemblies). Enable the Blazor PWA service worker for cache-first loading on return visits.",
                Evidence =
                [
                    $"Startup requests: {metrics.StartupRequestCount}",
                    $"Threshold: {t.MaxStartupRequests}"
                ]
            });
        }

        // STA-008: Large individual assets
        var largeAssets = assets
            .Where(a => a.StatusCode is >= 200 and < 300 && AssetSize(a) > maxIndividualBytes)
            .OrderByDescending(AssetSize)
            .Take(5)
            .ToList();

        if (largeAssets.Count > 0)
        {
            var worstSize  = AssetSize(largeAssets[0]);
            var assetWord  = largeAssets.Count > 1 ? "assets" : "asset";
            var exceedWord = largeAssets.Count > 1 ? "exceed" : "exceeds";
            var severity   = worstSize > maxIndividualBytes * 2
                ? PerformanceSeverity.High
                : PerformanceSeverity.Medium;

            findings.Add(new PerformanceFinding
            {
                Id          = "STA-008",
                Title       = $"Large individual {assetWord} detected",
                Severity    = severity,
                Category    = PerformanceCategory.Startup,
                Description = $"{largeAssets.Count} {assetWord} {exceedWord} the {t.MaxIndividualAssetMB} MB per-asset threshold. " +
                              "Without compression, these files will significantly delay startup.",
                Recommendation = "Ensure Brotli or Gzip compression is enabled on the server for all WASM and DLL files. " +
                                 "Consider splitting large application assemblies and moving non-critical code to lazy-loaded assemblies.",
                Evidence = largeAssets
                    .Select(a => $"{ShortName(a.Url)} — {FormatBytes(AssetSize(a))} ({a.Type})")
                    .ToList()
            });
        }

        return findings;
    }

    internal static IReadOnlyList<PerformanceRecommendation> GenerateRecommendations(
        IReadOnlyList<PerformanceFinding> findings)
    {
        var ids  = findings.Select(f => f.Id).ToHashSet();
        var recs = new List<PerformanceRecommendation>();
        int p    = 1;

        if (ids.Contains("STA-001"))
        {
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p++,
                Title       = "Enable Brotli compression and IL trimming",
                Description = "Configure the hosting server to compress WASM, DLL, and JSON files with Brotli. " +
                              "Enable <PublishTrimmed>true</PublishTrimmed> in the project file to remove unused framework code from the published output.",
                Category    = PerformanceCategory.Startup
            });
        }

        if (ids.Contains("STA-003"))
        {
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p++,
                Title       = "Move non-startup assemblies to lazy loading",
                Description = "Use Blazor lazy loading (LazyAssemblyLoader) to defer assemblies required only by specific routes. " +
                              "Only load assemblies needed for the initial view at startup.",
                Category    = PerformanceCategory.Startup
            });
        }

        if (ids.Contains("STA-002") && !ids.Contains("STA-001"))
        {
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p++,
                Title       = "Enable aggressive IL trimming",
                Description = "Add <PublishTrimmed>true</PublishTrimmed> and <TrimMode>Full</TrimMode> to the project file. " +
                              "Use the trim analyzer to identify and annotate code that prevents safe trimming.",
                Category    = PerformanceCategory.Startup
            });
        }

        if (ids.Contains("STA-007"))
        {
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p++,
                Title       = "Add a PWA service worker for cache-first loading",
                Description = "Configure the Blazor PWA service worker to cache all startup assets after the first visit. " +
                              "Returning users will load the application from the cache without network round-trips.",
                Category    = PerformanceCategory.Startup
            });
        }

        if (ids.Contains("STA-005") || ids.Contains("STA-006"))
        {
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p++,
                Title       = "Audit and reduce startup web assets",
                Description = "Use PurgeCSS to remove unused CSS rules. Audit JavaScript bundles for unused dependencies. " +
                              "Defer loading of non-critical scripts with the defer attribute.",
                Category    = PerformanceCategory.Assets
            });
        }

        if (ids.Contains("STA-004"))
        {
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p++,
                Title       = "Reduce localization resource footprint",
                Description = "Set <InvariantGlobalization>true</InvariantGlobalization> if the application targets only a single locale. " +
                              "For multi-locale apps, use sharded ICU data to load only the required culture files.",
                Category    = PerformanceCategory.Startup
            });
        }

        if (ids.Contains("STA-008") && !ids.Contains("STA-001"))
        {
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p,
                Title       = "Verify compression for large assets",
                Description = "Ensure the web server serves Brotli pre-compressed versions of large WASM and DLL files. " +
                              "Consider splitting large application assemblies into smaller libraries for granular lazy loading.",
                Category    = PerformanceCategory.Startup
            });
        }

        return recs;
    }

    internal static IReadOnlyList<PerformanceMetric> BuildDisplayMetrics(
        StartupMetrics m, StartupAnalysisThresholds t)
    {
        var maxStartup    = MbToBytes(t.MaxStartupDownloadMB);
        var maxFramework  = MbToBytes(t.MaxFrameworkMB);
        var maxApp        = MbToBytes(t.MaxApplicationMB);
        var maxIndividual = MbToBytes(t.MaxIndividualAssetMB);

        string SizeStatus(long value, long max) =>
            value > max           ? "poor"    :
            value > (long)(max * 0.7) ? "warning" : "good";

        string CountStatus(int value, int max) =>
            value > max           ? "poor"    :
            value > (int)(max * 0.7) ? "warning" : "good";

        return
        [
            new PerformanceMetric
            {
                Name      = "Total startup download",
                Value     = FormatBytes(m.StartupDownloadBytes),
                Threshold = $"< {t.MaxStartupDownloadMB} MB",
                Status    = SizeStatus(m.StartupDownloadBytes, maxStartup)
            },
            new PerformanceMetric
            {
                Name      = "Framework size",
                Value     = FormatBytes(m.FrameworkDownloadBytes),
                Threshold = $"< {t.MaxFrameworkMB} MB",
                Status    = SizeStatus(m.FrameworkDownloadBytes, maxFramework)
            },
            new PerformanceMetric
            {
                Name      = "Application assemblies",
                Value     = FormatBytes(m.ApplicationDownloadBytes),
                Threshold = $"< {t.MaxApplicationMB} MB",
                Status    = SizeStatus(m.ApplicationDownloadBytes, maxApp)
            },
            new PerformanceMetric
            {
                Name      = "Startup requests",
                Value     = m.StartupRequestCount.ToString(),
                Unit      = "requests",
                Threshold = $"< {t.MaxStartupRequests}",
                Status    = CountStatus(m.StartupRequestCount, t.MaxStartupRequests)
            },
            new PerformanceMetric
            {
                Name      = "Largest asset",
                Value     = FormatBytes(m.LargestAssetBytes),
                Threshold = $"< {t.MaxIndividualAssetMB} MB",
                Status    = SizeStatus(m.LargestAssetBytes, maxIndividual)
            },
            new PerformanceMetric
            {
                Name   = "Framework assemblies",
                Value  = m.FrameworkAssemblyCount.ToString(),
                Status = "good"
            },
            new PerformanceMetric
            {
                Name   = "App assemblies",
                Value  = m.ApplicationAssemblyCount.ToString(),
                Status = "good"
            }
        ];
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static long AssetSize(DiscoveredAsset a) => a.ContentLength ?? a.DownloadedBytes;

    private static long MbToBytes(double mb) => (long)(mb * 1024 * 1024);
    private static long KbToBytes(double kb) => (long)(kb * 1024);

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)        return "0 B";
        if (bytes < 1_024)     return $"{bytes} B";
        if (bytes < 1_048_576) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / 1_048_576.0:F2} MB";
    }

    private static string ShortName(string url)
    {
        var idx = url.LastIndexOf('/');
        var name = idx >= 0 ? url[(idx + 1)..] : url;
        return name.Length > 60 ? "…" + name[^57..] : name;
    }
}
