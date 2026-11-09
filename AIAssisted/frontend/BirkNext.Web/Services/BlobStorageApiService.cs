using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.BlobStorage;

namespace BirkNext.Web.Services;

/// <summary>Read-only Blob Storage explorer: stores, containers, listing, properties, preview, expectation checks, observations. Nothing here writes to a store.</summary>
public interface IBlobStorageApiService
{
    Task<(List<BlobStoreView>? Stores, string? Error)> StoresAsync(string environmentId, CancellationToken ct = default);
    Task<(List<BlobContainerView>? Containers, string? Error)> ContainersAsync(string environmentId, string storeId, CancellationToken ct = default);
    Task<(BlobListing? Listing, string? Error)> ListAsync(string environmentId, string storeId, string container, string? prefix, string? continuation, CancellationToken ct = default);
    Task<(BlobProperties? Properties, string? Error)> PropertiesAsync(string environmentId, string storeId, string container, string name, CancellationToken ct = default);
    Task<(BlobPreview? Preview, string? Error)> PreviewAsync(string environmentId, string storeId, string container, string name, CancellationToken ct = default);
    Task<(BlobValidation? Validation, string? Error)> ValidateAsync(BlobValidateRequest request, CancellationToken ct = default);
    Task<(BlobObservation? Observation, string? Error)> SaveAsync(BlobValidateRequest request, CancellationToken ct = default);
    Task<(List<BlobObservation>? Observations, string? Error)> ObservationsAsync(string environmentId, CancellationToken ct = default);
    Task<string?> DeleteObservationAsync(string environmentId, Guid id, CancellationToken ct = default);
}

public sealed class BlobStorageApiService(HttpClient http) : IBlobStorageApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private const string Base = "api/blob-storage";
    private const string Area = "Blob Storage";

    private static string E(string value) => Uri.EscapeDataString(value);
    private static string Store(string storeId) => $"{Base}/stores/{E(storeId)}";

    public Task<(List<BlobStoreView>? Stores, string? Error)> StoresAsync(string environmentId, CancellationToken ct = default) =>
        GetAsync<List<BlobStoreView>>($"{Base}/stores?environmentId={E(environmentId)}", ct);

    public Task<(List<BlobContainerView>? Containers, string? Error)> ContainersAsync(string environmentId, string storeId, CancellationToken ct = default) =>
        GetAsync<List<BlobContainerView>>($"{Store(storeId)}/containers?environmentId={E(environmentId)}", ct);

    public Task<(BlobListing? Listing, string? Error)> ListAsync(string environmentId, string storeId, string container, string? prefix, string? continuation, CancellationToken ct = default) =>
        GetAsync<BlobListing>($"{Store(storeId)}/containers/{E(container)}/blobs?environmentId={E(environmentId)}"
            + (string.IsNullOrEmpty(prefix) ? "" : $"&prefix={E(prefix)}") + (continuation is null ? "" : $"&continuation={E(continuation)}"), ct);

    public Task<(BlobProperties? Properties, string? Error)> PropertiesAsync(string environmentId, string storeId, string container, string name, CancellationToken ct = default) =>
        GetAsync<BlobProperties>($"{Store(storeId)}/containers/{E(container)}/properties?environmentId={E(environmentId)}&name={E(name)}", ct);

    public Task<(BlobPreview? Preview, string? Error)> PreviewAsync(string environmentId, string storeId, string container, string name, CancellationToken ct = default) =>
        GetAsync<BlobPreview>($"{Store(storeId)}/containers/{E(container)}/preview?environmentId={E(environmentId)}&name={E(name)}", ct);

    public Task<(BlobValidation? Validation, string? Error)> ValidateAsync(BlobValidateRequest request, CancellationToken ct = default) =>
        PostAsync<BlobValidation>($"{Base}/validate", request, ct);

    public Task<(BlobObservation? Observation, string? Error)> SaveAsync(BlobValidateRequest request, CancellationToken ct = default) =>
        PostAsync<BlobObservation>($"{Base}/observations", request, ct);

    public Task<(List<BlobObservation>? Observations, string? Error)> ObservationsAsync(string environmentId, CancellationToken ct = default) =>
        GetAsync<List<BlobObservation>>($"{Base}/observations?environmentId={E(environmentId)}", ct);

    public async Task<string?> DeleteObservationAsync(string environmentId, Guid id, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{Base}/observations/{id}?environmentId={E(environmentId)}");
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? null : response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? "You do not have permission to delete observations." : await Message(response, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return BackendRequestClassifier.FromException(Area, ex).UserMessage; }
    }

    private async Task<(T? Value, string? Error)> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<T>(Json, ct), null) : (null, await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, BackendRequestClassifier.FromException(Area, ex).UserMessage); }
    }

    private async Task<(T? Value, string? Error)> PostAsync<T>(string url, BlobValidateRequest request, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await http.SendJsonForEnvironmentAsync(HttpMethod.Post, url, request, Json, request.EnvironmentId, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) return (null, "You do not have permission to save observations (configuration write permission).");
            return (null, await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, BackendRequestClassifier.FromException(Area, ex).UserMessage); }
    }

    private static async Task<string> Message(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try { if (JsonDocument.Parse(text).RootElement is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("message", out var m)) return m.GetString() ?? text; }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(text) ? $"The request failed (HTTP {(int)response.StatusCode})." : text.Trim('"');
    }
}
