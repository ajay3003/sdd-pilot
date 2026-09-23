using BirkNext.ApiReview;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class ApiReviewEvidencePresentationTests
{
    [Fact]
    public void PreviousIntrospectionRejectionPromisesOnlyAnotherAttempt()
    {
        var target = new ApiReviewTarget { TargetId = "gql", ApiType = ApiReviewTargetType.GraphQl };
        var report = new ApiReviewReport { Targets = [new() { Target = target, Contract = new() { IntrospectionEnabled = false, Available = false, Status = ApiReviewCheckResult.NotApplicable } }] };
        var contracts = ApiReviewPresentation.Contracts([target], ["gql"], null, report);
        contracts.Rows.Single(r => r.Label == "GraphQL").Detail.Should().Be("Runtime introspection was unavailable previously; the review will attempt schema retrieval again.");
        ApiReviewEvidencePresentation.CheckSummary([new() { Area = ApiReviewFindingType.Drift, Result = ApiReviewCheckResult.Pass }, new() { Area = ApiReviewFindingType.Drift, Result = ApiReviewCheckResult.Pass }], new())
            .Should().Be("Drift checks · 2 completed · 0 changes detected");
    }

    [Theory]
    [InlineData("sec-tls", "HTTPS observed")]
    [InlineData("sec-hsts", "Header observed")]
    [InlineData("sec-cache-control", "Header observed")]
    [InlineData("sec-server-disclosure", "No issue detected")]
    [InlineData("cors-preflight", "Response observed")]
    [InlineData("cors-policy", "Observed")]
    [InlineData("gql-introspection", "Observed")]
    [InlineData("gql-error-leak", "No indicators observed")]
    [InlineData("errors-unknown-route", "Pass")]
    [InlineData("gql-error-shape", "Pass")]
    [InlineData("rest-latency", "Pass")]
    [InlineData("gql-latency", "Pass")]
    public void BoundedCheckLabelsPreserveEngineResult(string id, string label)
    {
        var check = new ApiReviewCheck { CheckId = id, Result = ApiReviewCheckResult.Pass };
        ApiReviewEvidencePresentation.CheckLabel(check, new()).Should().Be(label);
        check.Result.Should().Be(ApiReviewCheckResult.Pass);
        ApiReviewEvidencePresentation.CheckLabel(check with { Result = ApiReviewCheckResult.NotTested }, new()).Should().Be("Not tested");
        ApiReviewEvidencePresentation.CheckLabel(check with { Result = ApiReviewCheckResult.Fail }, new()).Should().Be("Fail");
    }

    [Fact]
    public void PayloadRequiresMeasurementAndThreshold_CompressionWarningIsPreserved()
    {
        var check = new ApiReviewCheck { CheckId = "rest-payload", Result = ApiReviewCheckResult.Pass, Detail = "10 bytes." };
        ApiReviewEvidencePresentation.CheckLabel(check, new() { LargePayloadBytes = 0 }).Should().Be("Observed");
        ApiReviewEvidencePresentation.CheckDetail(check, new() { LargePayloadBytes = 15 }).Should().Contain("warning > 15 bytes");
        var missing = ApiReviewEvidencePresentation.OperationChecks(new() { Checks = [check] }).Single();
        missing.Result.Should().Be(ApiReviewCheckResult.NotTested);
        missing.Detail.Should().Be("Payload size was not recorded.");
        var compression = new ApiReviewCheck { CheckId = "rest-compression", Result = ApiReviewCheckResult.Warning, Detail = "No Content-Encoding on primary response" };
        ApiReviewEvidencePresentation.CheckLabel(compression, new()).Should().Be("Warning");
        ApiReviewEvidencePresentation.CheckDetail(compression, new()).Should().Be(compression.Detail);
    }

    [Fact]
    public void GraphQlTimingShowsCapturedPolicyAndDriftCountsUseActualChecks()
    {
        ApiReviewEvidencePresentation.CheckDetail(new() { CheckId = "gql-latency", Detail = "141 ms." }, new() { SlowWarningMs = 500, SlowPoorMs = 1000 })
            .Should().Contain("141 ms").And.Contain("warning > 500 ms").And.Contain("poor > 1000 ms");
        var report = new ApiReviewReport { Targets = [new() { Checks = [new() { Area = ApiReviewFindingType.Drift, Result = ApiReviewCheckResult.Pass }, new() { Area = ApiReviewFindingType.Contract, Result = ApiReviewCheckResult.NotTested }] }] };
        ApiReviewEvidencePresentation.ContractCheckCoverage(report).Should().Be("0 contract checks executed · 1 drift/history checks executed");
    }
}
