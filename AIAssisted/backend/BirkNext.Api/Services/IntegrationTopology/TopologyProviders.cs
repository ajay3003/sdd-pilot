using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Integrations;
using BirkNext.IntegrationTopology;
using BirkNext.SourceArchitecture;

namespace BirkNext.Api.Services.IntegrationTopology;

/// <summary>Everything a provider may read for one environment. Providers never write anywhere and never call out unless the request asks.</summary>
public sealed record TopologyContext(string EnvironmentId, TrustedExecutionEnvironment? Trusted, string TrustDetail, IntegrationCatalog Catalog,
    IReadOnlyList<IqrSourceSnapshot> Snapshots, TopologyRequest Request);

/// <summary>A discovery source feeding the canonical topology. <see cref="Order"/>: lower runs first (locations before observations).</summary>
public interface ITopologyProvider
{
    int Order { get; }
    Task ContributeAsync(TopologyContext context, TopologyBuilder builder, CancellationToken ct);
}

public sealed record TopologyExpectedComponent(string Name, IReadOnlyList<string> Aliases, string Evidence);
public sealed record TopologyExpectedStep(string Label, string From, string To);
public sealed record TopologyExpectedPath(string Id, string Label, IReadOnlyList<TopologyExpectedStep> Steps);

/// <summary>
/// A domain's expectations: the components it knows (with explicit aliases for names different sources use) and the paths its scenarios
/// rely on. A profile never adds a connection: expected is not configured, discovered or observed.
/// </summary>
public interface ITopologyProfile
{
    string Id { get; }
    string Label { get; }
    IReadOnlyList<TopologyExpectedComponent> Components { get; }
    IReadOnlyList<TopologyExpectedPath> Paths { get; }
}

/// <summary>Target Environment: the backend-trusted environment's component membership and base locations (configuration only).</summary>
public sealed class TargetEnvironmentTopologyProvider : ITopologyProvider
{
    public int Order => 10;

