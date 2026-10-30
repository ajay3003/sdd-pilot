using System.Text.Json.Serialization;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.RuntimeAuthentication;

/// <summary>
/// The authentication a person selects for one runtime operation. Auto is a deterministic selection made before the run from what the
/// operation supports and whether the environment requires sign-in — never a retry with another mode after a failure.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuntimeAuthenticationMode { Auto, Attended, Unattended, None }

/// <summary>
/// State of the selected mode for one environment. NotRequired: no sign-in is needed (None). Configured: the mode is set up but no
/// authenticated context exists yet (attended: you sign in when the run starts; unattended: not validated with the current configuration).
/// Authenticated: a current context exists (attended proxy credential observed; unattended login validated — each run still signs in
/// fresh). Expired / Failed: a context existed or was attempted and is no longer usable. Unsupported: the operation cannot use the mode.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuntimeAuthenticationState { NotRequired, NotConfigured, Configured, Authenticated, Expired, Failed, Unsupported }

/// <summary>How an operation can use one mode.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RuntimeAuthenticationSupport { Supported, Unsupported, NotApplicable }

/// <summary>Where an attended context comes from, so the state is read from the right existing service.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AttendedAuthenticationSource
{
    /// <summary>No attended mode.</summary>
    None,
    /// <summary>The Local HTTPS proxy's memory-only credential observed from the tester's own browser (authenticated API execution).</summary>
    LocalHttpsProxy,
    /// <summary>The tester signs in in a browser BirkNext opens or observes when the run starts (authenticated session page, companion).</summary>
    InteractiveBrowser,
}

/// <summary>One runtime operation and the authentication modes it honestly supports today.</summary>
public sealed record RuntimeAuthenticationOperation(
    string Key,
    string Module,
    string Operation,
    RuntimeAuthenticationSupport Attended,
    AttendedAuthenticationSource AttendedSource,
    RuntimeAuthenticationSupport Unattended,
    bool Anonymous,
    bool ServiceAuthentication,
    string? UnattendedLimitation = null,
    string? ServiceDescription = null);

/// <summary>The catalog of runtime operations and the authentication each supports. One place, read by every module and the readiness report.</summary>
public static class RuntimeAuthenticationCatalog
{
    public const string FrontendRuntime = "frontend-quality.runtime";
    public const string FrontendAccessibility = "frontend-quality.accessibility";
    public const string ApiAuthenticatedChecks = "api-quality.authenticated-checks";
    public const string CriticalE2EBrowser = "critical-e2e.browser";
    public const string CriticalE2EApi = "critical-e2e.api";
    public const string EndpointDiscovery = "endpoint-discovery.capture";
    public const string BrowserDiscovery = "browser-discovery.capture";
    public const string PerformanceK6 = "performance.k6";
    public const string IntegrationRuntime = "integration-quality.runtime";

    private const string ApiUnattended = "Unattended login produces a browser session, not API credentials. Authenticated API checks need the attended Local HTTPS proxy.";
    private const string ObserveUnattended = "Discovery observes your own browsing. There is nothing to observe without a person using the application.";

    public static readonly IReadOnlyList<RuntimeAuthenticationOperation> All =
    [
        new(FrontendRuntime, "Frontend Quality Review", "Browser Runtime and Accessibility engines", RuntimeAuthenticationSupport.Supported, AttendedAuthenticationSource.InteractiveBrowser, RuntimeAuthenticationSupport.Supported, true, false),
        new(FrontendAccessibility, "Frontend Quality Review", "Accessibility scan", RuntimeAuthenticationSupport.Supported, AttendedAuthenticationSource.InteractiveBrowser, RuntimeAuthenticationSupport.Supported, true, false),
        new(ApiAuthenticatedChecks, "API Quality Review", "Authenticated API checks", RuntimeAuthenticationSupport.Supported, AttendedAuthenticationSource.LocalHttpsProxy, RuntimeAuthenticationSupport.Unsupported, true, false, ApiUnattended),
        new(CriticalE2EBrowser, "Critical E2E Regression", "Companion browser flow", RuntimeAuthenticationSupport.Supported, AttendedAuthenticationSource.InteractiveBrowser, RuntimeAuthenticationSupport.Unsupported, true, false,
            "Companion flows run in your own signed-in browser. Running them with the automation identity is not implemented."),
        new(CriticalE2EApi, "Critical E2E Regression", "Automated integration flow", RuntimeAuthenticationSupport.Supported, AttendedAuthenticationSource.LocalHttpsProxy, RuntimeAuthenticationSupport.Unsupported, true, false, ApiUnattended),
        new(EndpointDiscovery, "Endpoint Discovery", "Proxy capture", RuntimeAuthenticationSupport.Supported, AttendedAuthenticationSource.LocalHttpsProxy, RuntimeAuthenticationSupport.Unsupported, true, false, ObserveUnattended),
        new(BrowserDiscovery, "Browser Discovery", "Companion capture", RuntimeAuthenticationSupport.Supported, AttendedAuthenticationSource.InteractiveBrowser, RuntimeAuthenticationSupport.Unsupported, true, false, ObserveUnattended),
        new(PerformanceK6, "Performance Test Review", "k6 load test", RuntimeAuthenticationSupport.NotApplicable, AttendedAuthenticationSource.None, RuntimeAuthenticationSupport.NotApplicable, true, true,
            "k6 runs without a browser; it uses the scenario's authentication references.", "Scenario authentication references (token values resolved on the backend per run)"),
        new(IntegrationRuntime, "Integration Quality Review", "Runtime evidence and tests", RuntimeAuthenticationSupport.NotApplicable, AttendedAuthenticationSource.None, RuntimeAuthenticationSupport.NotApplicable, false, true,
            "Integrations authenticate with their own service credentials; browser login does not apply.", "Per-integration service credentials (Azure, Service Bus, Event Hub, Altinn clients)"),
    ];

