using BirkNext.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace BirkNext.Web.Tests.Integration;

/// <summary>
/// End-to-end test that simulates the complete AutoSave HTTP flow.
/// Captures and verifies what is actually sent in the POST request.
/// </summary>
public class AutoSaveHttpFlowTest
{
    private readonly ITestOutputHelper _output;

    public AutoSaveHttpFlowTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task AutoSaveMustSendArtifactsInHttpRequest()
    {
        _output.WriteLine("=== STEP 1: Setup repository with 5 artifacts ===");

        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceArtifactRepository, WorkspaceArtifactRepository>();
        services.AddSingleton<IWorkspaceUpdateCoordinator, WorkspaceUpdateCoordinator>();
        services.AddLogging();

        // Mock HTTP client that captures requests
        var capturedRequests = new List<string>();
        var mockHttpHandler = new MockHttpMessageHandler(
            requestBody =>
            {
                capturedRequests.Add(requestBody);
                // Return success response
                return new HttpResponseMessage
                {
                    StatusCode = System.Net.HttpStatusCode.OK,
                    Content = new StringContent(
                        JsonSerializer.Serialize(new
                        {
                            id = Guid.NewGuid(),
                            name = "Test_Workspace",
                            artifacts = new object[0], // Empty array in response
                            autoSaved = true
                        }),
                        System.Text.Encoding.UTF8,
                        "application/json")
                };
            });

        var httpClient = new HttpClient(mockHttpHandler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };

        services.AddSingleton(httpClient);
        services.AddScoped<IWorkspacePersistenceApiService, WorkspacePersistenceApiService>();

        var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<IWorkspaceArtifactRepository>();

        // Load 5 artifacts into repository
        repository.Set(WorkspaceArtifactType.Constitution, "constitution content", fileName: "constitution.md");
        repository.Set(WorkspaceArtifactType.Specification, "spec content", fileName: "spec.md");
        repository.Set(WorkspaceArtifactType.DataModel, "datamodel content", fileName: "data-model.md");
        repository.Set(WorkspaceArtifactType.Plan, "plan content", fileName: "plan.md");
        repository.Set(WorkspaceArtifactType.Tasks, "tasks content", fileName: "tasks.md");

        var count = repository.GetAllArtifacts().Count();
        _output.WriteLine($"✓ Repository loaded with {count} artifacts");
        Assert.Equal(5, count);

        _output.WriteLine("");
        _output.WriteLine("=== STEP 2: Call AutoSaveAsync (which should read artifacts and send them) ===");

        using (var scope = provider.CreateScope())
        {
            var apiService = scope.ServiceProvider.GetRequiredService<IWorkspacePersistenceApiService>();

            _output.WriteLine("Calling AutoSaveAsync...");
            var result = await apiService.AutoSaveAsync("Test_Workspace_Auto");

            _output.WriteLine("✓ AutoSaveAsync completed");
            Assert.NotNull(result);
        }

        _output.WriteLine("");
        _output.WriteLine("=== STEP 3: Verify HTTP Request Content ===");
        _output.WriteLine($"Captured {capturedRequests.Count} HTTP request(s)");

        Assert.NotEmpty(capturedRequests);
        var requestBody = capturedRequests[0];
        _output.WriteLine($"Request body: {requestBody}");

        // Parse the JSON request
        var jsonDoc = JsonDocument.Parse(requestBody);
        var root = jsonDoc.RootElement;

        Assert.True(root.TryGetProperty("artifacts", out var artifactsElement));
        _output.WriteLine("✓ Request contains 'artifacts' property");

        Assert.Equal(JsonValueKind.Array, artifactsElement.ValueKind);
        var artifactCount = artifactsElement.GetArrayLength();
        _output.WriteLine($"✓ Request contains {artifactCount} artifacts in array");

        Assert.Equal(5, artifactCount);

        // Verify each artifact has required fields
        _output.WriteLine("✓ Verifying each artifact in request:");
        var index = 0;
        foreach (var artifact in artifactsElement.EnumerateArray())
        {
            var type = artifact.GetProperty("artifactType").GetString();
            var content = artifact.GetProperty("content").GetString();
            var fileName = artifact.GetProperty("fileName").GetString();

            Assert.NotNull(type);
            Assert.NotNull(content);
            Assert.NotEmpty(content);
            Assert.NotNull(fileName);

            _output.WriteLine($"    [{index}] Type={type}, FileName={fileName}, ContentLength={content.Length}");
            index++;
        }

        _output.WriteLine("");
        _output.WriteLine("=== STEP 4: Verify GeneratedName ===");

        Assert.True(root.TryGetProperty("generatedName", out var generatedNameElement));
        var generatedName = generatedNameElement.GetString();
        _output.WriteLine($"✓ Request contains generatedName: {generatedName}");
        Assert.NotNull(generatedName);
        Assert.NotEmpty(generatedName);

        _output.WriteLine("");
        _output.WriteLine("=== TEST COMPLETE ===");
        _output.WriteLine("✓ AutoSaveAsync correctly sends all 5 artifacts in HTTP request");
        _output.WriteLine("✓ Each artifact has required fields (type, content, fileName)");
        _output.WriteLine("✓ Request body is valid JSON");
    }

    /// <summary>
    /// Mock HTTP handler that captures request body and returns a fixed response.
    /// </summary>
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<string, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string requestBody = "";
            if (request.Content != null)
            {
                requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return _handler(requestBody);
        }
    }
}