    public Task ContributeAsync(TopologyContext context, TopologyBuilder builder, CancellationToken ct)
    {
        if (context.Trusted is not { } trusted)
        {
            builder.Source(new(TopologySource.TargetEnvironment, TopologySourceState.NotAvailable, context.TrustDetail));
            return Task.CompletedTask;
        }
        foreach (var component in trusted.Components)
        {
            var location = TrustedComponentPolicy.SafeBaseUrl(component.BaseUrl);
            builder.Component(component.Name, TopologyStatus.Configured,
                new(TopologySource.TargetEnvironment, trusted.EnvironmentId, location is null && component.BaseUrl is { Length: > 0 }
                    ? $"Member of {trusted.DisplayName}; its base address is not a safe absolute http(s) address and is ignored." : $"Member of {trusted.DisplayName}."),
                location: location);
        }
        builder.Source(new(TopologySource.TargetEnvironment, TopologySourceState.Used,
            $"{trusted.DisplayName} ({trusted.EnvironmentType}, execution {(trusted.ExecutionAllowed ? "allowed" : "not allowed")}): {trusted.Components.Count} component(s), {trusted.IntegrationIds.Count} enrolled integration(s)."));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Source Analysis: components, their declared interfaces (routes, GraphQL, messaging), resolved HTTP/GraphQL dependencies and messaging
/// channels with their producers and consumers — the existing source-architecture extraction, never parsed again here. Every repository's
/// newest snapshot counts (separate repositories are separate archives).
/// </summary>
public sealed class SourceArchitectureTopologyProvider : ITopologyProvider
{
    public int Order => 20;

    public Task ContributeAsync(TopologyContext context, TopologyBuilder builder, CancellationToken ct)
    {
        var snapshots = context.Snapshots.Where(snapshot => snapshot.Architecture is not null)
            .GroupBy(snapshot => snapshot.Archive.FileName, StringComparer.OrdinalIgnoreCase).Select(group => group.OrderByDescending(item => item.AnalyzedAt).First()).ToList();
        if (snapshots.Count == 0)
        {
            builder.Source(new(TopologySource.SourceAnalysis, TopologySourceState.NotAvailable, "No Source Analysis snapshot with architecture evidence exists for this environment."));
            return Task.CompletedTask;
        }
        foreach (var snapshot in snapshots)
        {
            var architecture = snapshot.Architecture!;
            TopologyProvenance Provenance(string detail) => new(TopologySource.SourceAnalysis, snapshot.Archive.FileName, detail, snapshot.AnalyzedAt);
            var names = architecture.Components.ToDictionary(component => component.Id, component => component.Name, StringComparer.Ordinal);
            string Key(string componentId) => builder.ComponentKey(names.GetValueOrDefault(componentId) ?? componentId);
            foreach (var component in architecture.Components)
                builder.Component(component.Name, TopologyStatus.Discovered, Provenance($"{component.ComponentType} in {component.SourceProject}"), component.ComponentType.ToString());
            foreach (var item in architecture.Interfaces.Where(item => names.ContainsKey(item.ComponentId)))
            {
                var (type, direction) = Classify(item.Type, item.Protocol, item.Direction);
                builder.Interface(Key(item.ComponentId), type, item.Protocol, direction, item.RouteOrTopic ?? item.Name, Method(item.Name), TopologyStatus.Discovered,
                    Provenance($"{item.Type} {item.Name}"), review: item.Confidence is ArchitectureEvidenceState.Unresolved, conflict: item.Confidence is ArchitectureEvidenceState.Conflict);
            }
            foreach (var dependency in architecture.Dependencies.Where(item => item.DependencyType is ArchitectureDependencyType.Http or ArchitectureDependencyType.GraphQl))
            {
                var type = dependency.DependencyType == ArchitectureDependencyType.GraphQl ? TopologyInterfaceType.GraphQl : TopologyInterfaceType.Rest;
                var from = Key(dependency.FromComponentId);
                var target = dependency.TargetReference ?? dependency.ToId ?? "";
                var conflict = dependency.ConflictingTargets.Count > 0 || dependency.EvidenceState == ArchitectureEvidenceState.Conflict;
                var calls = builder.Interface(from, type, dependency.Protocol, TopologyDirection.Calls, target, null, TopologyStatus.Discovered,
                    Provenance($"Client dependency ({dependency.Framework ?? dependency.Protocol})"), dependency.ContractReference, conflict, review: !dependency.IsResolved,
                    note: conflict ? $"Conflicting targets: {string.Join(", ", dependency.ConflictingTargets)}" : !dependency.IsResolved ? "The called component is not resolved from source." : null);
                if (dependency.ToId is { } to && names.ContainsKey(to) && !conflict)
                    builder.Connection(from, Key(to), type, dependency.Protocol, dependency.TargetReference, TopologyStatus.Discovered,
                        Provenance("Resolved client dependency in source"), fromInterface: calls, contract: dependency.ContractReference);
            }
            foreach (var channel in architecture.MessagingChannels)
            {
                var protocol = channel.Type.ToString();
                var address = channel.EntityName ?? channel.Name;
                var review = channel.NameUnresolved || channel.Confidence is ArchitectureEvidenceState.Unresolved;
                var produced = channel.Producers.Where(item => names.ContainsKey(item.ComponentId))
                    .Select(item => (Key(item.ComponentId), builder.Interface(Key(item.ComponentId), TopologyInterfaceType.Messaging, protocol, TopologyDirection.Produces, address, null,
                        TopologyStatus.Discovered, Provenance($"Produces to {protocol} {address}"), channel.ContractReferences.FirstOrDefault(), review: review))).ToList();
                var consumed = channel.Consumers.Where(item => names.ContainsKey(item.ComponentId))
                    .Select(item => (Key(item.ComponentId), builder.Interface(Key(item.ComponentId), TopologyInterfaceType.Messaging, protocol, TopologyDirection.Consumes, address, null,
                        TopologyStatus.Discovered, Provenance($"Consumes from {protocol} {address}"), channel.ContractReferences.FirstOrDefault(), review: review))).ToList();
                foreach (var producer in produced)
                    foreach (var consumer in consumed.Where(item => item.Item1 != producer.Item1))
                        builder.Connection(producer.Item1, consumer.Item1, TopologyInterfaceType.Messaging, protocol, address, TopologyStatus.Discovered,
                            Provenance("Producer and consumer of the same channel in source"), producer.Item2, consumer.Item2, channel.ContractReferences.FirstOrDefault(), review: review);
            }
        }
        builder.Source(new(TopologySource.SourceAnalysis, TopologySourceState.Used, $"{snapshots.Count} repository snapshot(s): {string.Join(", ", snapshots.Select(item => item.Archive.FileName))}.",
            snapshots.Max(item => item.AnalyzedAt)));
        return Task.CompletedTask;
    }

    private static (TopologyInterfaceType, TopologyDirection) Classify(string type, string protocol, string direction)
    {
        var text = $"{type} {protocol}".ToLowerInvariant();
        var interfaceType = text.Contains("graphql") ? TopologyInterfaceType.GraphQl
            : text.Contains("eventhub") || text.Contains("event hub") || text.Contains("servicebus") || text.Contains("service bus") || text.Contains("queue") || text.Contains("topic") ? TopologyInterfaceType.Messaging
            : text.Contains("blob") || text.Contains("storage") ? TopologyInterfaceType.Storage
            : text.Contains("http") || text.Contains("rest") || text.Contains("route") || text.Contains("endpoint") ? TopologyInterfaceType.Rest : TopologyInterfaceType.Other;
        var dir = direction.ToLowerInvariant();
        var topologyDirection = interfaceType == TopologyInterfaceType.Messaging
            ? dir.Contains("out") || dir.Contains("produce") || dir.Contains("publish") ? TopologyDirection.Produces : TopologyDirection.Consumes
            : dir.Contains("out") ? TopologyDirection.Calls : TopologyDirection.Provides;
        return (interfaceType, topologyDirection);
    }

    /// <summary>"GET /api/x" → "GET"; otherwise null.</summary>
    private static string? Method(string name) => name.Split(' ', 2) is [var verb, _] && verb.All(char.IsLetter) && verb.Length <= 7 && verb == verb.ToUpperInvariant() ? verb : null;
}

/// <summary>
/// Integration Quality Review's catalog: each enabled integration states a configured connection from its producer to its consumer over its
/// endpoint or topic. Configured only — the catalog does not prove the connection exists or is used.
/// </summary>
public sealed class IntegrationCatalogTopologyProvider : ITopologyProvider
{
    public int Order => 30;

    public Task ContributeAsync(TopologyContext context, TopologyBuilder builder, CancellationToken ct)
    {
        var enabled = context.Catalog.Integrations.Where(item => item.Enabled).ToList();
        foreach (var integration in enabled)
        {
            var producer = integration.Producer ?? integration.SourceSystem;
            var consumer = integration.Consumer.DisplayName ?? integration.Consumer.LogicalName ?? integration.DestinationSystem;
            var enrolled = context.Trusted?.IntegrationIds.Contains(integration.Id, StringComparer.Ordinal) == true;
            var provenance = new TopologyProvenance(TopologySource.IntegrationCatalog, integration.Id,
                $"{integration.DisplayName}{(enrolled ? " · enabled for active runs" : "")}", integration.UpdatedAt == default ? null : integration.UpdatedAt);
            var (type, protocol) = integration.Kind switch
            {
                IntegrationKind.HttpApi => (TopologyInterfaceType.Rest, "HTTP"),
                IntegrationKind.ServiceBus => (TopologyInterfaceType.Messaging, "ServiceBus"),
                IntegrationKind.EventHub => (TopologyInterfaceType.Messaging, "EventHub"),
                IntegrationKind.File => (TopologyInterfaceType.File, "File"),
                IntegrationKind.Database => (TopologyInterfaceType.Database, "Database"),
                _ => (TopologyInterfaceType.Other, integration.Kind.ToString()),
            };
            var address = integration.EndpointOrTopic ?? integration.SourceResource ?? integration.DisplayName;
            string? from = null, to = null, fromInterface = null, toInterface = null;
            if (!string.IsNullOrWhiteSpace(producer))
            {
                from = builder.Component(producer, TopologyStatus.Configured, provenance);
                fromInterface = builder.Interface(from, type, protocol, type == TopologyInterfaceType.Messaging ? TopologyDirection.Produces : TopologyDirection.Calls, address, null,
                    TopologyStatus.Configured, provenance, integration.ProducerContractReference);
            }
            if (!string.IsNullOrWhiteSpace(consumer))
            {
                to = builder.Component(consumer, TopologyStatus.Configured, provenance);
                toInterface = builder.Interface(to, type, protocol, type == TopologyInterfaceType.Messaging ? TopologyDirection.Consumes : TopologyDirection.Provides, address, null,
                    TopologyStatus.Configured, provenance, integration.ConsumerContractReference);
            }
            if (from is not null && to is not null)
                builder.Connection(from, to, type, protocol, address, TopologyStatus.Configured, provenance, fromInterface, toInterface, integration.ConsumerContractReference ?? integration.ProducerContractReference);
        }
        builder.Source(enabled.Count == 0
            ? new(TopologySource.IntegrationCatalog, TopologySourceState.NotAvailable, "No enabled integration is configured in Integration Quality Review for this environment.")
            : new(TopologySource.IntegrationCatalog, TopologySourceState.Used, $"{enabled.Count} enabled integration(s) from Integration Quality Review."));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Runtime observations the browser already holds (Endpoint Discovery evidence: method, host and path). They mark a known interface Observed,
/// or add one under the component whose location matches the host. An unmatched host stays its own component. A connection is never
/// inferred from an observation.
/// </summary>
public sealed class ObservationTopologyProvider : ITopologyProvider
{
    public int Order => 90;

    public Task ContributeAsync(TopologyContext context, TopologyBuilder builder, CancellationToken ct)
    {
        var observations = (context.Request.Observations ?? []).Where(item => item.Host.Length > 0 && item.Path.Length > 0).Take(2000).ToList();
        if (observations.Count == 0)
        {
            builder.Source(new(TopologySource.BrowserObservation, TopologySourceState.NotRequested, "No runtime observation was supplied; nothing is called Observed."));
            return Task.CompletedTask;
        }
        foreach (var observation in observations)
        {
            var type = string.Equals(observation.Category, "GraphQl", StringComparison.OrdinalIgnoreCase) ? TopologyInterfaceType.GraphQl : TopologyInterfaceType.Rest;
            var path = observation.Path.Split('?', '#')[0];
            var provenance = new TopologyProvenance(TopologySource.BrowserObservation, observation.Host, $"Observed {observation.Method} {path}", observation.LastObservedAt);
            var component = builder.ComponentForHost(observation.Host)
                ?? builder.Component(observation.Host, TopologyStatus.Observed, new(TopologySource.BrowserObservation, observation.Host, "Host seen at runtime; not mapped to a configured component."), location: observation.Host);
            builder.Interface(component, type, type == TopologyInterfaceType.GraphQl ? "GraphQL" : "HTTP", TopologyDirection.Provides, path,
                type == TopologyInterfaceType.GraphQl ? observation.OperationName : observation.Method, TopologyStatus.Observed, provenance);
        }
        builder.Source(new(TopologySource.BrowserObservation, TopologySourceState.Used, $"{observations.Count} observed request pattern(s) from Endpoint Discovery.", observations.Max(item => item.LastObservedAt)));
        return Task.CompletedTask;
    }
}

/// <summary>Validation of a trusted component's configured addresses: absolute http(s), no credentials, query or fragment; OpenAPI path relative.</summary>
public static class TrustedComponentPolicy
{
    public static string? SafeBaseUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" : null;

    /// <summary>The OpenAPI document address on the component's own host, or null (absolute URLs, "..", other hosts are refused).</summary>
    public static Uri? OpenApiAddress(TrustedComponent component)
    {
        if (SafeBaseUrl(component.BaseUrl) is not { } baseUrl || string.IsNullOrWhiteSpace(component.OpenApiPath)) return null;
        var path = component.OpenApiPath.Trim();
        if (!path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) || path.Contains("://", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return null;
        var address = new Uri(new Uri(baseUrl), path.TrimStart('/'));
        return string.Equals(address.Host, new Uri(baseUrl).Host, StringComparison.OrdinalIgnoreCase) ? address : null;
    }
}
