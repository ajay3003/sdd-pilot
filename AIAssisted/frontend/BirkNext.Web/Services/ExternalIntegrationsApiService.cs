using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Target Environment → Connected Systems, the scope-aware setup import/export pipeline and the Setup Library. Reads, Detect, export and
/// preflight never write; create/update/delete, Apply and library saves are explicit. A secret is POSTed once and never returned or stored.
/// </summary>
public interface IExternalIntegrationsApiService
{
    Task<ActiveEventApiResult<ExternalIntegrationsView>> ListAsync(string environmentId, CancellationToken ct = default);
    Task<ActiveEventApiResult<ExternalIntegrationsView>> DetectAsync(string environmentId, CancellationToken ct = default);
    Task<ActiveEventApiResult<ExternalIntegrationView>> DetectOneAsync(string environmentId, string id, CancellationToken ct = default);
    Task<ActiveEventApiResult<ExternalIntegrationView>> SaveAsync(string environmentId, string? id, ExternalIntegrationRequest request, CancellationToken ct = default);
    Task<ActiveEventApiResult<bool>> DeleteAsync(string environmentId, string id, CancellationToken ct = default);
    Task<ActiveEventApiResult<bool>> AddSecretAsync(string environmentId, string id, string secret, CancellationToken ct = default);
    Task<ActiveEventApiResult<SetupPackageExport>> ExportAsync(string environmentId, SetupScope scope, ExternalProviderType? provider, string? integrationId, SetupPackageFormat format,
        IReadOnlyList<string>? testPacks = null, CancellationToken ct = default);
    Task<ActiveEventApiResult<SetupPreflightResult>> PreflightAsync(string environmentId, SetupPreflightRequest request, CancellationToken ct = default);
    Task<ActiveEventApiResult<SetupApplyPackageResult>> ApplyAsync(string environmentId, SetupApplyPackageRequest request, CancellationToken ct = default);

    Task<ActiveEventApiResult<IReadOnlyList<SetupLibraryItemView>>> LibraryAsync(ExternalProviderType? provider = null, SetupLibraryStatus? status = null, string? testPack = null,
        SetupScope? setupType = null, CancellationToken ct = default);
    Task<ActiveEventApiResult<SetupLibraryItemView>> SaveToLibraryAsync(SetupLibrarySaveRequest request, CancellationToken ct = default);
    Task<ActiveEventApiResult<SetupLibraryItemView>> NewLibraryVersionAsync(Guid id, SetupLibrarySaveRequest request, CancellationToken ct = default);
    Task<ActiveEventApiResult<SetupLibraryItemView>> DuplicateLibraryItemAsync(Guid id, CancellationToken ct = default);
    Task<ActiveEventApiResult<bool>> ArchiveLibraryItemAsync(Guid id, CancellationToken ct = default);
    Task<ActiveEventApiResult<SetupPackageExport>> ExportLibraryVersionAsync(Guid id, int? version, SetupPackageFormat format, CancellationToken ct = default);
    Task<ActiveEventApiResult<IReadOnlyList<SetupVersionDifference>>> CompareLibraryVersionsAsync(Guid id, int from, int to, CancellationToken ct = default);
    Task<ActiveEventApiResult<SetupPreflightResult>> PreflightLibraryVersionAsync(Guid id, int? version, string environmentId, CancellationToken ct = default);
    Task<ActiveEventApiResult<SetupApplyPackageResult>> ApplyLibraryVersionAsync(Guid id, int? version, string environmentId, Dictionary<string, bool>? decisions, CancellationToken ct = default);
}

public sealed class ExternalIntegrationsApiService(HttpClient http) : IExternalIntegrationsApiService
{
    private const string Base = "api/target-environments/external-integrations";
    private const string Library = "api/setup-library";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string E(string value) => Uri.EscapeDataString(value);
    private static HttpContent Body(object value) => JsonContent.Create(value, value.GetType(), options: Json);

    public Task<ActiveEventApiResult<ExternalIntegrationsView>> ListAsync(string environmentId, CancellationToken ct = default) =>
        Send<ExternalIntegrationsView>(HttpMethod.Get, $"{Base}?environmentId={E(environmentId)}", null, ct);
    public Task<ActiveEventApiResult<ExternalIntegrationsView>> DetectAsync(string environmentId, CancellationToken ct = default) =>
        Send<ExternalIntegrationsView>(HttpMethod.Post, $"{Base}/detect?environmentId={E(environmentId)}", null, ct);
    public Task<ActiveEventApiResult<ExternalIntegrationView>> DetectOneAsync(string environmentId, string id, CancellationToken ct = default) =>
        Send<ExternalIntegrationView>(HttpMethod.Post, $"{Base}/{E(id)}/detect?environmentId={E(environmentId)}", null, ct);
    public Task<ActiveEventApiResult<ExternalIntegrationView>> SaveAsync(string environmentId, string? id, ExternalIntegrationRequest request, CancellationToken ct = default) =>
        Send<ExternalIntegrationView>(id is null ? HttpMethod.Post : HttpMethod.Put, id is null ? $"{Base}?environmentId={E(environmentId)}" : $"{Base}/{E(id)}?environmentId={E(environmentId)}", Body(request), ct,
            environment: environmentId);
    public Task<ActiveEventApiResult<bool>> DeleteAsync(string environmentId, string id, CancellationToken ct = default) =>
        NoContent(HttpMethod.Delete, $"{Base}/{E(id)}?environmentId={E(environmentId)}", null, ct);
    public Task<ActiveEventApiResult<bool>> AddSecretAsync(string environmentId, string id, string secret, CancellationToken ct = default) =>
        NoContent(HttpMethod.Post, $"{Base}/{E(id)}/session-secret?environmentId={E(environmentId)}", Body(new AltinnSessionSecretRequest(secret)), ct);
    public Task<ActiveEventApiResult<SetupPackageExport>> ExportAsync(string environmentId, SetupScope scope, ExternalProviderType? provider, string? integrationId, SetupPackageFormat format,
        IReadOnlyList<string>? testPacks = null, CancellationToken ct = default) =>
        Send<SetupPackageExport>(HttpMethod.Get, $"{Base}/setup/export?environmentId={E(environmentId)}&scope={scope}&format={format}"
            + (provider is { } p ? $"&provider={p}" : "") + (integrationId is { } i ? $"&integrationId={E(i)}" : "") + string.Concat((testPacks ?? []).Select(t => $"&testPack={E(t)}")), null, ct);
    public Task<ActiveEventApiResult<SetupPreflightResult>> PreflightAsync(string environmentId, SetupPreflightRequest request, CancellationToken ct = default) =>
        Send<SetupPreflightResult>(HttpMethod.Post, $"{Base}/setup/preflight?environmentId={E(environmentId)}", Body(request), ct);
    public Task<ActiveEventApiResult<SetupApplyPackageResult>> ApplyAsync(string environmentId, SetupApplyPackageRequest request, CancellationToken ct = default) =>
        Send<SetupApplyPackageResult>(HttpMethod.Post, $"{Base}/setup/apply?environmentId={E(environmentId)}", Body(request), ct, environment: environmentId);

