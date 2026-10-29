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
    /// <summary>Approved synthetic children for report scenarios, chosen by alias. Local execution configuration only (environment variables,
    /// never a committed file): the identifier stays in the backend. BirkNext never adds to this list or invents a child.</summary>
    public List<SyntheticChildOptions> SyntheticChildren { get; set; } = [];
    /// <summary>Adds the built-in Skolenærvær default reporter and child to the Test Data identities at startup (fill-if-absent by alias;
    /// <see cref="SkoleNaervaerTestIdentityDefaults"/>). On by default; false keeps only configured and session identities.</summary>
    public bool BuiltInSkolenaervaerIdentities { get; set; } = true;
    /// <summary>
    /// Service-owner confirmations that an app's authorization policy (XACML: who may instantiate, write and complete, roles, authentication
    /// level, party representation) accepts the configured synthetic TEST reporter. Without a confirmation for the app, submission scenarios
    /// stay Not ready: BirkNext never infers an app policy.
    /// </summary>
    public List<AltinnAppPolicyConfirmation> ConfirmedAppPolicies { get; set; } = [];
    /// <summary>
    /// The most process/next calls one run may make. 1 for first runs: a plan that needs more is not executed (never truncated), and an
    /// unexpected process state after a transition always stops the run. Values below 1 or above 10 are treated as 1.
    /// </summary>
    public int MaxProcessTransitions { get; set; } = 1;
    /// <summary>After how many hours a live read of the deployed contract is shown as "Refresh recommended" (bounded default 24; 1–720).</summary>
    public int ContractRefreshRecommendedAfterHours { get; set; } = 24;
    /// <summary>The Altinn host addresses per logical environment (defaults: TT02). The one place to change when Altinn moves a host.</summary>
    public AltinnEndpointOptions Endpoints { get; set; } = new();
}

/// <summary>
/// Unattended reporter authentication: ID-porten TEST's documented headless login for API testing (authorization code + PKCE with a
/// <c>testid:</c> login_hint, client_secret_basic), then the Altinn TT02 token exchange. TEST only. The client id and scopes stay in the
/// Integration Quality Review Altinn test configuration; only trust and secrets live here.
/// </summary>
public sealed class UnattendedIdPortenTestOptions
{
    public bool Enabled { get; set; }
    /// <summary>
    /// Optional ID-porten TEST client id from local backend configuration, used when Integration Quality Review has none. When both are set
    /// they must agree; a disagreement is reported, never resolved by picking one.
    /// </summary>
    public string? ClientId { get; set; }
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
    /// <summary>Set only after confirming the synthetic person has an Altinn TT02 profile (Altinn's token exchange requires one).</summary>
    public bool AltinnProfileConfirmed { get; set; }
    /// <summary>Set when the reporter was entered in BirkNext for this backend session (memory only); null for backend configuration.</summary>
    public DateTimeOffset? SessionExpiresAt { get; internal set; }
    /// <summary>The Altinn role the reporter acts in. A run's instance owner is the reporter's own current party, so only PRIV is supported.</summary>
    public string AltinnRole { get; set; } = "PRIV";
    /// <summary>The reporter to use when several approved reporters apply (an explicit selection always wins). At most one may be the default.</summary>
    public bool Default { get; set; }
    /// <summary>A built-in default identity (not backend configuration and not a session entry).</summary>
    public bool BuiltIn { get; internal set; }
    /// <summary>Increments whenever the identifier is edited or restored in Test Data (a safe change marker; never derived from the value).</summary>
    public int ValueVersion { get; internal set; }
    public DateTimeOffset? ValueUpdatedAt { get; internal set; }
    /// <summary>A built-in default whose identifier was edited (differs from its seeded value).</summary>
    public bool ValueModified { get; internal set; }
}

