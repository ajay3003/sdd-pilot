using BirkNext.ApiReview;
using BirkNext.QualityEvidence;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>Shared quality evidence on the review pages: evidence by kind from the recorded reports, one logical finding per security
/// control across FQR and AQR, and current vs historical against the active context.</summary>
public sealed class QualityEvidenceUiTests : BunitContext
{
    private static FrontendQualityReviewReport Frontend() => new()
    {
        TargetUrl = "https://app.example.invalid/", GeneratedAt = new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc),
        TargetEnvironment = new FrontendReviewTargetIdentity("dev", "Development", "Development", "https://app.example.invalid/", new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc)),
        Findings =
        [
            new FrontendQualityFinding { Id = "f1", Title = "Missing HSTS header", Category = FrontendQualityCategory.Security, EngineId = FrontendQualityEngineId.StaticSecurity, SourceRuleId = "HDR-MISSING-STRICT-TRANSPORT-SECURITY" },
            new FrontendQualityFinding { Id = "f2", Title = "Large bundle", Category = FrontendQualityCategory.Performance, EngineId = FrontendQualityEngineId.PassivePerformance, SourceRuleId = "PERF-BUNDLE" },
        ],
    };

    private static ApiReviewReport Api() => new()
    {
        Environment = new ApiReviewEnvironmentSnapshot { EnvironmentId = "dev", TargetUrl = "https://app.example.invalid/" }, GeneratedAt = DateTimeOffset.Parse("2026-10-22T10:00:00Z"),
        Targets = [new ApiReviewTargetResult { Target = new ApiReviewTarget { Scheme = "https", Host = "app.example.invalid", BasePath = "/api" }, AccessMode = ApiReviewAccessMode.PublicHttp,
            Operations = [new ApiReviewOperationResult { Method = "GET", Path = "/orders", Executed = true, StatusCode = 200, ElapsedMs = 120 }],
            SecurityHeaders = [new("Strict-Transport-Security", true, false, null, SecurityHeaderOutcome.Missing, SecurityHeaderScope.Transport, "")] }],
    };

    [Fact]
    public void Summary_shows_kinds_providers_and_one_logical_finding_across_reviews()
    {
        var cut = Render<QualityEvidenceSummary>(p => p.Add(x => x.FrontendReport, Frontend()).Add(x => x.ApiReport, Api())
            .Add(x => x.Current, new QualityEvidenceContext("https://app.example.invalid/", "dev", "PublicHttp", null)));
        cut.FindAll("[data-testid=quality-evidence-kind]").Select(k => k.TextContent).Should().Contain(["API1", "Security2", "Performance2"]);
        cut.Find("[data-testid=quality-evidence-providers]").TextContent.Should().Contain("2 recorded execution(s)");
        var logical = cut.FindAll("[data-testid=quality-evidence-logical-finding]");
        logical.Should().ContainSingle("the FQR HSTS finding and the AQR missing header on the same origin are one issue");
        logical[0].TextContent.Should().Contain("HSTS").And.Contain("Frontend Quality Review").And.Contain("API Quality Review");
        cut.Find("[data-testid=quality-evidence-currency]").TextContent.Should().Contain("historical");
        cut.Markup.Should().Contain("not authorization correctness").And.Contain("passive timing is not a load test");
    }

    [Fact]
    public void Frontend_findings_map_to_evidence_kinds_and_providers()
    {
        var items = QualityEvidencePresentation.FromFrontendReview(Frontend(), "dev", "fqr:1");
        items.Select(i => (i.Kind, i.Provider)).Should().Equal((QualityEvidenceKind.Security, "http-security"), (QualityEvidenceKind.Performance, "http-performance"));
        items.Should().OnlyContain(i => i.Context.EnvironmentId == "dev" && i.Nature.StartsWith("Finding"));
    }
}
