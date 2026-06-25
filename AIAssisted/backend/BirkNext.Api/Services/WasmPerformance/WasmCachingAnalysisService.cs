using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.WasmPerformance;

public sealed class WasmCachingAnalysisService : IWasmCachingAnalysisService
{
    private static readonly CachingAnalysisThresholds Defaults = new();

    // ── Public interface ──────────────────────────────────────────────────────

    public CachingAnalysisResult Analyze(
        IReadOnlyList<DiscoveredAsset> assets,
        CachingAnalysisThresholds? thresholds = null)
    {
        var t         = thresholds ?? Defaults;
        var summaries = AnalyzeAssets(assets, t);
        var metrics   = CalculateMetrics(summaries, t);
        var findings  = DetectFindings(metrics, summaries, t);
        var recs      = GenerateRecommendations(findings);

        return new CachingAnalysisResult
        {
            Metrics         = metrics,
            AssetSummaries  = summaries.ToList(),
            Findings        = findings.ToList(),
            Recommendations = recs.ToList()
        };
    }

    // ── Pure static methods — unit-testable ───────────────────────────────────

    internal static CompressionStatus ClassifyCompression(string? contentEncoding)
    {
        if (string.IsNullOrWhiteSpace(contentEncoding)) return CompressionStatus.NotCompressed;

        // Split multi-value headers (e.g. "gzip, br") and check all parts
        var parts = contentEncoding.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Brotli takes priority over GZip when both appear in a multi-value header
        bool hasBrotli = parts.Any(p => string.Equals(p, "br",       StringComparison.OrdinalIgnoreCase));
        bool hasGzip   = parts.Any(p => string.Equals(p, "gzip",    StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(p, "deflate",  StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(p, "x-gzip",   StringComparison.OrdinalIgnoreCase));

        if (hasBrotli) return CompressionStatus.Brotli;
        if (hasGzip)   return CompressionStatus.Gzip;
        return parts.Length > 0 ? CompressionStatus.Other : CompressionStatus.NotCompressed;
    }

    internal static CacheStatus ClassifyCacheStatus(
        string? cacheControl, bool hasETag, bool hasLastModified)
    {
        if (string.IsNullOrWhiteSpace(cacheControl))
            return (hasETag || hasLastModified) ? CacheStatus.WeaklyCached : CacheStatus.NotCached;

        var cc = cacheControl.ToLowerInvariant();

        // no-store is absolute — never cache
        if (cc.Contains("no-store")) return CacheStatus.NotCached;

        // immutable directive — content-hashed, optimal
        if (cc.Contains("immutable")) return CacheStatus.ProperlyOptimized;

        // Extract max-age
        var maxAge = ExtractMaxAge(cacheControl);
        if (maxAge.HasValue)
        {
            if (maxAge.Value == 0) return CacheStatus.NotCached;
            if (maxAge.Value >= 86_400) return CacheStatus.ProperlyOptimized; // >= 1 day
            return CacheStatus.WeaklyCached;
        }

        // no-cache means must revalidate — allows serving from cache with 304
        if (cc.Contains("no-cache")) return CacheStatus.WeaklyCached;

        // public/private without explicit max-age — rely on ETag/Last-Modified
        return (hasETag || hasLastModified) ? CacheStatus.WeaklyCached : CacheStatus.NotCached;
    }

    internal static bool IsFrameworkAsset(AssetType type) =>
        type is AssetType.FrameworkDll   or AssetType.WasmRuntime    or
                AssetType.FrameworkJs    or AssetType.ApplicationDll or
                AssetType.SatelliteAssembly;

    internal static IReadOnlyList<AssetCachingSummary> AnalyzeAssets(
        IReadOnlyList<DiscoveredAsset> assets, CachingAnalysisThresholds t)
    {
        var results = new List<AssetCachingSummary>(assets.Count);

        foreach (var a in assets)
        {
            if (a.StatusCode is < 200 or >= 300) continue; // skip errored assets

            var compression = ClassifyCompression(a.ContentEncoding);
            var cacheStatus = ClassifyCacheStatus(
                a.CacheControl,
                !string.IsNullOrEmpty(a.ETag),
                !string.IsNullOrEmpty(a.LastModified));
            var sizeBytes   = a.ContentLength ?? a.DownloadedBytes;
            var rec         = GetAssetRecommendation(a.Type, compression, cacheStatus, sizeBytes, t);

            results.Add(new AssetCachingSummary
            {
                Url               = a.Url,
                Type              = a.Type,
                ContentEncoding   = a.ContentEncoding,
                CacheControl      = a.CacheControl,
                HasETag           = !string.IsNullOrEmpty(a.ETag),
                HasLastModified   = !string.IsNullOrEmpty(a.LastModified),
                SizeBytes         = sizeBytes,
                CacheStatus       = cacheStatus,
                CompressionStatus = compression,
                Recommendation    = rec
            });
        }

        return results;
    }

    internal static CachingMetrics CalculateMetrics(
        IReadOnlyList<AssetCachingSummary> summaries, CachingAnalysisThresholds t)
    {
        var largeThresholdBytes = (long)(t.LargeUncompressedKB * 1024);
        return new CachingMetrics
        {
            TotalAssets               = summaries.Count,
            CompressedAssets          = summaries.Count(s => s.CompressionStatus != CompressionStatus.NotCompressed),
            BrotliAssets              = summaries.Count(s => s.CompressionStatus == CompressionStatus.Brotli),
            GzipAssets                = summaries.Count(s => s.CompressionStatus == CompressionStatus.Gzip),
            CacheOptimizedAssets      = summaries.Count(s => s.CacheStatus == CacheStatus.ProperlyOptimized),
            AssetsWithoutCacheHeaders = summaries.Count(s => s.CacheStatus == CacheStatus.NotCached),
            UncompressedLargeAssets   = summaries.Count(s =>
                s.CompressionStatus == CompressionStatus.NotCompressed &&
                s.SizeBytes > largeThresholdBytes)
        };
    }

    internal static IReadOnlyList<PerformanceFinding> DetectFindings(
        CachingMetrics metrics, IReadOnlyList<AssetCachingSummary> summaries, CachingAnalysisThresholds t)
    {
        var findings = new List<PerformanceFinding>();

        var frameworkSummaries = summaries.Where(s => IsFrameworkAsset(s.Type)).ToList();
        var fwBrotli           = frameworkSummaries.Count(s => s.CompressionStatus == CompressionStatus.Brotli);
        var fwGzip             = frameworkSummaries.Count(s => s.CompressionStatus == CompressionStatus.Gzip);

        if (frameworkSummaries.Count > 0)
        {
            // CCH-001: No compression at all on framework assets
            if (fwBrotli == 0 && fwGzip == 0)
            {
                findings.Add(new PerformanceFinding
                {
                    Id          = "CCH-001",
                    Title       = "Framework assets served without any compression",
                    Severity    = PerformanceSeverity.High,
                    Category    = PerformanceCategory.Compression,
                    Description = $"None of the {frameworkSummaries.Count} framework assemblies and runtime " +
                                  "files are served with Brotli or GZip compression. Without compression, " +
                                  "Blazor WASM applications download many times more data than necessary, " +
                                  "dramatically increasing startup time.",
                    Recommendation = "Enable Brotli (and GZip as fallback) on the hosting server for " +
                                     ".wasm, .dll, and .js files. For ASP.NET Core: UseResponseCompression() " +
                                     "with BrotliCompressionProvider. For Nginx: ngx_brotli module.",
                    Evidence =
                    [
                        $"Framework assets checked: {frameworkSummaries.Count}",
                        "Brotli detected: 0",
                        "GZip detected: 0"
                    ]
                });
            }
            // CCH-002: GZip only — Brotli missing (lower severity as GZip is present)
            else if (fwBrotli == 0 && fwGzip > 0)
            {
                findings.Add(new PerformanceFinding
                {
                    Id          = "CCH-002",
                    Title       = "Brotli compression not detected on framework assets",
                    Severity    = PerformanceSeverity.Medium,
                    Category    = PerformanceCategory.Compression,
                    Description = $"Framework assets are compressed with GZip ({fwGzip} assets) " +
                                  "but Brotli is not configured. Brotli typically achieves 20–26% better " +
                                  "compression than GZip for Blazor WASM payloads.",
                    Recommendation = "Add Brotli alongside the existing GZip configuration. " +
                                     "All modern browsers support Brotli via the Accept-Encoding: br header. " +
                                     "Brotli reduces startup download size with no client-side changes.",
                    Evidence =
                    [
                        $"GZip compressed framework assets: {fwGzip}",
                        "Brotli compressed assets: 0"
                    ]
                });
            }

            // CCH-004: Framework assets not cache-optimized
            var fwNotCached = frameworkSummaries.Where(s => s.CacheStatus == CacheStatus.NotCached).ToList();
            if (fwNotCached.Count > 0)
            {
                findings.Add(new PerformanceFinding
                {
                    Id          = "CCH-004",
                    Title       = "Framework assets are not cache-optimised",
                    Severity    = PerformanceSeverity.Medium,
                    Category    = PerformanceCategory.Caching,
                    Description = $"{fwNotCached.Count} framework asset{(fwNotCached.Count != 1 ? "s are" : " is")} " +
                                  "served without Cache-Control headers or ETags. Blazor framework files are " +
                                  "content-addressed — the file path includes a hash — so they can safely " +
                                  "be cached indefinitely.",
                    Recommendation = "Add Cache-Control: max-age=31536000, immutable to all " +
                                     "/_framework/ assets. A new hash-based URL is generated on publish, " +
                                     "so stale content is never served.",
                    Evidence =
                    [
                        $"Framework assets without cache headers: {fwNotCached.Count}",
                        ..fwNotCached.Take(3).Select(s => ShortName(s.Url))
                    ]
                });
            }
        }

        // CCH-003: Large assets without any compression
        var largeThreshold = (long)(t.LargeUncompressedKB * 1024);
        var largeUncompressed = summaries
            .Where(s => s.CompressionStatus == CompressionStatus.NotCompressed && s.SizeBytes > largeThreshold)
            .OrderByDescending(s => s.SizeBytes)
            .Take(5)
            .ToList();

        if (largeUncompressed.Count > 0)
        {
            var worstBytes = largeUncompressed[0].SizeBytes;
            var assetWord  = largeUncompressed.Count > 1 ? "assets" : "asset";
            findings.Add(new PerformanceFinding
            {
                Id          = "CCH-003",
                Title       = $"Large uncompressed {assetWord} detected",
                Severity    = worstBytes > 1_048_576 ? PerformanceSeverity.High : PerformanceSeverity.Medium,
                Category    = PerformanceCategory.Compression,
                Description = $"{largeUncompressed.Count} {assetWord} exceed " +
                              $"{t.LargeUncompressedKB} KB and are served without compression. " +
                              "Each represents unnecessary download time and bandwidth.",
                Recommendation = "Enable server-side compression for all large static asset types. " +
                                 "Verify that the compression module covers application/wasm, " +
                                 "application/octet-stream, and application/javascript MIME types.",
                Evidence = largeUncompressed
                    .Select(s => $"{ShortName(s.Url)} — {FormatBytes(s.SizeBytes)} ({s.Type})")
                    .ToList()
            });
        }

        // CCH-005: Non-framework static assets missing cache headers
        var staticTypes = new HashSet<AssetType>
            { AssetType.Css, AssetType.JavaScript, AssetType.Font, AssetType.Image };

        var staticNotCached = summaries
            .Where(s => staticTypes.Contains(s.Type) && s.CacheStatus == CacheStatus.NotCached)
            .ToList();

        if (staticNotCached.Count > 0)
        {
            findings.Add(new PerformanceFinding
            {
                Id          = "CCH-005",
                Title       = "Static assets missing caching headers",
                Severity    = PerformanceSeverity.Low,
                Category    = PerformanceCategory.Caching,
                Description = $"{staticNotCached.Count} static asset{(staticNotCached.Count != 1 ? "s are" : " is")} " +
                              "served without Cache-Control or ETag headers. Without caching, every page load " +
                              "re-downloads these assets unconditionally.",
                Recommendation = "Add Cache-Control and ETag headers for CSS, JavaScript, font, and image files. " +
                                 "For files with content hashes in their names: max-age=31536000, immutable. " +
                                 "For non-hashed files: add at minimum ETag and Last-Modified for conditional requests.",
                Evidence =
                [
                    $"Static assets without cache headers: {staticNotCached.Count}",
                    ..staticNotCached.Take(3).Select(s => $"{ShortName(s.Url)} ({s.Type})")
                ]
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

        if (ids.Contains("CCH-001"))
        {
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p++,
                Title       = "Enable Brotli compression for Blazor framework assets",
                Description = "Configure the hosting server to serve .wasm, .dll, .js, and .json files " +
                              "with Brotli compression. For ASP.NET Core: app.UseResponseCompression() with " +
                              "BrotliCompressionProvider targeting application/wasm and application/octet-stream. " +
                              "For Nginx: enable the ngx_brotli module and add the relevant MIME types.",
                Category    = PerformanceCategory.Compression
            });

            recs.Add(new PerformanceRecommendation
            {
                Priority    = p++,
                Title       = "Enable GZip as a compression fallback",
                Description = "Add GZip alongside Brotli as a fallback for CDN intermediaries and older clients. " +
                              "Modern browsers all support Brotli, but GZip ensures compatibility where " +
                              "Brotli is not negotiated.",
                Category    = PerformanceCategory.Compression
            });
        }
        else if (ids.Contains("CCH-002"))
        {
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p++,
                Title       = "Add Brotli compression alongside existing GZip",
                Description = "Brotli achieves 20–26% better compression than GZip for Blazor WASM payloads. " +
                              "Configure the server to offer Brotli via Accept-Encoding: br negotiation. " +
                              "The existing GZip configuration can remain as a fallback.",
                Category    = PerformanceCategory.Compression
            });
        }

        if (ids.Contains("CCH-004"))
        {
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p++,
                Title       = "Add long-lived immutable caching for framework assets",
                Description = "All files under /_framework/ are content-addressed with hash-based filenames. " +
                              "Serve them with Cache-Control: max-age=31536000, immutable. This eliminates " +
                              "re-downloading unchanged assemblies on every application update.",
                Category    = PerformanceCategory.Caching
            });
        }

