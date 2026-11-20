using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>System Settings → Translation (api/translation-settings). Errors come back as a message to show; nothing is retried by itself.</summary>
public interface ITranslationSettingsApiService
{
    Task<(TranslationSettingsView? View, string? Error)> GetAsync(CancellationToken ct = default);
    Task<(TranslationSettingsView? View, string? Error)> SaveAsync(SaveTranslationSettingsRequest request, CancellationToken ct = default);
    Task<(LibreTranslateServiceStatus? Status, string? Error)> LibreTranslateAsync(CancellationToken ct = default);
    /// <summary>"pull", "start" or "stop" — returns the server's message.</summary>
    Task<string> LibreTranslateActionAsync(string action, CancellationToken ct = default);
    Task<(TranslationTestResult? Result, string? Error)> TestAsync(CancellationToken ct = default);
    /// <summary>The embedded offline translator: <paramref name="probe"/> starts it and translates the probe sentence; <paramref name="recheck"/> verifies the package again first.</summary>
    Task<(OfflineTranslationStatus? Status, string? Error)> OfflineAsync(bool probe, bool recheck = false, CancellationToken ct = default) => Task.FromResult<(OfflineTranslationStatus?, string?)>((null, null));
}

public sealed class TranslationSettingsApiService(HttpClient http) : ITranslationSettingsApiService
{
    private const string Base = "api/translation-settings";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Task<(TranslationSettingsView?, string?)> GetAsync(CancellationToken ct = default) => Send<TranslationSettingsView>(() => http.GetAsync(Base, ct), ct);
    public Task<(TranslationSettingsView?, string?)> SaveAsync(SaveTranslationSettingsRequest request, CancellationToken ct = default) =>
        Send<TranslationSettingsView>(() => http.PutAsJsonAsync(Base, request, Json, ct), ct);
    public Task<(LibreTranslateServiceStatus?, string?)> LibreTranslateAsync(CancellationToken ct = default) =>
        Send<LibreTranslateServiceStatus>(() => http.GetAsync($"{Base}/libretranslate", ct), ct);
    public async Task<string> LibreTranslateActionAsync(string action, CancellationToken ct = default)
    {
        if (action is not ("pull" or "start" or "stop")) return "Unknown action.";
        var (result, error) = await Send<JsonElement>(() => http.PostAsync($"{Base}/libretranslate/{action}", null, ct), ct);
        return error ?? (result.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");
    }
    public Task<(TranslationTestResult?, string?)> TestAsync(CancellationToken ct = default) => Send<TranslationTestResult>(() => http.PostAsync($"{Base}/test", null, ct), ct);
    public Task<(OfflineTranslationStatus?, string?)> OfflineAsync(bool probe, bool recheck = false, CancellationToken ct = default) =>
        Send<OfflineTranslationStatus>(() => http.GetAsync($"{Base}/offline?probe={(probe ? "true" : "false")}&recheck={(recheck ? "true" : "false")}", ct), ct);

    private static async Task<(T? Value, string? Error)> Send<T>(Func<Task<HttpResponseMessage>> call, CancellationToken ct)
    {
        try
        {
            using var response = await call();
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
            if ((int)response.StatusCode is 401 or 403) return (default, "You do not have permission to change system settings.");
            string? message = null;
            try { message = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("message").GetString(); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
            return (default, message ?? $"The request failed (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            return (default, "The translation settings could not be reached.");
        }
    }
}