/// <summary>
/// One approved synthetic child (approved test data). The identifier is used only to build the form; the UI, logs and history see the alias.
/// Whether Person knows the child is the approver's declaration (<see cref="KnownToPerson"/>), never verified by BirkNext.
/// </summary>
public sealed class SyntheticChildOptions
{
    /// <summary>Stable key, e.g. "synthetic-child-1" (lowercase letters, digits and hyphens).</summary>
    public string Alias { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>The synthetic Test-Norge person identifier (11 digits, valid control digits). Configuration outside committed files only.</summary>
    public string? TestPersonIdentifier { get; set; }
    /// <summary>Declared: the child is a registered synthetic test person (known child) rather than one Person does not know.</summary>
    public bool KnownToPerson { get; set; } = true;
    /// <summary>The child to use when several approved children apply. At most one may be the default; otherwise the tester chooses.</summary>
    public bool Default { get; set; }
    /// <summary>Set when the child was entered in BirkNext for this backend session (memory only); null for backend configuration.</summary>
    public DateTimeOffset? SessionExpiresAt { get; internal set; }
    /// <summary>A built-in default identity (not backend configuration and not a session entry).</summary>
    public bool BuiltIn { get; internal set; }
    /// <summary>Increments whenever the identifier is edited or restored in Test Data (a safe change marker; never derived from the value).</summary>
    public int ValueVersion { get; internal set; }
    public DateTimeOffset? ValueUpdatedAt { get; internal set; }
    /// <summary>A built-in default whose identifier was edited (differs from its seeded value).</summary>
    public bool ValueModified { get; internal set; }
}

public sealed class AltinnAppPolicyConfirmation
{
    public string AppId { get; set; } = "";
    /// <summary>The Altinn environment the confirmed policy was read from. Only TT02 is accepted; a confirmation for any other environment is ignored.</summary>
    public string Environment { get; set; } = "TT02";
    /// <summary>Where the confirmation is recorded (ticket, mail, policy file version) — a reference, not the policy itself.</summary>
    public string Reference { get; set; } = "";
    public string ConfirmedBy { get; set; } = "";
    public DateOnly? ConfirmedOn { get; set; }
    /// <summary>Optional, verified facts read from the deployed policy. Empty lists mean "not recorded", never "anything allowed".</summary>
    public string? PolicySource { get; set; }
    public string? PolicySha256 { get; set; }
    /// <summary>Altinn role codes the end-user rule grants (e.g. PRIV, DAGL).</summary>
    public List<string> EndUserRoles { get; set; } = [];
    /// <summary>XACML actions the end-user rule grants (e.g. read, write, instantiate). A data task's process/next is authorized by "write".</summary>
    public List<string> EndUserActions { get; set; } = [];
    public int? MinimumAuthenticationLevel { get; set; }
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
/// Nothing here can be widened from a request. The unattended client values resolve per environment: the Target Environment's saved Altinn
/// settings, then a session value (the client secret), then the backend's own configuration, then the Integration Quality Review binding;
/// two explicit values that disagree are a conflict, never a silent choice. Trust and app approval stay backend configuration only.
/// </summary>
public sealed class AltinnTestingPolicy(AltinnTestingOptions options, AltinnSessionStore? session = null, AltinnEnvironmentSettingsCache? environments = null)
{
    /// <summary>
    /// The configuration a consumer reads for an environment: the IQR binding overlaid with the Target Environment's saved Altinn values
    /// (environment, app, base URL), which take precedence. Read paths only — it is never saved back, so nothing is copied into IQR.
    /// </summary>
    public AltinnTestConfiguration Effective(AltinnTestConfiguration config, string environmentId)
    {
        var effective = config with { TargetEnvironmentId = environmentId };
        return environments?.Get(environmentId) is { } saved
            ? effective with { Environment = saved.Environment ?? effective.Environment, AppIdentifier = saved.AppIdentifier ?? effective.AppIdentifier, BaseUrlReference = saved.AppBaseUrl ?? effective.BaseUrlReference }
            : effective;
    }

    /// <summary>The Target Environment Altinn settings saved for the configuration's environment, or null.</summary>
    public AltinnEnvironmentSettings? Saved(AltinnTestConfiguration config) => environments?.Get(config.TargetEnvironmentId);

