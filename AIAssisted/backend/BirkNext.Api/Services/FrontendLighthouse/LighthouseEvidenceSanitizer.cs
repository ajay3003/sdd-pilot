using BirkNext.Api.Services.FrontendBrowserRuntime;
using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.FrontendLighthouse;

public sealed partial class LighthouseEvidenceSanitizer(BrowserEvidenceSanitizer browserSanitizer)
{
    public string? SanitizeUrl(string? value) => value is null ? null : RedactSentinels(browserSanitizer.SanitizeUrl(value));
    public string? SanitizeText(string? value) => value is null ? null : RedactSentinels(browserSanitizer.SanitizeMessage(value));

    private static string RedactSentinels(string value) => SecretRegex().Replace(value, "[REDACTED]");

    [GeneratedRegex(@"SECRET-[A-Z0-9-]+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretRegex();
}
