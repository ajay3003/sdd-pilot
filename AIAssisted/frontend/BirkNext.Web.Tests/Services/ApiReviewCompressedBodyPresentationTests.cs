using BirkNext.ApiReview;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Transfer size vs decoded payload in the AQR Performance table and export; older reports render unchanged.</summary>
public sealed class ApiReviewCompressedBodyPresentationTests
{
    private static ApiReviewOperationResult Op(long? contentLength, ApiReviewResponseBody? body) =>
        new() { Display = "GET /api/children", Method = "GET", Path = "/api/children", Executed = true, StatusCode = 200, ContentType = "application/json", ContentLength = contentLength, Body = body };

    [Fact]
    public void CompressedResponse_ShowsDecodedPayloadWithTransferAsSecondary()
    {
        var (primary, secondary) = ApiReviewEvidencePresentation.PayloadSize(Op(49152,
            new() { ContentEncoding = "gzip", TransferBytes = 8192, DecodedBytes = 49152, Decoding = ApiResponseBodyDecoding.Decoded, Inspected = true }));
        primary.Should().Be("48 KB decoded");
        secondary.Should().Be("Transfer: 8 KB · gzip");
    }

    [Fact]
    public void UncompressedResponse_ShowsOneSize()
    {
        ApiReviewEvidencePresentation.PayloadSize(Op(500, new() { TransferBytes = 500, DecodedBytes = 500, Decoding = ApiResponseBodyDecoding.NotEncoded, Inspected = true }))
            .Should().Be(("500 bytes", (string?)null));
    }

    [Theory]
    [InlineData(ApiResponseBodyDecoding.DecodeFailed, "The body could not be decoded as gzip (InvalidDataException).")]
    [InlineData(ApiResponseBodyDecoding.UnsupportedEncoding, "Unsupported Content-Encoding: zstd.")]
    public void UndecodableResponse_IsNotTestedWithItsReason_NeverZero(ApiResponseBodyDecoding decoding, string reason)
    {
        var (primary, secondary) = ApiReviewEvidencePresentation.PayloadSize(Op(null, new() { ContentEncoding = "gzip", TransferBytes = 21, Decoding = decoding, Reason = reason }));
        primary.Should().Be("Not tested");
        secondary.Should().Be(reason);
    }

    [Fact]
    public void LowerBoundAndNoBodyAreStatedAsSuch()
    {
        ApiReviewEvidencePresentation.PayloadSize(Op(null, new() { ContentEncoding = "gzip", DecodedBytes = 70 * 1024 * 1024, DecodedBytesIsLowerBound = true, Decoding = ApiResponseBodyDecoding.Decoded }))
            .Primary.Should().Be("At least 70 MB decoded");
        ApiReviewEvidencePresentation.PayloadSize(Op(0, new() { TransferBytes = 0, DecodedBytes = 0, Decoding = ApiResponseBodyDecoding.NoBody, Inspected = true }))
            .Primary.Should().Be("No body");
    }

    [Fact]
    public void HistoricalReport_WithoutBodyEvidence_RendersItsLegacyValueUnchanged()
    {
        ApiReviewEvidencePresentation.PayloadSize(Op(8192, null)).Should().Be(("8192", (string?)null), "no transfer/decoded value is invented for an old report");
        var legacyCheck = new ApiReviewCheck { CheckId = "rest-payload", Result = ApiReviewCheckResult.Pass, Detail = "8,192 bytes." };
        ApiReviewEvidencePresentation.OperationChecks(Op(8192, null) with { Checks = [legacyCheck] }).Single().Should().Be(legacyCheck);
    }

    [Fact]
    public void NewReportKeepsItsOwnNotTestedReason()
    {
        var check = new ApiReviewCheck { CheckId = "rest-payload", Result = ApiReviewCheckResult.NotTested, Detail = "Not tested: Unsupported Content-Encoding: zstd." };
        var op = Op(null, new() { ContentEncoding = "zstd", Decoding = ApiResponseBodyDecoding.UnsupportedEncoding, Reason = "Unsupported Content-Encoding: zstd." }) with { Checks = [check] };
        ApiReviewEvidencePresentation.OperationChecks(op).Single().Detail.Should().Be(check.Detail);
    }

    [Fact]
    public void Export_ListsBodyEvidenceForEncodedResponses_AndLeavesOldReportsAlone()
    {
        var target = new ApiReviewTarget { TargetId = "rest-1", ApiType = ApiReviewTargetType.Rest, ServiceName = "Children API", Scheme = "https", Host = "api-dev.example.test", Port = 443, BasePath = "/api/children" };
        var encoded = Op(49152, new() { ContentEncoding = "gzip", TransferBytes = 8192, DecodedBytes = 49152, Decoding = ApiResponseBodyDecoding.Decoded, Inspected = true });
        var html = new ReportExportService().ExportApiReview(new ApiReviewReport { Targets = [new() { Target = target, Operations = [encoded] }] }, "Test");
        html.Should().Contain("<th>Payload size</th>").And.Contain("48 KB decoded (Transfer: 8 KB · gzip)");
        html.Should().Contain("Response body evidence").And.Contain("8,192 bytes").And.Contain("49,152 bytes").And.Contain("Decoded payload");

        var legacy = new ReportExportService().ExportApiReview(new ApiReviewReport { Targets = [new() { Target = target, Operations = [Op(8192, null)] }] }, "Test");
        legacy.Should().NotContain("Response body evidence").And.Contain("<td>8192</td>");
    }
}
