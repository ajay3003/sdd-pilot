using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.AzureEnvironment;

namespace BirkNext.Web.Services;

public sealed record AzureSubscriptionsResult(List<AzureSubscription> Subscriptions, AzureCapability? Capability, string? Error);

/// <summary>Azure Environment Analysis backend calls. Sign-in and analysis act on BirkNext's own state; the backend only reads Azure.</summary>
public interface IAzureEnvironmentApiService
{
    Task<AzureConnectionStatus> StatusAsync(CancellationToken ct = default);
    Task<AzureConnectionStatus> SignInInteractiveAsync(CancellationToken ct = default);
    Task<AzureConnectionStatus> SignInDeviceCodeAsync(CancellationToken ct = default);
    Task<AzureConnectionStatus> RefreshAsync(CancellationToken ct = default);
    Task<AzureConnectionStatus> SignOutAsync(CancellationToken ct = default);
    Task<AzureSubscriptionsResult> SubscriptionsAsync(CancellationToken ct = default);
    Task<(AzureEnvironmentSnapshot? Snapshot, string? Error)> AnalyzeAsync(AzureAnalysisRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<AzureEnvironmentSnapshotSummary>> SnapshotsAsync(string environmentId, CancellationToken ct = default);
    Task<AzureEnvironmentSnapshot?> SnapshotAsync(string environmentId, Guid id, CancellationToken ct = default);
    Task<(DeclaredObservedComparison? Comparison, string? Error)> ComparisonAsync(string environmentId, Guid azureSnapshotId, Guid? sourceSnapshotId, string? environment, CancellationToken ct = default);
    Task<IReadOnlyList<AzureTargetSuggestion>> TargetSuggestionsAsync(string environmentId, Guid snapshotId, CancellationToken ct = default);
}

public sealed class AzureEnvironmentApiService(HttpClient http) : IAzureEnvironmentApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Base = "api/azure-environment";

    private static AzureConnectionStatus Unreachable(Exception e) => new() { State = AzureConnectionState.Failed, Message = $"BirkNext.Api could not be reached ({e.GetType().Name})." };

    public async Task<AzureConnectionStatus> StatusAsync(CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<AzureConnectionStatus>($"{Base}/status", Json, ct) ?? new(); }
        catch (HttpRequestException e) { return Unreachable(e); }
    }

    public Task<AzureConnectionStatus> SignInInteractiveAsync(CancellationToken ct = default) => Post("sign-in/interactive", ct);
    public Task<AzureConnectionStatus> SignInDeviceCodeAsync(CancellationToken ct = default) => Post("sign-in/device-code", ct);
    public Task<AzureConnectionStatus> RefreshAsync(CancellationToken ct = default) => Post("refresh", ct);
    public Task<AzureConnectionStatus> SignOutAsync(CancellationToken ct = default) => Post("sign-out", ct);

    private async Task<AzureConnectionStatus> Post(string route, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsync($"{Base}/{route}", null, ct);
            if (response.IsSuccessStatusCode) return await response.Content.ReadFromJsonAsync<AzureConnectionStatus>(Json, ct) ?? new();
            return (await StatusAsync(ct)) with { Message = await Message(response, ct) };
        }
        catch (HttpRequestException e) { return Unreachable(e); }
    }

    public async Task<AzureSubscriptionsResult> SubscriptionsAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync($"{Base}/subscriptions", ct);
            if (!response.IsSuccessStatusCode) return new([], null, await Message(response, ct));
            var body = await response.Content.ReadFromJsonAsync<SubscriptionsBody>(Json, ct);
            return new(body?.Subscriptions ?? [], body?.Capability, null);
        }
        catch (HttpRequestException e) { return new([], null, $"BirkNext.Api could not be reached ({e.GetType().Name})."); }
    }

    private sealed record SubscriptionsBody(List<AzureSubscription> Subscriptions, AzureCapability? Capability);

    public async Task<(AzureEnvironmentSnapshot? Snapshot, string? Error)> AnalyzeAsync(AzureAnalysisRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/analyze", request, Json, ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<AzureEnvironmentSnapshot>(Json, ct), null) : (null, await Message(response, ct));
        }
        catch (HttpRequestException e) { return (null, $"BirkNext.Api could not be reached ({e.GetType().Name})."); }
    }

    public async Task<IReadOnlyList<AzureEnvironmentSnapshotSummary>> SnapshotsAsync(string environmentId, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<List<AzureEnvironmentSnapshotSummary>>($"{Base}/snapshots?environmentId={Uri.EscapeDataString(environmentId)}", Json, ct) ?? []; }
        catch (HttpRequestException) { return []; }
    }

    public async Task<AzureEnvironmentSnapshot?> SnapshotAsync(string environmentId, Guid id, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<AzureEnvironmentSnapshot>($"{Base}/snapshots/{id}?environmentId={Uri.EscapeDataString(environmentId)}", Json, ct); }
        catch (HttpRequestException) { return null; }
    }

    public async Task<(DeclaredObservedComparison? Comparison, string? Error)> ComparisonAsync(string environmentId, Guid azureSnapshotId, Guid? sourceSnapshotId, string? environment, CancellationToken ct = default)
    {
        var url = $"{Base}/comparison?environmentId={Uri.EscapeDataString(environmentId)}&azureSnapshotId={azureSnapshotId}"
                  + (sourceSnapshotId is { } s ? $"&sourceSnapshotId={s}" : "") + (string.IsNullOrWhiteSpace(environment) ? "" : $"&environment={Uri.EscapeDataString(environment)}");
        try
        {
            using var response = await http.GetAsync(url, ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<DeclaredObservedComparison>(Json, ct), null) : (null, await Message(response, ct));
        }
        catch (HttpRequestException e) { return (null, $"BirkNext.Api could not be reached ({e.GetType().Name})."); }
    }

    public async Task<IReadOnlyList<AzureTargetSuggestion>> TargetSuggestionsAsync(string environmentId, Guid snapshotId, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<List<AzureTargetSuggestion>>($"{Base}/target-suggestions?environmentId={Uri.EscapeDataString(environmentId)}&snapshotId={snapshotId}", Json, ct) ?? []; }
        catch (HttpRequestException) { return []; }
    }

    private static async Task<string> Message(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try { if (JsonDocument.Parse(text).RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } message) return message; }
        catch (JsonException) { }
        return $"The request was not completed (HTTP {(int)response.StatusCode}).";
    }
}
