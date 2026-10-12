using System.Text.RegularExpressions;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>Backend configuration for Altinn app testing (<c>AltinnTesting</c> section). Only trust and secrets live here; the app id, client id
/// and scopes stay in the Integration Quality Review Altinn test configuration.</summary>
public sealed class AltinnTestingOptions
{
    public const string Section = "AltinnTesting";
    /// <summary>Exact Altinn app ids ("org/app") this BirkNext instance may submit to in TT02.</summary>
    public List<string> ApprovedApps { get; set; } = [];
    public IdPortenTestOptions IdPortenTest { get; set; } = new();
}

public sealed class IdPortenTestOptions
{
    /// <summary>The backend callback registered with the ID-porten TEST client (…/api/altinn-testing/sign-in/callback).</summary>
    public string? RedirectUri { get; set; }
    /// <summary>Optional client secret (user secrets / environment only). Without it the client must be a public PKCE client.</summary>
    public string? ClientSecret { get; set; }
    /// <summary>Optional acr_values, e.g. idporten-loa-substantial. Not sent when empty.</summary>
    public string? AcrValues { get; set; }
}

/// <summary>
/// Every host, app and environment rule for Altinn app testing, enforced by the backend on readiness and again on every request it sends.
/// Only Altinn TT02 and ID-porten TEST are reachable; Production hosts are refused by name even if an allowlist were ever misconfigured.
/// Nothing here can be widened from a request.
/// </summary>
public sealed class AltinnTestingPolicy(AltinnTestingOptions options)
{
    public const string IdPortenTestIssuer = "https://test.idporten.no";
    public const string IdPortenTestDiscovery = "https://test.idporten.no/.well-known/openid-configuration";
    public const string AltinnTokenExchange = "https://platform.tt02.altinn.no/authentication/api/v1/exchange/id-porten";
    public const string CallbackPath = "/api/altinn-testing/sign-in/callback";
    /// <summary>Altinn Storage (TT02). Its GET instance is read-only; the app's own GET instance marks the instance read for person callers.</summary>
    public const string Tt02StorageInstances = "https://platform.tt02.altinn.no/storage/api/v1/instances/";

    /// <summary>The Storage read of one instance, built only from the fixed TT02 base, a positive numeric party id and a GUID.</summary>
    public static Uri StorageInstance(long instanceOwnerPartyId, Guid instanceGuid) =>
        instanceOwnerPartyId > 0 ? new Uri($"{Tt02StorageInstances}{instanceOwnerPartyId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{instanceGuid:D}")
            : throw new ArgumentOutOfRangeException(nameof(instanceOwnerPartyId));

    private static readonly string[] IdPortenTestHosts = ["test.idporten.no", "login.test.idporten.no"];
    private static readonly string[] ProductionHosts = ["idporten.no", "login.idporten.no", "altinn.no", "www.altinn.no", "platform.altinn.no"];
    private static readonly Regex AppIdPattern = new("^(?<org>[a-z0-9][a-z0-9-]{0,62})/(?<app>[a-z0-9][a-z0-9-]{0,62})$", RegexOptions.CultureInvariant);

    public AltinnTestingOptions Options => options;

    /// <summary>(org, app) when the id has the exact Altinn shape and is approved by backend configuration.</summary>
    public (string Org, string App)? ApprovedApp(string? appId, out string reason)
    {
        var normalized = appId?.Trim() ?? "";
        if (normalized.Length == 0) { reason = "No Altinn app id is configured in Integration Quality Review (Altinn test configuration → App identifier)."; return null; }
        var match = AppIdPattern.Match(normalized);
        if (!match.Success) { reason = $"\"{normalized}\" is not an Altinn app id of the form org/app."; return null; }
        if (!options.ApprovedApps.Any(item => string.Equals(item.Trim(), normalized, StringComparison.Ordinal)))
        {
            reason = $"The app {normalized} is not approved for this BirkNext instance (backend {AltinnTestingOptions.Section}:ApprovedApps).";
            return null;
        }
        reason = $"Approved app {normalized}.";
        return (match.Groups["org"].Value, match.Groups["app"].Value);
    }

