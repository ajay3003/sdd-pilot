using System.Text.Json.Serialization;
using BirkNext.ApiReview;
using BirkNext.PerformanceTests;

namespace BirkNext.QualityEvidence;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Shared Quality Evidence. Browser, API, security and performance providers emit one normalized evidence item per observed fact, each
// tied to its execution and context (target, environment, access mode, authentication context). One safe execution can emit several
// kinds (a GET → API status + security headers + timing) without being repeated. Consumers (FQR, AQR, Performance, Environment Analysis,
// Impact, Test Evidence, traceability, Technology Coverage) read these items instead of rediscovering. Metric ≠ threshold ≠ assessment;
// declared security ≠ observed enforcement; passive timing ≠ load test; an observation from another target/auth context is historical.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityEvidenceKind { Browser, Api, Security, Performance, Accessibility }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityCapabilityKind { PublicHttp, AuthenticatedHttp, BrowserDom, AuthenticatedBrowserDom, CompanionLiveDom, CdpTarget, ContainerRuntime, LoadGenerator, RecordedBrowserEvidence }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QualityEvidenceCurrency { Current, Historical, Incompatible }

public sealed record QualityEvidenceProviderDescriptor(string Key, string Name, IReadOnlyList<QualityEvidenceKind> Emits, IReadOnlyList<QualityCapabilityKind> Requires,
    string Proves, string CannotProve);

public static class QualityEvidenceProviders
{
    public static IReadOnlyList<QualityEvidenceProviderDescriptor> All { get; } =
    [
        new("api-safe-request", "Safe API request (AQR)", [QualityEvidenceKind.Api, QualityEvidenceKind.Security, QualityEvidenceKind.Performance], [QualityCapabilityKind.PublicHttp, QualityCapabilityKind.AuthenticatedHttp],
            "One GET/HEAD/OPTIONS response yields status, content type, contract fit, security headers, CORS/rate-limit headers and timing — no repeated request.",
            "Single-request timing is not load behaviour; header presence is not full security."),
        new("api-security-probes", "API security probes (AQR)", [QualityEvidenceKind.Security], [QualityCapabilityKind.PublicHttp],
            "Anonymous enforcement, CORS preflight, documentation exposure — each a separate, bounded safe request.",
            "Authentication enforcement observed for a request is not full authorization correctness."),
        new("http-security", "HTTP Security (FQR)", [QualityEvidenceKind.Security], [QualityCapabilityKind.PublicHttp, QualityCapabilityKind.AuthenticatedHttp],
            "Document response headers, cookies and framework asset exposure.", "Runtime behaviour beyond the fetched document."),
        new("http-performance", "HTTP Performance (FQR)", [QualityEvidenceKind.Performance], [QualityCapabilityKind.PublicHttp, QualityCapabilityKind.AuthenticatedHttp],
            "Passive download timing, sizes, compression and caching of the frontend and its assets.", "Passive timing is not a load test and not field Core Web Vitals."),
        new("passive-web-security", "Passive Web Security (ZAP, FQR)", [QualityEvidenceKind.Security], [QualityCapabilityKind.ContainerRuntime, QualityCapabilityKind.PublicHttp],
            "Passive scanner alerts on proxied responses.", "No active scan; an alert is not an exploit."),
        new("browser-companion", "Browser Companion (live page)", [QualityEvidenceKind.Browser, QualityEvidenceKind.Performance, QualityEvidenceKind.Accessibility], [QualityCapabilityKind.CompanionLiveDom],
            "DOM, runtime errors, axe results and navigation/LCP/CLS/INP from the person's own browser page.",
            "One visited page in one session; not a lab measurement and not every page."),
        new("playwright-browser", "Browser runtime / accessibility (Playwright)", [QualityEvidenceKind.Browser, QualityEvidenceKind.Accessibility], [QualityCapabilityKind.BrowserDom],
            "Headless Chromium page load, console/runtime errors and axe results.", "Not required when compatible Companion DOM evidence already covers accessibility."),
        new("lighthouse", "Lighthouse (execution provider)", [QualityEvidenceKind.Performance, QualityEvidenceKind.Accessibility], [QualityCapabilityKind.CdpTarget],
            "Lab metrics (LCP, CLS, TBT, …) for one page under Lighthouse's emulation.", "Lab metrics are not field data."),
        new("browser-performance-quality", "Browser Performance Quality", [QualityEvidenceKind.Performance], [QualityCapabilityKind.RecordedBrowserEvidence],
            "Assessment of recorded Companion/proxy timings against thresholds.", "Percentiles only from at least 5 samples."),
        new("load-test", "Performance Test Review (k6)", [QualityEvidenceKind.Performance], [QualityCapabilityKind.LoadGenerator],
            "Load metrics (throughput, latency percentiles, error rate) for a defined scenario.", "A load result belongs to its scenario, environment and load profile."),
        new("declared-security", "Security Configuration Review (declared)", [QualityEvidenceKind.Security], [],
            "Security controls declared in source/configuration against approved expectations.", "Declared is not observed enforcement."),
    ];

