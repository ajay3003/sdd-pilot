using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.DeliveryReporting;

namespace BirkNext.Web.Services;

/// <summary>Delivery Reporting → Import from Azure DevOps (api/delivery-reporting/azure-devops). The token is sent on save only and never read back:
/// connections report "configured" only. Every Azure DevOps call happens in the backend, read-only.</summary>
public interface IDeliveryAzureDevOpsApiService
{
    Task<(List<AzureDevOpsConnectionView> Connections, string? Error)> ConnectionsAsync(CancellationToken ct = default);
    Task<(AzureDevOpsConnectionView? Connection, string? Error)> SaveConnectionAsync(Guid? id, AzureDevOpsConnectionRequest request, CancellationToken ct = default);
    Task<AzureDevOpsValidationResult> ValidateAsync(Guid id, CancellationToken ct = default);
    Task<AzureDevOpsList<AzureDevOpsProjectItem>> ProjectsAsync(Guid id, CancellationToken ct = default);
    Task<AzureDevOpsList<AzureDevOpsRepositoryItem>> RepositoriesAsync(Guid id, string project, CancellationToken ct = default);
    Task<AzureDevOpsList<AzureDevOpsBranchItem>> BranchesAsync(Guid id, string project, string repository, CancellationToken ct = default);
    Task<AzureDevOpsResolveResult> ResolveAsync(AzureDevOpsResolveRequest request, CancellationToken ct = default);
    Task<(DeliveryImportPreview? Preview, string? Note, string? Error)> PreviewAsync(AzureDevOpsResolveRequest request, CancellationToken ct = default);
}

public sealed class DeliveryAzureDevOpsApiService(HttpClient http) : IDeliveryAzureDevOpsApiService
{
    private const string Base = "api/delivery-reporting/azure-devops";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record PreviewResponse(DeliveryImportPreview? Preview, string? Note);

    private static string Failure(Exception e, string what, CancellationToken ct) => BackendRequestClassifier.FromException(DeliveryReportingText.Title, e, what, ct).UserMessage;

    public async Task<(List<AzureDevOpsConnectionView> Connections, string? Error)> ConnectionsAsync(CancellationToken ct = default)
    {
        try { return (await http.GetFromJsonAsync<List<AzureDevOpsConnectionView>>($"{Base}/connections", Json, ct) ?? [], null); }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return ([], Failure(e, $"GET {Base}/connections", ct)); }
    }

    public async Task<(AzureDevOpsConnectionView? Connection, string? Error)> SaveConnectionAsync(Guid? id, AzureDevOpsConnectionRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = id is { } key ? await http.PutAsJsonAsync($"{Base}/connections/{key}", request, Json, ct) : await http.PostAsJsonAsync($"{Base}/connections", request, Json, ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<AzureDevOpsConnectionView>(Json, ct), null) : (null, await MessageAsync(response, ct));
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return (null, Failure(e, $"POST {Base}/connections", ct)); }
    }

    public async Task<AzureDevOpsValidationResult> ValidateAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsync($"{Base}/connections/{id}/validate", null, ct);
            return response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<AzureDevOpsValidationResult>(Json, ct) is { } result ? result : new(false, "Failed", await MessageAsync(response, ct));
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return new(false, "Failed", Failure(e, $"POST {Base}/validate", ct)); }
    }

    public Task<AzureDevOpsList<AzureDevOpsProjectItem>> ProjectsAsync(Guid id, CancellationToken ct = default) => ListAsync<AzureDevOpsProjectItem>($"{Base}/connections/{id}/projects", ct);

    public Task<AzureDevOpsList<AzureDevOpsRepositoryItem>> RepositoriesAsync(Guid id, string project, CancellationToken ct = default) =>
        ListAsync<AzureDevOpsRepositoryItem>($"{Base}/connections/{id}/repositories?project={Uri.EscapeDataString(project)}", ct);

    public Task<AzureDevOpsList<AzureDevOpsBranchItem>> BranchesAsync(Guid id, string project, string repository, CancellationToken ct = default) =>
        ListAsync<AzureDevOpsBranchItem>($"{Base}/connections/{id}/branches?project={Uri.EscapeDataString(project)}&repository={Uri.EscapeDataString(repository)}", ct);

    public async Task<AzureDevOpsResolveResult> ResolveAsync(AzureDevOpsResolveRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/resolve", request, Json, ct);
            return response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<AzureDevOpsResolveResult>(Json, ct) is { } result ? result : new(null, null, await MessageAsync(response, ct));
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return new(null, null, Failure(e, $"POST {Base}/resolve", ct)); }
    }

    public async Task<(DeliveryImportPreview? Preview, string? Note, string? Error)> PreviewAsync(AzureDevOpsResolveRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/preview", request, Json, ct);
            if (!response.IsSuccessStatusCode) return (null, null, await MessageAsync(response, ct));
            var body = await response.Content.ReadFromJsonAsync<PreviewResponse>(Json, ct);
            return (body?.Preview, body?.Note, body?.Preview is null ? "The import preview could not be read." : null);
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return (null, null, Failure(e, $"POST {Base}/preview", ct)); }
    }

    private async Task<AzureDevOpsList<T>> ListAsync<T>(string url, CancellationToken ct)
    {
        try { return await http.GetFromJsonAsync<AzureDevOpsList<T>>(url, Json, ct) ?? new([], "No answer from the backend."); }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return new([], Failure(e, $"GET {url.Split('?')[0]}", ct)); }
    }

    private static async Task<string> MessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } message) return message;
        }
        catch (JsonException) { }
        return $"The request failed (HTTP {(int)response.StatusCode}).";
    }
}
