using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BirkNext.HeadlessAuthDiagnostic;
using BirkNext.Api.Services.BrowserAutomationDiagnostic;
using BirkNext.Api.Services.ManagedEdge;

namespace BirkNext.Api.Services.HeadlessAuthDiagnostic;

public sealed class BrowserAutomationEvidenceStore
{
    private readonly ConcurrentDictionary<string, (bool Passed, string Id, DateTimeOffset Time)> _results = new();
    private static string Key(string id, string url, string type) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{id}\n{url}\n{type}")));
    public void Record(BrowserAutomationDiagnosticReport report)
    {
        foreach (var entry in _results.Where(e => e.Value.Time < DateTimeOffset.UtcNow.AddMinutes(-30))) _results.TryRemove(entry.Key, out _);
        // Bound memory even if many distinct targets are submitted.
        if (_results.Count > 1000) _results.Clear();
        _results[Key(report.TargetEnvironmentId, report.TargetUrl, report.TargetEnvironmentType)] =
            (report.HeadlessTargetControlAvailable,
                report.DiagnosticId, DateTimeOffset.UtcNow);
    }
    public HeadlessPrerequisite Check(HeadlessDiagnosticRequest request) =>
        _results.TryGetValue(Key(request.TargetEnvironmentId, request.TargetUrl, request.EnvironmentType), out var result)
        && result.Passed && result.Time >= DateTimeOffset.UtcNow.AddMinutes(-30)
            ? new(true, "Target control demonstrated by Browser Automation Diagnostic (valid for 30 minutes).", result.Id)
            : new(false, "Playwright cannot yet be confirmed to retain automation control of this target long enough to evaluate authentication. Run Browser Automation Diagnostic successfully for this exact target first.");
}

public sealed class HeadlessDiagnosticOptions
{
    // Explicit, non-secret application shell contracts, keyed by Target Environment ID. Never a generic body/cookie check.
    public Dictionary<string, HeadlessVerificationContract> Verification { get; set; } = [];
    public string[] AdditionalNonProductionTypes { get; set; } = [];
}
public sealed class HeadlessVerificationContract
{
    public string TargetOrigin { get; set; } = "";
    public string AuthenticatedSelector { get; set; } = "";
}

internal static class HeadlessDiagnosticPolicy
{
    public static string ProfileRoot => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BirkNext", "HeadlessAuthDiagnosticEdgeProfile");
    public static bool IsDedicatedProfile(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || ManagedEdgePreflightService.IsNormalEdgeProfile(path)) return false;
        var parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.Equals(parent, System.IO.Path.GetFullPath(ProfileRoot), StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(System.IO.Path.GetFileName(path), "N", out _)) return false;
        // Refuse junctions/symlinks anywhere in the ancestry, so a dedicated name cannot alias personal data.
        for (var node = new DirectoryInfo(path); node is not null; node = node.Parent)
            if (node.Exists && (node.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    public static string? BlockedReason(HeadlessDiagnosticRequest r, string profile, HeadlessDiagnosticOptions options)
    {
        var type = r.EnvironmentType.Trim();
        if (type.Equals("Production", StringComparison.OrdinalIgnoreCase) || type.Equals("Prod", StringComparison.OrdinalIgnoreCase)
            || !(new[] { "Development", "QA", "Test", "Local", "RC" }.Concat(options.AdditionalNonProductionTypes).Contains(type, StringComparer.OrdinalIgnoreCase)))
            return "Headless authentication diagnostics are limited to explicitly non-production environments.";
        if (string.IsNullOrWhiteSpace(r.TargetEnvironmentId) || string.IsNullOrWhiteSpace(r.TargetUrl)) return "Select a Target Environment with a target URL.";
        if (!Uri.TryCreate(r.TargetUrl, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http") || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0)
            return "Use an absolute HTTP(S) target URL without credentials, query or fragment authentication data.";
        if (TargetEnvironmentDetection.TargetEnvironmentTypeClassifier.Infer(url.IdnHost) == Models.FrontendEnvironmentType.Production)
            return "Headless authentication diagnostics are limited to non-production environments; this hostname indicates Production.";
        if (!IsDedicatedProfile(profile)) return "A fresh dedicated headless diagnostic profile is required. Normal Edge, Companion and proxy profiles are refused.";
        return null;
    }
}
