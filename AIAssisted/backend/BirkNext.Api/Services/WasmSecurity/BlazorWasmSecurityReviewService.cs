using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BirkNext.Api.Services.WasmSecurity;

public sealed partial class BlazorWasmSecurityReviewService : IBlazorWasmSecurityReviewService
{
    private readonly HttpClient _http;
    private readonly ILogger<BlazorWasmSecurityReviewService> _logger;

    private const int MaxAssetBytes = 2 * 1024 * 1024;

    private static readonly string[] BootAssets =
    [
        "_framework/blazor.boot.json",
        "appsettings.json",
        "appsettings.Production.json",
        "appsettings.Development.json",
        "service-worker-assets.js",
        "_framework/blazor.webassembly.js",
        "service-worker.published.js",
    ];

    private static readonly string[] MapAssets =
    [
        "_framework/blazor.webassembly.js.map",
        "_framework/dotnet.js.map",
        "_framework/dotnet.runtime.js.map",
    ];

    public BlazorWasmSecurityReviewService(
        HttpClient http,
        ILogger<BlazorWasmSecurityReviewService> logger)
    {
        _http   = http;
        _logger = logger;
    }

    // ── Public entry point ─────────────────────────────────────────────────

    public async Task<WasmSecurityReviewReport> ScanAsync(WasmScanRequest request, CancellationToken ct)
    {
        var base_ = NormalizeBase(request.TargetUrl);
        var findings = new List<WasmSecurityFinding>();
        var assets   = new List<WasmDiscoveredAsset>();

        // ── Phase 1: fetch index, collect base headers ──────────────────────
        var (indexContent, indexHeaders, indexStatus) = await FetchAsync(base_, ct);
        assets.Add(Asset(base_, "HTML", indexStatus, indexContent?.Length));

        var isBlazorWasm = indexContent is not null && DetectBlazorWasm(indexContent);

        // ── Phase 2: fetch all known text assets in parallel ────────────────
        var assetTasks = BootAssets
            .Select(path => FetchAndRecord(base_, path, ct))
            .ToArray();

        var mapTasks = MapAssets
            .Select(path => FetchAndRecord(base_, path, ct))
            .ToArray();

        await Task.WhenAll([.. assetTasks, .. mapTasks]);

        var fetched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, content, status, size) in assetTasks.Select(t => t.Result))
        {
            assets.Add(Asset(Url(base_, path), ClassifyAssetType(path), status, size));
            if (content is not null) fetched[path] = content;
        }

        foreach (var (path, content, status, size) in mapTasks.Select(t => t.Result))
        {
            assets.Add(Asset(Url(base_, path), "SourceMap", status, size));
            if (content is not null) fetched[path] = content;
        }

        // ── Phase 3: run checks ─────────────────────────────────────────────
        var headers = BuildHeaderDict(indexHeaders);

        foreach (var (path, content) in fetched)
        {
            if (path.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("appsettings.Production.json", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("appsettings.Development.json", StringComparison.OrdinalIgnoreCase))
            {
                findings.AddRange(CheckConfigKeys(content, path, request));
                findings.AddRange(CheckMsalConfig(content, path, request));
                if (path.EndsWith("Development.json", StringComparison.OrdinalIgnoreCase))
                    findings.Add(DevelopmentConfigFinding(path));
            }

            if (path.Equals("_framework/blazor.boot.json", StringComparison.OrdinalIgnoreCase))
                findings.AddRange(CheckBootJson(content, path));

            findings.AddRange(CheckBrowserStorage(content, path));
            findings.AddRange(CheckSensitiveData(content, path));
        }

        findings.AddRange(CheckSecurityHeaders(headers));
        findings.AddRange(CheckCors(headers));

        // Source maps exposed
        foreach (var mapPath in MapAssets)
        {
            if (fetched.ContainsKey(mapPath))
                findings.Add(SourceMapFinding(Url(base_, mapPath)));
        }
        // sourceMappingURL inside JS
        if (fetched.TryGetValue("_framework/blazor.webassembly.js", out var jsContent))
            findings.AddRange(CheckSourceMappingUrl(jsContent));

        // Endpoint discovery + backend exposure
        var allContent = string.Join('\n', fetched.Values);
        var endpoints  = ClassifyEndpoints(ExtractUrls(allContent), request).ToList();
        findings.AddRange(CheckBackendEndpoints(endpoints, request));

        // Config exposure findings (for each accessible appsettings)
        foreach (var path in new[] { "appsettings.json", "appsettings.Production.json" })
            if (fetched.ContainsKey(path))
                findings.Insert(0, ConfigExposedFinding(Url(base_, path), path));

        var configSummary = BuildConfigSummary(fetched, findings);
        var headerResults = BuildHeaderResults(headers);
        var deduped       = Deduplicate(findings);
        var score         = CalculateScore(deduped);
        var recommendations = GenerateRecommendations(deduped);

        return new WasmSecurityReviewReport
        {
            TargetUrl   = request.TargetUrl,
            ScannedAt   = DateTime.UtcNow,
            IsBlazorWasm = isBlazorWasm,
            Health      = new WasmSecurityHealth
            {
                Score               = score,
                Critical            = deduped.Count(f => f.Severity == WasmSecuritySeverity.Critical),
                High                = deduped.Count(f => f.Severity == WasmSecuritySeverity.High),
                Medium              = deduped.Count(f => f.Severity == WasmSecuritySeverity.Medium),
                Low                 = deduped.Count(f => f.Severity == WasmSecuritySeverity.Low),
                Info                = deduped.Count(f => f.Severity == WasmSecuritySeverity.Info),
                AssetsScanned       = assets.Count(a => a.Analyzed),
                FindingsCount       = deduped.Count,
                EndpointsDiscovered = endpoints.Count,
                HeadersChecked      = headerResults.Count,
            },
            Findings           = [.. deduped.OrderBy(f => f.Severity)],
            Assets             = assets,
            Endpoints          = endpoints,
            ConfigurationSummary = configSummary,
            Headers            = headerResults,
            Recommendations    = recommendations,
            Limitations        = Limitations(),
        };
    }

    // ── Check: Configuration keys ──────────────────────────────────────────

    internal static IEnumerable<WasmSecurityFinding> CheckConfigKeys(
        string jsonContent, string source, WasmScanRequest request)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(jsonContent); }
        catch { yield break; }

        using (doc)
        {
            foreach (var (path, key, value) in FlattenJson(doc.RootElement, ""))
            {
                if (!IsSensitiveKey(key)) continue;

                var (rule, ruleTitle) = MapToConstitutionRule(WasmSecurityCategory.SecretsExposure);
                yield return new WasmSecurityFinding
                {
                    Id          = $"CFG-SECRET-{Slug(key)}",
                    Title       = $"Sensitive key '{key}' found in {source}",
                    Severity    = WasmSecuritySeverity.Critical,
                    Category    = WasmSecurityCategory.SecretsExposure,
                    Status      = WasmSecurityStatus.Fail,
                    Description = $"The configuration key '{key}' in {source} is publicly accessible in the browser. " +
                                  "Any value set for this key is exposed to all users.",
                    Recommendation = "Remove this key from client-side configuration. " +
                                     "Secrets must be stored server-side only and accessed via a secure backend API.",
                    Evidence    = [new WasmSecurityEvidence { Key = path, MaskedValue = MaskValue(value), Context = source }],
                    ConstitutionRule  = rule,
                    ConstitutionRuleTitle = ruleTitle,
                };
            }
        }
    }

    // ── Check: MSAL / OIDC configuration ──────────────────────────────────

    internal static IEnumerable<WasmSecurityFinding> CheckMsalConfig(
        string jsonContent, string source, WasmScanRequest request)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(jsonContent); }
        catch { yield break; }

        using (doc)
        {
            var root = doc.RootElement;

            // Look for AzureAd / MSAL / OIDC sections
            foreach (var sectionName in new[] { "AzureAd", "AzureAdB2C", "Authentication", "Oidc", "Msal" })
            {
                if (!root.TryGetProperty(sectionName, out var section)) continue;

                // ClientSecret must never appear
                foreach (var secretKey in new[] { "ClientSecret", "ClientPassword" })
                {
                    if (section.TryGetProperty(secretKey, out var secretEl))
                    {
                        var val = secretEl.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            yield return new WasmSecurityFinding
                            {
                                Id          = "MSAL-CLIENT-SECRET",
                                Title       = $"ClientSecret present in {sectionName} config ({source})",
                                Severity    = WasmSecuritySeverity.Critical,
                                Category    = WasmSecurityCategory.AuthenticationConfiguration,
                                Status      = WasmSecurityStatus.Fail,
                                Description = "A client secret was found in Blazor WASM client-side configuration. " +
                                              "Client secrets are confidential and must never be shipped to the browser.",
                                Recommendation = "Remove the ClientSecret from the Blazor WASM app entirely. " +
                                                 "Use PKCE (Proof Key for Code Exchange) for public clients. " +
                                                 "Never use confidential client flows in a browser app.",
                                Evidence    = [new WasmSecurityEvidence { Key = $"{sectionName}.{secretKey}", MaskedValue = MaskValue(val), Context = source }],
                                ConstitutionRule = "PS-02",
                                ConstitutionRuleTitle = "Client secrets must not be stored in browser-accessible configuration",
                            };
                        }
                    }
                }

                // Redirect URI: flag localhost in non-local deployment
                if (section.TryGetProperty("RedirectUri", out var redir))
                {
                    var uri = redir.GetString() ?? "";
                    if (uri.Contains("localhost", StringComparison.OrdinalIgnoreCase) &&
                        !request.TargetUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return new WasmSecurityFinding
                        {
                            Id          = "MSAL-LOCALHOST-REDIRECT",
                            Title       = $"Localhost RedirectUri in deployed {sectionName} config ({source})",
                            Severity    = WasmSecuritySeverity.High,
                            Category    = WasmSecurityCategory.AuthenticationConfiguration,
                            Status      = WasmSecurityStatus.Fail,
                            Description = "The redirect URI contains 'localhost', but the app is deployed to a non-local URL. " +
                                          "This may allow token redirection to a local attacker machine.",
                            Recommendation = "Remove localhost redirect URIs from production configuration and from the app registration.",
                            Evidence    = [new WasmSecurityEvidence { Key = $"{sectionName}.RedirectUri", MaskedValue = uri, Context = source }],
                        };
                    }
                }

                // Authority: flag if unexpected tenant
                if (!string.IsNullOrWhiteSpace(request.AllowedAuthority) &&
                    section.TryGetProperty("Authority", out var authority))
                {
                    var val = authority.GetString() ?? "";
                    if (!val.Contains(request.AllowedAuthority, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return new WasmSecurityFinding
                        {
                            Id          = "MSAL-UNEXPECTED-AUTHORITY",
                            Title       = $"Unexpected authority in {sectionName} config ({source})",
                            Severity    = WasmSecuritySeverity.High,
                            Category    = WasmSecurityCategory.AuthenticationConfiguration,
                            Status      = WasmSecurityStatus.Warning,
                            Description = $"The authority '{val}' does not match the expected authority '{request.AllowedAuthority}'.",
                            Recommendation = "Verify the authority matches the intended tenant and is not pointing to an unexpected directory.",
                            Evidence    = [new WasmSecurityEvidence { Key = $"{sectionName}.Authority", MaskedValue = val, Context = source }],
                        };
                    }
                }
            }
        }
    }

    // ── Check: Browser storage patterns ───────────────────────────────────

    internal static IEnumerable<WasmSecurityFinding> CheckBrowserStorage(
        string content, string source)
    {
        if (string.IsNullOrWhiteSpace(content)) yield break;

        var storageApis = new[]
        {
            "localStorage", "sessionStorage", "indexedDB", "document.cookie",
        };

        var tokenTerms = new[]
        {
            "token", "access_token", "id_token", "refresh_token", "bearer",
            "ssn", "fnr", "nationalId", "barnId", "kode6", "kode7",
        };

        bool hasStorage = storageApis.Any(api =>
            content.Contains(api, StringComparison.OrdinalIgnoreCase));

        if (!hasStorage) yield break;

        var foundStorage = storageApis
            .Where(api => content.Contains(api, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var foundTokens = tokenTerms
            .Where(term => content.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (foundTokens.Count == 0) yield break;

        var (rule, ruleTitle) = MapToConstitutionRule(WasmSecurityCategory.BrowserStorage);
        yield return new WasmSecurityFinding
        {
            Id          = $"STORAGE-TOKEN-{Slug(source)}",
            Title       = $"Potential token storage in browser storage API ({source})",
            Severity    = WasmSecuritySeverity.High,
            Category    = WasmSecurityCategory.BrowserStorage,
            Status      = WasmSecurityStatus.Warning,
            Description = $"Static analysis found usage of {string.Join(", ", foundStorage)} " +
                          $"co-located with token-related terms ({string.Join(", ", foundTokens.Take(5))}). " +
                          "This is a static indicator only — runtime verification required to confirm actual storage behavior.",
            Recommendation = "Avoid storing access tokens, ID tokens, or refresh tokens in localStorage or sessionStorage. " +
                             "Prefer in-memory storage or secure, HttpOnly cookies for sensitive tokens. " +
                             "Consider using MSAL's default in-memory token cache.",
            Evidence    = foundStorage.Select(s => new WasmSecurityEvidence { Key = s, MaskedValue = "pattern found", Context = source }).ToList(),
            ConstitutionRule      = rule,
            ConstitutionRuleTitle = ruleTitle,
        };
    }

    // ── Check: Sensitive data patterns ─────────────────────────────────────

    internal static IEnumerable<WasmSecurityFinding> CheckSensitiveData(
        string content, string source)
    {
        if (string.IsNullOrWhiteSpace(content)) yield break;

        // JWT-like strings
        if (JwtPattern().IsMatch(content))
        {
            yield return new WasmSecurityFinding
            {
                Id          = $"DATA-JWT-{Slug(source)}",
                Title       = $"JWT-like string found in {source}",
                Severity    = WasmSecuritySeverity.High,
                Category    = WasmSecurityCategory.SensitiveDataExposure,
                Status      = WasmSecurityStatus.Fail,
                Description = "A JWT-formatted string (three base64url segments separated by dots) was found in a publicly accessible file. " +
                              "This may be a hardcoded token, secret, or test credential.",
                Recommendation = "Remove any hardcoded tokens from client-side assets. Tokens must be obtained at runtime and never stored in source.",
                Evidence    = [new WasmSecurityEvidence { Key = "jwt-pattern", MaskedValue = "eyJ***.[masked]", Context = source }],
            };
        }

        // SAS tokens
        if (SasPattern().IsMatch(content))
        {
            yield return new WasmSecurityFinding
            {
                Id          = $"DATA-SAS-{Slug(source)}",
                Title       = $"SAS token pattern found in {source}",
                Severity    = WasmSecuritySeverity.Critical,
                Category    = WasmSecurityCategory.SensitiveDataExposure,
                Status      = WasmSecurityStatus.Fail,
                Description = "A Shared Access Signature (SAS) token pattern was found in a publicly accessible file. " +
                              "SAS tokens grant direct access to Azure storage resources.",
                Recommendation = "Remove SAS tokens from client-side assets. Generate short-lived SAS tokens server-side on demand.",
                Evidence    = [new WasmSecurityEvidence { Key = "sas-token", MaskedValue = "[SAS token — masked]", Context = source }],
            };
        }

        // Connection strings
        if (ConnectionStringPattern().IsMatch(content))
        {
            yield return new WasmSecurityFinding
            {
                Id          = $"DATA-CONNSTR-{Slug(source)}",
                Title       = $"Connection string pattern found in {source}",
                Severity    = WasmSecuritySeverity.Critical,
                Category    = WasmSecurityCategory.SensitiveDataExposure,
                Status      = WasmSecurityStatus.Fail,
                Description = "A connection string pattern was found in a publicly accessible file. " +
                              "Connection strings contain credentials granting direct database or service access.",
                Recommendation = "Remove all connection strings from client-side assets. " +
                                 "Backend services must never expose connection strings to the browser.",
                Evidence    = [new WasmSecurityEvidence { Key = "connection-string", MaskedValue = "[connection string — masked]", Context = source }],
            };
        }
    }

    // ── Check: Security headers ────────────────────────────────────────────

    internal static IEnumerable<WasmSecurityFinding> CheckSecurityHeaders(
        IReadOnlyDictionary<string, string> headers)
    {
        var checks = new (string Header, WasmSecuritySeverity Severity, string Impact)[]
        {
            ("Content-Security-Policy",   WasmSecuritySeverity.High,   "Without CSP, the app is vulnerable to XSS and data injection attacks."),
            ("X-Content-Type-Options",    WasmSecuritySeverity.Medium, "Without this header, browsers may MIME-sniff responses, enabling script injection."),
            ("Referrer-Policy",           WasmSecuritySeverity.Low,    "Without this header, sensitive URL parameters may leak to third parties via the Referer header."),
            ("Strict-Transport-Security", WasmSecuritySeverity.High,   "Without HSTS, users may be vulnerable to protocol downgrade attacks."),
            ("Permissions-Policy",        WasmSecuritySeverity.Low,    "Without Permissions-Policy, the app may unintentionally allow access to browser APIs (camera, microphone, etc.)."),
        };

        foreach (var (header, severity, impact) in checks)
        {
            if (headers.ContainsKey(header.ToLowerInvariant())) continue;

            yield return new WasmSecurityFinding
            {
                Id          = $"HDR-MISSING-{Slug(header)}",
                Title       = $"Missing security header: {header}",
                Severity    = severity,
                Category    = WasmSecurityCategory.SecurityHeaders,
                Status      = WasmSecurityStatus.Fail,
                Description = impact,
                Recommendation = $"Add the '{header}' response header to your hosting configuration or CDN.",
                ConstitutionRule  = "GL-26",
                ConstitutionRuleTitle = "Security headers must be set on all public-facing services",
            };
        }
    }

    // ── Check: CORS headers ────────────────────────────────────────────────

    internal static IEnumerable<WasmSecurityFinding> CheckCors(
        IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("access-control-allow-origin", out var acao)) yield break;

        if (acao.Trim() == "*")
        {
            var hasCredentials = headers.TryGetValue("access-control-allow-credentials", out var cred) &&
                                 cred.Equals("true", StringComparison.OrdinalIgnoreCase);

            yield return new WasmSecurityFinding
            {
                Id       = "CORS-WILDCARD",
                Title    = "Wildcard CORS Access-Control-Allow-Origin",
                Severity = hasCredentials ? WasmSecuritySeverity.Critical : WasmSecuritySeverity.Medium,
                Category = WasmSecurityCategory.CorsConfiguration,
                Status   = WasmSecurityStatus.Fail,
                Description = hasCredentials
                    ? "Access-Control-Allow-Origin: * is set with Access-Control-Allow-Credentials: true. " +
                      "This combination is invalid per the spec but may be misconfigured. It is a critical misconfiguration."
                    : "Access-Control-Allow-Origin: * allows any origin to read responses from this server.",
                Recommendation = hasCredentials
                    ? "Remove Access-Control-Allow-Credentials: true or replace the wildcard origin with an explicit origin list."
                    : "Replace the wildcard with an explicit list of trusted origins.",
                Evidence = [new WasmSecurityEvidence { Key = "Access-Control-Allow-Origin", MaskedValue = "*", Context = "response header" }],
            };
        }
    }

    // ── Check: blazor.boot.json ────────────────────────────────────────────

    internal static IEnumerable<WasmSecurityFinding> CheckBootJson(
        string content, string source)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(content); }
        catch { yield break; }

        using (doc)
        {
            var root = doc.RootElement;

            // Debug build flag
            if (root.TryGetProperty("debugBuild", out var debugBuild) &&
                debugBuild.ValueKind == JsonValueKind.True)
            {
                yield return new WasmSecurityFinding
                {
                    Id          = "BLAZOR-DEBUG-BUILD",
                    Title       = "Debug build detected in blazor.boot.json",
                    Severity    = WasmSecuritySeverity.High,
                    Category    = WasmSecurityCategory.DebugArtifactExposure,
                    Status      = WasmSecurityStatus.Fail,
                    Description = "The 'debugBuild' flag is true in blazor.boot.json. " +
                                  "Debug builds contain additional debug information and may expose internal implementation details.",
                    Recommendation = "Deploy only Release builds to production. Ensure the CI/CD pipeline uses -c Release for publish.",
                };
            }

            // PDB files
            if (root.TryGetProperty("resources", out var resources) &&
                resources.TryGetProperty("pdb", out var pdbs) &&
                pdbs.ValueKind == JsonValueKind.Object)
            {
                var pdbNames = pdbs.EnumerateObject().Select(p => p.Name).ToList();
                if (pdbNames.Count > 0)
                {
                    yield return new WasmSecurityFinding
                    {
                        Id          = "BLAZOR-PDB-EXPOSED",
                        Title       = $"{pdbNames.Count} PDB symbol file(s) listed in blazor.boot.json",
                        Severity    = WasmSecuritySeverity.Medium,
                        Category    = WasmSecurityCategory.DebugArtifactExposure,
                        Status      = WasmSecurityStatus.Fail,
                        Description = "PDB (Program Database) symbol files are listed in the boot manifest. " +
                                      "If downloadable, they expose method names, source file paths, and line numbers.",
                        Recommendation = "Remove PDB files from the published output. " +
                                         "Configure your publish profile to exclude symbol files from WASM output.",
                        Evidence    = pdbNames.Take(5).Select(n => new WasmSecurityEvidence { Key = "pdb", MaskedValue = n, Context = source }).ToList(),
                    };
                }
            }

            // Suspicious assembly names
            if (root.TryGetProperty("resources", out var res2) &&
                res2.TryGetProperty("assembly", out var assemblies) &&
                assemblies.ValueKind == JsonValueKind.Object)
            {
                var suspiciousTerms = new[] { "Admin", "Test", "Demo", "Internal", "Debug", "Dev.", "xunit", "Moq", "FluentAssertions", "Benchmark" };
                var suspicious = assemblies.EnumerateObject()
                    .Where(a => suspiciousTerms.Any(t => a.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    .Select(a => a.Name)
                    .Take(10)
                    .ToList();

                if (suspicious.Count > 0)
                {
                    yield return new WasmSecurityFinding
                    {
                        Id          = "BLAZOR-SUSPICIOUS-ASSEMBLIES",
                        Title       = "Suspicious assembly names found in blazor.boot.json",
                        Severity    = WasmSecuritySeverity.Medium,
                        Category    = WasmSecurityCategory.BlazorSpecific,
                        Status      = WasmSecurityStatus.Warning,
                        Description = "Assemblies with names suggesting internal tools, test infrastructure, or debug libraries " +
                                      "are included in the deployed WASM bundle.",
                        Recommendation = "Review the deployed assembly list. Remove test, demo, and internal-only assemblies from production builds. " +
                                         "Use linker trimming to reduce the assembly surface.",
                        Evidence    = suspicious.Select(n => new WasmSecurityEvidence { Key = "assembly", MaskedValue = n, Context = source }).ToList(),
                    };
                }
            }
        }
    }

    // ── Helpers: URL extraction + endpoint classification ─────────────────

    internal static IEnumerable<string> ExtractUrls(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) yield break;

        foreach (Match m in AbsoluteUrlPattern().Matches(content))
        {
            var url = m.Value.TrimEnd('.', ',', ';', '"', '\'', ')');
            if (url.Length < 10 || url.Length > 300) continue;
            yield return url;
        }
    }

    internal static IEnumerable<DiscoveredEndpoint> ClassifyEndpoints(
        IEnumerable<string> urls, WasmScanRequest request)
    {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DiscoveredEndpoint>();

        foreach (var url in urls)
        {
            if (!seen.Add(url)) continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;

            var cls = ClassifyUrl(uri, request);
            result.Add(new DiscoveredEndpoint { Url = url, Classification = cls, FoundIn = "scanned assets" });
        }

        return result;
    }

    internal static string ClassifyUrl(Uri uri, WasmScanRequest request)
    {
        var host = uri.Host.ToLowerInvariant();

        // Localhost/loopback checked before scheme — development URLs are always "Localhost"
        if (host == "localhost" || host == "127.0.0.1" || host == "::1") return "Localhost";

        if (uri.Scheme == "http") return "Insecure";

        if (PrivateIpPattern().IsMatch(host)) return "Internal";
        if (host.EndsWith(".local") || host.EndsWith(".internal") || host.Contains(".corp.")) return "Internal";

        if (request.AllowedBackendHostnames.Any(a =>
                host.Equals(a, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + a, StringComparison.OrdinalIgnoreCase)))
            return "Allowed";

        if (request.KnownSafeDomains.Any(d =>
                host.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)))
            return "Allowed";

        // CDN / well-known public safe patterns
        if (host.EndsWith(".azureedge.net") ||
            host.EndsWith(".cdn.net") ||
            host.EndsWith(".cloudfront.net") ||
            host.EndsWith("login.microsoftonline.com") ||
            host.EndsWith("graph.microsoft.com"))
            return "Allowed";

        // Direct Azure service URLs are suspicious (should go via gateway)
        if (host.EndsWith(".azurewebsites.net") ||
            host.EndsWith(".azure.com") ||
            host.EndsWith(".windows.net") ||
            host.EndsWith(".servicebus.windows.net") ||
            host.EndsWith(".blob.core.windows.net"))
            return "Suspicious";

        return "Unknown";
    }

    private static IEnumerable<WasmSecurityFinding> CheckBackendEndpoints(
        IReadOnlyList<DiscoveredEndpoint> endpoints, WasmScanRequest request)
    {
        var suspicious = endpoints.Where(e => e.Classification == "Suspicious").ToList();
        var localhost  = endpoints.Where(e => e.Classification == "Localhost").ToList();
        var insecure   = endpoints.Where(e => e.Classification == "Insecure").ToList();
        var internal_  = endpoints.Where(e => e.Classification == "Internal").ToList();

        if (suspicious.Count > 0)
        {
            var (rule, ruleTitle) = MapToConstitutionRule(WasmSecurityCategory.BackendEndpointExposure);
            yield return new WasmSecurityFinding
            {
                Id          = "ENDPOINT-DIRECT-BACKEND",
                Title       = $"{suspicious.Count} direct backend URL(s) found in client assets",
                Severity    = WasmSecuritySeverity.High,
                Category    = WasmSecurityCategory.BackendEndpointExposure,
                Status      = WasmSecurityStatus.Fail,
                Description = "Direct service URLs (Azure services, direct APIs) are embedded in client-side assets. " +
                              "Backend URLs should be hidden behind a gateway or proxy.",
                Recommendation = "Route all backend calls through an API gateway or reverse proxy. " +
                                 "Replace direct service URLs with relative paths or gateway-routed paths.",
                Evidence    = suspicious.Take(5).Select(e => new WasmSecurityEvidence { Key = "url", MaskedValue = e.Url, Context = e.FoundIn }).ToList(),
                ConstitutionRule      = rule,
                ConstitutionRuleTitle = ruleTitle,
            };
        }

        if (localhost.Count > 0)
        {
            yield return new WasmSecurityFinding
            {
                Id          = "ENDPOINT-LOCALHOST",
                Title       = $"{localhost.Count} localhost URL(s) found in client assets",
                Severity    = WasmSecuritySeverity.High,
                Category    = WasmSecurityCategory.DevelopmentArtifact,
                Status      = WasmSecurityStatus.Fail,
                Description = "Localhost URLs are embedded in client-side assets. " +
                              "These are development artifacts that should not be present in deployed builds.",
                Recommendation = "Remove localhost URLs from deployed configuration. " +
                                 "Use environment-specific appsettings or build-time replacement.",
                Evidence    = localhost.Take(5).Select(e => new WasmSecurityEvidence { Key = "url", MaskedValue = e.Url, Context = e.FoundIn }).ToList(),
            };
        }

        if (insecure.Count > 0)
        {
            yield return new WasmSecurityFinding
            {
                Id          = "ENDPOINT-HTTP",
                Title       = $"{insecure.Count} insecure http:// URL(s) found",
                Severity    = WasmSecuritySeverity.High,
                Category    = WasmSecurityCategory.BackendEndpointExposure,
                Status      = WasmSecurityStatus.Fail,
                Description = "HTTP (non-TLS) URLs are referenced in client-side assets. " +
                              "Calls over HTTP are unencrypted and vulnerable to interception.",
                Recommendation = "Replace all http:// URLs with https://. " +
                                 "Enforce TLS on all backend services.",
                Evidence    = insecure.Take(5).Select(e => new WasmSecurityEvidence { Key = "url", MaskedValue = e.Url, Context = e.FoundIn }).ToList(),
            };
        }

        if (internal_.Count > 0)
        {
            yield return new WasmSecurityFinding
            {
                Id          = "ENDPOINT-INTERNAL",
                Title       = $"{internal_.Count} internal hostname(s) exposed in client assets",
                Severity    = WasmSecuritySeverity.Medium,
                Category    = WasmSecurityCategory.BackendEndpointExposure,
                Status      = WasmSecurityStatus.Warning,
                Description = "Internal hostnames or private IP addresses are referenced in client-side assets. " +
                              "This exposes internal network topology to the browser.",
                Recommendation = "Remove internal hostnames from client-side assets. " +
                                 "All backend access must go through public-facing gateways.",
                Evidence    = internal_.Take(5).Select(e => new WasmSecurityEvidence { Key = "url", MaskedValue = e.Url, Context = e.FoundIn }).ToList(),
            };
        }
    }

    // ── Helpers: source map checks ─────────────────────────────────────────

    private static WasmSecurityFinding SourceMapFinding(string mapUrl) =>
        new()
        {
            Id          = $"SRCMAP-EXPOSED-{Slug(mapUrl)}",
            Title       = $"Source map file publicly accessible: {mapUrl}",
            Severity    = WasmSecuritySeverity.Medium,
            Category    = WasmSecurityCategory.SourceMapExposure,
            Status      = WasmSecurityStatus.Fail,
            Description = "A JavaScript source map file is publicly accessible. " +
                          "Source maps expose original TypeScript/C# source code paths and can reveal internal structure.",
            Recommendation = "Remove .map files from production deployments or restrict access via CDN/hosting rules.",
            Evidence    = [new WasmSecurityEvidence { Key = "source-map", MaskedValue = mapUrl, Context = "HTTP 200" }],
        };

    private static IEnumerable<WasmSecurityFinding> CheckSourceMappingUrl(string jsContent)
    {
        if (jsContent.Contains("sourceMappingURL", StringComparison.OrdinalIgnoreCase))
        {
            yield return new WasmSecurityFinding
            {
                Id          = "SRCMAP-REFERENCE",
                Title       = "sourceMappingURL comment found in blazor.webassembly.js",
                Severity    = WasmSecuritySeverity.Low,
                Category    = WasmSecurityCategory.SourceMapExposure,
                Status      = WasmSecurityStatus.Warning,
                Description = "The deployed JavaScript file contains a sourceMappingURL comment pointing to a source map.",
                Recommendation = "Remove source map references from production builds, or verify the .map file is not publicly accessible.",
            };
        }
    }

    private static WasmSecurityFinding DevelopmentConfigFinding(string path) =>
        new()
        {
            Id          = "CFG-DEV-EXPOSED",
            Title       = $"Development configuration file publicly accessible: {path}",
            Severity    = WasmSecuritySeverity.High,
            Category    = WasmSecurityCategory.DevelopmentArtifact,
            Status      = WasmSecurityStatus.Fail,
            Description = "An appsettings.Development.json file is publicly accessible in the deployed app. " +
                          "This file is intended for local development only and may contain development-only secrets or debug settings.",
            Recommendation = "Remove appsettings.Development.json from the publish output by configuring the .csproj to exclude it.",
        };

    private static WasmSecurityFinding ConfigExposedFinding(string url, string path) =>
        new()
        {
            Id          = $"CFG-EXPOSED-{Slug(path)}",
            Title       = $"Client configuration file publicly accessible: {path}",
            Severity    = WasmSecuritySeverity.Medium,
            Category    = WasmSecurityCategory.ConfigurationExposure,
            Status      = WasmSecurityStatus.Warning,
            Description = $"The file '{path}' is publicly accessible from the deployed application. " +
                          "While this is expected for Blazor WASM apps, all values in this file are visible to every user and must not contain secrets.",
            Recommendation = "Audit the contents of this file. Remove all secret values. " +
                             "Only include configuration that is safe to expose publicly.",
            Evidence    = [new WasmSecurityEvidence { Key = "url", MaskedValue = url, Context = "HTTP 200" }],
        };

    // ── Build report helpers ───────────────────────────────────────────────

    private static List<ConfigurationEntry> BuildConfigSummary(
        IReadOnlyDictionary<string, string> fetched,
        IReadOnlyList<WasmSecurityFinding> findings)
    {
        var result = new List<ConfigurationEntry>();
        var secretFindings = findings
            .Where(f => f.Category == WasmSecurityCategory.SecretsExposure)
            .SelectMany(f => f.Evidence)
            .ToDictionary(e => e.Key, e => (e.MaskedValue, Severity: WasmSecuritySeverity.Critical),
                          StringComparer.OrdinalIgnoreCase);

        foreach (var path in new[] { "appsettings.json", "appsettings.Production.json" })
        {
            if (!fetched.TryGetValue(path, out var content)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(content); }
            catch { continue; }

            using (doc)
            {
                foreach (var (jsonPath, key, value) in FlattenJson(doc.RootElement, "").Take(50))
                {
                    var hasFinding = secretFindings.TryGetValue(jsonPath, out var sf);
                    result.Add(new ConfigurationEntry
                    {
                        Key            = jsonPath,
                        MaskedValue    = hasFinding ? sf.MaskedValue : TruncateValue(value),
                        HasFinding     = hasFinding,
                        FindingSeverity = hasFinding ? sf.Severity : null,
                    });
                }
            }
        }

        return result;
    }

    private static List<SecurityHeaderResult> BuildHeaderResults(
        IReadOnlyDictionary<string, string> headers)
    {
        var checks = new (string Header, string Rec)[]
        {
            ("Content-Security-Policy",   "Add a CSP to restrict content sources."),
            ("X-Content-Type-Options",    "Add 'nosniff' to prevent MIME-sniffing."),
            ("Referrer-Policy",           "Add 'strict-origin-when-cross-origin' or stricter."),
            ("Strict-Transport-Security", "Add HSTS with at least 1 year max-age."),
            ("Permissions-Policy",        "Add to restrict access to browser APIs."),
            ("X-Frame-Options",           "Add 'DENY' or 'SAMEORIGIN', or use CSP frame-ancestors."),
        };

        return checks.Select(c =>
        {
            var key = c.Header.ToLowerInvariant();
            headers.TryGetValue(key, out var value);
            return new SecurityHeaderResult
            {
                Header         = c.Header,
                Status         = value is not null ? "Present" : "Missing",
                Value          = value is not null ? TruncateValue(value) : null,
                Recommendation = c.Rec,
            };
        }).ToList();
    }

    private static List<string> GenerateRecommendations(IReadOnlyList<WasmSecurityFinding> findings)
    {
        var recs = new HashSet<string>();

        foreach (var f in findings.Where(f => f.Status == WasmSecurityStatus.Fail || f.Status == WasmSecurityStatus.Warning))
            recs.Add(f.Recommendation);

        // Always include baseline reminders
        recs.Add("Regularly audit appsettings.json for secrets that should not be in client-side configuration.");
        recs.Add("Use an API gateway or reverse proxy for all backend API calls — never expose direct service URLs.");

        return [.. recs];
    }

    private static int CalculateScore(IReadOnlyList<WasmSecurityFinding> findings)
    {
        var score = 100;
        score -= findings.Count(f => f.Severity == WasmSecuritySeverity.Critical) * 25;
        score -= findings.Count(f => f.Severity == WasmSecuritySeverity.High) * 15;
        score -= findings.Count(f => f.Severity == WasmSecuritySeverity.Medium) * 7;
        score -= findings.Count(f => f.Severity == WasmSecuritySeverity.Low) * 3;
        return Math.Max(0, score);
    }

    private static List<WasmSecurityFinding> Deduplicate(List<WasmSecurityFinding> findings) =>
        findings.GroupBy(f => f.Id).Select(g => g.First()).ToList();

    private static List<string> Limitations() =>
    [
        "This scanner does not prove backend authorization correctness.",
        "Authenticated user flows are not tested in this version (v1).",
        "Runtime browser storage findings are static indicators — they require browser automation to confirm at runtime.",
        "This scanner does not replace OWASP ZAP, Burp Suite, or penetration testing.",
        "Only text assets are downloaded and analyzed. Compiled WASM binaries are not decompiled.",
        "Dynamic JavaScript execution is not performed.",
        "Path brute-forcing is not performed.",
    ];

    // ── Fetch helpers ──────────────────────────────────────────────────────

    private async Task<(string? Content, IEnumerable<(string Name, IEnumerable<string> Values)> Headers, string Status)>
        FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            var headers = response.Headers.Concat(response.Content.Headers)
                .Select(h => (h.Key, h.Value));

            if (!response.IsSuccessStatusCode)
                return (null, headers, response.StatusCode.ToString());

            var bytes  = await response.Content.ReadAsByteArrayAsync(ct);
            var content = bytes.Length <= MaxAssetBytes
                ? System.Text.Encoding.UTF8.GetString(bytes)
                : System.Text.Encoding.UTF8.GetString(bytes, 0, MaxAssetBytes) + "[truncated]";

            return (content, headers, "200 OK");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug("Asset fetch failed for {Url}: {Message}", url, ex.Message);
            return (null, [], "Error");
        }
        catch (TaskCanceledException)
        {
            return (null, [], "Timeout");
        }
    }

    private async Task<(string Path, string? Content, string Status, long? Size)>
        FetchAndRecord(string baseUrl, string path, CancellationToken ct)
    {
        var url = Url(baseUrl, path);
        var (content, _, status) = await FetchAsync(url, ct);
        return (path, content, status, content?.Length);
    }

    // ── Static utilities ───────────────────────────────────────────────────

    internal static bool IsSensitiveKey(string key)
    {
        var lower = key.ToLowerInvariant();
        return lower.Contains("password")         ||
               lower.Contains("secret")           ||
               lower.Contains("apikey")           ||
               lower.Contains("api_key")          ||
               lower.Contains("clientsecret")     ||
               lower.Contains("client_secret")    ||
               lower.Contains("connectionstring") ||
               lower.Contains("connection_string") ||
               lower.Contains("sharedaccesskey")  ||
               lower.Contains("shared_access_key") ||
               lower.Contains("storagekey")       ||
               lower.Contains("storage_key")      ||
               lower.Contains("instrumentationkey") ||
               lower.Contains("instrumentation_key") ||
               lower.Contains("privatekey")       ||
               lower.Contains("private_key")      ||
               lower.Contains("bearertoken")      ||
               lower.Contains("bearer_token")     ||
               lower.Contains("accesstoken")      ||
               lower.Contains("access_token")     ||
               lower.Contains("refreshtoken")     ||
               lower.Contains("refresh_token");
    }

    internal static string MaskValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Length <= 4) return "****";
        return value[..3] + new string('*', Math.Min(value.Length - 3, 20));
    }

    internal static (string? Rule, string? Title) MapToConstitutionRule(WasmSecurityCategory category) =>
        category switch
        {
            WasmSecurityCategory.SecretsExposure          => ("PS-02", "Secrets must not be stored in client-accessible configuration"),
            WasmSecurityCategory.BackendEndpointExposure  => ("GL-01", "All backend access must go through a reverse proxy or API gateway"),
            WasmSecurityCategory.AuthenticationConfiguration => ("PP-02", "Zero-trust: clients must not hold confidential credentials"),
            WasmSecurityCategory.BrowserStorage           => ("GL-26", "Tokens and sensitive data must not be stored in browser-accessible storage"),
            WasmSecurityCategory.SecurityHeaders          => ("GL-26", "Security headers must be set on all public-facing services"),
            _                                             => (null, null),
        };

    private static IEnumerable<(string Path, string Key, string Value)>
        FlattenJson(JsonElement el, string prefix)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    var path = prefix.Length == 0 ? prop.Name : $"{prefix}.{prop.Name}";
                    foreach (var item in FlattenJson(prop.Value, path))
                        yield return item;
                }
                break;
            case JsonValueKind.String:
                yield return (prefix, LastSegment(prefix), el.GetString() ?? "");
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Number:
                yield return (prefix, LastSegment(prefix), el.ToString());
                break;
        }
    }

    private static string LastSegment(string path) =>
        path.Contains('.') ? path[(path.LastIndexOf('.') + 1)..] : path;

    private static string TruncateValue(string value) =>
        value.Length > 80 ? value[..80] + "…" : value;

    private static bool DetectBlazorWasm(string html) =>
        html.Contains("_framework/blazor.webassembly.js", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("blazor.boot.json", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeBase(string url)
    {
        url = url.Trim();
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        return url.TrimEnd('/');
    }

    private static string Url(string base_, string path) => $"{base_}/{path.TrimStart('/')}";

    private static string Slug(string s) =>
        SlugPattern().Replace(s.ToUpperInvariant(), "-").Trim('-')[..Math.Min(s.Length, 30)];

    private static string ClassifyAssetType(string path) =>
        path.EndsWith(".json") ? "JSON" :
        path.EndsWith(".js")   ? "JavaScript" :
        path.EndsWith(".map")  ? "SourceMap" :
        path.EndsWith(".wasm") ? "WASM" :
        path.EndsWith(".dll")  ? "Assembly" : "Text";

    private static WasmDiscoveredAsset Asset(string url, string type, string status, long? size) =>
        new() { Url = url, AssetType = type, Status = status, SizeBytes = size, Analyzed = status == "200 OK" };

    private static IReadOnlyDictionary<string, string> BuildHeaderDict(
        IEnumerable<(string Name, IEnumerable<string> Values)> headers)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in headers)
            dict.TryAdd(name.ToLowerInvariant(), string.Join(", ", values));
        return dict;
    }

    // ── Compiled regexes ───────────────────────────────────────────────────

    [GeneratedRegex(@"https?://[a-zA-Z0-9\-._~:/?#\[\]@!$&'()*+,;=%]+", RegexOptions.Compiled)]
    private static partial Regex AbsoluteUrlPattern();

    [GeneratedRegex(@"eyJ[A-Za-z0-9\-_=]+\.eyJ[A-Za-z0-9\-_=]+\.[A-Za-z0-9\-_=+/]+", RegexOptions.Compiled)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"(sv=\d{4}|sig=[A-Za-z0-9%+/=]{20,}|SharedAccessSignature\s)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex SasPattern();

    [GeneratedRegex(@"(Server\s*=\s*|Data\s+Source\s*=\s*|AccountKey\s*=\s*|Endpoint\s*=\s*sb://)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringPattern();

    [GeneratedRegex(@"^(10\.\d+\.\d+\.\d+|172\.(1[6-9]|2\d|3[01])\.\d+\.\d+|192\.168\.\d+\.\d+)$", RegexOptions.Compiled)]
    private static partial Regex PrivateIpPattern();

    [GeneratedRegex(@"[^A-Z0-9]+", RegexOptions.Compiled)]
    private static partial Regex SlugPattern();
}
