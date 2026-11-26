using System.Net;
using System.Text;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The duplicate GET current-state at start-up (layout + workspace projection, 2026-11-26): callers that ask while a read is in flight share it;
/// a completed read is never reused, so the next caller always sees the backend's current answer.
/// </summary>
public sealed class CurrentStateCoalescingTests
{
    private sealed class GatedHandler : HttpMessageHandler
    {
        public int Calls;
        public TaskCompletionSource Gate = new();
        public string Body = """{"currentWorkspaceId":null,"artifactCount":3}""";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            await Gate.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
    }

    private static WorkspacePersistenceApiService Api(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, NullLogger<WorkspacePersistenceApiService>.Instance, new WorkspaceArtifactRepository(), null, null);

    [Fact]
    public async Task Concurrent_reads_share_one_request_and_a_later_read_asks_again()
    {
        var handler = new GatedHandler();
        var api = Api(handler);

        var first = api.GetCurrentStateAsync();
        var second = api.GetCurrentStateAsync();
        handler.Gate.SetResult();
        var (a, b) = (await first, await second);

        handler.Calls.Should().Be(1, "two callers during one read make one request");
        a.Should().BeSameAs(b);
        a!.ArtifactCount.Should().Be(3);

        handler.Body = """{"currentWorkspaceId":null,"artifactCount":7}""";
        (await api.GetCurrentStateAsync())!.ArtifactCount.Should().Be(7, "a completed read is never reused");
        handler.Calls.Should().Be(2);
    }
}
