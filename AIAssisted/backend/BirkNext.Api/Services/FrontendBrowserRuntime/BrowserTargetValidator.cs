namespace BirkNext.Api.Services.FrontendBrowserRuntime;

/// <summary>
/// Validates browser runtime targets against SSRF/security policy.
/// Blocks sensitive schemes, metadata endpoints, and enforces environment-specific policies.
/// </summary>
public sealed class BrowserTargetValidator
{
    private readonly bool _allowLoopback;

    public BrowserTargetValidator()
        : this(allowLoopback: false)
    {
    }

    internal BrowserTargetValidator(bool allowLoopback)
    {
        _allowLoopback = allowLoopback;
    }

    public sealed record ValidationResult(
        bool IsValid,
        string? BlockReason = null,
        string? ClassifiedType = null);

    public ValidationResult ValidateTarget(string targetUrl, string? environmentType = "Public")
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
            return new ValidationResult(false, "Target URL is required");

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri))
            return new ValidationResult(false, "Invalid URL format");

        var schemeCheck = ValidateScheme(uri.Scheme);
        if (!schemeCheck.IsValid)
            return schemeCheck;

        var hostCheck = ValidateHost(uri.Host, environmentType);
        if (!hostCheck.IsValid)
            return hostCheck;

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return new ValidationResult(false, "URL cannot contain userinfo (credentials)");

        return new ValidationResult(true, ClassifiedType: ClassifyTarget(uri.Host));
    }

    public ValidationResult ValidateRedirectTarget(string redirectUrl, string originalHost, string? environmentType = "Public")
    {
        if (!Uri.TryCreate(redirectUrl, UriKind.Absolute, out var redirectUri))
            return new ValidationResult(false, "Invalid redirect URL format");

        var targetValidation = ValidateTarget(redirectUrl, environmentType);
        if (!targetValidation.IsValid)
            return targetValidation;

        // Redirect to different host must also pass validation
        if (redirectUri.Host != originalHost)
        {
            var hostCheck = ValidateHost(redirectUri.Host, environmentType);
            if (!hostCheck.IsValid)
                return new ValidationResult(false, $"Redirect target blocked: {hostCheck.BlockReason}");
        }

        return new ValidationResult(true, ClassifiedType: ClassifyTarget(redirectUri.Host));
    }

    /// <summary>
    /// Validates a resolved IP address against security policy.
    /// Used by DNS resolver to validate all addresses before allowing HTTP requests.
    /// </summary>
    public ValidationResult ValidateResolvedAddress(string ipAddress, string? environmentType = "Public")
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
            return new ValidationResult(false, "IP address is required");

        return ValidateHost(ipAddress, environmentType);
    }

    private ValidationResult ValidateScheme(string scheme)
    {
        return scheme.ToLowerInvariant() switch
        {
            "http" or "https" => new ValidationResult(true),
            "file" => new ValidationResult(false, "file:// scheme not allowed"),
            "javascript" => new ValidationResult(false, "javascript: scheme not allowed"),
            "data" => new ValidationResult(false, "data: scheme not allowed"),
            "ftp" => new ValidationResult(false, "ftp: scheme not allowed"),
            _ => new ValidationResult(false, $"Scheme '{scheme}' not allowed")
        };
    }

    private ValidationResult ValidateHost(string host, string? environmentType)
    {
        // Block metadata endpoints
        if (host == "169.254.169.254" || host == "metadata.google.internal")
            return new ValidationResult(false, "Metadata endpoint blocked");

        // Block loopback and link-local
        if ((IsLoopback(host) && !_allowLoopback) || IsLinkLocal(host))
            return new ValidationResult(false, "Loopback/link-local addresses blocked by default");

        // Classify the target
        var classification = ClassifyTarget(host);

        // Private addresses require explicit environment trust (not implemented in Phase 2A)
        if (IsPrivateNetwork(host) && environmentType != "Internal")
            return new ValidationResult(false, "Private network addresses require internal environment context");

        return new ValidationResult(true);
    }

    private static bool IsLoopback(string host)
    {
        var normalizedHost = host.Trim('[', ']');
        return normalizedHost == "localhost" || normalizedHost == "127.0.0.1" ||
               normalizedHost == "::1" || normalizedHost.StartsWith("127.");
    }

    private static bool IsLinkLocal(string host)
    {
        // IPv4 link-local: 169.254.0.0/16
        // IPv6 link-local: fe80::/10
        return host.StartsWith("169.254.") || host.StartsWith("fe80:");
    }

    private static bool IsPrivateNetwork(string host)
    {
        // RFC 1918 private networks
        // 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16
        return host.StartsWith("10.") ||
               host.StartsWith("172.") ||
               host.StartsWith("192.168.") ||
               host.StartsWith("fc") || host.StartsWith("fd"); // ULA IPv6
    }

    private static string ClassifyTarget(string host)
    {
        if (IsLoopback(host) || IsLinkLocal(host))
            return "Loopback";
        if (host == "169.254.169.254" || host == "metadata.google.internal")
            return "Metadata";
        if (IsPrivateNetwork(host))
            return "Private";
        return "Public";
    }
}
