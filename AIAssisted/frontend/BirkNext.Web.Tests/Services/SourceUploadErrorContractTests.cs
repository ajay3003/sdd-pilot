using System.Net;
using System.Text;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class SourceUploadErrorContractTests
{
    [Fact]
    public async Task ParsesStructuredBackendValidationFailureWithoutLosingSafePathOrCode()
    {
        const string json = """{"code":"ARCHIVE_PATH_TRAVERSAL","stage":"validation","message":"Archive entry escapes the project root: ../escape.cs.","entryPath":"../escape.cs","actual":null,"limit":null}""";
        var client = Create(HttpStatusCode.BadRequest, json);
        var (snapshot, failure) = await client.AnalyzeSourceSnapshotDetailedAsync("dev", "unsafe.zip", new MemoryStream([1, 2, 3]));

        snapshot.Should().BeNull();
        failure.Should().NotBeNull();
        failure!.Code.Should().Be("ARCHIVE_PATH_TRAVERSAL");
        failure.StageLabel.Should().Be("Archive validation");
        failure.Guidance.Should().Contain("Rebuild the ZIP from the project folder");
        failure.EntryPath.Should().Be("../escape.cs");
    }

    [Fact]
    public async Task MapsFrameworkPayloadTooLargeResponseToSpecificSafeMessage()
    {
        var client = Create(HttpStatusCode.RequestEntityTooLarge, "plain proxy response");
        var (_, failure) = await client.AnalyzeSourceSnapshotDetailedAsync("dev", "large.zip", new MemoryStream([1]));
        failure!.Code.Should().Be("ARCHIVE_TOO_LARGE");
        failure.Message.Should().Contain("50 MB");
        failure.Limit.Should().Be(50L * 1024 * 1024);
    }

    [Fact]
    public async Task ParsesProblemDetailsDetailAndFlattenedExtensionsWithoutGenericFallback()
    {
        const string json = """{"type":"about:blank","title":"Source archive rejected","status":400,"detail":"Archive entry escapes the project root.","code":"ARCHIVE_PATH_TRAVERSAL","stage":"ArchiveValidation","entryPath":"../escape.cs","actual":null,"limit":null}""";
        var client = Create(HttpStatusCode.BadRequest, json);

        var (_, failure) = await client.AnalyzeSourceSnapshotDetailedAsync("dev", "unsafe.zip", new MemoryStream([1, 2, 3]));

        failure.Should().NotBeNull();
        failure!.Code.Should().Be("ARCHIVE_PATH_TRAVERSAL");
        failure.Stage.Should().Be("validation");
        failure.Message.Should().Be("Archive entry escapes the project root.");
        failure.EntryPath.Should().Be("../escape.cs");
    }

    [Fact]
    public async Task UnstructuredBadRequestIsTheOnlyArchiveRejectedFallback()
    {
        var client = Create(HttpStatusCode.BadRequest, "legacy proxy response with no known reason");
        var (_, failure) = await client.AnalyzeSourceSnapshotDetailedAsync("dev", "unknown.zip", new MemoryStream([1]));
        failure!.Code.Should().Be("ARCHIVE_REJECTED");
        failure.Stage.Should().Be("validation");
    }

    [Fact]
    public async Task Unstructured422IsAnalysisFailureRatherThanArchiveRejection()
    {
        var client = Create(HttpStatusCode.UnprocessableEntity, "legacy analysis response");
        var (_, failure) = await client.AnalyzeSourceSnapshotDetailedAsync("dev", "valid.zip", new MemoryStream([1]));
        failure!.Code.Should().Be("SOURCE_ANALYSIS_FAILED");
        failure.Stage.Should().Be("analysis");
        failure.Code.Should().NotBe("ARCHIVE_REJECTED");
    }

    [Fact]
    public async Task ProblemDetailsDoesNotExposeServerPathOrStackTrace()
    {
        const string json = """{"status":400,"detail":"InvalidDataException at C:\\temp\\extract\\archive.zip","code":"ARCHIVE_INVALID_ZIP","stage":"ArchiveValidation","entryPath":"C:\\temp\\secret.txt"}""";
        var client = Create(HttpStatusCode.BadRequest, json);

        var (_, failure) = await client.AnalyzeSourceSnapshotDetailedAsync("dev", "broken.zip", new MemoryStream([1]));

        failure.Should().NotBeNull();
        failure!.Code.Should().Be("ARCHIVE_INVALID_ZIP");
        failure.Message.Should().Be("The uploaded file is not a valid ZIP archive or is incomplete.");
        failure.EntryPath.Should().BeNull();
    }

    [Fact]
    public async Task ServerErrorWithLegacyValidationWordsIsNotMappedToArchiveRejection()
    {
        var client = Create(HttpStatusCode.InternalServerError, "Invalid or incomplete ZIP archive at /tmp/server-path");
        var (_, failure) = await client.AnalyzeSourceSnapshotDetailedAsync("dev", "broken.zip", new MemoryStream([1]));
        failure!.Code.Should().Be("SOURCE_SERVER_ERROR");
        failure.Stage.Should().Be("analysis");
    }

    [Fact]
    public void SourceAnalysisClientTimeoutAllowsLongDeterministicAnalysis()
    {
        var http = new HttpClient(new FixedResponseHandler(HttpStatusCode.OK, "{}"));

        _ = new IntegrationCatalogApiService(http);

        http.Timeout.Should().BeGreaterThan(TimeSpan.FromSeconds(100));
    }

    [Fact]
    public void UploadTimeoutGuidanceDoesNotClaimTheArchiveWasRejected()
    {
        var failure = new SourceUploadFailure("SOURCE_UPLOAD_TIMEOUT", "upload", "The upload timed out.");

        failure.Guidance.Should().Contain("Check the snapshot list").And.NotContain("rejected");
        failure.StageLabel.Should().Be("Upload");
    }

    private static IntegrationCatalogApiService Create(HttpStatusCode status, string body)
    {
        var http = new HttpClient(new FixedResponseHandler(status, body)) { BaseAddress = new Uri("http://localhost/") };
        return new IntegrationCatalogApiService(http);
    }

    private sealed class FixedResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
