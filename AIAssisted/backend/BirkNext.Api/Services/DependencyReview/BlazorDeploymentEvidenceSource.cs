using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.BrowserCompanion;
using BirkNext.Dependencies;

namespace BirkNext.Api.Services.DependencyReview;

/// <summary>What one deployed-evidence capture produced: an inventory of observed artifacts (when any strategy found some) and, always, the
/// per-strategy report.</summary>
public sealed record DeployedCaptureOutcome(DependencyInventorySnapshot? Inventory, DeploymentCaptureReport Report);

/// <summary>Deployed dependency evidence for a target (read-only GETs of public deployment metadata, plus evidence BirkNext already holds).</summary>
public interface IDeployedDependencySource
{
    Task<DeployedCaptureOutcome> CaptureAsync(DeployedCaptureRequest request, CancellationToken ct);
}

/// <summary>
/// One Blazor WebAssembly deployment-evidence capability with several strategies, each recorded with its own outcome:
/// <list type="number">
/// <item>Classic Blazor boot manifest (_framework/blazor.boot.json).</item>
/// <item>Blazor runtime metadata: dotnet.js (resolved through the page's import map) and the boot configuration it embeds between explicit markers.</item>
/// <item>Application asset manifest: a PWA service-worker-assets.js, when the target has one.</item>
/// <item>Browser runtime evidence: _framework resources the paired Browser Companion observed loading (a subset — never complete).</item>
/// <item>Build artifact evidence: not stored by BirkNext, reported as such.</item>
/// </list>
/// An HTTP 404 is a strategy result ("Not detected"), never the overall result. Artifact files are not package identities: every observed
/// artifact has version Unknown. Credentials are never sent and nothing beyond artifact names, paths and integrity values is stored.
/// </summary>
public sealed partial class BlazorDeploymentEvidenceSource(HttpClient http, IBrowserCompanionService companion, TimeProvider time) : IDeployedDependencySource
{
    private const long MaxBytes = 5 * 1024 * 1024;
    public const string Classic = "Classic Blazor boot manifest", Runtime = "Blazor runtime metadata", AssetManifest = "Application asset manifest",
        Browser = "Browser runtime evidence", Build = "Build artifact evidence";
    /// <summary>Browser observations older than this are labelled historical, not current deployment state.</summary>
    public static readonly TimeSpan BrowserEvidenceCurrentFor = TimeSpan.FromHours(24);

    private sealed record Fetch(string? Text, int? Status, string? Failure);

    private sealed class Observed(string name, string type)
    {
        public string Name { get; } = name;
        public string Type { get; } = type;
        public string? Location { get; set; }
        public Dictionary<string, string> Integrity { get; } = new(StringComparer.Ordinal);
        public List<string> ObservedBy { get; } = [];
    }

