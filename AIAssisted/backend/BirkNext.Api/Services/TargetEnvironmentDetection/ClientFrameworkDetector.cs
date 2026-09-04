using BirkNext.Api.Models;

namespace BirkNext.Api.Services.TargetEnvironmentDetection;

/// <summary>
/// Safely detects client-side frameworks (Blazor WASM, React, etc.) in HTML responses.
/// Uses bounded inspection of response content for positive framework signals.
/// </summary>
public interface IClientFrameworkDetector
{
    /// <summary>
    /// Detect framework type from response content.
    /// Returns null if no recognized framework is detected.
    /// </summary>
    ClientFrameworkType? DetectFramework(string? responseContent, string? contentType);
}

public sealed class ClientFrameworkDetector : IClientFrameworkDetector
{
    /// <summary>Maximum bytes to inspect in response body.</summary>
    private const int MaxInspectionLength = 32768; // 32 KB safe limit

    /// <summary>
    /// Blazor WASM detection markers:
    /// - X-Blazor-Environment header (checked separately, not here)
    /// - _framework/blazor.webassembly.js
    /// - importmap with blazor.webassembly.js
    /// - Microsoft.Authentication.WebAssembly.Msal
    /// - id="blazor-error-ui" or id="app" with Blazor bootstrap
    /// </summary>
    private static readonly string[] BlazorMarkers =
    [
        "_framework/blazor.webassembly",
        "blazor.webassembly.js",
        "Microsoft.Authentication.WebAssembly.Msal",
        "blazor-error-ui",
        "./_framework/dotnet",
        "dotnet.native.js"
    ];

    public ClientFrameworkType? DetectFramework(string? responseContent, string? contentType)
    {
        // Only inspect HTML content
        if (string.IsNullOrWhiteSpace(responseContent) ||
            string.IsNullOrWhiteSpace(contentType) ||
            !contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Bound inspection to prevent large memory usage
        var inspectionLength = Math.Min(responseContent.Length, MaxInspectionLength);
        var contentToInspect = responseContent[..inspectionLength];

        // Check for Blazor WASM with positive markers
        if (HasBlazorMarkers(contentToInspect))
            return ClientFrameworkType.BlazorWebAssembly;

        // Future: React, Angular, Vue detection can be added here with similar positive markers

        return null;
    }

    /// <summary>
    /// Check if content has multiple positive Blazor WASM indicators.
    /// Requires at least one framework marker to avoid false positives.
    /// </summary>
    private static bool HasBlazorMarkers(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;

        var markerCount = 0;
        foreach (var marker in BlazorMarkers)
        {
            if (content.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                markerCount++;
                // Found at least one Blazor marker, this is likely Blazor WASM
                return true;
            }
        }

        return false;
    }
}
