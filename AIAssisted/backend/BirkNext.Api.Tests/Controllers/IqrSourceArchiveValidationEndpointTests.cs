using System.IO.Compression;
using System.Text.Json;
using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Moq;

namespace BirkNext.Api.Tests.Controllers;

public sealed class IqrSourceArchiveValidationEndpointTests
{
    [Fact]
    public async Task TraversalUploadReturnsStructuredSafeValidationFailureAndCreatesNoSnapshot()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);
        var controller = new IqrSourceEvidenceController(new IqrSourceStore(db),
            Mock.Of<BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider>(), NullLogger<IqrSourceEvidenceController>.Instance);
        var content = TraversalZip();
        var file = new FormFile(new MemoryStream(content), 0, content.Length, "file", "unsafe.zip");
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=unit-test";
        context.Request.ContentLength = content.Length;
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>(), new FormFileCollection { file });
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        var result = await controller.AnalyzeSourceSnapshot("dev", CancellationToken.None);

        var response = result.Should().BeOfType<ObjectResult>().Subject;
        response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Value));
        var body = json.RootElement;
        body.GetProperty("code").GetString().Should().Be("ARCHIVE_PATH_TRAVERSAL");
        body.GetProperty("stage").GetString().Should().Be("validation");
        body.GetProperty("message").GetString().Should().Contain("escapes the project root").And.Contain("../escape.cs");
        body.ToString().Should().NotContain("InvalidDataException").And.NotContain("\\bin\\").And.NotContain("Temp");
        (await db.IqrSourceSnapshots.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MissingEnvironmentIsNoLongerAPrerequisite_TheUploadItselfIsValidated()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var controller = new IqrSourceEvidenceController(new IqrSourceStore(db),
            Mock.Of<BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider>(), NullLogger<IqrSourceEvidenceController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await controller.AnalyzeSourceSnapshot("", CancellationToken.None);
        var response = result.Should().BeOfType<ObjectResult>().Subject;
        JsonSerializer.Serialize(response.Value).Should().NotContain("NO_ACTIVE_ENVIRONMENT").And.NotContain("prerequisite", "no file was sent: the upload is rejected, never the missing target");
        (await db.IqrSourceSnapshots.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SafeUnknownTechnologyArchiveCreatesSnapshotThroughUploadEndpoint()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);
        var controller = new IqrSourceEvidenceController(new IqrSourceStore(db),
            Mock.Of<BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider>(), NullLogger<IqrSourceEvidenceController>.Instance);
        var content = Zip(("custom-repo/pom.xml", "<project/>"), ("custom-repo/src/Main.java", "class Main {}"));
        var file = new FormFile(new MemoryStream(content), 0, content.Length, "file", "custom-repo.zip");
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=unit-test";
        context.Request.ContentLength = content.Length;
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>(), new FormFileCollection { file });
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        var result = await controller.AnalyzeSourceSnapshot("dev", CancellationToken.None);

        var response = result.Should().BeOfType<OkObjectResult>().Subject;
        response.Value.Should().BeOfType<BirkNext.Integrations.IqrSourceSnapshot>();
        (await db.IqrSourceSnapshots.CountAsync()).Should().Be(1);
    }

    private static byte[] TraversalZip()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("../escape.cs").Open())) writer.Write("class Escape {} ");
        return stream.ToArray();
    }

    private static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(content);
            }
        return stream.ToArray();
    }
}
