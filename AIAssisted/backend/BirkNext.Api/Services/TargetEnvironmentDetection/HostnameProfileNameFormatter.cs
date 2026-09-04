using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.TargetEnvironmentDetection;

public static partial class HostnameProfileNameFormatter
{
    private static readonly string[] EnvironmentSuffixes = ["development", "production", "staging", "local", "dev", "prod", "qa", "test", "rc"];

    public static string? Format(string? hostname)
    {
        var label = hostname?.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(label) || label.Length < 2)
            return null;

        var normalized = Separators().Replace(label, " ");
        normalized = CamelBoundary().Replace(normalized, "$1 $2");
        foreach (var suffix in EnvironmentSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && normalized.Length > suffix.Length)
            {
                normalized = $"{normalized[..^suffix.Length]} {suffix}";
                break;
            }
        }

        return Whitespace().Replace(normalized, " ").Trim().ToUpperInvariant();
    }

    [GeneratedRegex("[-_]+")]
    private static partial Regex Separators();
    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex CamelBoundary();
    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
}
