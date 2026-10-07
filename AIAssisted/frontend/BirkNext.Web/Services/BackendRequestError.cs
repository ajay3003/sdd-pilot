using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

namespace BirkNext.Web.Services;

/// <summary>
/// Whether the BirkNext backend answered at transport level. Any HTTP response — 400, 401, 403, 404, 500 included —
/// proves the backend is reachable; only a failure before an HTTP response exists can make it unreachable.
/// </summary>
public enum BackendConnectivity
{
    /// <summary>Not known from this request (timeout, cancellation, a gateway answering on the backend's behalf).</summary>
    Unknown,
    Reachable,
    Unreachable,
}

/// <summary>What one backend request ended as. Separate from <see cref="BackendConnectivity"/>: an endpoint can fail while the backend is reachable.</summary>
public enum BackendRequestCategory
{
    BadRequest,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    ValidationError,
    ServerError,
    ServiceUnavailable,
    GatewayError,
    HttpError,
    /// <summary>HTTP succeeded, but the body could not be read as the expected shape.</summary>
    InvalidResponse,
    Timeout,
    TlsError,
    HostNotFound,
    ConnectionRefused,
    /// <summary>No HTTP response; the browser does not say why (refused, offline, or blocked by its cross-origin policy).</summary>
    NetworkError,
    Cancelled,
    Unknown,
}

/// <summary>
/// One failed backend request, classified once so every page says the same thing about the same failure. Carries the
/// operation and route template only — never a query string, body, identity value or token.
/// </summary>
public sealed record BackendRequestError(
    string Operation,
    BackendConnectivity Connectivity,
    BackendRequestCategory Category,
    int? HttpStatus,
    string Detail,
    string TechnicalMessage,
    bool Retryable,
    DateTimeOffset Timestamp,
    string? Endpoint = null)
{
    /// <summary>The plain-language message: the operation, then what happened.</summary>
    public string UserMessage => $"{Operation}: {Detail}";

    /// <summary>Short connectivity label for a status badge; text, so the state never depends on colour.</summary>
    public string ConnectivityLabel => Connectivity switch
    {
        BackendConnectivity.Reachable => "Backend reachable",
        BackendConnectivity.Unreachable => "Backend unreachable",
        _ => "Backend connectivity unknown",
    };

    public string CategoryLabel => BackendRequestClassifier.CategoryLabel(Category);

    /// <summary>True only when no HTTP response was obtained — the one case a page may call the backend unreachable.</summary>
    public bool IsTransportFailure => Connectivity == BackendConnectivity.Unreachable;
}

