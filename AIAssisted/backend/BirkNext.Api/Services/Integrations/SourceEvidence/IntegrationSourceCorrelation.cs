using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;

/// <summary>
/// Integration-specific source evidence. A bound Source Analysis snapshot is an evidence container: binding an integration to it does not make
/// everything in it evidence for that integration. This correlates the integration's typed identity — producer, consumer and their known
/// technical aliases, the CDC source table, the transport channel — with the snapshot's components, projects, rules, tests, dataflows and path
/// stages, and returns a scoped copy holding only the correlated evidence. When nothing correlates, the scoped copy holds none of the
/// repository's rules/tests/hops ("Source bound · integration-specific evidence unresolved"): unrelated repository evidence is never presented
/// as support. The snapshot itself is never modified.
/// </summary>
public static class IntegrationSourceCorrelation
{
    public static (IqrSourceSnapshot Scoped, IntegrationSourceEvidenceScope Scope) Correlate(IntegrationDefinition integration, IqrSourceSnapshot snapshot,
        IReadOnlyList<IntegrationAlias> aliases)
    {
        var names = TechnicalNames(integration, aliases);
        var correlatedOn = new List<string>();
        var components = (snapshot.Architecture?.Components ?? []).Where(component => names.Any(name => Names(name, component.Name, Project(component.SourceProject))))
            .ToList();
        if (components.Count > 0) correlatedOn.Add($"Component {string.Join(", ", components.Select(component => component.Name).Distinct())}");

        var componentIds = components.Select(component => component.Id).ToHashSet(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(integration.EndpointOrTopic))
            foreach (var channel in (snapshot.Architecture?.MessagingChannels ?? []).Where(channel =>
                IntegrationIdentityResolver.SameName(integration.EndpointOrTopic, channel.EntityName) || IntegrationIdentityResolver.SameName(integration.EndpointOrTopic, channel.Name)))
            {
                correlatedOn.Add($"Channel {channel.EntityName ?? channel.Name}");
                foreach (var endpoint in channel.Producers.Concat(channel.Consumers)) componentIds.Add(endpoint.ComponentId);
            }
        var projects = (snapshot.Architecture?.Components ?? []).Where(component => componentIds.Contains(component.Id)).Select(component => Project(component.SourceProject))
            .Where(project => project.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var resource = SourceResourceIdentity.Of(integration);
        bool TableMatches(string? table) => resource is not null && table is not null
            && (string.Equals(table, resource.Table, StringComparison.OrdinalIgnoreCase) || string.Equals(table, resource.Qualified, StringComparison.OrdinalIgnoreCase)
                || string.Equals(table, resource.Full, StringComparison.OrdinalIgnoreCase));
        var tableRules = snapshot.Rules.Where(rule => TableMatches(rule.Table)).ToList();
        if (tableRules.Count > 0)
        {
            correlatedOn.Add($"Source table {resource!.Qualified}");
            foreach (var rule in tableRules.Where(rule => rule.Project.Length > 0)) projects.Add(rule.Project);
        }

        bool InProject(string? project) => !string.IsNullOrWhiteSpace(project) && projects.Any(p =>
            string.Equals(project, p, StringComparison.OrdinalIgnoreCase) || project.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase));
        bool InPath(string? file) => !string.IsNullOrWhiteSpace(file) && projects.Any(p => file.Replace('\\', '/').Contains($"/{p}/", StringComparison.OrdinalIgnoreCase)
            || file.Replace('\\', '/').StartsWith($"{p}/", StringComparison.OrdinalIgnoreCase));

        var rules = snapshot.Rules.Where(rule => InProject(rule.Project) || TableMatches(rule.Table)).ToList();
        var tests = snapshot.Tests.Where(test => InProject(test.Project) || test.ProductionSymbols.Any(symbol => projects.Any(p => symbol.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase)))).ToList();
        var dataflows = snapshot.Dataflows.Where(flow => flow.Locations.Any(location => InPath(location.File))).ToList();
        var path = snapshot.IntegrationPath is { } full ? Scope(full, InProject, InPath) : null;
        var resolved = projects.Count > 0 && (rules.Count + tests.Count + dataflows.Count + (path?.Hops.Count ?? 0) > 0 || components.Count > 0);

