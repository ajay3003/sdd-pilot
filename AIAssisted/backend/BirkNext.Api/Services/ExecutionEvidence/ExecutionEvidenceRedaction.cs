using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.ExecutionEvidence;

namespace BirkNext.Api.Services.ExecutionEvidence;

/// <summary>
/// The one redaction policy applied to execution evidence BEFORE anything is persisted (version <see cref="Version"/>). Layers:
/// (1) authentication headers always masked (the Local HTTPS proxy list plus cookie/set-cookie/x-api-key …), (2) sensitive JSON/XML field
/// names and credential-bearing query parameters, (3) value patterns: Bearer/JWT/private keys/connection strings, and 11-digit numbers
/// (possible national identity numbers), (4) binary content is metadata-only. When safe redaction cannot be guaranteed (unparseable
/// structured body under a sensitive content type) the body is kept as metadata only.
/// </summary>
public static partial class ExecutionEvidenceRedaction
{
    public const string Version = "ee-redaction-v1";
    public const string Mask = "••••••";

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key", "api-key", "x-auth-token", "x-csrf-token", "x-xsrf-token",
        "x-amz-security-token", "x-ms-token-aad-access-token", "x-ms-token-aad-id-token", "ocp-apim-subscription-key", "x-functions-key",
    };

    [GeneratedRegex(@"(?i)(password|passwd|secret|token|api[-_]?key|apikey|authorization|cookie|session(id)?|credential|private[-_]?key|client[-_]?secret|connection[-_]?string|fodselsnummer|f[øo]dselsnummer|personnummer|ssn|national[-_]?id)")]
    private static partial Regex SensitiveName();
    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9\-._~+/=]{6,}|eyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]*|-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----|(?:AccountKey|SharedAccessKey|Password|Pwd)=[^;""\s]+")]
    private static partial Regex SensitiveValue();
    [GeneratedRegex(@"(?<!\d)\d{11}(?!\d)")] private static partial Regex ElevenDigits();
    [GeneratedRegex(@"<(?<tag>[A-Za-z_][\w.\-:]*)(?<attrs>[^>]*)>(?<value>[^<]*)</\k<tag>>")] private static partial Regex XmlElement();

    public static bool IsSensitiveHeader(string name) => SensitiveHeaders.Contains(name.Trim()) || SensitiveDataRedactor.IsSensitiveHeader(name);
    public static bool IsSensitiveName(string name) => SensitiveName().IsMatch(name);

    public static List<CapturedHeader> Headers(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers) => [.. headers.Select(h =>
    {
        if (IsSensitiveHeader(h.Key)) return new CapturedHeader(h.Key, Mask, true);
        var (text, masked) = Text(string.Join(", ", h.Value));
        return new CapturedHeader(h.Key, text.Length > 512 ? text[..512] + "…" : text, masked > 0);
    })];

    /// <summary>The URL with credential-bearing query parameters masked; user info is never kept.</summary>
    public static string Url(Uri uri)
    {
        var b = new UriBuilder(uri) { UserName = "", Password = "" };
        if (b.Query.Length > 1)
        {
            var pairs = b.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p =>
            {
                var i = p.IndexOf('='); var key = Uri.UnescapeDataString(i < 0 ? p : p[..i]);
                return i >= 0 && (IsSensitiveName(key) || key.Equals("code", StringComparison.OrdinalIgnoreCase) || key.Equals("sig", StringComparison.OrdinalIgnoreCase))
                    ? $"{p[..i]}={Uri.EscapeDataString(Mask)}" : i >= 0 ? $"{p[..i]}={Uri.EscapeDataString(Text(Uri.UnescapeDataString(p[(i + 1)..])).Text)}" : p;
            });
            b.Query = string.Join('&', pairs);
        }
        return b.Uri.ToString();
    }

    /// <summary>Free text: token/JWT/key/connection-string patterns and 11-digit numbers masked. Returns the text and how many masks were applied.</summary>
    public static (string Text, int Masked) Text(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (text ?? "", 0);
        var n = 0;
        var result = SensitiveValue().Replace(text, _ => { n++; return Mask; });
        result = ElevenDigits().Replace(result, _ => { n++; return Mask; });
        return (result, n);
    }

    /// <summary>
    /// A body as it may be stored: JSON values under sensitive names masked (then value patterns), XML element text under sensitive names masked,
    /// text bodies pattern-masked; binary never stored. <paramref name="limit"/> bounds the stored text (on a character boundary).
    /// </summary>
    public static CapturedBody Body(byte[]? raw, long byteCount, string? contentType, CapturePolicy policy, int limit, bool readTruncated)
    {
        var media = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (byteCount == 0 || raw is null || raw.Length == 0)
            return new() { ContentType = contentType, ByteCount = byteCount, Completeness = byteCount == 0 ? CaptureCompleteness.Empty : CaptureCompleteness.Unavailable,
                Note = byteCount == 0 ? "Empty body." : "The body could not be read." };
        var textual = media.Contains("json") || media.Contains("xml") || media.StartsWith("text/", StringComparison.Ordinal) || media is "application/graphql" or "application/x-www-form-urlencoded" or "";
        if (policy == CapturePolicy.MetadataOnly || !textual)
            return new() { ContentType = contentType, ByteCount = byteCount, Completeness = CaptureCompleteness.MetadataOnly, Sha256 = null,
                Note = policy == CapturePolicy.MetadataOnly ? "Capture policy: metadata only." : "Binary or unknown content: metadata only (never stored)." };
        string decoded;
        try { decoded = new UTF8Encoding(false, true).GetString(raw); }
        catch (DecoderFallbackException) { return new() { ContentType = contentType, ByteCount = byteCount, Completeness = CaptureCompleteness.MetadataOnly, Note = "Not valid UTF-8: metadata only." }; }

        int masked;
        string redacted;
        if (media.Contains("json") && !readTruncated)
        {
            try
            {
                var node = JsonNode.Parse(decoded, null, new JsonDocumentOptions { MaxDepth = 64 });
                masked = MaskJson(node);
                redacted = node?.ToJsonString() ?? "null";
                (redacted, var extra) = Text(redacted); masked += extra;
            }
            catch (JsonException) { (redacted, masked) = Text(decoded); }
        }
        else if (media.Contains("json"))
        {
            // A truncated JSON body cannot be parsed for field-level redaction: keep metadata only (conservative).
            return new() { ContentType = contentType, ByteCount = byteCount, Completeness = CaptureCompleteness.MetadataOnly,
                Note = "JSON body larger than the inspection limit: field-level redaction cannot be guaranteed, so it is not stored." };
        }
        else if (media.Contains("xml"))
        {
            var n = 0;
            redacted = XmlElement().Replace(decoded, m => IsSensitiveName(m.Groups["tag"].Value.Split(':')[^1]) ? (++n > 0 ? $"<{m.Groups["tag"].Value}{m.Groups["attrs"].Value}>{Mask}</{m.Groups["tag"].Value}>" : m.Value) : m.Value);
            (redacted, var extra) = Text(redacted); masked = n + extra;
        }
        else if (media == "application/x-www-form-urlencoded")
        {
            var n = 0;
            redacted = string.Join('&', decoded.Split('&').Select(p => { var i = p.IndexOf('='); return i > 0 && IsSensitiveName(Uri.UnescapeDataString(p[..i])) ? (++n > 0 ? $"{p[..i]}={Mask}" : p) : p; }));
            (redacted, var extra) = Text(redacted); masked = n + extra;
        }
        else (redacted, masked) = Text(decoded);

        var truncated = readTruncated || redacted.Length > limit;
        if (redacted.Length > limit) redacted = redacted[..(char.IsHighSurrogate(redacted[limit - 1]) ? limit - 1 : limit)];
        return new()
        {
            ContentType = contentType, ByteCount = byteCount, Encoding = "utf-8", Text = redacted, RedactedFields = masked,
            Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(redacted))).ToLowerInvariant(),
            Completeness = truncated ? CaptureCompleteness.Truncated : CaptureCompleteness.ExactRedacted,
            Note = truncated ? $"Stored text is limited to {limit:N0} characters; the original body was {byteCount:N0} bytes." : masked > 0 ? $"{masked} value(s) redacted." : null,
        };
    }

    /// <summary>Masks values under sensitive property names (recursively). GraphQL variables are JSON and follow the same rule.</summary>
    public static int MaskJson(JsonNode? node)
    {
        var n = 0;
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    if (IsSensitiveName(key) && o[key] is not null) { o[key] = Mask; n++; }
                    else n += MaskJson(o[key]);
                }
                break;
            case JsonArray a:
                foreach (var item in a) n += MaskJson(item);
                break;
        }
        return n;
    }

    /// <summary>A short value for an assertion's expected/actual, redacted the same way.</summary>
    public static string? Value(string? value, string? target) =>
        value is null ? null : target is not null && IsSensitiveName(target.Split('.', '/', '[')[^1]) ? Mask : Text(value.Length > 300 ? value[..300] + "…" : value).Text;
}
