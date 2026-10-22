using BirkNext.ApiReview;
using BirkNext.Applicability;
using BirkNext.PerformanceTests;
using BirkNext.QualityEvidence;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.ApiQuality;

/// <summary>
/// Shared Quality Evidence: one safe request emits API, security and performance evidence without a second request; evidence carries its
/// context (another target/auth context is historical, never current); equivalent security observations from FQR engines and AQR are one
/// logical finding; metric ≠ threshold ≠ assessment; declared ≠ observed; per-engine capability decisions (optional gaps never block).
/// </summary>
public sealed class QualityEvidenceArchitectureTests
{
    private static ApiReviewReport Report() => new()
    {
        Environment = new ApiReviewEnvironmentSnapshot { EnvironmentId = "dev", TargetUrl = "https://app.example.invalid", ContextIdentityDigest = "ctx-1" },
        GeneratedAt = DateTimeOffset.Parse("2026-10-22T10:00:00Z"),
        Targets =
        [
            new ApiReviewTargetResult
            {
                Target = new ApiReviewTarget { Scheme = "https", Host = "api.example.invalid", Port = 443, BasePath = "/api" },
                AccessMode = ApiReviewAccessMode.PublicHttp,
                Operations = [new ApiReviewOperationResult { Method = "GET", Path = "/orders", Executed = true, StatusCode = 200, ElapsedMs = 182 }, new ApiReviewOperationResult { Method = "POST", Path = "/orders", Executed = false }],
                SecurityHeaders = [new("Strict-Transport-Security", true, false, null, SecurityHeaderOutcome.Missing, SecurityHeaderScope.Transport, "")],
                AuthenticationEnforcement = new ApiAuthenticationEnforcementResult { Status = ApiAuthenticationEnforcementStatus.Verified, Probe = "GET /api/orders", StatusCode = 401 },
            },
        ],
    };

    [Fact]
    public void One_safe_request_emits_api_security_and_performance_evidence_without_repetition()
    {
        QualityEvidenceProviders.Get("api-safe-request").Emits.Should().BeEquivalentTo([QualityEvidenceKind.Api, QualityEvidenceKind.Security, QualityEvidenceKind.Performance]);
        var items = QualityEvidenceQueries.FromApiReview(Report(), "aqr:1");
        items.Should().OnlyContain(i => i.ExecutionId == "aqr:1");
        items.Count(i => i.Kind == QualityEvidenceKind.Api).Should().Be(1, "only the executed GET; the unexecuted POST emits nothing");
        items.Single(i => i.Kind == QualityEvidenceKind.Performance).Should().Match<QualityEvidenceItem>(i => i.Value == "182" && i.Unit == "ms" && i.Nature.Contains("passive"));
        items.Should().Contain(i => i.Signal == "Strict-Transport-Security" && i.Value == "Missing");
        items.Single(i => i.Provider == "api-security-probes").Nature.Should().Contain("not full authorization correctness");
        items.Should().NotContain(i => (i.Value ?? "").Contains("Bearer") || (i.Value ?? "").Contains("Cookie"));
    }

    [Fact]
    public void Evidence_from_another_target_or_auth_context_is_never_current()
    {
        var item = QualityEvidenceQueries.FromApiReview(Report(), "aqr:1")[0];
        var now = DateTimeOffset.Parse("2026-10-23T10:00:00Z");
        var same = new QualityEvidenceContext("https://app.example.invalid/", "dev", "PublicHttp", "ctx-1");
        QualityEvidenceQueries.Currency(item, same, now, TimeSpan.FromDays(30)).Should().Be(QualityEvidenceCurrency.Current);
        QualityEvidenceQueries.Currency(item, same with { AuthContextDigest = "ctx-2" }, now, TimeSpan.FromDays(30)).Should().Be(QualityEvidenceCurrency.Historical);
        QualityEvidenceQueries.Currency(item, same with { TargetOrigin = "https://other.example.invalid" }, now, TimeSpan.FromDays(30)).Should().Be(QualityEvidenceCurrency.Incompatible);
        QualityEvidenceQueries.Currency(item, same, now.AddDays(60), TimeSpan.FromDays(30)).Should().Be(QualityEvidenceCurrency.Historical);
    }

