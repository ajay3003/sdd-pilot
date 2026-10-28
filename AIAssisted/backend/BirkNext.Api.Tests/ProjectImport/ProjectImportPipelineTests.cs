using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.ProjectImport;
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
    /// <summary>This test's own durable staging directory (never the shared default a running BirkNext uses).</summary>
    private readonly string _staging = Path.Combine(Path.GetTempPath(), "birknext-tests", "project-import-staging-" + Guid.NewGuid().ToString("N"));

    private WebApplicationFactory<Program> CreateHost() => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        var database = Guid.NewGuid().ToString();
        builder.UseEnvironment("Test");
        builder.UseSetting("ProjectImport:StagingDirectory", _staging);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(database));
        });
    });

    public Task InitializeAsync()
    {
        _factory = CreateHost();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_staging, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    /// <summary>Stops the backend and starts a new one on the same staging directory (and a new, empty database).</summary>
    private async Task RestartAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        _factory = CreateHost();
        _client = _factory.CreateClient();
    }

    private async Task<JsonDocument> PreviewAsync(byte[] bytes, string fileName, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var multipart = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", fileName } };
        using var response = await _client.PostAsync("/api/project-import/preview", multipart);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, body);
        return JsonDocument.Parse(body);
    }

    private const string GeneratedOpenApi = """
        openapi: 3.0.1
        info:
          title: Person API
          version: '1'
        paths:
          /barn/{id}:
            get:
              operationId: hentBarn
              responses:
                '200':
                  description: ok

        """;
    private const string GeneratedSdl = """
        type Query {
          hentBarn(id: ID!): Barn
        }

        type Barn {
          id: ID!
          navn: String
        }

        """;

    [Fact]
    public async Task Generated_openapi_and_graphql_artifacts_keep_their_text_without_becoming_project_documents()
    {
        using var preview = await PreviewAsync(ProjectImportServiceTests.Zip([.. ProjectImportServiceTests.Documents, .. ProjectImportServiceTests.Source,
            ("Person/autodoc/openapi.yaml", GeneratedOpenApi), ("Person/autodoc/schema.graphql", GeneratedSdl)]), "shop.zip");
        var texts = preview.RootElement.GetProperty("generatedArtifactTexts").EnumerateArray().ToDictionary(d => d.GetProperty("relativePath").GetString()!, d => d.GetProperty("content").GetString());
        texts.Should().ContainKey("Person/autodoc/openapi.yaml").WhoseValue.Should().Be(GeneratedOpenApi);
        texts.Should().ContainKey("Person/autodoc/schema.graphql").WhoseValue.Should().Be(GeneratedSdl);
        preview.RootElement.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("relativePath").GetString()).Should().NotContain(p => p!.EndsWith(".yaml") || p!.EndsWith(".graphql"),
            "they are generated artifacts, never classified as project documents");
    }

    [Fact]
    public async Task The_structure_endpoint_reads_openapi_yaml_and_graphql_sdl_locally_and_refuses_unreadable_text()
    {
        using var openApi = await _client.PostAsJsonAsync("/api/system-diagnostics/generated-documentation/structure", new { Path = "Person/autodoc/openapi.yaml", Content = GeneratedOpenApi });
        openApi.StatusCode.Should().Be(HttpStatusCode.OK);
        var contract = JsonDocument.Parse(await openApi.Content.ReadAsStringAsync()).RootElement;
        contract.GetProperty("name").GetString().Should().Be("Person API");
        contract.GetProperty("operations").EnumerateArray().Select(o => o.GetProperty("operationId").GetString()).Should().Contain("hentBarn");

        using var sdl = await _client.PostAsJsonAsync("/api/system-diagnostics/generated-documentation/structure", new { Path = "Person/autodoc/schema.graphql", Content = GeneratedSdl });
        sdl.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await sdl.Content.ReadAsStringAsync()).RootElement.GetProperty("types").EnumerateArray().Select(t => t.GetProperty("name").GetString()).Should().Contain("Barn");

        using var bad = await _client.PostAsJsonAsync("/api/system-diagnostics/generated-documentation/structure", new { Path = "Person/autodoc/openapi.json", Content = "{ not json" });
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
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
        again.StatusCode.Should().Be(HttpStatusCode.OK, "a repeated commit of a settled stage returns the same result");
        using var againJson = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
        againJson.RootElement.GetProperty("source").GetProperty("snapshotId").GetString()
            .Should().Be(commitJson.RootElement.GetProperty("source").GetProperty("snapshotId").GetString());
        using var listAgain = JsonDocument.Parse(await _client.GetStringAsync("/api/source-analysis"));
        listAgain.RootElement.GetArrayLength().Should().Be(1, "no second snapshot");
        Directory.EnumerateFiles(_staging, "*.zip").Should().BeEmpty("a settled stage keeps only its result, not the archive");
    }

    [Fact]
    public async Task StreamedCommitReportsRealAnalysisStagesBeforeItsTerminalResult()
    {
        using var preview = await PreviewAsync(ProjectImportServiceTests.Zip([.. ProjectImportServiceTests.Documents, .. ProjectImportServiceTests.Source]), "shop.zip");
        var stagingId = preview.RootElement.GetProperty("stagingId").GetString();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/project-import/{stagingId}/commit");
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/x-ndjson"));

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/x-ndjson");
        var events = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => System.Text.Json.JsonSerializer.Deserialize<ProjectImportCommitEvent>(line, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)))
            .Where(item => item is not null).Cast<ProjectImportCommitEvent>().ToList();
        events.Should().NotBeEmpty();
        events.Select(e => e.Sequence).Should().Equal(Enumerable.Range(1, events.Count));
        events.Should().Contain(e => e.Kind == ProjectImportCommitEventKind.Progress && e.Stage == "Analyzing source structure");
        events.Should().Contain(e => e.Kind == ProjectImportCommitEventKind.Progress && e.Stage == "Saving source snapshot");
        events.Where(e => e.StageCount is not null).Select(e => (e.StageNumber, e.StageCount)).Should()
            .Equal(Enumerable.Range(1, 17).Select(stage => ((int?)stage, (int?)17)), "the source-analysis updates reflect the 17 real analyzer stages");
        events[^1].Kind.Should().Be(ProjectImportCommitEventKind.Completed);
        events[^1].Result!.Source.State.Should().Be(ProjectImportSourceState.Created);
        events.Take(events.Count - 1).Should().OnlyContain(e => e.Kind == ProjectImportCommitEventKind.Progress,
            "the terminal success event is sent only after source analysis and persistence return");
        (await _factory.Services.GetRequiredService<AppDbContext>().IqrSourceSnapshots.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task StagedImport_SurvivesABackendRestart_AndCommitsWithoutAnotherUpload()
    {
        var bytes = ProjectImportServiceTests.Zip([.. ProjectImportServiceTests.Documents, .. ProjectImportServiceTests.Source]);
        using var preview = await PreviewAsync(bytes, "shop.zip");
        var stagingId = preview.RootElement.GetProperty("stagingId").GetString();
        var sha = preview.RootElement.GetProperty("archive").GetProperty("sha256").GetString();
        Directory.EnumerateFiles(_staging).Select(Path.GetFileName).Should().BeEquivalentTo(
            [$"{Guid.Parse(stagingId!):N}.json", $"{Guid.Parse(stagingId!):N}.zip"], "file names come from the staging id only");
        File.ReadAllText(Directory.EnumerateFiles(_staging, "*.json").Single()).Should().NotContain("Program.cs", "the record holds metadata, not source");

        await RestartAsync();

        using var commit = await _client.PostAsync($"/api/project-import/{stagingId}/commit", null);
        var body = await commit.Content.ReadAsStringAsync();
        commit.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("source").GetProperty("state").GetString().Should().Be("Created");
        json.RootElement.GetProperty("provenance").GetProperty("archiveSha256").GetString().Should().Be(sha, "the same staged bytes");
        using var list = JsonDocument.Parse(await _client.GetStringAsync("/api/source-analysis"));
        list.RootElement.EnumerateArray().Should().ContainSingle();

        await RestartAsync();
        using var again = await _client.PostAsync($"/api/project-import/{stagingId}/commit", null);
        again.StatusCode.Should().Be(HttpStatusCode.OK, "the settled result survives a restart too");
        JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement.GetProperty("source").GetProperty("snapshotId").GetString()
            .Should().Be(json.RootElement.GetProperty("source").GetProperty("snapshotId").GetString());
    }

    [Fact]
    public async Task UnknownStage_IsAnExplicitExpiry_NotAGenericError()
    {
        using var commit = await _client.PostAsync($"/api/project-import/{Guid.NewGuid()}/commit", null);

        commit.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var json = JsonDocument.Parse(await commit.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("code").GetString().Should().Be("IMPORT_STAGING_EXPIRED");
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
