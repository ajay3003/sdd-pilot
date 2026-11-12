using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The client side of the workspace persistence fix: each document body is sent once, only when the backend does not have it, in bounded
/// batches; the saved lifecycle carries addresses; the live state is never changed; a failed step is a failed save (never "Saved").
/// </summary>
public sealed class WorkspaceDocumentBodyUploaderTests
{
    private sealed class Backend(params string[] alreadyStored) : HttpMessageHandler
    {
        public HashSet<string> Stored { get; } = [.. alreadyStored];
        public List<int> BatchSizes { get; } = [];
        public int FailUploads { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            if (request.RequestUri!.AbsolutePath.EndsWith("/bodies/missing"))
                return Json(new { missing = body["addresses"]!.AsArray().Select(a => (string)a!).Where(a => !Stored.Contains(a)).ToList() });
            if (FailUploads-- > 0) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            var items = body["bodies"]!.AsArray();
            BatchSizes.Add(items.Sum(i => ((string)i!["content"]!).Length));
            foreach (var i in items) Stored.Add((string)i!["sha256"]!);
            return Json(new { stored = items.Count, alreadyStored = 0 });
        }
        private static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json") };
    }

    private static SddLifecycleState Lifecycle(params string[] contents) => new()
    {
        Revisions = [.. contents.Select((c, i) => new SddArtifactRevision { Role = "Specification", Content = c, Fingerprint = $"f{i}" })],
    };

    [Fact]
    public async Task Only_missing_bodies_are_uploaded_once_and_the_lifecycle_carries_addresses()
    {
        var shared = new string('a', 1000);
        var lifecycle = Lifecycle(shared, shared, "plan");
        var backend = new Backend(WorkspaceDocumentBodyUploader.Hash("plan"));
        var uploader = new WorkspaceDocumentBodyUploader(new HttpClient(backend) { BaseAddress = new Uri("http://localhost/") });

        var prepared = await uploader.PrepareAsync(lifecycle, null);

        prepared.Succeeded.Should().BeTrue();
        backend.BatchSizes.Should().Equal(new[] { 1000 }, "the shared body once; the stored one not at all");
        var revisions = JsonNode.Parse(prepared.Json!)!["Revisions"]!.AsArray();
        revisions.Should().OnlyContain(r => (string)r!["Content"]! == "" && ((string)r!["ContentSha256"]!).Length == 64);
        lifecycle.Revisions.Should().OnlyContain(r => r.Content.Length > 0 && r.ContentSha256 == null, "the live lifecycle is never changed");

        var again = await uploader.PrepareAsync(lifecycle, null);
        again.Succeeded.Should().BeTrue();
        backend.BatchSizes.Should().HaveCount(1, "a second save uploads nothing new");
    }

    [Fact]
    public async Task Large_bodies_go_in_bounded_batches()
    {
        var lifecycle = Lifecycle([.. Enumerable.Range(0, 10).Select(i => new string((char)('a' + i), 2_000_000))]);
        var backend = new Backend();
        var prepared = await new WorkspaceDocumentBodyUploader(new HttpClient(backend) { BaseAddress = new Uri("http://localhost/") }).PrepareAsync(lifecycle, null);
        prepared.Succeeded.Should().BeTrue();
        backend.BatchSizes.Should().OnlyContain(s => s <= WorkspaceDocumentBodyUploader.MaxBatchCharacters).And.HaveCountGreaterThan(1);
        backend.BatchSizes.Sum().Should().Be(20_000_000);
        prepared.Json!.Length.Should().BeLessThan(10_000, "the save request is small however large the workspace is");
    }

    [Fact]
    public async Task A_failed_upload_is_a_failed_save_with_a_reason()
    {
        var backend = new Backend { FailUploads = 1 };
        var prepared = await new WorkspaceDocumentBodyUploader(new HttpClient(backend) { BaseAddress = new Uri("http://localhost/") }).PrepareAsync(Lifecycle("x"), null);
        prepared.Succeeded.Should().BeFalse();
        prepared.Error.Should().Contain("Uploading documents failed");
    }

    [Fact]
    public async Task The_auto_save_state_is_failed_with_the_reason_never_saved()
    {
        var persistence = new Mock<IWorkspacePersistenceApiService>();
        persistence.Setup(p => p.AutoSaveAsync(It.IsAny<string?>())).ReturnsAsync((SavedWorkspaceDto?)null);
        persistence.SetupGet(p => p.LastSaveError).Returns("Uploading documents failed (500).");
        var service = new WorkspaceAutoSaveService(new WorkspaceArtifactRepository(), persistence.Object, Mock.Of<IWorkspaceSessionRestoreService>(), Mock.Of<IWorkspaceUpdateCoordinator>(), NullLogger<WorkspaceAutoSaveService>.Instance);

        (await service.SaveNowAsync()).Should().BeFalse();
        service.SaveState.Should().Be(WorkspaceSaveState.Failed);
        service.LastSaveError.Should().Be("Uploading documents failed (500).");

        persistence.Setup(p => p.AutoSaveAsync(It.IsAny<string?>())).ReturnsAsync(new SavedWorkspaceDto());
        persistence.SetupGet(p => p.LastSaveError).Returns((string?)null);
        (await service.SaveNowAsync()).Should().BeTrue();
        service.SaveState.Should().Be(WorkspaceSaveState.Saved);
    }
}
