using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Services;

/// <summary>Shared REST correlation (declared ↔ observed): exact and parameterized paths, version and method differences, reverse-proxy
/// prefixes, trailing slashes, ambiguity and unmatched traffic — and the API Quality Review panel that shows observed ≠ verified.</summary>
public sealed class ApiOperationCorrelationTests : BunitContext
{
    private static readonly DateTimeOffset At = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static ObservedApiOperation Seen(string method, string path, int status = 200) => new(method, path, status, true, 3, At);

    private static readonly DeclaredApiOperation[] Declared =
    [
        new("GET", "/api/versions"),
        new("GET", "/api/autorisasjon/v1/brukere/{brukerId}/tilganger"),
        new("GET", "/api/person/search"),
        new("GET", "/api/person/{personId}"),
        new("POST", "/api/person"),
    ];

    [Fact]
    public void ExactAndParameterizedPaths_AreConfirmed_TheMostSpecificTemplateWins()
    {
        var result = ApiOperationCorrelation.Correlate(Declared,
        [
            Seen("GET", "/api/versions"),
            Seen("GET", "/api/autorisasjon/v1/brukere/551ec8c6-83c7-4ac7-89bb-b92aed55a1ff/tilganger"),
            Seen("GET", "/api/person/search"),
        ]);
        result.Rows.Should().OnlyContain(r => r.Confidence == OperationCorrelationConfidence.Confirmed);
        result.Rows[1].Declared.Single().PathTemplate.Should().Be("/api/autorisasjon/v1/brukere/{brukerId}/tilganger");
        result.Rows[2].Declared.Single().PathTemplate.Should().Be("/api/person/search", "literal segments beat a placeholder");
        result.Rows.Should().OnlyContain(r => r.Level == ApiEvidenceLevel.Correlated);
        result.UnobservedDeclared.Select(d => $"{d.Method} {d.PathTemplate}").Should().BeEquivalentTo(["GET /api/person/{personId}", "POST /api/person"]);
    }

    [Fact]
    public void VersionMethodAndUnknownPaths_AreUnmatched_WithTheReason()
    {
        var result = ApiOperationCorrelation.Correlate(Declared,
        [
            Seen("GET", "/api/autorisasjon/v2/brukere/1/tilganger"),
            Seen("DELETE", "/api/person/42"),
            Seen("GET", "/api/unknown"),
        ]);
        result.Rows.Should().OnlyContain(r => r.Confidence == OperationCorrelationConfidence.Unmatched && r.Level == ApiEvidenceLevel.Observed);
        result.Rows[1].Reason.Should().Contain("not for DELETE");
        result.Unmatched.Should().Be(3);
    }

    [Fact]
    public void TrailingSlashAndQuery_AreIgnored()
    {
        ApiOperationCorrelation.Correlate(Declared, [Seen("get", "/api/versions/?x=1")]).Rows.Single().Confidence.Should().Be(OperationCorrelationConfidence.Confirmed);
    }

    [Fact]
    public void ReverseProxyPrefix_IsOnlyACandidate_UnlessTheTargetBasePathExplainsIt()
    {
        DeclaredApiOperation[] declared = [new("GET", "/brukere/{id}")];
        var candidate = ApiOperationCorrelation.Correlate(declared, [Seen("GET", "/gateway/brukere/7")]);
        candidate.Rows.Single().Confidence.Should().Be(OperationCorrelationConfidence.Candidate);
        candidate.Rows.Single().Reason.Should().Contain("/gateway").And.Contain("review");
        candidate.Correlated.Should().Be(0, "a candidate is never counted as correlated");

        ApiOperationCorrelation.Correlate(declared, [Seen("GET", "/gateway/brukere/7")], basePath: "/gateway").Rows.Single().Confidence
            .Should().Be(OperationCorrelationConfidence.Confirmed, "the target's base path is the prefix");
    }

    [Fact]
    public void EquallySpecificTemplates_AreAmbiguous_NeverConfirmed()
    {
        DeclaredApiOperation[] declared = [new("GET", "/api/items/{id}"), new("GET", "/api/items/{slug}")];
        var row = ApiOperationCorrelation.Correlate(declared, [Seen("GET", "/api/items/abc")]).Rows.Single();
        row.Confidence.Should().Be(OperationCorrelationConfidence.Ambiguous);
        row.Declared.Should().HaveCount(2);
        row.Reason.Should().Contain("confirm which one explicitly");
    }

    [Fact]
    public void AqrPanel_ShowsObservedRequestsAsNotVerified_WithoutAContract()
    {
        var target = new ApiReviewTarget
        {
            TargetId = "rest-1", ApiType = ApiReviewTargetType.Rest, Host = "m2lbdev.example.test", BasePath = "/api", ServiceName = "Autorisasjon",
            Operations =
            [
                new ApiReviewOperation { Method = "GET", Path = "/api/versions", LastStatus = 200, AuthObserved = true, ObservedCount = 4, LastObservedAt = At, Source = ApiReviewTargetSource.DiscoveredTraffic },
                new ApiReviewOperation { Method = "GET", Path = "/api/autorisasjon/v1/brukere/{id}/tilganger", LastStatus = 200, AuthObserved = true, ObservedCount = 2, LastObservedAt = At, Source = ApiReviewTargetSource.DiscoveredTraffic },
                new ApiReviewOperation { Method = "GET", Path = "/health", Source = ApiReviewTargetSource.Configured },
            ],
        };
        var cut = Render<ApiRuntimeObservationsPanel>(p => p.Add(x => x.Targets, new[] { target }).Add(x => x.EnvironmentId, "dev"));

        cut.FindAll("[data-testid=aro-row]").Should().HaveCount(2, "configured operations are not observations");
        cut.Find("[data-testid=aro-declared]").TextContent.Should().Be("No contract bound");
        cut.Find("[data-testid=aro-observed]").TextContent.Should().Be("2");
        cut.Find("[data-testid=aro-verification]").TextContent.Should().StartWith("Not verified");
        cut.FindAll("[data-testid=aro-evidence]").Should().OnlyContain(e => e.TextContent == "Observed · not verified");
        cut.Find("[data-testid=aro-row]").TextContent.Should().Contain("HTTP 200").And.Contain("Bearer observed");
        cut.Find("[data-testid=aro-view-discovery]").GetAttribute("href").Should().Contain("tab=endpoint").And.Contain("profile=dev");
        cut.FindAll("[data-testid=aro-evidence], [data-testid=aro-verification]").Should().OnlyContain(e => !e.TextContent.StartsWith("Verified"), "nothing is shown as verified from an observation");
    }
}
