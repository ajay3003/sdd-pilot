using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.WasmPerformance;

public sealed class WasmAssetDiscoveryService : IWasmAssetDiscoveryService
{
    private const int MaxConcurrency     = 10;
    private const int MaxAssembliesToFetch = 500;
    private const int ContentFetchTimeoutSec = 15;
    private const int HeadersFetchTimeoutSec = 10;

    private static readonly string[] FrameworkPrefixes =
    [
        "Microsoft.", "System.", "mscorlib", "netstandard",
        "Mono.", "HotChocolate.", "StrawberryShake.",
        "Newtonsoft.", "MudBlazor.", "Blazored.", "Radzen.",
        "Serilog.", "AutoMapper.", "FluentValidation.",
        "dotnet.", "MediatR.", "Polly.", "Azure.",
        "BouncyCastle.", "NodaTime.", "Humanizer.",
    ];

    private readonly HttpClient _client;
    private readonly ILogger<WasmAssetDiscoveryService> _logger;

    public WasmAssetDiscoveryService(HttpClient client, ILogger<WasmAssetDiscoveryService> logger)
    {
        _client = client;
        _logger = logger;
    }

    // ── Orchestration ─────────────────────────────────────────────────────────

    public async Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(
        string targetUrl, CancellationToken ct = default)
    {
        var normalized = targetUrl.Trim().TrimEnd('/') + "/";
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var rootUri) ||
            (rootUri.Scheme != "http" && rootUri.Scheme != "https"))
        {
            return new WasmAssetDiscoveryResult
            {
                TargetUrl    = targetUrl,
                DiscoveredAt = DateTime.UtcNow,
                Error        = "TargetUrl must be a valid http or https URL."
            };
        }

        _logger.LogInformation("Asset discovery started for {Host}", rootUri.Host);

        var assets = new List<DiscoveredAsset>();

        // 1. Fetch index.html
        var (indexAsset, indexContent) = await FetchWithContentAsync(rootUri, AssetType.Index, ct);
        assets.Add(indexAsset);

        // Resolve framework base using <base href> so sub-path apps work correctly.
        var baseHref       = indexContent is not null ? ExtractBaseHref(indexContent) ?? "/" : "/";
        var frameworkBase  = new Uri(rootUri, baseHref.TrimEnd('/') + "/");

        // 2. blazor.webassembly.js (loader, listed in index.html but not in boot.json)
        assets.Add(await FetchHeadersOnlyAsync(
            new Uri(frameworkBase, "_framework/blazor.webassembly.js"),
            AssetType.FrameworkJs, ct));

        // 3. blazor.boot.json (full content needed to discover all assemblies)
        var (bootAsset, bootContent) = await FetchWithContentAsync(
            new Uri(frameworkBase, "_framework/blazor.boot.json"),
            AssetType.BootManifest, ct);
        assets.Add(bootAsset);

        var deferred = new List<(Uri Uri, AssetType Type)>();

        // 4. CSS / JS / font / image refs from index.html
        if (indexContent is not null)
        {
            foreach (var (path, type) in ParseIndexHtmlAssets(indexContent))
                deferred.Add((new Uri(rootUri, path), type));
        }

        // 5. All assets listed in blazor.boot.json
        if (bootContent is not null)
        {
            var manifest = ParseBootManifest(bootContent);
            if (manifest?.Resources is not null)
            {
                foreach (var (filename, type) in ExpandManifestAssets(manifest).Take(MaxAssembliesToFetch))
                    deferred.Add((new Uri(frameworkBase, $"_framework/{filename}"), type));
            }
        }

        // 6. Parallel HEAD-style fetches for everything else
        if (deferred.Count > 0)
            assets.AddRange(await FetchAssetsInParallelAsync(deferred, ct));

        var isBlazor = assets.Any(a => a.Type == AssetType.BootManifest && a.StatusCode is >= 200 and < 300);

        _logger.LogInformation(
            "Asset discovery complete for {Host}: {Count} assets, blazorWasm={IsBlazor}",
            rootUri.Host, assets.Count, isBlazor);

        return new WasmAssetDiscoveryResult
        {
            TargetUrl    = targetUrl,
            DiscoveredAt = DateTime.UtcNow,
            IsBlazorWasm = isBlazor,
            Assets       = assets
        };
    }

    // ── Pure static methods — unit-testable ───────────────────────────────────

    internal static BlazorBootManifest? ParseBootManifest(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<BlazorBootManifest>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyList<(string Filename, AssetType Type)> ExpandManifestAssets(
        BlazorBootManifest manifest)
    {
        var results = new List<(string, AssetType)>();
        var res = manifest.Resources;
        if (res is null) return results;

        Add(res.JsModuleNative,  AssetType.FrameworkJs);
        Add(res.JsModuleRuntime, AssetType.FrameworkJs);
        Add(res.WasmNative,      AssetType.WasmRuntime);
        Add(res.Icu,             AssetType.Other);
        Add(res.CoreAssembly,    AssetType.FrameworkDll);

        if (res.Assembly is not null)
        {
            foreach (var (filename, _) in res.Assembly)
                results.Add((filename, ClassifyAssembly(filename, manifest.MainAssemblyName)));
        }

        if (res.SatelliteResources is not null)
        {
            foreach (var (culture, files) in res.SatelliteResources)
            foreach (var (filename, _) in files)
                results.Add(($"{culture}/{filename}", AssetType.SatelliteAssembly));
        }

        return results;

        void Add(Dictionary<string, string>? dict, AssetType type)
        {
            if (dict is null) return;
            foreach (var (filename, _) in dict)
                results.Add((filename, type));
        }
    }

    internal static AssetType ClassifyAssembly(string filename, string? mainAssemblyName)
    {
        // Strip the .wasm extension to get the assembly name
        var name = System.IO.Path.GetFileNameWithoutExtension(filename);

        // Satellite assemblies: name ends with .resources after removing .wasm
        if (name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            return AssetType.SatelliteAssembly;

        // Main application assembly
        if (!string.IsNullOrEmpty(mainAssemblyName) &&
            name.Equals(mainAssemblyName, StringComparison.OrdinalIgnoreCase))
            return AssetType.ApplicationDll;

        // Known framework prefixes — match "Serilog" against prefix "Serilog." by
        // stripping the trailing dot and requiring name == bare prefix OR name starts with "bare."
        foreach (var prefix in FrameworkPrefixes)
        {
            var bare = prefix.TrimEnd('.');
            if (name.Equals(bare, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(bare + ".", StringComparison.OrdinalIgnoreCase))
                return AssetType.FrameworkDll;
        }

        return AssetType.ApplicationDll;
    }

    internal static IReadOnlyList<(string RelativePath, AssetType Type)> ParseIndexHtmlAssets(string html)
    {
        var results = new List<(string, AssetType)>();
        if (string.IsNullOrWhiteSpace(html)) return results;

        // CSS: <link rel="stylesheet" href="..."> in any attribute order
        foreach (Match m in Regex.Matches(html,
            @"<link\b[^>]*\brel\s*=\s*[""']stylesheet[""'][^>]*\bhref\s*=\s*[""']([^""'#?]+)[""']" +
            @"|<link\b[^>]*\bhref\s*=\s*[""']([^""'#?]+)[""'][^>]*\brel\s*=\s*[""']stylesheet[""']",
            RegexOptions.IgnoreCase))
        {
            var href = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (IsSameOriginRelative(href))
                results.Add((href, AssetType.Css));
        }

        // JS: <script src="..."> — exclude _framework entries (handled separately)
        foreach (Match m in Regex.Matches(html,
            @"<script\b[^>]*\bsrc\s*=\s*[""']([^""'#?]+)[""']",
            RegexOptions.IgnoreCase))
        {
            var src = m.Groups[1].Value;
            if (IsSameOriginRelative(src) && !src.Contains("_framework", StringComparison.OrdinalIgnoreCase))
                results.Add((src, AssetType.JavaScript));
        }

        // Icons / favicons: <link rel="icon|shortcut icon|apple-touch-icon" href="...">
        foreach (Match m in Regex.Matches(html,
            @"<link\b[^>]*\brel\s*=\s*[""'](?:icon|shortcut icon|apple-touch-icon)[""'][^>]*\bhref\s*=\s*[""']([^""'#?]+)[""']" +
            @"|<link\b[^>]*\bhref\s*=\s*[""']([^""'#?]+)[""'][^>]*\brel\s*=\s*[""'](?:icon|shortcut icon|apple-touch-icon)[""']",
            RegexOptions.IgnoreCase))
        {
            var href = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (IsSameOriginRelative(href))
                results.Add((href, AssetType.Image));
        }

        // Preloaded fonts: <link rel="preload" as="font" href="...">
        foreach (Match m in Regex.Matches(html,
            @"<link\b[^>]*\bas\s*=\s*[""']font[""'][^>]*\bhref\s*=\s*[""']([^""'#?]+)[""']" +
            @"|<link\b[^>]*\bhref\s*=\s*[""']([^""'#?]+\.(woff2?|ttf|otf|eot))[""']",
            RegexOptions.IgnoreCase))
        {
            var href = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (IsSameOriginRelative(href))
                results.Add((href, AssetType.Font));
        }

        return results.DistinctBy(r => r.Item1).ToList();
    }

    internal static string? ExtractBaseHref(string html)
    {
        var m = Regex.Match(html,
            @"<base\b[^>]*\bhref\s*=\s*[""']([^""']+)[""']",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool IsSameOriginRelative(string href) =>
        !string.IsNullOrWhiteSpace(href)          &&
        !href.StartsWith("//")                    &&
        !href.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) &&
        !href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
        !href.StartsWith("data:",    StringComparison.OrdinalIgnoreCase);

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private async Task<(DiscoveredAsset Asset, string? Content)> FetchWithContentAsync(
        Uri uri, AssetType type, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(ContentFetchTimeoutSec));

        try
        {
            var response = await _client.GetAsync(uri, cts.Token);
            sw.Stop();
            var content = response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cts.Token)
                : null;
            return (BuildAsset(uri.ToString(), type, response, content?.Length ?? 0, sw.Elapsed.TotalMilliseconds), content);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return (ErrorAsset(uri.ToString(), type, "Request timed out", sw.Elapsed.TotalMilliseconds), null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (ErrorAsset(uri.ToString(), type, ex.Message, sw.Elapsed.TotalMilliseconds), null);
        }
    }

    private async Task<DiscoveredAsset> FetchHeadersOnlyAsync(
        Uri uri, AssetType type, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(HeadersFetchTimeoutSec));

        try
        {
            var response = await _client.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, uri),
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token);
            sw.Stop();
            return BuildAsset(uri.ToString(), type, response, 0, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return ErrorAsset(uri.ToString(), type, "Request timed out", sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ErrorAsset(uri.ToString(), type, ex.Message, sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<IReadOnlyList<DiscoveredAsset>> FetchAssetsInParallelAsync(
        IReadOnlyList<(Uri Uri, AssetType Type)> items, CancellationToken ct)
    {
        using var semaphore = new SemaphoreSlim(MaxConcurrency);

        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync(ct);
            try   { return await FetchHeadersOnlyAsync(item.Uri, item.Type, ct); }
            finally { semaphore.Release(); }
        });

        return await Task.WhenAll(tasks);
    }

    private static DiscoveredAsset BuildAsset(
        string url, AssetType type,
        HttpResponseMessage response,
        long downloadedBytes,
        double elapsedMs)
    {
        return new DiscoveredAsset
        {
            Url             = url,
            Type            = type,
            StatusCode      = (int)response.StatusCode,
            ContentLength   = response.Content.Headers.ContentLength,
            DownloadedBytes = downloadedBytes,
            ContentType     = response.Content.Headers.ContentType?.MediaType,
            ContentEncoding = response.Content.Headers.ContentEncoding.FirstOrDefault(),
            CacheControl    = response.Headers.CacheControl?.ToString(),
            ETag            = response.Headers.ETag?.ToString(),
            LastModified    = response.Content.Headers.LastModified?.ToString("R"),
            DownloadTimeMs  = Math.Round(elapsedMs, 1)
        };
    }

    private static DiscoveredAsset ErrorAsset(
        string url, AssetType type, string error, double elapsedMs) =>
        new()
        {
            Url            = url,
            Type           = type,
            StatusCode     = 0,
            DownloadTimeMs = Math.Round(elapsedMs, 1),
            Error          = error
        };
}
