using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Target Environment → External Integrations → Altinn. Reading and detecting never write; saving and session values are explicit actions. A secret or identifier is
/// sent once in a POST body and never comes back: the browser never stores it (no localStorage or sessionStorage) and no response contains it.
/// </summary>
public interface IAltinnEnvironmentApiService
{
    Task<ActiveEventApiResult<AltinnEnvironmentView>> GetAsync(string environmentId, CancellationToken ct = default);
    Task<ActiveEventApiResult<AltinnEnvironmentView>> DetectAsync(string environmentId, CancellationToken ct = default);
    Task<ActiveEventApiResult<AltinnEnvironmentView>> SaveAsync(string environmentId, AltinnEnvironmentSettingsUpdate update, CancellationToken ct = default);
    Task<ActiveEventApiResult<AltinnEnvironmentView>> RecordObservationAsync(string environmentId, AltinnRuntimeObservationRequest request, CancellationToken ct = default);
    Task<ActiveEventApiResult<AltinnEnvironmentView>> RecordBrowserObservationAsync(string environmentId, AltinnBrowserObservationRequest request, CancellationToken ct = default);
    Task<ActiveEventApiResult<bool>> AddSessionSecretAsync(string environmentId, string secret, CancellationToken ct = default);
    Task<ActiveEventApiResult<bool>> ClearSessionAsync(string environmentId, CancellationToken ct = default);
    Task<ActiveEventApiResult<bool>> AddSessionIdentityAsync(AltinnSessionIdentityRequest request, CancellationToken ct = default);
    Task<ActiveEventApiResult<bool>> ClearSessionIdentitiesAsync(CancellationToken ct = default);
}

public sealed class AltinnEnvironmentApiService(HttpClient http) : IAltinnEnvironmentApiService
{
    private const string Base = "api/target-environments/altinn";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string E(string value) => Uri.EscapeDataString(value);

    public Task<ActiveEventApiResult<AltinnEnvironmentView>> GetAsync(string environmentId, CancellationToken ct = default) =>
        SendAsync<AltinnEnvironmentView>(HttpMethod.Get, $"{Base}?environmentId={E(environmentId)}", null, ct);

    public Task<ActiveEventApiResult<AltinnEnvironmentView>> DetectAsync(string environmentId, CancellationToken ct = default) =>
        SendAsync<AltinnEnvironmentView>(HttpMethod.Post, $"{Base}/detect?environmentId={E(environmentId)}", null, ct);

    public Task<ActiveEventApiResult<AltinnEnvironmentView>> SaveAsync(string environmentId, AltinnEnvironmentSettingsUpdate update, CancellationToken ct = default) =>
        SendAsync<AltinnEnvironmentView>(HttpMethod.Put, $"{Base}/settings?environmentId={E(environmentId)}", JsonContent.Create(update, options: Json), ct,
            environment: environmentId);

    public Task<ActiveEventApiResult<AltinnEnvironmentView>> RecordObservationAsync(string environmentId, AltinnRuntimeObservationRequest request, CancellationToken ct = default) =>
        SendAsync<AltinnEnvironmentView>(HttpMethod.Post, $"{Base}/observations?environmentId={E(environmentId)}", JsonContent.Create(request, options: Json), ct,
            environment: environmentId);

    public Task<ActiveEventApiResult<AltinnEnvironmentView>> RecordBrowserObservationAsync(string environmentId, AltinnBrowserObservationRequest request, CancellationToken ct = default) =>
        SendAsync<AltinnEnvironmentView>(HttpMethod.Post, $"{Base}/browser-observation?environmentId={E(environmentId)}", JsonContent.Create(request, options: Json), ct,
            environment: environmentId);

    public Task<ActiveEventApiResult<bool>> AddSessionSecretAsync(string environmentId, string secret, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Post, $"{Base}/session/secret?environmentId={E(environmentId)}", JsonContent.Create(new AltinnSessionSecretRequest(secret), options: Json), ct);

    public Task<ActiveEventApiResult<bool>> ClearSessionAsync(string environmentId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"{Base}/session?environmentId={E(environmentId)}", null, ct);

    public Task<ActiveEventApiResult<bool>> AddSessionIdentityAsync(AltinnSessionIdentityRequest request, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Post, $"{Base}/session/identities", JsonContent.Create(request, options: Json), ct);

    public Task<ActiveEventApiResult<bool>> ClearSessionIdentitiesAsync(CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"{Base}/session/identities", null, ct);

    private async Task<ActiveEventApiResult<bool>> SendNoContentAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        var result = await SendAsync<JsonElement?>(method, url, content, ct, expectBody: false);
        return result.Error is null ? new(true, null, result.Status) : new(false, result.Error, result.Status);
    }

    /// <param name="environment">Declared only by operations that attach backend-owned state to that Target Environment.</param>
    private async Task<ActiveEventApiResult<T>> SendAsync<T>(HttpMethod method, string url, HttpContent? content, CancellationToken ct, bool expectBody = true, string? environment = null)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url) { Content = content }.RequiresPersistentEnvironment(environment);
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return new(expectBody ? await response.Content.ReadFromJsonAsync<T>(Json, ct) : default, null, response.StatusCode);
            string? message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Sign-in required: this needs an authenticated BirkNext API token.",
                HttpStatusCode.Forbidden => "Not authorized: your account does not hold the permission this action needs.",
                _ => null,
            };
            if (message is null)
            {
                try { message = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("message").GetString(); }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or NotSupportedException) { }
            }
            return new(default, message ?? $"The backend refused the request (HTTP {(int)response.StatusCode}).", response.StatusCode);
        }
        catch (TargetEnvironmentNotSavedException ex) { return new(default, ex.Message, null); }
        catch (HttpRequestException)
        {
            // Never the request content: an exception detail must not carry a secret or identifier back into the UI.
            return new(default, "The BirkNext backend could not be reached.", null);
        }
    }
}
