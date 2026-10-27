using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

/// <summary>
/// Materializes never-saved built-in Target Environments the first time a backend write references one of their ids. Backend records
/// (External Integrations, IQR bindings and reviews, Altinn settings, setup applies) are keyed by environment id alone, while the seed
/// profiles live only in memory until stored, so without this a reload would mint new ids and orphan what was attached. Seed ids stay
/// random per browser rather than fixed: the backend has no project/workspace scope that would keep shared ids from colliding.
/// </summary>
public sealed class TargetEnvironmentMaterializationHandler(IFrontendAnalysisSettingsService settings, IJSRuntime js) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Fail closed: if storing fails the exception surfaces and the write is not sent, so nothing is attached to an id that will vanish.
        if (settings.IsLoaded && !settings.IsMaterialized && !IsRead(request.Method) && await ReferencesProfileAsync(request, cancellationToken))
            await settings.MaterializeAsync(js);
        return await base.SendAsync(request, cancellationToken);
    }

    private static bool IsRead(HttpMethod method) => method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options;

    private async Task<bool> ReferencesProfileAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var ids = settings.Settings.Profiles.Select(profile => profile.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (ids.Count == 0) return false;
        var uri = request.RequestUri?.ToString() ?? "";
        if (ids.Any(id => uri.Contains(id, StringComparison.OrdinalIgnoreCase))) return true;
        // Bodies are read only when textual (JSON, text, form); binary uploads carry the environment in the URL.
        var mediaType = request.Content?.Headers.ContentType?.MediaType ?? "";
        if (request.Content is null || !(mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)))
            return false;
        var body = await request.Content.ReadAsStringAsync(ct);
        return ids.Any(id => body.Contains(id, StringComparison.OrdinalIgnoreCase));
    }
}
