using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.LocalHttpsProxy;

/// <summary>
/// Central redaction applied BEFORE anything derived from proxied traffic reaches logs, status, evidence or exceptions.
/// Header values for credential-bearing headers are always masked; free text is scrubbed of Bearer values, JWT fragments and
/// token-like query parameters. Redaction is exact for named headers and best effort for free text.
/// </summary>
public static partial class SensitiveDataRedactor
{
    public const string Mask = "[REDACTED]";

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key", "api-key", "apikey",
        "x-auth-token", "x-access-token", "x-id-token", "x-refresh-token", "x-client-secret", "client-secret",
        "x-ms-client-secret", "x-functions-key", "ocp-apim-subscription-key", "x-amz-security-token", "x-csrf-token", "x-xsrf-token"
    };

    public static bool IsSensitiveHeader(string? name) => name is not null && SensitiveHeaders.Contains(name.Trim());

    public static string RedactHeaderValue(string name, string? value) => IsSensitiveHeader(name) ? Mask : RedactText(value ?? "");

    /// <summary>Redacts a raw "Name: value" header line.</summary>
    public static string RedactHeaderLine(string line)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0) return RedactText(line);
        var name = line[..colon].Trim();
        return IsSensitiveHeader(name) ? $"{name}: {Mask}" : RedactText(line);
    }

    public static IReadOnlyList<KeyValuePair<string, string>> RedactHeaders(IEnumerable<KeyValuePair<string, string>> headers) =>
        headers.Select(h => new KeyValuePair<string, string>(h.Key, RedactHeaderValue(h.Key, h.Value))).ToList();

    /// <summary>Scrubs credential-like material from free text (log messages, evidence, exception text).</summary>
    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var value = HeaderPattern().Replace(text, m => $"{m.Groups[1].Value}: {Mask}");
        value = BearerPattern().Replace(value, "Bearer " + Mask);
        value = JwtPattern().Replace(value, Mask);
        value = QueryPattern().Replace(value, m => $"{m.Groups[1].Value}={Mask}");
        return value;
    }

    [GeneratedRegex(@"(?im)\b(authorization|proxy-authorization|cookie|set-cookie|x-api-key|api-key|x-auth-token|x-access-token|x-client-secret|client-secret|x-functions-key|ocp-apim-subscription-key)\s*[:=]\s*[^\r\n]*")]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9\-._~+/=]+")]
    private static partial Regex BearerPattern();

    // JWT header segments always start with "eyJ" ({" in base64url). Match full tokens and bare fragments.
    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{6,}(?:\.[A-Za-z0-9_-]+){0,2}")]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"(?i)\b(access_token|id_token|refresh_token|token|code|client_secret|client_assertion|api_key|apikey|sig|signature|password|session|sessionid|sid)=([^&\s""'<>]+)")]
    private static partial Regex QueryPattern();
}
