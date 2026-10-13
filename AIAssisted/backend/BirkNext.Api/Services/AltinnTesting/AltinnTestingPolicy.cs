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
    public UnattendedIdPortenTestOptions UnattendedIdPortenTest { get; set; } = new();
    /// <summary>
    /// Service-owner confirmations that an app's authorization policy (XACML: who may instantiate, write and complete, roles, authentication
    /// level, party representation) accepts the configured synthetic TEST reporter. Without a confirmation for the app, submission scenarios
    /// stay Not ready: BirkNext never infers an app policy.
    /// </summary>
    public List<AltinnAppPolicyConfirmation> ConfirmedAppPolicies { get; set; } = [];
}

/// <summary>
/// Unattended reporter authentication: ID-porten TEST's documented headless login for API testing (authorization code + PKCE with a
/// <c>testid:</c> login_hint, client_secret_basic), then the Altinn TT02 token exchange. TEST only. The client id and scopes stay in the
/// Integration Quality Review Altinn test configuration; only trust and secrets live here.
/// </summary>
public sealed class UnattendedIdPortenTestOptions
{
    public bool Enabled { get; set; }
    /// <summary>The redirect URI registered for the ID-porten TEST client. It is only compared with the redirect ID-porten answers; it is never
    /// requested. Falls back to <see cref="IdPortenTestOptions.RedirectUri"/>.</summary>
    public string? RedirectUri { get; set; }
    /// <summary>The client secret (environment variable or secret store only, never a committed file). Falls back to <see cref="IdPortenTestOptions.ClientSecret"/>.</summary>
    public string? ClientSecret { get; set; }
    /// <summary>Approved synthetic TEST reporters, selected in the UI by alias only.</summary>
    public List<SyntheticReporterOptions> Reporters { get; set; } = [];
}

/// <summary>One approved synthetic TestID reporter. The identifier is used only in the login_hint; the UI, logs and history see the alias.</summary>
public sealed class SyntheticReporterOptions
{
    /// <summary>Stable key, e.g. "reporter-a" (lowercase letters, digits and hyphens).</summary>
    public string Alias { get; set; } = "";
    /// <summary>Display name, e.g. "Skolenærvær reporter A".</summary>
    public string Label { get; set; } = "";
    /// <summary>The synthetic Test-Norge person identifier (month + 80) of the TestID user. Configuration outside committed files only.</summary>
    public string? TestIdPersonIdentifier { get; set; }
    /// <summary>The requested security level: idporten-loa-substantial or idporten-loa-high (sent as acr_values and in the login_hint).</summary>
    public string AcrValue { get; set; } = "idporten-loa-substantial";
    /// <summary>Only the TT02 test environment is ever allowed.</summary>
    public string AllowedEnvironment { get; set; } = "TT02";
}

public sealed class AltinnAppPolicyConfirmation
{
    public string AppId { get; set; } = "";
    /// <summary>Where the confirmation is recorded (ticket, mail, policy file version) — a reference, not the policy itself.</summary>
    public string Reference { get; set; } = "";
    public string ConfirmedBy { get; set; } = "";
    public DateOnly? ConfirmedOn { get; set; }
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

