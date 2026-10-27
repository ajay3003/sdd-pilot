using BirkNext.Integrations;
using BirkNext.IntegrationTopology;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.IntegrationTopology;

/// <summary>
/// Topology source coverage for one profile: which declared source roles the current sources cover (by content), how each expected
/// relationship resolves against the evidence-based topology, what evidence and which source role is missing for each unresolved one, and a
/// categorical completeness per dimension (never one percentage). Generic: it reads the profile's declarations only.
/// <para>Source presence and source binding are separate. With the environment's canonical IQR bindings supplied (consumed, never written),
/// a recognized application source is Bound when a binding points at one of its snapshots, Available — needs binding when exactly one
/// distinct source is recognized and none is bound, and Ambiguous when several distinct sources are (the newest is never chosen). Not provided
/// is reserved for no recognized evidence at all. Infrastructure roles are consumed environment-wide from Infrastructure Evidence and need no
/// binding. Without binding information (null) recognized sources read Available, as before.</para>
/// </summary>
public static class TopologyCoverage
{
    public static TopologyCoverageView Evaluate(ITopologyProfile profile, IntegrationTopologyView view, TopologyBuilder builder, IReadOnlyList<IqrSourceSnapshot> snapshots,
        TopologyRequest request, DateTimeOffset now, IReadOnlyList<IntegrationEvidenceBinding>? bindings = null)
    {
        var latest = TopologySnapshots.Latest(snapshots);
        var bound = bindings?.Select(item => item.SnapshotId).ToHashSet();
        var roles = profile.SourceRoles.Select(role => Bind(Role(role, latest, profile), role, snapshots, bound)).ToList();
        var roleComponents = profile.SourceRoles.ToDictionary(role => role.Id, role => RoleComponents(role, latest, builder), StringComparer.Ordinal);
        var relationships = profile.Relationships.Select(relationship => Relationship(relationship, view, builder, roles, roleComponents)).ToList();
        var core = roles.Where(role => role.Requirement == TopologySourceRequirement.Required).All(role => role.State is TopologySourceRoleState.Available or TopologySourceRoleState.Bound);
        var resolved = relationships.Count(item => item.State is TopologyRelationshipState.Resolved or TopologyRelationshipState.Observed);
        var open = relationships.Count - resolved;
        return new TopologyCoverageView
        {
            ProfileId = profile.Id, ProfileLabel = profile.Label, ResolverVersion = CrossSourceTopologyProvider.ResolverVersion, GeneratedAt = now,
            Roles = roles, Relationships = relationships, Completeness = Completeness(relationships, request), CoreSourcesComplete = core,
            Summary = relationships.Count == 0 ? "This profile declares no expected relationships."
                : open == 0 ? "Topology is resolved: every expected relationship has sufficient evidence."
                : $"Topology is partially resolved. {open} expected relationship{(open == 1 ? "" : "s")} need{(open == 1 ? "s" : "")} additional evidence.",
        };
    }

