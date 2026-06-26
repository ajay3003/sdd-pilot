using BirkNext.Api.Services.WasmSecurity;
using FluentAssertions;

namespace BirkNext.Api.Tests.Unit.WasmSecurity;

public class WasmSecurityServiceTests
{
    private static WasmScanRequest DefaultRequest(string url = "https://myapp.example.com") =>
        new() { TargetUrl = url };

    // ── appsettings exposure detection ─────────────────────────────────────

    [Fact]
    public void CheckConfigKeys_PlainConfig_NoFindings()
    {
        const string json = """{"ApiBaseUrl":"https://api.example.com","Theme":"dark"}""";
        var findings = BlazorWasmSecurityReviewService
            .CheckConfigKeys(json, "appsettings.json", DefaultRequest()).ToList();
        findings.Should().BeEmpty();
    }

    [Fact]
    public void CheckConfigKeys_ClientSecret_CriticalFinding()
    {
        const string json = """{"AzureAd":{"ClientId":"abc","ClientSecret":"super-secret-value"}}""";
        var findings = BlazorWasmSecurityReviewService
            .CheckConfigKeys(json, "appsettings.json", DefaultRequest()).ToList();

        findings.Should().ContainSingle(f =>
            f.Severity == WasmSecuritySeverity.Critical &&
            f.Category == WasmSecurityCategory.SecretsExposure);
    }

    [Fact]
    public void CheckConfigKeys_Password_CriticalFinding()
    {
        const string json = """{"Database":{"Password":"db-password-123"}}""";
        var findings = BlazorWasmSecurityReviewService
            .CheckConfigKeys(json, "appsettings.json", DefaultRequest()).ToList();

        findings.Should().ContainSingle(f => f.Severity == WasmSecuritySeverity.Critical);
    }

    [Fact]
    public void CheckConfigKeys_InstrumentationKey_CriticalFinding()
    {
        const string json = """{"ApplicationInsights":{"InstrumentationKey":"abc123-xxx-yyy"}}""";
        var findings = BlazorWasmSecurityReviewService
            .CheckConfigKeys(json, "appsettings.json", DefaultRequest()).ToList();

        findings.Should().ContainSingle(f => f.Severity == WasmSecuritySeverity.Critical);
    }

    [Fact]
    public void CheckConfigKeys_MultipleSecrets_MultipleFindings()
    {
        const string json = """{"Password":"p1","ConnectionString":"cs1","ApiKey":"key1"}""";
        var findings = BlazorWasmSecurityReviewService
            .CheckConfigKeys(json, "appsettings.json", DefaultRequest()).ToList();

        findings.Should().HaveCount(3);
        findings.Should().AllSatisfy(f => f.Severity.Should().Be(WasmSecuritySeverity.Critical));
    }

    [Fact]
    public void CheckConfigKeys_InvalidJson_NoFindings()
    {
        var findings = BlazorWasmSecurityReviewService
            .CheckConfigKeys("not json", "appsettings.json", DefaultRequest()).ToList();
        findings.Should().BeEmpty();
    }

    // ── Secret key detection with masking ──────────────────────────────────

    [Theory]
    [InlineData("clientsecret")]
    [InlineData("ClientSecret")]
    [InlineData("CLIENTSECRET")]
    [InlineData("password")]
    [InlineData("apikey")]
    [InlineData("api_key")]
    [InlineData("connectionstring")]
    [InlineData("sharedaccesskey")]
    [InlineData("instrumentationkey")]
    [InlineData("privatekey")]
    [InlineData("accesstoken")]
    [InlineData("refreshtoken")]
    public void IsSensitiveKey_SensitiveKeys_ReturnsTrue(string key)
    {
        BlazorWasmSecurityReviewService.IsSensitiveKey(key).Should().BeTrue();
    }

    [Theory]
    [InlineData("ApiBaseUrl")]
    [InlineData("Theme")]
    [InlineData("Environment")]
    [InlineData("ClientId")]
    [InlineData("TenantId")]
    public void IsSensitiveKey_SafeKeys_ReturnsFalse(string key)
    {
        BlazorWasmSecurityReviewService.IsSensitiveKey(key).Should().BeFalse();
    }

    [Fact]
    public void MaskValue_ShortValue_MaskedFully()
    {
        BlazorWasmSecurityReviewService.MaskValue("ab").Should().Be("****");
    }

    [Fact]
    public void MaskValue_LongSecret_ShowsFirstThreeChars()
    {
        var masked = BlazorWasmSecurityReviewService.MaskValue("super-secret-value");
        masked.Should().StartWith("sup");
        masked.Should().Contain("*");
        masked.Should().NotContain("secret");
    }