    public static RuntimeAuthenticationOperation? Find(string key) => All.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.Ordinal));
}

/// <summary>What the existing services report for one environment, per mode. Never a token, cookie or password.</summary>
public sealed record RuntimeAuthenticationFacts
{
    /// <summary>Effective "sign-in required" of the environment (Target Environments → Authentication); null when not determined.</summary>
    public bool? SignInRequired { get; init; }
    public RuntimeAuthenticationState Attended { get; init; } = RuntimeAuthenticationState.NotConfigured;
    public string AttendedReason { get; init; } = "";
    public string? AttendedIdentity { get; init; }
    public DateTimeOffset? AttendedExpiresAt { get; init; }
    public RuntimeAuthenticationState Unattended { get; init; } = RuntimeAuthenticationState.NotConfigured;
    public string UnattendedReason { get; init; } = "";
    public string? UnattendedIdentity { get; init; }
}

public sealed record RuntimeAuthenticationRequest
{
    public string OperationKey { get; init; } = "";
    public RuntimeAuthenticationMode Mode { get; init; }
    public string EnvironmentId { get; init; } = "";
    /// <summary>Normalized Frontend URL + profile fingerprint of the environment, so a context of another environment is never used.</summary>
    public string? ContextFingerprint { get; init; }
    public string? FrontendUrl { get; init; }
    public bool? SignInRequired { get; init; }
    /// <summary>The environment's saved authenticated testing method (Target Environments → Authentication).</summary>
    public AuthenticatedTestingMethod TestingMethod { get; init; } = AuthenticatedTestingMethod.ManagedEdgeCdp;
}

public sealed record RuntimeAuthenticationResolveRequest(List<RuntimeAuthenticationRequest> Requests);

