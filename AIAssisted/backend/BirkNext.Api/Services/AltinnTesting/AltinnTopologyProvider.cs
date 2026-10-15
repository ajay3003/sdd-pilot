using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.IntegrationTopology;
using BirkNext.IntegrationTopology;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>
/// The Altinn app the environment tests against, as an external-application interface of the "Altinn" component: the app id and form data
/// type from the Integration Quality Review Altinn configuration, its host from the central Altinn endpoint resolution, Discovered once its
/// public metadata has been read. Nothing is fetched here.
/// </summary>
public sealed class AltinnTopologyProvider(IIntegrationMessageFlowStore messageFlows, AltinnTestingPolicy policy) : ITopologyProvider
{
    public const string ComponentName = "Altinn";
    public int Order => 25;

    public async Task ContributeAsync(TopologyContext context, TopologyBuilder builder, CancellationToken ct)
    {
        var flow = await messageFlows.GetAsync(context.EnvironmentId, ct);
        var config = flow.AltinnConfiguration;
        if (policy.ApprovedApp(config.AppIdentifier, out var reason) is not { } app)
        {
            builder.Source(new(TopologySource.ExternalApplicationMetadata, TopologySourceState.NotAvailable, reason));
            return;
        }
        string? host = null;
        try { host = policy.EndpointsFor(config.Environment, out _)?.AppBase(app.Org, app.App).Host; } catch (AltinnContractException) { }
        var live = flow.AppContract?.PublicMetadata;
        var provenance = live is not null
            ? new TopologyProvenance(TopologySource.ExternalApplicationMetadata, config.AppIdentifier!, $"Public metadata read (form data type {live.FormDataTypeId ?? "unknown"}).", live.CapturedAt)
            : new TopologyProvenance(TopologySource.IntegrationCatalog, config.AppIdentifier!, "Configured in Integration Quality Review → Altinn test configuration; metadata not read.");
        var level = live is not null ? TopologyStatus.Discovered : TopologyStatus.Configured;
        var component = builder.Component(ComponentName, level, provenance, "ExternalSystem", host is null ? null : $"https://{host}/");
        builder.Interface(component, TopologyInterfaceType.ExternalApplication, "Altinn App API", TopologyDirection.Provides, config.AppIdentifier!, live?.FormDataTypeId,
            level, provenance, live?.FormDataTypeId is { } dataType ? AltinnContractEvolution.ContractId(config.AppIdentifier!, dataType) : null);
        builder.Source(new(TopologySource.ExternalApplicationMetadata, live is null ? TopologySourceState.NotAvailable : TopologySourceState.Used,
            live is null ? $"{config.AppIdentifier} is configured; its public metadata has not been read." : $"{config.AppIdentifier}: public metadata.", live?.CapturedAt));
    }
}
