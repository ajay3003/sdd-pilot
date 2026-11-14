using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>Project-declared Integration Journeys (name + ordered component chain). Design metadata only: nothing runs or is associated from here.</summary>
public interface IDeclaredIntegrationJourneyApiService
{
    Task<(IReadOnlyList<DeclaredIntegrationJourney>? Journeys, string? Error)> ListAsync(string projectKey, CancellationToken ct = default);
    Task<(DeclaredIntegrationJourney? Journey, string? Error)> DeclareAsync(DeclaredIntegrationJourneyRequest request, CancellationToken ct = default);
    Task<string?> RemoveAsync(string projectKey, string journeyId, CancellationToken ct = default);
}

public sealed class DeclaredIntegrationJourneyApiService(HttpClient http) : IDeclaredIntegrationJourneyApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(IReadOnlyList<DeclaredIntegrationJourney>? Journeys, string? Error)> ListAsync(string projectKey, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync($"api/integration-journeys/declared?projectKey={Uri.EscapeDataString(projectKey)}", ct);
            if (!response.IsSuccessStatusCode) return (null, await MessageAsync(response, "Declared journeys could not be loaded", ct));
            return (await response.Content.ReadFromJsonAsync<List<DeclaredIntegrationJourney>>(Json, ct) ?? [], null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, "Declared journeys could not be loaded: the backend could not be reached."); }
    }

    public async Task<(DeclaredIntegrationJourney? Journey, string? Error)> DeclareAsync(DeclaredIntegrationJourneyRequest request, CancellationToken ct = default)
    {
        // Validated before sending as well (the backend validates again): names only, never a URL or credential.
        if (DeclaredIntegrationJourneyRules.Validate(request) is { } invalid) return (null, invalid);
        try
        {
            using var response = await http.PostAsJsonAsync("api/integration-journeys/declared", request, Json, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<DeclaredIntegrationJourney>(Json, ct), null);
            return (null, await MessageAsync(response, "The journey was not saved", ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, "The journey was not saved: the backend could not be reached."); }
    }

    public async Task<string?> RemoveAsync(string projectKey, string journeyId, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.DeleteAsync($"api/integration-journeys/declared/{Uri.EscapeDataString(projectKey)}/{Uri.EscapeDataString(journeyId)}", ct);
            return response.IsSuccessStatusCode ? null : await MessageAsync(response, "The journey was not removed", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return "The journey was not removed: the backend could not be reached."; }
    }

    private static async Task<string> MessageAsync(HttpResponseMessage response, string fallback, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } message) return message;
        }
        catch (JsonException) { }
        return $"{fallback} ({(int)response.StatusCode}).";
    }
}