    public const string TargetEnvironmentAltinn = "Target Environment → Connected Systems → Altinn";

    public const string IdPortenTestIssuer = "https://test.idporten.no";
    public const string IdPortenTestDiscovery = "https://test.idporten.no/.well-known/openid-configuration";
    /// <summary>ID-porten TEST's authorization and token endpoints as published by its discovery document (the runtime reads them from
    /// discovery; these are the expected values a dry run checks against the request guard). ID-porten is its own endpoint family, never
    /// derived from an Altinn address.</summary>
    public const string IdPortenTestAuthorization = "https://login.test.idporten.no/authorize";
    public const string IdPortenTestToken = "https://test.idporten.no/token";
    public const string CallbackPath = "/api/altinn-testing/sign-in/callback";
    private static readonly string[] IdPortenTestHosts = ["test.idporten.no", "login.test.idporten.no"];
    private static readonly string[] ProductionHosts = ["idporten.no", "login.idporten.no", "altinn.no", "www.altinn.no", "platform.altinn.no"];
    private static readonly Regex AppIdPattern = new("^(?<org>[a-z0-9][a-z0-9-]{0,62})/(?<app>[a-z0-9][a-z0-9-]{0,62})$", RegexOptions.CultureInvariant);

    public AltinnTestingOptions Options => options;

    private AltinnEndpoints? _endpoints;
    /// <summary>
    /// The Altinn TT02 addresses (<c>AltinnTesting:Endpoints:Test</c>): the single source for every Altinn URL BirkNext builds or allows.
    /// Throws <see cref="AltinnContractException"/> naming the configuration key when they are missing or invalid.
    /// </summary>
    public AltinnEndpoints Endpoints => _endpoints ??= AltinnEndpoints.Create(options.Endpoints?.Test, "Test", IsProductionHost);

    /// <summary>The addresses of a logical Altinn environment: Test (TT02) only. Production and every other environment have none.</summary>
    public AltinnEndpoints? EndpointsFor(ExternalTestEnvironment environment, out string reason)
    {
        if (environment != ExternalTestEnvironment.Test)
        {
            reason = environment == ExternalTestEnvironment.Production
                ? "Altinn Production has no endpoint in BirkNext and is never contacted."
                : $"The Altinn environment {environment} has no endpoint; Altinn app testing runs only in TT02 (Test).";
            return null;
        }
        reason = "Altinn TT02 (Test) addresses from backend configuration (AltinnTesting:Endpoints:Test). Configured, not verified.";
        return Endpoints;
    }

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

    /// <summary>
    /// Approves one outgoing request: https on the default port, no credentials in the URL, never a production host, and only an ID-porten
    /// TEST host or a host of the configured Altinn TT02 endpoints (<see cref="Endpoints"/>; misconfigured endpoints allow no Altinn host).
    /// </summary>
    public string? RequestBlock(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps) return "Only https requests are allowed.";
        if (!string.IsNullOrEmpty(uri.UserInfo)) return "Credentials in a URL are not allowed.";
        if (uri.Port != 443) return "Only the default https port is allowed.";
        var host = uri.Host.ToLowerInvariant();
        if (IsProductionHost(host)) return $"{host} is a production host and is never contacted.";
        if (IdPortenTestHosts.Contains(host)) return null;
        AltinnEndpoints endpoints;
        try { endpoints = Endpoints; }
        catch (AltinnContractException ex) { return ex.Message; }
        return endpoints.IsAltinnHost(host) ? null : $"{host} is not an approved ID-porten TEST or Altinn TT02 host.";
    }

    /// <summary>Production ID-porten and Altinn hosts: a fixed safety denylist, checked before any allowlist and never configurable.</summary>
    public static bool IsProductionHost(string host) =>
        ProductionHosts.Contains(host) || host.EndsWith(".apps.altinn.no", StringComparison.Ordinal);

