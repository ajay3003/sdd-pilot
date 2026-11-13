using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Api.Services.IntegrationTopology;
using BirkNext.Interpretation;
using BirkNext.IntegrationTopology;
using BirkNext.Scopes;

namespace BirkNext.Api.Services.ScopeRegistry;

/// <summary>
/// Systems & Scopes overview for one Target Environment: the canonical interpretation's systems and connections, plus the connections Integration
/// Topology resolves from its own evidence (read offline from stored facts — no discovery, no network call). A topology component is matched to
/// a canonical system only by an exact name or an explicit/confirmed alias; otherwise it keeps its own name and cannot be a scope member.
/// Configured locations are configuration only, never a verified connection.
/// </summary>
public sealed class SystemsOverviewService(CanonicalInterpretationService canonical, IntegrationTopologyService? topology = null)
{
    public async Task<SystemsOverviewView> OverviewAsync(string scope, CancellationToken ct)
    {
        var view = await canonical.SystemsOverviewAsync(scope, ct);
        if (topology is null) return view with { Sources = [.. view.Sources, "Integration Topology: not available in this host"] };
        IntegrationTopologyView topo;
        try { topo = (await topology.BuildAsync(scope, new TopologyRequest(null), ct)).View; }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Collections.Generic.KeyNotFoundException)
        {
            return view with { Sources = [.. view.Sources, "Integration Topology: could not be read for this environment"] };
        }
        return Merge(view, topo);
    }

    /// <summary>Pure merge (testable): topology connections become connections with their own evidence state; locations annotate systems.</summary>
    public static SystemsOverviewView Merge(SystemsOverviewView view, IntegrationTopologyView topo)
    {
        var names = new Dictionary<string, SystemOverviewItem>(StringComparer.Ordinal);
        foreach (var s in view.Systems)
        {
            names.TryAdd(CanonicalKeys.Normalize(s.Name), s);
            foreach (var a in s.Aliases.Where(a => a.State is AliasState.Explicit or AliasState.Confirmed)) names.TryAdd(CanonicalKeys.Normalize(a.Alias), s);
        }
        var components = topo.Components.ToDictionary(c => c.Id, StringComparer.Ordinal);
        SystemOverviewItem? Match(TopologyComponent? c) => c is null ? null : names.GetValueOrDefault(CanonicalKeys.Normalize(c.Name));

        var connections = new List<SystemConnectionView>(view.Connections);
        foreach (var c in topo.Connections)
        {
            if (c.Relationship == TopologyRelationshipState.ExpectedUnresolved) continue; // an expectation alone is never a connection
            var from = components.GetValueOrDefault(c.ProducerComponentId);
            var to = components.GetValueOrDefault(c.ConsumerComponentId);
            if (from is null || to is null) continue;
            var (fromSystem, toSystem) = (Match(from), Match(to));
            connections.Add(new SystemConnectionView
            {
                Id = $"topology:{c.Id}", SourceKey = fromSystem?.Key ?? $"topology:{from.Id}", SourceName = fromSystem?.Name ?? from.Name,
                TargetKey = toSystem?.Key ?? $"topology:{to.Id}", TargetName = toSystem?.Name ?? to.Name,
                Label = TopologyLabel(c), Kind = c.Type.ToString(), Via = string.IsNullOrWhiteSpace(c.Protocol) ? null : c.Protocol,
                State = c.Relationship switch
                {
                    TopologyRelationshipState.Observed => SystemConnectionState.ObservedAtRuntime,
                    TopologyRelationshipState.Resolved => SystemConnectionState.FromSourceEvidence,
                    TopologyRelationshipState.StronglySupported => SystemConnectionState.NeedsReview,
                    _ => SystemConnectionState.Candidate,
                },
                Origin = "Integration Topology",
                Evidence = [.. c.Evidence.Select(e => $"{e.Source}: {e.Detail} ({e.Strength})")],
                Explanation = string.Join(" ", c.Notes), Unverified = c.Unverified,
            });
        }
        var located = view.Systems.Select(s =>
        {
            var component = topo.Components.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Location) && ReferenceEquals(Match(c), s));
            var outgoing = connections.Count(x => x.SourceKey == s.Key && x.State != SystemConnectionState.Rejected);
            var incoming = connections.Count(x => x.TargetKey == s.Key && x.State != SystemConnectionState.Rejected);
            var review = connections.Count(x => x.Origin == "Integration Topology" && (x.SourceKey == s.Key || x.TargetKey == s.Key)
                && x.State is SystemConnectionState.NeedsReview or SystemConnectionState.Candidate);
            return s with { ConfiguredLocation = component?.Location, Outgoing = outgoing, Incoming = incoming, NeedsReview = s.NeedsReview + review };
        }).ToList();
        var used = topo.Sources.Count(x => x.State == TopologySourceState.Used);
        return view with
        {
            Systems = located,
            Connections = [.. connections.OrderBy(c => c.SourceName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.TargetName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal)],
            Sources = [.. view.Sources, $"Integration Topology: used ({used} evidence source{(used == 1 ? "" : "s")} available, resolver v{topo.ResolverVersion})"],
        };
    }

    static string TopologyLabel(TopologyConnection c) => c.Type switch
    {
        TopologyInterfaceType.Rest => "provides a REST API to",
        TopologyInterfaceType.GraphQl => "provides a GraphQL API to",
        TopologyInterfaceType.Messaging => "sends messages to",
        TopologyInterfaceType.File => "provides files to",
        TopologyInterfaceType.Storage => "shares storage with",
        TopologyInterfaceType.Database => "provides data to",
        TopologyInterfaceType.ExternalApplication => "sends to",
        _ => "is connected to",
    };
}
