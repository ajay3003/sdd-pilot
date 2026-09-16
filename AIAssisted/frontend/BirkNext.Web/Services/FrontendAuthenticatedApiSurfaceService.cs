using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Web.Services;

/// <summary>
/// Frontend client for the Frontend Quality Review's authenticated API-surface probes. Sends the non-secret review identity and the
/// environment's configured endpoint URLs; receives sanitized results only. Any transport failure is reported as "not executed",
/// never as a public fallback.
/// </summary>
public interface IFrontendAuthenticatedApiSurfaceService
{
    Task<FrontendAuthenticatedApiSurfaceResult> ProbeAsync(FrontendAuthenticatedApiSurfaceRequest request, CancellationToken cancellationToken = default);
}

public sealed class FrontendAuthenticatedApiSurfaceService(HttpClient http) : IFrontendAuthenticatedApiSurfaceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public const string BackendUnavailableReason = "Authenticated API surface not checked: the BirkNext backend could not be reached.";

    public async Task<FrontendAuthenticatedApiSurfaceResult> ProbeAsync(FrontendAuthenticatedApiSurfaceRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/frontend-quality/authenticated-api-surface", request, JsonOptions, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return NotExecuted(request, $"Authenticated API surface not checked: the BirkNext backend returned HTTP {(int)response.StatusCode}.");
            return await response.Content.ReadFromJsonAsync<FrontendAuthenticatedApiSurfaceResult>(JsonOptions, cancellationToken)
                   ?? NotExecuted(request, BackendUnavailableReason);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return NotExecuted(request, BackendUnavailableReason);
        }
    }

    private static FrontendAuthenticatedApiSurfaceResult NotExecuted(FrontendAuthenticatedApiSurfaceRequest request, string reason) => new()
    {
        Capabilities = new AuthenticatedReviewCapabilities { Method = request.Identity.Method, Reason = reason },
        ContextAvailable = false,
        NotExecutedReason = reason,
    };
}
