using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Specifications;

namespace BirkNext.Web.Services;

/// <summary>
/// System Settings → Spesifikasjonsformat and the read-only OpenSpec views (api/specification-format). Errors come back as a Norwegian message to
/// show; nothing is retried by itself.
/// </summary>
public interface ISpecificationFormatApiService
{
    Task<(SpecificationFormatSettingsView? View, string? Error)> GetAsync(CancellationToken ct = default);
    Task<(SpecificationFormatImpact? Impact, string? Error)> ImpactAsync(string applicationKey, SpecificationFormat format, CancellationToken ct = default);
    /// <summary>Saves a format. A 409 carries the impact that must be confirmed first (returned as <c>Impact</c>).</summary>
    Task<(SpecificationFormatSettingsView? View, SpecificationFormatImpact? Impact, string? Error)> SaveAsync(SaveSpecificationFormatRequest request, CancellationToken ct = default);
    Task<(List<FormatConsumerStatus>? Consumers, string? Error)> ConsumersAsync(CancellationToken ct = default);
    Task<(OpenSpecSourceStatus? Status, string? Error)> OpenSpecStatusAsync(string projectKey, CancellationToken ct = default);
    Task<(OpenSpecSourceChangeReport? Report, string? Error)> OpenSpecSourceChangesAsync(string projectKey, CancellationToken ct = default);
    Task<(OpenSpecReconciliationReport? Report, string? Error)> OpenSpecReconciliationAsync(string projectKey, CancellationToken ct = default);
    Task<(string? Markdown, string? Error)> OpenSpecReconciliationMarkdownAsync(string projectKey, CancellationToken ct = default);
}

public sealed class SpecificationFormatApiService(HttpClient http) : ISpecificationFormatApiService
{
    private const string Base = "api/specification-format";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static string Project(string projectKey) => $"{Base}/projects/{Uri.EscapeDataString(projectKey)}/openspec";

    public Task<(SpecificationFormatSettingsView?, string?)> GetAsync(CancellationToken ct = default) => Send<SpecificationFormatSettingsView>(() => http.GetAsync(Base, ct), ct);

    public Task<(SpecificationFormatImpact?, string?)> ImpactAsync(string applicationKey, SpecificationFormat format, CancellationToken ct = default) =>
        Send<SpecificationFormatImpact>(() => http.GetAsync($"{Base}/impact?application={Uri.EscapeDataString(applicationKey)}&format={format}", ct), ct);

    public async Task<(SpecificationFormatSettingsView?, SpecificationFormatImpact?, string?)> SaveAsync(SaveSpecificationFormatRequest request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PutAsJsonAsync(Base, request, Json, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<SpecificationFormatSettingsView>(Json, ct), null, null);
            if ((int)response.StatusCode is 401 or 403) return (null, null, "Du har ikke tilgang til å endre systeminnstillinger.");
            var body = await Body(response, ct);
            SpecificationFormatImpact? impact = null;
            if (body is { } b && b.TryGetProperty("impact", out var i)) impact = i.Deserialize<SpecificationFormatImpact>(Json);
            return (null, impact, Message(body) ?? $"Lagringen feilet (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return (null, null, "Innstillingene for spesifikasjonsformat kunne ikke nås.");
        }
    }

    public Task<(List<FormatConsumerStatus>?, string?)> ConsumersAsync(CancellationToken ct = default) => Send<List<FormatConsumerStatus>>(() => http.GetAsync($"{Base}/consumers", ct), ct);

    public Task<(OpenSpecSourceStatus?, string?)> OpenSpecStatusAsync(string projectKey, CancellationToken ct = default) =>
        Send<OpenSpecSourceStatus>(() => http.GetAsync(Project(projectKey), ct), ct);

    public Task<(OpenSpecSourceChangeReport?, string?)> OpenSpecSourceChangesAsync(string projectKey, CancellationToken ct = default) =>
        Send<OpenSpecSourceChangeReport>(() => http.GetAsync($"{Project(projectKey)}/source-changes", ct), ct);

    public Task<(OpenSpecReconciliationReport?, string?)> OpenSpecReconciliationAsync(string projectKey, CancellationToken ct = default) =>
        Send<OpenSpecReconciliationReport>(() => http.GetAsync($"{Project(projectKey)}/reconciliation", ct), ct);

    public async Task<(string?, string?)> OpenSpecReconciliationMarkdownAsync(string projectKey, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync($"{Project(projectKey)}/reconciliation/export", ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadAsStringAsync(ct), null) : (null, $"Rapporten kunne ikke hentes (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return (null, "Rapporten kunne ikke hentes."); }
    }

    private static async Task<JsonElement?> Body(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException) { return null; }
    }

    private static string? Message(JsonElement? body) =>
        body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

    private static async Task<(T? Value, string? Error)> Send<T>(Func<Task<HttpResponseMessage>> call, CancellationToken ct)
    {
        try
        {
            using var response = await call();
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
            if ((int)response.StatusCode is 401 or 403) return (default, "Du har ikke tilgang.");
            return (default, Message(await Body(response, ct)) ?? $"Forespørselen feilet (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return (default, "Innstillingene for spesifikasjonsformat kunne ikke nås.");
        }
    }
}