    [Fact]
    public void Equivalent_security_observations_across_engines_and_reviews_are_one_logical_finding_per_origin()
    {
        var findings = QualityEvidenceQueries.Deduplicate(
        [
            ("FQR · HTTP Security", "HDR-MISSING-STRICT-TRANSPORT-SECURITY", "https://app.example.invalid/"),
            ("FQR · Passive Web Security", "10035", "https://app.example.invalid/login"),
            ("API Quality Review", "Strict-Transport-Security", "https://app.example.invalid"),
            ("API Quality Review", "Strict-Transport-Security", "https://api.example.invalid"),
            ("FQR · HTTP Security", "Some unrelated rule", "https://app.example.invalid/"),
        ]);
        findings.Should().HaveCount(2, "one per origin; unrelated rules are not security-control equivalents");
        findings.Single(f => f.Key.EndsWith("app.example.invalid")).Sources.Should().HaveCount(3);
        QualityEvidenceQueries.Control("HSTS", true, false, ["https://app.example.invalid"]).State.Should().Contain("Declared but not observed");
        QualityEvidenceQueries.Control("HSTS", null, null, []).State.Should().Be("Runtime not assessed");
    }

    [Fact]
    public void Metrics_thresholds_and_assessments_stay_apart_and_small_samples_have_no_percentiles()
    {
        var run = new PerformanceTestRun
        {
            TargetOrigin = "https://api.example.invalid",
            Metrics = new PerformanceMetrics { RequestCount = 1200, ErrorRatePercent = 0.5, RequestsPerSecond = 40, Latency = new PerformanceLatency { P95Ms = 420 } },
            ThresholdResults = [new PerformanceThresholdResult { Metric = PerformanceMetric.LatencyP95Ms, Expected = 500, Measured = 420, Severity = ThresholdSeverity.Required, Outcome = CheckOutcome.Pass }],
        };
        var assessments = QualityEvidenceQueries.FromLoadTest(run);
        var p95 = assessments.Single(a => a.Metric.Name == "p95 latency");
        (p95.Metric.Value, p95.Metric.Samples, p95.Metric.Measurement).Should().Be((420, 1200, "Load test (k6 scenario)"));
        p95.Threshold!.Warning.Should().Be(500);
        p95.State.Should().Be("Pass");
        assessments.Single(a => a.Metric.Name == "Throughput").State.Should().Be("No threshold — observation only");
        QualityEvidenceQueries.Percentile("API latency", [120, 180, 90], 95, "ms", "Passive", "GET /orders").Name.Should().Contain("no p95").And.Contain("3 sample(s)");
        QualityEvidenceQueries.Percentile("API latency", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 95, "ms", "Passive", "GET /orders").Value.Should().Be(10);
        QualityEvidenceQueries.Measurement("http-performance").Should().StartWith("Passive");
    }

    [Fact]
    public void Engines_plan_per_capability_and_optional_gaps_never_block()
    {
        var available = new HashSet<QualityCapabilityKind> { QualityCapabilityKind.PublicHttp, QualityCapabilityKind.CompanionLiveDom };
        QualityEvidenceQueries.Decide("HTTP Security", true, [QualityCapabilityKind.PublicHttp, QualityCapabilityKind.AuthenticatedHttp], available).Outcome.Should().Be("Runs");
        QualityEvidenceQueries.Decide("Accessibility", true, [QualityCapabilityKind.BrowserDom, QualityCapabilityKind.CompanionLiveDom], available).Outcome.Should().Be("Runs", "Companion DOM satisfies accessibility — Playwright is not a hard prerequisite");
        QualityEvidenceQueries.Decide("Lighthouse", false, [QualityCapabilityKind.CdpTarget], available).Outcome.Should().StartWith("Skipped (optional, non-blocking)");
        var gap = QualityEvidenceQueries.Decide("Lighthouse", true, [QualityCapabilityKind.CdpTarget], available);
        gap.Outcome.Should().StartWith("Coverage gap");
        gap.Reason.Should().Contain("not an application defect");
    }

    [Fact]
    public void Impact_and_traceability_read_existing_evidence_without_claiming_verification()
    {
        var items = QualityEvidenceQueries.FromApiReview(Report(), "aqr:1");
        QualityEvidenceQueries.ForSurfaces(items, ["/orders"]).Should().HaveCount(2);
        QualityEvidenceQueries.ForRequirement(items, "Responses shall be fast (latency under 500 ms)").Should().ContainSingle().Which.Should().Contain("182ms");
        QualityEvidenceQueries.ForRequirement(items, "HSTS shall be enabled").Should().Contain(x => x.Contains("Strict-Transport-Security: Missing"));
    }
}