    /// <summary>The value the interactive (attended) fallback uses as reporter: the tester's own TestID sign-in held for their execution context.</summary>
    public const string InteractiveReporter = "interactive-session";
    public static readonly string[] AcrValues = ["idporten-loa-substantial", "idporten-loa-high"];
    private static readonly Regex AliasPattern = new("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);

    public string? UnattendedRedirectUri => string.IsNullOrWhiteSpace(options.UnattendedIdPortenTest.RedirectUri) ? options.IdPortenTest.RedirectUri : options.UnattendedIdPortenTest.RedirectUri;
    public bool UnattendedSecretAvailable => !string.IsNullOrEmpty(UnattendedClientSecret);
    internal string? UnattendedClientSecret => string.IsNullOrEmpty(options.UnattendedIdPortenTest.ClientSecret) ? options.IdPortenTest.ClientSecret : options.UnattendedIdPortenTest.ClientSecret;

    /// <summary>The approved reporter with this alias, or null with the reason. Its identifier never appears in the reason.</summary>
    public SyntheticReporterOptions? Reporter(string? alias, out string reason)
    {
        var matches = options.UnattendedIdPortenTest.Reporters.Where(item => string.Equals(item.Alias, alias, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1) { reason = matches.Count == 0 ? "The reporter is not an approved synthetic TEST reporter." : "The reporter alias is configured more than once."; return null; }
        var problem = ReporterProblem(matches[0]);
        reason = problem ?? $"Approved synthetic TEST reporter {matches[0].Alias}.";
        return problem is null ? matches[0] : null;
    }

    /// <summary>Why a configured reporter cannot be used (alias shape, synthetic identifier, security level, environment), or null.</summary>
    public static string? ReporterProblem(SyntheticReporterOptions reporter) =>
        !AliasPattern.IsMatch(reporter.Alias) ? "The reporter alias must be lowercase letters, digits and hyphens."
        : !IntegrationJourneys.Packs.SkoleNaervaer.SyntheticPersonIdentifier.IsSynthetic(reporter.TestIdPersonIdentifier?.Trim())
            ? $"Reporter {reporter.Alias}: the TestID identifier is missing or not a synthetic Test-Norge identifier (month + 80)."
        : !AcrValues.Contains(reporter.AcrValue, StringComparer.Ordinal) ? $"Reporter {reporter.Alias}: the security level must be one of {string.Join(", ", AcrValues)}."
        : !string.Equals(reporter.AllowedEnvironment, "TT02", StringComparison.Ordinal) ? $"Reporter {reporter.Alias}: only the TT02 test environment is allowed."
        : null;

    /// <summary>What is missing for unattended reporter authentication, each problem on its own (no value is ever echoed).</summary>
    public IReadOnlyList<string> UnattendedProblems(AltinnTestConfiguration config)
    {
        var problems = new List<string>();
        if (!options.UnattendedIdPortenTest.Enabled)
            problems.Add($"Unattended ID-porten TEST authentication is not enabled (backend {AltinnTestingOptions.Section}:UnattendedIdPortenTest:Enabled).");
        if (config.AuthenticationType != ExternalAuthenticationType.IDPorten)
            problems.Add("Authentication type is not ID-porten (Integration Quality Review → Altinn test configuration → Authentication).");
        if (string.IsNullOrWhiteSpace(config.ClientReference))
            problems.Add("No ID-porten TEST client id is configured (Altinn test configuration → Client reference).");
        if (!config.ConfiguredScopes.Contains("openid", StringComparer.Ordinal))
            problems.Add("The configured scopes do not include openid (Altinn test configuration → Configured scopes).");
        if (UnattendedRedirect() is null)
            problems.Add($"No registered redirect URI is configured for the ID-porten TEST client (backend {AltinnTestingOptions.Section}:UnattendedIdPortenTest:RedirectUri): https, or http on loopback.");
        if (string.IsNullOrEmpty(UnattendedClientSecret))
            problems.Add("The ID-porten TEST client secret is not available to the backend (environment variable or secret store).");
        if (!options.UnattendedIdPortenTest.Reporters.Any(item => ReporterProblem(item) is null))
            problems.Add("No approved synthetic TEST reporter is configured (backend reporter alias with a synthetic TestID identifier).");
        problems.AddRange(options.UnattendedIdPortenTest.Reporters.Select(ReporterProblem).OfType<string>());
        if (options.UnattendedIdPortenTest.Reporters.GroupBy(item => item.Alias, StringComparer.Ordinal).Any(group => group.Count() > 1))
            problems.Add("A reporter alias is configured more than once.");
        return problems;
    }

    /// <summary>The registered redirect URI when it is usable: absolute, https or loopback http, no credentials or fragment.</summary>
    public Uri? UnattendedRedirect() =>
        Uri.TryCreate(UnattendedRedirectUri, UriKind.Absolute, out var redirect) && string.IsNullOrEmpty(redirect.UserInfo) && redirect.Fragment.Length == 0
            && (redirect.Scheme == Uri.UriSchemeHttps || (redirect.Scheme == Uri.UriSchemeHttp && redirect.IsLoopback)) ? redirect : null;

    /// <summary>The service owner's confirmation of the app's authorization policy, or null with why it is not confirmed.</summary>
    public AltinnAppPolicyConfirmation? AppPolicy(string? appId, out string detail)
    {
        var confirmation = options.ConfirmedAppPolicies.FirstOrDefault(item => string.Equals(item.AppId?.Trim(), appId?.Trim(), StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(item.Reference) && !string.IsNullOrWhiteSpace(item.ConfirmedBy) && item.ConfirmedOn is not null);
        detail = confirmation is null
            ? $"App authorization policy not confirmed: no service-owner confirmation that {appId ?? "the app"}'s XACML policy lets the synthetic TEST reporter instantiate, write and complete (backend {AltinnTestingOptions.Section}:ConfirmedAppPolicies). Real submissions stay disabled."
            : $"App authorization policy confirmed by the service owner ({confirmation.ConfirmedOn:yyyy-MM-dd}, reference {confirmation.Reference}). The policy itself is not read by BirkNext.";
        return confirmation;
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
