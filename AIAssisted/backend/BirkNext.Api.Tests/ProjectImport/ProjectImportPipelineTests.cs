using System.Net;
using System.Text.Json;
using BirkNext.Api.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BirkNext.Api.Tests.ProjectImport;

/// <summary>Project Import through the real ASP.NET pipeline: multipart upload once, preview, commit, then Source Analysis lists the snapshot.</summary>
public sealed class ProjectImportPipelineTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        var database = Guid.NewGuid().ToString();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<AppDbContext>();
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(database));
            });
        });
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<JsonDocument> PreviewAsync(byte[] bytes, string fileName, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var multipart = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", fileName } };
        using var response = await _client.PostAsync("/api/project-import/preview", multipart);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, body);
        return JsonDocument.Parse(body);
    }

    [Fact]
    public async Task MixedImport_SourceAnalysisListsTheSnapshot_WithoutASecondUpload()
    {
        var bytes = ProjectImportServiceTests.Zip([.. ProjectImportServiceTests.Documents, .. ProjectImportServiceTests.Source]);
        // A client path in the multipart file name is metadata only: the server reads the uploaded bytes and never opens it.
        using var preview = await PreviewAsync(bytes, @"C:\Users\someone\Downloads\shop.zip");
        var root = preview.RootElement;
        root.GetProperty("documents").GetArrayLength().Should().Be(5);
        root.GetProperty("source").GetProperty("detected").GetBoolean().Should().BeTrue();
        root.GetProperty("archive").GetProperty("fileName").GetString().Should().Be("shop.zip");
        root.ToString().Should().NotContain(@"C:\Users", "no client path is echoed");
        var stagingId = root.GetProperty("stagingId").GetString();
        var importId = root.GetProperty("importId").GetString();

        using var commit = await _client.PostAsync($"/api/project-import/{stagingId}/commit?environmentId=dev", null);
        var commitBody = await commit.Content.ReadAsStringAsync();
        commit.StatusCode.Should().Be(HttpStatusCode.OK, commitBody);
        using var commitJson = JsonDocument.Parse(commitBody);
        commitJson.RootElement.GetProperty("source").GetProperty("state").GetString().Should().Be("Created");

        using var list = JsonDocument.Parse(await _client.GetStringAsync("/api/source-analysis?environmentId=dev"));
        var snapshot = list.RootElement.EnumerateArray().Should().ContainSingle().Subject;
        snapshot.GetProperty("projectImport").GetProperty("importId").GetString().Should().Be(importId);
        snapshot.GetProperty("archive").GetProperty("sha256").GetString().Should().Be(root.GetProperty("archive").GetProperty("sha256").GetString());

        using var again = await _client.PostAsync($"/api/project-import/{stagingId}/commit?environmentId=dev", null);
        again.StatusCode.Should().Be(HttpStatusCode.NotFound, "a settled staging is released");
        (await again.Content.ReadAsStringAsync()).Should().Contain("IMPORT_STAGING_EXPIRED");
    }

    [Fact]
    public async Task RejectedArchive_UsesTheStructuredUploadContract_AndCreatesNothing()
    {
        var bytes = ProjectImportServiceTests.Zip([.. ProjectImportServiceTests.Documents, ("../outside.cs", "class X {}")]);

        using var preview = await PreviewAsync(bytes, "unsafe.zip", HttpStatusCode.BadRequest);

        preview.RootElement.GetProperty("code").GetString().Should().Be("ARCHIVE_PATH_TRAVERSAL");
        preview.RootElement.GetProperty("stage").GetString().Should().Be("validation");
        using var list = JsonDocument.Parse(await _client.GetStringAsync("/api/source-analysis?environmentId=dev"));
        list.RootElement.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task DocumentsOnlyImport_WithoutEnvironment_IsNotAFailure()
    {
        using var preview = await PreviewAsync(ProjectImportServiceTests.Zip(ProjectImportServiceTests.Documents), "docs.zip");
        var stagingId = preview.RootElement.GetProperty("stagingId").GetString();

        using var commit = await _client.PostAsync($"/api/project-import/{stagingId}/commit", null);

        commit.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await commit.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("source").GetProperty("state").GetString().Should().Be("NotDetected");
    }
}

public sealed class ProjectImportWorkspacePersistenceTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("{not json", false)]
    [InlineData("{\"CurrentProjectImportId\":null}", false)]
    [InlineData("{\"CurrentProjectImportId\":\"\"}", false)]
    [InlineData("{\"Revisions\":[],\"CurrentProjectImportId\":\"import-0123456789abcdef\"}", true)]
    public void AutoSave_TreatsACurrentProjectImportAsAProject(string? lifecycle, bool expected) =>
        BirkNext.Api.Controllers.WorkspacePersistenceController.HasCurrentProjectImport(lifecycle).Should().Be(expected,
            "a source-only import has no artifacts and no Sample Project name, but it is still the current project and must be saved");

    [Fact]
    public void LocalDataReset_ClearsStagedImports()
    {
        BirkNext.Api.Services.LocalDataReset.LocalDataResetCoordinator.Cleared.Should().Contain(c => c.Contains("Project Import"));
        var store = new BirkNext.Api.Services.ProjectImport.ProjectImportStagingStore();
        var workspace = BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceArchiveReader.ReadDetailed("a.zip",
            ProjectImportServiceTests.Zip(ProjectImportServiceTests.Source)).Workspace!;
        store.Add("import-x", "a.zip", [], workspace, new());
        store.Clear();
        store.Count.Should().Be(0);
    }
}
