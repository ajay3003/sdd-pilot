namespace BirkNext.Web.Configuration;

/// <summary>
/// Validates the frontend's BackendUrl: HTTPS, or plain HTTP to the backend on this computer (loopback). BirkNext is one local instance for
/// one local tester (Local Tester Mode), so a loopback backend is accepted in every hosting environment — a local Release run or the Windows
/// Tester Package as much as development. Any other HTTP backend is refused.
/// </summary>
public static class BackendUrlValidator
{
    /// <summary>
    /// The HttpClient base address for <paramref name="backendUrl"/>, always ending in "/" so relative "api/…" paths
    /// append to it (a base without the slash would drop its last path segment, e.g. a "/birknext" prefix).
    /// </summary>
    public static Uri BaseAddress(string backendUrl) => new(backendUrl.EndsWith('/') ? backendUrl : backendUrl + "/");

    /// <summary>Throws unless <paramref name="backendUrl"/> is HTTPS or loopback HTTP. <paramref name="environment"/> is reported only.</summary>
    public static void Validate(string backendUrl, string environment)
    {
        var isHttps = backendUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !IsLoopback(backendUrl))
        {
            throw new InvalidOperationException(
                "BackendUrl must use HTTPS scheme for security. HTTP is only allowed for loopback (localhost/127.0.0.1/::1), the BirkNext backend on this computer. " +
                $"Current value: {backendUrl}, Environment: {environment}");
        }
    }

    /// <summary>The URL's host is loopback: localhost, 127.0.0.0/8 or ::1 (also the unbracketed "http://::1:port" form). A host name that only
    /// starts with "127." or contains "localhost" is not.</summary>
    public static bool IsLoopback(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || (System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && System.Net.IPAddress.IsLoopback(address));
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        var rest = scheme < 0 ? url : url[(scheme + 3)..];
        return rest == "::1" || rest.StartsWith("::1:", StringComparison.Ordinal) || rest.StartsWith("::1/", StringComparison.Ordinal);
    }
}
