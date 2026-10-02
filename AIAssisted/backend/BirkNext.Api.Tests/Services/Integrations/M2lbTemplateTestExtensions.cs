using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>The M2LB DEV records exist only after the template is applied explicitly; tests that need them apply it first, as a person would.</summary>
internal static class M2lbTemplateTestExtensions
{
    public static async Task<IntegrationCatalog> GetWithM2lbTemplateAsync(this IIntegrationCatalogService service, string environmentId, string? environmentType, string? targetUrl)
    {
        await service.ApplyTemplateAsync(environmentId, M2lbDevIntegrationSeed.Name);
        return await service.GetAsync(environmentId, environmentType, targetUrl);
    }
}
