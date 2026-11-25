using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>Malware scan state of an evidence file. BirkNext ships no scanner: unless a host registers one, every file is
/// <see cref="NotScanned"/> and is never shown as clean.</summary>
public static class EvidenceScanStatus
{
    public const string NotScanned = "not-scanned";
    public const string Clean = "clean";
    public const string Rejected = "rejected";
}

/// <summary>A malware scanner for evidence files. Null or unavailable = files are stored as <see cref="EvidenceScanStatus.NotScanned"/>.</summary>
public interface IEvidenceFileScanner
{
    /// <summary>True when a scan can be performed right now.</summary>
    bool Available { get; }
    /// <summary>Scans the bytes; returns <see cref="EvidenceScanStatus.Clean"/> or <see cref="EvidenceScanStatus.Rejected"/> (never guesses).</summary>
    Task<string> ScanAsync(string fileName, byte[] content, CancellationToken ct);
}

/// <summary>
/// The conservative content policy for evidence files when no malware scanner exists: an allow-list of passive types, a content check per type
/// (image/PDF signatures, PDFs without active content, text that is valid UTF-8 and not an HTML page, JSON/HAR that parses, XML that parses with
/// DTDs prohibited), and a safe display name. Nothing uploaded is ever executed, rendered inline or parsed beyond these checks.
/// </summary>
public static class EvidenceFilePolicy
{
    private static readonly string[] PdfActiveContent = ["/JavaScript", "/JS ", "/JS(", "/JS<", "/Launch", "/EmbeddedFile", "/RichMedia", "/XFA"];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Why the content is refused for its type, or null when it is accepted.</summary>
    public static string? ContentProblem(string extension, string contentType, byte[] c)
    {
        if (!ManualTestRunService.SignatureMatches(contentType, c)) return "The file's content does not match its type.";
        switch (contentType)
        {
            case "application/pdf":
                var latin = Encoding.Latin1.GetString(c);
                return PdfActiveContent.FirstOrDefault(m => latin.Contains(m, StringComparison.Ordinal)) is { } marker
                    ? $"PDF files with active content ({marker.Trim(' ', '(', '<')}) are not accepted as evidence." : null;
            case "image/png" or "image/jpeg" or "image/gif" or "image/webp":
                return null;
        }
        string text;
        try { text = StrictUtf8.GetString(c.AsSpan(c.Length >= 3 && c[0] == 0xEF && c[1] == 0xBB && c[2] == 0xBF ? 3 : 0)); }
        catch (DecoderFallbackException) { return "Text evidence must be UTF-8."; }
        var head = text.TrimStart()[..Math.Min(text.TrimStart().Length, 512)];
        if (contentType is "text/plain" or "text/csv" &&
            (head.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<script", StringComparison.OrdinalIgnoreCase)))
            return "This looks like a web page; attach a screenshot or a text export instead.";
        if (contentType == "application/json")
        {
            try
            {
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 256 });
                if (extension.Equals(".har", StringComparison.OrdinalIgnoreCase) && !(doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("log", out _)))
                    return "A HAR file has a \"log\" object at its root.";
            }
            catch (JsonException) { return "The JSON file does not parse."; }
        }
        if (contentType == "application/xml")
        {
            try
            {
                using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 20_000_000 });
                while (reader.Read()) { }
            }
            catch (XmlException) { return "The XML file does not parse (document type declarations are not accepted)."; }
        }
        return null;
    }

    /// <summary>
    /// A safe display and download name: the last path segment, without control or bidirectional formatting characters (which can disguise an
    /// extension), without characters that are invalid in file names or quotes, and never starting with a spreadsheet formula character. The
    /// extension is kept as uploaded (it was checked against the allow-list).
    /// </summary>
    public static string SafeName(string? fileName)
    {
        var last = (fileName ?? "").Replace('\\', '/').Split('/').Last();
        var sb = new StringBuilder(last.Length);
        foreach (var ch in last)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (char.IsControl(ch) || category == UnicodeCategory.Format) continue;
            sb.Append(ch is '"' or '<' or '>' or ':' or '|' or '?' or '*' or ';' ? '_' : ch);
        }
        var name = sb.ToString().Trim().Trim('.').Trim();
        if (name.Length > 0 && name[0] is '=' or '+' or '-' or '@') name = "_" + name;
        return name.Length > 255 ? name[..255] : name;
    }

    /// <summary>The name as uploaded, kept as metadata only (control characters removed, length bounded); never used as a path or a download name.</summary>
    public static string OriginalName(string? fileName)
    {
        var s = new string((fileName ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length > 500 ? s[..500] : s;
    }
}