    public static QualityEvidenceProviderDescriptor Get(string key) => All.First(p => p.Key == key);
}

/// <summary>The context an observation belongs to. A different target, environment, access mode or authentication context makes it historical.</summary>
public sealed record QualityEvidenceContext(string TargetOrigin, string EnvironmentId, string AccessMode, string? AuthContextDigest);

/// <summary>One normalized observation: what was seen (subject, signal, value), by which provider and execution, in which context. Never a
/// threshold verdict by itself; secrets, tokens, cookies and authorization headers are never values here.</summary>
public sealed record QualityEvidenceItem(QualityEvidenceKind Kind, string Provider, string ExecutionId, string Subject, string Signal, string? Value, string? Unit,
    DateTimeOffset ObservedAt, QualityEvidenceContext Context, string Nature = "Observed");

/// <summary>A metric observation kept apart from the threshold applied to it and from the resulting assessment.</summary>
public sealed record QualityMetric(string Name, double? Value, string Unit, int Samples, string Measurement, string Subject);
public sealed record QualityThresholdRef(string Name, double? Warning, double? Poor, string Source);
public sealed record QualityAssessment(QualityMetric Metric, QualityThresholdRef? Threshold, string State, string Basis);

/// <summary>A security control's declared vs observed state — never collapsed.</summary>
public sealed record SecurityControlEvidence(string Control, string Declared, string Observed, string State, IReadOnlyList<string> Surfaces);

/// <summary>A logical finding grouping equivalent observations across engines/reviews (e.g. one missing HSTS, many surfaces).</summary>
public sealed record QualityLogicalFinding(string Key, string Control, IReadOnlyList<string> Sources, IReadOnlyList<string> Surfaces);

/// <summary>Whether a quality engine can run, given the capabilities available now: optional and unavailable is non-blocking; required and
/// unavailable is a coverage/readiness gap — never an application defect.</summary>
public sealed record QualityCapabilityDecision(string Engine, bool Required, bool Available, string Outcome, string Reason);

