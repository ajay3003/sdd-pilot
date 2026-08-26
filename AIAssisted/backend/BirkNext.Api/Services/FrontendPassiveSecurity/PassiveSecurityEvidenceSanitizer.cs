using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.FrontendPassiveSecurity;

public sealed partial class PassiveSecurityEvidenceSanitizer
{
    public const int MaxEvidenceLength = 512;
    public string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var safe = UserInfo().Replace(value, "//[REDACTED]@");
        safe = Bearer().Replace(safe, "$1[REDACTED]");
        safe = NamedSecret().Replace(safe, "$1[REDACTED]");
        safe = Cookie().Replace(safe, "$1[REDACTED]");
        return safe.Length <= MaxEvidenceLength ? safe : safe[..MaxEvidenceLength] + "…";
    }

    [GeneratedRegex(@"//[^/@\s]+@", RegexOptions.IgnoreCase)] private static partial Regex UserInfo();
    [GeneratedRegex(@"(?i)(authorization\s*[:=]\s*bearer\s+|bearer\s+)[^\s,;]+") ] private static partial Regex Bearer();
    [GeneratedRegex(@"(?i)((?:access_token|id_token|code|api_key|apikey|secret|client_secret)\s*[=:]\s*)[^&\s,;]+") ] private static partial Regex NamedSecret();
    [GeneratedRegex(@"(?i)((?:set-cookie|cookie)\s*:\s*)[^\r\n]+") ] private static partial Regex Cookie();
}
