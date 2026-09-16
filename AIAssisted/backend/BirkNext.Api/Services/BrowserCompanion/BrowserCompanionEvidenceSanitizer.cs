using System.Text.RegularExpressions;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.BrowserCompanion;

namespace BirkNext.Api.Services.BrowserCompanion;

/// <summary>
/// Central sanitizer for Browser Companion evidence. Every companion message passes through here before it is stored or
/// exposed: strings are redacted (Authorization/Bearer/JWT/e-mail/token-shaped values) and length-capped, URLs lose query
/// strings and userinfo, selectors are reduced to structural tokens, and every list is bounded. The extension sanitizes on
/// its side too; this layer is the authoritative one and is applied regardless of what the extension sent.
/// </summary>
public sealed class BrowserCompanionEvidenceSanitizer(BrowserEvidenceSanitizer inner)
{
    private static readonly Regex QueryOrFragment = new(@"[?#].*$", RegexOptions.Compiled);
    private static readonly Regex UnsafeSelectorToken = new(@"\[[^\]]*\]|=|\""|'|@|:contains|\d{6,}", RegexOptions.Compiled);
    private static readonly Regex LongOpaque = new(@"\b[A-Za-z0-9_-]{48,}\b", RegexOptions.Compiled);

    public string Text(string? value, int max = BrowserCompanionLimits.MaxStringLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var s = inner.SanitizeMessage(value);
        s = LongOpaque.Replace(s, "[REDACTED]");
        return s.Length > max ? s[..(max - 1)] + "…" : s;
    }

    /// <summary>Scheme + host + path only.</summary>
    public string Url(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var stripped = QueryOrFragment.Replace(value.Trim(), "");
        if (Uri.TryCreate(stripped, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "data" or "blob" or "javascript") return uri.Scheme + ":[REDACTED]";
            stripped = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        }
        return Text(stripped, 400);
    }

    /// <summary>Structural selectors only; anything carrying attribute values, quotes, e-mail-like or numeric identifiers is rejected.</summary>
    public string? Selector(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        if (s.Length > BrowserCompanionLimits.MaxSelectorLength) s = s[..BrowserCompanionLimits.MaxSelectorLength];
        // Allow the role attribute selector the extension emits ([role=button]); reject any other attribute selector.
        var withoutRole = Regex.Replace(s, @"\[role=[a-z-]+\]", "");
        if (UnsafeSelectorToken.IsMatch(withoutRole)) return null;
        return inner.SanitizeMessage(s) == s ? s : null;
    }

    public BrowserPageEvidence Sanitize(BrowserPageEvidence evidence)
    {
        var origin = Url(evidence.PageOrigin).TrimEnd('/');
        var path = NormalizePath(evidence.PagePath);
        return new BrowserPageEvidence
        {
            ProfileId = Text(evidence.ProfileId, 64),
            PageOrigin = origin,
            PagePath = path,
            VisitStartedAt = evidence.VisitStartedAt,
            CapturedAt = evidence.CapturedAt,
            SnapshotKind = evidence.SnapshotKind is "initial" or "update" or "final" ? evidence.SnapshotKind : "update",
            SnapshotSequence = Math.Max(0, evidence.SnapshotSequence),
            DocumentTitle = string.IsNullOrWhiteSpace(evidence.DocumentTitle) ? null : Text(evidence.DocumentTitle, 200),
            BrowserName = string.IsNullOrWhiteSpace(evidence.BrowserName) ? null : Text(evidence.BrowserName, 60),
            Dom = evidence.Dom is null ? null : SanitizeDom(evidence.Dom),
            Accessibility = evidence.Accessibility is null ? null : SanitizeAccessibility(evidence.Accessibility),
            Performance = evidence.Performance is null ? null : SanitizePerformance(evidence.Performance),
            Runtime = evidence.Runtime is null ? null : SanitizeRuntime(evidence.Runtime),
            Blazor = evidence.Blazor is null ? null : SanitizeBlazor(evidence.Blazor),
        };
    }

    public static string NormalizePath(string? path)
    {
        var p = QueryOrFragment.Replace(path ?? "", "");
        while (p.Length > 1 && p.EndsWith('/')) p = p[..^1];
        if (p.Length == 0) p = "/";
        return p.Length > 400 ? p[..400] : p;
    }

    private BrowserDomSummary SanitizeDom(BrowserDomSummary dom) => new()
    {
        NodeCount = Clamp(dom.NodeCount), MaxDepth = Clamp(dom.MaxDepth), InteractiveCount = Clamp(dom.InteractiveCount),
        FormControlCount = Clamp(dom.FormControlCount), IframeCount = Clamp(dom.IframeCount), ImageCount = Clamp(dom.ImageCount),
        HeadingCounts = dom.HeadingCounts.Where(kv => kv.Key is "h1" or "h2" or "h3" or "h4" or "h5" or "h6").ToDictionary(kv => kv.Key, kv => Clamp(kv.Value)),
        HeadingOrder = dom.HeadingOrder.Where(l => l is >= 1 and <= 6).Take(200).ToList(),
        Landmarks = dom.Landmarks.Where(kv => Regex.IsMatch(kv.Key, "^[a-z]{1,20}$")).ToDictionary(kv => kv.Key, kv => Clamp(kv.Value)),
        DuplicateIdCount = Clamp(dom.DuplicateIdCount), HiddenFocusableCount = Clamp(dom.HiddenFocusableCount),
        DialogCount = Clamp(dom.DialogCount), PositiveTabIndexCount = Clamp(dom.PositiveTabIndexCount),
    };

    private BrowserAccessibilitySummary SanitizeAccessibility(BrowserAccessibilitySummary a11y) => new()
    {
        Engine = "BirkNext Accessibility Checks",
        RulesEvaluated = Clamp(a11y.RulesEvaluated),
        Findings = a11y.Findings
            .Where(f => Regex.IsMatch(f.RuleId ?? "", "^[a-z0-9-]{3,60}$"))
            .Take(BrowserCompanionLimits.MaxAccessibilityRulesPerPage)
            .Select(f => new BrowserAccessibilityRuleResult
            {
                RuleId = f.RuleId, Severity = f.Severity is "Critical" or "High" or "Medium" or "Low" or "Info" ? f.Severity : "Low",
                Wcag = string.IsNullOrWhiteSpace(f.Wcag) ? null : Text(f.Wcag, 16), Title = Text(f.Title, 120), Guidance = Text(f.Guidance, 240),
                Count = Clamp(f.Count),
                Selectors = f.Selectors.Select(Selector).Where(s => s is not null).Cast<string>().Distinct().Take(BrowserCompanionLimits.MaxSelectorsPerRule).ToList(),
            }).ToList(),
    };

    private BrowserPerformanceSummary SanitizePerformance(BrowserPerformanceSummary p) => new()
    {
        TtfbMs = Metric(p.TtfbMs), DomContentLoadedMs = Metric(p.DomContentLoadedMs), LoadEventMs = Metric(p.LoadEventMs),
        NavigationType = string.IsNullOrWhiteSpace(p.NavigationType) ? null : Text(p.NavigationType, 20),
        FirstContentfulPaintMs = Metric(p.FirstContentfulPaintMs), LcpMs = Metric(p.LcpMs), Cls = Metric(p.Cls),
        LongTaskCount = Clamp(p.LongTaskCount), LongTaskTotalMs = Metric(p.LongTaskTotalMs), LongestTaskMs = Metric(p.LongestTaskMs),
        ResourceCount = Clamp(p.ResourceCount), TransferredBytes = ClampBytes(p.TransferredBytes), JsBytes = ClampBytes(p.JsBytes), CssBytes = ClampBytes(p.CssBytes),
        ImageBytes = ClampBytes(p.ImageBytes), WasmBytes = ClampBytes(p.WasmBytes), FontBytes = ClampBytes(p.FontBytes), ApiBytes = ClampBytes(p.ApiBytes),
        FrameworkDataBytes = ClampBytes(p.FrameworkDataBytes), OtherBytes = ClampBytes(p.OtherBytes),
        DuplicateFetchCount = Clamp(p.DuplicateFetchCount), DuplicateResources = Resources(p.DuplicateResources),
        FailedResourceCount = Clamp(p.FailedResourceCount), FailedResources = Resources(p.FailedResources), LongestResources = Resources(p.LongestResources),
        UnsupportedMetrics = p.UnsupportedMetrics.Select(m => Text(m, 40)).Take(20).ToList(),
    };

    private BrowserRuntimeSummary SanitizeRuntime(BrowserRuntimeSummary r) => new()
    {
        ErrorCount = Clamp(r.ErrorCount), RejectionCount = Clamp(r.RejectionCount), ResourceFailureCount = Clamp(r.ResourceFailureCount),
        ConsoleCaptured = false,
        Errors = r.Errors.Take(BrowserCompanionLimits.MaxRuntimeErrorsPerPage).Select(e => new BrowserRuntimeError
        {
            Kind = e.Kind is "error" or "unhandledrejection" or "resource" ? e.Kind : "error",
            Message = Text(e.Message), Source = string.IsNullOrWhiteSpace(e.Source) ? null : Url(e.Source),
            Count = Clamp(e.Count), FirstAt = e.FirstAt, LastAt = e.LastAt,
        }).ToList(),
    };

    private BrowserBlazorSummary SanitizeBlazor(BrowserBlazorSummary b) => new()
    {
        Detected = b.Detected, BlazorScriptPresent = b.BlazorScriptPresent, BootManifestObserved = b.BootManifestObserved, BootManifestFailed = b.BootManifestFailed,
        FrameworkResourceCount = Clamp(b.FrameworkResourceCount), FrameworkBytes = ClampBytes(b.FrameworkBytes), WasmBytes = ClampBytes(b.WasmBytes),
        FrameworkFailures = Resources(b.FrameworkFailures), RepeatedFrameworkDownloads = Clamp(b.RepeatedFrameworkDownloads), ErrorUiVisible = b.ErrorUiVisible,
    };

    private List<BrowserResourceEntry> Resources(IEnumerable<BrowserResourceEntry> entries) => entries
        .Take(BrowserCompanionLimits.MaxResourcesPerList)
        .Select(e => new BrowserResourceEntry
        {
            Url = Url(e.Url), Kind = Regex.IsMatch(e.Kind ?? "", "^[a-z-]{1,20}$") ? e.Kind! : "other",
            DurationMs = Metric(e.DurationMs), TransferBytes = e.TransferBytes is { } t ? ClampBytes(t) : null,
            Status = e.Status is >= 0 and <= 999 ? e.Status : null, Count = Clamp(e.Count),
        }).ToList();

    private static int Clamp(int value) => Math.Clamp(value, 0, 10_000_000);
    private static long ClampBytes(long value) => Math.Clamp(value, 0, 100L * 1024 * 1024 * 1024);
    private static double? Metric(double? value) => value is { } v && double.IsFinite(v) && v >= 0 ? Math.Round(v, 4) : null;
}
