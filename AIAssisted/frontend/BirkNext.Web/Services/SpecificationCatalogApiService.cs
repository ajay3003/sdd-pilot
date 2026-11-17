using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Specifications;

namespace BirkNext.Web.Services;

/// <summary>The server-held requirement catalog of an imported project (captured at Project Import) and the check of recorded requirement links
/// against it. Read-only: a check never changes a link.</summary>
public interface ISpecificationCatalogApiService
{
    Task<(SpecificationCatalogView? Catalog, string? Error)> CurrentAsync(string projectKey, CancellationToken ct = default);
    Task<(RequirementReferenceCheckResult? Result, string? Error)> CheckAsync(string projectKey, IReadOnlyList<RequirementReference> references, CancellationToken ct = default);
}

public sealed class SpecificationCatalogApiService(HttpClient http) : ISpecificationCatalogApiService
{
    private const string Base = "api/specification-catalog";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(SpecificationCatalogView?, string?)> CurrentAsync(string projectKey, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync($"{Base}/{Uri.EscapeDataString(projectKey)}", ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, null); // no server catalog (legacy import): not an error
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<SpecificationCatalogView>(Json, ct), null) : (null, $"The specification catalog could not be read (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return (null, "The specification catalog could not be read: the backend could not be reached."); }
    }

    public async Task<(RequirementReferenceCheckResult?, string?)> CheckAsync(string projectKey, IReadOnlyList<RequirementReference> references, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync($"{Base}/{Uri.EscapeDataString(projectKey)}/check", new RequirementReferenceCheckRequest([.. references]), Json, ct);
            return response.IsSuccessStatusCode ? (await response.Content.ReadFromJsonAsync<RequirementReferenceCheckResult>(Json, ct), null) : (null, $"Requirement links could not be checked (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { return (null, "Requirement links could not be checked: the backend could not be reached."); }
    }
}