    public async Task<DeployedCaptureOutcome> CaptureAsync(DeployedCaptureRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (!Uri.TryCreate(request.TargetUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var root) || root.Scheme is not ("http" or "https"))
            return new(null, new DeploymentCaptureReport { Summary = "Enter an http(s) deployment URL.", CapturedAt = now, Capability = CaptureCapability.Unknown, Overall = CaptureOverall.Failed });

        var strategies = new List<CaptureStrategyResult>();
        var artifacts = new Dictionary<string, Observed>(StringComparer.OrdinalIgnoreCase);
        var host = root.GetLeftPart(UriPartial.Authority);
        var index = await GetAsync(root, ct);
        var html = index.Text ?? "";
        var baseHref = BaseHref().Match(html) is { Success: true } m ? m.Groups["href"].Value : "/";
        var frameworkBase = new Uri(root, baseHref.TrimEnd('/') + "/");
        var blazorReferenced = BlazorDeploymentMetadata.ReferencesBlazorRuntime(html);
        string? runtimeLayout = null;

        void Add(IEnumerable<DeployedArtifact> found, string method, Uri? baseUri)
        {
            foreach (var a in found)
            {
                var name = BlazorDeploymentMetadata.Logical(a.Name);
                if (!artifacts.TryGetValue(name, out var item)) artifacts[name] = item = new Observed(name, a.Type == "runtime" ? "runtime" : BlazorDeploymentMetadata.Kind(name));
                item.Location ??= baseUri is null ? null : new Uri(baseUri, $"_framework/{a.File}").GetLeftPart(UriPartial.Path);
                if (a.Integrity is { Length: > 0 } integrity) item.Integrity[method] = integrity.StartsWith("sha256-", StringComparison.Ordinal) ? integrity[7..] : integrity;
                if (!item.ObservedBy.Contains(method)) item.ObservedBy.Add(method);
            }
        }

        // 1. Classic boot manifest.
        var bootUri = new Uri(frameworkBase, "_framework/blazor.boot.json");
        var boot = await GetAsync(bootUri, ct);
        var classic = Strategy("classic-boot-manifest", Classic, bootUri, boot, text =>
        {
            var found = BlazorDeploymentMetadata.BootConfigArtifacts(text);
            if (found is null) return (CaptureStrategyOutcome.Failed, "The response is not a Blazor boot manifest (malformed or unexpected shape).", 0);
            Add(found, Classic, frameworkBase);
            return (CaptureStrategyOutcome.Captured, $"{found.Count} deployed artifacts listed, with integrity values where the manifest states them.", found.Count);
        }, now);
        strategies.Add(classic);
        if (classic.Outcome == CaptureStrategyOutcome.Captured) runtimeLayout = "Classic boot manifest";
        var enumerated = classic.Outcome == CaptureStrategyOutcome.Captured && classic.Artifacts > 0;

        // 2. Modern runtime metadata (dotnet.js, resolved through the import map when the page has one).
        if (enumerated)
            strategies.Add(NotNeeded("modern-runtime", Runtime));
        else
        {
            var dotnetPath = BlazorDeploymentMetadata.ImportMapDotnetJs(html) ?? "_framework/dotnet.js";
            var dotnetUri = new Uri(frameworkBase, dotnetPath.TrimStart('.', '/'));
            var dotnet = await GetAsync(dotnetUri, ct);
            var modern = Strategy("modern-runtime", Runtime, dotnetUri, dotnet, text =>
            {
                runtimeLayout = "Modern Blazor runtime";
                if (BlazorDeploymentMetadata.EmbeddedBootConfig(text) is { } json && BlazorDeploymentMetadata.BootConfigArtifacts(json) is { Count: > 0 } found)
                {
                    Add(found, Runtime, frameworkBase);
                    return (CaptureStrategyOutcome.Captured, $"{found.Count} deployed artifacts listed in the boot configuration embedded in dotnet.js.", found.Count);
                }
                return (CaptureStrategyOutcome.Detected, "The Blazor runtime (dotnet.js) is deployed, but it exposes no artifact list BirkNext can read safely; nothing is inferred from minified code.", 0);
            }, now);
            strategies.Add(modern);
            enumerated = modern.Outcome == CaptureStrategyOutcome.Captured;
        }

        // 3. Application asset manifest (only PWAs have one; absence is "Not detected", not a failure).
        if (enumerated)
            strategies.Add(NotNeeded("service-worker-assets", AssetManifest));
        else
        {
            var swUri = new Uri(frameworkBase, "service-worker-assets.js");
            var sw = await GetAsync(swUri, ct);
            var asset = Strategy("service-worker-assets", AssetManifest, swUri, sw, text =>
            {
                var found = BlazorDeploymentMetadata.ServiceWorkerArtifacts(text);
                if (found is null) return (CaptureStrategyOutcome.NotDetected, "service-worker-assets.js exists but is not a Blazor asset manifest.", 0);
                Add(found, AssetManifest, frameworkBase);
                return (CaptureStrategyOutcome.Captured, $"{found.Count} _framework assets listed with hashes in the PWA asset manifest.", found.Count);
            }, now);
            if (sw.Status is 404 or 410) asset = asset with { Detail = "Not detected · the target has no PWA asset manifest (not every Blazor app is a PWA)." };
            strategies.Add(asset);
            enumerated = asset.Outcome == CaptureStrategyOutcome.Captured;
        }

        // 4. Browser runtime evidence (what the paired Browser Companion observed loading; local, no request to the target).
        var (browser, browserDetected) = BrowserStrategy(request.ProfileId, root, now, Add);
        strategies.Add(browser);

        // 5. Build artifact evidence.
        strategies.Add(new CaptureStrategyResult { Strategy = "build-artifact", Name = Build, Outcome = CaptureStrategyOutcome.NotAvailable,
            Detail = "BirkNext stores no build or publish artifact manifest for this target." });

        var runtimeDetected = strategies.Any(s => s.Strategy is "classic-boot-manifest" or "modern-runtime" && s.Outcome is CaptureStrategyOutcome.Captured or CaptureStrategyOutcome.Detected);
        var framework = runtimeDetected || blazorReferenced || browserDetected || strategies.Any(s => s.Outcome == CaptureStrategyOutcome.Captured) ? "Blazor WebAssembly" : "Not detected";
        var anyFailure = strategies.Any(s => s.Outcome is CaptureStrategyOutcome.Failed or CaptureStrategyOutcome.AuthenticationRequired);
        var overall = enumerated ? CaptureOverall.Captured
            : browser.Outcome == CaptureStrategyOutcome.Partial ? CaptureOverall.Partial
            : anyFailure && !strategies.Any(s => s.Outcome is CaptureStrategyOutcome.NotDetected or CaptureStrategyOutcome.Detected) ? CaptureOverall.Failed
            : CaptureOverall.NotCaptured;
        var capability = enumerated ? CaptureCapability.Supported
            : browser.Outcome == CaptureStrategyOutcome.Partial || runtimeDetected ? CaptureCapability.Partial
            : anyFailure ? CaptureCapability.Unknown
            : CaptureCapability.Unsupported;
        var attempted = strategies.Count(s => s.Outcome is not (CaptureStrategyOutcome.NotAttempted or CaptureStrategyOutcome.NotAvailable));
        var summary = overall switch
        {
            CaptureOverall.Captured => $"Deployment evidence captured: {artifacts.Count} deployed artifacts.",
            CaptureOverall.Partial => $"Deployment evidence captured with limitations: {artifacts.Count} runtime artifacts observed. Browser observation covers loaded resources, not necessarily all deployed artifacts.",
            CaptureOverall.Failed => "Deployment evidence not captured: the target could not be read (see capture details).",
            _ => framework == "Blazor WebAssembly"
                ? $"Deployment evidence not captured: no supported capture strategy produced deployment evidence ({attempted} attempted)."
                : "Deployment evidence not captured: no Blazor WebAssembly deployment metadata was found, and no other deployment-capture provider applies to this target.",
        };
        var report = new DeploymentCaptureReport
        {
            Framework = framework, RuntimeLayout = runtimeLayout, Capability = capability, Overall = overall, Strategies = strategies, Summary = summary,
            Complete = enumerated, CapturedAt = now,
        };
        if (artifacts.Count == 0) return new(null, report);

        var limitations = new List<string>
        {
            "Deployed artifacts are files, not NuGet package identities: package versions are not available from deployment evidence.",
            "Deployed is not runtime-loaded: lazy-loaded or unused assemblies are listed the same way.",
        };
        if (!enumerated) limitations.Add("Browser observation covers loaded resources only; an artifact missing from it is not proven absent from the deployment.");
        foreach (var a in artifacts.Values.Where(a => a.Integrity.Values.Distinct(StringComparer.Ordinal).Count() > 1))
            limitations.Add($"Evidence discrepancy for {a.Name}: {string.Join(" vs ", a.Integrity.Select(i => $"{i.Key} {i.Value}"))}. Neither value is chosen.");
        var items = artifacts.Values.Select(a => new InventoryDependency
        {
            PackageName = a.Name, PackageManager = a.Type == "assembly" ? "assembly" : "runtime artifact", Relationship = DependencyRelationship.Unknown, Stage = InventoryStage.Deployed,
            Location = a.Location ?? $"{host}/_framework/{a.Name}", ObservedBy = a.ObservedBy,
            Hashes = [.. a.Integrity.Select(i => new DependencyHash($"SHA-256 integrity ({i.Key})", i.Value))],
        }).OrderBy(i => i.PackageName, StringComparer.OrdinalIgnoreCase).ToList();
        var methods = strategies.Where(s => s.Outcome is CaptureStrategyOutcome.Captured or CaptureStrategyOutcome.Partial).Select(s => s.Name).ToList();
        return new(new DependencyInventorySnapshot
        {
            Id = Guid.NewGuid(), Name = $"{root.Host}{(request.Environment is { Length: > 0 } e ? $" ({e})" : "")} · deployed artifacts {now:yyyy-MM-dd HH:mm} UTC", SourceType = InventorySourceType.Deployment,
            SourceName = host, Stage = InventoryStage.Deployed, CapturedAt = now, RecordedAt = now, Environment = string.IsNullOrWhiteSpace(request.Environment) ? null : request.Environment.Trim(),
            ContentSha256 = InventorySources.Sha256(Encoding.UTF8.GetBytes(string.Join("\n", items.Select(i => $"{i.PackageName}|{string.Join(",", i.Hashes.Select(h => h.Value))}")))),
            Dependencies = items, Limitations = limitations, Capture = report,
            Provenance = $"Deployment capture of {host} at {now:yyyy-MM-dd HH:mm} UTC via {string.Join(" + ", methods)} (read-only).",
        }, report);
    }

