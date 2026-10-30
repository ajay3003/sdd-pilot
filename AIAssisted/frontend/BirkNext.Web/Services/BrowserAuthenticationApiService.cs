using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.BrowserAuthentication;

namespace BirkNext.Web.Services;

/// <summary>Unattended browser login settings (api/browser-authentication). The password is sent only when the user saves a new one and is
/// never returned. Attended browser login has no calls here.</summary>
public interface IBrowserAuthenticationApiService
{
    Task<(UnattendedLoginView? View, string? Error)> GetAsync(CancellationToken ct = default);
    Task<AutomatedBrowserExecutionReadiness?> AutomatedExecutionAsync(CancellationToken ct = default);
    Task<(UnattendedLoginView? View, string? Error)> SaveAsync(SaveUnattendedLoginRequest request, CancellationToken ct = default);
    Task<(UnattendedLoginView? View, string? Error)> SetEnabledAsync(bool enabled, CancellationToken ct = default);
    Task<(UnattendedLoginValidationResult? Result, string? Error)> ValidateAsync(CancellationToken ct = default);
    Task<string?> ClearAsync(CancellationToken ct = default);
}

public sealed class BrowserAuthenticationApiService(HttpClient http) : IBrowserAuthenticationApiService
{
    private const string Base = "api/browser-authentication";
    private const string Title = "Authentication";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(UnattendedLoginView? View, string? Error)> GetAsync(CancellationToken ct = default)
    {
        try { return (await http.GetFromJsonAsync<UnattendedLoginView>($"{Base}/unattended", Json, ct), null); }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(Title, e, $"GET {Base}/unattended", ct).UserMessage); }
    }

    public async Task<AutomatedBrowserExecutionReadiness?> AutomatedExecutionAsync(CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<AutomatedBrowserExecutionReadiness>($"{Base}/automated-execution", Json, ct); }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return null; }
    }

    public Task<(UnattendedLoginView? View, string? Error)> SaveAsync(SaveUnattendedLoginRequest request, CancellationToken ct = default) =>
        SendAsync<UnattendedLoginView>(HttpMethod.Put, $"{Base}/unattended", request, ct);

    public Task<(UnattendedLoginView? View, string? Error)> SetEnabledAsync(bool enabled, CancellationToken ct = default) =>
        SendAsync<UnattendedLoginView>(HttpMethod.Put, $"{Base}/unattended/enabled", new SetUnattendedLoginEnabledRequest(enabled), ct);

    public Task<(UnattendedLoginValidationResult? Result, string? Error)> ValidateAsync(CancellationToken ct = default) =>
        SendAsync<UnattendedLoginValidationResult>(HttpMethod.Post, $"{Base}/unattended/validate", null, ct);

    public async Task<string?> ClearAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await http.DeleteAsync($"{Base}/unattended", ct);
            return response.IsSuccessStatusCode ? null : await MessageAsync(response, ct);
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return BackendRequestClassifier.FromException(Title, e, $"DELETE {Base}/unattended", ct).UserMessage; }
    }

    private async Task<(T?, string?)> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct) where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body, options: Json) };
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<T>(Json, ct), null) : (null, await MessageAsync(response, ct));
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e))
        { return (null, BackendRequestClassifier.FromException(Title, e, $"{method} {url}", ct).UserMessage); }
    }

    private static async Task<string> MessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            return "You are not allowed to change authentication settings on this backend.";
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
