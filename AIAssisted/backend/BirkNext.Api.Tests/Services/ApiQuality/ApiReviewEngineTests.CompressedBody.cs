using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Tests.Services.ApiQuality;

/// <summary>
/// Public-path compressed REST bodies: transfer size and decoded payload size are separate, the REST payload threshold evaluates the
/// decoded size, JSON/ProblemDetails/internal-detail analysis sees the decoded body, and anything that cannot be decoded is Not tested —
/// never a 0-byte Pass. Content-Length describes the transferred (possibly compressed) representation.
/// </summary>
public sealed partial class ApiReviewEngineTests
{
    private static byte[] Encode(string coding, byte[] data)
    {
        using var output = new MemoryStream();
        using (Stream encoder = coding == "br" ? new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true) : new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            encoder.Write(data);
        return output.ToArray();
    }

    /// <summary>A response whose body is really encoded with <paramref name="coding"/> (codings applied in listed order, as HTTP lists them).</summary>
    private static HttpResponseMessage Encoded(HttpStatusCode status, string body, string coding, string contentType = "application/json", bool chunked = false)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        foreach (var c in coding.Split(',', StringSplitOptions.TrimEntries)) bytes = Encode(c, bytes);
        return Raw(status, bytes, coding, contentType, chunked);
    }

    private static HttpResponseMessage Raw(HttpStatusCode status, byte[] bytes, string? coding, string contentType = "application/json", bool chunked = false)
    {
        HttpContent content = chunked ? new StreamContent(new NonSeekable(bytes)) : new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (coding is not null) content.Headers.TryAddWithoutValidation("Content-Encoding", coding);
        var response = new HttpResponseMessage(status) { Content = content };
        response.Headers.TryAddWithoutValidation("Cache-Control", "no-store");
        response.Headers.TryAddWithoutValidation("Strict-Transport-Security", "max-age=31536000");
        response.Headers.TryAddWithoutValidation("X-Content-Type-Options", "nosniff");
        return response;
    }

    /// <summary>No length is computable → HttpClient sees a body without Content-Length (like a chunked response).</summary>
    private sealed class NonSeekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    /// <summary>A JSON collection of roughly <paramref name="kilobytes"/> KB with varied values (so it does not compress to nothing).</summary>
    private static string JsonOfSize(int kilobytes)
    {
        var sb = new StringBuilder("{\"items\":[");
        var i = 0;
        while (sb.Length < kilobytes * 1024)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"{{\"id\":{i},\"name\":\"child-{i * 7919 % 100003}\",\"code\":\"{Guid.NewGuid():N}\"}}");
            i++;
        }
        sb.Append($"],\"totalCount\":{i}}}");
        return sb.ToString();
    }

    private static ApiReviewRunRequest PublicRequest(long? restPayloadBytes = 500 * 1024, long compressionMinimum = 1024) =>
        Request(AuthenticatedTestingMethod.ManagedEdgeCdp, false, Rest()) with
        {
            Policy = new ApiReviewPolicy { ErrorHandlingProbes = true, RestPayloadWarningBytes = restPayloadBytes, CompressionMinimumBytes = compressionMinimum },
        };

    private static Fixture Serving(Func<HttpResponseMessage> main, Func<HttpResponseMessage>? unknownRoute = null) => new()
    {
        Respond = (req, _) => req.RequestUri!.AbsolutePath.Contains("birknext-unknown-route")
            ? unknownRoute?.Invoke() ?? Json(HttpStatusCode.NotFound, "{\"type\":\"about:blank\",\"title\":\"Not Found\",\"status\":404}", "application/problem+json")
            : req.Method == HttpMethod.Options ? Json(HttpStatusCode.NoContent, "") : main(),
    };

    private static ApiReviewCheck OpCheck(ApiReviewReport report, string id) => Assert.Single(report.Targets.Single().Operations.Single(o => o.Executed).Checks, c => c.CheckId == id);
    private static ApiReviewCheck TargetCheck(ApiReviewReport report, string id) => Assert.Single(report.Targets.Single().Checks, c => c.CheckId == id);

    // ── Reader ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    public async Task Reader_Compressed_KeepsTransferAndDecodedSizesApart(string coding)
    {
        var json = JsonOfSize(100);
        using var response = Encoded(HttpStatusCode.OK, json, coding);
        var transferDeclared = response.Content.Headers.ContentLength;

        var read = await ResponseBodyReader.ReadAsync(response, CancellationToken.None);

        Assert.Equal(ApiResponseBodyDecoding.Decoded, read.Evidence.Decoding);
        Assert.Equal(coding, read.Evidence.ContentEncoding);
        Assert.Equal(transferDeclared, read.Evidence.TransferBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(json), read.Evidence.DecodedBytes);
        Assert.True(read.Evidence.TransferBytes < read.Evidence.DecodedBytes, "Content-Length is the compressed transfer size, not the payload");
        Assert.True(read.Evidence.Inspected);
        Assert.Equal(json, read.Text);
    }

    [Fact]
    public async Task Reader_Uncompressed_TransferEqualsDecoded()
    {
        using var response = Raw(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{\"ok\":true}"), null);
        var read = await ResponseBodyReader.ReadAsync(response, CancellationToken.None);
        Assert.Equal(ApiResponseBodyDecoding.NotEncoded, read.Evidence.Decoding);
        Assert.Null(read.Evidence.ContentEncoding);
        Assert.Equal(11, read.Evidence.TransferBytes);
        Assert.Equal(11, read.Evidence.DecodedBytes);
    }

    [Fact]
    public async Task Reader_ChunkedGzip_WithoutContentLength_IsMeasuredFromCapturedBytes()
    {
        var json = JsonOfSize(40);
        var encoded = Encode("gzip", Encoding.UTF8.GetBytes(json));
        using var response = Raw(HttpStatusCode.OK, encoded, "gzip", chunked: true);
        Assert.Null(response.Content.Headers.ContentLength);

        var read = await ResponseBodyReader.ReadAsync(response, CancellationToken.None);

        Assert.Equal(encoded.Length, read.Evidence.TransferBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(json), read.Evidence.DecodedBytes);
        Assert.Equal(json, read.Text);
    }

    [Fact]
    public async Task Reader_InvalidGzip_IsDecodeFailed_NeverZeroBytes()
    {
        using var response = Raw(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{\"not\":\"gzip at all\"}"), "gzip");
        var read = await ResponseBodyReader.ReadAsync(response, CancellationToken.None);
        Assert.Equal(ApiResponseBodyDecoding.DecodeFailed, read.Evidence.Decoding);
        Assert.Null(read.Evidence.DecodedBytes);
        Assert.Null(read.Text);
        Assert.False(read.Evidence.Inspected);
        Assert.Equal(21, read.Evidence.TransferBytes);
        Assert.Contains("could not be decoded as gzip", read.Evidence.Reason);
    }

    [Fact]
    public async Task Reader_UnsupportedEncoding_IsNotDecoded_TransferStillCounted()
    {
        using var response = Raw(HttpStatusCode.OK, [1, 2, 3, 4, 5], "zstd");
        var read = await ResponseBodyReader.ReadAsync(response, CancellationToken.None);
        Assert.Equal(ApiResponseBodyDecoding.UnsupportedEncoding, read.Evidence.Decoding);
        Assert.Equal("Unsupported Content-Encoding: zstd.", read.Evidence.Reason);
        Assert.Equal(5, read.Evidence.TransferBytes);
        Assert.Null(read.Evidence.DecodedBytes);
        Assert.Null(read.Text);
    }

    [Fact]
    public async Task Reader_SupportedChain_IsDecodedInReverseOrder_UnsupportedChainIsNot()
    {
        using var chained = Encoded(HttpStatusCode.OK, "{\"chain\":true}", "gzip, br");
        var read = await ResponseBodyReader.ReadAsync(chained, CancellationToken.None);
        Assert.Equal(ApiResponseBodyDecoding.Decoded, read.Evidence.Decoding);
        Assert.Equal("{\"chain\":true}", read.Text);

        using var mixed = Raw(HttpStatusCode.OK, Encode("gzip", "{}"u8.ToArray()), "gzip, zstd");
        var unsupported = await ResponseBodyReader.ReadAsync(mixed, CancellationToken.None);
        Assert.Equal(ApiResponseBodyDecoding.UnsupportedEncoding, unsupported.Evidence.Decoding);
        Assert.Equal("Unsupported Content-Encoding chain: gzip, zstd.", unsupported.Evidence.Reason);
        Assert.Null(unsupported.Text);
    }

    [Fact]
    public async Task Reader_BodyOverInspectionLimit_IsSizedExactly_ButNotAnalysed()
    {
        var json = JsonOfSize(10);
        using var response = Encoded(HttpStatusCode.OK, json, "gzip");
        var read = await ResponseBodyReader.ReadAsync(response, CancellationToken.None, maxInspected: 1024);
        Assert.Equal(Encoding.UTF8.GetByteCount(json), read.Evidence.DecodedBytes);
        Assert.False(read.Evidence.DecodedBytesIsLowerBound);
        Assert.False(read.Evidence.Inspected);
        Assert.Null(read.Text);
        Assert.Contains("body inspection limit", read.Evidence.Reason);
    }

    [Fact]
    public async Task Reader_DecompressionBomb_StopsAtTheCountingLimit()
    {
        var zeros = new byte[32 * 1024 * 1024];
        using var response = Raw(HttpStatusCode.OK, Encode("gzip", zeros), "gzip");
        Assert.True(response.Content.Headers.ContentLength < 1024 * 1024, "32 MB of zeros compresses to well under 1 MB (a >32x expansion)");

        var read = await ResponseBodyReader.ReadAsync(response, CancellationToken.None, maxInspected: 64 * 1024, maxCounted: 1024 * 1024);

        Assert.True(read.Evidence.DecodedBytesIsLowerBound);
        Assert.InRange(read.Evidence.DecodedBytes!.Value, 1024 * 1024 + 1, 1024 * 1024 + 16384);
        Assert.False(read.Evidence.Inspected);
        Assert.Null(read.Text);
    }

    [Fact]
    public async Task Reader_NoBody_AndCharsetAreHandled()
    {
        using var empty = new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new ByteArrayContent([]) };
        Assert.Equal(ApiResponseBodyDecoding.NoBody, (await ResponseBodyReader.ReadAsync(empty, CancellationToken.None)).Evidence.Decoding);

        var latin1 = Encoding.Latin1.GetBytes("{\"navn\":\"Ærø\"}");
        using var response = Raw(HttpStatusCode.OK, Encode("gzip", latin1), "gzip", "application/json");
        response.Content.Headers.ContentType!.CharSet = "iso-8859-1";
        Assert.Equal("{\"navn\":\"Ærø\"}", (await ResponseBodyReader.ReadAsync(response, CancellationToken.None)).Text);
    }

    // ── Engine ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RestPayload_UsesDecodedSize_NotTheCompressedTransferSize()
    {
        var json = JsonOfSize(600);
        var report = await Engine(Serving(() => Encoded(HttpStatusCode.OK, json, "gzip"))).RunAsync(PublicRequest());

        var op = report.Targets.Single().Operations.Single(o => o.Executed);
        Assert.Equal(Encoding.UTF8.GetByteCount(json), op.ContentLength);
        Assert.Equal(ApiResponseBodyDecoding.Decoded, op.Body!.Decoding);
        Assert.True(op.Body.TransferBytes < 500 * 1024, "the transfer size alone would have passed");
        var payload = OpCheck(report, "rest-payload");
        Assert.Equal(ApiReviewCheckResult.Warning, payload.Result);
        Assert.Contains("decoded (transfer", payload.Detail);
        Assert.Contains(report.Findings, f => f.Id.StartsWith("rest-large-payload") || f.Title.StartsWith("Large response payload"));
        Assert.Equal(ApiReviewCheckResult.Pass, TargetCheck(report, "rest-compression").Result);
    }

    [Fact]
    public async Task RestPayload_BelowThresholdDecoded_Passes()
    {
        var report = await Engine(Serving(() => Encoded(HttpStatusCode.OK, JsonOfSize(450), "br"))).RunAsync(PublicRequest());
        Assert.Equal(ApiReviewCheckResult.Pass, OpCheck(report, "rest-payload").Result);
        Assert.DoesNotContain(report.Findings, f => f.Title.StartsWith("Large response payload"));
    }

    [Fact]
    public async Task CompressedJson_IsParsedAfterDecoding()
    {
        var report = await Engine(Serving(() => Encoded(HttpStatusCode.OK, "{\"items\":[{\"id\":1}],\"totalCount\":1}", "gzip"))).RunAsync(PublicRequest());
        var jsonCheck = OpCheck(report, "rest-json-valid");
        Assert.Equal(ApiReviewCheckResult.Pass, jsonCheck.Result);
        Assert.Contains("after removing Content-Encoding gzip", jsonCheck.Detail);
        Assert.DoesNotContain(report.Findings, f => f.Title.Contains("does not parse"));
    }

    [Fact]
    public async Task CompressedProblemDetails_IsRecognised()
    {
        var report = await Engine(Serving(() => Json(HttpStatusCode.OK, "{\"ok\":true}"),
            () => Encoded(HttpStatusCode.NotFound, "{\"type\":\"about:blank\",\"title\":\"Not Found\",\"status\":404,\"traceId\":\"x\"}", "gzip", "application/problem+json"))).RunAsync(PublicRequest());
        Assert.Equal(ApiReviewCheckResult.Pass, TargetCheck(report, "errors-format").Result);
        Assert.Equal(ApiReviewCheckResult.Pass, TargetCheck(report, "errors-leak").Result);
        Assert.DoesNotContain(report.Findings, f => f.Title.Contains("do not use ProblemDetails"));
    }

    [Fact]
    public async Task CompressedErrorBody_IsScannedForInternalDetails()
    {
        const string leak = "{\"title\":\"Error\",\"status\":500,\"detail\":\"System.NullReferenceException at Api.Children.Get() in C:\\\\src\\\\Children.cs:line 42\"}";
        var report = await Engine(Serving(() => Json(HttpStatusCode.OK, "{\"ok\":true}"), () => Encoded(HttpStatusCode.InternalServerError, leak, "br", "application/problem+json"))).RunAsync(PublicRequest());
        var check = TargetCheck(report, "errors-leak");
        Assert.Equal(ApiReviewCheckResult.Fail, check.Result);
        Assert.Contains("exception-type", check.Detail);
    }

    [Fact]
    public async Task UndecodableErrorBody_IsNotTested_NeverClean()
    {
        var report = await Engine(Serving(() => Json(HttpStatusCode.OK, "{\"ok\":true}"), () => Raw(HttpStatusCode.NotFound, "garbage"u8.ToArray(), "gzip", "application/problem+json"))).RunAsync(PublicRequest());
        Assert.Equal(ApiReviewCheckResult.NotTested, TargetCheck(report, "errors-leak").Result);
        Assert.Equal(ApiReviewCheckResult.NotTested, TargetCheck(report, "errors-format").Result);
    }

    [Fact]
    public async Task InvalidGzip_PayloadAndJsonAreNotTested_NoZeroBytePass_NoInvalidJsonFinding()
    {
        var report = await Engine(Serving(() => Raw(HttpStatusCode.OK, "{\"plain\":true}"u8.ToArray(), "gzip"))).RunAsync(PublicRequest());
        var op = report.Targets.Single().Operations.Single(o => o.Executed);
        Assert.Null(op.ContentLength);
        Assert.Equal(ApiResponseBodyDecoding.DecodeFailed, op.Body!.Decoding);
        Assert.Equal(ApiReviewCheckResult.NotTested, OpCheck(report, "rest-payload").Result);
        Assert.Contains("could not be decoded", OpCheck(report, "rest-payload").Detail);
        Assert.Equal(ApiReviewCheckResult.NotTested, OpCheck(report, "rest-json-valid").Result);
        Assert.DoesNotContain(report.Findings, f => f.Title.Contains("does not parse"));
        Assert.Equal(ApiReviewCheckResult.Pass, TargetCheck(report, "rest-compression").Result);
    }

    [Fact]
    public async Task UnsupportedEncoding_PayloadIsNotTested_NoFalseWarning()
    {
        var report = await Engine(Serving(() => Raw(HttpStatusCode.OK, new byte[800 * 1024], "zstd"))).RunAsync(PublicRequest());
        var payload = OpCheck(report, "rest-payload");
        Assert.Equal(ApiReviewCheckResult.NotTested, payload.Result);
        Assert.Contains("Unsupported Content-Encoding: zstd", payload.Detail);
        Assert.DoesNotContain(report.Findings, f => f.Title.StartsWith("Large response payload"));
    }

    [Theory]
    [InlineData(500, null, ApiReviewCheckResult.NotApplicable)]
    [InlineData(10 * 1024, null, ApiReviewCheckResult.Warning)]
    [InlineData(500, "gzip", ApiReviewCheckResult.Pass)]
    public async Task Compression_UsesDecodedSize_AndStaysSeparateFromPayload(int bytes, string? coding, ApiReviewCheckResult expected)
    {
        var body = "{\"v\":\"" + new string('a', bytes - 8) + "\"}";
        var report = await Engine(Serving(() => coding is null ? Raw(HttpStatusCode.OK, Encoding.UTF8.GetBytes(body), null) : Encoded(HttpStatusCode.OK, body, coding))).RunAsync(PublicRequest());
        Assert.Equal(expected, TargetCheck(report, "rest-compression").Result);
        Assert.Equal(ApiReviewCheckResult.Pass, OpCheck(report, "rest-payload").Result);
    }

    [Fact]
    public async Task NoBody_PayloadIsNotApplicable_NotAZeroBytePass()
    {
        var report = await Engine(Serving(() => new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new ByteArrayContent([]) })).RunAsync(PublicRequest());
        Assert.Equal(ApiReviewCheckResult.NotApplicable, OpCheck(report, "rest-payload").Result);
    }

    [Fact]
    public async Task CompressedBinary_IsNotParsedAsJson()
    {
        var report = await Engine(Serving(() => Raw(HttpStatusCode.OK, Encode("gzip", [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]), "gzip", "image/png"))).RunAsync(PublicRequest());
        Assert.Equal(ApiReviewCheckResult.NotApplicable, OpCheck(report, "rest-json-valid").Result);
    }

    [Fact]
    public async Task GatewayResponseEncodedAnyway_IsNotAnalysed()
    {
        var gateway = new EncodingGateway();
        var target = Rest(auth: true);
        var report = await Engine(new Fixture(), gateway).RunAsync(Request(AuthenticatedTestingMethod.LocalHttpsProxy, false, target));
        var op = report.Targets.Single().Operations.Single(o => o.Executed);
        Assert.Equal(ApiResponseBodyDecoding.NotDecoded, op.Body!.Decoding);
        Assert.Null(op.ContentLength);
        Assert.Equal(ApiReviewCheckResult.NotTested, OpCheck(report, "rest-payload").Result);
        Assert.Equal(ApiReviewCheckResult.NotTested, OpCheck(report, "rest-json-valid").Result);
    }

    /// <summary>An authenticated gateway whose upstream compressed although no Accept-Encoding was sent.</summary>
    private sealed class EncodingGateway : IAuthenticatedReviewGateway
    {
        public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity) =>
            new() { Method = identity.Method, ContextStatus = AuthenticatedApiContextStatus.Available, PublicApi = true, AuthenticatedApi = true, AuthenticatedRest = true, AuthenticatedGraphQlQuery = true, ObservedHost = "api-dev.example.test", Reason = "test" };

        public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(AuthenticatedReviewIdentity identity, string httpMethod, string url, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthenticatedReviewExecutionOutcome
            {
                Status = AuthenticatedExecutionStatus.Executed, Mode = ReviewExecutionMode.AuthenticatedViaLocalHttpsProxy, Message = "HTTP 200",
                Result = new AuthenticatedApiExecutionResult
                {
                    StatusCode = 200, ContentType = "application/json", ContentLength = 900, ElapsedMs = 10, Outcome = "HTTP 200", JsonValid = false,
                    SecurityHeaders = new Dictionary<string, string> { ["content-encoding"] = "gzip", ["cache-control"] = "no-store" },
                },
            });

        public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(AuthenticatedReviewIdentity identity, string endpointUrl, string query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AuthenticatedGraphQlSchemaOutcome> FetchGraphQlSchemaAsync(AuthenticatedReviewIdentity identity, string endpointUrl, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