public static class QualityEvidenceQueries
{
    /// <summary>Current only in the exact same target, environment, access mode and authentication context (and within the freshness window);
    /// another identity is never reused; a changed target/auth context makes old captures historical.</summary>
    public static QualityEvidenceCurrency Currency(QualityEvidenceItem item, QualityEvidenceContext current, DateTimeOffset now, TimeSpan maxAge)
    {
        if (!string.Equals(item.Context.TargetOrigin.TrimEnd('/'), current.TargetOrigin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(item.Context.EnvironmentId, current.EnvironmentId, StringComparison.Ordinal)) return QualityEvidenceCurrency.Incompatible;
        if (!string.Equals(item.Context.AccessMode, current.AccessMode, StringComparison.Ordinal) || item.Context.AuthContextDigest != current.AuthContextDigest
            || now - item.ObservedAt > maxAge) return QualityEvidenceCurrency.Historical;
        return QualityEvidenceCurrency.Current;
    }

    /// <summary>All evidence one AQR report holds, normalized: each executed operation's single response emits API (status), security
    /// (headers) and performance (timing) items — one request, several kinds.</summary>
    public static IReadOnlyList<QualityEvidenceItem> FromApiReview(ApiReviewReport report, string executionId)
    {
        var items = new List<QualityEvidenceItem>();
        foreach (var target in report.Targets)
        {
            var context = new QualityEvidenceContext(report.Environment.TargetUrl, report.Environment.EnvironmentId, target.AccessMode.ToString(), report.Environment.ContextIdentityDigest);
            foreach (var op in target.Operations.Where(o => o.Executed && !o.Historical))
            {
                var subject = $"{op.Method} {target.Target.Url.TrimEnd('/')}{op.Path}";
                items.Add(new(QualityEvidenceKind.Api, "api-safe-request", executionId, subject, "HTTP status", op.StatusCode.ToString(), null, report.GeneratedAt, context));
                if (op.ElapsedMs is { } ms)
                    items.Add(new(QualityEvidenceKind.Performance, "api-safe-request", executionId, subject, "Single request latency", ms.ToString("0", System.Globalization.CultureInfo.InvariantCulture), "ms", report.GeneratedAt, context, "Observed (passive, one request)"));
            }
            foreach (var header in target.SecurityHeaders)
                items.Add(new(QualityEvidenceKind.Security, "api-safe-request", executionId, target.Target.Origin, header.Header, header.Present ? "Present" : "Missing", null, report.GeneratedAt, context));
            if (target.AuthenticationEnforcement is { } enforcement)
                items.Add(new(QualityEvidenceKind.Security, "api-security-probes", executionId, target.Target.Origin, "Authentication enforcement (anonymous request)", enforcement.Status.ToString(), null, report.GeneratedAt, context,
                    "Observed for one anonymous request — not full authorization correctness"));
        }
        return items;
    }

    /// <summary>Load-test evidence of one run: metrics as observed, separate from the thresholds applied and from the verdict.</summary>
    public static IReadOnlyList<QualityAssessment> FromLoadTest(PerformanceTestRun run)
    {
        if (run.Metrics is not { } m) return [];
        var samples = (int)Math.Min(int.MaxValue, m.RequestCount);
        QualityMetric Metric(string name, double? value, string unit) => new(name, value, unit, samples, "Load test (k6 scenario)", run.TargetOrigin);
        var metrics = new[] { Metric("p95 latency", m.Latency.P95Ms, "ms"), Metric("Error rate", m.ErrorRatePercent, "%"), Metric("Throughput", m.RequestsPerSecond, "req/s") };
        return [.. metrics.Select(metric =>
        {
            var threshold = run.ThresholdResults.FirstOrDefault(t => t.Metric.ToString().Contains(metric.Name.Split(' ')[0], StringComparison.OrdinalIgnoreCase));
            return new QualityAssessment(metric, threshold is null ? null : new QualityThresholdRef(threshold.Metric.ToString(), threshold.Expected, null, threshold.Severity.ToString()),
                threshold is null ? "No threshold — observation only" : threshold.Outcome.ToString(),
                threshold is null ? "A metric without a threshold is not assessed." : $"{threshold.Severity} threshold from the test definition: {threshold.Explanation}");
        })];
    }

    /// <summary>Percentiles are only meaningful from enough samples: below 5 there is no p95, only the observed values.</summary>
    public static QualityMetric Percentile(string name, IReadOnlyList<double> samples, double percentile, string unit, string measurement, string subject)
    {
        if (samples.Count < 5) return new($"{name} (no p{percentile:0} — {samples.Count} sample(s))", samples.Count == 0 ? null : samples.Max(), unit, samples.Count, measurement, subject);
        var sorted = samples.Order().ToList();
        var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return new($"{name} p{percentile:0}", sorted[Math.Clamp(index, 0, sorted.Count - 1)], unit, samples.Count, measurement, subject);
    }

    public static string Measurement(string provider) => provider switch
    {
        "load-test" => "Load test",
        "lighthouse" => "Lab (Lighthouse emulation)",
        "browser-companion" => "Observed in a real browser session",
        _ => "Passive (single request timing)",
    };

    /// <summary>Canonical control for equivalent security observations from different engines/reviews (FQR HTTP Security, ZAP, AQR).</summary>
    public static string? SecurityControl(string ruleOrHeader)
    {
        var s = ruleOrHeader.ToLowerInvariant();
        return s.Contains("strict-transport") || s.Contains("hsts") || s == "10035" ? "HSTS"
            : s.Contains("content-security-policy") || s.Contains("csp") || s == "10038" ? "Content-Security-Policy"
            : s.Contains("nosniff") || s.Contains("content-type-options") || s == "10021" ? "X-Content-Type-Options"
            : s.Contains("frame-options") || s.Contains("clickjack") || s == "10020" ? "Frame protection"
            : s.Contains("referrer-policy") ? "Referrer-Policy"
            : s.Contains("permissions-policy") ? "Permissions-Policy"
            : null;
    }

    /// <summary>One logical finding per (control, origin) across engines and reviews; each source observation is kept.</summary>
    public static IReadOnlyList<QualityLogicalFinding> Deduplicate(IEnumerable<(string Source, string RuleOrHeader, string Surface)> observations) =>
        [.. observations.Select(o => (o.Source, Control: SecurityControl(o.RuleOrHeader), o.Surface, Origin: Origin(o.Surface))).Where(o => o.Control is not null)
            .GroupBy(o => $"{o.Control}|{o.Origin}", StringComparer.OrdinalIgnoreCase)
            .Select(g => new QualityLogicalFinding(g.Key, g.First().Control!, [.. g.Select(x => x.Source).Distinct()], [.. g.Select(x => x.Surface).Distinct()]))];

    private static string Origin(string surface) => Uri.TryCreate(surface.Split(' ').Last(), UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Authority}" : surface;

    /// <summary>Declared (Security Configuration Review) vs observed (runtime) for one control.</summary>
    public static SecurityControlEvidence Control(string control, bool? declared, bool? observed, IReadOnlyList<string> surfaces) =>
        new(control, declared is null ? "Not declared in source" : declared.Value ? "Declared" : "Declared off",
            observed is null ? "Not observed" : observed.Value ? "Observed" : "Not present at runtime",
            (declared, observed) switch
            {
                (true, true) => "Declared and observed",
                (true, false) => "Declared but not observed at runtime — review",
                (null or false, true) => "Observed at runtime (not declared in source)",
                (_, null) => "Runtime not assessed",
                _ => "Neither declared nor observed",
            }, surfaces);

    /// <summary>Per-engine capability decision: engines plan independently (public HTTP and authenticated browser engines coexist).</summary>
    public static QualityCapabilityDecision Decide(string engine, bool required, IReadOnlyList<QualityCapabilityKind> needsAnyOf, IReadOnlySet<QualityCapabilityKind> available)
    {
        var ok = needsAnyOf.Count == 0 || needsAnyOf.Any(available.Contains);
        return new(engine, required, ok,
            ok ? "Runs" : required ? "Coverage gap (required capability unavailable)" : "Skipped (optional, non-blocking)",
            ok ? $"Uses {string.Join(" / ", needsAnyOf.Where(available.Contains))}"
                : $"Needs {string.Join(" or ", needsAnyOf)}; {(required ? "a readiness problem of the review, not an application defect" : "optional engines never block the review")}.");
    }

    /// <summary>Quality evidence on changed surfaces (paths/endpoints) — for Impact Analysis.</summary>
    public static IReadOnlyList<QualityEvidenceItem> ForSurfaces(IEnumerable<QualityEvidenceItem> items, IEnumerable<string> changedPaths)
    {
        var paths = changedPaths.Where(p => p.Length > 1).ToList();
        return [.. items.Where(i => paths.Any(p => i.Subject.Contains(p, StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>Validation evidence for a requirement (traceability): observations only; "verified" is never claimed.</summary>
    public static IReadOnlyList<string> ForRequirement(IEnumerable<QualityEvidenceItem> items, string requirement)
    {
        var text = requirement.ToLowerInvariant();
        var result = new List<string>();
        if (text.Contains("hsts") || text.Contains("security header") || text.Contains("https"))
            result.AddRange(items.Where(i => i.Kind == QualityEvidenceKind.Security).Select(i => $"{i.Signal}: {i.Value} on {i.Subject} ({i.Provider}, {i.ObservedAt:yyyy-MM-dd})"));
        if (text.Contains("respon") || text.Contains("latency") || text.Contains("performance"))
            result.AddRange(items.Where(i => i.Kind == QualityEvidenceKind.Performance).Select(i => $"{i.Signal}: {i.Value}{i.Unit} on {i.Subject} — {i.Nature}"));
        return result.Distinct().Take(20).ToList();
    }
}
