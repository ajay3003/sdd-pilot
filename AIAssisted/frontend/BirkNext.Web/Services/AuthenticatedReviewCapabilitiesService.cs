using System.Net.Http.Json;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Web.Services;

/// <summary>Resolves the backend authenticated-review capability matrix for a review page's auth-status header. Returns non-secret flags only.</summary>
public interface IAuthenticatedReviewCapabilitiesService
{
    Task<AuthenticatedReviewCapabilities> ResolveAsync(AuthenticatedReviewIdentity identity, CancellationToken ct = default);
}

public sealed class AuthenticatedReviewCapabilitiesService(HttpClient http) : IAuthenticatedReviewCapabilitiesService
{
    public async Task<AuthenticatedReviewCapabilities> ResolveAsync(AuthenticatedReviewIdentity identity, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/authenticated-review/capabilities", identity, ct);
            if (!response.IsSuccessStatusCode)
                return Fallback(identity, BackendRequestClassifier.FromStatus("Authenticated capability status", response.StatusCode, "POST api/authenticated-review/capabilities").UserMessage);
            return (await response.Content.ReadFromJsonAsync<AuthenticatedReviewCapabilities>(cancellationToken: ct)) ?? Fallback(identity);
        }
        catch (Exception ex) { return Fallback(identity, BackendRequestClassifier.FromException("Authenticated capability status", ex, "POST api/authenticated-review/capabilities", ct).UserMessage); }
    }

    private static AuthenticatedReviewCapabilities Fallback(AuthenticatedReviewIdentity identity, string? reason = null) =>
        new() { Method = identity.Method, PublicApi = true, Reason = reason ?? "Authenticated capability status is unavailable." };
}