    public static bool IsIdPortenTestHost(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && IdPortenTestHosts.Contains(uri.Host.ToLowerInvariant()) && uri.Port == 443;

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

    public static bool IsValidAlias(string? alias) => alias is not null && AliasPattern.IsMatch(alias);

    /// <summary>
    /// The ID-porten TEST client id in effect, by precedence: Target Environment → Connected Systems → Altinn, the backend's own configuration, the Integration
    /// Quality Review binding. Null when none is set or when two of them are set and differ (then <paramref name="problem"/> says so).
    /// <paramref name="source"/> names where it came from.
    /// </summary>
    public string? ClientId(AltinnTestConfiguration config, out string source, out string? problem)
    {
        var candidates = new (string? Value, string Source)[]
        {
            (Saved(config)?.Unattended.ClientId?.Trim(), TargetEnvironmentAltinn),
            (options.UnattendedIdPortenTest.ClientId?.Trim(), "Local backend configuration"),
            (config.ClientReference?.Trim(), "Integration Quality Review → Altinn test configuration"),
        }.Where(item => !string.IsNullOrEmpty(item.Value)).ToList();
        problem = null;
        if (candidates.Select(item => item.Value).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            source = string.Join(" and ", candidates.Select(item => item.Source));
            problem = $"The ID-porten TEST client id differs between {source}; keep one value ({TargetEnvironmentAltinn}).";
            return null;
        }
        if (candidates.Count > 0) { source = candidates[0].Source; return candidates[0].Value; }
        source = "Not configured";
        return null;
    }

    /// <summary>The unattended client's scopes: the Target Environment's saved scopes, else the Integration Quality Review scopes.</summary>
    public IReadOnlyList<string> Scopes(AltinnTestConfiguration config) =>
        (Saved(config)?.Unattended.Scopes is { Count: > 0 } saved ? saved : config.ConfiguredScopes)
            .Select(scope => scope.Trim()).Where(scope => scope.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Automated execution enabled: the Target Environment's explicit choice, else the backend configuration.</summary>
    public bool UnattendedEnabled(AltinnTestConfiguration? config = null) => (config is null ? null : Saved(config)?.Unattended.Enabled) ?? options.UnattendedIdPortenTest.Enabled;

    public string? UnattendedRedirectUri => string.IsNullOrWhiteSpace(options.UnattendedIdPortenTest.RedirectUri) ? options.IdPortenTest.RedirectUri : options.UnattendedIdPortenTest.RedirectUri;

    /// <summary>
    /// The registered redirect URI: the Target Environment's saved value, else the backend's explicit unattended value, else the interactive
    /// sign-in callback (a fallback default). The saved and the explicit backend value set and different is a conflict.
    /// </summary>
    public string? UnattendedRedirectUriFor(AltinnTestConfiguration? config, out string? problem)
    {
        var saved = config is null ? null : Saved(config)?.Unattended.RedirectUri?.Trim();
        var local = options.UnattendedIdPortenTest.RedirectUri?.Trim();
        problem = !string.IsNullOrEmpty(saved) && !string.IsNullOrEmpty(local) && !string.Equals(saved, local, StringComparison.Ordinal)
            ? $"The registered redirect URI differs between {TargetEnvironmentAltinn} and the backend configuration; keep one value." : null;
        return problem is not null ? null : !string.IsNullOrEmpty(saved) ? saved : UnattendedRedirectUri?.Trim();
    }

    public bool UnattendedSecretAvailable => !string.IsNullOrEmpty(UnattendedClientSecret);
    internal string? UnattendedClientSecret => string.IsNullOrEmpty(options.UnattendedIdPortenTest.ClientSecret) ? options.IdPortenTest.ClientSecret : options.UnattendedIdPortenTest.ClientSecret;

    /// <summary>The client secret in effect for the environment: a session value entered in BirkNext, else the backend's own configuration.</summary>
    internal string? UnattendedClientSecretFor(AltinnTestConfiguration? config) => session?.Secret(config?.TargetEnvironmentId) ?? UnattendedClientSecret;

    /// <summary>Where the environment's secret comes from (never the value).</summary>
    public AltinnSettingState SecretState(AltinnTestConfiguration? config) =>
        session?.Secret(config?.TargetEnvironmentId) is not null ? AltinnSettingState.AvailableForSession
        : UnattendedSecretAvailable ? AltinnSettingState.AvailableToBackend : AltinnSettingState.Missing;

    /// <summary>The approved reporter with this alias, or null with the reason. Its identifier never appears in the reason.</summary>
    public SyntheticReporterOptions? Reporter(string? alias, out string reason)
    {
        var matches = options.UnattendedIdPortenTest.Reporters.Where(item => string.Equals(item.Alias, alias, StringComparison.Ordinal)).ToList();
        if (matches.Count != 1) { reason = matches.Count == 0 ? "The reporter is not an approved synthetic TEST reporter." : "The reporter alias is configured more than once."; return null; }
        var problem = ReporterProblem(matches[0]);
        reason = problem ?? $"Approved synthetic TEST reporter {matches[0].Alias}.";
        return problem is null ? matches[0] : null;
    }

    /// <summary>Why a configured reporter cannot be used (its configuration, then the deliberate Altinn profile confirmation), or null.</summary>
    public static string? ReporterProblem(SyntheticReporterOptions reporter) =>
        ReporterConfigurationProblem(reporter)
        ?? (!reporter.AltinnProfileConfirmed ? $"Reporter {reporter.Alias}: the Altinn TT02 profile of the synthetic person is not confirmed (AltinnProfileConfirmed)." : null);

    /// <summary>
    /// Why a reporter's configuration is unusable (alias shape, synthetic identifier, security level, environment, role), or null. Configured
    /// is not confirmed: the Altinn profile confirmation is a separate, deliberate step that nothing here infers.
    /// </summary>
    public static string? ReporterConfigurationProblem(SyntheticReporterOptions reporter) =>
        !AliasPattern.IsMatch(reporter.Alias) ? "The reporter alias must be lowercase letters, digits and hyphens."
        : !IntegrationJourneys.Packs.SkoleNaervaer.SyntheticPersonIdentifier.IsSynthetic(reporter.TestIdPersonIdentifier?.Trim())
            ? $"Reporter {reporter.Alias}: the TestID identifier is missing or not a synthetic Test-Norge identifier (month + 80)."
        : !AcrValues.Contains(reporter.AcrValue, StringComparer.Ordinal) ? $"Reporter {reporter.Alias}: the security level must be one of {string.Join(", ", AcrValues)}."
        : !string.Equals(reporter.AllowedEnvironment, "TT02", StringComparison.Ordinal) ? $"Reporter {reporter.Alias}: only the TT02 test environment is allowed."
        : !string.Equals(reporter.AltinnRole, "PRIV", StringComparison.OrdinalIgnoreCase)
            ? $"Reporter {reporter.Alias}: only the Altinn role PRIV (the person's own party) is supported; acting for an organisation is not part of this run."
        : null;

    /// <summary>
    /// True when the run's child identifier is the selected reporter's own synthetic person. Compared in the backend only; neither value is
    /// returned. The reporter is the Altinn actor and the child is form data: they must be different persons.
    /// </summary>
    public bool IsReporterIdentity(string? alias, string? childIdentifier) =>
        !string.IsNullOrWhiteSpace(childIdentifier)
        && options.UnattendedIdPortenTest.Reporters.FirstOrDefault(item => string.Equals(item.Alias, alias, StringComparison.Ordinal)) is { TestIdPersonIdentifier: { Length: > 0 } identifier }
        && string.Equals(identifier.Trim(), childIdentifier.Trim(), StringComparison.Ordinal);

    /// <summary>Whether the reporter has a confirmed Altinn TT02 profile (reported separately so it is never hidden among other problems).</summary>
    public int ReportersAwaitingProfile => options.UnattendedIdPortenTest.Reporters.Count(item => ReporterConfigurationProblem(item) is null && !item.AltinnProfileConfirmed);

    /// <summary>
    /// When the confirmation records verified policy facts, they must allow what a submission does: an end user in the reporter's role may
    /// instantiate and write (write also authorizes process/next on a data task). Unrecorded facts are not assumed either way.
    /// </summary>
    public static string? PolicyProblem(AltinnAppPolicyConfirmation confirmation, string reporterRole)
    {
        if (confirmation.EndUserRoles.Count > 0 && !confirmation.EndUserRoles.Contains(reporterRole, StringComparer.OrdinalIgnoreCase))
            return $"The confirmed app policy grants end users with the roles {string.Join(", ", confirmation.EndUserRoles)}, not {reporterRole}.";
        if (confirmation.EndUserActions.Count > 0 && new[] { "instantiate", "write" }.FirstOrDefault(action => !confirmation.EndUserActions.Contains(action, StringComparer.OrdinalIgnoreCase)) is { } missing)
            return $"The confirmed app policy does not grant end users the action {missing}, which a submission needs.";
        return null;
    }

    /// <summary>What is missing for unattended reporter authentication, each problem on its own (no value is ever echoed).</summary>
    public IReadOnlyList<string> UnattendedProblems(AltinnTestConfiguration config)
    {
        var problems = new List<string>();
        if (!UnattendedEnabled(config))
            problems.Add($"Automated execution is not enabled for this environment ({TargetEnvironmentAltinn} → Automated execution).");
        if (config.AuthenticationType != ExternalAuthenticationType.IDPorten && Saved(config)?.Unattended.ClientId is null)
            problems.Add("Authentication type is not ID-porten (Integration Quality Review → Altinn test configuration → Authentication).");
        if (ClientId(config, out _, out var clientProblem) is null)
            problems.Add(clientProblem ?? $"No unattended ID-porten TEST client id is configured ({TargetEnvironmentAltinn} → Automated execution).");
        if (!Scopes(config).Contains("openid", StringComparer.Ordinal))
            problems.Add($"The unattended client's scopes do not include openid ({TargetEnvironmentAltinn} → Automated execution).");
        if (UnattendedRedirect(config) is null)
            problems.Add(UnattendedRedirectProblem(config) ?? $"No registered redirect URI is configured for the unattended ID-porten TEST client ({TargetEnvironmentAltinn} → Automated execution): https, or http on loopback.");
        if (string.IsNullOrEmpty(UnattendedClientSecretFor(config)))
            problems.Add($"The ID-porten TEST client secret is not available ({TargetEnvironmentAltinn} → Add session secret).");
        if (!options.UnattendedIdPortenTest.Reporters.Any(item => ReporterProblem(item) is null))
            problems.Add("No approved synthetic TEST reporter is configured (Test Data → session test identities: a reporter with a synthetic TestID identifier and a confirmed TT02 profile).");
        problems.AddRange(options.UnattendedIdPortenTest.Reporters.Select(ReporterProblem).OfType<string>());
        if (options.UnattendedIdPortenTest.Reporters.GroupBy(item => item.Alias, StringComparer.Ordinal).Any(group => group.Count() > 1))
            problems.Add("A reporter alias is configured more than once.");
        return problems;
    }

    /// <summary>The registered redirect URI when it is usable: absolute, https or loopback http, no credentials or fragment.</summary>
    public Uri? UnattendedRedirect(AltinnTestConfiguration? config = null) =>
        Uri.TryCreate(UnattendedRedirectUriFor(config, out _), UriKind.Absolute, out var redirect) && string.IsNullOrEmpty(redirect.UserInfo) && redirect.Fragment.Length == 0
            && (redirect.Scheme == Uri.UriSchemeHttps || (redirect.Scheme == Uri.UriSchemeHttp && redirect.IsLoopback)) ? redirect : null;

    private string? UnattendedRedirectProblem(AltinnTestConfiguration? config) { UnattendedRedirectUriFor(config, out var problem); return problem; }

    /// <summary>The execution bound on process/next calls per run (see <see cref="AltinnTestingOptions.MaxProcessTransitions"/>).</summary>
    public int MaxProcessTransitions => options.MaxProcessTransitions is >= 1 and <= 10 ? options.MaxProcessTransitions : 1;

    public TimeSpan ContractRefreshRecommendedAfter => TimeSpan.FromHours(options.ContractRefreshRecommendedAfterHours is >= 1 and <= 720 ? options.ContractRefreshRecommendedAfterHours : 24);

    /// <summary>The service owner's confirmation of the app's authorization policy, or null with why it is not confirmed.</summary>
    public AltinnAppPolicyConfirmation? AppPolicy(string? appId, out string detail) => AppPolicy(appId, null, out detail);

    /// <summary>
    /// The confirmation, checked against the policy as last read from its TT02 Resource Registry source. A changed hash never inherits the old
    /// confirmation: the policy needs confirming again and real execution stays blocked. Without a re-read, or a recorded hash, that is said.
    /// </summary>
    public AltinnAppPolicyConfirmation? AppPolicy(string? appId, AltinnObservedPolicy? observed, out string detail)
    {
        var confirmation = ConfirmedAppPolicy(appId, out detail);
        if (confirmation is null) return null;
        if (observed is null)
        {
            detail += " The deployed policy has not been re-read since; read the public TT02 metadata to check it.";
            return confirmation;
        }
        if (observed.Sha256 is not { Length: > 0 } current)
        {
            detail += $" The deployed policy could not be re-read ({observed.Problem ?? "no hash"}); its current state is unknown.";
            return confirmation;
        }
        if (confirmation.PolicySha256 is not { Length: > 0 } confirmed)
        {
            detail += " No policy hash was recorded with the confirmation, so a policy change cannot be detected.";
            return confirmation;
        }
        if (!string.Equals(confirmed.Trim(), current, StringComparison.OrdinalIgnoreCase))
        {
            detail = $"App authorization policy changed since it was confirmed (confirmed sha256 {Short(confirmed)}…, deployed now {Short(current)}…, read {observed.ReadAt:yyyy-MM-dd HH:mm} UTC). "
                + $"Needs confirmation: review the new policy and record it in {AltinnTestingOptions.Section}:ConfirmedAppPolicies. Real submissions stay disabled.";
            return null;
        }
        detail += $" Matches the deployed policy read {observed.ReadAt:yyyy-MM-dd HH:mm} UTC.";
        return confirmation;
    }

    private string? PolicySourceOrNull(string org, string app)
    {
        try { return Endpoints.PolicySource(org, app).ToString(); }
        catch (AltinnContractException) { return null; }
    }

    private static string Short(string sha) => sha.Trim()[..Math.Min(12, sha.Trim().Length)];

    private AltinnAppPolicyConfirmation? ConfirmedAppPolicy(string? appId, out string detail)
    {
        var candidates = options.ConfirmedAppPolicies.Where(item => string.Equals(item.AppId?.Trim(), appId?.Trim(), StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(item.Reference) && !string.IsNullOrWhiteSpace(item.ConfirmedBy) && item.ConfirmedOn is not null).ToList();
        var problems = candidates.Select(item => ConfirmationProblem(item)).ToList();
        var confirmation = candidates.Where((_, index) => problems[index] is null).FirstOrDefault();
        detail = confirmation is null && problems.OfType<string>().FirstOrDefault() is { } invalid
            ? $"App authorization policy not confirmed: {invalid} Real submissions stay disabled."
            : confirmation is null && string.IsNullOrWhiteSpace(appId)
            ? $"App authorization policy not confirmed: no Altinn app id is saved for the run, and confirmations ({AltinnTestingOptions.Section}:ConfirmedAppPolicies) are looked up by app id. Real submissions stay disabled."
            : confirmation is null
            ? $"App authorization policy not confirmed: no service-owner confirmation that {appId ?? "the app"}'s XACML policy lets the synthetic TEST reporter instantiate, write and complete (backend {AltinnTestingOptions.Section}:ConfirmedAppPolicies). Real submissions stay disabled."
            // Provenance first: a person recorded this in backend configuration (before and independently of any setup action); matched by app id.
            : $"App authorization policy confirmed in backend configuration {AltinnTestingOptions.Section}:ConfirmedAppPolicies for app {appId} ({confirmation.ConfirmedOn:yyyy-MM-dd}, {confirmation.ConfirmedBy}, reference {confirmation.Reference}) — a recorded human confirmation, never derived from source evidence or applied configuration"
              + (confirmation.EndUserRoles.Count > 0 ? $"; end-user roles {string.Join(", ", confirmation.EndUserRoles)}" : "")
              + (confirmation.EndUserActions.Count > 0 ? $"; end-user actions {string.Join(", ", confirmation.EndUserActions)}" : "")
              + (confirmation.MinimumAuthenticationLevel is { } level ? $"; minimum authentication level {level}" : "")
              + (confirmation.PolicySha256 is { Length: > 0 } sha ? $"; policy sha256 {sha[..Math.Min(12, sha.Length)]}…" : "")
              + ".";
        return confirmation;
    }

    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Why a recorded confirmation cannot stand for the app's TT02 policy, or null: it must be for TT02, a recorded policy source must be that
    /// app's own TT02 Resource Registry policy (never another app or a Production host), and a recorded hash must be a SHA-256 hex digest.
    /// </summary>
    public string? ConfirmationProblem(AltinnAppPolicyConfirmation confirmation)
    {
        if (!string.Equals(confirmation.Environment?.Trim(), "TT02", StringComparison.Ordinal))
            return $"the confirmation for {confirmation.AppId} is for the environment \"{confirmation.Environment}\", not TT02.";
        if (!string.IsNullOrWhiteSpace(confirmation.PolicySource)
            && AppIdPattern.Match(confirmation.AppId.Trim()) is { Success: true } app
            && !string.Equals(confirmation.PolicySource.Trim(), PolicySourceOrNull(app.Groups["org"].Value, app.Groups["app"].Value), StringComparison.Ordinal))
            return $"the recorded policy source is not {confirmation.AppId}'s TT02 Resource Registry policy.";
        if (!string.IsNullOrWhiteSpace(confirmation.PolicySha256) && !Sha256Hex.IsMatch(confirmation.PolicySha256.Trim()))
            return "the recorded policy hash is not a SHA-256 hex digest.";
        return null;
    }

    /// <summary>What is missing for ID-porten TEST sign-in, each problem on its own. Empty when sign-in can be offered.</summary>
    public IReadOnlyList<string> SignInProblems(AltinnTestConfiguration config)
    {
        var problems = new List<string>();
        if (config.AuthenticationType != ExternalAuthenticationType.IDPorten)
            problems.Add("Authentication type is not ID-porten (Integration Quality Review → Altinn test configuration → Authentication).");
        if (ClientId(config, out _, out var clientProblem) is null)
            problems.Add(clientProblem ?? "No ID-porten TEST client id is configured (Altinn test configuration → Client reference).");
        if (!Scopes(config).Contains("openid", StringComparer.Ordinal))
            problems.Add("The configured scopes do not include openid (Altinn test configuration → Configured scopes).");
        if (!Uri.TryCreate(options.IdPortenTest.RedirectUri, UriKind.Absolute, out var redirect))
            problems.Add($"No redirect URI is configured for the ID-porten TEST client (backend {AltinnTestingOptions.Section}:IdPortenTest:RedirectUri).");
        else if (!string.Equals(redirect.AbsolutePath, CallbackPath, StringComparison.Ordinal) || (redirect.Scheme != Uri.UriSchemeHttps && !redirect.IsLoopback))
            problems.Add($"The redirect URI must be https (or loopback http) and end in {CallbackPath}.");
        return problems;
    }
}