        if (ids.Contains("CCH-005"))
        {
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p++,
                Title       = "Add ETag and Cache-Control for static web assets",
                Description = "Add ETag and Last-Modified headers to CSS, JavaScript, font, and image files. " +
                              "This enables conditional requests (If-None-Match) so unchanged assets " +
                              "return HTTP 304 rather than being fully re-downloaded.",
                Category    = PerformanceCategory.Caching
            });
        }

        if (ids.Contains("CCH-003"))
        {
            recs.Add(new PerformanceRecommendation
            {
                Priority    = p++,
                Title       = "Expand compression coverage to all large asset MIME types",
                Description = "Verify that the server's compression module covers all relevant MIME types: " +
                              "application/wasm, application/octet-stream, application/javascript, " +
                              "text/css, application/json. Some servers exclude octet-stream by default, " +
                              "which prevents .dll and .wasm files from being compressed.",
                Category    = PerformanceCategory.Compression
            });
        }

        // Always recommend caching API consideration
        recs.Add(new PerformanceRecommendation
        {
            Priority    = p,
            Title       = "Consider caching reference-data API responses",
            Description = "API endpoints returning rarely-changing data (configuration, lookup tables, taxonomy) " +
                          "can use Cache-Control: public, max-age=N or ETag support for conditional requests. " +
                          "Avoid caching user-specific, sensitive, or mutation responses.",
            Category    = PerformanceCategory.Caching
        });

        return recs;
    }

    // ── Per-asset recommendation ──────────────────────────────────────────────

    internal static string? GetAssetRecommendation(
        AssetType type, CompressionStatus compression, CacheStatus cache, long sizeBytes,
        CachingAnalysisThresholds t)
    {
        bool isFramework = IsFrameworkAsset(type);
        bool isStatic    = type is AssetType.Css or AssetType.JavaScript
                                  or AssetType.Font or AssetType.Image;
        bool large       = sizeBytes > (long)(t.LargeUncompressedKB * 1024);

        if (isFramework)
        {
            bool needsCompression = compression == CompressionStatus.NotCompressed;
            bool needsCache       = cache is CacheStatus.NotCached or CacheStatus.WeaklyCached;

            if (needsCompression && needsCache)
                return "Enable Brotli; add max-age=31536000, immutable";
            if (needsCompression)
                return "Enable Brotli compression";
            if (cache == CacheStatus.NotCached)
                return "Add max-age=31536000, immutable";
            if (cache == CacheStatus.WeaklyCached)
                return "Increase to max-age=31536000, immutable";
        }
        else if (isStatic)
        {
            if (compression == CompressionStatus.NotCompressed && large)
                return "Enable compression for large asset";
            if (cache == CacheStatus.NotCached)
                return "Add ETag or Cache-Control headers";
        }

        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int? ExtractMaxAge(string cacheControl)
    {
        var m = Regex.Match(cacheControl, @"\bmax-age\s*=\s*(\d+)", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var sec) ? sec : null;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)        return "0 B";
        if (bytes < 1_024)     return $"{bytes} B";
        if (bytes < 1_048_576) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / 1_048_576.0:F2} MB";
    }

    private static string ShortName(string url)
    {
        var idx  = url.LastIndexOf('/');
        var name = idx >= 0 ? url[(idx + 1)..] : url;
        return name.Length > 60 ? "…" + name[^57..] : name;
    }
}