    public Task<ActiveEventApiResult<IReadOnlyList<SetupLibraryItemView>>> LibraryAsync(ExternalProviderType? provider = null, SetupLibraryStatus? status = null, string? testPack = null,
        SetupScope? setupType = null, CancellationToken ct = default) =>
        Send<IReadOnlyList<SetupLibraryItemView>>(HttpMethod.Get, $"{Library}?" + string.Join("&", new[]
        {
            provider is { } p ? $"provider={p}" : null, status is { } s ? $"status={s}" : null, testPack is { } t ? $"testPack={E(t)}" : null, setupType is { } st ? $"setupType={st}" : null,
        }.OfType<string>()), null, ct);
    public Task<ActiveEventApiResult<SetupLibraryItemView>> SaveToLibraryAsync(SetupLibrarySaveRequest request, CancellationToken ct = default) =>
        Send<SetupLibraryItemView>(HttpMethod.Post, Library, Body(request), ct);
    public Task<ActiveEventApiResult<SetupLibraryItemView>> NewLibraryVersionAsync(Guid id, SetupLibrarySaveRequest request, CancellationToken ct = default) =>
        Send<SetupLibraryItemView>(HttpMethod.Post, $"{Library}/{id}/versions", Body(request), ct);
    public Task<ActiveEventApiResult<SetupLibraryItemView>> DuplicateLibraryItemAsync(Guid id, CancellationToken ct = default) =>
        Send<SetupLibraryItemView>(HttpMethod.Post, $"{Library}/{id}/duplicate", null, ct);
    public Task<ActiveEventApiResult<bool>> ArchiveLibraryItemAsync(Guid id, CancellationToken ct = default) => NoContent(HttpMethod.Post, $"{Library}/{id}/archive", null, ct);
    public Task<ActiveEventApiResult<SetupPackageExport>> ExportLibraryVersionAsync(Guid id, int? version, SetupPackageFormat format, CancellationToken ct = default) =>
        Send<SetupPackageExport>(HttpMethod.Get, $"{Library}/{id}/export?format={format}" + (version is { } v ? $"&version={v}" : ""), null, ct);
    public Task<ActiveEventApiResult<IReadOnlyList<SetupVersionDifference>>> CompareLibraryVersionsAsync(Guid id, int from, int to, CancellationToken ct = default) =>
        Send<IReadOnlyList<SetupVersionDifference>>(HttpMethod.Get, $"{Library}/{id}/compare?from={from}&to={to}", null, ct);
    public Task<ActiveEventApiResult<SetupPreflightResult>> PreflightLibraryVersionAsync(Guid id, int? version, string environmentId, CancellationToken ct = default) =>
        Send<SetupPreflightResult>(HttpMethod.Post, $"{Library}/{id}/preflight?environmentId={E(environmentId)}" + (version is { } v ? $"&version={v}" : ""), null, ct);
    public Task<ActiveEventApiResult<SetupApplyPackageResult>> ApplyLibraryVersionAsync(Guid id, int? version, string environmentId, Dictionary<string, bool>? decisions, CancellationToken ct = default) =>
        Send<SetupApplyPackageResult>(HttpMethod.Post, $"{Library}/{id}/apply?environmentId={E(environmentId)}" + (version is { } v ? $"&version={v}" : ""), Body(decisions ?? []), ct,
            environment: environmentId);

    private async Task<ActiveEventApiResult<bool>> NoContent(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        var result = await Send<JsonElement?>(method, url, content, ct, body: false);
        return result.Error is null ? new(true, null, result.Status) : new(false, result.Error, result.Status);
    }

    /// <param name="environment">Declared only by operations that attach backend-owned state to that Target Environment.</param>
    private async Task<ActiveEventApiResult<T>> Send<T>(HttpMethod method, string url, HttpContent? content, CancellationToken ct, bool body = true, string? environment = null)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url) { Content = content }.RequiresPersistentEnvironment(environment);
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return new(body ? await response.Content.ReadFromJsonAsync<T>(Json, ct) : default, null, response.StatusCode);
            string? message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Sign-in required: this needs an authenticated BirkNext API token.",
                HttpStatusCode.Forbidden => "Not authorized: your account does not hold the permission this action needs.",
                HttpStatusCode.NotFound => "Not found.",
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
        catch (HttpRequestException) { return new(default, "The BirkNext backend could not be reached.", null); }
    }
}
