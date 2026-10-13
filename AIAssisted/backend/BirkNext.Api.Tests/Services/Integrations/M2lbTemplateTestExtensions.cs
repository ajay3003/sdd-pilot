using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>The M2LB DEV records exist only after the helper is applied explicitly; tests that need them apply it first, as a person would.</summary>
internal static class M2lbTemplateTestExtensions
{
    private const string DevType = "Development";
    private static readonly string DevUrl = "https://" + M2lbDevIntegrationSeed.FrontendHost + "/";

    /// <summary>Only what the M2LB DEV helper itself adds (v5: three platforms and the confirmed Person integration).</summary>
    public static async Task<IntegrationCatalog> GetWithM2lbHelperAsync(this IIntegrationCatalogService service, string environmentId, string? environmentType, string? targetUrl)
    {
        await service.ApplyTemplateAsync(environmentId, M2lbDevIntegrationSeed.Name, DevType, DevUrl);
        return await service.GetAsync(environmentId, environmentType, targetUrl);
    }

    /// <summary>
    /// A realistic M2LB DEV catalog for review tests: the helper, plus the other fifteen BIRK CDC integrations of
    /// <see cref="M2lbDevHistoricalFixture"/> added as ordinary integrations (in the product they come from Source Analysis suggestions).
    /// </summary>
    public static async Task<IntegrationCatalog> GetWithM2lbTemplateAsync(this IIntegrationCatalogService service, string environmentId, string? environmentType, string? targetUrl)
    {
        await service.ApplyTemplateAsync(environmentId, M2lbDevIntegrationSeed.Name, DevType, DevUrl);
        var existing = (await service.GetAsync(environmentId, null, null)).Integrations.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var definition in M2lbDevHistoricalFixture.Integrations(environmentId, DateTimeOffset.UtcNow).Where(d => !existing.Contains(d.Id)))
            await service.CreateAsync(environmentId, definition);
        return await service.GetAsync(environmentId, environmentType, targetUrl);
    }
}

/// <summary>
/// TEST FIXTURE ONLY: the sixteen BIRK CDC integrations the M2LB DEV seed created up to v4 (one confirmed mapping, eight consumers suggested by
/// an earlier audit, seven needing confirmation). The product no longer seeds the fifteen discoverable ones — Source Analysis suggests them —
/// but review tests keep this realistic, many-hub shape.
/// </summary>
internal static class M2lbDevHistoricalFixture
{
    public static readonly string[] Tables =
    [
        "Person", "Tiltak", "Bestilling", "TjenesteType", "TiltaksStatusType", "AvslutningsGrunnType", "Barn", "BarnStatusType",
        "BarnType", "Kommune", "KjønnType", "TvangsProtokoll", "HjemmelType", "TvangsProtokollStatusType", "Romning", "RomningKategoriType",
    ];

    private static readonly Dictionary<string, string> SuggestedConsumers = new(StringComparer.Ordinal)
    {
        ["Barn"] = "PersonBiRKAdapter", ["Tiltak"] = "Tjeneste API", ["Bestilling"] = "Tjeneste API", ["TjenesteType"] = "Tjeneste API",
        ["TiltaksStatusType"] = "Tjeneste API", ["AvslutningsGrunnType"] = "Tjeneste API", ["TvangsProtokoll"] = "Hendelse BiRK Adapter", ["Romning"] = "Hendelse BiRK Adapter",
    };

    public static IReadOnlyList<IntegrationDefinition> Integrations(string environmentId, DateTimeOffset now) =>
        Tables.Select(table => table == M2lbDevIntegrationSeed.ConfirmedTable
            ? M2lbDevIntegrationSeed.Integrations(environmentId, now).Single()
            : new IntegrationDefinition
            {
                Id = M2lbDevIntegrationSeed.IntegrationId(table), EnvironmentId = environmentId, PlatformId = M2lbDevIntegrationSeed.PlatformId, DisplayName = $"BIRK {table} CDC",
                Kind = IntegrationKind.EventHub, Enabled = true, SystemName = M2lbDevIntegrationSeed.SystemName,
                SourceSystem = "BIRK SQL Server", SourceResource = $"{M2lbDevIntegrationSeed.SourceDatabase}.dbo.{table}", DestinationSystem = "M2LB",
                EndpointOrTopic = M2lbDevIntegrationSeed.Topic(table), ConsumerGroup = null,
                Producer = "Debezium SQL Server CDC", ProducerAuthentication = IntegrationAuthMechanism.Sas, ConsumerAuthentication = IntegrationAuthMechanism.ManagedIdentity,
                Consumer = SuggestedConsumers.TryGetValue(table, out var service)
                    ? new IntegrationConsumer { DisplayName = service, MappingState = ConsumerMappingState.Suggested, MappingSource = "Audited M2LB source (QA-context audit)" }
                    : new IntegrationConsumer { MappingState = ConsumerMappingState.NeedsConfirmation },
                ContractRelationship = ContractRelationshipState.NotConfigured, PartitionCount = 1, RetentionDays = 7, UpdatedAt = now,
            }).ToList();
}
