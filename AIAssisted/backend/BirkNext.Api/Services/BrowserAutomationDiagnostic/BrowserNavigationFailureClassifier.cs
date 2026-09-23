using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.BrowserAutomationDiagnostic;

/// <summary>
/// What the BROWSER observed when a target navigation failed, normalized. A category is derived from a browser error
/// code (or from the failure kind when there is no code); it describes the failure the browser saw, never the
/// infrastructure cause behind it. "DNS" means the browser could not resolve the name — not that a DNS server is wrong.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserNavigationFailureCategory
{
    /// <summary>No recognised browser error code, or a code this classifier does not map. The code, if any, is still shown.</summary>
    Unknown,
    Dns,
    TlsCertificate,
    ConnectionRefused,
    /// <summary>The BROWSER reported a connection timeout (<c>ERR_CONNECTION_TIMED_OUT</c>, <c>ERR_TIMED_OUT</c>).</summary>
    ConnectionTimeout,
    ConnectionReset,
    NetworkUnavailable,
    Proxy,
    Tunnel,
    AddressUnreachable,
    Protocol,
    /// <summary>Only from a code that says so (<c>ERR_BLOCKED_BY_ADMINISTRATOR</c>, <c>ERR_UNSAFE_PORT</c>) — never from an exception type.</summary>
    BrowserPolicyOrRestriction,
    /// <summary>The page, context or browser closed during the navigation. Not a network error.</summary>
    TargetClosed,
    /// <summary>PLAYWRIGHT's navigation timeout elapsed with no browser error. An absence of a response, not a browser observation.</summary>
    NavigationTimeout,
}

/// <summary>
/// The safe, structured account of a failed target navigation. Carries the exception TYPE and a browser error code
/// extracted by an anchored pattern — never the exception message, which contains the requested URL and its query.
/// </summary>
public sealed record BrowserNavigationFailureEvidence
{
    public BrowserAutomationDiagnosticStage ObservedAtStage { get; init; } = BrowserAutomationDiagnosticStage.TargetNavigation;
    /// <summary>Observed: the exception type Playwright raised.</summary>
    public string ExceptionType { get; init; } = "";
    /// <summary>Observed: the browser-native error code, canonical form <c>net::ERR_…</c>. Null when none was exposed.</summary>
    public string? BrowserErrorCode { get; init; }
    /// <summary>Derived from the code (or the failure kind).</summary>
    public BrowserNavigationFailureCategory Category { get; init; }
    public string CategoryLabel { get; init; } = "";
    /// <summary>Derived: what the code means as a browser observation. Never a root cause.</summary>
    public string Interpretation { get; init; } = "";
    /// <summary>Configured: the navigation timeout the failure happened under.</summary>
    public long NavigationTimeoutMs { get; init; }
}

/// <summary>
/// The single source of truth for turning a navigation exception into safe evidence. Used by both modes of the
/// Browser Automation Diagnostic and by the Headless Authentication Diagnostic, so all three read a failure the same way.
///
/// Playwright reports a browser navigation failure as the first line of its message, for example
/// <c>page.goto: net::ERR_NAME_NOT_RESOLVED at https://host/?code=…</c>. Only a code at the START of that line (after an
/// optional <c>api.name:</c> prefix) is accepted, so text later in the line — the URL, its query, anything a page or a
/// redirect put there — can never be mistaken for, or smuggled in as, a browser error code.
/// </summary>
public static partial class BrowserNavigationFailureClassifier
{
    /// <summary>How many exceptions of an InnerException chain are inspected. Bounded; the chain is never dumped.</summary>
    public const int MaxExceptionChainDepth = 4;
    /// <summary>How much of a message's first line is inspected.</summary>
    private const int MaxInspectedLength = 256;