/// <summary>The resolved authentication for one operation in one environment. Nothing here is a secret or an execution authorization.</summary>
public sealed record RuntimeAuthenticationResolution
{
    public string OperationKey { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public RuntimeAuthenticationMode Requested { get; init; }
    /// <summary>The mode the run uses. Auto resolves to exactly one mode; the run never switches to another.</summary>
    public RuntimeAuthenticationMode Effective { get; init; }
    public RuntimeAuthenticationState State { get; init; }
    public string Reason { get; init; } = "";
    /// <summary>Why Auto chose <see cref="Effective"/>; null when a mode was selected explicitly.</summary>
    public string? AutoReason { get; init; }
    /// <summary>Who the run acts as: "your browser session", the automation identity alias, or null for None.</summary>
    public string? Identity { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public IReadOnlyList<RuntimeAuthenticationModeOption> Options { get; init; } = [];
    /// <summary>The run may start with this resolution. Expired/Failed/NotConfigured/Unsupported block a mode that needs authentication.</summary>
    public bool CanStart => State is RuntimeAuthenticationState.NotRequired or RuntimeAuthenticationState.Configured or RuntimeAuthenticationState.Authenticated
        && !(Effective == RuntimeAuthenticationMode.Unattended && State != RuntimeAuthenticationState.Authenticated);
}

/// <summary>One selectable mode as the selector shows it.</summary>
public sealed record RuntimeAuthenticationModeOption(RuntimeAuthenticationMode Mode, RuntimeAuthenticationSupport Support, RuntimeAuthenticationState State, string Detail);

/// <summary>
/// The one resolution policy. Pure: facts in, resolution out. Rules: an unsupported mode is never used; Auto picks None when the
/// environment does not require sign-in, otherwise Attended when supported, otherwise Unattended when supported — fixed before the run,
/// never chosen by trying modes; a failure of the effective mode is reported, never replaced by another mode or identity.
/// </summary>
public static class RuntimeAuthenticationPolicy
{
    public static RuntimeAuthenticationResolution Resolve(RuntimeAuthenticationOperation operation, RuntimeAuthenticationMode requested, string environmentId, RuntimeAuthenticationFacts facts)
    {
        var options = Options(operation, facts);
        string? autoReason = null;
        var effective = requested;
        if (requested == RuntimeAuthenticationMode.Auto)
            (effective, autoReason) = facts.SignInRequired == false && operation.Anonymous
                ? (RuntimeAuthenticationMode.None, "The environment does not require sign-in.")
                : operation.Attended == RuntimeAuthenticationSupport.Supported
                    ? (RuntimeAuthenticationMode.Attended, "Attended is this operation's interactive mode.")
                    : operation.Unattended == RuntimeAuthenticationSupport.Supported
                        ? (RuntimeAuthenticationMode.Unattended, "This operation supports only unattended login.")
                        : (RuntimeAuthenticationMode.None, operation.ServiceAuthentication ? "Service authentication applies; no browser login." : "No browser authentication mode is supported.");

        var option = options.First(o => o.Mode == effective);
        var state = option.State;
        var reason = option.Detail;
        if (effective == RuntimeAuthenticationMode.None && facts.SignInRequired == true && operation.Anonymous)
            reason = "Runs without authentication: pages and APIs that need sign-in are reported as not assessed, not as passed.";
        return new RuntimeAuthenticationResolution
        {
            OperationKey = operation.Key, EnvironmentId = environmentId, Requested = requested, Effective = effective, State = state, Reason = reason,
            AutoReason = autoReason, Options = options,
            Identity = effective switch
            {
                RuntimeAuthenticationMode.Attended => facts.AttendedIdentity ?? "Your browser session",
                RuntimeAuthenticationMode.Unattended => facts.UnattendedIdentity,
                _ => null,
            },
            ExpiresAt = effective == RuntimeAuthenticationMode.Attended ? facts.AttendedExpiresAt : null,
        };
    }

    public static IReadOnlyList<RuntimeAuthenticationModeOption> Options(RuntimeAuthenticationOperation operation, RuntimeAuthenticationFacts facts) =>
    [
        new(RuntimeAuthenticationMode.Auto, RuntimeAuthenticationSupport.Supported, RuntimeAuthenticationState.Configured, "Chosen before the run; never switches mode after a failure."),
        Option(RuntimeAuthenticationMode.Attended, operation.Attended, facts.Attended, facts.AttendedReason, operation.ServiceAuthentication ? operation.UnattendedLimitation : null),
        Option(RuntimeAuthenticationMode.Unattended, operation.Unattended, facts.Unattended, facts.UnattendedReason, operation.UnattendedLimitation),
        operation.Anonymous
            ? new(RuntimeAuthenticationMode.None, RuntimeAuthenticationSupport.Supported, RuntimeAuthenticationState.NotRequired, "No sign-in. Only public pages and APIs are assessed.")
            : new(RuntimeAuthenticationMode.None, RuntimeAuthenticationSupport.NotApplicable, RuntimeAuthenticationState.NotRequired,
                operation.ServiceDescription is { } s ? $"No browser login. Uses {s}." : "No browser login."),
    ];

    private static RuntimeAuthenticationModeOption Option(RuntimeAuthenticationMode mode, RuntimeAuthenticationSupport support, RuntimeAuthenticationState state, string reason, string? limitation) =>
        support == RuntimeAuthenticationSupport.Supported
            ? new(mode, support, state, reason)
            : new(mode, support, RuntimeAuthenticationState.Unsupported, limitation ?? "Not supported by this operation.");

    public static string StateLabel(RuntimeAuthenticationState state) => state switch
    {
        RuntimeAuthenticationState.NotRequired => "No sign-in",
        RuntimeAuthenticationState.NotConfigured => "Not configured",
        RuntimeAuthenticationState.Configured => "Configured",
        RuntimeAuthenticationState.Authenticated => "Authenticated",
        RuntimeAuthenticationState.Expired => "Expired",
        RuntimeAuthenticationState.Failed => "Failed",
        _ => "Unsupported",
    };

    public static string ModeLabel(RuntimeAuthenticationMode mode) => mode switch
    {
        RuntimeAuthenticationMode.Attended => "Attended",
        RuntimeAuthenticationMode.Unattended => "Unattended",
        RuntimeAuthenticationMode.None => "None",
        _ => "Auto",
    };
}
