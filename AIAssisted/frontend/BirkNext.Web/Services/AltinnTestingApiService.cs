using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>Altinn app testing: the app contract (the app's source in Source Analysis / public TT02 metadata) and the tester's ID-porten TEST sign-in.
/// The browser never sees a token: sign-in returns only the ID-porten TEST address the backend built.</summary>
public interface IAltinnTestingApiService
{
    Task<ActiveEventApiResult<AltinnAppContractView>> ContractAsync(string environmentId, CancellationToken ct = default);
    Task<ActiveEventApiResult<AltinnAppContractView>> ReadPublicMetadataAsync(string environmentId, CancellationToken ct = default);
    /// <summary>Skolenærvær's read: the backend first saves the Skolenærvær default environment and app to the IQR Altinn test configuration where
    /// none is configured (never replacing a value), then reads the public TT02 metadata anonymously.</summary>
    Task<ActiveEventApiResult<AltinnAppContractView>> ReadSkoleNaervaerPublicMetadataAsync(string environmentId, CancellationToken ct = default) =>
        ReadPublicMetadataAsync(environmentId, ct);
    Task<ActiveEventApiResult<AltinnAppContractView>> UseSourceAnalysisAsync(string environmentId, Guid? snapshotId, CancellationToken ct = default);
    Task<ActiveEventApiResult<AltinnSignInStatus>> SignInStatusAsync(string environmentId, CancellationToken ct = default);
    Task<ActiveEventApiResult<AltinnSignInStart>> StartSignInAsync(string environmentId, CancellationToken ct = default);
    Task<ActiveEventApiResult<bool>> SignOutAsync(string environmentId, CancellationToken ct = default);
    /// <summary>Where each setting comes from (configured, discovered, policy evidence, run input). No secret or identifier is returned.</summary>
    Task<ActiveEventApiResult<AltinnConfigurationOverview>> ConfigurationAsync(string environmentId, CancellationToken ct = default) =>
        Task.FromResult(new ActiveEventApiResult<AltinnConfigurationOverview>(null, "The configuration overview is not available.", HttpStatusCode.NotFound));
    /// <summary>Confirms the latest live deployed contract after its changes were reviewed.</summary>
    Task<ActiveEventApiResult<AltinnAppContractView>> AcceptContractChangesAsync(string environmentId, CancellationToken ct = default) =>
        Task.FromResult(new ActiveEventApiResult<AltinnAppContractView>(null, "Not available.", HttpStatusCode.NotFound));
    /// <summary>Imports an OpenAPI document for offline analysis (never live-verified, never executed against).</summary>
    Task<ActiveEventApiResult<AltinnAppContractView>> ImportOpenApiAsync(string environmentId, string json, CancellationToken ct = default) =>
        Task.FromResult(new ActiveEventApiResult<AltinnAppContractView>(null, "Not available.", HttpStatusCode.NotFound));
}

public sealed class AltinnTestingApiService(HttpClient http) : IAltinnTestingApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string E(string value) => Uri.EscapeDataString(value);

    public Task<ActiveEventApiResult<AltinnAppContractView>> ContractAsync(string environmentId, CancellationToken ct = default) =>
        SendAsync<AltinnAppContractView>(HttpMethod.Get, $"api/altinn-testing/contract?environmentId={E(environmentId)}", null, ct);

    public Task<ActiveEventApiResult<AltinnAppContractView>> ReadPublicMetadataAsync(string environmentId, CancellationToken ct = default) =>
        SendAsync<AltinnAppContractView>(HttpMethod.Post, $"api/altinn-testing/contract/public-metadata?environmentId={E(environmentId)}", null, ct);

    public Task<ActiveEventApiResult<AltinnAppContractView>> ReadSkoleNaervaerPublicMetadataAsync(string environmentId, CancellationToken ct = default) =>
        SendAsync<AltinnAppContractView>(HttpMethod.Post, $"api/altinn-testing/contract/public-metadata?environmentId={E(environmentId)}&skoleNaervaerDefaults=true", null, ct);

    public Task<ActiveEventApiResult<AltinnAppContractView>> UseSourceAnalysisAsync(string environmentId, Guid? snapshotId, CancellationToken ct = default) =>
        SendAsync<AltinnAppContractView>(HttpMethod.Post,
            $"api/altinn-testing/contract/source-analysis?environmentId={E(environmentId)}" + (snapshotId is { } id ? $"&snapshotId={id}" : ""), null, ct);

    public Task<ActiveEventApiResult<AltinnSignInStatus>> SignInStatusAsync(string environmentId, CancellationToken ct = default) =>
        SendAsync<AltinnSignInStatus>(HttpMethod.Get, $"api/altinn-testing/sign-in/status?environmentId={E(environmentId)}", null, ct);

    public Task<ActiveEventApiResult<AltinnSignInStart>> StartSignInAsync(string environmentId, CancellationToken ct = default) =>
        SendAsync<AltinnSignInStart>(HttpMethod.Post, $"api/altinn-testing/sign-in/start?environmentId={E(environmentId)}", null, ct);

    public Task<ActiveEventApiResult<AltinnConfigurationOverview>> ConfigurationAsync(string environmentId, CancellationToken ct = default) =>
        SendAsync<AltinnConfigurationOverview>(HttpMethod.Get, $"api/altinn-testing/configuration?environmentId={E(environmentId)}", null, ct);

    public Task<ActiveEventApiResult<AltinnAppContractView>> AcceptContractChangesAsync(string environmentId, CancellationToken ct = default) =>
        SendAsync<AltinnAppContractView>(HttpMethod.Post, $"api/altinn-testing/contract/accept-changes?environmentId={E(environmentId)}", null, ct);

    public Task<ActiveEventApiResult<AltinnAppContractView>> ImportOpenApiAsync(string environmentId, string json, CancellationToken ct = default) =>
        SendAsync<AltinnAppContractView>(HttpMethod.Post, $"api/altinn-testing/contract/import-openapi?environmentId={E(environmentId)}",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"), ct);

    public async Task<ActiveEventApiResult<bool>> SignOutAsync(string environmentId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/altinn-testing/sign-in/sign-out?environmentId={E(environmentId)}");
        using var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode ? new(true, null, response.StatusCode) : new(false, Message(response.StatusCode) ?? "Sign-out failed.", response.StatusCode);
    }

    private async Task<ActiveEventApiResult<T>> SendAsync<T>(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return new(await response.Content.ReadFromJsonAsync<T>(Json, ct), null, response.StatusCode);
        var message = Message(response.StatusCode);
        if (message is null)
        {
            try { message = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("message").GetString(); }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or NotSupportedException) { }
        }
        return new(default, message ?? $"The backend refused the request (HTTP {(int)response.StatusCode}).", response.StatusCode);
    }

    private static string? Message(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "Sign-in required: this needs an authenticated BirkNext API token.",
        HttpStatusCode.Forbidden => "Not authorized: your account does not hold the permission this action needs.",
        _ => null,
    };
}
