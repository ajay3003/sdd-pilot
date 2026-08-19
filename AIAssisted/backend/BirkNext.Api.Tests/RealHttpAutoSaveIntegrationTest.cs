using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using BirkNext.Api.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace BirkNext.Api.Tests;

/// <summary>
/// Test that manually verifies JSON deserialization of AutoSaveRequest.
/// This tests the exact scenario that happens in the browser:
/// 1. Frontend sends JSON POST with artifacts
/// 2. Backend deserializes it
/// 3. Artifacts should NOT be null/empty
/// </summary>
public class RealHttpAutoSaveIntegrationTest
{
    private readonly ITestOutputHelper _output;

    public RealHttpAutoSaveIntegrationTest(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void AutoSaveRequestMustDeserializeArtifactsFromJson()
    {
        _output.WriteLine("=== TEST: JSON Deserialization of AutoSaveRequest ===");

        // This is the exact JSON that frontend sends (from AutoSaveHttpFlowTest)
        var jsonPayload = @"{
  ""generatedName"":""Test_Workspace_Auto"",
  ""artifacts"":[
    {""artifactType"":""Constitution"",""fileName"":""constitution.md"",""originalPath"":null,""content"":""constitution content"",""contentHash"":null,""encoding"":""utf-8"",""parseVersion"":""1.0""},
    {""artifactType"":""Specification"",""fileName"":""spec.md"",""originalPath"":null,""content"":""spec content"",""contentHash"":null,""encoding"":""utf-8"",""parseVersion"":""1.0""},
    {""artifactType"":""DataModel"",""fileName"":""data-model.md"",""originalPath"":null,""content"":""datamodel content"",""contentHash"":null,""encoding"":""utf-8"",""parseVersion"":""1.0""},
    {""artifactType"":""Plan"",""fileName"":""plan.md"",""originalPath"":null,""content"":""plan content"",""contentHash"":null,""encoding"":""utf-8"",""parseVersion"":""1.0""},
    {""artifactType"":""Tasks"",""fileName"":""tasks.md"",""originalPath"":null,""content"":""tasks content"",""contentHash"":null,""encoding"":""utf-8"",""parseVersion"":""1.0""}
  ]
}";

        _output.WriteLine("Input JSON payload:");
        _output.WriteLine(jsonPayload);
        _output.WriteLine("");

        // Deserialize using the exact same options ASP.NET Core uses
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        WorkspacePersistenceController.AutoSaveRequest? deserializedRequest = null;
        try
        {
            deserializedRequest = JsonSerializer.Deserialize<WorkspacePersistenceController.AutoSaveRequest>(
                jsonPayload,
                options);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"❌ DESERIALIZATION FAILED: {ex.Message}");
            throw;
        }

        _output.WriteLine("✓ JSON deserialized successfully");

        // Verify the deserialized object
        Assert.NotNull(deserializedRequest);
        _output.WriteLine($"✓ Request object created");

        Assert.NotNull(deserializedRequest.GeneratedName);
        _output.WriteLine($"✓ GeneratedName: {deserializedRequest.GeneratedName}");

        Assert.NotNull(deserializedRequest.Artifacts);
        _output.WriteLine($"✓ Artifacts property exists (not null)");

        Assert.NotEmpty(deserializedRequest.Artifacts);
        _output.WriteLine($"✓ Artifacts list is NOT empty: {deserializedRequest.Artifacts.Count} items");

        Assert.Equal(5, deserializedRequest.Artifacts.Count);
        _output.WriteLine($"✓ Artifacts count is exactly 5");

        // Verify each artifact
        _output.WriteLine("");
        _output.WriteLine("Verifying each artifact:");
        foreach (var artifact in deserializedRequest.Artifacts)
        {
            Assert.NotNull(artifact.Content);
            Assert.NotEmpty(artifact.Content);
            _output.WriteLine($"  - {artifact.ArtifactType}: {artifact.Content.Length} bytes, fileName={artifact.FileName}");
        }

        _output.WriteLine("");
        _output.WriteLine("✓✓✓ JSON DESERIALIZATION TEST PASSED ✓✓✓");
        _output.WriteLine("Frontend JSON correctly deserializes to AutoSaveRequest with 5 artifacts");
    }

    [Fact]
    public async Task AutoSaveServiceMustReceiveAndSaveDeserializedArtifacts()
    {
        _output.WriteLine("");
        _output.WriteLine("=== TEST: Service receives deserialized artifacts and saves them ===");

        var dbName = $"deserialize_test_{Guid.NewGuid()}";

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options =>
            options.UseInMemoryDatabase(dbName));
        services.AddLogging();
        services.AddScoped<IWorkspacePersistenceService, WorkspacePersistenceService>();

        var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Database.EnsureCreated();
        }

        // Simulate what the controller receives after JSON deserialization
        var deserializedArtifacts = new List<WorkspaceArtifactDto>
        {
            new() { ArtifactType = ArtifactType.Constitution, FileName = "constitution.md", Content = "constitution content" },
            new() { ArtifactType = ArtifactType.Specification, FileName = "spec.md", Content = "spec content" },
            new() { ArtifactType = ArtifactType.DataModel, FileName = "data-model.md", Content = "datamodel content" },
            new() { ArtifactType = ArtifactType.Plan, FileName = "plan.md", Content = "plan content" },
            new() { ArtifactType = ArtifactType.Tasks, FileName = "tasks.md", Content = "tasks content" }
        };

        _output.WriteLine($"Service receives {deserializedArtifacts.Count} deserialized artifacts");

        using (var scope = provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IWorkspacePersistenceService>();

            // This is what the controller calls
            var workspace = await service.AutoSaveAsync("Deserialized_Test", null, deserializedArtifacts);

            _output.WriteLine($"✓ Service.AutoSaveAsync returned workspace {workspace.Id}");
            _output.WriteLine($"✓ Response workspace contains {workspace.Artifacts.Count} artifacts");

            Assert.Equal(5, workspace.Artifacts.Count);
        }

        // Verify database
        using (var scope = provider.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var artifactCount = await context.SavedWorkspaceArtifacts.CountAsync();
            _output.WriteLine($"✓ Database contains {artifactCount} artifacts");

            Assert.Equal(5, artifactCount);
        }

        _output.WriteLine("");
        _output.WriteLine("✓✓✓ SERVICE TEST PASSED ✓✓✓");
    }
}