    [Fact]
    public void MaskValue_Empty_ReturnsEmpty()
    {
        BlazorWasmSecurityReviewService.MaskValue("").Should().Be("");
    }

    // ── Direct backend URL detection ───────────────────────────────────────

    [Fact]
    public void ClassifyUrl_AzureWebsites_IsSuspicious()
    {
        var uri = new Uri("https://myapi.azurewebsites.net/api/data");
        var cls = BlazorWasmSecurityReviewService.ClassifyUrl(uri, DefaultRequest());
        cls.Should().Be("Suspicious");
    }

    [Fact]
    public void ClassifyUrl_AllowedHostname_IsAllowed()
    {
        var request = new WasmScanRequest
        {
            TargetUrl = "https://myapp.example.com",
            AllowedBackendHostnames = ["api.myapp.example.com"],
        };
        var uri = new Uri("https://api.myapp.example.com/v1/resource");
        BlazorWasmSecurityReviewService.ClassifyUrl(uri, request).Should().Be("Allowed");
    }

    [Fact]
    public void ClassifyUrl_MicrosoftLoginDomain_IsAllowed()
    {
        var uri = new Uri("https://login.microsoftonline.com/tenant-id/v2.0");
        BlazorWasmSecurityReviewService.ClassifyUrl(uri, DefaultRequest()).Should().Be("Allowed");
    }

    [Fact]
    public void ClassifyUrl_ServiceBusUrl_IsSuspicious()
    {
        var uri = new Uri("https://mybus.servicebus.windows.net");
        BlazorWasmSecurityReviewService.ClassifyUrl(uri, DefaultRequest()).Should().Be("Suspicious");
    }

    // ── Localhost URL detection ─────────────────────────────────────────────

    [Theory]
    [InlineData("http://localhost:5000/api")]
    [InlineData("https://localhost:7001/graphql")]
    [InlineData("http://127.0.0.1:3000")]
    public void ClassifyUrl_LocalhostUrls_ClassifiedAsLocalhost(string url)
    {
        var uri = new Uri(url);
        BlazorWasmSecurityReviewService.ClassifyUrl(uri, DefaultRequest()).Should().Be("Localhost");
    }

    [Fact]
    public void ClassifyUrl_HttpNotHttps_IsInsecure()
    {
        var uri = new Uri("http://api.example.com/endpoint");
        BlazorWasmSecurityReviewService.ClassifyUrl(uri, DefaultRequest()).Should().Be("Insecure");
    }

    // ── Source map detection ───────────────────────────────────────────────

    [Fact]
    public void ExtractUrls_FindsAbsoluteUrls()
    {
        const string content = """
            var apiUrl = "https://api.example.com/v1";
            var graphql = "https://api.example.com/graphql";
            """;

        var urls = BlazorWasmSecurityReviewService.ExtractUrls(content).ToList();
        urls.Should().Contain("https://api.example.com/v1");
        urls.Should().Contain("https://api.example.com/graphql");
    }

    [Fact]
    public void ExtractUrls_NoUrls_ReturnsEmpty()
    {
        var urls = BlazorWasmSecurityReviewService.ExtractUrls("no urls here").ToList();
        urls.Should().BeEmpty();
    }

    // ── Missing security headers ───────────────────────────────────────────