/// <summary>The single mapping from exceptions and HTTP statuses to <see cref="BackendRequestError"/>.</summary>
public static class BackendRequestClassifier
{
    /// <summary>The exceptions a backend call can end with. Pages catch these and nothing broader.</summary>
    public static bool IsRequestFailure(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or NotSupportedException or AuthenticationException;

    /// <summary>Connectivity proven by an HTTP status: any response from the backend — success or error — means it is reachable.</summary>
    public static BackendConnectivity ConnectivityFor(int status) => status is 502 or 504 ? BackendConnectivity.Unknown : BackendConnectivity.Reachable;

    public static BackendRequestError FromStatus(string operation, HttpStatusCode status, string? endpoint = null, DateTimeOffset? at = null) =>
        FromStatus(operation, (int)status, endpoint, at);

    public static BackendRequestError FromStatus(string operation, int status, string? endpoint = null, DateTimeOffset? at = null)
    {
        var (category, connectivity, retryable) = status switch
        {
            400 => (BackendRequestCategory.BadRequest, BackendConnectivity.Reachable, false),
            401 => (BackendRequestCategory.Unauthorized, BackendConnectivity.Reachable, false),
            403 => (BackendRequestCategory.Forbidden, BackendConnectivity.Reachable, false),
            404 => (BackendRequestCategory.NotFound, BackendConnectivity.Reachable, false),
            409 => (BackendRequestCategory.Conflict, BackendConnectivity.Reachable, true),
            422 => (BackendRequestCategory.ValidationError, BackendConnectivity.Reachable, false),
            503 => (BackendRequestCategory.ServiceUnavailable, BackendConnectivity.Reachable, true),
            // A gateway answering for the backend proves the gateway is reachable, not the backend behind it.
            502 or 504 => (BackendRequestCategory.GatewayError, BackendConnectivity.Unknown, true),
            >= 500 => (BackendRequestCategory.ServerError, BackendConnectivity.Reachable, true),
            _ => (BackendRequestCategory.HttpError, BackendConnectivity.Reachable, false),
        };
        var detail = category switch
        {
            BackendRequestCategory.BadRequest => $"the backend rejected the request (HTTP {status}).",
            BackendRequestCategory.Unauthorized => $"authentication required (HTTP {status}).",
            BackendRequestCategory.Forbidden => $"access denied by the backend (HTTP {status}).",
            BackendRequestCategory.NotFound => $"the BirkNext backend is reachable, but this endpoint was not found (HTTP {status}).",
            BackendRequestCategory.Conflict => $"the backend refused the request in its current state (HTTP {status}).",
            BackendRequestCategory.ValidationError => $"the backend did not accept the request (HTTP {status}).",
            BackendRequestCategory.ServiceUnavailable => $"the backend is reachable but reported it is temporarily unavailable (HTTP {status}).",
            BackendRequestCategory.GatewayError => $"a gateway in front of the backend returned HTTP {status}.",
            BackendRequestCategory.ServerError => $"the backend returned HTTP {status}.",
            _ => $"the backend returned HTTP {status}.",
        };
        return new BackendRequestError(operation, connectivity, category, status, detail, $"HTTP {status}", retryable, at ?? DateTimeOffset.Now, endpoint);
    }

    public static BackendRequestError FromException(string operation, Exception ex, string? endpoint = null, CancellationToken cancellationToken = default, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.Now;
        if (ex is HttpRequestException { StatusCode: { } status }) return FromStatus(operation, status, endpoint, now);

        BackendRequestError Make(BackendConnectivity c, BackendRequestCategory k, string detail, bool retry = true) =>
            new(operation, c, k, null, detail, $"{k} ({ex.GetType().Name})", retry, now, endpoint);

        switch (ex)
        {
            case OperationCanceledException when cancellationToken.IsCancellationRequested:
                return Make(BackendConnectivity.Unknown, BackendRequestCategory.Cancelled, "the request was cancelled.");
            case TaskCanceledException or OperationCanceledException:
                // HttpClient.Timeout surfaces as TaskCanceledException (inner TimeoutException) without the caller cancelling.
                return Make(BackendConnectivity.Unknown, BackendRequestCategory.Timeout, "the request timed out.");
            case JsonException or NotSupportedException:
                return Make(BackendConnectivity.Reachable, BackendRequestCategory.InvalidResponse, "the backend answered, but the response could not be read.", retry: false);
            case AuthenticationException:
                return Make(BackendConnectivity.Unreachable, BackendRequestCategory.TlsError, "secure connection to the BirkNext backend failed (TLS).");
            case HttpRequestException http:
                return FromTransport(http, Make);
            default:
                return Make(BackendConnectivity.Unknown, BackendRequestCategory.Unknown, "the request failed.");
        }
    }

    private static BackendRequestError FromTransport(HttpRequestException ex, Func<BackendConnectivity, BackendRequestCategory, string, bool, BackendRequestError> make)
    {
        if (ex.InnerException is AuthenticationException || ex.HttpRequestError == HttpRequestError.SecureConnectionError)
            return make(BackendConnectivity.Unreachable, BackendRequestCategory.TlsError, "secure connection to the BirkNext backend failed (TLS).", true);
        var socket = ex.InnerException as SocketException ?? ex.InnerException?.InnerException as SocketException;
        if (ex.HttpRequestError == HttpRequestError.NameResolutionError || socket?.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
            return make(BackendConnectivity.Unreachable, BackendRequestCategory.HostNotFound, "BirkNext backend unreachable — host not found.", true);
        if (socket?.SocketErrorCode == SocketError.ConnectionRefused || (ex.HttpRequestError == HttpRequestError.ConnectionError && socket is null))
            return make(BackendConnectivity.Unreachable, BackendRequestCategory.ConnectionRefused, "BirkNext backend unreachable — connection refused.", true);
        if (ex.InnerException is TimeoutException)
            return make(BackendConnectivity.Unknown, BackendRequestCategory.Timeout, "the request timed out.", true);
        if (ex.HttpRequestError is HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded)
            return make(BackendConnectivity.Unknown, BackendRequestCategory.InvalidResponse, "the connection closed before a complete HTTP response arrived.", true);
        if (socket is not null)
            return make(BackendConnectivity.Unreachable, BackendRequestCategory.NetworkError, $"BirkNext backend unreachable — network failure ({socket.SocketErrorCode}).", true);
        // Browser fetch failures arrive here: no status, no socket detail. The browser hides whether the connection was
        // refused, the network is down, or a cross-origin policy blocked the response, so all three are named.
        return make(BackendConnectivity.Unreachable, BackendRequestCategory.NetworkError,
            "BirkNext backend unreachable — the browser got no HTTP response (connection refused, network failure, or blocked by the browser's cross-origin policy).", true);
    }

    public static string CategoryLabel(BackendRequestCategory category) => category switch
    {
        BackendRequestCategory.BadRequest => "Request rejected",
        BackendRequestCategory.Unauthorized => "Authentication required",
        BackendRequestCategory.Forbidden => "Access denied",
        BackendRequestCategory.NotFound => "Endpoint not found",
        BackendRequestCategory.Conflict => "Conflict",
        BackendRequestCategory.ValidationError => "Validation error",
        BackendRequestCategory.ServerError => "Server error",
        BackendRequestCategory.ServiceUnavailable => "Service unavailable",
        BackendRequestCategory.GatewayError => "Gateway error",
        BackendRequestCategory.HttpError => "HTTP error",
        BackendRequestCategory.InvalidResponse => "Unreadable response",
        BackendRequestCategory.Timeout => "Request timed out",
        BackendRequestCategory.TlsError => "Secure connection failed",
        BackendRequestCategory.HostNotFound => "Host not found",
        BackendRequestCategory.ConnectionRefused => "Connection refused",
        BackendRequestCategory.NetworkError => "Network failure",
        BackendRequestCategory.Cancelled => "Cancelled",
        _ => "Unknown failure",
    };
}
