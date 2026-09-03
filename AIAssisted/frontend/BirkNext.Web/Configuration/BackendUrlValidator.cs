namespace BirkNext.Web.Configuration;

/// <summary>
/// Validates BackendUrl configuration for HTTPS security and Development loopback exceptions.
/// </summary>
public static class BackendUrlValidator
{
    /// <summary>
    /// Validates a backend URL based on environment.
    ///
    /// Production: HTTPS required
    /// Development: HTTPS required, but HTTP allowed for loopback (localhost/127.x/::1)
    /// </summary>
    public static void Validate(string backendUrl, string environment)
    {
        var isLoopbackUrl = backendUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || backendUrl.Contains("127.")
            || backendUrl.Contains("[::1]")
            || backendUrl.Contains("::1");
        var isHttps = backendUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (!isHttps && !(environment == "Development" && isLoopbackUrl))
        {
            throw new InvalidOperationException(
                "BackendUrl must use HTTPS scheme for security. HTTP is only allowed for loopback (localhost/127.0.0.1/::1) in Development. " +
                $"Current value: {backendUrl}, Environment: {environment}");
        }
    }
}
