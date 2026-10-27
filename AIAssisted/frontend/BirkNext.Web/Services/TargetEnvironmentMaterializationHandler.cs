using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

/// <summary>
/// Backend client operations that create or update environment-owned state (External Integrations, IQR configuration, Altinn settings,
/// setup applies) declare the Target Environment they attach to. Backend records are keyed by environment id alone, while never-saved
/// built-in defaults live only in memory, so such an operation must store those defaults first or a reload would orphan what it attached.
/// Reads, previews, checks and runs do not declare it and never store anything.
/// </summary>
public static class TargetEnvironmentPersistence
{
    public static readonly HttpRequestOptionsKey<string> RequiredEnvironment = new("BirkNext.RequiresPersistentEnvironment");

    /// <summary>Declares that this request attaches backend-owned state to <paramref name="environmentId"/>; null declares nothing.</summary>
    public static HttpRequestMessage RequiresPersistentEnvironment(this HttpRequestMessage request, string? environmentId)
    {
        if (!string.IsNullOrWhiteSpace(environmentId)) request.Options.Set(RequiredEnvironment, environmentId);
        return request;
    }

    public static async Task<HttpResponseMessage> SendForEnvironmentAsync(this HttpClient http, HttpMethod method, string url, HttpContent? content,
        string environmentId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content }.RequiresPersistentEnvironment(environmentId);
        return await http.SendAsync(request, ct);
    }

    public static Task<HttpResponseMessage> SendJsonForEnvironmentAsync<T>(this HttpClient http, HttpMethod method, string url, T value,
        System.Text.Json.JsonSerializerOptions options, string environmentId, CancellationToken ct) =>
        http.SendForEnvironmentAsync(method, url, System.Net.Http.Json.JsonContent.Create(value, options: options), environmentId, ct);
}

/// <summary>The never-saved Target Environment could not be stored, so the operation that would attach state to it was not sent.</summary>
public sealed class TargetEnvironmentNotSavedException(Exception inner)
    : HttpRequestException("The Target Environment could not be saved, so nothing was attached to it. Save the Target Environment and try again.", inner);

/// <summary>
/// Stores the never-saved built-in Target Environments before an operation declared with
/// <see cref="TargetEnvironmentPersistence.RequiresPersistentEnvironment"/> is sent. Seed ids stay random per browser rather than fixed: the
/// backend has no project/workspace scope that would keep shared ids from colliding.
/// </summary>
public sealed class TargetEnvironmentMaterializationHandler(IFrontendAnalysisSettingsService settings, IJSRuntime js) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!settings.IsMaterialized && request.Options.TryGetValue(TargetEnvironmentPersistence.RequiredEnvironment, out var environmentId))
        {
            // Fail closed: if storing fails the operation is not sent, so nothing is attached to an id that would vanish on reload.
            try { await settings.MaterializeAsync(js, environmentId); }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw new TargetEnvironmentNotSavedException(ex); }
        }
        return await base.SendAsync(request, cancellationToken);
    }
}
