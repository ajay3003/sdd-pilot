using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BirkNext.Api.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BirkNext.Api.Tests.Controllers;

/// <summary>
/// Source Analysis upload through the real ASP.NET pipeline (routing, [ApiController] validation, multipart binding, request limits),
/// not by calling the action directly — a direct call skips the model-validation filter that produced the code-less 400 behind
/// "ARCHIVE_REJECTED" for the real M2LB upload.
/// </summary>
public sealed class SourceUploadPipelineTests : IAsyncLifetime
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

    private Task<HttpResponseMessage> Upload(string query, byte[] bytes, string fileName = "M2LB (2).zip")
    {
        var multipart = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", fileName } };
        return _client.PostAsync($"/api/source-analysis/snapshots{query}", multipart);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?environmentId=")]
    [InlineData("?environmentId=%20")]
    public async Task MissingEnvironment_ReturnsTheStructuredPrerequisiteFailure_NotACodelessProblemDetails(string query)
    {
        using var response = await Upload(query, Zip(("repo/Program.cs", "class P {}")));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json", "not application/problem+json from request validation");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("code").GetString().Should().Be("NO_ACTIVE_ENVIRONMENT");
        json.RootElement.GetProperty("stage").GetString().Should().Be("prerequisite");
    }

    [Fact]
    public async Task UploadedBytes_TravelThroughBindingBufferReaderAndSnapshot_Unchanged()
    {
        // Non-ASCII entry names, as in the real archive (Norwegian characters in 31 of its 2165 entries).
        var bytes = Zip(("repo/src/Barnevern/Tjeneste.cs", "namespace Barnevern; class Tjeneste {}"),
                        ("repo/src/Vedtak/Ærlig\u00f8\u00e5.cs", "class Ok {}"),
                        ("repo/repo.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"));

        using var response = await Upload("?environmentId=dev", bytes, "M2LB (2)(5).zip");

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("archive").GetProperty("sha256").GetString()
            .Should().Be(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "the server read exactly the uploaded bytes");
        body.Should().NotContain(@"C:\").And.NotContain("/tmp/");
    }

    [Fact]
    public async Task ListWithoutEnvironment_IsAPlainRequestError_NotProblemDetailsValidation()
    {
        using var response = await _client.GetAsync("/api/source-analysis?environmentId=");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("environmentId is required");
    }
}