    /// <summary>Capabilities a snapshot shows, from its stored facts (nothing is re-analyzed).</summary>
    public static HashSet<string> Capabilities(IqrSourceSnapshot snapshot)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (snapshot.Architecture is { } architecture)
        {
            if (architecture.Components.Count > 0) found.Add(TopologyCapabilities.ApplicationSource);
            if (architecture.Dependencies.Any(item => item.DependencyType is ArchitectureDependencyType.Http or ArchitectureDependencyType.GraphQl)) found.Add(TopologyCapabilities.HttpClient);
            if (architecture.Interfaces.Any(item => item.Direction.Contains("in", StringComparison.OrdinalIgnoreCase) && (item.Protocol.Contains("HTTP", StringComparison.OrdinalIgnoreCase) || item.Protocol.Contains("GraphQL", StringComparison.OrdinalIgnoreCase))))
                found.Add(TopologyCapabilities.HttpServer);
            if (architecture.MessagingChannels.Any(item => item.Type is MessagingChannelType.ServiceBusQueue or MessagingChannelType.ServiceBusTopic or MessagingChannelType.ServiceBusEntity)) found.Add(TopologyCapabilities.ServiceBus);
            if (architecture.MessagingChannels.Any(item => item.Type is MessagingChannelType.EventHub or MessagingChannelType.KafkaTopic)) found.Add(TopologyCapabilities.EventHub);
        }
        if (snapshot.EvidenceDomains is { } domains)
        {
            if (domains.Infrastructure.Formats.Contains(InfrastructureFormat.Terraform)) found.Add(TopologyCapabilities.Terraform);
            foreach (var resource in domains.Infrastructure.Resources)
                switch (InfrastructureIdentity.KindOfType(resource.ResourceType))
                {
                    case InfrastructureResourceKind.EventHub or InfrastructureResourceKind.EventHubNamespace: found.Add(TopologyCapabilities.EventHub); break;
                    case InfrastructureResourceKind.ServiceBusNamespace or InfrastructureResourceKind.ServiceBusQueue or InfrastructureResourceKind.ServiceBusTopic or InfrastructureResourceKind.ServiceBusSubscription:
                        found.Add(TopologyCapabilities.ServiceBus); break;
                }
            if (domains.Configuration.Entries.Any(entry => System.Text.RegularExpressions.Regex.IsMatch(entry.Key, "(?i)topic[._:\\-]?prefix")
                && System.Text.RegularExpressions.Regex.IsMatch(entry.Key + " " + entry.File, "(?i)(debezium|kafka[._\\-]?connect|cdc)"))) found.Add(TopologyCapabilities.Cdc);
            if (domains.Contracts.Contracts.Any(item => item.Type == SourceContractType.OpenApi)) found.Add(TopologyCapabilities.OpenApi);
            if (domains.ConfigurationValues?.Values.Any(item => item.SemanticKey == "Application.DisplayName") == true) found.Add(TopologyCapabilities.ApplicationMetadata);
        }
        return found;
    }

    private static TopologySourceRoleCoverage Role(TopologySourceRoleDefinition role, List<IqrSourceSnapshot> snapshots, ITopologyProfile profile)
    {
        var coverage = new TopologySourceRoleCoverage
        {
            RoleId = role.Id, DisplayName = role.DisplayName, Requirement = role.Requirement, CapabilitiesProvided = [.. role.Capabilities], Guidance = role.Guidance, Consequence = role.Consequence,
        };
        if (role.Requirement == TopologySourceRequirement.BuiltInReference)
        {
            TopologyReferenceFacts? facts = null;
            string? failure = null;
            if (profile.References.FirstOrDefault(item => item.RoleId == role.Id) is { } reference)
                try { facts = reference.Load(); } catch (Exception ex) when (ex is not OutOfMemoryException) { failure = ex.Message; }
            return facts is null
                ? coverage with { State = TopologySourceRoleState.ReferenceUnavailable, Detail = $"Test Pack reference unavailable{(failure is null ? "." : $": {failure}")}" }
                : coverage with { State = TopologySourceRoleState.ReferenceAvailable, CapabilitiesFound = [TopologyCapabilities.Reference], Detail = $"Test Pack reference available · no import required. {facts.Detail}" };
        }
        if (role.Match.InfrastructureFormats.Count > 0)
        {
            var matching = snapshots.Where(item => item.EvidenceDomains?.Infrastructure.Formats.Any(role.Match.InfrastructureFormats.Contains) == true).ToList();
            if (matching.Count == 0)
                return coverage with
                {
                    State = snapshots.Any(item => item.EvidenceDomains is null) ? TopologySourceRoleState.NeedsAnalysis : TopologySourceRoleState.NotProvided,
                    Detail = snapshots.Any(item => item.EvidenceDomains is null) ? "Some sources were analyzed before infrastructure evidence was read; re-analyze them, or import the infrastructure source." : "No source with Infrastructure as Code is available.",
                };
            var old = matching.Where(item => item.EvidenceDomains!.Infrastructure.AnalyzerVersion < 3).Select(item => item.Archive.FileName).ToList();
            return coverage with
            {
                State = TopologySourceRoleState.Available, MatchingSources = [.. matching.Select(item => item.Archive.FileName)],
                CapabilitiesFound = [.. role.Capabilities.Where(matching.SelectMany(Capabilities).ToHashSet().Contains)],
                Detail = old.Count == 0 ? "Infrastructure topology supported." : $"Infrastructure topology supported. {string.Join(", ", old)} was analyzed before endpoint injections were read; module address injections are used after re-analysis.",
            };
        }
        var parts = PartsOf(role.Match).Select(part => (Token: part.Label, Sources: snapshots.Where(snapshot => snapshot.Architecture?.Components.Any(part.Matches) == true).ToList())).ToList();
        var byConfiguration = role.Match.ConfigurationKeyPrefixes.Count == 0 ? [] : snapshots.Where(snapshot => snapshot.EvidenceDomains?.Configuration.Entries
            .Any(entry => role.Match.ConfigurationKeyPrefixes.Any(prefix => entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) == true).ToList();
        var sources = parts.SelectMany(part => part.Sources).Concat(byConfiguration).Distinct().ToList();
        if (sources.Count == 0)
        {
            // Only a snapshot with neither architecture nor infrastructure evidence predates the analysis this role needs.
            var unanalyzed = snapshots.Any(item => item.Architecture is null && (item.EvidenceDomains?.Infrastructure.Formats.Count ?? 0) == 0);
            return coverage with
            {
                State = unanalyzed ? TopologySourceRoleState.NeedsAnalysis : TopologySourceRoleState.NotProvided,
                Detail = unanalyzed ? "Some sources were analyzed before architecture evidence was read; re-analyze them, or import this source." : "Not provided.",
            };
        }
        var missing = parts.Where(part => part.Sources.Count == 0).Select(part => part.Token).ToList();
        return coverage with
        {
            State = missing.Count == 0 ? TopologySourceRoleState.Available : TopologySourceRoleState.PartlyAvailable,
            MatchingSources = [.. sources.Select(item => item.Archive.FileName)], MissingParts = missing,
            CapabilitiesFound = [.. role.Capabilities.Where(sources.SelectMany(Capabilities).ToHashSet().Contains)],
            Detail = missing.Count == 0 ? "Application topology supported." : $"Found in {string.Join(", ", sources.Select(item => item.Archive.FileName))}; not found: {string.Join(", ", missing)}.",
        };
    }

    private const string ResourcePrefix = "resource:";
    private const string RolePrefix = "role:";

    /// <summary>The components a role's matching sources contribute (by the role's component tokens/prefixes), as topology keys.</summary>
    private static List<string> RoleComponents(TopologySourceRoleDefinition role, List<IqrSourceSnapshot> snapshots, TopologyBuilder builder) =>
        [.. snapshots.SelectMany(snapshot => snapshot.Architecture?.Components ?? []).Where(component => PartsOf(role.Match).Any(part => part.Matches(component)))
            .Select(component => builder.ComponentKey(component.Name, TopologySnapshots.Identities(component))).Distinct()];

    private sealed record Part(string Label, Func<ArchitectureComponent, bool> Matches);

    private static List<Part> PartsOf(TopologySourceMatch match) =>
    [
        .. match.Components.Select(token => (Label: token.Replace("|", " or ", StringComparison.Ordinal), Alternatives: token.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            .Select(part => new Part(part.Label, component => part.Alternatives.Any(token => component.Name.Contains(token, StringComparison.OrdinalIgnoreCase) || component.SourceProject.Contains(token, StringComparison.OrdinalIgnoreCase)))),
        .. match.ComponentPrefixes.Select(prefix => new Part(prefix + "*", component => component.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || TopologySnapshots.ProjectName(component.SourceProject).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))),
    ];

    private static TopologyRelationshipResult Relationship(TopologyExpectedRelationship expected, IntegrationTopologyView view, TopologyBuilder builder,
        List<TopologySourceRoleCoverage> roles, Dictionary<string, List<string>> roleComponents)
    {
        string Name(string id) => view.Components.FirstOrDefault(item => item.Id == id)?.Name ?? id;
        TopologyReach.End End(string value) => value.StartsWith(ResourcePrefix, StringComparison.Ordinal) ? TopologyReach.End.Kind(value[ResourcePrefix.Length..])
            : value.StartsWith(RolePrefix, StringComparison.Ordinal) ? new(roleComponents.GetValueOrDefault(value[RolePrefix.Length..]) ?? [], null)
            : TopologyReach.End.Of(builder.ComponentKey(value));
        string Display(string value) => value.StartsWith(ResourcePrefix, StringComparison.Ordinal) ? Label(value[ResourcePrefix.Length..])
            : value.StartsWith(RolePrefix, StringComparison.Ordinal) ? roles.FirstOrDefault(role => role.RoleId == value[RolePrefix.Length..])?.DisplayName ?? value[RolePrefix.Length..]
            : value;
        var from = End(expected.From);
        var to = End(expected.To);
        var via = expected.Via is null ? null : End(expected.Via);
        var chains = TopologyReach.Chains(view, from, to, via);
        var result = new TopologyRelationshipResult
        {
            Id = expected.Id, Label = expected.Label, From = Display(expected.From), To = Display(expected.To), Transport = expected.Transport, Dimension = expected.Dimension,
        };
        if (TopologyReach.Best(chains) is { } best && !best.Conflict)
        {
            var evidence = best.Hops.SelectMany(hop => hop.Evidence).Distinct().ToList();
            var state = best.State;
            result = result with
            {
                State = state, ConnectionIds = [.. best.Hops.Select(hop => hop.Id)], RuntimeObserved = best.Hops.All(hop => hop.Status == TopologyStatus.Observed),
                Route = Route(chains, best, Name),
                Evidence = evidence, Why = Why(evidence), Unverified = TopologyBuilder.Unverified(state, evidence),
            };
            if (state is TopologyRelationshipState.Resolved or TopologyRelationshipState.Observed) return result;
            return Guidance(result with { AvailableEvidence = Why(evidence) }, expected, roles);
        }
        var conflict = chains.Any(chain => chain.Conflict);
        var partial = Partial(view, from, to, via, Name, TransportKind(expected.Transport));
        var present = new[] { from, to }.Any(end => end.Components?.Any(id => view.Components.Any(c => c.Id == id && c.Status != TopologyStatus.Expected)) == true);
        var state2 = conflict || partial is not null && present ? TopologyRelationshipState.Candidate : TopologyRelationshipState.ExpectedUnresolved;
        return Guidance(result with
        {
            State = state2,
            AvailableEvidence = conflict ? "Sources disagree about this connection; review it before relying on it." : partial,
            Why = state2 == TopologyRelationshipState.Candidate ? "Partial evidence: only one side of the relationship is stated." : "Expected only: no source states this relationship.",
            Unverified = "Not resolved: nothing about this relationship is verified.",
        }, expected, roles);
    }

    /// <summary>The best chain's route; when other chains are equally certain over different resources, they are listed instead of one being chosen.</summary>
    private static string Route(List<TopologyReach.Chain> chains, TopologyReach.Chain best, Func<string, string> name)
    {
        string Path(TopologyReach.Chain chain) => string.Join(" → ", chain.Hops.Select(hop => hop.ConsumerComponentId).Prepend(chain.Hops[0].ProducerComponentId).Select(name));
        var alternatives = chains.Where(chain => chain != best && !chain.Conflict && chain.State == best.State).Select(Path).Distinct().Where(path => path != Path(best)).Take(6).ToList();
        return alternatives.Count == 0 ? Path(best) : $"{Path(best)} (equally supported: {string.Join("; ", alternatives)})";
    }

    /// <summary>The resource kind a transport uses ("Service Bus" → servicebus); null for HTTP and others.</summary>
    private static string? TransportKind(string transport) =>
        transport.Contains("Service Bus", StringComparison.OrdinalIgnoreCase) ? "servicebus"
        : transport.Contains("Event Hub", StringComparison.OrdinalIgnoreCase) ? "eventhub"
        : transport.Contains("CDC", StringComparison.OrdinalIgnoreCase) ? "connector" : null;

    private static string Label(string kind) => kind switch { "servicebus" => "Service Bus", "eventhub" => "Event Hub", "connector" => "CDC connector", "table" => "Captured table", _ => kind };

    /// <summary>Evidence of one side only — what the from-side produces or calls without a resolved target, what feeds the to-side, what the
    /// via end produces. Phrased as what is available, never as a connection.</summary>
    private static string? Partial(IntegrationTopologyView view, TopologyReach.End from, TopologyReach.End to, TopologyReach.End? via, Func<string, string> name, string? transport)
    {
        var notes = new List<string>();
        // Only resources of the relationship's transport count as one-sided evidence (an HTTP relationship lists clients, never topics).
        bool Resource(string id) => transport is not null && view.Components.Any(item => item.Id == id && item.Kind == TopologyComponentKind.Resource)
            && (transport == "connector" || id.StartsWith(TopologyBuilder.ResourceKey(transport, ""), StringComparison.Ordinal));
        foreach (var id in from.Components ?? [])
        {
            notes.AddRange(view.Connections.Where(hop => hop.ProducerComponentId == id && Resource(hop.ConsumerComponentId))
                .Select(hop => $"{name(id)} {(hop.Type == TopologyInterfaceType.Messaging ? "produces to" : "feeds")} {name(hop.ConsumerComponentId)}").Distinct().Take(4));
            notes.AddRange(view.Components.Where(item => item.Id == id).SelectMany(item => item.Interfaces)
                .Where(item => item.Direction == TopologyDirection.Calls && item.Status is TopologyStatus.NeedsReview or TopologyStatus.Conflict)
                .Select(item => $"{name(id)} declares client {item.Address}, but its target is not resolved").Distinct().Take(3));
        }
        foreach (var id in to.Components ?? [])
            notes.AddRange(view.Connections.Where(hop => hop.ConsumerComponentId == id && Resource(hop.ProducerComponentId))
                .Select(hop => $"{name(id)} consumes {name(hop.ProducerComponentId)}").Distinct().Take(4));
        if (from.Components is null && via is not null)
            notes.AddRange(view.Connections.Where(hop => via.Matches(hop.ProducerComponentId) && from.Matches(hop.ConsumerComponentId))
                .Select(hop => $"{name(hop.ProducerComponentId)} produces to {name(hop.ConsumerComponentId)}").Distinct().Take(6));
        if (from.Components is { Count: 0 } && to.ResourceKind is not null)
            notes.AddRange(view.Connections.Where(hop => to.Matches(hop.ConsumerComponentId) && view.Components.Any(item => item.Id == hop.ProducerComponentId && item.Kind == TopologyComponentKind.Resource)).Select(hop => $"{name(hop.ProducerComponentId)} → {name(hop.ConsumerComponentId)} is stated").Distinct().Take(3));
        return notes.Count == 0 ? null : string.Join("; ", notes) + ".";
    }

    /// <summary>
    /// Which source role would provide the missing evidence and what resolves the gap, from that role's actual state: the first declared role
    /// whose source is not settled decides (absent → import, existing but unbound → bind, several candidates → review them); when every role is
    /// settled (bound, available or a built-in reference) the evidence is simply not in them, so the fix is configuration (for a relationship a
    /// configured integration can state) or a look at the missing evidence. An existing source is never answered with "import".
    /// </summary>
    private static TopologyRelationshipResult Guidance(TopologyRelationshipResult result, TopologyExpectedRelationship expected, List<TopologySourceRoleCoverage> roles)
    {
        var candidates = expected.EvidenceRoles.Select(id => roles.FirstOrDefault(role => role.RoleId == id)).OfType<TopologySourceRoleCoverage>().ToList();
        var role = candidates.FirstOrDefault(item => !Settled(item.State)) ?? candidates.FirstOrDefault();
        var action = role is null ? TopologyGapAction.None
            : !Settled(role.State) ? role.Action
            : expected.ConfigurationResolvable ? TopologyGapAction.ConfigureIntegration : TopologyGapAction.ViewMissingEvidence;
        return result with
        {
            MissingEvidence = expected.MissingEvidence.Length > 0 ? expected.MissingEvidence : "Evidence stating both ends of this relationship.",
            SourceRoleId = role?.RoleId, SourceRoleName = role?.DisplayName, SourceRoleState = role?.State, MatchingSources = role?.MatchingSources ?? [],
            Action = action, Owner = Owner(action),
        };
    }

    /// <summary>A source whose presence/binding needs no further action (its evidence may still not state a relationship).</summary>
    public static bool Settled(TopologySourceRoleState state) => state is TopologySourceRoleState.Available or TopologySourceRoleState.Bound
        or TopologySourceRoleState.PartlyAvailable or TopologySourceRoleState.ReferenceAvailable;

    /// <summary>The canonical feature that owns each fix (Integration Topology owns none of them).</summary>
    public static string? Owner(TopologyGapAction action) => action switch
    {
        TopologyGapAction.ImportSource => "Project Import",
        TopologyGapAction.BindSource or TopologyGapAction.ReviewCandidates => "Integration Quality Review → Setup & Evidence (canonical source binding)",
        TopologyGapAction.ReanalyzeSource or TopologyGapAction.ViewMissingEvidence or TopologyGapAction.ViewEvidence => "Source Analysis",
        TopologyGapAction.ConfigureIntegration => "Target Environment → Integration Catalog",
        _ => null,
    };

    /// <summary>Adds binding to a recognized role and picks its action. Infrastructure and built-in reference roles need no binding.</summary>
    private static TopologySourceRoleCoverage Bind(TopologySourceRoleCoverage coverage, TopologySourceRoleDefinition role, IReadOnlyList<IqrSourceSnapshot> all, HashSet<Guid>? bound)
    {
        var matching = all.Where(item => coverage.MatchingSources.Contains(item.Archive.FileName, StringComparer.Ordinal)).ToList();
        var analyzed = matching.Count == 0 ? (DateTimeOffset?)null : matching.Max(item => item.AnalyzedAt);
        coverage = coverage with { AnalyzedAt = analyzed };
        var application = role.Match.InfrastructureFormats.Count == 0 && role.Requirement != TopologySourceRequirement.BuiltInReference;
        if (!application || bound is null || coverage.State is not (TopologySourceRoleState.Available or TopologySourceRoleState.PartlyAvailable))
            return coverage with { Action = DefaultAction(coverage.State, role) };
        var boundArchives = matching.Where(item => bound.Contains(item.Id)).Select(item => item.Archive.FileName).Distinct(StringComparer.Ordinal).ToList();
        if (boundArchives.Count > 0)
            return coverage with
            {
                BoundSources = boundArchives,
                State = coverage.State == TopologySourceRoleState.PartlyAvailable ? TopologySourceRoleState.PartlyAvailable : TopologySourceRoleState.Bound,
                Detail = $"{coverage.Detail} Bound in Integration Quality Review: {string.Join(", ", boundArchives)}.",
                Action = TopologyGapAction.ViewEvidence,
            };
        // Distinct content (archive hash), not distinct uploads: a re-import of the same archive is one candidate.
        var distinct = matching.Select(item => item.Archive.Sha256 is { Length: > 0 } sha ? sha : item.Archive.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return distinct > 1 && coverage.MatchingSources.Count > 1
            ? coverage with
            {
                State = TopologySourceRoleState.Ambiguous, Action = TopologyGapAction.ReviewCandidates,
                Detail = $"{coverage.MatchingSources.Count} sources match ({string.Join(", ", coverage.MatchingSources)}); none is bound. Review the candidates in Integration Quality Review — the newest is never chosen automatically.",
            }
            : coverage with
            {
                State = TopologySourceRoleState.AvailableNeedsBinding, Action = TopologyGapAction.BindSource,
                Detail = $"{string.Join(", ", coverage.MatchingSources)} matches by content but is not bound to any integration in Integration Quality Review. Its facts are already used for topology; binding makes it the canonical source."
                    + (coverage.MissingParts.Count > 0 ? $" Not found in it: {string.Join(", ", coverage.MissingParts)}." : ""),
            };
    }

    private static TopologyGapAction DefaultAction(TopologySourceRoleState state, TopologySourceRoleDefinition role) => state switch
    {
        TopologySourceRoleState.NotProvided => TopologyGapAction.ImportSource,
        TopologySourceRoleState.NeedsAnalysis => TopologyGapAction.ReanalyzeSource,
        TopologySourceRoleState.Available or TopologySourceRoleState.PartlyAvailable or TopologySourceRoleState.Bound => TopologyGapAction.ViewEvidence,
        _ => TopologyGapAction.None,
    };

    private static string Why(List<TopologyEvidence> evidence)
    {
        var strongest = evidence.OrderByDescending(item => item.Strength).ThenBy(item => item.Source, StringComparer.Ordinal).ToList();
        var families = strongest.Where(item => item.Strength >= TopologyEvidenceStrength.Strong).Select(item => item.Source).Distinct().ToList();
        var lead = strongest.FirstOrDefault();
        return lead is null ? "No typed evidence."
            : $"{Strength(lead.Strength)}: {lead.Detail}{(families.Count > 1 ? $" Independent sources agree ({string.Join(", ", families)})." : "")}";
    }

    public static string Strength(TopologyEvidenceStrength strength) => strength switch
    {
        TopologyEvidenceStrength.VeryStrong => "Very strong",
        TopologyEvidenceStrength.Strong => "Strong",
        TopologyEvidenceStrength.Supporting => "Supporting",
        _ => "Weak",
    };

    private static List<TopologyCompleteness> Completeness(List<TopologyRelationshipResult> relationships, TopologyRequest request)
    {
        var list = relationships.GroupBy(item => item.Dimension).Select(group =>
        {
            var open = group.Where(item => item.State is not (TopologyRelationshipState.Resolved or TopologyRelationshipState.Observed)).ToList();
            return new TopologyCompleteness(group.Key, open.Count == 0 ? TopologyCompletenessState.Complete : TopologyCompletenessState.Partial,
                open.Count == 0 ? $"{group.Count()} expected relationship{(group.Count() == 1 ? "" : "s")} resolved."
                    : $"{group.Count() - open.Count} of {group.Count()} resolved; open: {string.Join(", ", open.Select(item => item.Label))}.");
        }).ToList();
        var observed = request.Observations?.Count > 0;
        list.Add(new("Runtime observation", observed ? TopologyCompletenessState.Observed : TopologyCompletenessState.NotObserved,
            observed ? $"{request.Observations!.Count} runtime observation(s) merged; observations mark interfaces, never connections." : "No runtime observation was supplied; nothing is called Observed."));
        return list;
    }
}
