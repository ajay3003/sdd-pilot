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

    /// <summary>
    /// An identifier BirkNext itself issued (the Target Environment id), reduced to identifier characters and length-capped.
    /// It is deliberately NOT put through <see cref="Text"/>: that is the credential redaction for free text observed on a page,
    /// and it rewrites anything token-shaped — including a 32-character <c>Guid.NewGuid().ToString("N")</c> environment id, which
    /// is exactly the form BirkNext issues. Redacting an identity the backend is about to compare against its own session turns
    /// every page into a foreign one. Nothing from the page reaches this field: it is echoed back from pairing.
    /// </summary>
    public static string Identifier(string? value, int max = 64) =>
        string.IsNullOrWhiteSpace(value) ? "" : new string(value.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').Take(max).ToArray());

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
            ProfileId = Identifier(evidence.ProfileId),
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
        Axe = a11y.Axe is null ? null : new BrowserAxeEvidence
        {
            State = a11y.Axe.State == "Completed" && a11y.Axe.Version == AxeRuleCatalog.Version ? "Completed" : "Unavailable",
            Version = Regex.IsMatch(a11y.Axe.Version ?? "", @"^\d{1,2}\.\d{1,2}\.\d{1,2}$") ? a11y.Axe.Version : null,
            EvidenceVersion = Text(a11y.Axe.EvidenceVersion, 200),
            Rules = a11y.Axe.State != "Completed" || a11y.Axe.Version != AxeRuleCatalog.Version ? [] : a11y.Axe.Rules.Where(r => Regex.IsMatch(r.RuleId ?? "", "^[a-z0-9-]{1,80}$"))
                .Take(300).Select(r => new BrowserAxeRule
                {
                    RuleId = r.RuleId, Outcome = r.Outcome is "Pass" or "Fail" or "ManualReviewRequired" or "NotApplicable" ? r.Outcome : "NotTested",
                    CriterionIds = AxeRuleCatalog.Criteria(r.RuleId).ToList(),
                    Count = Clamp(r.Count)
                }).ToList()
        },
        Engine = "BirkNext Accessibility Checks",
        RulesEvaluated = Clamp(a11y.RulesEvaluated),
        VideoCount = a11y.VideoCount is { } video ? Clamp(video) : null,
        AudioCount = a11y.AudioCount is { } audio ? Clamp(audio) : null,
        MediaScopeComplete = a11y.MediaScopeComplete,
        NavigationStructure = a11y.NavigationStructure.Take(100).Select(Clamp).ToList(),
        ComponentStructure = a11y.ComponentStructure.Take(100).Select(Clamp).ToList(),
        Checks = a11y.Checks.Where(c => Regex.IsMatch(c.CheckId ?? "", "^[a-z0-9-]{3,60}$"))
            .Take(80).Select(c => new BrowserWcagCheck
            {
                CheckId = c.CheckId,
                Outcome = c.Outcome is "Pass" or "Fail" or "ManualReviewRequired" or "NotApplicable" or "NotTested" ? c.Outcome : "NotTested",
                Tested = Clamp(c.Tested), Failed = Clamp(c.Failed), Uncertain = Clamp(c.Uncertain),
                Selectors = c.Selectors.Select(Selector).Where(s => s is not null).Cast<string>().Distinct().Take(5).ToList(),
            }).ToList(),
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
        ObservationType = p.ObservationType is "spa-navigation" ? "spa-navigation" : "initial-load",
        TtfbMs = Metric(p.TtfbMs), DomContentLoadedMs = Metric(p.DomContentLoadedMs), LoadEventMs = Metric(p.LoadEventMs),
        NavigationType = string.IsNullOrWhiteSpace(p.NavigationType) ? null : Text(p.NavigationType, 20),
        FirstContentfulPaintMs = Metric(p.FirstContentfulPaintMs), LcpMs = Metric(p.LcpMs), Cls = Metric(p.Cls),
        StabilizationMs = Metric(p.StabilizationMs),
        StabilizedBy = p.StabilizedBy is "quiet" or "max-wait" ? p.StabilizedBy : null,
        Interaction = p.Interaction is null ? null : new BrowserInteractionSummary
        {
            Status = p.Interaction.Status is "measured" or "insufficient-samples" or "not-measured" or "not-supported" ? p.Interaction.Status : "not-measured",
            InteractionCount = Clamp(p.Interaction.InteractionCount), MinimumInteractions = Clamp(p.Interaction.MinimumInteractions),
            // An INP value is accepted only when the extension itself says it was measured: never fabricated on the backend either.
            InpMs = p.Interaction.Status == "measured" ? Metric(p.Interaction.InpMs) : null,
            LongestInteractionMs = Metric(p.Interaction.LongestInteractionMs), FirstInputDelayMs = Metric(p.Interaction.FirstInputDelayMs),
        },
        LongTaskCount = Clamp(p.LongTaskCount), LongTaskTotalMs = Metric(p.LongTaskTotalMs), LongestTaskMs = Metric(p.LongestTaskMs),
        MainThreadBlockingMs = Metric(p.MainThreadBlockingMs), LongTasksAfterStabilization = Clamp(p.LongTasksAfterStabilization),
        Mutations = p.Mutations is null ? null : new BrowserDomMutationSummary
        {
            BatchCount = Clamp(p.Mutations.BatchCount), MutationCount = Clamp(p.Mutations.MutationCount), LargestBatch = Clamp(p.Mutations.LargestBatch),
            LastMutationMs = Metric(p.Mutations.LastMutationMs), LargeBatchesAfterStabilization = Clamp(p.Mutations.LargeBatchesAfterStabilization),
        },
        JsHeapUsedBytes = p.JsHeapUsedBytes is { } heap && heap >= 0 ? ClampBytes(heap) : null,
        ResourceCount = Clamp(p.ResourceCount), CachedResourceCount = Clamp(p.CachedResourceCount), DecodedBytes = ClampBytes(p.DecodedBytes),
        TransferredBytes = ClampBytes(p.TransferredBytes), JsBytes = ClampBytes(p.JsBytes), CssBytes = ClampBytes(p.CssBytes),
        ImageBytes = ClampBytes(p.ImageBytes), WasmBytes = ClampBytes(p.WasmBytes), FontBytes = ClampBytes(p.FontBytes), ApiBytes = ClampBytes(p.ApiBytes),
        FrameworkDataBytes = ClampBytes(p.FrameworkDataBytes), OtherBytes = ClampBytes(p.OtherBytes),
        Categories = p.Categories.Where(c => Regex.IsMatch(c.Kind ?? "", "^[a-z-]{1,20}$")).Take(12).Select(c => new BrowserResourceCategorySummary
        {
            Kind = c.Kind, Count = Clamp(c.Count), TransferBytes = ClampBytes(c.TransferBytes), CachedCount = Clamp(c.CachedCount),
            Largest = c.Largest is null ? null : Resource(c.Largest), Slowest = c.Slowest is null ? null : Resource(c.Slowest),
        }).ToList(),
        LargestResource = p.LargestResource is null ? null : Resource(p.LargestResource),
        SlowestResource = p.SlowestResource is null ? null : Resource(p.SlowestResource),
        Timeline = p.Timeline.Take(BrowserCompanionLimits.MaxTimelineEntries).Select(Resource).ToList(),
        Collector = p.Collector is null ? null : new BrowserCollectorSummary
        {
            SnapshotBuildMs = Metric(p.Collector.SnapshotBuildMs), ObserverCallbacks = Clamp(p.Collector.ObserverCallbacks), SnapshotsSent = Clamp(p.Collector.SnapshotsSent),
            PayloadBytes = Clamp(p.Collector.PayloadBytes), EntriesExamined = Clamp(p.Collector.EntriesExamined),
        },
        DuplicateFetchCount = Clamp(p.DuplicateFetchCount), DuplicateResources = Resources(p.DuplicateResources),
        FailedResourceCount = Clamp(p.FailedResourceCount), FailedResources = Resources(p.FailedResources), LongestResources = Resources(p.LongestResources),
        UnsupportedMetrics = p.UnsupportedMetrics.Select(m => Text(m, 40)).Take(20).ToList(),
    };

    private BrowserRuntimeSummary SanitizeRuntime(BrowserRuntimeSummary r) => new()
    {
        ErrorCount = Clamp(r.ErrorCount), RejectionCount = Clamp(r.RejectionCount), ResourceFailureCount = Clamp(r.ResourceFailureCount),
        ErrorsBeforeStabilization = Clamp(r.ErrorsBeforeStabilization),
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
        BootManifestMs = Metric(b.BootManifestMs),
        FrameworkResourceCount = Clamp(b.FrameworkResourceCount), FrameworkBytes = ClampBytes(b.FrameworkBytes), WasmBytes = ClampBytes(b.WasmBytes),
        RuntimeResourceCount = Clamp(b.RuntimeResourceCount), RuntimeBytes = ClampBytes(b.RuntimeBytes),
        AssemblyCount = Clamp(b.AssemblyCount), AssemblyBytes = ClampBytes(b.AssemblyBytes),
        CultureResourceCount = Clamp(b.CultureResourceCount), CultureBytes = ClampBytes(b.CultureBytes), TimezoneDataObserved = b.TimezoneDataObserved,
        FrameworkJsCount = Clamp(b.FrameworkJsCount), CachedFrameworkResourceCount = Clamp(b.CachedFrameworkResourceCount),
        LoadKind = b.LoadKind is "cold" or "warm" or "mixed" or "none" or "unknown" ? b.LoadKind : "unknown",
        FrameworkLoadStartMs = Metric(b.FrameworkLoadStartMs), FrameworkLoadEndMs = Metric(b.FrameworkLoadEndMs),
        FrameworkFailures = Resources(b.FrameworkFailures), SlowestFrameworkResource = b.SlowestFrameworkResource is null ? null : Resource(b.SlowestFrameworkResource),
        RepeatedFrameworkDownloads = Clamp(b.RepeatedFrameworkDownloads), ErrorUiVisible = b.ErrorUiVisible,
    };

    private List<BrowserResourceEntry> Resources(IEnumerable<BrowserResourceEntry> entries) => entries
        .Take(BrowserCompanionLimits.MaxResourcesPerList)
        .Select(Resource).ToList();

    private BrowserResourceEntry Resource(BrowserResourceEntry e) => new()
    {
        Url = Url(e.Url), Kind = Regex.IsMatch(e.Kind ?? "", "^[a-z-]{1,20}$") ? e.Kind! : "other",
        DurationMs = Metric(e.DurationMs), TransferBytes = e.TransferBytes is { } t ? ClampBytes(t) : null,
        Status = e.Status is >= 0 and <= 999 ? e.Status : null, Count = Clamp(e.Count),
        NetworkCount = e.NetworkCount is { } n ? Clamp(n) : null, StartMs = Metric(e.StartMs),
        DecodedBytes = e.DecodedBytes is { } d ? ClampBytes(d) : null,
        Delivery = e.Delivery is "cache" or "network" ? e.Delivery : null,
    };

    private static int Clamp(int value) => Math.Clamp(value, 0, 10_000_000);
    private static long ClampBytes(long value) => Math.Clamp(value, 0, 100L * 1024 * 1024 * 1024);
    private static double? Metric(double? value) => value is { } v && double.IsFinite(v) && v >= 0 ? Math.Round(v, 4) : null;
}