    [GeneratedRegex(@"^(?:[A-Za-z][A-Za-z0-9_.]{0,40}:\s+)?(?i:net::)?(?<code>ERR_[A-Z0-9]{1,24}(?:_[A-Z0-9]{1,24}){0,8})(?=$|[\s:;,.)])",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex LeadingBrowserErrorCode();

    /// <summary>
    /// The browser error code in the exception chain, as <c>net::ERR_…</c>, or null. Reads only exception messages —
    /// trusted text from Playwright — and only the anchored code, so nothing else of the message escapes.
    /// </summary>
    public static string? TryExtractBrowserErrorCode(Exception? exception)
    {
        for (var (current, depth) = (exception, 0); current is not null && depth < MaxExceptionChainDepth;
             (current, depth) = (current.InnerException, depth + 1))
        {
            if (TryExtractBrowserErrorCode(current.Message) is { } code) return code;
        }
        return null;
    }

    /// <summary>The same extraction over one message. Exposed for tests; callers pass exception text only.</summary>
    public static string? TryExtractBrowserErrorCode(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var newline = message.IndexOfAny(['\r', '\n']);
        var line = (newline >= 0 ? message[..newline] : message).TrimStart();
        if (line.Length > MaxInspectedLength) line = line[..MaxInspectedLength];
        try
        {
            var match = LeadingBrowserErrorCode().Match(line);
            return match.Success ? "net::" + match.Groups["code"].Value : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>The whole safe description of a failed navigation.</summary>
    public static BrowserNavigationFailureEvidence Describe(
        Exception exception, BrowserAutomationDiagnosticStage stage, TimeSpan navigationTimeout)
    {
        var kind = BrowserAutomationDiagnosticExceptionClassifier.Classify(exception);
        // A closed target is its own evidence. Its message is not searched, so no network code is ever attached to it.
        var code = kind == BrowserAutomationFailureKind.TargetClosed ? null : TryExtractBrowserErrorCode(exception);
        var category = Categorize(kind, code);
        return new()
        {
            ObservedAtStage = stage,
            ExceptionType = BrowserAutomationDiagnosticExceptionClassifier.TypeName(exception),
            BrowserErrorCode = code,
            Category = category,
            CategoryLabel = CategoryLabel(category),
            Interpretation = Interpretation(category, code, navigationTimeout),
            NavigationTimeoutMs = (long)navigationTimeout.TotalMilliseconds,
        };
    }

    /// <summary>
    /// The failure kind decides first — a closed target and a Playwright timeout are not network errors — then the
    /// browser code. Without a code the answer is Unknown, never a guess from the exception type.
    /// </summary>
    public static BrowserNavigationFailureCategory Categorize(BrowserAutomationFailureKind kind, string? browserErrorCode) =>
        kind switch
        {
            BrowserAutomationFailureKind.TargetClosed => BrowserNavigationFailureCategory.TargetClosed,
            BrowserAutomationFailureKind.Timeout when browserErrorCode is null => BrowserNavigationFailureCategory.NavigationTimeout,
            _ when browserErrorCode is null => BrowserNavigationFailureCategory.Unknown,
            _ => CategoryOf(browserErrorCode),
        };

    /// <summary>Known Chromium/Edge codes. Not exhaustive; anything else is Unknown with its code preserved.</summary>
    private static readonly Dictionary<string, BrowserNavigationFailureCategory> KnownCodes = new(StringComparer.Ordinal)
    {
        ["ERR_NAME_NOT_RESOLVED"] = BrowserNavigationFailureCategory.Dns,
        ["ERR_NAME_RESOLUTION_FAILED"] = BrowserNavigationFailureCategory.Dns,
        ["ERR_ICANN_NAME_COLLISION"] = BrowserNavigationFailureCategory.Dns,
        ["ERR_BAD_SSL_CLIENT_AUTH_CERT"] = BrowserNavigationFailureCategory.TlsCertificate,
        ["ERR_CONNECTION_REFUSED"] = BrowserNavigationFailureCategory.ConnectionRefused,
        ["ERR_CONNECTION_TIMED_OUT"] = BrowserNavigationFailureCategory.ConnectionTimeout,
        ["ERR_TIMED_OUT"] = BrowserNavigationFailureCategory.ConnectionTimeout,
        ["ERR_CONNECTION_RESET"] = BrowserNavigationFailureCategory.ConnectionReset,
        ["ERR_CONNECTION_CLOSED"] = BrowserNavigationFailureCategory.ConnectionReset,
        ["ERR_CONNECTION_ABORTED"] = BrowserNavigationFailureCategory.ConnectionReset,
        ["ERR_EMPTY_RESPONSE"] = BrowserNavigationFailureCategory.ConnectionReset,
        ["ERR_INTERNET_DISCONNECTED"] = BrowserNavigationFailureCategory.NetworkUnavailable,
        ["ERR_NETWORK_CHANGED"] = BrowserNavigationFailureCategory.NetworkUnavailable,
        ["ERR_ADDRESS_UNREACHABLE"] = BrowserNavigationFailureCategory.AddressUnreachable,
        ["ERR_MANDATORY_PROXY_CONFIGURATION_FAILED"] = BrowserNavigationFailureCategory.Proxy,
        ["ERR_TUNNEL_CONNECTION_FAILED"] = BrowserNavigationFailureCategory.Tunnel,
        ["ERR_HTTP2_PROTOCOL_ERROR"] = BrowserNavigationFailureCategory.Protocol,
        ["ERR_QUIC_PROTOCOL_ERROR"] = BrowserNavigationFailureCategory.Protocol,
        ["ERR_INVALID_RESPONSE"] = BrowserNavigationFailureCategory.Protocol,
        ["ERR_INVALID_HTTP_RESPONSE"] = BrowserNavigationFailureCategory.Protocol,
        ["ERR_RESPONSE_HEADERS_TRUNCATED"] = BrowserNavigationFailureCategory.Protocol,
        ["ERR_BLOCKED_BY_ADMINISTRATOR"] = BrowserNavigationFailureCategory.BrowserPolicyOrRestriction,
        ["ERR_UNSAFE_PORT"] = BrowserNavigationFailureCategory.BrowserPolicyOrRestriction,
    };

    public static BrowserNavigationFailureCategory CategoryOf(string browserErrorCode)
    {
        var code = browserErrorCode.StartsWith("net::", StringComparison.OrdinalIgnoreCase) ? browserErrorCode[5..] : browserErrorCode;
        if (KnownCodes.TryGetValue(code, out var known)) return known;
        // Families whose prefix alone is unambiguous in Chromium's net error list. PROXY first: a proxy certificate
        // error is about reaching the proxy, not the target.
        if (code.StartsWith("ERR_PROXY_", StringComparison.Ordinal)) return BrowserNavigationFailureCategory.Proxy;
        if (code.StartsWith("ERR_DNS_", StringComparison.Ordinal)) return BrowserNavigationFailureCategory.Dns;
        if (code.StartsWith("ERR_CERT_", StringComparison.Ordinal) || code.StartsWith("ERR_SSL_", StringComparison.Ordinal))
            return BrowserNavigationFailureCategory.TlsCertificate;
        return BrowserNavigationFailureCategory.Unknown;
    }

    public static string CategoryLabel(BrowserNavigationFailureCategory category) => category switch
    {
        BrowserNavigationFailureCategory.Dns => "DNS",
        BrowserNavigationFailureCategory.TlsCertificate => "TLS / certificate",
        BrowserNavigationFailureCategory.ConnectionRefused => "Connection refused",
        BrowserNavigationFailureCategory.ConnectionTimeout => "Connection timeout",
        BrowserNavigationFailureCategory.ConnectionReset => "Connection reset or closed",
        BrowserNavigationFailureCategory.NetworkUnavailable => "Network unavailable",
        BrowserNavigationFailureCategory.Proxy => "Proxy",
        BrowserNavigationFailureCategory.Tunnel => "Proxy tunnel",
        BrowserNavigationFailureCategory.AddressUnreachable => "Address unreachable",
        BrowserNavigationFailureCategory.Protocol => "Protocol",
        BrowserNavigationFailureCategory.BrowserPolicyOrRestriction => "Browser policy or restriction",
        BrowserNavigationFailureCategory.TargetClosed => "Target closed",
        BrowserNavigationFailureCategory.NavigationTimeout => "Navigation timeout",
        _ => "Unknown",
    };

    /// <summary>
    /// What the browser observed, in one sentence. Deliberately stops at the observation: it names no team, no
    /// server and no policy owner, and gives no remediation.
    /// </summary>
    public static string Interpretation(BrowserNavigationFailureCategory category, string? browserErrorCode, TimeSpan navigationTimeout) =>
        (category, browserErrorCode) switch
        {
            (BrowserNavigationFailureCategory.TlsCertificate, "net::ERR_CERT_AUTHORITY_INVALID") =>
                "The browser did not trust the certificate chain the target presented.",
            (BrowserNavigationFailureCategory.TlsCertificate, "net::ERR_CERT_COMMON_NAME_INVALID") =>
                "The certificate the target presented does not match the target hostname, as judged by the browser.",
            (BrowserNavigationFailureCategory.TlsCertificate, "net::ERR_CERT_DATE_INVALID") =>
                "The browser judged the target's certificate to be outside its validity period (by this workstation's clock).",
            (BrowserNavigationFailureCategory.BrowserPolicyOrRestriction, "net::ERR_UNSAFE_PORT") =>
                "The browser refuses connections to this port: it is on the browser's built-in restricted-port list.",
            (BrowserNavigationFailureCategory.BrowserPolicyOrRestriction, _) =>
                "The browser reported that navigation to this address is blocked by browser policy.",
            (BrowserNavigationFailureCategory.Dns, _) => "The browser could not resolve the target hostname.",
            (BrowserNavigationFailureCategory.TlsCertificate, _) =>
                "The browser rejected or could not validate the target's TLS connection or certificate.",
            (BrowserNavigationFailureCategory.ConnectionRefused, _) => "The target actively refused the connection.",
            (BrowserNavigationFailureCategory.ConnectionTimeout, _) =>
                "The browser did not establish a connection to the target within its own connection timeout.",
            (BrowserNavigationFailureCategory.ConnectionReset, _) =>
                "The connection to the target was reset or closed before a response was committed.",
            (BrowserNavigationFailureCategory.NetworkUnavailable, _) =>
                "The browser reported that the network was unavailable or changed during the navigation.",
            (BrowserNavigationFailureCategory.Proxy, _) => "The browser could not establish the connection through the configured proxy.",
            (BrowserNavigationFailureCategory.Tunnel, _) => "The browser could not establish the proxy tunnel to the target.",
            (BrowserNavigationFailureCategory.AddressUnreachable, _) => "The target network address was unreachable from this browser.",
            (BrowserNavigationFailureCategory.Protocol, _) => "The target's response did not follow the protocol the browser expected.",
            (BrowserNavigationFailureCategory.TargetClosed, _) =>
                "The browser page or context closed while the target navigation was in progress.",
            (BrowserNavigationFailureCategory.NavigationTimeout, _) =>
                FormattableString.Invariant($"Playwright's navigation timeout ({navigationTimeout.TotalSeconds:0.#} s) elapsed before the browser committed a ")
                + "response. The browser reported no network error, so this is an absence of a response, not a diagnosed network failure.",
            (_, not null) =>
                "Target navigation failed with a browser error code this diagnostic does not classify. The code is shown as the browser reported it.",
            _ => "Target navigation failed before browser control could be established. The browser did not expose a "
                 + "recognised network/navigation error code.",
        };
}
