using System.Text.RegularExpressions;
using BirkNext.Api.Services.LocalHttpsProxy;

namespace BirkNext.Api.Services.TestEvidence;

/// <summary>
/// Bounding and redaction for text that leaves a test artifact (names, failure messages, stack traces, output). Credentials are scrubbed by the
/// shared <see cref="SensitiveDataRedactor"/>; absolute file-system paths are reduced to their file name so machine layout and user names
/// (C:\Users\name\…, /home/name/…) never persist. Redaction is best effort for free text; output is additionally bounded.
/// </summary>
public static partial class TestEvidenceText
{
    public static string Bound(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var clean = ControlCharacters().Replace(value, " ");
        return clean.Length <= max ? clean : clean[..max] + " … [truncated]";
    }

    public static string? Redact(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = SensitiveDataRedactor.RedactText(value);
        text = WindowsPath().Replace(text, m => "…\\" + m.Groups["file"].Value);
        text = UnixPath().Replace(text, m => "…/" + m.Groups["file"].Value);
        text = UncPath().Replace(text, m => "…\\" + m.Groups["file"].Value);
        return Bound(text.Trim(), max);
    }

    /// <summary>File name of a path (either separator), never the directories.</summary>
    public static string? FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Replace('\\', '/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        return name.Length == 0 ? null : Bound(name, 260);
    }

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F]")]
    private static partial Regex ControlCharacters();

    [GeneratedRegex(@"\b[A-Za-z]:\\(?:[^\\\r\n:*?""<>|]+\\)*(?<file>[^\\\r\n:*?""<>|]+)")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"(?<![\w.])/(?:home|Users|users|root|tmp|var|opt|mnt|srv|usr|agent|azp|__w|builds|workspace|runner)(?:/[^/\s:'""]+)*/(?<file>[^/\s:'""]+)")]
    private static partial Regex UnixPath();

    [GeneratedRegex(@"\\\\[^\\\s]+(?:\\[^\\\r\n:*?""<>|]+)*\\(?<file>[^\\\r\n:*?""<>|]+)")]
    private static partial Regex UncPath();
}
