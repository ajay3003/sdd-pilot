using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.TestEvidence;

namespace BirkNext.Web.Services;

/// <summary>Context the user gives with a result file. A snapshot selected for correlation binds the source version only when confirmed.</summary>
public sealed record TestResultUploadContext(string? EnvironmentId, Guid? SourceSnapshotId, bool SourceBindingConfirmed, string? BuildReference, string? CommitReference, string? EnvironmentReference);

public interface ITestEvidenceApiService
{
    Task<IReadOnlyList<TestEvidenceProviderDescriptor>> ProvidersAsync(CancellationToken ct = default);
    Task<(SourceTestInventory? Inventory, string? Error)> SourceTestsAsync(string environmentId, Guid snapshotId, CancellationToken ct = default);
    Task<(TestResultArtifactPreview? Preview, string? Error)> PreviewAsync(string fileName, Stream content, TestResultUploadContext context, CancellationToken ct = default);
}

public sealed class TestEvidenceApiService(HttpClient http) : ITestEvidenceApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<TestEvidenceProviderDescriptor>> ProvidersAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<TestEvidenceProviderDescriptor>>("api/test-evidence/providers", Json, ct) ?? [];

    public async Task<(SourceTestInventory? Inventory, string? Error)> SourceTestsAsync(string environmentId, Guid snapshotId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/test-evidence/source-tests?environmentId={Uri.EscapeDataString(environmentId)}&snapshotId={snapshotId}", ct);
        if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<SourceTestInventory>(Json, ct), null);
        return (null, await Message(response, ct) ?? "Source test definitions are unavailable for this snapshot.");
    }

    public async Task<(TestResultArtifactPreview? Preview, string? Error)> PreviewAsync(string fileName, Stream content, TestResultUploadContext context, CancellationToken ct = default)
    {
        using var body = new MultipartFormDataContent();
        body.Add(new StreamContent(content), "file", fileName);
        void Field(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) body.Add(new StringContent(value), name); }
        Field("environmentId", context.EnvironmentId);
        Field("sourceSnapshotId", context.SourceSnapshotId?.ToString());
        Field("sourceBindingConfirmed", context.SourceBindingConfirmed ? "true" : "false");
        Field("buildReference", context.BuildReference);
        Field("commitReference", context.CommitReference);
        Field("environmentReference", context.EnvironmentReference);
        using var response = await http.PostAsync("api/test-evidence/results/preview", body, ct);
        if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<TestResultArtifactPreview>(Json, ct), null);
        return (null, await Message(response, ct) ?? "The test result file could not be validated. Nothing was imported.");
    }

    private static async Task<string?> Message(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.TryGetProperty("message", out var message) ? message.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
