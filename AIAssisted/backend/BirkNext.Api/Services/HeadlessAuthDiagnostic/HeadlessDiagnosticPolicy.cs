using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BirkNext.HeadlessAuthDiagnostic;
using BirkNext.Api.Services.BrowserAutomationDiagnostic;
using BirkNext.Api.Services.ManagedEdge;

namespace BirkNext.Api.Services.HeadlessAuthDiagnostic;

/// <summary>
/// The single source of truth for "may the Headless Authentication &amp; Session Control Diagnostic run against this
/// target". Written by the Browser Automation Diagnostic endpoint, read by the authentication diagnostic; correlated
/// by Target Environment id, URL and type, and valid for 30 minutes.
///
/// What it stores is <see cref="BrowserAutomationDiagnosticReport.HeadlessAutomationControlAfterTargetNavigation"/>:
/// headless automation stayed in stable control through the target navigation and ended on the target origin or at
/// its authentication handoff. Deliberately NOT "target application identified" — a valid redirect to Entra is what
/// the authentication diagnostic exists to inspect.
/// </summary>
public sealed class BrowserAutomationEvidenceStore(TimeProvider? clock = null)
{
    public static readonly TimeSpan Validity = TimeSpan.FromMinutes(30);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (bool Passed, string Id, DateTimeOffset Time, string Where)> _results = new();
    private static string Key(string id, string url, string type) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{id}\n{url}\n{type}")));
    public void Record(BrowserAutomationDiagnosticReport report)
    {
        var now = _clock.GetUtcNow();
        foreach (var entry in _results.Where(e => e.Value.Time < now - Validity)) _results.TryRemove(entry.Key, out _);
        // Bound memory even if many distinct targets are submitted.
        if (_results.Count > 1000) _results.Clear();
        _results[Key(report.TargetEnvironmentId, report.TargetUrl, report.TargetEnvironmentType)] =
            (report.HeadlessAutomationControlAfterTargetNavigation, report.DiagnosticId, now, Where(report));
    }
    public HeadlessPrerequisite Check(HeadlessDiagnosticRequest request) =>
        _results.TryGetValue(Key(request.TargetEnvironmentId, request.TargetUrl, request.EnvironmentType), out var result)
        && result.Passed && result.Time >= _clock.GetUtcNow() - Validity
            ? new(true, $"Headless browser automation remained controllable through target navigation ({result.Where}), as demonstrated by the Browser Automation Diagnostic (valid for 30 minutes).", result.Id)
            : new(false, "Headless browser automation cannot yet be confirmed to stay controllable through navigation to this target. Run Browser Automation Diagnostic successfully for this exact target first.");

    private static string Where(BrowserAutomationDiagnosticReport report) => report.Headless?.Target?.FinalLocation switch
    {
        BrowserAutomationFinalLocation.AuthenticationAuthority => $"reached the authentication redirect at {report.Headless.Target.AuthenticationHost}",
        BrowserAutomationFinalLocation.SessionControlProxy => "reached the session-control proxy",
        BrowserAutomationFinalLocation.TargetOrigin => "stayed on the target origin",
        _ => "location not established",
    };
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
    /// <summary>
    /// Optional structural marker of the application shell that exists BEFORE sign-in (a stable root element such as
    /// <c>[data-app-shell]</c>). The Browser Automation Diagnostic uses it, after the expected origin, to identify the
    /// target application. Never page text, never authenticated content.
    /// </summary>
    public string ApplicationShellSelector { get; set; } = "";

    /// <summary>The shell marker for a Target Environment — only when its configured origin matches the target URL's.</summary>
    public static string? ApplicationMarkerFor(HeadlessDiagnosticOptions options, string targetEnvironmentId, string targetUrl) =>
        options.Verification.GetValueOrDefault(targetEnvironmentId) is { ApplicationShellSelector.Length: > 0 } contract
        && Uri.TryCreate(contract.TargetOrigin, UriKind.Absolute, out var origin)
        && Uri.TryCreate(targetUrl, UriKind.Absolute, out var target)
        && BirkNext.Api.Services.AuthenticatedReview.AuthenticationOriginPolicy.SameOrigin(origin, target)
            ? contract.ApplicationShellSelector
            : null;
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
