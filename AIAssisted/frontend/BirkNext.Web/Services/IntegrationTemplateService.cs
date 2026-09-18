using System.Net.Http.Json;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Fetches the known M2LB integration templates, resolved for one environment.
///
/// The catalogue lives in the backend so there is one copy of it; this client never holds a second
/// one. Every template comes back whatever the environment is — the catalogue is reusable knowledge,
/// not per-environment inventory. What varies is the binding: a template with no evidenced values
/// for this environment arrives with its structural fields listed as missing, which is a normal
/// state the user resolves by supplying them, not an empty catalogue.
/// </summary>
public interface IIntegrationTemplateService
{
    /// <param name="environmentType">
    /// The normalised environment type ("Development", "QA", "Production") — never a profile display
    /// name, which would not match any binding.
    /// </param>
    Task<List<KnownIntegrationTemplateView>> GetForEnvironmentAsync(string? environmentType);
}

public sealed class IntegrationTemplateService(HttpClient http) : IIntegrationTemplateService
{
    public async Task<List<KnownIntegrationTemplateView>> GetForEnvironmentAsync(string? environmentType)
    {
        var templates = await http.GetFromJsonAsync<List<KnownIntegrationTemplateView>>(
            $"api/integration-quality/known-templates?environmentType={Uri.EscapeDataString(environmentType ?? "")}");

        return templates ?? [];
    }
}
