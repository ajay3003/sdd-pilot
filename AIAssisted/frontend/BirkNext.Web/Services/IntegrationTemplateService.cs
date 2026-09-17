using System.Net.Http.Json;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Fetches the integration templates evidenced for an environment.
///
/// The catalogue lives in the backend so there is one copy of it. An environment with no
/// evidenced templates returns an empty list, which is a normal outcome rather than an error:
/// only environments whose values were actually established have templates.
/// </summary>
public interface IIntegrationTemplateService
{
    Task<List<KnownIntegrationTemplate>> GetForEnvironmentAsync(string? environmentName);
}

public sealed class IntegrationTemplateService(HttpClient http) : IIntegrationTemplateService
{
    public async Task<List<KnownIntegrationTemplate>> GetForEnvironmentAsync(string? environmentName)
    {
        if (string.IsNullOrWhiteSpace(environmentName))
            return [];

        var templates = await http.GetFromJsonAsync<List<KnownIntegrationTemplate>>(
            $"api/integration-quality/known-templates?environmentName={Uri.EscapeDataString(environmentName)}");

        return templates ?? [];
    }
}
