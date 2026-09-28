using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations;

/// <summary>
/// M2LB DEV SCIM identity provisioning platform (Microsoft Entra ID → SCIM adapter → KjentBruker → Service Bus entra.brukere → Autorisasjon).
/// Configured values come from a developer-side audit (2026-09-28) of the adapter pipeline and the platform Terraform:
/// <list type="bullet">
/// <item>Container app, resource group, image: <c>Autorisasjon/.pipeline/autorisasjon-scim-adapter-dev.yml</c> (DOTNET_ENVIRONMENT=Development).</item>
/// <item>Identity, ingress, Service Bus and caller settings: <c>terraform/layers/04-apps/ca_scim_adapter.tf</c> — ServiceBus__FQDN is the namespace
/// FQDN, sender scope entra.brukere, AzureAd__ObjectId = the Microsoft.Azure.SyncFabric service principal, external ingress behind the hub
/// application gateway; no HTTP health probe path is configured.</item>
/// </list>
/// The public tenant URL Entra calls is NOT in either source (the hub gateway is outside the audited repositories), so the base URL stays
/// Unknown and the safe runtime checks are Not configured until someone enters it. BirkNext never reads Terraform at runtime.
/// </summary>
public static partial class M2lbDevIntegrationSeed
{
    public const string ScimPlatformId = "dev:scim:m2lb";

    public static IntegrationPlatform ScimPlatform(string environmentId, DateTimeOffset now) => new()
    {
        Id = ScimPlatformId, EnvironmentId = environmentId, Name = "M2LB Entra SCIM Provisioning", Kind = IntegrationKind.IdentityProvisioning, Enabled = true,
        Region = "nwe", ResourceGroup = "rg-m2lb-dev-apps-nwe", TechnicalOwner = "platform-team", MonitoringProvider = "Application Insights",
        ProducerTechnology = "ASP.NET Core minimal API (M2LB.Autorisasjon.ScimAdapter)", ProducerAuthentication = IntegrationAuthMechanism.EntraIdClientCredentials,
        DefaultConsumerAuthentication = IntegrationAuthMechanism.ManagedIdentity, Origin = IntegrationRecordOrigin.Seed, UpdatedAt = now,
        ScimProvisioning = new ScimProvisioningSettings
        {
            BaseUrl = null, BasePath = "/scim/v2", UsersResource = "/Users",
            Authentication = "Entra ID JWT from the provisioning service (Microsoft.Azure.SyncFabric): appid + service-principal oid claims",
            Persistence = "KjentBruker (AutorisasjonsDbContext, table KjentBrukere)", OutboundPlatformId = ServiceBusPlatformId, Topic = "entra.brukere",
            Downstream = "Autorisasjon", ContainerApp = "ca-m2lb-scim-adp-dev-nwe-001", ResourceGroup = "rg-m2lb-dev-apps-nwe", ManagedIdentity = "id-m2lb-scim-adp-dev-nwe",
            ConfigurationNotes =
            [
                "ServiceBus__FQDN is set to the platform Service Bus namespace FQDN (terraform/layers/04-apps/ca_scim_adapter.tf:43–47), so the Service Bus publisher — not the disabled fallback — is expected in DEV.",
                "AzureAd__ObjectId is the Microsoft.Azure.SyncFabric service principal of the tenant (ca_scim_adapter.tf:61–67).",
                "Ingress is external on the internal-LB environment; the hub application gateway is the only public entry and routes /scim/v2/* (ca_scim_adapter.tf:33–36). Its public URL is not in the audited source.",
                "No HTTP health probe path is configured for the container app (health_probe_path_prefix unset).",
                "The container app ignores later image/environment/probe changes in Terraform (lifecycle ignore_changes): deployed runtime values may differ.",
            ],
        },
    };
}