        var scoped = snapshot with
        {
            Rules = resolved ? rules : [], Tests = resolved ? tests : [], Dataflows = resolved ? dataflows : [], IntegrationPath = resolved ? path : null,
            // Observability describes the repository's applications: kept only when this integration's own components were correlated.
            Observability = resolved ? snapshot.Observability : null,
        };
        var scope = new IntegrationSourceEvidenceScope(integration.Id)
        {
            SnapshotId = snapshot.Id, ArchiveName = snapshot.Archive.FileName, SnapshotBound = true, EvidenceResolved = resolved,
            CorrelatedOn = correlatedOn, CorrelatedProjects = [.. projects.Order(StringComparer.OrdinalIgnoreCase)],
            IntegrationRules = scoped.Rules.Count, IntegrationTests = scoped.Tests.Count, IntegrationPathHops = scoped.IntegrationPath?.Hops.Count ?? 0,
            RepositoryRules = snapshot.Rules.Count, RepositoryTests = snapshot.Tests.Count, RepositoryPathHops = snapshot.IntegrationPath?.Hops.Count ?? 0,
            Summary = resolved
                ? $"Source bound · integration-specific evidence resolved through {string.Join("; ", correlatedOn)} ({scoped.Rules.Count} rule(s), {scoped.Tests.Count} developer test(s) in {string.Join(", ", projects)})."
                : $"Source bound · integration-specific evidence unresolved: nothing in {snapshot.Archive.FileName} correlates with this integration's producer, consumer, channel or source resource. "
                  + "Repository evidence is context only and is not shown as support.",
        };
        return (scoped, scope);
    }

    /// <summary>The integration's technical names: producer, consumer and systems as configured, plus the technical side of every alias whose
    /// logical side is one of them (expected aliases only widen correlation; they never make a fact source-proven).</summary>
    public static IReadOnlyList<string> TechnicalNames(IntegrationDefinition integration, IReadOnlyList<IntegrationAlias> aliases)
    {
        var configured = new[] { integration.Producer, integration.SourceSystem, integration.Consumer.DisplayName, integration.Consumer.LogicalName, integration.DestinationSystem }
            .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToList();
        return [.. configured.Concat(aliases.Where(alias => configured.Any(name => IntegrationIdentityResolver.AnyName(alias.Logical, name) || IntegrationIdentityResolver.SameName(alias.Logical, name)))
            .Select(alias => alias.Technical)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A name identifies a component when it is the component's (or project's) whole name, or one of its dot-separated segments
    /// ("SkoleAdapter" ↔ "M2LB.SkoleAdapter.Worker"). Never a substring or similarity.</summary>
    private static bool Names(string name, params string[] values) => values.Any(value => IntegrationIdentityResolver.SameName(name, value)
        || (!name.Contains('.') && value.Split('.').Any(segment => IntegrationIdentityResolver.SameName(name, segment))));

    private static string Project(string? sourceProject) => BirkNext.Api.Services.IntegrationTopology.TopologySnapshots.ProjectName(sourceProject);

    private static IntegrationPathEvidence Scope(IntegrationPathEvidence path, Func<string?, bool> inProject, Func<string?, bool> inPath)
    {
        var stages = path.Stages.Where(stage => inProject(stage.Project)).ToList();
        var ids = stages.Select(stage => stage.Id).ToHashSet(StringComparer.Ordinal);
        return path with
        {
            Stages = stages,
            Hops = [.. path.Hops.Where(hop => ids.Contains(hop.From) || ids.Contains(hop.To))],
            Fields = [.. path.Fields.Where(field => field.Steps.Any(step => inPath(step.Location?.File)))],
            Rules = [.. path.Rules.Where(rule => rule.Locations.Any(location => inPath(location.File)))],
        };
    }
}