    /// <summary>Outcome of one HTTP strategy: 404 is Not detected, 401/403 needs authentication, transport errors and other statuses failed.</summary>
    private static CaptureStrategyResult Strategy(string key, string name, Uri uri, Fetch fetch, Func<string, (CaptureStrategyOutcome Outcome, string Detail, int Artifacts)> parse, DateTimeOffset now)
    {
        var technical = $"GET {uri.GetLeftPart(UriPartial.Path)} → {(fetch.Status is { } s ? $"HTTP {s}" : fetch.Failure)}";
        if (fetch.Text is { } text)
        {
            var (outcome, detail, count) = parse(text);
            return new CaptureStrategyResult { Strategy = key, Name = name, Outcome = outcome, Detail = detail, Artifacts = count, Complete = outcome == CaptureStrategyOutcome.Captured, ObservedAt = now, Technical = technical };
        }
        var (o, d) = fetch.Status switch
        {
            >= 200 and < 300 => (CaptureStrategyOutcome.Failed, fetch.Failure ?? "The response could not be read."),
            404 or 410 => (CaptureStrategyOutcome.NotDetected, "Not detected · HTTP 404."),
            401 or 403 => (CaptureStrategyOutcome.AuthenticationRequired, $"The target requires authentication for this file (HTTP {fetch.Status}); no credentials are sent."),
            { } other => (CaptureStrategyOutcome.Failed, $"The target answered HTTP {other}."),
            null => (CaptureStrategyOutcome.Failed, fetch.Failure ?? "The target could not be read."),
        };
        return new CaptureStrategyResult { Strategy = key, Name = name, Outcome = o, Detail = d, ObservedAt = now, Technical = technical };
    }

