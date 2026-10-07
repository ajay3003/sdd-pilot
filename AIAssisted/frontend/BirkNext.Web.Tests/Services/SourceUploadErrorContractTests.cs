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