    /// <summary>The TT02 base address of an approved app. Built only from the validated org/app — never from request input.</summary>
    public static Uri AppBase(string org, string app) => new($"https://{org}.apps.tt02.altinn.no/{org}/{app}/");

    /// <summary>Approves one outgoing request: https, an ID-porten TEST or Altinn TT02 host, no credentials in the URL, no production host.</summary>
    public static string? RequestBlock(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps) return "Only https requests are allowed.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "Credentials in a URL are not allowed.";
        if (uri.Port != 443) return "Only the default https port is allowed.";
        var host = uri.Host.ToLowerInvariant();
        if (ProductionHosts.Contains(host) || host.EndsWith(".apps.altinn.no", StringComparison.Ordinal)) return $"{host} is a production host and is never contacted.";
        if (IdPortenTestHosts.Contains(host) || host == "platform.tt02.altinn.no" || IsTt02AppHost(host)) return null;
        return $"{host} is not an approved ID-porten TEST or Altinn TT02 host.";
    }

    public static bool IsIdPortenTestHost(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && IdPortenTestHosts.Contains(uri.Host.ToLowerInvariant()) && uri.Port == 443;

    private static bool IsTt02AppHost(string host) =>
        host.EndsWith(".apps.tt02.altinn.no", StringComparison.Ordinal) && host.Count(c => c == '.') == 4 && AppIdPattern.IsMatch(host.Split('.')[0] + "/x");

    /// <summary>The Altinn environment configured in Integration Quality Review must be TT02 (Test) and Production must stay blocked.</summary>
    public static string? AltinnEnvironmentBlock(AltinnTestConfiguration config) =>
        config.Environment == ExternalTestEnvironment.Production ? "The Altinn environment is Production; Altinn app testing never runs against Production."
        : !config.ProductionBlocked ? "Production blocking is off in the Altinn test configuration."
        : config.Environment != ExternalTestEnvironment.Test ? $"The Altinn environment is {config.Environment}; Altinn app testing runs only in TT02 (Altinn test configuration → Environment = Test)."
        : null;

    /// <summary>The backend trusted environment that runs the test must be Development or QA without a production marker.</summary>
    public static string? TargetBlock(TrustedExecutionEnvironment? trusted, string trustDetail)
    {
        if (trusted is null) return trustDetail;
        if (!trusted.ExecutionAllowed) return "Trusted, but active execution is not allowed for this environment.";
        if (ActiveEventPolicy.EnvironmentBlock(trusted.EnvironmentType) is { } blocked) return blocked;
        if (Uri.TryCreate(trusted.TargetUrl, UriKind.Absolute, out var url) && ActiveEventPolicy.LooksLikeProduction(url.Host))
            return "The trusted environment's target host carries a production marker.";
        return null;
    }

    /// <summary>What is missing for ID-porten TEST sign-in, each problem on its own. Empty when sign-in can be offered.</summary>
    public IReadOnlyList<string> SignInProblems(AltinnTestConfiguration config)
    {
        var problems = new List<string>();
        if (config.AuthenticationType != ExternalAuthenticationType.IDPorten)
            problems.Add("Authentication type is not ID-porten (Integration Quality Review → Altinn test configuration → Authentication).");
        if (string.IsNullOrWhiteSpace(config.ClientReference))
            problems.Add("No ID-porten TEST client id is configured (Altinn test configuration → Client reference).");
        if (!config.ConfiguredScopes.Contains("openid", StringComparer.Ordinal))
            problems.Add("The configured scopes do not include openid (Altinn test configuration → Configured scopes).");
        if (!Uri.TryCreate(options.IdPortenTest.RedirectUri, UriKind.Absolute, out var redirect))
            problems.Add($"No redirect URI is configured for the ID-porten TEST client (backend {AltinnTestingOptions.Section}:IdPortenTest:RedirectUri).");
        else if (!string.Equals(redirect.AbsolutePath, CallbackPath, StringComparison.Ordinal) || (redirect.Scheme != Uri.UriSchemeHttps && !redirect.IsLoopback))
            problems.Add($"The redirect URI must be https (or loopback http) and end in {CallbackPath}.");
        return problems;
    }
}
