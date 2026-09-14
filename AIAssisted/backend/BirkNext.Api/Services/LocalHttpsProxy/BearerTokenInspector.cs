using System.Text;
using System.Text.Json;

namespace BirkNext.Api.Services.LocalHttpsProxy;

/// <summary>Non-secret metadata derived from an observed Bearer token. Never contains the token or arbitrary claim values.</summary>
public sealed record BearerTokenMetadata(bool IsJwt, DateTimeOffset? ExpiresAt, DateTimeOffset? IssuedAt, string? IssuerHost, string? TenantId, bool HasAudience)
{
    public string Format => IsJwt ? "JWT" : "Opaque";
}

/// <summary>
/// Decodes JWT payload metadata in backend memory only. This is unsigned inspection for expiry/tenant correlation; it is NOT
/// cryptographic validation and never establishes token validity. The raw token is never logged or returned.
/// </summary>
public static class BearerTokenInspector
{
    public static bool LooksLikeBearerToken(string? token) =>
        token is { Length: >= 20 and <= 8192 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/' or '=');

    public static BearerTokenMetadata Inspect(string token)
    {
        var parts = token.Split('.');
        if (parts.Length is not (3 or 5) || !parts[0].StartsWith("eyJ", StringComparison.Ordinal)) return new(false, null, null, null, null, false);
        try
        {
            using var header = JsonDocument.Parse(Base64Url(parts[0]));
            if (header.RootElement.ValueKind != JsonValueKind.Object || !header.RootElement.TryGetProperty("alg", out _)) return new(false, null, null, null, null, false);
            if (parts.Length == 5) return new(true, null, null, null, null, false); // JWE: payload is encrypted, no metadata available.
            using var payload = JsonDocument.Parse(Base64Url(parts[1]));
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(true, null, null, null, null, false);
            DateTimeOffset? exp = root.TryGetProperty("exp", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var expSeconds) ? DateTimeOffset.FromUnixTimeSeconds(expSeconds) : null;
            DateTimeOffset? iat = root.TryGetProperty("iat", out var i) && i.ValueKind == JsonValueKind.Number && i.TryGetInt64(out var iatSeconds) ? DateTimeOffset.FromUnixTimeSeconds(iatSeconds) : null;
            string? issuerHost = null;
            string? tenant = null;
            if (root.TryGetProperty("iss", out var iss) && iss.ValueKind == JsonValueKind.String && Uri.TryCreate(iss.GetString(), UriKind.Absolute, out var issuer))
            {
                issuerHost = issuer.IdnHost.ToLowerInvariant();
                var first = issuer.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (first is not null && Guid.TryParse(first, out var tenantFromIssuer)) tenant = tenantFromIssuer.ToString("D");
            }
            if (root.TryGetProperty("tid", out var tid) && tid.ValueKind == JsonValueKind.String && Guid.TryParse(tid.GetString(), out var tidGuid)) tenant = tidGuid.ToString("D");
            var hasAudience = root.TryGetProperty("aud", out var aud) && aud.ValueKind is JsonValueKind.String or JsonValueKind.Array;
            return new(true, exp, iat, issuerHost, tenant, hasAudience);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return new(false, null, null, null, null, false);
        }
    }

    private static byte[] Base64Url(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4) { case 2: padded += "=="; break; case 3: padded += "="; break; case 1: throw new FormatException("Invalid base64url length."); }
        return Convert.FromBase64String(padded);
    }

    /// <summary>Test/diagnostic helper: a JWT-shaped token with the given payload. Unsigned; never a real credential.</summary>
    public static string BuildUnsignedJwt(object payload)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""));
        var body = Encode(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{header}.{body}.{Encode(new byte[32])}";
    }
}