    private static CaptureStrategyResult NotNeeded(string key, string name) => new()
    {
        Strategy = key, Name = name, Outcome = CaptureStrategyOutcome.NotAttempted, Detail = "Not needed: an earlier strategy enumerated the deployed artifacts.",
    };

    /// <summary>
    /// _framework resources the paired Browser Companion observed on this target's origin (sanitized URLs, statuses; no hashes). The lists it
    /// keeps are capped, so this is always a subset of what the deployment serves.
    /// </summary>
    private (CaptureStrategyResult Result, bool BlazorDetected) BrowserStrategy(string? profileId, Uri root, DateTimeOffset now, Action<IEnumerable<DeployedArtifact>, string, Uri?> add)
    {
        var origin = root.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        var pages = string.IsNullOrWhiteSpace(profileId) ? [] : companion.Status(profileId).Pages
            .Where(p => string.Equals(p.PageOrigin.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.CapturedAt).ToList();
        if (pages.Count == 0)
            return (new CaptureStrategyResult { Strategy = "browser-runtime", Name = Browser, Outcome = CaptureStrategyOutcome.NotAvailable,
                Detail = string.IsNullOrWhiteSpace(profileId) ? "No Target Environment selected, so no Browser Companion evidence applies." : "No Browser Companion evidence for this target. Browse the target with the companion connected to add runtime evidence." }, false);
        var blazor = pages.Any(p => p.Blazor?.Detected == true);
        var resources = pages.SelectMany(p => new[] { p.Performance?.Timeline, p.Performance?.LongestResources, p.Performance?.FailedResources, p.Blazor?.FrameworkFailures }
                .SelectMany(list => list ?? []).Concat(new[] { p.Blazor?.SlowestFrameworkResource }.OfType<BrowserResourceEntry>()))
            .Where(r => r.Url.Contains("/_framework/", StringComparison.Ordinal) && r.Status is null or < 400 && !r.Url.Contains("[REDACTED]", StringComparison.Ordinal))
            .Select(r => System.IO.Path.GetFileName(new Uri(r.Url).AbsolutePath)).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(f => new DeployedArtifact(f, f, null, BlazorDeploymentMetadata.Kind(f))).ToList();
        var latest = pages[0];
        var historical = now - latest.CapturedAt > BrowserEvidenceCurrentFor;
        var label = historical ? "Historical browser evidence" : "Browser evidence from the current session";
        if (resources.Count == 0)
            return (new CaptureStrategyResult { Strategy = "browser-runtime", Name = Browser, Outcome = CaptureStrategyOutcome.NotDetected, ObservedAt = latest.CapturedAt,
                Detail = $"{label} ({latest.CapturedAt:yyyy-MM-dd HH:mm} UTC) lists no _framework resources for {origin}." }, blazor);
        add(resources, Browser, new Uri(root, "/"));
        return (new CaptureStrategyResult
        {
            Strategy = "browser-runtime", Name = Browser, Outcome = CaptureStrategyOutcome.Partial, Artifacts = resources.Count, Complete = false, ObservedAt = latest.CapturedAt,
            Detail = $"{label}: {resources.Count} _framework resources observed loading on {pages.Count} page(s), last {latest.CapturedAt:yyyy-MM-dd HH:mm} UTC. Loaded resources only — not every deployed artifact; no integrity values.",
            Technical = $"Browser Companion page evidence ({latest.BrowserName ?? "browser"}, extension resource timing); latest page {latest.Identity}.",
        }, blazor);
    }

    [GeneratedRegex(@"<base\s+href\s*=\s*[""'](?<href>[^""']*)[""']", RegexOptions.IgnoreCase)] private static partial Regex BaseHref();

    /// <summary>Read-only GET without credentials. Transport failures and timeouts are reported, not thrown.</summary>
    private async Task<Fetch> GetAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return new(null, (int)response.StatusCode, null);
            if (response.Content.Headers.ContentLength > MaxBytes) return new(null, (int)response.StatusCode, "larger than the 5 MB limit");
            var text = await response.Content.ReadAsStringAsync(ct);
            return text.Length > MaxBytes ? new(null, (int)response.StatusCode, "larger than the 5 MB limit") : new(text, (int)response.StatusCode, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return new(null, null, "The target did not answer within the timeout."); }
        catch (HttpRequestException ex) { return new(null, null, $"The target could not be reached ({ex.HttpRequestError})."); }
    }
}
