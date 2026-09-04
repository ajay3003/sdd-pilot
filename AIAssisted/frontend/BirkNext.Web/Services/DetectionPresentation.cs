using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static partial class DetectionPresentation
{
    private static readonly string[] EnvironmentSuffixes = ["development", "production", "staging", "local", "dev", "prod", "qa", "test", "rc"];

    public static string FrameworkLabel(ClientFrameworkType? framework) => framework switch
    {
        ClientFrameworkType.BlazorWebAssembly => "Blazor WebAssembly",
        ClientFrameworkType.React => "React",
        ClientFrameworkType.Angular => "Angular",
        ClientFrameworkType.Vue => "Vue",
        ClientFrameworkType.Other => "Other",
        _ => "Not determined"
    };

    public static string? ProfileName(string? hostname)
    {
        var label = hostname?.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(label) || label.Length < 2) return null;
        var value = Separators().Replace(label, " ");
        value = CamelBoundary().Replace(value, "$1 $2");
        foreach (var suffix in EnvironmentSuffixes)
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && value.Length > suffix.Length)
            {
                value = $"{value[..^suffix.Length]} {suffix}";
                break;
            }
        }
        return Whitespace().Replace(value, " ").Trim().ToUpperInvariant();
    }

    [GeneratedRegex("[-_]+")]
    private static partial Regex Separators();
    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex CamelBoundary();
    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
}