    [Fact]
    public void CheckSecurityHeaders_MissingCsp_HighFinding()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Content-Type-Options"] = "nosniff",
        };

        var findings = BlazorWasmSecurityReviewService.CheckSecurityHeaders(headers).ToList();
        findings.Should().Contain(f =>
            f.Id == "HDR-MISSING-CONTENT-SECURITY-POLICY" &&
            f.Severity == WasmSecuritySeverity.High);
    }

    [Fact]
    public void CheckSecurityHeaders_MissingHsts_HighFinding()
    {
        var findings = BlazorWasmSecurityReviewService
            .CheckSecurityHeaders(new Dictionary<string, string>()).ToList();

        findings.Should().Contain(f =>
            f.Id.Contains("STRICT-TRANSPORT") &&
            f.Severity == WasmSecuritySeverity.High);
    }

    [Fact]
    public void CheckSecurityHeaders_AllPresent_NoFindings()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-security-policy"]   = "default-src 'self'",
            ["x-content-type-options"]    = "nosniff",
            ["referrer-policy"]           = "strict-origin-when-cross-origin",
            ["strict-transport-security"] = "max-age=31536000",
            ["permissions-policy"]        = "camera=()",
        };

        var findings = BlazorWasmSecurityReviewService.CheckSecurityHeaders(headers).ToList();
        findings.Should().BeEmpty();
    }

    // ── Wildcard CORS detection ────────────────────────────────────────────

    [Fact]
    public void CheckCors_WildcardOrigin_MediumFinding()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["access-control-allow-origin"] = "*",
        };

        var findings = BlazorWasmSecurityReviewService.CheckCors(headers).ToList();
        findings.Should().ContainSingle(f =>
            f.Id == "CORS-WILDCARD" &&
            f.Severity == WasmSecuritySeverity.Medium);
    }

    [Fact]
    public void CheckCors_WildcardWithCredentials_CriticalFinding()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["access-control-allow-origin"]      = "*",
            ["access-control-allow-credentials"] = "true",
        };

        var findings = BlazorWasmSecurityReviewService.CheckCors(headers).ToList();
        findings.Should().ContainSingle(f =>
            f.Severity == WasmSecuritySeverity.Critical);
    }

    [Fact]
    public void CheckCors_SpecificOrigin_NoFindings()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["access-control-allow-origin"] = "https://myapp.example.com",
        };

        BlazorWasmSecurityReviewService.CheckCors(headers).Should().BeEmpty();
    }

    // ── MSAL clientSecret detection ────────────────────────────────────────

    [Fact]
    public void CheckMsalConfig_ClientSecretPresent_CriticalFinding()
    {
        const string json = """
            {
              "AzureAd": {
                "ClientId": "abc-123",
                "ClientSecret": "shhh-dont-tell"
              }
            }
            """;

        var findings = BlazorWasmSecurityReviewService
            .CheckMsalConfig(json, "appsettings.json", DefaultRequest()).ToList();

        findings.Should().ContainSingle(f =>
            f.Id == "MSAL-CLIENT-SECRET" &&
            f.Severity == WasmSecuritySeverity.Critical);
    }

    [Fact]
    public void CheckMsalConfig_LocalhostRedirectInDeployedApp_HighFinding()
    {
        const string json = """
            {
              "AzureAd": {
                "ClientId": "abc-123",
                "RedirectUri": "http://localhost:5173/auth"
              }
            }
            """;

        var request = new WasmScanRequest { TargetUrl = "https://myapp.azure.com" };
        var findings = BlazorWasmSecurityReviewService
            .CheckMsalConfig(json, "appsettings.json", request).ToList();

        findings.Should().ContainSingle(f =>
            f.Id == "MSAL-LOCALHOST-REDIRECT" &&
            f.Severity == WasmSecuritySeverity.High);
    }

    [Fact]
    public void CheckMsalConfig_LocalhostRedirectInLocalApp_NoFinding()
    {
        const string json = """{"AzureAd":{"ClientId":"abc","RedirectUri":"http://localhost:5173"}}""";

        var request = new WasmScanRequest { TargetUrl = "http://localhost:5173" };
        var findings = BlazorWasmSecurityReviewService
            .CheckMsalConfig(json, "appsettings.json", request).ToList();

        findings.Should().NotContain(f => f.Id == "MSAL-LOCALHOST-REDIRECT");
    }

    [Fact]
    public void CheckMsalConfig_UnexpectedAuthority_HighFinding()
    {
        const string json = """{"AzureAd":{"Authority":"https://login.microsoftonline.com/other-tenant"}}""";

        var request = new WasmScanRequest
        {
            TargetUrl        = "https://app.example.com",
            AllowedAuthority = "expected-tenant",
        };

        var findings = BlazorWasmSecurityReviewService
            .CheckMsalConfig(json, "appsettings.json", request).ToList();

        findings.Should().ContainSingle(f =>
            f.Id == "MSAL-UNEXPECTED-AUTHORITY" &&
            f.Severity == WasmSecuritySeverity.High);
    }

    // ── localStorage token pattern detection ──────────────────────────────

    [Fact]
    public void CheckBrowserStorage_LocalStorageWithToken_HighFinding()
    {
        const string js = "localStorage.setItem('access_token', token);";
        var findings = BlazorWasmSecurityReviewService
            .CheckBrowserStorage(js, "app.js").ToList();

        findings.Should().ContainSingle(f =>
            f.Category == WasmSecurityCategory.BrowserStorage &&
            f.Severity == WasmSecuritySeverity.High);
    }

    [Fact]
    public void CheckBrowserStorage_LocalStorageWithoutTokenTerms_NoFinding()
    {
        const string js = "localStorage.setItem('theme', 'dark');";
        var findings = BlazorWasmSecurityReviewService
            .CheckBrowserStorage(js, "app.js").ToList();
        findings.Should().BeEmpty();
    }

    [Fact]
    public void CheckBrowserStorage_NoStorageApi_NoFinding()
    {
        const string js = "var token = getTokenFromApi();";
        var findings = BlazorWasmSecurityReviewService
            .CheckBrowserStorage(js, "app.js").ToList();
        findings.Should().BeEmpty();
    }

    // ── Endpoint classification ────────────────────────────────────────────

    [Fact]
    public void ClassifyEndpoints_MixedUrls_CorrectClassifications()
    {
        var urls = new[]
        {
            "https://api.myapp.example.com/v1",
            "https://myservice.azurewebsites.net/api",
            "http://localhost:5000/api",
            "http://unsecure.example.com",
        };

        var request = new WasmScanRequest
        {
            TargetUrl = "https://myapp.example.com",
            AllowedBackendHostnames = ["api.myapp.example.com"],
        };

        var endpoints = BlazorWasmSecurityReviewService
            .ClassifyEndpoints(urls, request).ToList();

        endpoints.Should().ContainSingle(e => e.Classification == "Allowed");
        endpoints.Should().ContainSingle(e => e.Classification == "Suspicious");
        endpoints.Should().ContainSingle(e => e.Classification == "Localhost");
        endpoints.Should().ContainSingle(e => e.Classification == "Insecure");
    }

    [Fact]
    public void ClassifyEndpoints_DuplicateUrls_Deduplicated()
    {
        var urls = new[] { "https://api.example.com", "https://api.example.com" };
        var endpoints = BlazorWasmSecurityReviewService
            .ClassifyEndpoints(urls, DefaultRequest()).ToList();
        endpoints.Should().HaveCount(1);
    }

    // ── Constitution rule mapping ──────────────────────────────────────────

    [Fact]
    public void MapToConstitutionRule_BackendEndpointExposure_ReturnsGL01()
    {
        var (rule, title) = BlazorWasmSecurityReviewService
            .MapToConstitutionRule(WasmSecurityCategory.BackendEndpointExposure);
        rule.Should().Be("GL-01");
        title.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void MapToConstitutionRule_SecretsExposure_ReturnsPS02()
    {
        var (rule, _) = BlazorWasmSecurityReviewService
            .MapToConstitutionRule(WasmSecurityCategory.SecretsExposure);
        rule.Should().Be("PS-02");
    }

    [Fact]
    public void MapToConstitutionRule_Authentication_ReturnsPP02()
    {
        var (rule, _) = BlazorWasmSecurityReviewService
            .MapToConstitutionRule(WasmSecurityCategory.AuthenticationConfiguration);
        rule.Should().Be("PP-02");
    }

    // ── Boot JSON checks ────────────────────────────────────────────────────

    [Fact]
    public void CheckBootJson_DebugBuildTrue_HighFinding()
    {
        const string json = """{"debugBuild":true,"resources":{}}""";
        var findings = BlazorWasmSecurityReviewService
            .CheckBootJson(json, "blazor.boot.json").ToList();

        findings.Should().ContainSingle(f =>
            f.Id == "BLAZOR-DEBUG-BUILD" &&
            f.Severity == WasmSecuritySeverity.High);
    }

    [Fact]
    public void CheckBootJson_PdbFiles_MediumFinding()
    {
        const string json = """
            {
              "debugBuild": false,
              "resources": {
                "pdb": {
                  "MyApp.pdb": "sha256-abc123"
                }
              }
            }
            """;

        var findings = BlazorWasmSecurityReviewService
            .CheckBootJson(json, "blazor.boot.json").ToList();

        findings.Should().ContainSingle(f =>
            f.Id == "BLAZOR-PDB-EXPOSED" &&
            f.Severity == WasmSecuritySeverity.Medium);
    }

    [Fact]
    public void CheckBootJson_SuspiciousAssemblies_MediumFinding()
    {
        const string json = """
            {
              "debugBuild": false,
              "resources": {
                "assembly": {
                  "MyApp.dll": "sha256-abc",
                  "xunit.core.dll": "sha256-def",
                  "Moq.dll": "sha256-ghi"
                }
              }
            }
            """;

        var findings = BlazorWasmSecurityReviewService
            .CheckBootJson(json, "blazor.boot.json").ToList();

        findings.Should().ContainSingle(f =>
            f.Id == "BLAZOR-SUSPICIOUS-ASSEMBLIES" &&
            f.Severity == WasmSecuritySeverity.Medium);
    }

    [Fact]
    public void CheckBootJson_CleanReleaseBuild_NoFindings()
    {
        const string json = """
            {
              "debugBuild": false,
              "resources": {
                "assembly": {
                  "MyApp.dll": "sha256-abc",
                  "Microsoft.AspNetCore.Components.dll": "sha256-def"
                }
              }
            }
            """;

        BlazorWasmSecurityReviewService
            .CheckBootJson(json, "blazor.boot.json").Should().BeEmpty();
    }
}
